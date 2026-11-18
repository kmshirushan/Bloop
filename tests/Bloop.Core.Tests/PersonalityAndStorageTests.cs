using System.Text.Json;
using Bloop.Core.Behaviours;
using Bloop.Core.Events;
using Bloop.Core.Models;
using Bloop.Core.Movement;
using Bloop.Core.Personality;
using Bloop.Storage;
using Xunit;

namespace Bloop.Core.Tests;

public sealed class PersonalityAndStorageTests
{
    [Fact]
    public void PersonalityEngine_BlocksLowerPriorityTransitionWhileDragging()
    {
        var engine = new PersonalityEngine();
        Assert.Equal(MascotState.Idle, engine.CurrentState);

        Assert.True(engine.TryTransition(MascotState.Dragging, force: true));
        Assert.False(engine.TryTransition(MascotState.Happy, force: false));
        Assert.Equal(MascotState.Dragging, engine.CurrentState);

        Assert.True(engine.TryTransition(MascotState.Settling, force: false));
        Assert.Equal(MascotState.Settling, engine.CurrentState);
    }

    [Fact]
    public void PersonalityEngine_EmitsStateChangedEvenWhenReTriggeringSameExpression()
    {
        var engine = new PersonalityEngine();
        int eventCount = 0;
        engine.StateChanged += (_, _) => eventCount++;

        engine.TryTransition(MascotState.Happy, force: true);
        engine.TryTransition(MascotState.Happy, force: true);

        Assert.Equal(2, eventCount);
    }

    [Fact]
    public void IpcMessage_SerializesAndDeserializesCleanly()
    {
        var envelope = new IpcMessage<HostStatePayload>(
            Type: "host:state_change",
            Timestamp: 1700000000000L,
            Payload: new HostStatePayload("happy", (int)MascotState.Happy, "cmd123")
        );

        var json = JsonSerializer.Serialize(envelope);
        var raw = JsonSerializer.Deserialize<RawIpcMessage>(json);

        Assert.NotNull(raw);
        Assert.Equal("host:state_change", raw!.Type);
        var payload = raw.Payload.Deserialize<HostStatePayload>();
        Assert.NotNull(payload);
        Assert.Equal("happy", payload!.State);
        Assert.Equal("cmd123", payload.CommandId);
    }

    [Fact]
    public void SettingsRepository_PersistsAndLoadsWindowCoordinates()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"bloop_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(tempDb);
            var repo = new SettingsRepository(db);

            Assert.False(repo.LoadWindowPosition().HasSavedPosition);

            repo.SaveWindowPosition(1420.5, 780.25);
            var (left, top, hasSaved) = repo.LoadWindowPosition();

            Assert.True(hasSaved);
            Assert.Equal(1420.5, left, 2);
            Assert.Equal(780.25, top, 2);
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    [Fact]
    public void SettingsRepository_PersistsMovementSettingsAndDockPlacement()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"bloop_m2_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(tempDb);
            var repo = new SettingsRepository(db);

            var customSettings = new MovementSettings
            {
                AutonomousMovementEnabled = false,
                EdgeDockingEnabled = true,
                EdgeDetectionThresholdDip = 42.0,
                DockedVisibleRatio = 0.65,
                MovementSpeedMultiplier = 1.4,
                MinAutonomyIntervalMinutes = 4.0,
                MaxAutonomyIntervalMinutes = 9.0,
                AutonomyCooldownMinutes = 6.0
            };

            repo.SaveMovementSettings(customSettings);
            repo.SaveMascotPlacement(1660, 420, DockingEdge.Right, @"\\.\DISPLAY2", alwaysOnTop: true);

            var loadedSettings = repo.LoadMovementSettings();
            var loadedPlacement = repo.LoadMascotPlacement();

            Assert.False(loadedSettings.AutonomousMovementEnabled);
            Assert.True(loadedSettings.EdgeDockingEnabled);
            Assert.Equal(42.0, loadedSettings.EdgeDetectionThresholdDip, 2);
            Assert.Equal(0.65, loadedSettings.DockedVisibleRatio, 2);
            Assert.Equal(1.4, loadedSettings.MovementSpeedMultiplier, 2);
            Assert.Equal(6.0, loadedSettings.AutonomyCooldownMinutes, 2);

