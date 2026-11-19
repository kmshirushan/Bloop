using Bloop.Core.Productivity;
using Bloop.Storage;
using Xunit;

namespace Bloop.Core.Tests;

public sealed class Milestone7ProductivityTests
{
    [Fact]
    public void M7_1_LauncherSearchMatcher_RanksExactPrefixWordBoundaryAcronymAndRecentsCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new List<LauncherEntry>
        {
            new("app:vscode", "Visual Studio Code", @"C:\Apps\Code.lnk", LauncherEntryKind.Application, "Development"),
            new("app:vs", "Visual Studio 2022", @"C:\Apps\VS.lnk", LauncherEntryKind.Application, "Development"),
            new("app:calc", "Calculator", @"C:\Apps\Calc.lnk", LauncherEntryKind.Application, "System", LastUsedUtc: now.AddMinutes(-5), LaunchCount: 4),
            new("folder:docs", "Project Documents", @"C:\Users\Test\Documents", LauncherEntryKind.Folder, "Recent Folder", LastUsedUtc: now.AddMinutes(-1), LaunchCount: 2)
        };

        // 1. Empty query prioritizes recently used items first
        var defaultList = LauncherSearchMatcher.FilterAndRank(catalog, "");
        Assert.Equal("folder:docs", defaultList[0].Id);
        Assert.Equal("app:calc", defaultList[1].Id);

        // 2. Acronym query "vsc" matches "Visual Studio Code"
        var acronymMatches = LauncherSearchMatcher.FilterAndRank(catalog, "vsc");
        Assert.Single(acronymMatches);
        Assert.Equal("app:vscode", acronymMatches[0].Id);

        // 3. Prefix match "calc" matches "Calculator"
        var prefixMatches = LauncherSearchMatcher.FilterAndRank(catalog, "calc");
        Assert.Single(prefixMatches);
        Assert.Equal("app:calc", prefixMatches[0].Id);

