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
    private bool _isControlPanelActive;

    public MovementState CurrentState { get; private set; } = MovementState.Free;
    public DockingEdge CurrentEdge { get; private set; } = DockingEdge.None;
    public MovementSettings Settings { get; private set; } = MovementSettings.Default;

    public double CurrentWindowLeftDip { get; private set; }
    public double CurrentWindowTopDip { get; private set; }
    public string CurrentMonitorId { get; private set; } = "primary";

    public event EventHandler<MovementStateChangedEventArgs>? MovementStateChanged;
    public event EventHandler<DockingEdge>? EdgeCandidateChanged;
    public event EventHandler<HopPlanEventArgs>? HopRequested;

    public MovementController(Random? random = null)
    {
        _random = random ?? new Random();
        ScheduleNextAutonomousHop(DateTimeOffset.UtcNow, isInitial: true);
    }

    public void UpdateSettings(MovementSettings settings)
    {
        Settings = settings;
        ScheduleNextAutonomousHop(DateTimeOffset.UtcNow, isInitial: true);
    }

    public void SetControlPanelActive(bool isActive)
    {
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

    public bool BeginDrag(double windowLeftDip, double windowTopDip)
    {
        CurrentWindowLeftDip = windowLeftDip;
        CurrentWindowTopDip = windowTopDip;
        CurrentEdge = DockingEdge.None;
        TransitionTo(MovementState.Dragging, DockingEdge.None);
        EdgeCandidateChanged?.Invoke(this, DockingEdge.None);
        return true;
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

    /// <summary>
    /// Computes the anchored window coordinates within the monitor work area and the 3D viewport offset
    /// so that approximately Settings.DockedVisibleRatio (~60%) of Bloop's body remains visible.
    /// </summary>
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

    /// <summary>
    /// Maps the desired visible body ratio (e.g. 0.60 for 60% visible) to Three.js world-space root offset.
    /// At camera Z=4.7, FOV=34 deg, the half-frustum extent at Z=0 is ~1.438 world units, and sphere radius is 1.0.
    /// When the sphere center is shifted to +/-1.24 world units, ~40% of the diameter is clipped outside the frustum,
    /// leaving ~60% of the mascot's projected body area cleanly visible against the window border.
    /// </summary>
    public static (double OffsetX, double OffsetY) CalculateViewportOffset(DockingEdge edge, double visibleRatio)
    {
        if (edge == DockingEdge.None) return (0, 0);

        double clampedRatio = Math.Clamp(visibleRatio, 0.35, 0.90);
        // Frustum half-width at z=0 is ~1.438, sphere radius = 1.0.
        // Center shift so that (visibleDiameter = 2.0 * clampedRatio) remains inside [-1.438, +1.438]:
        // edgeOfFrustum = 1.438; innerEdgeOfSphere = center - 1.0 => visibleWidth = 1.438 - (center - 1.0) = 2.438 - center
        // Solve 2.438 - center = 2.0 * clampedRatio => center = 2.438 - 2.0 * clampedRatio.
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
                isUndockPopOut: false);
            return;
        }

        // Clamp within work area and settle freely
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
        ScheduleNextAutonomousHop(_lastMovementCompletedUtc, isInitial: false);
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
            isUndockPopOut: true);
        return true;
    }

    public bool TryTriggerAutonomousHop(
        DateTimeOffset nowUtc,
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea)
    {
        if (!CanRunAutonomousMovement(nowUtc))
        {
            return false;
        }

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

        StartHop(
            CurrentWindowLeftDip,
            CurrentWindowTopDip,
            dockPos.WindowLeftDip,
            dockPos.WindowTopDip,
            nextEdge,
            dockPos.ViewportOffsetX,
            dockPos.ViewportOffsetY,
            isUndockPopOut: false);

        return true;
    }

    public bool CanRunAutonomousMovement(DateTimeOffset nowUtc)
    {
        if (!Settings.AutonomousMovementEnabled || !Settings.EdgeDockingEnabled)
            return false;

        if (_isControlPanelActive)
            return false;

        if (CurrentState is MovementState.Dragging or MovementState.PreparingToHop or MovementState.Hopping or MovementState.Undocking or MovementState.Paused)
            return false;

        if (_lastMovementCompletedUtc != DateTimeOffset.MinValue &&
            (nowUtc - _lastMovementCompletedUtc) < TimeSpan.FromMinutes(Settings.AutonomyCooldownMinutes))
        {
            return false;
        }

        return nowUtc >= _nextAutonomyDueUtc;
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

        // Filter out the current edge so Bloop prefers a different edge
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
        ScheduleNextAutonomousHop(_lastMovementCompletedUtc, isInitial: false);
        TransitionTo(finalEdge == DockingEdge.None ? MovementState.Free : MovementState.Docked, finalEdge);
    }

    private void StartHop(
        double startLeftDip,
        double startTopDip,
        double targetLeftDip,
        double targetTopDip,
        DockingEdge targetEdge,
        double targetOffsetX,
        double targetOffsetY,
        bool isUndockPopOut)
    {
        double speed = Math.Clamp(Settings.MovementSpeedMultiplier, 0.5, 2.0);
        int anticipationMs = (int)Math.Round((isUndockPopOut ? 100 : 130) / speed);
        int flightMs = (int)Math.Round((isUndockPopOut ? 360 : 520) / speed);
        int landingMs = (int)Math.Round((isUndockPopOut ? 250 : 300) / speed);

        TransitionTo(MovementState.PreparingToHop, targetEdge);
        TransitionTo(MovementState.Hopping, targetEdge);

        HopRequested?.Invoke(this, new HopPlanEventArgs(
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
        ));
    }

    private void ScheduleNextAutonomousHop(DateTimeOffset fromUtc, bool isInitial)
    {
        double minMinutes = Math.Max(0.5, Settings.MinAutonomyIntervalMinutes);
        double maxMinutes = Math.Max(minMinutes + 0.5, Settings.MaxAutonomyIntervalMinutes);
        double delayMinutes = minMinutes + _random.NextDouble() * (maxMinutes - minMinutes);
        if (!isInitial)
        {
            delayMinutes = Math.Max(delayMinutes, Settings.AutonomyCooldownMinutes);
        }
        _nextAutonomyDueUtc = fromUtc.AddMinutes(delayMinutes);
    }

    private void TransitionTo(MovementState nextState, DockingEdge edge)
    {
        var prev = CurrentState;
        CurrentState = nextState;
        CurrentEdge = edge;
        MovementStateChanged?.Invoke(this, new MovementStateChangedEventArgs(prev, nextState, edge, DateTimeOffset.UtcNow));
    }
}

