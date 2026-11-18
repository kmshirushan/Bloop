using Bloop.Core.Movement;

namespace Bloop.Core.Notifications;

/// <summary>
/// Pure geometry calculator for Attached (beside Bloop without overlapping) and Detached
/// (clamped/recovered within visible monitor work area) notification panel modes.
/// </summary>
public static class PanelPlacementCalculator
{
    private const double GapDip = 12.0;

    public static (double LeftDip, double TopDip) CalculateAttachedPosition(
        double mascotLeftDip,
        double mascotTopDip,
        double mascotWidthDip,
        double mascotHeightDip,
        double panelWidthDip,
        double panelHeightDip,
        WorkAreaBounds workArea)
    {
        double mascotCenterX = mascotLeftDip + mascotWidthDip * 0.5;
        double workCenterX = (workArea.Left + workArea.Right) * 0.5;

        double candidateLeftLeft = mascotLeftDip - panelWidthDip - GapDip;
        double candidateRightLeft = mascotLeftDip + mascotWidthDip + GapDip;

        double chosenLeft;
        if (mascotCenterX >= workCenterX)
        {
            chosenLeft = candidateLeftLeft >= workArea.Left + 4.0
                ? candidateLeftLeft
                : Math.Min(candidateRightLeft, workArea.Right - panelWidthDip - 4.0);
        }
        else
        {
            chosenLeft = candidateRightLeft + panelWidthDip <= workArea.Right - 4.0
                ? candidateRightLeft
                : Math.Max(candidateLeftLeft, workArea.Left + 4.0);
        }

        double idealTop = mascotTopDip + (mascotHeightDip - panelHeightDip) * 0.5;
        double minTop = workArea.Top + 8.0;
        double maxTop = Math.Max(minTop, workArea.Bottom - panelHeightDip - 8.0);
        double chosenTop = Math.Clamp(idealTop, minTop, maxTop);

        return (Math.Round(chosenLeft), Math.Round(chosenTop));
    }

    public static NotificationPanelPlacement RecoverDetachedPlacement(
        NotificationPanelPlacement saved,
        WorkAreaBounds targetWorkArea)
    {
        double maxW = Math.Max(320.0, targetWorkArea.Width - 24.0);
        double maxH = Math.Max(360.0, targetWorkArea.Height - 24.0);

        double width = Math.Clamp(saved.DetachedWidthDip, 320.0, Math.Min(800.0, maxW));
        double height = Math.Clamp(saved.DetachedHeightDip, 360.0, Math.Min(900.0, maxH));

        double minLeft = targetWorkArea.Left + 8.0;
        double maxLeft = Math.Max(minLeft, targetWorkArea.Right - width - 8.0);
        double minTop = targetWorkArea.Top + 8.0;
        double maxTop = Math.Max(minTop, targetWorkArea.Bottom - height - 8.0);

        double left = Math.Clamp(saved.DetachedLeftDip, minLeft, maxLeft);
        double top = Math.Clamp(saved.DetachedTopDip, minTop, maxTop);

        return saved with
        {
            DetachedLeftDip = Math.Round(left),
            DetachedTopDip = Math.Round(top),
            DetachedWidthDip = Math.Round(width),
            DetachedHeightDip = Math.Round(height),
            MonitorId = targetWorkArea.MonitorId
        };
    }
}
