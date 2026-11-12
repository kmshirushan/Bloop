using System.Text.Json;
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

        // While control panel is active or dragging, autonomous hop is blocked even after 10 minutes
        controller.SetControlPanelActive(true);
        Assert.False(controller.TryTriggerAutonomousHop(now.AddMinutes(10), 260, 260, workArea));
        controller.SetControlPanelActive(false);

        // When autonomous movement is disabled, it never triggers
        controller.UpdateSettings(controller.Settings with { AutonomousMovementEnabled = false });
        Assert.False(controller.TryTriggerAutonomousHop(now.AddMinutes(15), 260, 260, workArea));

        // Re-enable autonomous movement and advance past max interval + cooldown -> triggers hop to a DIFFERENT edge
        controller.UpdateSettings(controller.Settings with { AutonomousMovementEnabled = true });
        Assert.True(controller.TryTriggerAutonomousHop(DateTimeOffset.UtcNow.AddMinutes(15), 260, 260, workArea));
        Assert.Equal(MovementState.Hopping, controller.CurrentState);
        Assert.NotEqual(DockingEdge.Left, controller.CurrentEdge);
    }
}
