using Bloop.Core.Awareness;
using Bloop.Core.Behaviours;
using Bloop.Core.Movement;
using Bloop.Core.Personality;

namespace Bloop.Core.Notifications;

/// <summary>
/// Deterministic importance classifier and batching reaction scheduler (3-second window).
/// Decouples persistent notification reception from adaptive mascot visual reactions.
/// </summary>
public sealed class NotificationReactionScheduler : INotificationReactionScheduler
{
    private readonly List<NotificationItem> _pendingBatch = [];
    private DateTimeOffset? _batchOpenedAtUtc;
    private DateTimeOffset _lastReactionUtc = DateTimeOffset.MinValue;
    private int _deferredWhileHiddenCount;
    private NotificationImportance _highestDeferredImportance = NotificationImportance.Normal;
    private string? _lastDeferredAppDisplayName;
    private string? _lastDeferredTitle;
    private string? _lastDeferredBody;
    private byte[]? _lastDeferredLogoPngBytes;
    private DateTimeOffset? _lastDeferredTimestampUtc;
    private string? _lastDeferredNotificationId;

    public event EventHandler<NotificationReactionDecision>? ReactionDecided;

    public int PendingBatchCount => _pendingBatch.Count;
    public int DeferredWhileHiddenCount => _deferredWhileHiddenCount;

    public NotificationImportance GetPendingBatchHighestImportance()
    {
        if (_pendingBatch.Count == 0)
        {
            return NotificationImportance.Muted;
        }
        return _pendingBatch.Max(n => n.Importance);
    }

