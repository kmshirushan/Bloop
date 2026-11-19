using Bloop.Core.Awareness;
using Bloop.Core.Behaviours;
using Bloop.Core.Movement;
using Bloop.Core.Notifications;
using Bloop.Core.Personality;
using Bloop.Storage;
using Xunit;

namespace Bloop.Core.Tests;

public sealed class Milestone6AwarenessTests
{
    private static ForegroundWindowObservation MakeObservation(
        string processName = "chrome",
        bool isValid = true,
        bool isBloop = false,
        bool isShell = false,
        bool isVisible = true,
        bool isMinimized = false,
        bool isMaximized = false,
        bool hasCaption = true,
        int winLeft = 100,
        int winTop = 100,
        int winRight = 1400,
        int winBottom = 900,
        int monLeft = 0,
        int monTop = 0,
        int monRight = 1920,
        int monBottom = 1080,
        int workLeft = 0,
        int workTop = 0,
        int workRight = 1920,
        int workBottom = 1040,
        string monitorId = "\\\\.\\DISPLAY1")
    {
        return new ForegroundWindowObservation(
            WindowHandle: new IntPtr(0x10020),
            ProcessId: 4242,
            ProcessName: processName,
            ApplicationName: processName,
            WindowClassName: "Chrome_WidgetWin_1",
            IsValidExternalWindow: isValid,
            IsBloopOwnedWindow: isBloop,
            IsShellOrDesktopSurface: isShell,
            IsVisible: isVisible,
            IsMinimized: isMinimized,
            IsMaximized: isMaximized,
            HasCaptionOrThickFrame: hasCaption,
            WindowLeftPx: winLeft,
            WindowTopPx: winTop,
            WindowRightPx: winRight,
            WindowBottomPx: winBottom,
            MonitorLeftPx: monLeft,
            MonitorTopPx: monTop,
            MonitorRightPx: monRight,
            MonitorBottomPx: monBottom,
            WorkAreaLeftPx: workLeft,
            WorkAreaTopPx: workTop,
            WorkAreaRightPx: workRight,
            WorkAreaBottomPx: workBottom,
            MonitorId: monitorId);
    }

    [Fact]
    public void IsFullScreenWindowCandidate_DistinguishesMaximizedFromGenuineFullScreen_AndSupportsNegativeCoordinates()
    {
        var settings = AwarenessSettings.Default;

        // 1. Maximized browser on a monitor with visible taskbar (covers WorkArea 0..1040, not Monitor 0..1080) -> NOT FullScreen
        var maximizedVisibleTaskbar = MakeObservation(
            processName: "msedge",
            isMaximized: true,
            hasCaption: true,
            winLeft: 0, winTop: 0, winRight: 1920, winBottom: 1040);
        Assert.False(ContextPolicyResolver.IsFullScreenWindowCandidate(maximizedVisibleTaskbar, settings, out var reason1));
        Assert.Equal("DoesNotCoverMonitorBounds", reason1);

        // 2. Maximized IDE on an auto-hide taskbar monitor (WorkArea == Monitor 0..1080, IsMaximized=true, HasCaption=true) -> NOT FullScreen
        var maximizedAutoHideTaskbar = MakeObservation(
            processName: "devenv",
            isMaximized: true,
            hasCaption: true,
            winLeft: 0, winTop: 0, winRight: 1920, winBottom: 1080,
            workLeft: 0, workTop: 0, workRight: 1920, workBottom: 1080);
        Assert.False(ContextPolicyResolver.IsFullScreenWindowCandidate(maximizedAutoHideTaskbar, settings, out var reason2));
        Assert.Equal("MaximizedStandardWindowWithCaption", reason2);

        // 3. Genuine Full-Screen Video / Borderless Game covering secondary monitor with NEGATIVE coordinates (-1920..0) -> IS FullScreen
        var negativeMonitorFullScreen = MakeObservation(
            processName: "vlc",
            isMaximized: false,
            hasCaption: false,
            winLeft: -1920, winTop: 0, winRight: 0, winBottom: 1080,
            monLeft: -1920, monTop: 0, monRight: 0, monBottom: 1080,
            workLeft: -1920, workTop: 0, workRight: 0, workBottom: 1040,
            monitorId: "\\\\.\\DISPLAY2");
        Assert.True(ContextPolicyResolver.IsFullScreenWindowCandidate(negativeMonitorFullScreen, settings, out var reason3));
        Assert.Null(reason3);

        // 4. User exclusion rule overrides full-screen suppression
        var excludedSettings = settings with { FullScreenExcludedProcessesCsv = "vlc.exe, mpv" };
        Assert.False(ContextPolicyResolver.IsFullScreenWindowCandidate(negativeMonitorFullScreen, excludedSettings, out var reason4));
        Assert.Contains("ExcludedByFullScreenCsv", reason4);
    }

