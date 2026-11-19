using System.Diagnostics;

namespace Bloop.Core.Productivity;

public enum TaskPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public enum TaskListFilter
{
    All = 0,
    Active = 1,
    Completed = 2
}

public sealed record TodoTaskItem(
    string Id,
    string Title,
    string Notes = "",
    TaskPriority Priority = TaskPriority.Normal,
    DateTimeOffset? DueAtUtc = null,
    bool IsCompleted = false,
    DateTimeOffset CreatedAtUtc = default,
    DateTimeOffset? CompletedAtUtc = null,
    DateTimeOffset UpdatedAtUtc = default
);

public enum CountdownTimerState
{
    Idle = 0,
    Running = 1,
    Paused = 2,
    Completed = 3
}

public sealed record CountdownTimerSnapshot(
    string TimerId,
    string Label,
    TimeSpan TotalDuration,
    TimeSpan RemainingDuration,
    CountdownTimerState State,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? TargetEndUtc = null,
    DateTimeOffset UpdatedAtUtc = default
)
{
    public static CountdownTimerSnapshot Default => new(
        TimerId: "default-timer",
        Label: "Focus Timer",
        TotalDuration: TimeSpan.FromMinutes(25),
        RemainingDuration: TimeSpan.FromMinutes(25),
        State: CountdownTimerState.Idle,
        UpdatedAtUtc: DateTimeOffset.UtcNow);
}

public sealed record ReminderItem(
    string Id,
    string Title,
    DateTimeOffset TriggerAtUtc,
    string? TaskId = null,
    TaskPriority Priority = TaskPriority.Normal,
    bool IsDelivered = false,
    DateTimeOffset? DeliveredAtUtc = null,
    DateTimeOffset CreatedAtUtc = default
);

public sealed record ReminderDeliveryDecision(
    ReminderItem Reminder,
    bool IsMissedRecovery,
    bool IsPriorityAlert
);

public sealed record QuickNoteItem(
    string Id,
    string Title,
    string Content,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
);

public sealed record ProductivityDiagnosticEvent(
    string EventName,
    string Summary,
    string? EntityId = null,
    DateTimeOffset TimestampUtc = default
);

public interface IProductivityRepository
{
    // Tasks
    IReadOnlyList<TodoTaskItem> GetTasks(TaskListFilter filter = TaskListFilter.All);
    TodoTaskItem? GetTaskById(string id);
    TodoTaskItem SaveTask(TodoTaskItem task);
    TodoTaskItem SetTaskCompleted(string id, bool isCompleted, DateTimeOffset nowUtc);
    bool DeleteTask(string id);

    // Reminders
    IReadOnlyList<ReminderItem> GetReminders(bool includeDelivered = false);
    ReminderItem SaveReminder(ReminderItem reminder);
    bool MarkReminderDelivered(string id, DateTimeOffset deliveredAtUtc);
    bool DeleteReminder(string id);

    // Timer persistence
    CountdownTimerSnapshot LoadTimerSnapshot();
    void SaveTimerSnapshot(CountdownTimerSnapshot snapshot);

    // Quick Notes
    IReadOnlyList<QuickNoteItem> GetNotes(string? searchQuery = null);
    QuickNoteItem? GetNoteById(string id);
    QuickNoteItem SaveNote(QuickNoteItem note);
    bool DeleteNote(string id);
}

/// <summary>
/// Monotonic + wall-clock countdown timer engine.
/// Uses a monotonic Stopwatch anchor for in-process countdown evaluation (immune to system clock jitter
/// and UI refresh frequency) while preserving an absolute TargetEndUtc deadline for SQLite persistence
/// and restart recovery.
/// </summary>
public sealed class CountdownTimerEngine
{
    private CountdownTimerSnapshot _current;
    private readonly Stopwatch _monotonicStopwatch = new();
    private TimeSpan _runSegmentInitialRemaining = TimeSpan.Zero;
    private DateTimeOffset _runSegmentAnchorUtc = DateTimeOffset.MinValue;

