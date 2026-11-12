using Bloop.Core.Events;
using Bloop.Core.Interfaces;

namespace Bloop.Core.Personality;

/// <summary>
/// State-driven animation & personality controller with priority management
/// and controlled idle variation.
/// </summary>
public sealed class PersonalityEngine : IPersonalityEngine
{
    private readonly Random _random = new();
    private DateTimeOffset _lastInteractionUtc = DateTimeOffset.UtcNow;

    public MascotState CurrentState { get; private set; } = MascotState.Idle;

    public event EventHandler<MascotStateChangedEventArgs>? StateChanged;

    public bool TryTransition(MascotState nextState, bool force = false)
    {
        if (!force && CurrentState == MascotState.Dragging && nextState != MascotState.Settling && nextState != MascotState.Idle)
        {
            return false;
        }

        if (nextState != MascotState.Idle && nextState != MascotState.Sleepy)
        {
            _lastInteractionUtc = DateTimeOffset.UtcNow;
        }

        var prev = CurrentState;
        CurrentState = nextState;
        StateChanged?.Invoke(this, new MascotStateChangedEventArgs(prev, nextState, DateTimeOffset.UtcNow));
        return true;
    }

    public MascotState EvaluateIdleVariation(TimeSpan idleDuration)
    {
        if (CurrentState == MascotState.Dragging || CurrentState == MascotState.Settling)
        {
            return CurrentState;
        }

        if (idleDuration > TimeSpan.FromMinutes(3))
        {
            TryTransition(MascotState.Sleepy, force: true);
            return CurrentState;
        }

        // Occasional curious glance when idle
        if (CurrentState == MascotState.Idle && _random.NextDouble() < 0.08)
        {
            TryTransition(MascotState.Curious, force: true);
            return CurrentState;
        }

        if (CurrentState != MascotState.Idle && (DateTimeOffset.UtcNow - _lastInteractionUtc) > TimeSpan.FromSeconds(2.5))
        {
            TryTransition(MascotState.Idle, force: true);
        }

        return CurrentState;
    }
}