    [Fact]
    public void ContextPolicyResolver_AppliesFullScreenHysteresis_BeforeConfirmingAndClearing()
    {
        var resolver = new ContextPolicyResolver();
        var settings = AwarenessSettings.Default with
        {
            FullScreenEnterDelayMs = 400,
            FullScreenRestoreDelayMs = 700
        };

        var t0 = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var fullScreenObs = MakeObservation(
            processName: "eldenring",
            isMaximized: false,
            hasCaption: false,
            winLeft: 0, winTop: 0, winRight: 1920, winBottom: 1080);
        var normalObs = MakeObservation(processName: "explorer");

        // At t=0ms: Candidate detected, but not yet confirmed (mascot still visible)
        var (snap0, pol0, _) = resolver.Evaluate(fullScreenObs, TimeSpan.Zero, false, false, settings, false, t0);
        Assert.True(snap0.IsFullScreenCandidate);
        Assert.False(snap0.IsFullScreen);
        Assert.True(pol0.ShouldShowMascot);

        // At t=250ms (< 400ms enter delay): Still unconfirmed
        var (snap250, pol250, _) = resolver.Evaluate(fullScreenObs, TimeSpan.Zero, false, false, settings, false, t0.AddMilliseconds(250));
        Assert.False(snap250.IsFullScreen);
        Assert.True(pol250.ShouldShowMascot);

        // At t=450ms (>= 400ms enter delay): Confirmed FullScreen -> Mascot hidden & movement suppressed
        var (snap450, pol450, events450) = resolver.Evaluate(fullScreenObs, TimeSpan.Zero, false, false, settings, false, t0.AddMilliseconds(450));
        Assert.True(snap450.IsFullScreen);
        Assert.Equal(DesktopContext.FullScreen, snap450.Context);
        Assert.False(pol450.ShouldShowMascot);
        Assert.False(pol450.AllowAutonomousMovement);
        Assert.False(pol450.AllowNotificationReaction);
        Assert.Contains(events450, e => e.EventName == "FullScreenConfirmed");

        // At t=600ms: Normal window becomes active, but restore delay (700ms) has just started
        var (snap600, pol600, _) = resolver.Evaluate(normalObs, TimeSpan.Zero, false, false, settings, false, t0.AddMilliseconds(600));
        Assert.True(snap600.IsFullScreen);
        Assert.False(pol600.ShouldShowMascot);

        // At t=1350ms (750ms after clearing >= 700ms restore delay): Restored to Normal -> Mascot visible
        var (snap1350, pol1350, events1350) = resolver.Evaluate(normalObs, TimeSpan.Zero, false, false, settings, false, t0.AddMilliseconds(1350));
        Assert.False(snap1350.IsFullScreen);
        Assert.Equal(DesktopContext.Normal, snap1350.Context);
        Assert.True(pol1350.ShouldShowMascot);
        Assert.Contains(events1350, e => e.EventName == "FullScreenCleared");
    }

