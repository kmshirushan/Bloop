using System.Windows.Threading;
using Bloop.Core.Awareness;
using Bloop.Core.Behaviours;
using Bloop.Core.Interfaces;
using Bloop.Desktop.Services;

namespace Bloop.Desktop.Awareness;

/// <summary>
/// Coordinates the independent 500ms desktop awareness loop, session/power event transitions,
/// policy propagation to MovementController and BehaviourScheduler, and rate-limited diagnostics.
/// </summary>
public sealed class AwarenessCoordinator : IDisposable
{
    private readonly IAwarenessProvider _provider;
    private readonly IContextPolicyResolver _policyResolver;
    private readonly IMovementController _movement;
    private readonly BehaviourScheduler _behaviourScheduler;
    private readonly ISettingsRepository _settingsRepository;
    private readonly DiagnosticsLogService _diagnostics;
    private readonly DispatcherTimer _awarenessTimer;

    private Func<IntPtr[]>? _ownedWindowHandlesProvider;
    private TimeSpan _lastSampledIdleDuration = TimeSpan.Zero;
    private int _tickCount;
    private bool _isDisposed;

    public AwarenessSettings Settings { get; private set; }
    public AwarenessSnapshot CurrentSnapshot { get; private set; } = AwarenessSnapshot.Default;
    public AwarenessPolicy CurrentPolicy { get; private set; } = AwarenessPolicy.Default;

    public event EventHandler<(bool ShouldShowMascot, AwarenessPolicy Policy)>? MascotVisibilityPolicyChanged;
    public event EventHandler<(AwarenessSnapshot Snapshot, AwarenessPolicy Policy)>? StateChanged;

    public AwarenessCoordinator(
        IAwarenessProvider provider,
        IContextPolicyResolver policyResolver,
        IMovementController movement,
        BehaviourScheduler behaviourScheduler,
        ISettingsRepository settingsRepository,
        DiagnosticsLogService diagnostics)
    {
        _provider = provider;
        _policyResolver = policyResolver;
        _movement = movement;
        _behaviourScheduler = behaviourScheduler;
        _settingsRepository = settingsRepository;
        _diagnostics = diagnostics;

        Settings = _settingsRepository.LoadAwarenessSettings();

        _provider.SessionLockChanged += OnSessionLockChanged;
        _provider.PowerSuspendChanged += OnPowerSuspendChanged;

        // Independent 500ms awareness timer (separate from the 30 Hz companion animation timer)
        _awarenessTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _awarenessTimer.Tick += OnAwarenessTimerTick;
    }

    public void Start(Func<IntPtr[]> ownedWindowHandlesProvider)
    {
        _ownedWindowHandlesProvider = ownedWindowHandlesProvider;
        _diagnostics.RecordCustom(
            "AwarenessInitialized",
            $"Desktop Awareness Engine started (Enabled={Settings.IsEnabled}, FullScreenHide={Settings.AutoHideOnFullScreen}, FocusInference={Settings.AutoFocusInferenceEnabled})");

        EvaluateNow(DateTimeOffset.UtcNow);
        _awarenessTimer.Start();
    }

    public void Stop()
    {
        _awarenessTimer.Stop();
    }

    public void UpdateSettings(AwarenessSettings settings, bool persist = true)
    {
        Settings = AwarenessValidator.Normalize(settings);
        if (persist)
        {
            _settingsRepository.SaveAwarenessSettings(Settings);
        }
        EvaluateNow(DateTimeOffset.UtcNow);
    }

