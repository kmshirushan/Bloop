namespace Bloop.Core.Personality;

/// <summary>
/// Supported mascot states in priority order.
/// Higher numerical values represent higher priority states.
/// </summary>
public enum MascotState
{
    Idle = 0,
    Sleepy = 10,
    Hover = 20,
    Curious = 30,
    Happy = 40,
    Surprised = 50,
    Annoyed = 55,
    Notification = 60,
    Settling = 70,
    Dragging = 80
}