    [Fact]
    public void ContextPolicyResolver_EnforcesMandatory8LevelPrecedence_AndOwnWindowExclusion()
    {
        var resolver = new ContextPolicyResolver();
        var settings = AwarenessSettings.Default;
        var t0 = DateTimeOffset.UtcNow;

        // 1. Observe external IDE (Code)
        var codeObs = MakeObservation(processName: "Code", isMaximized: true, winLeft: 0, winTop: 0, winRight: 1920, winBottom: 1040);
        var (snap1, _, _) = resolver.Evaluate(codeObs, TimeSpan.FromSeconds(5), false, false, settings, false, t0);
        Assert.Equal("Code", snap1.ForegroundProcessName);

        // 2. User clicks Bloop's own NotificationWindow/SettingsWindow -> ForegroundProcessName stays "Code", never "Bloop.Desktop"
        var bloopOwnObs = MakeObservation(processName: "Bloop.Desktop", isValid: false, isBloop: true);
        var (snap2, _, _) = resolver.Evaluate(bloopOwnObs, TimeSpan.FromSeconds(6), false, false, settings, false, t0.AddSeconds(1));
        Assert.Equal("Code", snap2.ForegroundProcessName);

        // 3. Automatic Focus Inference requires multi-signal confidence (active user + stable duration >= 30s + maximized IDE)
        var (snapFocus, polFocus, _) = resolver.Evaluate(codeObs, TimeSpan.FromSeconds(8), false, false, settings, false, t0.AddSeconds(35));
        Assert.Equal(DesktopContext.Focus, snapFocus.Context);
        Assert.True(polFocus.ShouldShowMascot);
        Assert.True(polFocus.IsQuietMode);
        Assert.False(polFocus.AllowAutonomousMovement);
        Assert.False(polFocus.AllowAttentionSeeking);
        Assert.False(polFocus.AllowCursorChasing);
        Assert.True(polFocus.AllowNotificationReaction);

        // 4. Global Pause overrides Focus/Normal autonomous actions while preserving visibility
        var (_, polGlobalPause, _) = resolver.Evaluate(codeObs, TimeSpan.FromSeconds(8), false, false, settings, globalBehavioursPaused: true, t0.AddSeconds(36));
        Assert.True(polGlobalPause.ShouldShowMascot);
        Assert.False(polGlobalPause.AllowAutonomousMovement);
        Assert.False(polGlobalPause.AllowNotificationReaction);
        Assert.Equal("GlobalPause", polGlobalPause.Reason);

        // 5. Session Lock overrides Global Pause & Focus -> hides Mascot immediately
        var (snapLock, polLock, _) = resolver.Evaluate(codeObs, TimeSpan.FromSeconds(8), isSessionLocked: true, isSystemSuspended: false, settings, globalBehavioursPaused: true, t0.AddSeconds(37));
        Assert.Equal(DesktopContext.SessionLocked, snapLock.Context);
        Assert.False(polLock.ShouldShowMascot);
        Assert.Equal("SessionLocked", polLock.Reason);

        // 6. System Suspend has highest precedence (Level 1)
        var (snapSuspend, polSuspend, _) = resolver.Evaluate(codeObs, TimeSpan.FromSeconds(8), isSessionLocked: true, isSystemSuspended: true, settings, globalBehavioursPaused: true, t0.AddSeconds(38));
        Assert.Equal(DesktopContext.SystemSuspended, snapSuspend.Context);
        Assert.False(polSuspend.ShouldShowMascot);
        Assert.Equal("SystemSuspended", polSuspend.Reason);
    }

