using Bloop.Core.Awareness;
using Bloop.Core.Events;
using Bloop.Core.Interfaces;
using Bloop.Core.Movement;
using Bloop.Core.Personality;

namespace Bloop.Core.Behaviours;

/// <summary>
/// Coordinates all Milestone 3 autonomous and interactive companion behaviours:
/// - Autonomous Edge Hopping
/// - Foreground Window-Boundary Exploration
/// - Corner Exploration
/// - Docked Peekaboo & Edge Balancing
/// - Cursor Chasing
/// - Attention Seeking, Sleep Mode, and Welcome-Back Reactions
/// - Global Pause & Priority Resolution
/// </summary>
public sealed class BehaviourScheduler
{
    private readonly IMovementController _movement;
    private readonly IPersonalityEngine _personality;
    private readonly BehaviourPriorityResolver _priorityResolver;
    private readonly ExplorationBehaviour _exploration;
    private readonly InteractionBehaviour _interaction;
    private readonly NotificationBehaviour _notification;
    private readonly Random _random;

    private DateTimeOffset _lastPeekabooUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPeekabooDueUtc;
    private DateTimeOffset _nextEdgeBalanceDueUtc;
    private DateTimeOffset _lastCursorChaseUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUserActivityUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastAttentionWaveUtc = DateTimeOffset.MinValue;
    private bool _isSleeping;
    private bool _deferredWelcomeBackPending;

    public BehaviourSettings Settings { get; private set; } = BehaviourSettings.Default;
    public AwarenessPolicy CurrentAwarenessPolicy { get; private set; } = AwarenessPolicy.Default;
    public InteractionBehaviour Interaction => _interaction;
    public ExplorationBehaviour Exploration => _exploration;
    public NotificationBehaviour Notifications => _notification;
    public BehaviourPriorityResolver PriorityResolver => _priorityResolver;
    public bool IsSleeping => _isSleeping;

    public event EventHandler<(DockingEdge Edge, int DurationMs)>? PeekabooRequested;
    public event EventHandler<(DockingEdge Edge, int DurationMs)>? EdgeBalanceRequested;
    public event EventHandler<AutonomyDiagnosticEventArgs>? DiagnosticLogged;

    public BehaviourScheduler(
        IMovementController movement,
        IPersonalityEngine personality,
        Random? random = null)
    {
        _movement = movement;
        _personality = personality;
        _random = random ?? new Random();
        _priorityResolver = new BehaviourPriorityResolver();
        _exploration = new ExplorationBehaviour(_random);
        _interaction = new InteractionBehaviour(_random);
        _notification = new NotificationBehaviour();

        var now = DateTimeOffset.UtcNow;
        ScheduleNextPeekaboo(now);
        ScheduleNextEdgeBalance(now);
    }

    public void UpdateSettings(BehaviourSettings settings)
    {
        var prev = Settings;
        Settings = settings;

        _movement.SetPaused(settings.GlobalBehavioursPaused);

        if (Math.Abs(prev.PeekabooCooldownMinutes - settings.PeekabooCooldownMinutes) > 1e-4 ||
            (!prev.PeekabooEnabled && settings.PeekabooEnabled))
        {
            ScheduleNextPeekaboo(DateTimeOffset.UtcNow);
        }

        if (!settings.SleepModeEnabled && _isSleeping)
        {
            _isSleeping = false;
            _personality.TryTransition(MascotState.Idle, force: true);
        }
    }

