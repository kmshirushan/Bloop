using Bloop.Core.Movement;

namespace Bloop.Core.Behaviours;

/// <summary>
/// Geometry of the user's current foreground application window (in DIPs),
/// obtained strictly from Win32 window rect APIs without screen capture or OCR.
/// </summary>
public readonly record struct ForegroundWindowGeometry(
    bool IsValid,
    double LeftDip,
    double TopDip,
    double RightDip,
    double BottomDip,
    bool IsMinimized,
    bool IsMaximized,
    bool IsFullscreen,
    string MonitorId
)
{
    public double WidthDip => Math.Max(0, RightDip - LeftDip);
    public double HeightDip => Math.Max(0, BottomDip - TopDip);
}

public sealed record ExplorationTarget(
    double TargetWindowLeftDip,
    double TargetWindowTopDip,
    DockingEdge DockEdge,
    string Description
);

/// <summary>
/// Evaluates whether Bloop can safely hop to perch on or peek beside the active foreground window's boundary.
/// Includes strict safeguards against minimized, maximized, fullscreen, tiny, or off-monitor windows.
/// </summary>
public sealed class ExplorationBehaviour
{
    private readonly Random _random;

    public ExplorationBehaviour(Random? random = null)
    {
        _random = random ?? new Random();
    }

    public bool TrySelectWindowBoundaryTarget(
        ForegroundWindowGeometry fg,
        WorkAreaBounds workArea,
        double mascotWidthDip,
        double mascotHeightDip,
        out ExplorationTarget? target,
        out string? rejectionReason)
    {
        target = null;

        if (!fg.IsValid)
        {
            rejectionReason = "NoValidForegroundWindow";
            return false;
        }

        if (fg.IsMinimized || fg.IsMaximized || fg.IsFullscreen)
        {
            rejectionReason = $"UnsuitableWindowState(Min={fg.IsMinimized},Max={fg.IsMaximized},Full={fg.IsFullscreen})";
            return false;
        }

        // Check fullscreen coverage against work area as an extra safeguard
        if (fg.WidthDip >= workArea.Width - 16 && fg.HeightDip >= workArea.Height - 16)
        {
            rejectionReason = "WindowCoversEntireWorkArea";
            return false;
        }

        if (fg.WidthDip < mascotWidthDip * 1.4 || fg.HeightDip < mascotHeightDip)
        {
            rejectionReason = "ForegroundWindowTooSmall";
            return false;
        }

        // Check if there is enough room above the foreground window's top title bar to perch
        double clearanceTop = fg.TopDip - workArea.Top;
        double minTopClearance = mascotHeightDip * 0.65;

        if (clearanceTop >= minTopClearance)
        {
            double minLeft = Math.Max(workArea.Left + 16, fg.LeftDip + 24);
            double maxLeft = Math.Min(workArea.Right - mascotWidthDip - 16, fg.RightDip - mascotWidthDip - 24);

            if (maxLeft >= minLeft)
            {
                double perchLeft = minLeft + _random.NextDouble() * (maxLeft - minLeft);
                // Perch right along the top border of the foreground window
                double perchTop = Math.Clamp(
                    fg.TopDip - (mascotHeightDip * 0.72),
                    workArea.Top,
                    workArea.Bottom - mascotHeightDip
                );

                target = new ExplorationTarget(
                    perchLeft,
                    perchTop,
                    DockingEdge.None,
                    $"Foreground window top-edge perch at ({perchLeft:0},{perchTop:0})"
                );
                rejectionReason = null;
                return true;
            }
        }

        rejectionReason = "InsufficientTopClearanceForPerch";
        return false;
    }

    /// <summary>
    /// Selects one of the 4 screen work-area corners to hop to and dock against.
    /// </summary>
    public ExplorationTarget SelectCornerTarget(
        WorkAreaBounds workArea,
        double mascotWidthDip,
        double mascotHeightDip,
        DockingEdge currentEdge)
    {
        double margin = 12.0;
        (double Left, double Top, DockingEdge Edge, string Name)[] corners =
        [
            (workArea.Left, workArea.Top + margin, DockingEdge.Left, "TopLeftCorner"),
            (Math.Max(workArea.Left, workArea.Right - mascotWidthDip), workArea.Top + margin, DockingEdge.Right, "TopRightCorner"),
            (workArea.Left, Math.Max(workArea.Top, workArea.Bottom - mascotHeightDip - margin), DockingEdge.Left, "BottomLeftCorner"),
            (Math.Max(workArea.Left, workArea.Right - mascotWidthDip), Math.Max(workArea.Top, workArea.Bottom - mascotHeightDip - margin), DockingEdge.Right, "BottomRightCorner")
        ];

        var filtered = corners.Where(c => c.Edge != currentEdge).ToArray();
        var pick = (filtered.Length > 0 ? filtered : corners)[_random.Next(filtered.Length > 0 ? filtered.Length : corners.Length)];
        return new ExplorationTarget(pick.Left, pick.Top, pick.Edge, pick.Name);
    }
}