    [Fact]
    public void ContextPolicyResolver_ClassifiesInactivityTiers_AndRespectsManualOverridesAndThresholdValidation()
    {
        // Invalid threshold ordering is automatically normalized (Quiet < Away < Sleep)
        var dirtySettings = new AwarenessSettings
        {
            QuietThresholdSeconds = 400,
            AwayThresholdSeconds = 100,
            SleepThresholdSeconds = 200
        };
        var normalized = AwarenessValidator.Normalize(dirtySettings);
        Assert.True(normalized.QuietThresholdSeconds < normalized.AwayThresholdSeconds);
        Assert.True(normalized.AwayThresholdSeconds < normalized.SleepThresholdSeconds);

        var resolver = new ContextPolicyResolver();
        var settings = AwarenessSettings.Default; // Quiet=60s, Away=300s, Sleep=900s
        var obs = MakeObservation(processName: "notepad");
        var t0 = DateTimeOffset.UtcNow;

        // Active (15s)
        var (sActive, pActive, _) = resolver.Evaluate(obs, TimeSpan.FromSeconds(15), false, false, settings, false, t0);
        Assert.Equal(UserInactivityState.Active, sActive.InactivityState);
        Assert.True(pActive.AllowAutonomousMovement);

        // Quiet (90s)
        var (sQuiet, pQuiet, _) = resolver.Evaluate(obs, TimeSpan.FromSeconds(90), false, false, settings, false, t0.AddSeconds(1));
        Assert.Equal(UserInactivityState.Quiet, sQuiet.InactivityState);
        Assert.True(pQuiet.IsQuietMode);
        Assert.False(pQuiet.AllowAttentionSeeking);

        // Away (400s)
        var (sAway, pAway, _) = resolver.Evaluate(obs, TimeSpan.FromSeconds(400), false, false, settings, false, t0.AddSeconds(2));
        Assert.Equal(UserInactivityState.Away, sAway.InactivityState);
        Assert.Equal(DesktopContext.Away, sAway.Context);
        Assert.False(pAway.AllowAutonomousMovement);

        // Sleeping (1000s)
        var (sSleep, pSleep, _) = resolver.Evaluate(obs, TimeSpan.FromSeconds(1000), false, false, settings, false, t0.AddSeconds(3));
        Assert.Equal(UserInactivityState.Sleeping, sSleep.InactivityState);
        Assert.True(pSleep.ShouldEnterSleepMode);

        // Manual ForceAwake overrides Sleeping
        var forceAwakeSettings = settings with { ManualIdleOverride = ManualIdleOverride.ForceAwake };
        var (sWake, pWake, _) = resolver.Evaluate(obs, TimeSpan.FromSeconds(1000), false, false, forceAwakeSettings, false, t0.AddSeconds(4));
        Assert.Equal(UserInactivityState.Active, sWake.InactivityState);
        Assert.False(pWake.ShouldEnterSleepMode);
    }

