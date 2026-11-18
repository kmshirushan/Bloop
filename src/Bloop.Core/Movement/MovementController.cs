using Bloop.Core.Awareness;
using Bloop.Core.Events;
using Bloop.Core.Interfaces;

namespace Bloop.Core.Movement;

/// <summary>
/// Platform-independent movement, edge-docking, and autonomous hopping controller.
/// Owns physical MovementState, DockingEdge, docking geometry calculation, and autonomy cooldowns.
/// </summary>
public sealed class MovementController : IMovementController
{
    private readonly Random _random;
    private DateTimeOffset _lastMovementCompletedUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAutonomyDueUtc;
    private DateTimeOffset _settlingUntilUtc = DateTimeOffset.MinValue;
    private bool _isControlPanelActive;
    private bool _isPaused;
    private string? _lastLoggedRejectionKey;

    public MovementState CurrentState { get; private set; } = MovementState.Free;
    public DockingEdge CurrentEdge { get; private set; } = DockingEdge.None;
    public MovementSettings Settings { get; private set; } = MovementSettings.Default;
    public AwarenessPolicy CurrentAwarenessPolicy { get; private set; } = AwarenessPolicy.Default;
    public DateTimeOffset NextAutonomyDueUtc => _nextAutonomyDueUtc;

    public double CurrentWindowLeftDip { get; private set; }
    public double CurrentWindowTopDip { get; private set; }
    public string CurrentMonitorId { get; private set; } = "primary";

    public event EventHandler<MovementStateChangedEventArgs>? MovementStateChanged;
    public event EventHandler<DockingEdge>? EdgeCandidateChanged;
    public event EventHandler<HopPlanEventArgs>? HopRequested;
    public event EventHandler<AutonomyDiagnosticEventArgs>? DiagnosticLogged;

    public MovementController(Random? random = null)
    {
        _random = random ?? new Random();
        var now = DateTimeOffset.UtcNow;
        ScheduleNextAutonomousHop(now, isInitial: true, reason: "ConstructorInit");
        EmitDiagnostic(
            AutonomyDiagnosticStage.SchedulerInitialized,
            $"Autonomy scheduler initialized (NextDue={_nextAutonomyDueUtc:HH:mm:ss} UTC)");
    }

    public void UpdateSettings(MovementSettings settings)
    {
        var prev = Settings;
        Settings = settings;

        // Only reschedule the movement deadline when autonomy timing or enablement settings actually changed!
        bool timingChanged =
            Math.Abs(prev.MinAutonomyIntervalMinutes - settings.MinAutonomyIntervalMinutes) > 1e-4 ||
            Math.Abs(prev.MaxAutonomyIntervalMinutes - settings.MaxAutonomyIntervalMinutes) > 1e-4 ||
            Math.Abs(prev.AutonomyCooldownMinutes - settings.AutonomyCooldownMinutes) > 1e-4 ||
            (!prev.AutonomousMovementEnabled && settings.AutonomousMovementEnabled);

        if (timingChanged)
        {
            ScheduleNextAutonomousHop(DateTimeOffset.UtcNow, isInitial: true, reason: "TimingSettingsUpdated");
        }
    }

    public void SetControlPanelActive(bool isActive)
    {
        // Note: Having the demonstration or settings window visible no longer blocks autonomous movement.
        _isControlPanelActive = isActive;
    }

    public void SyncPosition(double windowLeftDip, double windowTopDip, string monitorId, DockingEdge edge = DockingEdge.None)
    {
        CurrentWindowLeftDip = windowLeftDip;
        CurrentWindowTopDip = windowTopDip;
        if (!string.IsNullOrWhiteSpace(monitorId))
        {
            CurrentMonitorId = monitorId;
        }
        CurrentEdge = edge;
        TransitionTo(edge == DockingEdge.None ? MovementState.Free : MovementState.Docked, edge);
    }

