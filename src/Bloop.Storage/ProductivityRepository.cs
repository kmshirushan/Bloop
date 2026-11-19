using System.Globalization;
using Bloop.Core.Productivity;
using Microsoft.Data.Sqlite;

namespace Bloop.Storage;

/// <summary>
/// Thread-safe SQLite repository for Tasks, Countdown Timer state, Reminders, and Quick Notes.
/// </summary>
public sealed class ProductivityRepository : IProductivityRepository
{
    private readonly DatabaseService _db;
    private readonly object _writeLock = new();

    public ProductivityRepository(DatabaseService db)
    {
        _db = db;
    }

    public IReadOnlyList<TodoTaskItem> GetTasks(TaskListFilter filter = TaskListFilter.All)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();

        var whereClause = filter switch
        {
            TaskListFilter.Active => "WHERE is_completed = 0",
            TaskListFilter.Completed => "WHERE is_completed = 1",
            _ => string.Empty
        };

        cmd.CommandText = $"""
            SELECT id, title, notes, priority, due_at_utc, is_completed, created_at_utc, completed_at_utc, updated_at_utc
            FROM productivity_tasks
            {whereClause}
            ORDER BY is_completed ASC, priority DESC, updated_at_utc DESC;
            """;

        var list = new List<TodoTaskItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadTask(reader));
        }

        return list;
    }

    public TodoTaskItem? GetTaskById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, notes, priority, due_at_utc, is_completed, created_at_utc, completed_at_utc, updated_at_utc
            FROM productivity_tasks
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.Trim());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadTask(reader) : null;
    }

    public TodoTaskItem SaveTask(TodoTaskItem task)
    {
        lock (_writeLock)
        {
            var now = DateTimeOffset.UtcNow;
            var id = string.IsNullOrWhiteSpace(task.Id) ? $"task-{Guid.NewGuid():N}" : task.Id.Trim();
            var title = string.IsNullOrWhiteSpace(task.Title) ? "Untitled Task" : task.Title.Trim();
            var notes = task.Notes?.Trim() ?? string.Empty;
            var priority = Enum.IsDefined(task.Priority) ? task.Priority : TaskPriority.Normal;
            var createdAt = task.CreatedAtUtc == default ? now : task.CreatedAtUtc;
            var updatedAt = task.UpdatedAtUtc == default ? now : task.UpdatedAtUtc;
            DateTimeOffset? completedAt = task.IsCompleted ? (task.CompletedAtUtc ?? now) : null;

            var normalized = new TodoTaskItem(
                Id: id,
                Title: title,
                Notes: notes,
                Priority: priority,
                DueAtUtc: task.DueAtUtc,
                IsCompleted: task.IsCompleted,
                CreatedAtUtc: createdAt,
                CompletedAtUtc: completedAt,
                UpdatedAtUtc: updatedAt);

            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO productivity_tasks (
                    id, title, notes, priority, due_at_utc, is_completed, created_at_utc, completed_at_utc, updated_at_utc
                ) VALUES (
                    $id, $title, $notes, $priority, $dueAt, $isCompleted, $createdAt, $completedAt, $updatedAt
                )
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    notes = excluded.notes,
                    priority = excluded.priority,
                    due_at_utc = excluded.due_at_utc,
                    is_completed = excluded.is_completed,
                    completed_at_utc = excluded.completed_at_utc,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$id", normalized.Id);
            cmd.Parameters.AddWithValue("$title", normalized.Title);
            cmd.Parameters.AddWithValue("$notes", normalized.Notes);
            cmd.Parameters.AddWithValue("$priority", (int)normalized.Priority);
            cmd.Parameters.AddWithValue("$dueAt", normalized.DueAtUtc.HasValue ? normalized.DueAtUtc.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$isCompleted", normalized.IsCompleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$createdAt", normalized.CreatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$completedAt", normalized.CompletedAtUtc.HasValue ? normalized.CompletedAtUtc.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$updatedAt", normalized.UpdatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();

            return normalized;
        }
    }

    public TodoTaskItem SetTaskCompleted(string id, bool isCompleted, DateTimeOffset nowUtc)
    {
        lock (_writeLock)
        {
            var existing = GetTaskById(id) ?? throw new InvalidOperationException($"Task '{id}' not found.");
            var updated = existing with
            {
                IsCompleted = isCompleted,
                CompletedAtUtc = isCompleted ? nowUtc : null,
                UpdatedAtUtc = nowUtc
            };
            return SaveTask(updated);
        }
    }

    public bool DeleteTask(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (_writeLock)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM productivity_reminders WHERE task_id = $id;
                DELETE FROM productivity_tasks WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id.Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<ReminderItem> GetReminders(bool includeDelivered = false)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        var whereClause = includeDelivered ? string.Empty : "WHERE is_delivered = 0";
        cmd.CommandText = $"""
            SELECT id, task_id, title, trigger_at_utc, priority, is_delivered, delivered_at_utc, created_at_utc
            FROM productivity_reminders
            {whereClause}
            ORDER BY is_delivered ASC, trigger_at_utc ASC;
            """;

        var list = new List<ReminderItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadReminder(reader));
        }

        return list;
    }

    public ReminderItem SaveReminder(ReminderItem reminder)
    {
        lock (_writeLock)
        {
            var now = DateTimeOffset.UtcNow;
            var id = string.IsNullOrWhiteSpace(reminder.Id) ? $"rem-{Guid.NewGuid():N}" : reminder.Id.Trim();
            var title = string.IsNullOrWhiteSpace(reminder.Title) ? "Reminder" : reminder.Title.Trim();
            var priority = Enum.IsDefined(reminder.Priority) ? reminder.Priority : TaskPriority.Normal;
            var createdAt = reminder.CreatedAtUtc == default ? now : reminder.CreatedAtUtc;

            var normalized = new ReminderItem(
                Id: id,
                Title: title,
                TriggerAtUtc: reminder.TriggerAtUtc,
                TaskId: string.IsNullOrWhiteSpace(reminder.TaskId) ? null : reminder.TaskId.Trim(),
                Priority: priority,
                IsDelivered: reminder.IsDelivered,
                DeliveredAtUtc: reminder.DeliveredAtUtc,
                CreatedAtUtc: createdAt);

            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO productivity_reminders (
                    id, task_id, title, trigger_at_utc, priority, is_delivered, delivered_at_utc, created_at_utc
                ) VALUES (
                    $id, $taskId, $title, $triggerAt, $priority, $isDelivered, $deliveredAt, $createdAt
                )
                ON CONFLICT(id) DO UPDATE SET
                    task_id = excluded.task_id,
                    title = excluded.title,
                    trigger_at_utc = excluded.trigger_at_utc,
                    priority = excluded.priority,
                    is_delivered = excluded.is_delivered,
                    delivered_at_utc = excluded.delivered_at_utc;
                """;
            cmd.Parameters.AddWithValue("$id", normalized.Id);
            cmd.Parameters.AddWithValue("$taskId", normalized.TaskId is not null ? normalized.TaskId : DBNull.Value);
            cmd.Parameters.AddWithValue("$title", normalized.Title);
            cmd.Parameters.AddWithValue("$triggerAt", normalized.TriggerAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$priority", (int)normalized.Priority);
            cmd.Parameters.AddWithValue("$isDelivered", normalized.IsDelivered ? 1 : 0);
            cmd.Parameters.AddWithValue("$deliveredAt", normalized.DeliveredAtUtc.HasValue ? normalized.DeliveredAtUtc.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$createdAt", normalized.CreatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();

            return normalized;
        }
    }

    public bool MarkReminderDelivered(string id, DateTimeOffset deliveredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (_writeLock)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE productivity_reminders
                SET is_delivered = 1, delivered_at_utc = $deliveredAt
                WHERE id = $id AND is_delivered = 0;
                """;
            cmd.Parameters.AddWithValue("$id", id.Trim());
            cmd.Parameters.AddWithValue("$deliveredAt", deliveredAtUtc.ToString("O"));
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool DeleteReminder(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (_writeLock)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM productivity_reminders WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id.Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public CountdownTimerSnapshot LoadTimerSnapshot()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT timer_id, label, total_seconds, remaining_seconds, state, started_at_utc, target_end_utc, updated_at_utc
            FROM productivity_timers
            WHERE timer_id = 'default-timer';
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return CountdownTimerSnapshot.Default;
        }

        string timerId = reader.GetString(0);
        string label = reader.GetString(1);
        double totalSec = Math.Clamp(reader.GetDouble(2), 1.0, 24 * 3600.0);
        double remSec = Math.Clamp(reader.GetDouble(3), 0.0, 24 * 3600.0);
        int stateInt = reader.GetInt32(4);
        var state = Enum.IsDefined((CountdownTimerState)stateInt) ? (CountdownTimerState)stateInt : CountdownTimerState.Idle;
        DateTimeOffset? startedAt = reader.IsDBNull(5) ? null : ParseIsoTimestamp(reader.GetString(5));
        DateTimeOffset? targetEnd = reader.IsDBNull(6) ? null : ParseIsoTimestamp(reader.GetString(6));
        DateTimeOffset updatedAt = ParseIsoTimestamp(reader.GetString(7)) ?? DateTimeOffset.UtcNow;

        return new CountdownTimerSnapshot(
            TimerId: timerId,
            Label: label,
            TotalDuration: TimeSpan.FromSeconds(totalSec),
            RemainingDuration: TimeSpan.FromSeconds(remSec),
            State: state,
            StartedAtUtc: startedAt,
            TargetEndUtc: targetEnd,
            UpdatedAtUtc: updatedAt);
    }

    public void SaveTimerSnapshot(CountdownTimerSnapshot snapshot)
    {
        lock (_writeLock)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO productivity_timers (
                    timer_id, label, total_seconds, remaining_seconds, state, started_at_utc, target_end_utc, updated_at_utc
                ) VALUES (
                    $id, $label, $totalSec, $remSec, $state, $startedAt, $targetEnd, $updatedAt
                )
                ON CONFLICT(timer_id) DO UPDATE SET
                    label = excluded.label,
                    total_seconds = excluded.total_seconds,
                    remaining_seconds = excluded.remaining_seconds,
                    state = excluded.state,
                    started_at_utc = excluded.started_at_utc,
                    target_end_utc = excluded.target_end_utc,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(snapshot.TimerId) ? "default-timer" : snapshot.TimerId);
            cmd.Parameters.AddWithValue("$label", string.IsNullOrWhiteSpace(snapshot.Label) ? "Countdown Timer" : snapshot.Label.Trim());
            cmd.Parameters.AddWithValue("$totalSec", snapshot.TotalDuration.TotalSeconds);
            cmd.Parameters.AddWithValue("$remSec", snapshot.RemainingDuration.TotalSeconds);
            cmd.Parameters.AddWithValue("$state", (int)snapshot.State);
            cmd.Parameters.AddWithValue("$startedAt", snapshot.StartedAtUtc.HasValue ? snapshot.StartedAtUtc.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$targetEnd", snapshot.TargetEndUtc.HasValue ? snapshot.TargetEndUtc.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("$updatedAt", snapshot.UpdatedAtUtc == default ? DateTimeOffset.UtcNow.ToString("O") : snapshot.UpdatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<QuickNoteItem> GetNotes(string? searchQuery = null)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();

        var q = searchQuery?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(q))
        {
            cmd.CommandText = """
                SELECT id, title, content, created_at_utc, updated_at_utc
                FROM quick_notes
                ORDER BY updated_at_utc DESC;
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT id, title, content, created_at_utc, updated_at_utc
                FROM quick_notes
                WHERE title LIKE $pattern OR content LIKE $pattern
                ORDER BY updated_at_utc DESC;
                """;
            cmd.Parameters.AddWithValue("$pattern", $"%{q}%");
        }

        var list = new List<QuickNoteItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadNote(reader));
        }

        return list;
    }

    public QuickNoteItem? GetNoteById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, content, created_at_utc, updated_at_utc
            FROM quick_notes
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id.Trim());

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadNote(reader) : null;
    }

    public QuickNoteItem SaveNote(QuickNoteItem note)
    {
        lock (_writeLock)
        {
            var now = DateTimeOffset.UtcNow;
            var id = string.IsNullOrWhiteSpace(note.Id) ? $"note-{Guid.NewGuid():N}" : note.Id.Trim();
            var content = note.Content ?? string.Empty;
            var rawTitle = note.Title?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(rawTitle))
            {
                var firstLine = content
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                rawTitle = !string.IsNullOrWhiteSpace(firstLine)
                    ? (firstLine.Length > 48 ? firstLine[..48] + "…" : firstLine)
                    : "Untitled Note";
            }

            var createdAt = note.CreatedAtUtc == default ? now : note.CreatedAtUtc;
            var updatedAt = note.UpdatedAtUtc == default ? now : note.UpdatedAtUtc;

            var normalized = new QuickNoteItem(
                Id: id,
                Title: rawTitle,
                Content: content,
                CreatedAtUtc: createdAt,
                UpdatedAtUtc: updatedAt);

            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO quick_notes (
                    id, title, content, created_at_utc, updated_at_utc
                ) VALUES (
                    $id, $title, $content, $createdAt, $updatedAt
                )
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    content = excluded.content,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$id", normalized.Id);
            cmd.Parameters.AddWithValue("$title", normalized.Title);
            cmd.Parameters.AddWithValue("$content", normalized.Content);
            cmd.Parameters.AddWithValue("$createdAt", normalized.CreatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$updatedAt", normalized.UpdatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();

            return normalized;
        }
    }

    public bool DeleteNote(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        lock (_writeLock)
        {
            using var conn = _db.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM quick_notes WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id.Trim());
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private static TodoTaskItem ReadTask(SqliteDataReader r)
    {
        string id = r.GetString(0);
        string title = r.GetString(1);
        string notes = r.GetString(2);
        int prioInt = r.GetInt32(3);
        var prio = Enum.IsDefined((TaskPriority)prioInt) ? (TaskPriority)prioInt : TaskPriority.Normal;
        DateTimeOffset? dueAt = r.IsDBNull(4) ? null : ParseIsoTimestamp(r.GetString(4));
        bool isCompleted = r.GetInt32(5) != 0;
        DateTimeOffset createdAt = ParseIsoTimestamp(r.GetString(6)) ?? DateTimeOffset.UtcNow;
        DateTimeOffset? completedAt = r.IsDBNull(7) ? null : ParseIsoTimestamp(r.GetString(7));
        DateTimeOffset updatedAt = ParseIsoTimestamp(r.GetString(8)) ?? createdAt;

        return new TodoTaskItem(id, title, notes, prio, dueAt, isCompleted, createdAt, completedAt, updatedAt);
    }

    private static ReminderItem ReadReminder(SqliteDataReader r)
    {
        string id = r.GetString(0);
        string? taskId = r.IsDBNull(1) ? null : r.GetString(1);
        string title = r.GetString(2);
        DateTimeOffset triggerAt = ParseIsoTimestamp(r.GetString(3)) ?? DateTimeOffset.UtcNow;
        int prioInt = r.GetInt32(4);
        var prio = Enum.IsDefined((TaskPriority)prioInt) ? (TaskPriority)prioInt : TaskPriority.Normal;
        bool isDelivered = r.GetInt32(5) != 0;
        DateTimeOffset? deliveredAt = r.IsDBNull(6) ? null : ParseIsoTimestamp(r.GetString(6));
        DateTimeOffset createdAt = ParseIsoTimestamp(r.GetString(7)) ?? DateTimeOffset.UtcNow;

        return new ReminderItem(id, title, triggerAt, taskId, prio, isDelivered, deliveredAt, createdAt);
    }

    private static QuickNoteItem ReadNote(SqliteDataReader r)
    {
        string id = r.GetString(0);
        string title = r.GetString(1);
        string content = r.GetString(2);
        DateTimeOffset createdAt = ParseIsoTimestamp(r.GetString(3)) ?? DateTimeOffset.UtcNow;
        DateTimeOffset updatedAt = ParseIsoTimestamp(r.GetString(4)) ?? createdAt;

        return new QuickNoteItem(id, title, content, createdAt, updatedAt);
    }

    private static DateTimeOffset? ParseIsoTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto
            : null;
    }
}
