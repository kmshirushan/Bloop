using Bloop.Core.Awareness;
using Bloop.Core.Behaviours;
using Bloop.Core.Movement;
using Bloop.Core.Personality;

namespace Bloop.Core.Notifications;

public enum NotificationImportance
{
    Muted = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}

public enum NotificationPermissionStatus
{
    NotRequested = 0,
    Allowed = 1,
    Denied = 2,
    Revoked = 3,
    Unsupported = 4
}

public enum NotificationSyncTrigger
{
    Startup = 0,
    PlatformEvent = 1,
    PanelOpened = 2,
    SessionUnlocked = 3,
    SystemResumed = 4,
    UserInteraction = 5,
    AdaptiveFastPoll = 6,
    AdaptiveIdlePoll = 7,
    Manual = 8
}

public enum NotificationReactionKind
{
    None = 0,
    SubtleEdgeGlance = 1,
    HighImportanceEntrance = 2
}

public sealed record NotificationSource(
    string AppUserModelId,
    string DisplayName,
    byte[]? LogoPngBytes = null
);

public sealed record NotificationItem
{
    public required string Id { get; init; }
    public required uint OsNotificationId { get; init; }
    public required NotificationSource Source { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset ReceivedAtUtc { get; init; }
    public bool IsRead { get; init; }
    public bool IsDismissedLocally { get; init; }
    public bool IsPresentInOs { get; init; } = true;
    public NotificationImportance Importance { get; init; } = NotificationImportance.Normal;
    public bool HasExplicitUrgencyHint { get; init; }

    public static string BuildDeterministicId(string appUserModelId, uint osNotificationId, DateTimeOffset createdAtUtc)
    {
        var normalizedAumid = string.IsNullOrWhiteSpace(appUserModelId) ? "unknown" : appUserModelId.Trim().ToLowerInvariant();
        // Round creation time to seconds to prevent sub-millisecond clock jitter from duplicating records
        long epochSeconds = createdAtUtc.ToUnixTimeSeconds();
        return $"{normalizedAumid}:{osNotificationId}:{epochSeconds}";
    }
}

public sealed record NotificationSyncResult(
    NotificationSyncTrigger Trigger,
    IReadOnlyList<NotificationItem> AddedItems,
    IReadOnlyList<uint> RemovedFromOsIds,
    int TotalActiveInOs,
    int TotalVisibleInInbox,
    int UnreadCount,
    long DurationMs,
    DateTimeOffset TimestampUtc,
    bool IsInitialImport,
    string? ErrorMessage = null
);

public sealed record NotificationReactionDecision(
    bool ShouldReact,
    NotificationReactionKind ReactionKind,
    NotificationImportance HighestImportance,
    int BatchCount,
    int UnreadCount,
    string? SuppressionReason = null,
    string? PreviewAppDisplayName = null,
    string? PreviewTitle = null,
    string? PreviewBody = null,
    byte[]? PreviewLogoPngBytes = null,
    DateTimeOffset? PreviewTimestampUtc = null,
    string? PreviewNotificationId = null
);

public sealed record NotificationSettings
{
    public bool IntegrationEnabled { get; init; } = false; // Opt-in by default per privacy requirement
    public bool ConsentExplained { get; init; } = false;
    public bool StoreLocalHistoryEnabled { get; init; } = true;
    public int RetentionDays { get; init; } = 7;
    public int MaxStoredNotifications { get; init; } = 200;
    public bool ReactionsEnabled { get; init; } = true;
    public bool HighImportanceCenterEntranceEnabled { get; init; } = true;
    public double BatchWindowSeconds { get; init; } = 3.0;
    public double ReactionCooldownSeconds { get; init; } = 8.0;
    public double FastPollIntervalSeconds { get; init; } = 2.5;
    public double FastPollWindowSeconds { get; init; } = 15.0;
    public double IdlePollIntervalSeconds { get; init; } = 12.0;
    public string HighPriorityKeywordsCsv { get; init; } = "";
    public IReadOnlyDictionary<string, NotificationImportance> AppPriorityRules { get; init; } =
        new Dictionary<string, NotificationImportance>(StringComparer.OrdinalIgnoreCase);

    public static NotificationSettings Default => new();
}

public sealed record NotificationPanelPlacement(
    bool IsDetached = false,
    double DetachedLeftDip = 120.0,
    double DetachedTopDip = 120.0,
    double DetachedWidthDip = 380.0,
    double DetachedHeightDip = 500.0,
    string MonitorId = "primary"
)
{
    public static NotificationPanelPlacement Default => new();
}

public interface INotificationChangeSignal : IDisposable
{
    event EventHandler<NotificationSyncTrigger>? ChangeSignaled;
    void Start();
    void Stop();
    void NotifyActivityBoost(NotificationSyncTrigger reason);
    void ConfigureIntervals(TimeSpan fastInterval, TimeSpan fastWindow, TimeSpan idleInterval, bool active);
}

public interface INotificationListener : IDisposable
{
    bool IsSupported { get; }
    bool IsPlatformChangeEventSupported { get; }
    NotificationPermissionStatus GetPermissionStatus();
    Task<NotificationPermissionStatus> RequestPermissionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NotificationItem>> GetCurrentNotificationsAsync(CancellationToken ct = default);
    bool TryRemoveOsNotification(uint osNotificationId);
}

public interface INotificationRepository
{
    NotificationSyncResult SynchronizeSnapshot(
        IReadOnlyList<NotificationItem> osSnapshot,
        NotificationSyncTrigger trigger,
        bool isInitialStartupImport,
        NotificationSettings settings,
        Func<NotificationItem, NotificationImportance> importanceClassifier,
        long durationMs = 0);

    IReadOnlyList<NotificationItem> GetInboxNotifications(bool includeLocallyDismissed = false);
    int GetUnreadCount();
    void MarkRead(string id, bool isRead = true);
    void MarkAllRead();
    void DismissLocally(string id);
    void DismissAppGroupLocally(string appUserModelId);
    void MarkRemovedFromOs(uint osNotificationId);
    void ClearLocalHistory();

    NotificationSettings LoadNotificationSettings();
    void SaveNotificationSettings(NotificationSettings settings);
    NotificationPanelPlacement LoadPanelPlacement();
    void SavePanelPlacement(NotificationPanelPlacement placement);
}

public interface INotificationReactionScheduler
{
    event EventHandler<NotificationReactionDecision>? ReactionDecided;
    int PendingBatchCount { get; }
    NotificationImportance GetPendingBatchHighestImportance();
    NotificationImportance ClassifyImportance(NotificationItem item, NotificationSettings settings);
    void EnqueueNewNotifications(IReadOnlyList<NotificationItem> newlyArrived, DateTimeOffset nowUtc, NotificationSettings settings);
    NotificationReactionDecision? EvaluatePendingBatch(
        DateTimeOffset nowUtc,
        NotificationSettings settings,
        BehaviourSettings behaviourSettings,
        BehaviourPriorityResolver priorityResolver,
        MovementState currentMovement,
        MascotState currentMood,
        int totalUnreadCount,
        bool forceFlush = false,
        AwarenessPolicy? awarenessPolicy = null);

    int DeferredWhileHiddenCount { get; }
    NotificationReactionDecision? TryConsumeCatchUpReaction(int totalUnreadCount, bool catchUpEnabled, DateTimeOffset nowUtc);
}

