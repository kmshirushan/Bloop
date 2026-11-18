using Bloop.Core.Personality;

namespace Bloop.Core.Behaviours;

public sealed record NotificationAlertEvent(
    string SourceApp,
    string Title,
    DateTimeOffset Timestamp
);

/// <summary>
/// Decouples notification reception/inbox storage from optional mascot visual reactions.
/// Center-screen interruptions are deferred to Milestone 4; in Milestone 3 this provides
/// the behaviour event interface and non-intrusive reaction state resolution.
/// </summary>
public sealed class NotificationBehaviour
{
    private readonly List<NotificationAlertEvent> _inbox = [];

    public IReadOnlyList<NotificationAlertEvent> Inbox => _inbox;

    public event EventHandler<NotificationAlertEvent>? NotificationReceived;
    public event EventHandler<MascotState>? NotificationReactionTriggered;

    /// <summary>
    /// Records an incoming notification in the inbox regardless of whether visual reactions are enabled,
    /// and triggers a mascot reaction only when NotificationReactionsEnabled is true and priority allows.
    /// </summary>
    public bool OnNotificationArrived(
        NotificationAlertEvent notification,
        BehaviourSettings settings,
        BehaviourPriorityResolver priorityResolver,
        Movement.MovementState currentMovement,
        MascotState currentMood)
    {
        _inbox.Add(notification);
        NotificationReceived?.Invoke(this, notification);

        if (!settings.NotificationReactionsEnabled)
        {
            return false;
        }

        if (!priorityResolver.CanExecute(
                BehaviourCategory.NotificationReaction,
                currentMovement,
                currentMood,
                out _))
        {
            return false;
        }

        NotificationReactionTriggered?.Invoke(this, MascotState.Notification);
        return true;
    }
}