    [Fact]
    public void MovementController_BehaviourScheduler_AndNotificationScheduler_RespectAwarenessPolicyAndSettling()
    {
        var workArea = new WorkAreaBounds(0, 0, 1920, 1040, "primary");
        var movement = new MovementController(new Random(42));
        var personality = new PersonalityEngine();
        var scheduler = new BehaviourScheduler(movement, personality, new Random(42));
        var notifScheduler = new NotificationReactionScheduler();
        var nowUtc = DateTimeOffset.UtcNow;

        // 1. FullScreen policy hides mascot, cancels any active hop, and defers notification reactions without dropping them
        var fullScreenPolicy = new AwarenessPolicy
        {
            ShouldShowMascot = false,
            AllowAutonomousMovement = false,
            AllowNotificationReaction = false,
            EffectiveContext = DesktopContext.FullScreen,
            Reason = "FullScreen(vlc)"
        };
        movement.ApplyAwarenessPolicy(fullScreenPolicy, nowUtc);
        Assert.False(movement.CanRunAutonomousMovement(nowUtc.AddHours(1), out var moveRej));
        Assert.Contains("AwarenessHidden", moveRej);

        var n1 = new NotificationItem
        {
            Id = "m6-n1",
            OsNotificationId = 101,
            Source = new NotificationSource("Bloop.Test", "TestApp"),
            Title = "Urgent Alert",
            Body = "Test",
            CreatedAtUtc = nowUtc,
            ReceivedAtUtc = nowUtc,
            HasExplicitUrgencyHint = true
        };
        notifScheduler.EnqueueNewNotifications([n1], nowUtc, NotificationSettings.Default);
        var hiddenDec = notifScheduler.EvaluatePendingBatch(
            nowUtc.AddSeconds(5),
            NotificationSettings.Default,
            BehaviourSettings.Default,
            scheduler.PriorityResolver,
            movement.CurrentState,
            personality.CurrentState,
            totalUnreadCount: 1,
            forceFlush: true,
            awarenessPolicy: fullScreenPolicy);

        Assert.NotNull(hiddenDec);
        Assert.False(hiddenDec!.ShouldReact);
        Assert.Equal(1, notifScheduler.DeferredWhileHiddenCount);

        // 2. Restore after FullScreen: applies post-restore settling cooldown and plays a single SubtleEdgeGlance catch-up reaction
        var normalPolicy = AwarenessPolicy.Default;
        movement.ApplyAwarenessPolicy(normalPolicy, nowUtc.AddSeconds(10), postRestoreSettling: TimeSpan.FromSeconds(4));
        Assert.False(movement.CanRunAutonomousMovement(nowUtc.AddSeconds(12), out var settleRej));
        Assert.Contains("PostRestoreSettling", settleRej);

        var catchUp = notifScheduler.TryConsumeCatchUpReaction(totalUnreadCount: 1, catchUpEnabled: true, nowUtc.AddSeconds(11));
        Assert.NotNull(catchUp);
        Assert.True(catchUp!.ShouldReact);
        Assert.Equal(NotificationReactionKind.SubtleEdgeGlance, catchUp.ReactionKind);
        Assert.Equal(0, notifScheduler.DeferredWhileHiddenCount);

        // 3. Focus Mode suppresses repeated-click annoyance escalation and ordinary notifications, while allowing High-priority alerts
        var focusPolicy = new AwarenessPolicy
        {
            ShouldShowMascot = true,
            AllowAutonomousMovement = false,
            AllowRepeatedClickAnnoyance = false,
            AllowNotificationReaction = true,
            AllowCenterScreenEntrance = true,
            IsQuietMode = true,
            EffectiveContext = DesktopContext.Focus,
            Reason = "ManualFocusMode"
        };

        for (int i = 0; i < 7; i++)
        {
            var clickRes = scheduler.Interaction.EvaluateSingleClick(nowUtc.AddMilliseconds(i * 150), BehaviourSettings.Default, focusPolicy);
            Assert.NotEqual(MascotState.Annoyed, clickRes.ReactionState);
        }

        var ordinaryNotif = n1 with { Id = "m6-n2", HasExplicitUrgencyHint = false };
        notifScheduler.EnqueueNewNotifications([ordinaryNotif], nowUtc.AddSeconds(20), NotificationSettings.Default);
        var focusOrdinaryDec = notifScheduler.EvaluatePendingBatch(
            nowUtc.AddSeconds(25),
            NotificationSettings.Default,
            BehaviourSettings.Default,
            scheduler.PriorityResolver,
            movement.CurrentState,
            personality.CurrentState,
            totalUnreadCount: 2,
            forceFlush: true,
            awarenessPolicy: focusPolicy);
        Assert.NotNull(focusOrdinaryDec);
        Assert.False(focusOrdinaryDec!.ShouldReact);
        Assert.Contains("SuppressedInFocusMode", focusOrdinaryDec.SuppressionReason);
    }

    [Fact]
    public void SettingsRepository_PersistsAndLoadsAwarenessSettingsAndAppRules()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m6_settings_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var repo = new SettingsRepository(db);

            var custom = AwarenessSettings.Default with
            {
                IsEnabled = true,
                FullScreenDetectionEnabled = true,
                AutoHideOnFullScreen = true,
                FullScreenRestoreDelayMs = 950,
                ManualFocusModeEnabled = true,
                QuietThresholdSeconds = 90,
                AwayThresholdSeconds = 360,
                SleepThresholdSeconds = 1200,
                FullScreenExcludedProcessesCsv = "obs64, discord",
                AppRules = new Dictionary<string, AppAwarenessBehavior>(StringComparer.OrdinalIgnoreCase)
                {
                    ["code"] = AppAwarenessBehavior.Focus,
                    ["spotify"] = AppAwarenessBehavior.Normal,
                    ["vlc"] = AppAwarenessBehavior.ExcludeFullScreenSuppression
                }
            };

            repo.SaveAwarenessSettings(custom);
            var loaded = repo.LoadAwarenessSettings();

