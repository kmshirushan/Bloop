namespace Bloop.Core.Movement;

/// <summary>
/// User-configurable movement, docking, and autonomous behaviour preferences.
/// </summary>
public sealed record MovementSettings
{
    public bool AutonomousMovementEnabled { get; init; } = true;
    public bool EdgeDockingEnabled { get; init; } = true;
    public double EdgeDetectionThresholdDip { get; init; } = 35.0;
    public double DockedVisibleRatio { get; init; } = 0.60; // Target ~60% visible body area when docked
    public double MovementSpeedMultiplier { get; init; } = 1.0;
    public double MinAutonomyIntervalMinutes { get; init; } = 3.0;
    public double MaxAutonomyIntervalMinutes { get; init; } = 8.0;
    public double AutonomyCooldownMinutes { get; init; } = 5.0;

    public static MovementSettings Default => new();
}