        // 4. Non-matching query returns empty list (no-results state)
        var noResults = LauncherSearchMatcher.FilterAndRank(catalog, "nonexistent_xyz_999");
        Assert.Empty(noResults);
    }

    [Fact]
    public void M7_2_ProductivityRepository_PersistsTasks_SupportsCompletionToggleFilteringAndDeletion()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m72_tasks_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            Assert.Equal(4, db.SchemaVersion);
            var repo = new ProductivityRepository(db);
            var t0 = new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

            var task1 = repo.SaveTask(new TodoTaskItem(
                Id: "",
                Title: "Review Milestone 7 Architecture",
                Notes: "Verify SQLite Migration v4",
                Priority: TaskPriority.High,
                DueAtUtc: t0.AddHours(2),
                CreatedAtUtc: t0,
                UpdatedAtUtc: t0));

            var task2 = repo.SaveTask(new TodoTaskItem(
                Id: "",
                Title: "Clean up temp files",
                Priority: TaskPriority.Low,
                CreatedAtUtc: t0.AddMinutes(1),
                UpdatedAtUtc: t0.AddMinutes(1)));

            Assert.Equal(2, repo.GetTasks(TaskListFilter.All).Count);
            Assert.Equal(2, repo.GetTasks(TaskListFilter.Active).Count);
            Assert.Empty(repo.GetTasks(TaskListFilter.Completed));

            // Edit task title & priority
            var edited = repo.SaveTask(task2 with { Title = "Clean up scratch directory", Priority = TaskPriority.Normal });
            Assert.Equal("Clean up scratch directory", repo.GetTaskById(task2.Id)?.Title);

            // Mark complete
            var completed = repo.SetTaskCompleted(task1.Id, isCompleted: true, t0.AddMinutes(10));
            Assert.True(completed.IsCompleted);
            Assert.NotNull(completed.CompletedAtUtc);
            Assert.Single(repo.GetTasks(TaskListFilter.Active));
            Assert.Single(repo.GetTasks(TaskListFilter.Completed));

            // Reopen completed task
            var reopened = repo.SetTaskCompleted(task1.Id, isCompleted: false, t0.AddMinutes(15));
            Assert.False(reopened.IsCompleted);
            Assert.Null(reopened.CompletedAtUtc);
            Assert.Equal(2, repo.GetTasks(TaskListFilter.Active).Count);

            // Delete task
            Assert.True(repo.DeleteTask(task2.Id));
            Assert.Single(repo.GetTasks(TaskListFilter.All));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void M7_2_CountdownTimerEngine_UsesWallClockTargetTimestamp_AndPersistsAcrossRestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m72_timer_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var repo = new ProductivityRepository(db);
            var t0 = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

            var engine = new CountdownTimerEngine(nowUtc: t0);
            var started = engine.Start(TimeSpan.FromSeconds(120), "Deep Focus Sprint", t0);
            Assert.Equal(CountdownTimerState.Running, started.State);
            Assert.Equal(t0.AddSeconds(120), started.TargetEndUtc);

            // Even with zero intermediate UI ticks (e.g. Bloop sleeping or hidden for 45 seconds),
            // wall-clock evaluation at t0 + 45s yields exact 75s remaining!
            bool doneAt45 = engine.EvaluateTick(t0.AddSeconds(45));
            Assert.False(doneAt45);
            Assert.Equal(75.0, engine.Current.RemainingDuration.TotalSeconds, 1);

            // Pause at t0 + 50s -> 70s remaining
            var paused = engine.Pause(t0.AddSeconds(50));
            Assert.Equal(CountdownTimerState.Paused, paused.State);
            Assert.Equal(70.0, paused.RemainingDuration.TotalSeconds, 1);
            Assert.Null(paused.TargetEndUtc);

            // Resume at t0 + 100s -> new TargetEndUtc is t0 + 170s
            var resumed = engine.Resume(t0.AddSeconds(100));
            Assert.Equal(CountdownTimerState.Running, resumed.State);
            Assert.Equal(t0.AddSeconds(170), resumed.TargetEndUtc);
            repo.SaveTimerSnapshot(resumed);

            // Simulate app restart at t0 + 140s (30s left)
            var loadedSnap = repo.LoadTimerSnapshot();
            var restartedEngine = new CountdownTimerEngine(loadedSnap, nowUtc: t0.AddSeconds(140));
            Assert.Equal(CountdownTimerState.Running, restartedEngine.Current.State);
            Assert.Equal(30.0, restartedEngine.Current.RemainingDuration.TotalSeconds, 1);

            // Complete at t0 + 171s
            bool completedFired = false;
            restartedEngine.TimerCompleted += (_, _) => completedFired = true;
            bool doneAt171 = restartedEngine.EvaluateTick(t0.AddSeconds(171));
            Assert.True(doneAt171);
            Assert.True(completedFired);
            Assert.Equal(CountdownTimerState.Completed, restartedEngine.Current.State);
            Assert.Equal(TimeSpan.Zero, restartedEngine.Current.RemainingDuration);

            // Reset & Cancel
            var reset = restartedEngine.Reset(t0.AddSeconds(180));
            Assert.Equal(CountdownTimerState.Idle, reset.State);
            Assert.Equal(120.0, reset.RemainingDuration.TotalSeconds, 1);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void M7_2_ReminderScheduler_TriggersDueAndMissedReminders_AndPreventsDuplicateDelivery()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m72_reminders_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var repo = new ProductivityRepository(db);
            var t0 = new DateTimeOffset(2026, 11, 18, 11, 0, 0, TimeSpan.Zero);

            // 1. Missed reminder scheduled 10 minutes ago (while app was closed)
            var missedRem = repo.SaveReminder(new ReminderItem(
                Id: "rem-missed",
                Title: "Standup Meeting",
                TriggerAtUtc: t0.AddMinutes(-10),
                Priority: TaskPriority.High,
                CreatedAtUtc: t0.AddMinutes(-30)));

            // 2. Future reminder scheduled 5 minutes from now
            var futureRem = repo.SaveReminder(new ReminderItem(
                Id: "rem-future",
                Title: "Submit PR",
                TriggerAtUtc: t0.AddMinutes(5),
                Priority: TaskPriority.Normal,
                CreatedAtUtc: t0));

            var scheduler = new ReminderScheduler(repo);

            // Startup evaluation recovers missed reminder and leaves future reminder pending
            var startupTriggered = scheduler.EvaluateDueReminders(t0, isStartupRecovery: true);
            Assert.Single(startupTriggered);
            Assert.Equal("rem-missed", startupTriggered[0].Reminder.Id);
            Assert.True(startupTriggered[0].IsMissedRecovery);
            Assert.True(startupTriggered[0].IsPriorityAlert);

            // Evaluating again at t0 + 1m does NOT duplicate-deliver rem-missed
            Assert.Empty(scheduler.EvaluateDueReminders(t0.AddMinutes(1)));

            // At t0 + 5m, future reminder becomes due and triggers once
            var dueTriggered = scheduler.EvaluateDueReminders(t0.AddMinutes(5));
            Assert.Single(dueTriggered);
            Assert.Equal("rem-future", dueTriggered[0].Reminder.Id);
            Assert.False(dueTriggered[0].IsMissedRecovery);

            // Second tick at t0 + 5m10s does NOT re-trigger
            Assert.Empty(scheduler.EvaluateDueReminders(t0.AddMinutes(5).AddSeconds(10)));
            Assert.Empty(repo.GetReminders(includeDelivered: false));
            Assert.Equal(2, repo.GetReminders(includeDelivered: true).Count);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void M7_3_QuickNotes_SupportsCreateEditAutoSaveSearchDelete_AndPreventsDuplicateRecords()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"bloop_m73_notes_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var repo = new ProductivityRepository(db);
            var t0 = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

            // 1. Create note with empty title -> derives clean title from first line of content
            var note1 = repo.SaveNote(new QuickNoteItem(
                Id: "note-alpha",
                Title: "",
                Content: "Liquid Glass Shader Notes\nUse Fresnel power 2.65 and specular 0.72.",
                CreatedAtUtc: t0,
                UpdatedAtUtc: t0));
            Assert.Equal("Liquid Glass Shader Notes", note1.Title);

            // 2. Rapid auto-save updates on the same note ID update in-place without creating duplicate rows
            for (int i = 1; i <= 5; i++)
            {
                _ = repo.SaveNote(note1 with
                {
                    Content = $"Liquid Glass Shader Notes v{i}\nUpdated iteration {i}",
                    UpdatedAtUtc = t0.AddSeconds(i)
                });
            }

            var allNotes = repo.GetNotes();
            Assert.Single(allNotes);
            Assert.Contains("Updated iteration 5", allNotes[0].Content);

            // 3. Create a second note and test search by title and content
            var note2 = repo.SaveNote(new QuickNoteItem(
                Id: "note-beta",
                Title: "Grocery & Errands",
                Content: "Pick up espresso beans and sparkling water.",
                CreatedAtUtc: t0.AddMinutes(1),
                UpdatedAtUtc: t0.AddMinutes(2)));

            Assert.Equal(2, repo.GetNotes().Count);
            Assert.Equal("note-beta", repo.GetNotes()[0].Id); // Ordered by updated_at_utc DESC

            var searchContent = repo.GetNotes("espresso");
            Assert.Single(searchContent);
            Assert.Equal("note-beta", searchContent[0].Id);

            var searchTitle = repo.GetNotes("Shader");
            Assert.Single(searchTitle);
            Assert.Equal("note-alpha", searchTitle[0].Id);

            // 4. Handle empty note gracefully
            var emptyNote = repo.SaveNote(new QuickNoteItem(
                Id: "note-empty",
                Title: "",
                Content: "",
                CreatedAtUtc: t0.AddMinutes(3),
                UpdatedAtUtc: t0.AddMinutes(3)));
            Assert.Equal("Untitled Note", emptyNote.Title);

            // 5. Delete note
            Assert.True(repo.DeleteNote("note-empty"));
            Assert.Equal(2, repo.GetNotes().Count);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void M7_4_FormatDisplayClock_UsesCeilingSeconds_PreventingSubSecondTruncationAcceleration()
    {
        // Starting a 60s timer: after 50ms (59.95s remaining), must still display "01:00" for the full first second!
        Assert.Equal("01:00", CountdownTimerEngine.FormatDisplayClock(TimeSpan.FromSeconds(60.0)));
        Assert.Equal("01:00", CountdownTimerEngine.FormatDisplayClock(TimeSpan.FromSeconds(59.95)));
        Assert.Equal("01:00", CountdownTimerEngine.FormatDisplayClock(TimeSpan.FromSeconds(59.01)));

        // Exactly at 59.00s and down to 58.01s -> displays "00:59"
        Assert.Equal("00:59", CountdownTimerEngine.FormatDisplayClock(TimeSpan.FromSeconds(59.0)));
        Assert.Equal("00:59", CountdownTimerEngine.FormatDisplayClock(TimeSpan.FromSeconds(58.25)));

        // Final second (0.99s down to 0.01s) -> displays "00:01", and 0.00s -> displays "00:00"
        Assert.Equal("00:01", CountdownTimerEngine.FormatDisplayClock(TimeSpan.FromSeconds(0.95)));
        Assert.Equal("00:01", CountdownTimerEngine.FormatDisplayClock(TimeSpan.FromSeconds(0.05)));
        Assert.Equal("00:00", CountdownTimerEngine.FormatDisplayClock(TimeSpan.Zero));
        Assert.Equal("00:00", CountdownTimerEngine.FormatDisplayClock(TimeSpan.FromSeconds(-1.5)));

        // 5-minute countdown intermediate checkpoints
        var t0 = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
        var engine = new CountdownTimerEngine(nowUtc: t0);
        engine.Start(TimeSpan.FromMinutes(5), "5-Min Check", t0);
        Assert.Equal("05:00", CountdownTimerEngine.FormatDisplayClock(engine.Current.RemainingDuration));

        // After 250ms (first 4Hz refresh), display must STILL be 05:00, not 04:59!
        engine.EvaluateTick(t0.AddMilliseconds(250));
        Assert.Equal("05:00", CountdownTimerEngine.FormatDisplayClock(engine.Current.RemainingDuration));

        // After 1.00s -> 04:59
        engine.EvaluateTick(t0.AddSeconds(1));
        Assert.Equal("04:59", CountdownTimerEngine.FormatDisplayClock(engine.Current.RemainingDuration));

        // After 150.0s -> 02:30
        engine.EvaluateTick(t0.AddSeconds(150));
        Assert.Equal("02:30", CountdownTimerEngine.FormatDisplayClock(engine.Current.RemainingDuration));
    }

    [Fact]
    public void M7_4_PanelPlacementCalculator_PositionsLiquidGlassToastBesideMascot_AndRespectsMultiMonitorWorkArea()
    {
        var workArea = new Bloop.Core.Movement.WorkAreaBounds(0, 0, 1920, 1040, "primary");

        // 1. Mascot docked on Right edge -> toast bubble placed cleanly to the left of Bloop
        var (leftR, topR, offsetR) = Bloop.Core.Notifications.PanelPlacementCalculator.CalculateToastBubblePosition(
            mascotLeftDip: 1660,
            mascotTopDip: 700,
            mascotWidthDip: 260,
            mascotHeightDip: 260,
            toastWidthDip: 334,
            toastHeightDip: 112,
            workArea);
        Assert.Equal(1660 - 334 - 10, leftR);
        Assert.True(offsetR > 0);
        Assert.InRange(topR, workArea.Top + 10, workArea.Bottom - 112 - 10);

        // 2. Mascot docked on Left edge of a negative-coordinate secondary monitor (-1920..0) -> toast placed to the right of Bloop
        var negativeMonitor = new Bloop.Core.Movement.WorkAreaBounds(-1920, 0, -1920 + 1920, 1040, "\\\\.\\DISPLAY2");
        var (leftL, topL, offsetL) = Bloop.Core.Notifications.PanelPlacementCalculator.CalculateToastBubblePosition(
            mascotLeftDip: -1920,
            mascotTopDip: 200,
            mascotWidthDip: 260,
            mascotHeightDip: 260,
            toastWidthDip: 334,
            toastHeightDip: 112,
            negativeMonitor);
        Assert.Equal(-1920 + 260 + 10, leftL);
        Assert.True(offsetL < 0);
        Assert.InRange(topL, negativeMonitor.Top + 10, negativeMonitor.Bottom - 112 - 10);
    }
}