    /// <summary>
    /// Updates the scheduler with the latest OS-wide awareness snapshot and resolved policy.
    /// Integrates GetLastInputInfo idle duration with the existing Sleep and Welcome-Back state machine.
    /// </summary>
    public void ApplyAwarenessContext(AwarenessSnapshot snapshot, AwarenessPolicy policy, DateTimeOffset nowUtc)
    {
        var prevPolicy = CurrentAwarenessPolicy;
        CurrentAwarenessPolicy = policy ?? AwarenessPolicy.Default;

        // Keep _lastUserActivityUtc synced with mouse activity while AWAKE.
        // While SLEEPING (ShouldEnterSleepMode=true), external desktop mouse or keyboard activity MUST NOT wake Bloop.
        var mouseLastActiveUtc = nowUtc - snapshot.MouseIdleDuration;
        if (!CurrentAwarenessPolicy.ShouldEnterSleepMode && mouseLastActiveUtc > _lastUserActivityUtc)
        {
            var prevInactivity = nowUtc - _lastUserActivityUtc;
            _lastUserActivityUtc = mouseLastActiveUtc;

            if (_isSleeping && snapshot.InactivityState == UserInactivityState.Active)
            {
                WakeFromSleep(prevInactivity, CurrentAwarenessPolicy.ShouldShowMascot && !CurrentAwarenessPolicy.IsQuietMode);
            }
        }

        // If Focus Mode just ended and a Welcome-Back reaction was deferred during Focus, play it now
        if (_deferredWelcomeBackPending &&
            prevPolicy.EffectiveContext == DesktopContext.Focus &&
            CurrentAwarenessPolicy.EffectiveContext == DesktopContext.Normal &&
            CurrentAwarenessPolicy.ShouldShowMascot)
        {
            _deferredWelcomeBackPending = false;
            if (Settings.WelcomeBackReactionEnabled && !Settings.GlobalBehavioursPaused)
            {
                _personality.TryTransition(MascotState.Happy, force: true);
                EmitDiagnostic(
                    AutonomyDiagnosticStage.MovementDispatched,
                    "Deferred Welcome-Back reaction played after Focus Mode exit");
            }
        }

        // If policy mandates entering Sleep Mode (either via Inactivity or Manual ForceSleep override)
        if (CurrentAwarenessPolicy.ShouldEnterSleepMode && !_isSleeping && CurrentAwarenessPolicy.ShouldShowMascot)
        {
            _isSleeping = true;
            _personality.TryTransition(MascotState.Sleepy, force: true);
            EmitDiagnostic(
                AutonomyDiagnosticStage.MovementDispatched,
                $"Entered Sleep Mode via AwarenessPolicy ({CurrentAwarenessPolicy.Reason})");
        }
        else if (_isSleeping && !CurrentAwarenessPolicy.ShouldEnterSleepMode && snapshot.InactivityState == UserInactivityState.Active)
        {
            var sleptDuration = nowUtc - _lastUserActivityUtc;
            _lastUserActivityUtc = nowUtc;
            WakeFromSleep(sleptDuration, CurrentAwarenessPolicy.ShouldShowMascot && !CurrentAwarenessPolicy.IsQuietMode);
        }
    }

    public void SetManualSleepState(bool sleep, DateTimeOffset nowUtc)
    {
        if (sleep)
        {
            _isSleeping = true;
            _personality.TryTransition(MascotState.Sleepy, force: true);
            EmitDiagnostic(AutonomyDiagnosticStage.MovementDispatched, "Manually entered Sleep Mode");
        }
        else if (_isSleeping)
        {
            _lastUserActivityUtc = nowUtc;
            WakeFromSleep(TimeSpan.Zero, playWelcomeBack: true);
        }
    }

    private void WakeFromSleep(TimeSpan inactivity, bool playWelcomeBack)
    {
        _isSleeping = false;
        if (CurrentAwarenessPolicy.EffectiveContext == DesktopContext.Focus)
        {
            _deferredWelcomeBackPending = true;
            _personality.TryTransition(MascotState.Idle, force: true);
            return;
        }

        if (playWelcomeBack && Settings.WelcomeBackReactionEnabled && !Settings.GlobalBehavioursPaused)
        {
            _personality.TryTransition(MascotState.Happy, force: true);
            EmitDiagnostic(
                AutonomyDiagnosticStage.MovementDispatched,
                $"Welcome-back reaction triggered after {inactivity.TotalSeconds:0}s sleep");
        }
        else
        {
            _personality.TryTransition(MascotState.Idle, force: true);
        }
    }

    /// <summary>
    /// Records user cursor movement or direct interaction; triggers Welcome-Back reaction
    /// if Bloop was in Sleep Mode or prolonged inactivity.
    /// </summary>
    public void NotifyUserActivity(
        DateTimeOffset nowUtc,
        double cursorDipX,
        double cursorDipY,
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea,
        bool isInsideMascot = false)
    {
        // M6.1: Once sleeping under Intelligent Sleep policy, desktop mouse movement outside Bloop MUST NOT wake him.
        if (CurrentAwarenessPolicy.ShouldEnterSleepMode && !isInsideMascot)
        {
            return;
        }

        var inactivity = nowUtc - _lastUserActivityUtc;
        _lastUserActivityUtc = nowUtc;

        if (_isSleeping && !CurrentAwarenessPolicy.ShouldEnterSleepMode)
        {
            WakeFromSleep(inactivity, playWelcomeBack: CurrentAwarenessPolicy.ShouldShowMascot);
        }

        // Evaluate playful Cursor Chasing when cursor lingers within [220..420] DIPs of Free Bloop
        if (!Settings.GlobalBehavioursPaused &&
            Settings.CursorChasingEnabled &&
            CurrentAwarenessPolicy.ShouldShowMascot &&
            CurrentAwarenessPolicy.AllowCursorChasing &&
            _movement.CurrentState == MovementState.Free &&
            (nowUtc - _lastCursorChaseUtc).TotalSeconds >= Math.Max(5.0, Settings.CursorChaseCooldownSeconds))
        {
            double centerDipX = _movement.CurrentWindowLeftDip + windowWidthDip * 0.5;
            double centerDipY = _movement.CurrentWindowTopDip + windowHeightDip * 0.5;
            double dist = Math.Sqrt(Math.Pow(cursorDipX - centerDipX, 2) + Math.Pow(cursorDipY - centerDipY, 2));

            if (dist is >= 220.0 and <= 440.0)
            {
                _lastCursorChaseUtc = nowUtc;
                double stepX = (cursorDipX - centerDipX) * 0.55;
                double stepY = (cursorDipY - centerDipY) * 0.55;
                double targetLeft = Math.Clamp(
                    _movement.CurrentWindowLeftDip + stepX,
                    workArea.Left,
                    Math.Max(workArea.Left, workArea.Right - windowWidthDip)
                );
                double targetTop = Math.Clamp(
                    _movement.CurrentWindowTopDip + stepY,
                    workArea.Top,
                    Math.Max(workArea.Top, workArea.Bottom - windowHeightDip)
                );

                _personality.TryTransition(MascotState.Curious, force: true);
                _movement.TryHopToCoordinate(targetLeft, targetTop, DockingEdge.None, workArea, "CursorChaseHop");
            }
        }
    }