            Assert.True(loadedPlacement.HasSavedPosition);
            Assert.Equal(DockingEdge.Right, loadedPlacement.DockedEdge);
            Assert.Equal(@"\\.\DISPLAY2", loadedPlacement.MonitorId);
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    [Fact]
    public void MovementController_DetectsAllFourEdgesWithinThreshold()
    {
        var workArea = new WorkAreaBounds(0, 0, 1920, 1040, @"\\.\DISPLAY1");
        const double winW = 260;
        const double winH = 260;
        const double threshold = 35.0;

        Assert.Equal(DockingEdge.Left, MovementController.DetectClosestEdge(18, 400, winW, winH, workArea, threshold));
        Assert.Equal(DockingEdge.Right, MovementController.DetectClosestEdge(1920 - winW - 12, 400, winW, winH, workArea, threshold));
        Assert.Equal(DockingEdge.Top, MovementController.DetectClosestEdge(600, 20, winW, winH, workArea, threshold));
        Assert.Equal(DockingEdge.Bottom, MovementController.DetectClosestEdge(600, 1040 - winH - 15, winW, winH, workArea, threshold));
        Assert.Equal(DockingEdge.None, MovementController.DetectClosestEdge(500, 400, winW, winH, workArea, threshold));
    }

    [Fact]
    public void MovementController_CalculatesDockingPositionsInsideNegativeAndStandardMonitorBounds()
    {
        // Secondary monitor to the left with negative coordinates
        var leftMonitor = new WorkAreaBounds(-1920, 0, 0, 1040, @"\\.\DISPLAY2");
        var controller = new MovementController();

        var leftDock = controller.CalculateDockingPosition(DockingEdge.Left, -1910, 300, 260, 260, leftMonitor);
        Assert.Equal(-1920, leftDock.WindowLeftDip, 2);
        Assert.Equal(300, leftDock.WindowTopDip, 2);
        Assert.True(leftDock.ViewportOffsetX < 0); // Shifted left to clip 40% outside left viewport border

        var rightDock = controller.CalculateDockingPosition(DockingEdge.Right, -270, 300, 260, 260, leftMonitor);
        Assert.Equal(-260, rightDock.WindowLeftDip, 2);
        Assert.True(rightDock.ViewportOffsetX > 0);

        var topDock = controller.CalculateDockingPosition(DockingEdge.Top, -800, 10, 260, 260, leftMonitor);
        Assert.Equal(0, topDock.WindowTopDip, 2);
        Assert.True(topDock.ViewportOffsetY > 0);

        var bottomDock = controller.CalculateDockingPosition(DockingEdge.Bottom, -800, 770, 260, 260, leftMonitor);
        Assert.Equal(1040 - 260, bottomDock.WindowTopDip, 2);
        Assert.True(bottomDock.ViewportOffsetY < 0);
    }

    [Fact]
    public void MovementController_TransitionsThroughDragHopDockAndUndock()
    {
        var workArea = new WorkAreaBounds(0, 0, 1920, 1040, @"\\.\DISPLAY1");
        var controller = new MovementController(new Random(42));
        HopPlanEventArgs? lastHop = null;
        controller.HopRequested += (_, plan) => lastHop = plan;

        controller.BeginDrag(400, 400);
        Assert.Equal(MovementState.Dragging, controller.CurrentState);

        // Release near right edge -> triggers hop to right dock
        controller.CompleteDrag(1920 - 260 - 10, 400, 260, 260, workArea);
        Assert.Equal(MovementState.Hopping, controller.CurrentState);
        Assert.NotNull(lastHop);
        Assert.Equal(DockingEdge.Right, lastHop!.TargetEdge);

        controller.CompleteHop(lastHop.TargetWindowLeftDip, lastHop.TargetWindowTopDip, lastHop.TargetEdge);
        Assert.Equal(MovementState.Docked, controller.CurrentState);
        Assert.Equal(DockingEdge.Right, controller.CurrentEdge);

        // Clicking while docked triggers undock pop-out hop
        Assert.True(controller.RequestUndockPopOut(260, 260, workArea));
        Assert.Equal(MovementState.Hopping, controller.CurrentState);
        Assert.True(lastHop.IsUndockPopOut);
        Assert.Equal(DockingEdge.None, lastHop.TargetEdge);

        controller.CompleteHop(lastHop.TargetWindowLeftDip, lastHop.TargetWindowTopDip, DockingEdge.None);
        Assert.Equal(MovementState.Free, controller.CurrentState);
        Assert.Equal(DockingEdge.None, controller.CurrentEdge);
    }

