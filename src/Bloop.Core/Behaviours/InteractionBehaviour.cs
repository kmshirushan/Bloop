using Bloop.Core.Awareness;
using Bloop.Core.Personality;

namespace Bloop.Core.Behaviours;

public enum AnnoyanceStage
{
    Calm = 0,
    Mild = 1,
    Annoyed = 2
}

public sealed record ClickReactionResult(
    MascotState ReactionState,
    AnnoyanceStage AnnoyanceStage,
    int RecentClickCount,
    string Description
);

/// <summary>
/// Manages single-click weighted reactions with recent-history deduplication
/// and progressive repeated-click annoyance with time-window decay and recovery.
/// Double-clicks are explicitly excluded so opening the Companion Panel never triggers annoyance.
/// </summary>
public sealed class InteractionBehaviour
{
    private readonly Random _random;
    private readonly Queue<DateTimeOffset> _recentSingleClicks = new();
    private readonly Queue<MascotState> _recentReactions = new();
    private DateTimeOffset _lastAnnoyedAtUtc = DateTimeOffset.MinValue;

    public AnnoyanceStage CurrentAnnoyanceStage { get; private set; } = AnnoyanceStage.Calm;

    public InteractionBehaviour(Random? random = null)
    {
        _random = random ?? new Random();
    }

    /// <summary>
    /// Clears recent click burst when a double-click occurs so opening the control panel
    /// never contributes toward repeated-click annoyance.
    /// </summary>
    public void NotifyDoubleClick()
    {
        _recentSingleClicks.Clear();
    }

    public ClickReactionResult EvaluateSingleClick(
        DateTimeOffset nowUtc,
        BehaviourSettings settings,
        AwarenessPolicy? awarenessPolicy = null)
    {
        double windowSec = Math.Max(1.0, settings.AnnoyanceWindowSeconds);
        while (_recentSingleClicks.Count > 0 && (nowUtc - _recentSingleClicks.Peek()).TotalSeconds > windowSec)
        {
            _recentSingleClicks.Dequeue();
        }

        // Check recovery from previous annoyance
        if (CurrentAnnoyanceStage != AnnoyanceStage.Calm &&
            (nowUtc - _lastAnnoyedAtUtc).TotalSeconds >= Math.Max(1.5, settings.AnnoyanceRecoverySeconds))
        {
            CurrentAnnoyanceStage = AnnoyanceStage.Calm;
        }

        _recentSingleClicks.Enqueue(nowUtc);
        int clickCount = _recentSingleClicks.Count;

        bool allowAnnoyance = settings.RepeatedClickAnnoyanceEnabled &&
                              (awarenessPolicy?.AllowRepeatedClickAnnoyance ?? true);

        // 1. Progressive Repeated-Click Annoyance
        if (allowAnnoyance)
        {
            int peakThreshold = Math.Max(3, settings.AnnoyancePeakThreshold);
            int mildThreshold = Math.Clamp(settings.AnnoyanceMildThreshold, 2, peakThreshold - 1);

            if (clickCount >= peakThreshold)
            {
                CurrentAnnoyanceStage = AnnoyanceStage.Annoyed;
                _lastAnnoyedAtUtc = nowUtc;
                return new ClickReactionResult(
                    MascotState.Annoyed,
                    AnnoyanceStage.Annoyed,
                    clickCount,
                    $"Peak annoyance ({clickCount} clicks in {windowSec:0.0}s)"
                );
            }

            if (clickCount >= mildThreshold)
            {
                CurrentAnnoyanceStage = AnnoyanceStage.Mild;
                _lastAnnoyedAtUtc = nowUtc;
                return new ClickReactionResult(
                    MascotState.Surprised,
                    AnnoyanceStage.Mild,
                    clickCount,
                    $"Mild annoyance startle ({clickCount} clicks in {windowSec:0.0}s)"
                );
            }
        }

        // 2. Weighted Random Click Reaction with Recent-Reaction History Deduplication
        bool allowRandomClick = settings.RandomClickReactionsEnabled &&
                                (awarenessPolicy?.AllowRandomClickReactions ?? true);
        if (!allowRandomClick)
        {
            return new ClickReactionResult(
                MascotState.Happy,
                AnnoyanceStage.Calm,
                clickCount,
                "Standard happy bounce (random click reactions disabled)"
            );
        }

        var chosen = SelectWeightedReactionAvoidingRecent();
        RecordReaction(chosen);

        return new ClickReactionResult(
            chosen,
            AnnoyanceStage.Calm,
            clickCount,
            $"Weighted reaction: {chosen}"
        );
    }

    private MascotState SelectWeightedReactionAvoidingRecent()
    {
        // Base weights: Happy (55%), Curious (28%), Surprised (17%)
        (MascotState State, double BaseWeight)[] candidates =
        [
            (MascotState.Happy, 0.55),
            (MascotState.Curious, 0.28),
            (MascotState.Surprised, 0.17)
        ];

        MascotState? lastReaction = _recentReactions.Count > 0 ? _recentReactions.Last() : null;
        bool lastTwoIdentical = _recentReactions.Count >= 2 &&
                                _recentReactions.All(r => r == lastReaction);

        double totalWeight = 0;
        Span<double> adjusted = stackalloc double[candidates.Length];

        for (int i = 0; i < candidates.Length; i++)
        {
            double w = candidates[i].BaseWeight;
            if (lastTwoIdentical && candidates[i].State == lastReaction)
            {
                // Strongly suppress a 3rd identical reaction in a row
                w = 0.0;
            }
            else if (candidates[i].State == lastReaction)
            {
                // Reduce immediate repeat weight by 65%
                w *= 0.35;
            }
            adjusted[i] = w;
            totalWeight += w;
        }

        if (totalWeight <= 0)
        {
            return MascotState.Curious;
        }

        double roll = _random.NextDouble() * totalWeight;
        double cumulative = 0;
        for (int i = 0; i < candidates.Length; i++)
        {
            cumulative += adjusted[i];
            if (roll <= cumulative)
            {
                return candidates[i].State;
            }
        }

        return MascotState.Happy;
    }

    private void RecordReaction(MascotState state)
    {
        if (_recentReactions.Count >= 2)
        {
            _recentReactions.Dequeue();
        }
        _recentReactions.Enqueue(state);
    }
}

