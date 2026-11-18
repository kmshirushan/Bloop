namespace Bloop.Core.Behaviours;

/// <summary>
/// Independent configuration for all optional Milestone 3 autonomous and interactive behaviours.
/// Disabling any individual behaviour never disables unrelated behaviours.
/// </summary>
public sealed record BehaviourSettings
{
    public bool GlobalBehavioursPaused { get; init; } = false;
    public bool AutonomousEdgeHoppingEnabled { get; init; } = true;
    public bool WindowBoundaryExplorationEnabled { get; init; } = true;
    public bool CornerExplorationEnabled { get; init; } = true;
    public bool EdgeBalancingEnabled { get; init; } = true;
    public bool PeekabooEnabled { get; init; } = true;
    public bool CursorChasingEnabled { get; init; } = true;
    public bool AttentionSeekingEnabled { get; init; } = true;
    public bool SleepModeEnabled { get; init; } = true;
    public bool WelcomeBackReactionEnabled { get; init; } = true;
    public bool RandomClickReactionsEnabled { get; init; } = true;
    public bool RepeatedClickAnnoyanceEnabled { get; init; } = true;
    public bool NotificationReactionsEnabled { get; init; } = true;

    public int AnnoyanceMildThreshold { get; init; } = 3;
    public int AnnoyancePeakThreshold { get; init; } = 5;
    public double AnnoyanceWindowSeconds { get; init; } = 3.5;
    public double AnnoyanceRecoverySeconds { get; init; } = 4.0;
    public double PeekabooCooldownMinutes { get; init; } = 2.0;
    public double AttentionSeekMinutes { get; init; } = 1.5;
    public double SleepInactivityMinutes { get; init; } = 3.0;
    public double CursorChaseCooldownSeconds { get; init; } = 35.0;

    public static BehaviourSettings Default => new();
}

