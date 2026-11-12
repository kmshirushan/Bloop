namespace Bloop.Core.Movement;

/// <summary>
/// Represents a monitor work area in logical (DIP) coordinates along with its device identity.
/// </summary>
public readonly record struct WorkAreaBounds(
    double Left,
    double Top,
    double Right,
    double Bottom,
    string MonitorId = "primary"
)
{
    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);
}

/// <summary>
/// Computed docking target including the native window anchor (always inside the monitor work area)
/// and the normalized viewport offset for the 3D character so it clips naturally at the window border.
/// </summary>
public sealed record DockingPosition(
    DockingEdge Edge,
    double WindowLeftDip,
    double WindowTopDip,
    double ViewportOffsetX,
    double ViewportOffsetY,
    string MonitorId
);