    [Fact]
    public void MovementController_EnforcesAutonomyCooldownAndInterruptionRules()
    {
        var workArea = new WorkAreaBounds(0, 0, 1920, 1040, @"\\.\DISPLAY1");
        var controller = new MovementController(new Random(7));
        controller.SyncPosition(800, 500, workArea.MonitorId, DockingEdge.Left);

        // Immediately after completion, cooldown blocks autonomous hop
        var now = DateTimeOffset.UtcNow;
        controller.CompleteHop(0, 500, DockingEdge.Left);
        Assert.False(controller.TryTriggerAutonomousHop(now.AddMinutes(1), 260, 260, workArea));

        // When autonomous movement is disabled, it never triggers
        controller.UpdateSettings(controller.Settings with { AutonomousMovementEnabled = false });
        Assert.False(controller.TryTriggerAutonomousHop(now.AddMinutes(15), 260, 260, workArea));

        // Re-enable autonomous movement and advance past max interval + cooldown -> triggers hop to a DIFFERENT edge
        controller.UpdateSettings(controller.Settings with { AutonomousMovementEnabled = true });
        Assert.True(controller.TryTriggerAutonomousHop(DateTimeOffset.UtcNow.AddMinutes(15), 260, 260, workArea));
        Assert.Equal(MovementState.Hopping, controller.CurrentState);
        Assert.NotEqual(DockingEdge.Left, controller.CurrentEdge);
    }

    [Fact]
    public void MovementController_UpdateSettings_DoesNotResetDeadlineWhenNonTimingSettingChanges()
    {
        var controller = new MovementController(new Random(11));
        var originalDue = controller.NextAutonomyDueUtc;

        // Changing DockedVisibleRatio or MovementSpeedMultiplier must NOT reset NextAutonomyDueUtc
        controller.UpdateSettings(controller.Settings with
        {
            DockedVisibleRatio = 0.75,
            MovementSpeedMultiplier = 1.5
        });

        Assert.Equal(originalDue, controller.NextAutonomyDueUtc);
    }

    [Fact]
    public void InteractionBehaviour_DeduplicatesRecentReactionsAndTriggersProgressiveAnnoyanceWithRecovery()
    {
        var interaction = new InteractionBehaviour(new Random(3));
        var settings = BehaviourSettings.Default with
        {
            AnnoyanceMildThreshold = 3,
            AnnoyancePeakThreshold = 5,
            AnnoyanceWindowSeconds = 3.5,
            AnnoyanceRecoverySeconds = 4.0
        };

        var t0 = DateTimeOffset.UtcNow;

        // Clicks 1 & 2 -> calm weighted reactions
        var r1 = interaction.EvaluateSingleClick(t0, settings);
        var r2 = interaction.EvaluateSingleClick(t0.AddMilliseconds(300), settings);
        Assert.Equal(AnnoyanceStage.Calm, r1.AnnoyanceStage);
        Assert.Equal(AnnoyanceStage.Calm, r2.AnnoyanceStage);

        // Click 3 -> mild annoyance (Surprised)
        var r3 = interaction.EvaluateSingleClick(t0.AddMilliseconds(600), settings);
        Assert.Equal(AnnoyanceStage.Mild, r3.AnnoyanceStage);
        Assert.Equal(MascotState.Surprised, r3.ReactionState);

        // Click 4 & 5 -> peak annoyance (Annoyed)
        _ = interaction.EvaluateSingleClick(t0.AddMilliseconds(900), settings);
        var r5 = interaction.EvaluateSingleClick(t0.AddMilliseconds(1200), settings);
        Assert.Equal(AnnoyanceStage.Annoyed, r5.AnnoyanceStage);
        Assert.Equal(MascotState.Annoyed, r5.ReactionState);

        // Double-click clears click burst so opening the panel never triggers annoyance
        interaction.NotifyDoubleClick();
        var afterDoubleClick = interaction.EvaluateSingleClick(t0.AddSeconds(10), settings);
        Assert.Equal(AnnoyanceStage.Calm, afterDoubleClick.AnnoyanceStage);
    }

