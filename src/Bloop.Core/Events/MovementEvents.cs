using Bloop.Core.Movement;

namespace Bloop.Core.Events;

public sealed record MovementStateChangedEventArgs(
    MovementState PreviousState,
    MovementState CurrentState,
    DockingEdge Edge,
    DateTimeOffset Timestamp
);

public sealed record HopPlanEventArgs(
    double StartWindowLeftDip,
    double StartWindowTopDip,
    double TargetWindowLeftDip,
    double TargetWindowTopDip,
    DockingEdge TargetEdge,
    double TargetViewportOffsetX,
    double TargetViewportOffsetY,
    int AnticipationMs,
    int FlightMs,
    int LandingMs,
    bool IsUndockPopOut
);