    public NotificationImportance ClassifyImportance(NotificationItem item, NotificationSettings settings)
    {
        // 1. User-configured application priority (by AppUserModelId or DisplayName)
        if (!string.IsNullOrWhiteSpace(item.Source.AppUserModelId) &&
            settings.AppPriorityRules.TryGetValue(item.Source.AppUserModelId.Trim(), out var aumidPriority))
        {
            return aumidPriority;
        }

        if (!string.IsNullOrWhiteSpace(item.Source.DisplayName) &&
            settings.AppPriorityRules.TryGetValue(item.Source.DisplayName.Trim(), out var namePriority))
        {
            return namePriority;
        }

        // 2. Trusted urgency metadata if present on the OS toast binding
        if (item.HasExplicitUrgencyHint)
        {
            return NotificationImportance.High;
        }

        // 3. Optional user-configured keyword rules (never inferred from arbitrary unconfigured wording)
        if (!string.IsNullOrWhiteSpace(settings.HighPriorityKeywordsCsv))
        {
            var keywords = settings.HighPriorityKeywordsCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var kw in keywords)
            {
                if (kw.Length >= 2 &&
                    (item.Title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                     item.Body.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                {
                    return NotificationImportance.High;
                }
            }
        }

        // 4. Normal importance fallback
        return NotificationImportance.Normal;
    }

    public void EnqueueNewNotifications(
        IReadOnlyList<NotificationItem> newlyArrived,
        DateTimeOffset nowUtc,
        NotificationSettings settings)
    {
        if (newlyArrived.Count == 0)
        {
            return;
        }

        foreach (var item in newlyArrived)
        {
            var classified = item with { Importance = ClassifyImportance(item, settings) };
            if (_pendingBatch.All(x => x.Id != classified.Id))
            {
                _pendingBatch.Add(classified);
            }
        }

        if (_batchOpenedAtUtc is null && _pendingBatch.Count > 0)
        {
            _batchOpenedAtUtc = nowUtc;
        }
    }

    public NotificationReactionDecision? EvaluatePendingBatch(
        DateTimeOffset nowUtc,
        NotificationSettings settings,
        BehaviourSettings behaviourSettings,
        BehaviourPriorityResolver priorityResolver,
        MovementState currentMovement,
        MascotState currentMood,
        int totalUnreadCount,
        bool forceFlush = false,
        AwarenessPolicy? awarenessPolicy = null)
    {
        if (_pendingBatch.Count == 0 || _batchOpenedAtUtc is null)
        {
            return null;
        }

        double batchWindowSec = Math.Clamp(settings.BatchWindowSeconds, 0.5, 15.0);
        if (!forceFlush && (nowUtc - _batchOpenedAtUtc.Value).TotalSeconds < batchWindowSec)
        {
            return null;
        }

        // Defer (rather than drop) batch while an active hop is mid-flight so the reaction plays cleanly after landing
        if (!forceFlush && currentMovement is MovementState.Hopping or MovementState.PreparingToHop or MovementState.Undocking)
        {
            return null;
        }

        var snapshot = _pendingBatch.ToList();
        _pendingBatch.Clear();
        _batchOpenedAtUtc = null;

        var eligible = snapshot.Where(n => n.Importance != NotificationImportance.Muted).ToList();
        if (eligible.Count == 0)
        {
            return Decide(new NotificationReactionDecision(
                false,
                NotificationReactionKind.None,
                NotificationImportance.Muted,
                snapshot.Count,
                totalUnreadCount,
                "AllMuted"));
        }

        var topItem = eligible.OrderByDescending(n => n.Importance).ThenByDescending(n => n.CreatedAtUtc).First();
        var highest = topItem.Importance;

        // Milestone 6: If mascot is hidden by FullScreen, SessionLock, or SystemSuspend,
        // suppress visual interruption and record count for a single non-intrusive catch-up reaction after restore.
        if (awarenessPolicy is { ShouldShowMascot: false })
        {
            _deferredWhileHiddenCount += eligible.Count;
            if (highest > _highestDeferredImportance)
            {
                _highestDeferredImportance = highest;
            }
            _lastDeferredAppDisplayName = topItem.Source.DisplayName;
            _lastDeferredTitle = topItem.Title;
            _lastDeferredBody = topItem.Body;
            _lastDeferredLogoPngBytes = topItem.Source.LogoPngBytes;
            _lastDeferredTimestampUtc = topItem.CreatedAtUtc;
            _lastDeferredNotificationId = topItem.Id;

            return Decide(new NotificationReactionDecision(
                false,
                NotificationReactionKind.None,
                highest,
                eligible.Count,
                totalUnreadCount,
                $"SuppressedWhileMascotHidden({awarenessPolicy.Reason})"));
        }

        if (!settings.ReactionsEnabled || !behaviourSettings.NotificationReactionsEnabled)
        {
            return Decide(new NotificationReactionDecision(
                false,
                NotificationReactionKind.None,
                highest,
                eligible.Count,
                totalUnreadCount,
                "ReactionsDisabled"));
        }

        if (behaviourSettings.GlobalBehavioursPaused)
        {
            return Decide(new NotificationReactionDecision(
                false,
                NotificationReactionKind.None,
                highest,
                eligible.Count,
                totalUnreadCount,
                "GlobalBehavioursPaused"));
        }

        // Milestone 6: In Focus Mode, suppress ordinary notifications while respecting High/Critical alerts
        if (awarenessPolicy is { EffectiveContext: DesktopContext.Focus } && highest < NotificationImportance.High)
        {
            return Decide(new NotificationReactionDecision(
                false,
                NotificationReactionKind.None,
                highest,
                eligible.Count,
                totalUnreadCount,
                $"SuppressedInFocusMode({awarenessPolicy.Reason})"));
        }

        if (!priorityResolver.CanExecute(BehaviourCategory.NotificationReaction, currentMovement, currentMood, out var rejectReason, awarenessPolicy))
        {
            return Decide(new NotificationReactionDecision(
                false,
                NotificationReactionKind.None,
                highest,
                eligible.Count,
                totalUnreadCount,
                rejectReason ?? "PreemptedByHigherPriority"));
        }

        // Sleep mode rule: ordinary notifications do not wake Bloop; High/Critical notifications wake Bloop
        if ((currentMood == MascotState.Sleepy || awarenessPolicy is { ShouldEnterSleepMode: true }) &&
            highest < NotificationImportance.High)
        {
            return Decide(new NotificationReactionDecision(
                false,
                NotificationReactionKind.None,
                highest,
                eligible.Count,
                totalUnreadCount,
                "SuppressedWhileSleeping(OrdinaryImportance)"));
        }

        double cooldownSec = Math.Clamp(settings.ReactionCooldownSeconds, 1.0, 120.0);
        if ((nowUtc - _lastReactionUtc).TotalSeconds < cooldownSec && highest < NotificationImportance.Critical)
        {
            return Decide(new NotificationReactionDecision(
                false,
                NotificationReactionKind.None,
                highest,
                eligible.Count,
                totalUnreadCount,
                "ReactionCooldownActive"));
        }

        _lastReactionUtc = nowUtc;

        bool allowEntrance = settings.HighImportanceCenterEntranceEnabled &&
                             (awarenessPolicy?.AllowCenterScreenEntrance ?? true);
        var reactionKind = (highest >= NotificationImportance.High && allowEntrance)
            ? NotificationReactionKind.HighImportanceEntrance
            : NotificationReactionKind.SubtleEdgeGlance;

        return Decide(new NotificationReactionDecision(
            true,
            reactionKind,
            highest,
            eligible.Count,
            totalUnreadCount,
            null,
            topItem.Source.DisplayName,
            topItem.Title,
            topItem.Body,
            topItem.Source.LogoPngBytes,
            topItem.CreatedAtUtc,
            topItem.Id));
    }

    public NotificationReactionDecision? TryConsumeCatchUpReaction(
        int totalUnreadCount,
        bool catchUpEnabled,
        DateTimeOffset nowUtc)
    {
        if (_deferredWhileHiddenCount <= 0)
        {
            return null;
        }

        int count = _deferredWhileHiddenCount;
        var highest = _highestDeferredImportance;
        var appName = _lastDeferredAppDisplayName;
        var title = _lastDeferredTitle;
        var body = _lastDeferredBody;
        var logo = _lastDeferredLogoPngBytes;
        var ts = _lastDeferredTimestampUtc;
        var notifId = _lastDeferredNotificationId;

        _deferredWhileHiddenCount = 0;
        _highestDeferredImportance = NotificationImportance.Normal;
        _lastDeferredAppDisplayName = null;
        _lastDeferredTitle = null;
        _lastDeferredBody = null;
        _lastDeferredLogoPngBytes = null;
        _lastDeferredTimestampUtc = null;
        _lastDeferredNotificationId = null;

        if (!catchUpEnabled || totalUnreadCount <= 0)
        {
            return null;
        }

        _lastReactionUtc = nowUtc;
        // Always use a single SubtleEdgeGlance for post-fullscreen catch-up so it is never intrusive
        return Decide(new NotificationReactionDecision(
            true,
            NotificationReactionKind.SubtleEdgeGlance,
            highest,
            count,
            totalUnreadCount,
            null,
            appName,
            title,
            body,
            logo,
            ts,
            notifId));
    }

    private NotificationReactionDecision Decide(NotificationReactionDecision decision)
    {
        ReactionDecided?.Invoke(this, decision);
        return decision;
    }
}