    public void EvaluateTick(
        DateTimeOffset nowUtc,
        double windowWidthDip,
        double windowHeightDip,
        WorkAreaBounds workArea,
        ForegroundWindowGeometry foregroundWindow)
    {
        if (Settings.GlobalBehavioursPaused || !CurrentAwarenessPolicy.ShouldShowMascot)
        {
            return;
        }

        // 1. Check Inactivity -> Sleep Mode or Attention Seeking
        var idleDuration = nowUtc - _lastUserActivityUtc;
        if ((Settings.SleepModeEnabled &&
             idleDuration >= TimeSpan.FromMinutes(Math.Max(0.2, Settings.SleepInactivityMinutes))) ||
            CurrentAwarenessPolicy.ShouldEnterSleepMode)
        {
            if (!_isSleeping &&
                _priorityResolver.CanExecute(BehaviourCategory.Idle, _movement.CurrentState, _personality.CurrentState, out _, CurrentAwarenessPolicy))
            {
                _isSleeping = true;
                _personality.TryTransition(MascotState.Sleepy, force: true);
                EmitDiagnostic(
                    AutonomyDiagnosticStage.MovementDispatched,
                    $"Entered Sleep Mode after {idleDuration.TotalSeconds:0}s inactivity");
            }
            return; // Do not hop around while sleeping
        }

        if (Settings.AttentionSeekingEnabled &&
            CurrentAwarenessPolicy.AllowAttentionSeeking &&
            !_isSleeping &&
            idleDuration >= TimeSpan.FromMinutes(Math.Max(0.2, Settings.AttentionSeekMinutes)) &&
            (nowUtc - _lastAttentionWaveUtc) >= TimeSpan.FromMinutes(Math.Max(0.2, Settings.AttentionSeekMinutes)))
        {
            if (_priorityResolver.CanExecute(BehaviourCategory.AutonomousMicro, _movement.CurrentState, _personality.CurrentState, out _, CurrentAwarenessPolicy))
            {
                _lastAttentionWaveUtc = nowUtc;
                _personality.TryTransition(MascotState.Curious, force: true);
                EmitDiagnostic(
                    AutonomyDiagnosticStage.MovementDispatched,
                    $"Attention-seeking reaction triggered after {idleDuration.TotalSeconds:0}s idle");
            }
        }

        // 2. Check Docked Micro-Behaviours: Peekaboo & Edge Balancing
        if (CurrentAwarenessPolicy.AllowPeekaboo &&
            _movement.CurrentState == MovementState.Docked &&
            _movement.CurrentEdge != DockingEdge.None &&
            _priorityResolver.CanExecute(
                BehaviourCategory.AutonomousMicro,
                _movement.CurrentState,
                _personality.CurrentState,
                out _,
                CurrentAwarenessPolicy))
        {
            if (Settings.PeekabooEnabled && nowUtc >= _nextPeekabooDueUtc)
            {
                TriggerPeekaboo(nowUtc);
                return;
            }

            if (Settings.EdgeBalancingEnabled && nowUtc >= _nextEdgeBalanceDueUtc)
            {
                TriggerEdgeBalance(nowUtc);
                return;
            }
        }

        // 3. Check Autonomous Movement (Edge Hopping, Corner Exploration, or Window-Boundary Exploration)
        if (!CurrentAwarenessPolicy.AllowAutonomousMovement)
        {
            return;
        }

        if (!Settings.AutonomousEdgeHoppingEnabled &&
            !Settings.WindowBoundaryExplorationEnabled &&
            !Settings.CornerExplorationEnabled)
        {
            return;
        }

        if (!_priorityResolver.CanExecute(
                BehaviourCategory.AutonomousMovement,
                _movement.CurrentState,
                _personality.CurrentState,
                out _,
                CurrentAwarenessPolicy))
        {
            return;
        }

        if (!_movement.CanRunAutonomousMovement(nowUtc))
        {
            _movement.TryTriggerAutonomousHop(nowUtc, windowWidthDip, windowHeightDip, workArea);
            return;
        }

        // A. Try Foreground Window-Boundary Exploration (~35% weight when enabled)
        if (Settings.WindowBoundaryExplorationEnabled &&
            ((!Settings.AutonomousEdgeHoppingEnabled && !Settings.CornerExplorationEnabled) || _random.NextDouble() < 0.35))
        {
            if (_exploration.TrySelectWindowBoundaryTarget(
                    foregroundWindow,
                    workArea,
                    windowWidthDip,
                    windowHeightDip,
                    out var perchTarget,
                    out var exploreRejection) &&
                perchTarget is not null)
            {
                _movement.TryHopToCoordinate(
                    perchTarget.TargetWindowLeftDip,
                    perchTarget.TargetWindowTopDip,
                    perchTarget.DockEdge,
                    workArea,
                    "WindowBoundaryPerch");
                return;
            }

            if (!Settings.AutonomousEdgeHoppingEnabled && !Settings.CornerExplorationEnabled)
            {
                EmitDiagnostic(
                    AutonomyDiagnosticStage.EligibilityRejected,
                    $"Window exploration skipped: {exploreRejection}",
                    exploreRejection);
                return;
            }
        }

        // B. Try Corner Exploration (~30% weight when enabled)
        if (Settings.CornerExplorationEnabled &&
            (!Settings.AutonomousEdgeHoppingEnabled || _random.NextDouble() < 0.30))
        {
            var corner = _exploration.SelectCornerTarget(workArea, windowWidthDip, windowHeightDip, _movement.CurrentEdge);
            _movement.TryHopToCoordinate(
                corner.TargetWindowLeftDip,
                corner.TargetWindowTopDip,
                corner.DockEdge,
                workArea,
                $"CornerExploration({corner.Description})");
            return;
        }

        // C. Standard 4-Edge Autonomous Hopping
        if (Settings.AutonomousEdgeHoppingEnabled)
        {
            _movement.TryTriggerAutonomousHop(nowUtc, windowWidthDip, windowHeightDip, workArea);
        }
    }

