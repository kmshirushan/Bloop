using Bloop.Core.Awareness;
using Bloop.Core.Movement;
using Bloop.Core.Personality;

namespace Bloop.Core.Behaviours;

public enum BehaviourCategory
{
    Idle = 10,
    AutonomousMicro = 30,       // Peekaboo, idle glance
    AutonomousMovement = 40,    // Edge hop, window-boundary perch
    NotificationReaction = 60,  // Notification alert reaction
    UserInteraction = 80,       // Click / repeated-click annoyance
    UserDrag = 100              // Manual drag / manual undock
}

/// <summary>
/// Enforces priority rules so user interactions always preempt autonomous behaviours
/// and expression reactions never cancel active physical movement unless user-initiated.
/// </summary>
public sealed class BehaviourPriorityResolver
{
    public bool CanExecute(
        BehaviourCategory requested,
        MovementState currentMovement,
        MascotState currentMood,
        out string? rejectionReason,
        AwarenessPolicy? awarenessPolicy = null)
    {
        if (awarenessPolicy is not null)
        {
            if (!awarenessPolicy.ShouldShowMascot && requested < BehaviourCategory.UserDrag)
            {
                rejectionReason = $"MascotHiddenByAwareness({awarenessPolicy.Reason})";
                return false;
            }

            if (requested == BehaviourCategory.AutonomousMovement && !awarenessPolicy.AllowAutonomousMovement)
            {
                rejectionReason = $"AutonomousMovementSuppressed({awarenessPolicy.Reason})";
                return false;
            }

            if (requested == BehaviourCategory.AutonomousMicro &&
                !awarenessPolicy.AllowPeekaboo &&
                !awarenessPolicy.AllowAttentionSeeking)
            {
                rejectionReason = $"MicroBehaviourSuppressed({awarenessPolicy.Reason})";
                return false;
            }

            if (requested == BehaviourCategory.NotificationReaction && !awarenessPolicy.AllowNotificationReaction)
            {
                rejectionReason = $"NotificationReactionSuppressed({awarenessPolicy.Reason})";
                return false;
            }
        }

        if (currentMovement == MovementState.Paused && requested < BehaviourCategory.UserInteraction)
        {
            rejectionReason = "MovementPaused";
            return false;
        }

        if (currentMovement == MovementState.Dragging && requested < BehaviourCategory.UserDrag)
        {
            rejectionReason = "PreemptedByUserDragging";
            return false;
        }

        if (currentMovement is MovementState.Hopping or MovementState.PreparingToHop or MovementState.Undocking)
        {
            if (requested is BehaviourCategory.AutonomousMovement or BehaviourCategory.AutonomousMicro or BehaviourCategory.NotificationReaction)
            {
                rejectionReason = $"PreemptedByActiveHop({currentMovement})";
                return false;
            }
        }

        rejectionReason = null;
        return true;
    }
}

