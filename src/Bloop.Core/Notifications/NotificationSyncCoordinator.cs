using System.Diagnostics;
using Bloop.Core.Events;

namespace Bloop.Core.Notifications;

/// <summary>
/// Orchestrates non-overlapping notification snapshot synchronization between INotificationListener,
/// INotificationChangeSignal, INotificationRepository, and INotificationReactionScheduler.
/// Privacy-preserving: logs sync duration, trigger, and counts without ever logging notification message contents.
/// </summary>
public sealed class NotificationSyncCoordinator : IDisposable
{
    private readonly INotificationListener _listener;
    private readonly INotificationChangeSignal _changeSignal;
    private readonly INotificationRepository _repository;
    private readonly INotificationReactionScheduler _reactionScheduler;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private NotificationSettings _settings;
    private NotificationPermissionStatus _lastPermissionStatus = NotificationPermissionStatus.NotRequested;
    private bool _hasCompletedInitialImport;
    private int _pendingCoalescedTrigger = -1;
    private bool _disposed;

    public event EventHandler<NotificationSyncResult>? Synchronized;
    public event EventHandler<NotificationPermissionStatus>? PermissionStatusChanged;
    public event EventHandler<(string Stage, string Summary)>? DiagnosticLogged;

    public NotificationSettings Settings => _settings;
    public NotificationPermissionStatus PermissionStatus => _lastPermissionStatus;
    public bool HasCompletedInitialImport => _hasCompletedInitialImport;

    public NotificationSyncCoordinator(
        INotificationListener listener,
        INotificationChangeSignal changeSignal,
        INotificationRepository repository,
        INotificationReactionScheduler reactionScheduler)
    {
        _listener = listener;
        _changeSignal = changeSignal;
        _repository = repository;
        _reactionScheduler = reactionScheduler;
        _settings = _repository.LoadNotificationSettings();

        _changeSignal.ChangeSignaled += OnChangeSignaled;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        RefreshSignalConfiguration();
        if (!_settings.IntegrationEnabled)
        {
            LogDiagnostic("NotificationInit", "Notification integration is disabled (opt-in required).");
            return;
        }

        var perm = _listener.GetPermissionStatus();
        UpdatePermissionStatus(perm);
        if (perm == NotificationPermissionStatus.Allowed)
        {
            _changeSignal.Start();
            await SynchronizeNowAsync(NotificationSyncTrigger.Startup, ct);
        }
    }

    public async Task<NotificationPermissionStatus> EnableAndRequestPermissionAsync(CancellationToken ct = default)
    {
        _settings = _settings with { IntegrationEnabled = true, ConsentExplained = true };
        _repository.SaveNotificationSettings(_settings);

        var status = await _listener.RequestPermissionAsync(ct);
        UpdatePermissionStatus(status);
        RefreshSignalConfiguration();

        if (status == NotificationPermissionStatus.Allowed)
        {
            _changeSignal.Start();
            await SynchronizeNowAsync(NotificationSyncTrigger.Startup, ct);
        }
        else
        {
            _changeSignal.Stop();
            LogDiagnostic("NotificationPermission", $"Permission request returned {status}.");
        }

        return status;
    }

    public void UpdateSettings(NotificationSettings settings)
    {
        bool wasEnabled = _settings.IntegrationEnabled;
        _settings = settings;
        _repository.SaveNotificationSettings(settings);
        RefreshSignalConfiguration();

        if (!settings.IntegrationEnabled)
        {
            _changeSignal.Stop();
            LogDiagnostic("NotificationSettings", "Notification integration disabled by user.");
        }
        else if (!wasEnabled && settings.IntegrationEnabled)
        {
            _ = InitializeAsync();
        }
    }

    public void NotifyPanelOpened()
    {
        if (!_settings.IntegrationEnabled) return;
        _changeSignal.NotifyActivityBoost(NotificationSyncTrigger.PanelOpened);
        _ = SynchronizeNowAsync(NotificationSyncTrigger.PanelOpened);
    }

    public void NotifyUserInteraction()
    {
        if (!_settings.IntegrationEnabled) return;
        _changeSignal.NotifyActivityBoost(NotificationSyncTrigger.UserInteraction);
    }

    public async Task<NotificationSyncResult?> SynchronizeNowAsync(
        NotificationSyncTrigger trigger = NotificationSyncTrigger.Manual,
        CancellationToken ct = default)
    {
        if (_disposed) return null;

        if (!_settings.IntegrationEnabled)
        {
            _changeSignal.Stop();
            return null;
        }

        // Coalesce overlapping synchronization triggers rather than running concurrent GetNotificationsAsync calls
        if (!_syncLock.Wait(0))
        {
            Interlocked.Exchange(ref _pendingCoalescedTrigger, (int)trigger);
            return null;
        }

        try
        {
            NotificationSyncResult? lastResult = await ExecuteSingleSyncPassAsync(trigger, ct);

            while (!_disposed && !ct.IsCancellationRequested)
            {
                int nextTriggerInt = Interlocked.Exchange(ref _pendingCoalescedTrigger, -1);
                if (nextTriggerInt < 0)
                {
                    break;
                }
                lastResult = await ExecuteSingleSyncPassAsync((NotificationSyncTrigger)nextTriggerInt, ct);
            }

            return lastResult;
        }
        finally
        {
            if (!_disposed)
            {
                _syncLock.Release();
            }
        }
    }