            Assert.True(loaded.ManualFocusModeEnabled);
            Assert.Equal(950, loaded.FullScreenRestoreDelayMs);
            Assert.Equal(90, loaded.QuietThresholdSeconds);
            Assert.Equal(360, loaded.AwayThresholdSeconds);
            Assert.Equal(1200, loaded.SleepThresholdSeconds);
            Assert.Equal("obs64, discord", loaded.FullScreenExcludedProcessesCsv);
            Assert.Equal(AppAwarenessBehavior.Focus, loaded.AppRules["code"]);
            Assert.Equal(AppAwarenessBehavior.Normal, loaded.AppRules["spotify"]);
            Assert.Equal(AppAwarenessBehavior.ExcludeFullScreenSuppression, loaded.AppRules["vlc"]);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void M6_1_IntelligentSleep_EnforcesMouseOnlyInactivity_HoverOnlyWake_AndPriorityNotificationWake()
    {
        var resolver = new ContextPolicyResolver();
        var settings = AwarenessSettings.Default with
        {
            AutoSleepEnabled = true,
            SleepAfterSeconds = 30.0,
            PriorityNotificationWakeEnabled = true
        };
        var obs = MakeObservation(processName: "explorer");
        var t0 = new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);

        // 1. Mouse movement before sleep resets countdown (at t=20s, mouse idle is 5s -> stays Awake)
        var (s1, p1, _) = resolver.Evaluate(
            obs,
            rawIdleDuration: TimeSpan.FromSeconds(5),
            isSessionLocked: false,
            isSystemSuspended: false,
            settings,
            globalBehavioursPaused: false,
            t0.AddSeconds(20),
            mouseIdleDuration: TimeSpan.FromSeconds(5),
            mouseMovedOnDesktop: true);
        Assert.False(s1.IsSleeping);
        Assert.Equal(DesktopContext.Normal, p1.EffectiveContext);

        // 2 & 3. Keyboard activity alone does NOT reset mouse inactivity countdown; Bloop enters sleep after 30s of mouse inactivity
        // (rawIdleDuration = 1s due to keyboard typing, but mouseIdleDuration = 30.5s >= 30s)
        var (sSleep, pSleep, eSleep) = resolver.Evaluate(
            obs,
            rawIdleDuration: TimeSpan.FromSeconds(1),
            isSessionLocked: false,
            isSystemSuspended: false,
            settings,
            globalBehavioursPaused: false,
            t0.AddSeconds(51),
            mouseIdleDuration: TimeSpan.FromSeconds(30.5),
            mouseMovedOnDesktop: false);
        Assert.True(sSleep.IsSleeping);
        Assert.Equal(DesktopContext.Sleeping, pSleep.EffectiveContext);
        Assert.True(pSleep.ShouldShowMascot); // Sleeping never hides Bloop (no SW_HIDE)
        Assert.True(pSleep.ShouldEnterSleepMode);
        Assert.False(pSleep.AllowAutonomousMovement);
        Assert.Contains(eSleep, e => e.EventName == "SleepEntered");

        // 4. Moving the mouse elsewhere on the desktop (mouseIdleDuration drops to 0s, mouseMovedOnDesktop=true) does NOT wake Bloop once sleeping
        var (sStillAsleep, pStillAsleep, _) = resolver.Evaluate(
            obs,
            rawIdleDuration: TimeSpan.Zero,
            isSessionLocked: false,
            isSystemSuspended: false,
            settings,
            globalBehavioursPaused: false,
            t0.AddSeconds(55),
            mouseIdleDuration: TimeSpan.Zero,
            mouseMovedOnDesktop: true);
        Assert.True(sStillAsleep.IsSleeping);
        Assert.Equal(DesktopContext.Sleeping, pStillAsleep.EffectiveContext);
        Assert.True(pStillAsleep.ShouldEnterSleepMode);

        // 5 & 6. Moving the pointer over Bloop wakes him and restarts the sleep countdown after pointer leaves
        var tWake = t0.AddSeconds(60);
        bool wokeByPointer = resolver.TryWakeFromPointerEntry(tWake, out var wakeEvt);
        Assert.True(wokeByPointer);
        Assert.NotNull(wakeEvt);
        Assert.Equal("SleepExited", wakeEvt!.EventName);