    [Fact]
    public void ExplorationBehaviour_RejectsMinimizedMaximizedFullscreenAndSelectsTopPerchWhenValid()
    {
        var exploration = new ExplorationBehaviour(new Random(5));
        var workArea = new WorkAreaBounds(0, 0, 1920, 1040, @"\\.\DISPLAY1");

        // 1. Maximized window rejected
        var maxWin = new ForegroundWindowGeometry(true, 0, 0, 1920, 1040, false, true, false, @"\\.\DISPLAY1");
        Assert.False(exploration.TrySelectWindowBoundaryTarget(maxWin, workArea, 260, 260, out _, out var reason1));
        Assert.Contains("UnsuitableWindowState", reason1);

        // 2. Normal window with top clearance >= 260 * 0.65 (169 DIP) succeeds
        var normalWin = new ForegroundWindowGeometry(true, 200, 220, 1400, 900, false, false, false, @"\\.\DISPLAY1");
        Assert.True(exploration.TrySelectWindowBoundaryTarget(normalWin, workArea, 260, 260, out var target, out _));
        Assert.NotNull(target);
        Assert.Equal(DockingEdge.None, target!.DockEdge);
        Assert.InRange(target.TargetWindowLeftDip, 200, 1400 - 260);
    }

    [Fact]
    public void NotificationBehaviour_RecordsInboxEvenWhenVisualReactionIsDisabled()
    {
        var notif = new NotificationBehaviour();
        var resolver = new BehaviourPriorityResolver();
        var alert = new NotificationAlertEvent("Teams", "Standup in 5m", DateTimeOffset.UtcNow);

        bool reactedDisabled = notif.OnNotificationArrived(
            alert,
            BehaviourSettings.Default with { NotificationReactionsEnabled = false },
            resolver,
            MovementState.Docked,
            MascotState.Idle
        );

        Assert.False(reactedDisabled);
        Assert.Single(notif.Inbox);

        bool reactedEnabled = notif.OnNotificationArrived(
            alert,
            BehaviourSettings.Default with { NotificationReactionsEnabled = true },
            resolver,
            MovementState.Docked,
            MascotState.Idle
        );

        Assert.True(reactedEnabled);
        Assert.Equal(2, notif.Inbox.Count);
    }

    [Fact]
    public void SettingsRepository_PersistsIndependentBehaviourSettings()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"bloop_m3_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(tempDb);
            var repo = new SettingsRepository(db);

            var custom = BehaviourSettings.Default with
            {
                AutonomousEdgeHoppingEnabled = false,
                WindowBoundaryExplorationEnabled = true,
                PeekabooEnabled = true,
                RandomClickReactionsEnabled = false,
                RepeatedClickAnnoyanceEnabled = true,
                NotificationReactionsEnabled = false,
                AnnoyancePeakThreshold = 7
            };

            repo.SaveBehaviourSettings(custom);
            var loaded = repo.LoadBehaviourSettings();