    private async Task<NotificationSyncResult> ExecuteSingleSyncPassAsync(
        NotificationSyncTrigger trigger,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var perm = _listener.GetPermissionStatus();
        UpdatePermissionStatus(perm);

        if (perm != NotificationPermissionStatus.Allowed)
        {
            _changeSignal.Stop();
            sw.Stop();
            var blockedResult = new NotificationSyncResult(
                trigger,
                [],
                [],
                0,
                _repository.GetInboxNotifications().Count,
                _repository.GetUnreadCount(),
                sw.ElapsedMilliseconds,
                DateTimeOffset.UtcNow,
                !_hasCompletedInitialImport,
                $"PermissionNotAllowed:{perm}");

            LogDiagnostic("NotificationSync", $"Sync skipped (Trigger={trigger}, Permission={perm})");
            Synchronized?.Invoke(this, blockedResult);
            return blockedResult;
        }

        try
        {
            var osSnapshot = await _listener.GetCurrentNotificationsAsync(ct);
            bool isInitial = !_hasCompletedInitialImport;

            var syncResult = _repository.SynchronizeSnapshot(
                osSnapshot,
                trigger,
                isInitial,
                _settings,
                item => _reactionScheduler.ClassifyImportance(item, _settings),
                sw.ElapsedMilliseconds);

            _hasCompletedInitialImport = true;
            sw.Stop();
            syncResult = syncResult with { DurationMs = sw.ElapsedMilliseconds };

            // Boost adaptive polling when changes are detected
            if (syncResult.AddedItems.Count > 0 || syncResult.RemovedFromOsIds.Count > 0)
            {
                _changeSignal.NotifyActivityBoost(trigger);
            }

            // Enqueue newly arrived notifications for mascot reaction ONLY after the initial startup import
            if (!isInitial && syncResult.AddedItems.Count > 0)
            {
                _reactionScheduler.EnqueueNewNotifications(syncResult.AddedItems, DateTimeOffset.UtcNow, _settings);
            }

            // Privacy-safe telemetry: record counts, trigger, and latency without message content
            if (syncResult.AddedItems.Count > 0 || syncResult.RemovedFromOsIds.Count > 0 || isInitial || trigger == NotificationSyncTrigger.Manual)
            {
                LogDiagnostic(
                    "NotificationSync",
                    $"Trigger={trigger}, Initial={isInitial}, Added={syncResult.AddedItems.Count}, RemovedOs={syncResult.RemovedFromOsIds.Count}, ActiveOs={syncResult.TotalActiveInOs}, Unread={syncResult.UnreadCount}, Duration={syncResult.DurationMs}ms");
            }

            Synchronized?.Invoke(this, syncResult);
            return syncResult;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            var failResult = new NotificationSyncResult(
                trigger,
                [],
                [],
                0,
                _repository.GetInboxNotifications().Count,
                _repository.GetUnreadCount(),
                sw.ElapsedMilliseconds,
                DateTimeOffset.UtcNow,
                !_hasCompletedInitialImport,
                ex.GetType().Name);

            LogDiagnostic("NotificationSyncError", $"Trigger={trigger}, Error={ex.GetType().Name}, Duration={sw.ElapsedMilliseconds}ms");
            Synchronized?.Invoke(this, failResult);
            return failResult;
        }
    }

    public bool DismissFromWindowsAndMarkLocally(string notificationId, uint osNotificationId, bool alsoDismissLocally = false)
    {
        bool removed = _listener.TryRemoveOsNotification(osNotificationId);
        if (removed)
        {
            _repository.MarkRemovedFromOs(osNotificationId);
        }
        if (alsoDismissLocally)
        {
            _repository.DismissLocally(notificationId);
        }
        _changeSignal.NotifyActivityBoost(NotificationSyncTrigger.UserInteraction);
        return removed;
    }

    private void OnChangeSignaled(object? sender, NotificationSyncTrigger trigger)
    {
        if (_disposed) return;
        _ = SynchronizeNowAsync(trigger);
    }

    private void RefreshSignalConfiguration()
    {
        bool active = _settings.IntegrationEnabled && _listener.GetPermissionStatus() == NotificationPermissionStatus.Allowed;
        _changeSignal.ConfigureIntervals(
            TimeSpan.FromSeconds(Math.Clamp(_settings.FastPollIntervalSeconds, 1.0, 30.0)),
            TimeSpan.FromSeconds(Math.Clamp(_settings.FastPollWindowSeconds, 5.0, 120.0)),
            TimeSpan.FromSeconds(Math.Clamp(_settings.IdlePollIntervalSeconds, 3.0, 120.0)),
            active);
    }

    private void UpdatePermissionStatus(NotificationPermissionStatus next)
    {
        if (_lastPermissionStatus == next) return;
        _lastPermissionStatus = next;
        PermissionStatusChanged?.Invoke(this, next);
    }

    private void LogDiagnostic(string stage, string summary)
    {
        DiagnosticLogged?.Invoke(this, (stage, summary));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _changeSignal.ChangeSignaled -= OnChangeSignaled;
        _changeSignal.Dispose();
        _syncLock.Dispose();
    }
}