    public event EventHandler<CountdownTimerSnapshot>? StateChanged;
    public event EventHandler<CountdownTimerSnapshot>? TimerCompleted;
    public event EventHandler<ProductivityDiagnosticEvent>? DiagnosticLogged;

    public CountdownTimerSnapshot Current => _current;

    /// <summary>
    /// Formats a remaining duration for a seconds-precision display using Ceiling of whole seconds
    /// so that a newly started N-second timer displays N for a full second rather than dropping
    /// to N-1 on the first sub-second refresh tick.
    /// </summary>
    public static string FormatDisplayClock(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "00:00";
        }

        long wholeSeconds = (long)Math.Ceiling(remaining.TotalSeconds - 1e-6);
        if (wholeSeconds < 0)
        {
            wholeSeconds = 0;
        }

        var displaySpan = TimeSpan.FromSeconds(wholeSeconds);
        return displaySpan.TotalHours >= 1.0
            ? $"{(int)displaySpan.TotalHours:00}:{displaySpan.Minutes:00}:{displaySpan.Seconds:00}"
            : $"{displaySpan.Minutes:00}:{displaySpan.Seconds:00}";
    }

    public CountdownTimerEngine(CountdownTimerSnapshot? initialSnapshot = null, DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        _current = initialSnapshot ?? CountdownTimerSnapshot.Default with { UpdatedAtUtc = now };

        if (_current.State == CountdownTimerState.Running && _current.TargetEndUtc.HasValue)
        {
            var recoveredRemaining = _current.TargetEndUtc.Value - now;
            if (recoveredRemaining > TimeSpan.Zero)
            {
                _runSegmentInitialRemaining = recoveredRemaining;
                _runSegmentAnchorUtc = now;
                _monotonicStopwatch.Restart();
            }
        }

        EvaluateTick(now);
    }

    public CountdownTimerSnapshot Start(TimeSpan duration, string? label, DateTimeOffset nowUtc)
    {
        var clamped = TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds, 1.0, 24 * 3600.0));
        var cleanLabel = string.IsNullOrWhiteSpace(label) ? "Countdown Timer" : label.Trim();

        _runSegmentInitialRemaining = clamped;
        _runSegmentAnchorUtc = nowUtc;
        _monotonicStopwatch.Restart();

        _current = new CountdownTimerSnapshot(
            TimerId: _current.TimerId,
            Label: cleanLabel,
            TotalDuration: clamped,
            RemainingDuration: clamped,
            State: CountdownTimerState.Running,
            StartedAtUtc: nowUtc,
            TargetEndUtc: nowUtc + clamped,
            UpdatedAtUtc: nowUtc);

        Log("TimerStarted", $"Started '{cleanLabel}' for {FormatDisplayClock(clamped)} (TargetEndUtc={_current.TargetEndUtc:O})", _current.TimerId, nowUtc);
        StateChanged?.Invoke(this, _current);
        return _current;
    }

    public CountdownTimerSnapshot Pause(DateTimeOffset nowUtc)
    {
        if (_current.State != CountdownTimerState.Running || !_current.TargetEndUtc.HasValue)
        {
            return _current;
        }

        var remaining = ComputeCurrentRemaining(nowUtc);
        _monotonicStopwatch.Stop();

        if (remaining <= TimeSpan.Zero)
        {
            return CompleteInternal(nowUtc);
        }

        _current = _current with
        {
            RemainingDuration = remaining,
            State = CountdownTimerState.Paused,
            TargetEndUtc = null,
            UpdatedAtUtc = nowUtc
        };

        Log("TimerPaused", $"Paused '{_current.Label}' with {remaining.TotalSeconds:F2}s remaining ({FormatDisplayClock(remaining)})", _current.TimerId, nowUtc);
        StateChanged?.Invoke(this, _current);
        return _current;
    }

    public CountdownTimerSnapshot Resume(DateTimeOffset nowUtc)
    {
        if (_current.State != CountdownTimerState.Paused)
        {
            return _current;
        }

        var remaining = _current.RemainingDuration > TimeSpan.Zero
            ? _current.RemainingDuration
            : _current.TotalDuration;

        _runSegmentInitialRemaining = remaining;
        _runSegmentAnchorUtc = nowUtc;
        _monotonicStopwatch.Restart();

        _current = _current with
        {
            RemainingDuration = remaining,
            State = CountdownTimerState.Running,
            TargetEndUtc = nowUtc + remaining,
            UpdatedAtUtc = nowUtc
        };

        Log("TimerResumed", $"Resumed '{_current.Label}' with {remaining.TotalSeconds:F2}s remaining ({FormatDisplayClock(remaining)})", _current.TimerId, nowUtc);
        StateChanged?.Invoke(this, _current);
        return _current;
    }

    public CountdownTimerSnapshot Reset(DateTimeOffset nowUtc)
    {
        _monotonicStopwatch.Reset();
        _runSegmentInitialRemaining = TimeSpan.Zero;
        var total = _current.TotalDuration > TimeSpan.Zero ? _current.TotalDuration : TimeSpan.FromMinutes(25);
        _current = _current with
        {
            RemainingDuration = total,
            State = CountdownTimerState.Idle,
            StartedAtUtc = null,
            TargetEndUtc = null,
            UpdatedAtUtc = nowUtc
        };

        Log("TimerReset", $"Reset '{_current.Label}' to {FormatDisplayClock(total)}", _current.TimerId, nowUtc);
        StateChanged?.Invoke(this, _current);
        return _current;
    }

    public CountdownTimerSnapshot Cancel(DateTimeOffset nowUtc)
    {
        _monotonicStopwatch.Reset();
        _runSegmentInitialRemaining = TimeSpan.Zero;
        var total = _current.TotalDuration > TimeSpan.Zero ? _current.TotalDuration : TimeSpan.FromMinutes(25);
        _current = _current with
        {
            RemainingDuration = total,
            State = CountdownTimerState.Idle,
            StartedAtUtc = null,
            TargetEndUtc = null,
            UpdatedAtUtc = nowUtc
        };

        Log("TimerCancelled", $"Cancelled '{_current.Label}'", _current.TimerId, nowUtc);
        StateChanged?.Invoke(this, _current);
        return _current;
    }

    /// <summary>
    /// Evaluates remaining time from the monotonic clock (when running in real-time) or
    /// the deterministic TargetEndUtc deadline (when synthetic test timestamps or OS resume jumps occur).
    /// Returns true if the timer transitioned to Completed on this evaluation.
    /// </summary>
    public bool EvaluateTick(DateTimeOffset nowUtc)
    {
        if (_current.State != CountdownTimerState.Running || !_current.TargetEndUtc.HasValue)
        {
            return false;
        }

        var remaining = ComputeCurrentRemaining(nowUtc);
        if (remaining <= TimeSpan.Zero)
        {
            _monotonicStopwatch.Stop();
            CompleteInternal(nowUtc);
            return true;
        }

        _current = _current with
        {
            RemainingDuration = remaining,
            UpdatedAtUtc = nowUtc
        };
        StateChanged?.Invoke(this, _current);
        return false;
    }

    private TimeSpan ComputeCurrentRemaining(DateTimeOffset nowUtc)
    {
        if (!_current.TargetEndUtc.HasValue)
        {
            return _current.RemainingDuration;
        }

        var wallRemaining = _current.TargetEndUtc.Value - nowUtc;

        if (_monotonicStopwatch.IsRunning && _runSegmentAnchorUtc != DateTimeOffset.MinValue)
        {
            var monotonicElapsed = _monotonicStopwatch.Elapsed;
            var wallElapsed = nowUtc - _runSegmentAnchorUtc;

            // Use monotonic Stopwatch for real-time execution when wall-clock and monotonic elapsed agree within 50ms.
            // If wall-clock was adjusted by NTP while running (or system resumed from suspend / synthetic test clock advanced),
            // reconcile accordingly:
            double driftMs = Math.Abs((wallElapsed - monotonicElapsed).TotalMilliseconds);
            if (driftMs <= 50.0)
            {
                return _runSegmentInitialRemaining - monotonicElapsed;
            }
        }

        return wallRemaining;
    }

    private CountdownTimerSnapshot CompleteInternal(DateTimeOffset nowUtc)
    {
        _current = _current with
        {
            RemainingDuration = TimeSpan.Zero,
            State = CountdownTimerState.Completed,
            TargetEndUtc = null,
            UpdatedAtUtc = nowUtc
        };

        Log("TimerCompleted", $"Completed '{_current.Label}'", _current.TimerId, nowUtc);
        StateChanged?.Invoke(this, _current);
        TimerCompleted?.Invoke(this, _current);
        return _current;
    }

    private void Log(string eventName, string summary, string? id, DateTimeOffset nowUtc)
    {
        DiagnosticLogged?.Invoke(this, new ProductivityDiagnosticEvent(eventName, summary, id, nowUtc));
    }
}