            Assert.False(loaded.AutonomousEdgeHoppingEnabled);
            Assert.True(loaded.WindowBoundaryExplorationEnabled);
            Assert.True(loaded.PeekabooEnabled);
            Assert.False(loaded.RandomClickReactionsEnabled);
            Assert.True(loaded.RepeatedClickAnnoyanceEnabled);
            Assert.False(loaded.NotificationReactionsEnabled);
            Assert.Equal(7, loaded.AnnoyancePeakThreshold);
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    [Fact]
    public void BehaviourScheduler_SupportsSleepModeWelcomeBackCornerExplorationAndGlobalPause()
    {
        var workArea = new WorkAreaBounds(0, 0, 1920, 1040, @"\\.\DISPLAY1");
        var movement = new MovementController(new Random(21));
        var personality = new PersonalityEngine();
        var scheduler = new BehaviourScheduler(movement, personality, new Random(21));

        scheduler.UpdateSettings(BehaviourSettings.Default with
        {
            SleepModeEnabled = true,
            SleepInactivityMinutes = 2.0,
            WelcomeBackReactionEnabled = true,
            AttentionSeekingEnabled = true,
            AttentionSeekMinutes = 1.0,
            CursorChasingEnabled = true,
            CursorChaseCooldownSeconds = 10.0
        });

        var t0 = DateTimeOffset.UtcNow;
        movement.SyncPosition(800, 500, workArea.MonitorId, DockingEdge.None);

        // 1. After 1.2 minutes of inactivity -> Attention Seeking sets Curious state
        scheduler.EvaluateTick(t0.AddMinutes(1.2), 260, 260, workArea, default);
        Assert.Equal(MascotState.Curious, personality.CurrentState);
        Assert.False(scheduler.IsSleeping);

        // 2. After 2.5 minutes of inactivity -> Sleep Mode activates Sleepy state
        scheduler.EvaluateTick(t0.AddMinutes(2.5), 260, 260, workArea, default);
        Assert.True(scheduler.IsSleeping);
        Assert.Equal(MascotState.Sleepy, personality.CurrentState);

        // 3. User cursor activity wakes Bloop with a Happy welcome-back reaction & triggers Cursor Chase hop
        scheduler.NotifyUserActivity(t0.AddMinutes(3.0), 1220, 520, 260, 260, workArea);
        Assert.False(scheduler.IsSleeping);
        Assert.Equal(MovementState.Hopping, movement.CurrentState);
        movement.CompleteHop(movement.CurrentWindowLeftDip, movement.CurrentWindowTopDip, DockingEdge.None);

        // 4. Global Pause suspends movement and cancels active movement
        scheduler.UpdateSettings(scheduler.Settings with { GlobalBehavioursPaused = true });
        Assert.Equal(MovementState.Paused, movement.CurrentState);

        scheduler.UpdateSettings(scheduler.Settings with { GlobalBehavioursPaused = false });
        Assert.Equal(MovementState.Free, movement.CurrentState);
    }

    [Fact]
    public void BehaviourScheduler_UserDragCancelsScheduledExplorationAndPeekabooAndEdgeBalanceWorkWhileDocked()
    {
        var workArea = new WorkAreaBounds(0, 0, 1920, 1040, @"\\.\DISPLAY1");
        var movement = new MovementController(new Random(9));
        var personality = new PersonalityEngine();
        var scheduler = new BehaviourScheduler(movement, personality, new Random(9));

        int peekabooCount = 0;
        int balanceCount = 0;
        scheduler.PeekabooRequested += (_, _) => peekabooCount++;
        scheduler.EdgeBalanceRequested += (_, _) => balanceCount++;

        movement.SyncPosition(0, 400, workArea.MonitorId, DockingEdge.Left);
        Assert.Equal(MovementState.Docked, movement.CurrentState);

        Assert.True(scheduler.TriggerPeekaboo(DateTimeOffset.UtcNow));
        Assert.Equal(1, peekabooCount);

        Assert.True(scheduler.TriggerEdgeBalance(DateTimeOffset.UtcNow));
        Assert.Equal(1, balanceCount);

        // Start a corner exploration hop and interrupt it immediately with user drag
        var corner = scheduler.Exploration.SelectCornerTarget(workArea, 260, 260, DockingEdge.Left);
        Assert.True(movement.TryHopToCoordinate(corner.TargetWindowLeftDip, corner.TargetWindowTopDip, corner.DockEdge, workArea, "CornerTest"));
        Assert.Equal(MovementState.Hopping, movement.CurrentState);

        // User drags Bloop mid-hop -> cancels hop and enters Dragging state cleanly
        movement.BeginDrag(200, 300);
        Assert.Equal(MovementState.Dragging, movement.CurrentState);
    }
}