        // At tWake + 10s, even if global mouseIdleDuration was 25s, sleep countdown restarted at tWake (only 10s elapsed < 30s)
        var (sAfterHover, pAfterHover, _) = resolver.Evaluate(
            obs,
            rawIdleDuration: TimeSpan.FromSeconds(10),
            isSessionLocked: false,
            isSystemSuspended: false,
            settings,
            globalBehavioursPaused: false,
            tWake.AddSeconds(10),
            mouseIdleDuration: TimeSpan.FromSeconds(25));
        Assert.False(sAfterHover.IsSleeping);
        Assert.Equal(DesktopContext.Normal, pAfterHover.EffectiveContext);

        // At tWake + 31s with 31s mouse inactivity -> enters sleep again
        var (sSleep2, _, _) = resolver.Evaluate(
            obs,
            rawIdleDuration: TimeSpan.FromSeconds(31),
            isSessionLocked: false,
            isSystemSuspended: false,
            settings,
            globalBehavioursPaused: false,
            tWake.AddSeconds(31),
            mouseIdleDuration: TimeSpan.FromSeconds(31));
        Assert.True(sSleep2.IsSleeping);

        // 7 & 9. Priority notification does NOT wake Bloop when PriorityNotificationWakeEnabled is disabled
        var disabledWakeSettings = settings with { PriorityNotificationWakeEnabled = false };
        bool disabledWake = resolver.TryWakeFromPriorityNotification(
            "UrgentToast",
            disabledWakeSettings,
            globalBehavioursPaused: false,
            out var rejectDisabledEvt,
            tWake.AddSeconds(32));
        Assert.False(disabledWake);
        Assert.NotNull(rejectDisabledEvt);
        Assert.Equal("NotificationWakeRejected", rejectDisabledEvt!.EventName);
        Assert.Equal("PriorityNotificationWakeDisabled", rejectDisabledEvt.PolicyReason);
        Assert.True(resolver.IsSleeping);

        // 8 & 10. Priority notification wakes Bloop once when enabled; subsequent call in same batch returns false
        bool prioWake = resolver.TryWakeFromPriorityNotification(
            "PriorityBatch(High)",
            settings,
            globalBehavioursPaused: false,
            out var prioWakeEvt,
            tWake.AddSeconds(33));
        Assert.True(prioWake);
        Assert.NotNull(prioWakeEvt);
        Assert.Equal("NotificationWakeAccepted", prioWakeEvt!.EventName);
        Assert.False(resolver.IsSleeping);

        bool secondWakeInBatch = resolver.TryWakeFromPriorityNotification(
            "PriorityBatch(Critical)",
            settings,
            globalBehavioursPaused: false,
            out var secondEvt,
            tWake.AddSeconds(33));
        Assert.False(secondWakeInBatch);
        Assert.Null(secondEvt);

        // 11, 12, 13. FullScreen, SessionLock, and SystemSuspend override Sleep and suppress entry
        var fullScreenObs = MakeObservation(
            processName: "game",
            isMaximized: false,
            hasCaption: false,
            winLeft: 0, winTop: 0, winRight: 1920, winBottom: 1080);
        var tFs = tWake.AddSeconds(70);
        _ = resolver.Evaluate(fullScreenObs, TimeSpan.FromSeconds(5), false, false, settings, false, tFs, TimeSpan.FromSeconds(5));
        // Confirm FullScreen after 1s while mouse idle is still 6s (< 30s)
        _ = resolver.Evaluate(fullScreenObs, TimeSpan.FromSeconds(6), false, false, settings, false, tFs.AddSeconds(1), TimeSpan.FromSeconds(6));
        // Now mouse idle reaches 35s while FullScreen is active -> SleepSuppressedByHigherPriorityState
        var (sFullScreen, pFullScreen, eFs) = resolver.Evaluate(fullScreenObs, TimeSpan.FromSeconds(35), false, false, settings, false, tFs.AddSeconds(40), TimeSpan.FromSeconds(35));
        Assert.False(sFullScreen.IsSleeping);
        Assert.Equal(DesktopContext.FullScreen, pFullScreen.EffectiveContext);
        Assert.False(pFullScreen.ShouldShowMascot);
        Assert.Contains(eFs, e => e.EventName == "SleepSuppressedByHigherPriorityState");
    }
}
