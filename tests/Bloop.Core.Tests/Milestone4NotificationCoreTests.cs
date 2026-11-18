using Bloop.Core.Behaviours;
using Bloop.Core.Movement;
using Bloop.Core.Notifications;
using Bloop.Core.Personality;
using Bloop.Storage;
using Xunit;

namespace Bloop.Core.Tests;

public sealed class Milestone4NotificationCoreTests
{
    private static NotificationItem CreateSampleItem(
        uint osId,
        string aumid,
        string appName,
        string title,
        string body,
        DateTimeOffset createdUtc,
        bool explicitUrgency = false)
    {
        return new NotificationItem
        {
            Id = NotificationItem.BuildDeterministicId(aumid, osId, createdUtc),
            OsNotificationId = osId,
            Source = new NotificationSource(aumid, appName),
            Title = title,
            Body = body,
            CreatedAtUtc = createdUtc,
            ReceivedAtUtc = createdUtc,
            HasExplicitUrgencyHint = explicitUrgency
        };
    }

    [Fact]
    public void DatabaseService_AppliesMigrationV2_Idempotently()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m4_mig_{Guid.NewGuid():N}.db");
        try
        {
            using (var db1 = new DatabaseService(dbPath))
            {
                Assert.True(db1.SchemaVersion >= 2);
                var settingsRepo = new SettingsRepository(db1);
                settingsRepo.SaveWindowPosition(250, 350);
            }

            // Re-opening existing DB must keep SchemaVersion>=2 and preserve existing user_settings
            using (var db2 = new DatabaseService(dbPath))
            {
                Assert.True(db2.SchemaVersion >= 2);
                var settingsRepo = new SettingsRepository(db2);
                var pos = settingsRepo.LoadWindowPosition();
                Assert.True(pos.HasSavedPosition);
                Assert.Equal(250, pos.Left);
                Assert.Equal(350, pos.Top);
            }
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void NotificationRepository_SynchronizeSnapshot_DeduplicatesAndPreservesReadAndLocalHistory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m4_sync_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var repo = new NotificationRepository(db);
            var scheduler = new NotificationReactionScheduler();
            var settings = NotificationSettings.Default with { IntegrationEnabled = true };

            var t0 = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
            var n1 = CreateSampleItem(101, "Microsoft.VisualStudioCode", "VS Code", "Build Complete", "0 errors", t0);
            var n2 = CreateSampleItem(102, "Microsoft.Outlook", "Outlook", "Calendar Reminder", "Standup in 5m", t0.AddSeconds(10));

            // 1. Initial startup import
            var sync1 = repo.SynchronizeSnapshot([n1, n1, n2], NotificationSyncTrigger.Startup, isInitialStartupImport: true, settings, item => scheduler.ClassifyImportance(item, settings));
            Assert.True(sync1.IsInitialImport);
            Assert.Equal(2, sync1.AddedItems.Count);
            Assert.Equal(2, sync1.TotalVisibleInInbox);
            Assert.Equal(2, sync1.UnreadCount);

            // 2. Mark n1 as read locally; repeat sync with same snapshot
            repo.MarkRead(n1.Id, isRead: true);
            var sync2 = repo.SynchronizeSnapshot([n1, n2], NotificationSyncTrigger.AdaptiveIdlePoll, isInitialStartupImport: false, settings, item => scheduler.ClassifyImportance(item, settings));
            Assert.Empty(sync2.AddedItems);
            Assert.Equal(2, sync2.TotalVisibleInInbox);
            Assert.Equal(1, sync2.UnreadCount);

            // 3. n1 disappears from Windows Action Center -> IsPresentInOs becomes false, but local history is preserved!
            var sync3 = repo.SynchronizeSnapshot([n2], NotificationSyncTrigger.PlatformEvent, isInitialStartupImport: false, settings, item => scheduler.ClassifyImportance(item, settings));
            Assert.Single(sync3.RemovedFromOsIds);
            Assert.Equal(101u, sync3.RemovedFromOsIds[0]);

            var inbox = repo.GetInboxNotifications();
            Assert.Equal(2, inbox.Count);
            var storedN1 = inbox.Single(x => x.Id == n1.Id);
            Assert.True(storedN1.IsRead);
            Assert.False(storedN1.IsPresentInOs);
            Assert.False(storedN1.IsDismissedLocally);

            // 4. Local dismissal hides n1 from inbox without deleting n2
            repo.DismissLocally(n1.Id);
            var visibleAfterDismiss = repo.GetInboxNotifications();
            Assert.Single(visibleAfterDismiss);
            Assert.Equal(n2.Id, visibleAfterDismiss[0].Id);

            // 5. ClearLocalHistory tombstones n2 (still in OS) so subsequent OS sync does NOT re-import n2 as new
            repo.ClearLocalHistory();
            Assert.Empty(repo.GetInboxNotifications());

            var sync4 = repo.SynchronizeSnapshot([n2], NotificationSyncTrigger.AdaptiveIdlePoll, isInitialStartupImport: false, settings, item => scheduler.ClassifyImportance(item, settings));
            Assert.Empty(sync4.AddedItems);
            Assert.Empty(repo.GetInboxNotifications());
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void NotificationRepository_PersistsSettingsAppRulesAndPanelPlacement()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m4_cfg_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var repo = new NotificationRepository(db);

            var rules = new Dictionary<string, NotificationImportance>(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.Outlook"] = NotificationImportance.High,
                ["Promotions.App"] = NotificationImportance.Muted
            };

            var customSettings = new NotificationSettings
            {
                IntegrationEnabled = true,
                ConsentExplained = true,
                StoreLocalHistoryEnabled = true,
                RetentionDays = 14,
                MaxStoredNotifications = 350,
                ReactionsEnabled = true,
                HighImportanceCenterEntranceEnabled = false,
                BatchWindowSeconds = 4.0,
                ReactionCooldownSeconds = 10.0,
                FastPollIntervalSeconds = 2.0,
                FastPollWindowSeconds = 20.0,
                IdlePollIntervalSeconds = 15.0,
                HighPriorityKeywordsCsv = "incident,pagerduty",
                AppPriorityRules = rules
            };

            repo.SaveNotificationSettings(customSettings);
            var loaded = repo.LoadNotificationSettings();

            Assert.True(loaded.IntegrationEnabled);
            Assert.True(loaded.ConsentExplained);
            Assert.Equal(14, loaded.RetentionDays);
            Assert.Equal(350, loaded.MaxStoredNotifications);
            Assert.False(loaded.HighImportanceCenterEntranceEnabled);
            Assert.Equal(4.0, loaded.BatchWindowSeconds);
            Assert.Equal("incident,pagerduty", loaded.HighPriorityKeywordsCsv);
            Assert.Equal(NotificationImportance.High, loaded.AppPriorityRules["Microsoft.Outlook"]);
            Assert.Equal(NotificationImportance.Muted, loaded.AppPriorityRules["Promotions.App"]);

            var placement = new NotificationPanelPlacement(
                IsDetached: true,
                DetachedLeftDip: 420.5,
                DetachedTopDip: 180.25,
                DetachedWidthDip: 440.0,
                DetachedHeightDip: 560.0,
                MonitorId: @"\\.\DISPLAY2");

            repo.SavePanelPlacement(placement);
            var loadedPlacement = repo.LoadPanelPlacement();
            Assert.True(loadedPlacement.IsDetached);
            Assert.Equal(420.5, loadedPlacement.DetachedLeftDip);
            Assert.Equal(180.25, loadedPlacement.DetachedTopDip);
            Assert.Equal(440.0, loadedPlacement.DetachedWidthDip);
            Assert.Equal(560.0, loadedPlacement.DetachedHeightDip);
            Assert.Equal(@"\\.\DISPLAY2", loadedPlacement.MonitorId);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void NotificationReactionScheduler_Batches3Seconds_ClassifiesDeterministically_AndRespectsPriorities()
    {
        var scheduler = new NotificationReactionScheduler();
        var priorityResolver = new BehaviourPriorityResolver();
        var behaviourSettings = BehaviourSettings.Default;

        var rules = new Dictionary<string, NotificationImportance>(StringComparer.OrdinalIgnoreCase)
        {
            ["PagerDuty"] = NotificationImportance.Critical,
            ["NoiseApp"] = NotificationImportance.Muted
        };

        var settings = NotificationSettings.Default with
        {
            IntegrationEnabled = true,
            BatchWindowSeconds = 3.0,
            ReactionCooldownSeconds = 8.0,
            HighPriorityKeywordsCsv = "production alert",
            AppPriorityRules = rules
        };

        var t0 = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var ordinaryItem = CreateSampleItem(1, "ChatApp", "ChatApp", "Hello", "How are you?", t0);
        var keywordHighItem = CreateSampleItem(2, "MonitorApp", "MonitorApp", "Production Alert", "CPU > 90%", t0.AddSeconds(1));

        scheduler.EnqueueNewNotifications([ordinaryItem, keywordHighItem], t0, settings);

        // Before 3-second batch window expires -> null
        var early = scheduler.EvaluatePendingBatch(
            t0.AddSeconds(1.5),
            settings,
            behaviourSettings,
            priorityResolver,
            MovementState.Docked,
            MascotState.Idle,
            totalUnreadCount: 2);
        Assert.Null(early);

        // Defer while Hopping -> still null and batch retained
        var duringHop = scheduler.EvaluatePendingBatch(
            t0.AddSeconds(3.5),
            settings,
            behaviourSettings,
            priorityResolver,
            MovementState.Hopping,
            MascotState.Idle,
            totalUnreadCount: 2);
        Assert.Null(duringHop);

        // Once Docked after 3.5s -> single batched reaction using highest importance (High -> HighImportanceEntrance)
        var decision = scheduler.EvaluatePendingBatch(
            t0.AddSeconds(4.0),
            settings,
            behaviourSettings,
            priorityResolver,
            MovementState.Docked,
            MascotState.Idle,
            totalUnreadCount: 2);

        Assert.NotNull(decision);
        Assert.True(decision!.ShouldReact);
        Assert.Equal(NotificationReactionKind.HighImportanceEntrance, decision.ReactionKind);
        Assert.Equal(NotificationImportance.High, decision.HighestImportance);
        Assert.Equal(2, decision.BatchCount);

        // Ordinary notification while Sleeping -> suppressed so Bloop stays asleep
        var t1 = t0.AddSeconds(20);
        scheduler.EnqueueNewNotifications([ordinaryItem with { Id = "chat:2", OsNotificationId = 3 }], t1, settings);
        var sleepDecision = scheduler.EvaluatePendingBatch(
            t1.AddSeconds(3.5),
            settings,
            behaviourSettings,
            priorityResolver,
            MovementState.Docked,
            MascotState.Sleepy,
            totalUnreadCount: 3);

        Assert.NotNull(sleepDecision);
        Assert.False(sleepDecision!.ShouldReact);
        Assert.Contains("Sleep", sleepDecision.SuppressionReason);
    }

    [Fact]
    public async Task NotificationSyncCoordinator_HandlesStartupImport_PermissionRevocation_AndPrivacyLogging()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m4_coord_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var repo = new NotificationRepository(db);
            var scheduler = new NotificationReactionScheduler();
            var listener = new FakeNotificationListener();
            var signal = new FakeNotificationChangeSignal();

            var t0 = new DateTimeOffset(2026, 11, 12, 14, 0, 0, TimeSpan.Zero);
            var nStartup = CreateSampleItem(501, "App.Mail", "Mail", "Secret Subject 12345", "Private Body Content 67890", t0);
            listener.Items.Add(nStartup);

            using var coordinator = new NotificationSyncCoordinator(listener, signal, repo, scheduler);
            var logs = new List<string>();
            coordinator.DiagnosticLogged += (_, e) => logs.Add(e.Summary);

            // 1. Enable & request permission -> performs initial startup sync
            var perm = await coordinator.EnableAndRequestPermissionAsync();
            Assert.Equal(NotificationPermissionStatus.Allowed, perm);
            Assert.True(coordinator.HasCompletedInitialImport);
            Assert.Equal(0, scheduler.PendingBatchCount); // Initial startup import must NOT trigger reaction batch!
            Assert.Single(repo.GetInboxNotifications());

            // 2. New notification arrives after startup -> enqueued in reaction batch & boosts adaptive signal
            var nNew = CreateSampleItem(502, "App.Chat", "Chat", "Confidential Ping", "Private Message", t0.AddMinutes(1));
            listener.Items.Add(nNew);
            signal.Fire(NotificationSyncTrigger.AdaptiveFastPoll);
            await Task.Delay(50);

            Assert.Equal(1, scheduler.PendingBatchCount);
            Assert.Equal(2, repo.GetInboxNotifications().Count);
            Assert.True(signal.BoostCount > 0);

            // 3. Privacy check: diagnostic logs must NEVER contain notification titles or bodies
            Assert.NotEmpty(logs);
            foreach (var log in logs)
            {
                Assert.DoesNotContain("Secret Subject", log);
                Assert.DoesNotContain("Private Body", log);
                Assert.DoesNotContain("Confidential Ping", log);
                Assert.DoesNotContain("Private Message", log);
            }

            // 4. Simulate runtime permission revocation -> stops signal, preserves existing local history
            listener.CurrentPermission = NotificationPermissionStatus.Revoked;
            var revokedSync = await coordinator.SynchronizeNowAsync(NotificationSyncTrigger.Manual);
            Assert.NotNull(revokedSync);
            Assert.Contains("Revoked", revokedSync!.ErrorMessage);
            Assert.False(signal.IsStarted);
            Assert.Equal(2, repo.GetInboxNotifications().Count); // Local history preserved!
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private sealed class FakeNotificationListener : INotificationListener
    {
        public bool IsSupported => true;
        public bool IsPlatformChangeEventSupported => false;
        public NotificationPermissionStatus CurrentPermission { get; set; } = NotificationPermissionStatus.Allowed;
        public List<NotificationItem> Items { get; } = [];

        public NotificationPermissionStatus GetPermissionStatus() => CurrentPermission;

        public Task<NotificationPermissionStatus> RequestPermissionAsync(CancellationToken ct = default) =>
            Task.FromResult(CurrentPermission);

        public Task<IReadOnlyList<NotificationItem>> GetCurrentNotificationsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationItem>>(Items.ToList());

        public bool TryRemoveOsNotification(uint osNotificationId) =>
            Items.RemoveAll(x => x.OsNotificationId == osNotificationId) > 0;

        public void Dispose() { }
    }

    private sealed class FakeNotificationChangeSignal : INotificationChangeSignal
    {
        public event EventHandler<NotificationSyncTrigger>? ChangeSignaled;
        public bool IsStarted { get; private set; }
        public int BoostCount { get; private set; }

        public void Start() => IsStarted = true;
        public void Stop() => IsStarted = false;
        public void NotifyActivityBoost(NotificationSyncTrigger reason) => BoostCount++;
        public void ConfigureIntervals(TimeSpan fastInterval, TimeSpan fastWindow, TimeSpan idleInterval, bool active) { }
        public void Fire(NotificationSyncTrigger trigger) => ChangeSignaled?.Invoke(this, trigger);
        public void Dispose() => IsStarted = false;
    }

    [Fact]
    public void PanelPlacementCalculator_SelectsSideWithoutCoveringMascot_AndRecoversOffscreenDetachedPanel()
    {
        var primaryMonitor = new WorkAreaBounds(0, 0, 1920, 1040, @"\\.\DISPLAY1");

        // 1. Mascot docked on Right edge (Left=1700, Top=500, 220x220) -> Attached panel must open to the LEFT of Mascot
        var (leftWhenRightDocked, topWhenRightDocked) = PanelPlacementCalculator.CalculateAttachedPosition(
            mascotLeftDip: 1700,
            mascotTopDip: 500,
            mascotWidthDip: 220,
            mascotHeightDip: 220,
            panelWidthDip: 370,
            panelHeightDip: 490,
            workArea: primaryMonitor);

        Assert.True(leftWhenRightDocked + 370 <= 1700); // Never covers Mascot
        Assert.True(leftWhenRightDocked >= primaryMonitor.Left);
        Assert.True(topWhenRightDocked >= primaryMonitor.Top);
        Assert.True(topWhenRightDocked + 490 <= primaryMonitor.Bottom);

        // 2. Mascot docked on Left edge (Left=0, Top=500, 220x220) -> Attached panel must open to the RIGHT of Mascot
        var (leftWhenLeftDocked, _) = PanelPlacementCalculator.CalculateAttachedPosition(
            mascotLeftDip: 0,
            mascotTopDip: 500,
            mascotWidthDip: 220,
            mascotHeightDip: 220,
            panelWidthDip: 370,
            panelHeightDip: 490,
            workArea: primaryMonitor);

        Assert.True(leftWhenLeftDocked >= 220); // Never covers Mascot

        // 3. Detached panel saved on a disconnected secondary monitor (-2400, -900) recovers inside primary work area
        var offscreen = new NotificationPanelPlacement(
            IsDetached: true,
            DetachedLeftDip: -2400,
            DetachedTopDip: -900,
            DetachedWidthDip: 420,
            DetachedHeightDip: 540,
            MonitorId: @"\\.\DISPLAY3");

        var recovered = PanelPlacementCalculator.RecoverDetachedPlacement(offscreen, primaryMonitor);
        Assert.True(recovered.IsDetached);
        Assert.InRange(recovered.DetachedLeftDip, primaryMonitor.Left, primaryMonitor.Right - recovered.DetachedWidthDip);
        Assert.InRange(recovered.DetachedTopDip, primaryMonitor.Top, primaryMonitor.Bottom - recovered.DetachedHeightDip);
        Assert.Equal(@"\\.\DISPLAY1", recovered.MonitorId);
    }
}