    public bool TriggerPeekaboo(DateTimeOffset nowUtc)
    {
        if (_movement.CurrentState != MovementState.Docked || _movement.CurrentEdge == DockingEdge.None)
        {
            return false;
        }

        _lastPeekabooUtc = nowUtc;
        ScheduleNextPeekaboo(nowUtc);

        EmitDiagnostic(
            AutonomyDiagnosticStage.MovementDispatched,
            $"Dispatched docked Peekaboo on edge={_movement.CurrentEdge}",
            selectedEdge: _movement.CurrentEdge);

        PeekabooRequested?.Invoke(this, (_movement.CurrentEdge, 1600));
        return true;
    }

    public bool TriggerEdgeBalance(DateTimeOffset nowUtc)
    {
        if (_movement.CurrentState != MovementState.Docked || _movement.CurrentEdge == DockingEdge.None)
        {
            return false;
        }

        ScheduleNextEdgeBalance(nowUtc);

        EmitDiagnostic(
            AutonomyDiagnosticStage.MovementDispatched,
            $"Dispatched docked EdgeBalance wobble on edge={_movement.CurrentEdge}",
            selectedEdge: _movement.CurrentEdge);

        EdgeBalanceRequested?.Invoke(this, (_movement.CurrentEdge, 1800));
        return true;
    }

    private void ScheduleNextPeekaboo(DateTimeOffset fromUtc)
    {
        double baseMin = Math.Max(0.1, Settings.PeekabooCooldownMinutes);
        double delayMin = baseMin + _random.NextDouble() * baseMin * 0.5;
        _nextPeekabooDueUtc = fromUtc.AddMinutes(delayMin);
    }

    private void ScheduleNextEdgeBalance(DateTimeOffset fromUtc)
    {
        double delayMin = 1.2 + _random.NextDouble() * 1.5;
        _nextEdgeBalanceDueUtc = fromUtc.AddMinutes(delayMin);
    }

    private void EmitDiagnostic(
        AutonomyDiagnosticStage stage,
        string summary,
        string? rejectionReason = null,
        DockingEdge selectedEdge = DockingEdge.None)
    {
        DiagnosticLogged?.Invoke(this, new AutonomyDiagnosticEventArgs(
            stage,
            summary,
            _movement.NextAutonomyDueUtc,
            rejectionReason,
            selectedEdge,
            DateTimeOffset.UtcNow));
    }
}