    public (bool IsDeferredUntilHopComplete, double NewLeftDip, double NewTopDip, DockingPosition? DockPosition) RecalculatePositionForScaleChange(
        double previousWindowSizeDip,
        double newWindowSizeDip,
        WorkAreaBounds workArea)
    {
        if (CurrentState is MovementState.Hopping or MovementState.PreparingToHop or MovementState.Undocking)
        {
            return (true, CurrentWindowLeftDip, CurrentWindowTopDip, null);
        }

        double centerX = CurrentWindowLeftDip + (previousWindowSizeDip * 0.5);
        double centerY = CurrentWindowTopDip + (previousWindowSizeDip * 0.5);
        double desiredLeft = centerX - (newWindowSizeDip * 0.5);
        double desiredTop = centerY - (newWindowSizeDip * 0.5);

        if (CurrentEdge != DockingEdge.None)
        {
            var dockPos = CalculateDockingPosition(
                CurrentEdge,
                desiredLeft,
                desiredTop,
                newWindowSizeDip,
                newWindowSizeDip,
                workArea);
            CurrentWindowLeftDip = dockPos.WindowLeftDip;
            CurrentWindowTopDip = dockPos.WindowTopDip;
            CurrentMonitorId = workArea.MonitorId;
            return (false, dockPos.WindowLeftDip, dockPos.WindowTopDip, dockPos);
        }

        double clampedLeft = Math.Clamp(
            desiredLeft,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - newWindowSizeDip));
        double clampedTop = Math.Clamp(
            desiredTop,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - newWindowSizeDip));

        CurrentWindowLeftDip = clampedLeft;
        CurrentWindowTopDip = clampedTop;
        CurrentMonitorId = workArea.MonitorId;
        return (false, clampedLeft, clampedTop, null);
    }

    public bool BeginDrag(double windowLeftDip, double windowTopDip)
    {
        if (CurrentState is MovementState.Hopping or MovementState.PreparingToHop or MovementState.Undocking)
        {
            EmitDiagnostic(
                AutonomyDiagnosticStage.MovementCancelled,
                "Active hop cancelled by user drag interaction");
        }

        CurrentWindowLeftDip = windowLeftDip;
        CurrentWindowTopDip = windowTopDip;
        CurrentEdge = DockingEdge.None;
        TransitionTo(MovementState.Dragging, DockingEdge.None);
        EdgeCandidateChanged?.Invoke(this, DockingEdge.None);
        return true;
    }

    public void CancelActiveMovement(string reason)
    {
        if (CurrentState is MovementState.Hopping or MovementState.PreparingToHop or MovementState.Undocking)
        {
            EmitDiagnostic(
                AutonomyDiagnosticStage.MovementCancelled,
                $"Movement cancelled: {reason}");
            TransitionTo(CurrentEdge == DockingEdge.None ? MovementState.Free : MovementState.Docked, CurrentEdge);
        }
    }

    public void SetPaused(bool paused)
    {
        if (_isPaused == paused) return;
        _isPaused = paused;
        if (paused)
        {
            CancelActiveMovement("GlobalPauseActivated");
            TransitionTo(MovementState.Paused, CurrentEdge);
            EmitDiagnostic(AutonomyDiagnosticStage.EligibilityRejected, "Global pause activated", "GlobalPauseActive");
        }
        else
        {
            TransitionTo(CurrentEdge == DockingEdge.None ? MovementState.Free : MovementState.Docked, CurrentEdge);
            EmitDiagnostic(AutonomyDiagnosticStage.EligibilityChecked, "Global pause resumed");
        }
    }

    public void ApplyAwarenessPolicy(AwarenessPolicy policy, DateTimeOffset nowUtc, TimeSpan? postRestoreSettling = null)
    {
        CurrentAwarenessPolicy = policy ?? AwarenessPolicy.Default;

        if (!CurrentAwarenessPolicy.ShouldShowMascot)
        {
            CancelActiveMovement($"MascotHidden({CurrentAwarenessPolicy.Reason})");
        }

        if (postRestoreSettling.HasValue && postRestoreSettling.Value > TimeSpan.Zero)
        {
            var settleDue = nowUtc + postRestoreSettling.Value;
            if (settleDue > _settlingUntilUtc)
            {
                _settlingUntilUtc = settleDue;
            }
            if (_nextAutonomyDueUtc < _settlingUntilUtc)
            {
                _nextAutonomyDueUtc = _settlingUntilUtc;
            }
        }
    }

    public DockingEdge EvaluateDragProximity(
        double windowLeftDip,
        double windowTopDip,
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea)
    {
        CurrentWindowLeftDip = windowLeftDip;
        CurrentWindowTopDip = windowTopDip;
        CurrentMonitorId = workArea.MonitorId;

        if (!Settings.EdgeDockingEnabled)
        {
            EdgeCandidateChanged?.Invoke(this, DockingEdge.None);
            return DockingEdge.None;
        }

        var edge = DetectClosestEdge(
            windowLeftDip,
            windowTopDip,
            windowWidthDip,
            windowHeightDip,
            workArea,
            Settings.EdgeDetectionThresholdDip
        );

        EdgeCandidateChanged?.Invoke(this, edge);
        return edge;
    }

    public static DockingEdge DetectClosestEdge(
        double windowLeftDip,
        double windowTopDip,
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea,
        double thresholdDip)
    {
        double distLeft = Math.Abs(windowLeftDip - workArea.Left);
        double distRight = Math.Abs((windowLeftDip + windowWidthDip) - workArea.Right);
        double distTop = Math.Abs(windowTopDip - workArea.Top);
        double distBottom = Math.Abs((windowTopDip + windowHeightDip) - workArea.Bottom);

        double minDistance = thresholdDip;
        DockingEdge candidate = DockingEdge.None;

        if (distLeft <= minDistance)
        {
            minDistance = distLeft;
            candidate = DockingEdge.Left;
        }
        if (distRight <= minDistance)
        {
            minDistance = distRight;
            candidate = DockingEdge.Right;
        }
        if (distTop <= minDistance)
        {
            minDistance = distTop;
            candidate = DockingEdge.Top;
        }
        if (distBottom <= minDistance)
        {
            candidate = DockingEdge.Bottom;
        }

        return candidate;
    }

    public DockingPosition CalculateDockingPosition(
        DockingEdge edge,
        double desiredLeftDip,
        double desiredTopDip,
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea)
    {
        double clampedLeft = Math.Clamp(
            desiredLeftDip,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - windowWidthDip)
        );
        double clampedTop = Math.Clamp(
            desiredTopDip,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - windowHeightDip)
        );

        var (offsetX, offsetY) = CalculateViewportOffset(edge, Settings.DockedVisibleRatio);

        return edge switch
        {
            DockingEdge.Left => new DockingPosition(
                DockingEdge.Left,
                workArea.Left,
                clampedTop,
                offsetX,
                offsetY,
                workArea.MonitorId),
            DockingEdge.Right => new DockingPosition(
                DockingEdge.Right,
                Math.Max(workArea.Left, workArea.Right - windowWidthDip),
                clampedTop,
                offsetX,
                offsetY,
                workArea.MonitorId),
            DockingEdge.Top => new DockingPosition(
                DockingEdge.Top,
                clampedLeft,
                workArea.Top,
                offsetX,
                offsetY,
                workArea.MonitorId),
            DockingEdge.Bottom => new DockingPosition(
                DockingEdge.Bottom,
                clampedLeft,
                Math.Max(workArea.Top, workArea.Bottom - windowHeightDip),
                offsetX,
                offsetY,
                workArea.MonitorId),
            _ => new DockingPosition(
                DockingEdge.None,
                clampedLeft,
                clampedTop,
                0,
                0,
                workArea.MonitorId)
        };
    }

    public static (double OffsetX, double OffsetY) CalculateViewportOffset(DockingEdge edge, double visibleRatio)
    {
        if (edge == DockingEdge.None) return (0, 0);

        double clampedRatio = Math.Clamp(visibleRatio, 0.35, 0.90);
        double shiftMagnitude = Math.Clamp(2.44 - (2.0 * clampedRatio), 0.55, 1.75);

        return edge switch
        {
            DockingEdge.Left => (-shiftMagnitude, 0),
            DockingEdge.Right => (shiftMagnitude, 0),
            DockingEdge.Top => (0, shiftMagnitude),
            DockingEdge.Bottom => (0, -shiftMagnitude),
            _ => (0, 0)
        };
    }

    public void CompleteDrag(
        double releaseLeftDip,
        double releaseTopDip,
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea)
    {
        CurrentMonitorId = workArea.MonitorId;
        EdgeCandidateChanged?.Invoke(this, DockingEdge.None);

        var candidateEdge = Settings.EdgeDockingEnabled
            ? DetectClosestEdge(
                releaseLeftDip,
                releaseTopDip,
                windowWidthDip,
                windowHeightDip,
                workArea,
                Settings.EdgeDetectionThresholdDip)
            : DockingEdge.None;

        if (candidateEdge != DockingEdge.None)
        {
            var dockTarget = CalculateDockingPosition(
                candidateEdge,
                releaseLeftDip,
                releaseTopDip,
                windowWidthDip,
                windowHeightDip,
                workArea);

            StartHop(
                releaseLeftDip,
                releaseTopDip,
                dockTarget.WindowLeftDip,
                dockTarget.WindowTopDip,
                candidateEdge,
                dockTarget.ViewportOffsetX,
                dockTarget.ViewportOffsetY,
                isUndockPopOut: false,
                sourceReason: $"ManualDragTo{candidateEdge}");
            return;
        }

        double clampedLeft = Math.Clamp(
            releaseLeftDip,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - windowWidthDip));
        double clampedTop = Math.Clamp(
            releaseTopDip,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - windowHeightDip));

        CurrentWindowLeftDip = clampedLeft;
        CurrentWindowTopDip = clampedTop;
        CurrentEdge = DockingEdge.None;
        _lastMovementCompletedUtc = DateTimeOffset.UtcNow;
        ScheduleNextAutonomousHop(_lastMovementCompletedUtc, isInitial: false, reason: "ManualDragReleaseFree");
        TransitionTo(MovementState.Free, DockingEdge.None);
    }

    public bool RequestUndockPopOut(
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea)
    {
        if (CurrentState != MovementState.Docked || CurrentEdge == DockingEdge.None)
        {
            return false;
        }

        const double popOutMarginDip = 48.0;
        double targetLeft = CurrentWindowLeftDip;
        double targetTop = CurrentWindowTopDip;

        switch (CurrentEdge)
        {
            case DockingEdge.Left:
                targetLeft = Math.Min(workArea.Right - windowWidthDip, workArea.Left + popOutMarginDip);
                break;
            case DockingEdge.Right:
                targetLeft = Math.Max(workArea.Left, workArea.Right - windowWidthDip - popOutMarginDip);
                break;
            case DockingEdge.Top:
                targetTop = Math.Min(workArea.Bottom - windowHeightDip, workArea.Top + popOutMarginDip);
                break;
            case DockingEdge.Bottom:
                targetTop = Math.Max(workArea.Top, workArea.Bottom - windowHeightDip - popOutMarginDip);
                break;
        }

        TransitionTo(MovementState.Undocking, DockingEdge.None);
        StartHop(
            CurrentWindowLeftDip,
            CurrentWindowTopDip,
            targetLeft,
            targetTop,
            DockingEdge.None,
            0,
            0,
            isUndockPopOut: true,
            sourceReason: "ClickUndockPopOut");
        return true;
    }

    public bool TryTriggerAutonomousHop(
        DateTimeOffset nowUtc,
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea)
    {
        if (!CanRunAutonomousMovement(nowUtc, out var rejectionReason))
        {
            // Deduplicate identical rejection reason category to avoid log flooding while still logging transitions
            string rejectionKey = rejectionReason?.Split('(')[0] ?? "Rejected";
            if (_lastLoggedRejectionKey != rejectionKey)
            {
                _lastLoggedRejectionKey = rejectionKey;
                EmitDiagnostic(
                    AutonomyDiagnosticStage.EligibilityRejected,
                    $"Autonomous movement rejected: {rejectionReason}",
                    rejectionReason: rejectionReason);
            }
            return false;
        }

        _lastLoggedRejectionKey = null;
        EmitDiagnostic(
            AutonomyDiagnosticStage.EligibilityChecked,
            "Autonomous movement eligible — selecting target edge");

        return ExecuteAutonomousEdgeHop(windowWidthDip, windowHeightDip, workArea, "ScheduledAutonomy");
    }

    public bool TriggerManualAutonomousHop(
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea)
    {
        if (CurrentState is MovementState.Dragging or MovementState.Hopping or MovementState.PreparingToHop or MovementState.Undocking)
        {
            var reason = $"BusyState({CurrentState})";
            EmitDiagnostic(
                AutonomyDiagnosticStage.EligibilityRejected,
                $"Manual dev trigger rejected: {reason}",
                rejectionReason: reason);
            return false;
        }

        EmitDiagnostic(
            AutonomyDiagnosticStage.EligibilityChecked,
            "Developer manual trigger invoked — bypassing timer/cooldown");

        return ExecuteAutonomousEdgeHop(windowWidthDip, windowHeightDip, workArea, "DeveloperManualTrigger");
    }

    public bool TryHopToCoordinate(
        double targetLeftDip,
        double targetTopDip,
        DockingEdge targetEdge,
        WorkAreaBounds workArea,
        string reason)
    {
        if (CurrentState is MovementState.Dragging or MovementState.Hopping or MovementState.PreparingToHop or MovementState.Undocking or MovementState.Paused)
        {
            return false;
        }

        CurrentMonitorId = workArea.MonitorId;
        var (offsetX, offsetY) = CalculateViewportOffset(targetEdge, Settings.DockedVisibleRatio);
        EmitDiagnostic(
            AutonomyDiagnosticStage.TargetSelected,
            $"Selected target ({targetLeftDip:0},{targetTopDip:0}) edge={targetEdge} ({reason})",
            selectedEdge: targetEdge);

        StartHop(
            CurrentWindowLeftDip,
            CurrentWindowTopDip,
            targetLeftDip,
            targetTopDip,
            targetEdge,
            offsetX,
            offsetY,
            isUndockPopOut: false,
            sourceReason: reason);
        return true;
    }

    private bool ExecuteAutonomousEdgeHop(
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea,
        string sourceReason)
    {
        CurrentMonitorId = workArea.MonitorId;
        var nextEdge = SelectNextAutonomousEdge(CurrentEdge);
        double margin = 48.0;
        double randomX = workArea.Left + margin + _random.NextDouble() * Math.Max(10, workArea.Width - windowWidthDip - 2 * margin);
        double randomY = workArea.Top + margin + _random.NextDouble() * Math.Max(10, workArea.Height - windowHeightDip - 2 * margin);

        var dockPos = CalculateDockingPosition(
            nextEdge,
            randomX,
            randomY,
            windowWidthDip,
            windowHeightDip,
            workArea);

        EmitDiagnostic(
            AutonomyDiagnosticStage.TargetSelected,
            $"Selected target edge={nextEdge} at ({dockPos.WindowLeftDip:0},{dockPos.WindowTopDip:0}) on {workArea.MonitorId}",
            selectedEdge: nextEdge);

        StartHop(
            CurrentWindowLeftDip,
            CurrentWindowTopDip,
            dockPos.WindowLeftDip,
            dockPos.WindowTopDip,
            nextEdge,
            dockPos.ViewportOffsetX,
            dockPos.ViewportOffsetY,
            isUndockPopOut: false,
            sourceReason: sourceReason);

        return true;
    }

    public bool CanRunAutonomousMovement(DateTimeOffset nowUtc) =>
        CanRunAutonomousMovement(nowUtc, out _);

    public bool CanRunAutonomousMovement(DateTimeOffset nowUtc, out string? rejectionReason)
    {
        if (!Settings.AutonomousMovementEnabled)
        {
            rejectionReason = "AutonomousMovementDisabled";
            return false;
        }

        if (!CurrentAwarenessPolicy.ShouldShowMascot)
        {
            rejectionReason = $"AwarenessHidden({CurrentAwarenessPolicy.Reason})";
            return false;
        }

        if (!CurrentAwarenessPolicy.AllowAutonomousMovement)
        {
            rejectionReason = $"AwarenessSuppressed({CurrentAwarenessPolicy.Reason})";
            return false;
        }

        if (nowUtc < _settlingUntilUtc)
        {
            rejectionReason = $"PostRestoreSettling({(_settlingUntilUtc - nowUtc).TotalSeconds:0}s remaining)";
            return false;
        }

        if (!Settings.EdgeDockingEnabled)
        {
            rejectionReason = "EdgeDockingDisabled";
            return false;
        }

        if (CurrentState is MovementState.Dragging or MovementState.PreparingToHop or MovementState.Hopping or MovementState.Undocking or MovementState.Paused)
        {
            rejectionReason = $"ActiveMovementState({CurrentState})";
            return false;
        }

        if (_lastMovementCompletedUtc != DateTimeOffset.MinValue)
        {
            var elapsedSinceLast = nowUtc - _lastMovementCompletedUtc;
            var requiredCooldown = TimeSpan.FromMinutes(Settings.AutonomyCooldownMinutes);
            if (elapsedSinceLast < requiredCooldown)
            {
                var remainingCooldown = requiredCooldown - elapsedSinceLast;
                rejectionReason = $"CooldownActive({remainingCooldown.TotalSeconds:0}s remaining)";
                return false;
            }
        }

        if (nowUtc < _nextAutonomyDueUtc)
        {
            var remainingDue = _nextAutonomyDueUtc - nowUtc;
            rejectionReason = $"NotYetDue({remainingDue.TotalSeconds:0}s remaining)";
            return false;
        }

        rejectionReason = null;
        return true;
    }

    public DockingEdge SelectNextAutonomousEdge(DockingEdge currentEdge)
    {
        Span<DockingEdge> allEdges =
        [
            DockingEdge.Left,
            DockingEdge.Right,
            DockingEdge.Top,
            DockingEdge.Bottom
        ];

        var alternatives = new List<DockingEdge>(3);
        foreach (var edge in allEdges)
        {
            if (edge != currentEdge)
            {
                alternatives.Add(edge);
            }
        }

        return alternatives[_random.Next(alternatives.Count)];
    }

    public void CompleteHop(double finalLeftDip, double finalTopDip, DockingEdge finalEdge)
    {
        CurrentWindowLeftDip = finalLeftDip;
        CurrentWindowTopDip = finalTopDip;
        CurrentEdge = finalEdge;
        _lastMovementCompletedUtc = DateTimeOffset.UtcNow;
        ScheduleNextAutonomousHop(_lastMovementCompletedUtc, isInitial: false, reason: "HopCompleted");
        TransitionTo(finalEdge == DockingEdge.None ? MovementState.Free : MovementState.Docked, finalEdge);
        EmitDiagnostic(
            AutonomyDiagnosticStage.MovementCompleted,
            $"Hop completed at ({finalLeftDip:0},{finalTopDip:0}) edge={finalEdge}; next due {_nextAutonomyDueUtc:HH:mm:ss} UTC",
            selectedEdge: finalEdge);
    }

    private void StartHop(
        double startLeftDip,
        double startTopDip,
        double targetLeftDip,
        double targetTopDip,
        DockingEdge targetEdge,
        double targetOffsetX,
        double targetOffsetY,
        bool isUndockPopOut,
        string sourceReason)
    {
        double speed = Math.Clamp(Settings.MovementSpeedMultiplier, 0.5, 2.0);
        int anticipationMs = (int)Math.Round((isUndockPopOut ? 100 : 130) / speed);
        int flightMs = (int)Math.Round((isUndockPopOut ? 360 : 520) / speed);
        int landingMs = (int)Math.Round((isUndockPopOut ? 250 : 300) / speed);

        TransitionTo(MovementState.PreparingToHop, targetEdge);
        TransitionTo(MovementState.Hopping, targetEdge);

        var plan = new HopPlanEventArgs(
            startLeftDip,
            startTopDip,
            targetLeftDip,
            targetTopDip,
            targetEdge,
            targetOffsetX,
            targetOffsetY,
            anticipationMs,
            flightMs,
            landingMs,
            isUndockPopOut
        );

        EmitDiagnostic(
            AutonomyDiagnosticStage.MovementDispatched,
            $"Dispatched hop ({sourceReason}): ({startLeftDip:0},{startTopDip:0}) -> ({targetLeftDip:0},{targetTopDip:0}), edge={targetEdge}, duration={anticipationMs + flightMs + landingMs}ms",
            selectedEdge: targetEdge);

        HopRequested?.Invoke(this, plan);
    }

    private void ScheduleNextAutonomousHop(DateTimeOffset fromUtc, bool isInitial, string reason)
    {
        double minMinutes = Math.Max(0.05, Settings.MinAutonomyIntervalMinutes);
        double maxMinutes = Math.Max(minMinutes + 0.05, Settings.MaxAutonomyIntervalMinutes);
        double delayMinutes = minMinutes + _random.NextDouble() * (maxMinutes - minMinutes);
        if (!isInitial)
        {
            delayMinutes = Math.Max(delayMinutes, Settings.AutonomyCooldownMinutes);
        }
        _nextAutonomyDueUtc = fromUtc.AddMinutes(delayMinutes);

        EmitDiagnostic(
            AutonomyDiagnosticStage.NextMovementScheduled,
            $"Scheduled next autonomous hop in {delayMinutes * 60:0}s ({reason}, Due={_nextAutonomyDueUtc:HH:mm:ss} UTC)");
    }

    private void EmitDiagnostic(
        AutonomyDiagnosticStage stage,
        string summary,
        string? rejectionReason = null,
        DockingEdge selectedEdge = DockingEdge.None)
    {
        DiagnosticLogged?.Invoke(this, new AutonomyDiagnosticEventArgs(
            stage,
            summary,
            _nextAutonomyDueUtc,
            rejectionReason,
            selectedEdge,
            DateTimeOffset.UtcNow));
    }

    private void TransitionTo(MovementState nextState, DockingEdge edge)
    {
        var prev = CurrentState;
        CurrentState = nextState;
        CurrentEdge = edge;
        MovementStateChanged?.Invoke(this, new MovementStateChangedEventArgs(prev, nextState, edge, DateTimeOffset.UtcNow));
    }
}
