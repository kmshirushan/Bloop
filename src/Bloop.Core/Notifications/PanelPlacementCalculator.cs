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

    /// <summary>
    /// Computes the DPI-aware placement for the floating Liquid Glass Toast Window
    /// relative to Bloop, avoiding overlap with the mascot or monitor edges.
    /// </summary>
    public static (double LeftDip, double TopDip, double EntranceOffsetX) CalculateToastBubblePosition(
        double mascotLeftDip,
        double mascotTopDip,
        double mascotWidthDip,
        double mascotHeightDip,
        double toastWidthDip,
        double toastHeightDip,
        WorkAreaBounds workArea)
    {
        double mascotCenterX = mascotLeftDip + mascotWidthDip * 0.5;
        double workCenterX = (workArea.Left + workArea.Right) * 0.5;

        const double gap = 10.0;
        double candidateLeft = mascotLeftDip - toastWidthDip - gap;
        double candidateRight = mascotLeftDip + mascotWidthDip + gap;

        double chosenLeft;
        double entranceOffsetX;
        if (mascotCenterX >= workCenterX)
        {
            if (candidateLeft >= workArea.Left + 8.0)
            {
                chosenLeft = candidateLeft;
                entranceOffsetX = 14.0; // Slides gently leftwards away from Bloop
            }
            else
            {
                chosenLeft = Math.Min(candidateRight, workArea.Right - toastWidthDip - 8.0);
                entranceOffsetX = -14.0;
            }
        }
        else
        {
            if (candidateRight + toastWidthDip <= workArea.Right - 8.0)
            {
                chosenLeft = candidateRight;
                entranceOffsetX = -14.0; // Slides gently rightwards away from Bloop
            }
            else
            {
                chosenLeft = Math.Max(candidateLeft, workArea.Left + 8.0);
                entranceOffsetX = 14.0;
            }
        }

        // Position slightly towards the upper half of Bloop like an organic floating speech/thought bubble
        double idealTop = mascotTopDip + (mascotHeightDip * 0.18) - (toastHeightDip * 0.25);
        double minTop = workArea.Top + 10.0;
        double maxTop = Math.Max(minTop, workArea.Bottom - toastHeightDip - 10.0);
        double chosenTop = Math.Clamp(idealTop, minTop, maxTop);

        return (Math.Round(chosenLeft), Math.Round(chosenTop), entranceOffsetX);
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
