namespace Bloop.Core.Movement;

/// <summary>
/// Physical movement states of Bloop, completely independent of mood/expression (MascotState).
/// </summary>
public enum MovementState
{
    Free = 0,
    Dragging = 10,
    PreparingToHop = 20,
    Hopping = 30,
    Docked = 40,
    Undocking = 50,
    Paused = 60
}