    public void SetManualFocusMode(bool enabled, TimeSpan? duration = null)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = Settings with
        {
            ManualFocusModeEnabled = enabled,
            ManualFocusExpiresAtUtc = enabled && duration.HasValue ? now + duration.Value : null
        };
        UpdateSettings(updated, persist: true);
    }

    public void SetManualIdleOverride(ManualIdleOverride idleOverride)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = Settings with { ManualIdleOverride = idleOverride };
        if (idleOverride == ManualIdleOverride.ForceSleep)
        {
            _behaviourScheduler.SetManualSleepState(true, now);
        }
        else if (idleOverride == ManualIdleOverride.ForceAwake)
        {
            _behaviourScheduler.SetManualSleepState(false, now);
        }
        UpdateSettings(updated, persist: true);
    }

    private void OnAwarenessTimerTick(object? sender, EventArgs e)
    {
        if (_isDisposed) return;

        // Pause polling ticks while system is suspended
        if (_provider.IsSystemSuspended && Settings.PowerAwarenessEnabled)
        {
            return;
        }

        _tickCount++;
        EvaluateNow(DateTimeOffset.UtcNow, sampleIdleNow: (_tickCount % 2 == 0));
    }

    public void EvaluateNow(DateTimeOffset nowUtc, bool sampleIdleNow = true)
    {
        if (_isDisposed) return;

        var ownedHandles = _ownedWindowHandlesProvider?.Invoke() ?? [];
        var obs = _provider.ObserveForegroundWindow(ownedHandles);

        if (sampleIdleNow)
        {
            _lastSampledIdleDuration = _provider.GetUserIdleDuration();
        }

        var (mouseMoved, _, _, mouseIdle) = _provider.SampleMouseActivity(nowUtc);

        ApplyObservationInternal(
            obs,
            _lastSampledIdleDuration,
            _provider.IsSessionLocked,
            _provider.IsSystemSuspended,
            nowUtc,
            mouseIdle,
            mouseMoved);
    }

    public bool NotifyPointerEnteredMascot(DateTimeOffset nowUtc)
    {
        if (_isDisposed) return false;

        _diagnostics.RecordCustom("PointerEnteredMascot", "Pointer entered Bloop's interactive hit area");
        bool woke = _policyResolver.TryWakeFromPointerEntry(nowUtc, out var wakeEvent);
        if (wakeEvent is not null)
        {
            _diagnostics.RecordCustom(wakeEvent.EventName, wakeEvent.Summary);
            EvaluateNow(nowUtc, sampleIdleNow: false);
        }
        return woke;
    }

    public bool TryRequestNotificationWake(
        Bloop.Core.Notifications.NotificationImportance highestImportance,
        int batchCount,
        string? sourceApp)
    {
        if (_isDisposed) return false;

        string summary = $"Source={sourceApp ?? "App"}, Importance={highestImportance}, Batch={batchCount}";
        _diagnostics.RecordCustom("NotificationWakeRequested", $"Notification wake requested ({summary})");

        if (highestImportance < Bloop.Core.Notifications.NotificationImportance.High)
        {
            _diagnostics.RecordCustom("NotificationWakeRejected", $"Ordinary notification does not wake Bloop ({summary})");
            return false;
        }

        bool accepted = _policyResolver.TryWakeFromPriorityNotification(
            summary,
            Settings,
            _behaviourScheduler.Settings.GlobalBehavioursPaused,
            out var wakeEvent);

        if (wakeEvent is not null)
        {
            _diagnostics.RecordCustom(wakeEvent.EventName, wakeEvent.Summary);
        }

        if (accepted)
        {
            EvaluateNow(DateTimeOffset.UtcNow, sampleIdleNow: false);
        }

        return accepted;
    }

    /// <summary>
    /// Allows deterministic end-to-end runtime verification of awareness scenarios
    /// (FullScreen, Maximized, Focus, Inactivity, Intelligent Sleep, SessionLock, SystemSuspend).
    /// </summary>
    public void EvaluateSyntheticObservation(
        ForegroundWindowObservation observation,
        TimeSpan idleDuration,
        bool isSessionLocked,
        bool isSystemSuspended,
        DateTimeOffset nowUtc,
        TimeSpan? mouseIdleDuration = null,
        bool mouseMovedOnDesktop = false)
    {
        _lastSampledIdleDuration = idleDuration;
        ApplyObservationInternal(
            observation,
            idleDuration,
            isSessionLocked,
            isSystemSuspended,
            nowUtc,
            mouseIdleDuration,
            mouseMovedOnDesktop);
    }

    private void ApplyObservationInternal(
        ForegroundWindowObservation observation,
        TimeSpan idleDuration,
        bool isSessionLocked,
        bool isSystemSuspended,
        DateTimeOffset nowUtc,
        TimeSpan? mouseIdleDuration = null,
        bool mouseMovedOnDesktop = false)
    {
        var prevSnapshot = CurrentSnapshot;
        var prevPolicy = CurrentPolicy;

        var (snapshot, policy, events) = _policyResolver.Evaluate(
            observation,
            idleDuration,
            isSessionLocked,
            isSystemSuspended,
            Settings,
            _behaviourScheduler.Settings.GlobalBehavioursPaused,
            nowUtc,
            mouseIdleDuration,
            mouseMovedOnDesktop);

        CurrentSnapshot = snapshot;
        CurrentPolicy = policy;

        foreach (var ev in events)
        {
            _diagnostics.RecordCustom(ev.EventName, ev.Summary);
        }

        bool wasHidden = !prevPolicy.ShouldShowMascot;
        bool isNowVisible = policy.ShouldShowMascot;
        bool exitedFocus = prevPolicy.EffectiveContext == DesktopContext.Focus &&
                           policy.EffectiveContext == DesktopContext.Normal;

        TimeSpan? settling = (wasHidden && isNowVisible) || exitedFocus
            ? TimeSpan.FromSeconds(Settings.PostRestoreSettlingSeconds)
            : null;

        _movement.ApplyAwarenessPolicy(policy, nowUtc, settling);
        _behaviourScheduler.ApplyAwarenessContext(snapshot, policy, nowUtc);

        if (prevPolicy.ShouldShowMascot != policy.ShouldShowMascot)
        {
            MascotVisibilityPolicyChanged?.Invoke(this, (policy.ShouldShowMascot, policy));
        }

        bool stateChanged = prevSnapshot.Context != snapshot.Context ||
                            prevSnapshot.InactivityState != snapshot.InactivityState ||
                            prevPolicy.ShouldShowMascot != policy.ShouldShowMascot ||
                            prevPolicy.IsQuietMode != policy.IsQuietMode ||
                            !string.Equals(prevSnapshot.ForegroundProcessName, snapshot.ForegroundProcessName, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(prevPolicy.Reason, policy.Reason, StringComparison.Ordinal);

        if (stateChanged)
        {
            StateChanged?.Invoke(this, (snapshot, policy));
        }
    }

    private void OnSessionLockChanged(object? sender, bool isLocked)
    {
        Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            EvaluateNow(DateTimeOffset.UtcNow, sampleIdleNow: true);
        });
    }

    private void OnPowerSuspendChanged(object? sender, bool isSuspended)
    {
        Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            if (!isSuspended)
            {
                // System resumed: clear any stale pre-sleep full-screen candidate state before re-evaluating
                _policyResolver.ResetState(now);
            }
            EvaluateNow(now, sampleIdleNow: true);
        });
    }

    public string FormatDiagnosticsSummary()
    {
        var s = CurrentSnapshot;
        var p = CurrentPolicy;
        string fgApp = string.IsNullOrWhiteSpace(s.ForegroundProcessName) ? "Desktop / None" : $"{s.ForegroundProcessName}.exe";
        string vis = p.ShouldShowMascot ? "Visible" : "Hidden";
        string move = p.AllowAutonomousMovement ? "Permitted" : "Suppressed";

        return $"""
            Bloop Awareness
            Context: {s.Context} ({s.InactivityState}, Conf={s.ContextConfidence:0.00})
            Foreground App: {fgApp}
            Mascot Visibility: {vis}
            Movement: {move}
            Notification Reception: Active
            Reason: {p.Reason}
            """;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _awarenessTimer.Stop();
        _awarenessTimer.Tick -= OnAwarenessTimerTick;
        _provider.SessionLockChanged -= OnSessionLockChanged;
        _provider.PowerSuspendChanged -= OnPowerSuspendChanged;
    }
}