/// <summary>
/// Persistent reminder scheduler with restart recovery, missed-reminder detection,
/// and duplicate-delivery prevention.
/// </summary>
public sealed class ReminderScheduler
{
    private readonly IProductivityRepository _repository;
    private readonly HashSet<string> _deliveredIdsInSession = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<ReminderDeliveryDecision>? ReminderTriggered;
    public event EventHandler<ProductivityDiagnosticEvent>? DiagnosticLogged;

    public ReminderScheduler(IProductivityRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Evaluates due and missed reminders against nowUtc, marks them delivered in SQLite atomically,
    /// and prevents duplicate delivery across restarts and ticks.
    /// </summary>
    public IReadOnlyList<ReminderDeliveryDecision> EvaluateDueReminders(
        DateTimeOffset nowUtc,
        bool isStartupRecovery = false)
    {
        var pending = _repository.GetReminders(includeDelivered: false);
        if (pending.Count == 0)
        {
            return [];
        }

        var triggered = new List<ReminderDeliveryDecision>();

        foreach (var rem in pending.OrderBy(r => r.TriggerAtUtc))
        {
            if (rem.IsDelivered || _deliveredIdsInSession.Contains(rem.Id))
            {
                continue;
            }

            if (nowUtc < rem.TriggerAtUtc)
            {
                continue;
            }

            bool isMissed = isStartupRecovery || (nowUtc - rem.TriggerAtUtc).TotalSeconds > 15.0;
            bool marked = _repository.MarkReminderDelivered(rem.Id, nowUtc);
            _deliveredIdsInSession.Add(rem.Id);

            if (!marked)
            {
                continue;
            }

            var updatedRem = rem with
            {
                IsDelivered = true,
                DeliveredAtUtc = nowUtc
            };

            var decision = new ReminderDeliveryDecision(
                Reminder: updatedRem,
                IsMissedRecovery: isMissed,
                IsPriorityAlert: rem.Priority == TaskPriority.High);

            triggered.Add(decision);

            DiagnosticLogged?.Invoke(
                this,
                new ProductivityDiagnosticEvent(
                    isMissed ? "MissedReminderRecovered" : "ReminderTriggered",
                    $"{(isMissed ? "Recovered missed reminder" : "Triggered reminder")}: '{rem.Title}' (Scheduled={rem.TriggerAtUtc:O})",
                    rem.Id,
                    nowUtc));

            ReminderTriggered?.Invoke(this, decision);
        }

        return triggered;
    }
}
