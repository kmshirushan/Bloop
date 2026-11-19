using Bloop.Core.Awareness;
using Bloop.Core.Behaviours;
using Bloop.Core.Events;
using Bloop.Core.Movement;
using Bloop.Core.Personality;
using Bloop.Core.Productivity;

namespace Bloop.Core.Interfaces;

public interface IPersonalityEngine
{
    MascotState CurrentState { get; }
    event EventHandler<MascotStateChangedEventArgs>? StateChanged;
    bool TryTransition(MascotState nextState, bool force = false);
    MascotState EvaluateIdleVariation(TimeSpan idleDuration);
}

public interface IMovementController
{
    MovementState CurrentState { get; }
    DockingEdge CurrentEdge { get; }
    MovementSettings Settings { get; }
    DateTimeOffset NextAutonomyDueUtc { get; }
    double CurrentWindowLeftDip { get; }
    double CurrentWindowTopDip { get; }
    string CurrentMonitorId { get; }

    event EventHandler<MovementStateChangedEventArgs>? MovementStateChanged;
    event EventHandler<DockingEdge>? EdgeCandidateChanged;
    event EventHandler<HopPlanEventArgs>? HopRequested;
    event EventHandler<AutonomyDiagnosticEventArgs>? DiagnosticLogged;

    void UpdateSettings(MovementSettings settings);
    void SetControlPanelActive(bool isActive);
    void SyncPosition(double windowLeftDip, double windowTopDip, string monitorId, DockingEdge edge = DockingEdge.None);
    bool BeginDrag(double windowLeftDip, double windowTopDip);
    DockingEdge EvaluateDragProximity(double windowLeftDip, double windowTopDip, double windowWidthDip, double windowHeightDip, WorkAreaBounds workArea);
    DockingPosition CalculateDockingPosition(DockingEdge edge, double desiredLeftDip, double desiredTopDip, double windowWidthDip, double windowHeightDip, WorkAreaBounds workArea);
    void CompleteDrag(double releaseLeftDip, double releaseTopDip, double windowWidthDip, double windowHeightDip, WorkAreaBounds workArea);
    bool RequestUndockPopOut(double windowWidthDip, double windowHeightDip, WorkAreaBounds workArea);
    bool TryTriggerAutonomousHop(DateTimeOffset nowUtc, double windowWidthDip, double windowHeightDip, WorkAreaBounds workArea);
    bool TriggerManualAutonomousHop(double windowWidthDip, double windowHeightDip, WorkAreaBounds workArea);
    bool TryHopToCoordinate(double targetLeftDip, double targetTopDip, DockingEdge targetEdge, WorkAreaBounds workArea, string reason);
    bool CanRunAutonomousMovement(DateTimeOffset nowUtc, out string? rejectionReason);
    bool CanRunAutonomousMovement(DateTimeOffset nowUtc);
    DockingEdge SelectNextAutonomousEdge(DockingEdge currentEdge);
    void CompleteHop(double finalLeftDip, double finalTopDip, DockingEdge finalEdge);
    void CancelActiveMovement(string reason);
    void SetPaused(bool paused);
    AwarenessPolicy CurrentAwarenessPolicy { get; }
    void ApplyAwarenessPolicy(AwarenessPolicy policy, DateTimeOffset nowUtc, TimeSpan? postRestoreSettling = null);
}

public sealed record PersistedMascotPlacement(
    double Left,
    double Top,
    bool HasSavedPosition,
    DockingEdge DockedEdge,
    string MonitorId,
    bool AlwaysOnTop
);

public interface ISettingsRepository
{
    (double Left, double Top, bool HasSavedPosition) LoadWindowPosition();
    void SaveWindowPosition(double left, double top);
    PersistedMascotPlacement LoadMascotPlacement();
    void SaveMascotPlacement(double left, double top, DockingEdge edge, string monitorId, bool alwaysOnTop = true);
    MovementSettings LoadMovementSettings();
    void SaveMovementSettings(MovementSettings settings);
    BehaviourSettings LoadBehaviourSettings();
    void SaveBehaviourSettings(BehaviourSettings settings);
    AwarenessSettings LoadAwarenessSettings();
    void SaveAwarenessSettings(AwarenessSettings settings);
    IReadOnlyList<LauncherEntry> LoadRecentLauncherEntries();
    void SaveRecentLauncherEntries(IReadOnlyList<LauncherEntry> entries);
}

