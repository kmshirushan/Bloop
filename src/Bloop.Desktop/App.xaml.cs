using System.Windows;
using Bloop.Core.Awareness;
using Bloop.Core.Behaviours;
using Bloop.Core.Movement;
using Bloop.Core.Notifications;
using Bloop.Core.Personality;
using Bloop.Core.Productivity;
using Bloop.Desktop.Awareness;
using Bloop.Desktop.Services;
using Bloop.Desktop.Windows;
using Bloop.Storage;
using Bloop.Windows.Awareness;
using Bloop.Windows.Notifications;
using Bloop.Windows.Productivity;

namespace Bloop.Desktop;

public partial class App : System.Windows.Application
{
    private DatabaseService? _databaseService;
    private string? _tempVerifyDbPath;
    private NotificationService? _notificationService;
    private AdaptiveNotificationChangeSignal? _notificationChangeSignal;
    private NotificationSyncCoordinator? _notificationCoordinator;
    private WindowsAwarenessProvider? _awarenessProvider;
    private AwarenessCoordinator? _awarenessCoordinator;
    private MascotWindow? _mascotWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool verifyNaturalAutonomy = e.Args.Any(a => string.Equals(a, "--verify-natural-autonomy", StringComparison.OrdinalIgnoreCase));
        bool verifyM4Runtime = e.Args.Any(a => string.Equals(a, "--verify-m4-runtime", StringComparison.OrdinalIgnoreCase));
        bool verifyM5Runtime = e.Args.Any(a => string.Equals(a, "--verify-m5-runtime", StringComparison.OrdinalIgnoreCase));
        bool verifyM51Runtime = e.Args.Any(a => string.Equals(a, "--verify-m5-1-runtime", StringComparison.OrdinalIgnoreCase));
        bool verifyM6Runtime = e.Args.Any(a => string.Equals(a, "--verify-m6-runtime", StringComparison.OrdinalIgnoreCase));
        bool verifyM61M7Runtime = e.Args.Any(a => string.Equals(a, "--verify-m61-m7-runtime", StringComparison.OrdinalIgnoreCase));
        bool verifyM74Runtime = e.Args.Any(a => string.Equals(a, "--verify-m7-4-runtime", StringComparison.OrdinalIgnoreCase));
        string? m5ScreenshotDir = e.Args
            .FirstOrDefault(a => a.StartsWith("--m5-screenshots-dir=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1];

        if (verifyM51Runtime || verifyM6Runtime || verifyM61M7Runtime || verifyM74Runtime)
        {
            _tempVerifyDbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bloop_verify_{Guid.NewGuid():N}.db");
        }

        _databaseService = new DatabaseService(_tempVerifyDbPath);
        var settingsRepo = new SettingsRepository(_databaseService);
        var notificationRepo = new NotificationRepository(_databaseService);
        var appearanceRepo = new AppearanceRepository(_databaseService);
        var productivityRepo = new ProductivityRepository(_databaseService);
        var launcherService = new WindowsLauncherCatalogService(settingsRepo);
        var timerEngine = new CountdownTimerEngine(productivityRepo.LoadTimerSnapshot(), DateTimeOffset.UtcNow);
        var reminderScheduler = new ReminderScheduler(productivityRepo);
        var diagnostics = new DiagnosticsLogService();

        launcherService.DiagnosticLogged += (_, d) => diagnostics.RecordCustom(d.EventName, d.Summary);
        timerEngine.DiagnosticLogged += (_, d) => diagnostics.RecordCustom(d.EventName, d.Summary);
        reminderScheduler.DiagnosticLogged += (_, d) => diagnostics.RecordCustom(d.EventName, d.Summary);

        var personalityEngine = new PersonalityEngine();
        var movementController = new MovementController();
        var movementSettings = settingsRepo.LoadMovementSettings();
        if (verifyNaturalAutonomy)
        {
            movementSettings = movementSettings with
            {
                AutonomousMovementEnabled = true,
                MinAutonomyIntervalMinutes = 0.06,
                MaxAutonomyIntervalMinutes = 0.07,
                AutonomyCooldownMinutes = 0.05
            };
        }
        movementController.UpdateSettings(movementSettings);
        diagnostics.RecordCustom("SchedulerInitialized", $"Autonomy scheduler initialized (NextDue={movementController.NextAutonomyDueUtc.ToLocalTime():HH:mm:ss.fff}, Interval={movementController.Settings.MinAutonomyIntervalMinutes:F2}-{movementController.Settings.MaxAutonomyIntervalMinutes:F2}m)");

        var behaviourScheduler = new BehaviourScheduler(movementController, personalityEngine);
        var behaviourSettings = settingsRepo.LoadBehaviourSettings();
        if (verifyNaturalAutonomy)
        {
            behaviourSettings = behaviourSettings with
            {
                GlobalBehavioursPaused = false,
                AutonomousEdgeHoppingEnabled = true,
                WindowBoundaryExplorationEnabled = false,
                CornerExplorationEnabled = false
            };
        }
        behaviourScheduler.UpdateSettings(behaviourSettings);

        _notificationService = new NotificationService();
        _notificationChangeSignal = new AdaptiveNotificationChangeSignal();
        var notificationReactionScheduler = new NotificationReactionScheduler();
        _notificationCoordinator = new NotificationSyncCoordinator(
            _notificationService,
            _notificationChangeSignal,
            notificationRepo,
            notificationReactionScheduler);

        var webViewService = new WebViewService();
        var windowService = new WindowService(settingsRepo, personalityEngine, movementController, behaviourScheduler);

        windowService.InitializeSystemTray();

        _awarenessProvider = new WindowsAwarenessProvider();
        var policyResolver = new ContextPolicyResolver();
        _awarenessCoordinator = new AwarenessCoordinator(
            _awarenessProvider,
            policyResolver,
            movementController,
            behaviourScheduler,
            settingsRepo,
            diagnostics);

        _mascotWindow = new MascotWindow(
            webViewService,
            windowService,
            personalityEngine,
            movementController,
            behaviourScheduler,
            settingsRepo,
            _notificationCoordinator,
            notificationRepo,
            notificationReactionScheduler,
            appearanceRepo,
            _awarenessCoordinator,
            diagnostics,
            launcherService,
            productivityRepo,
            timerEngine,
            reminderScheduler
        );
        _mascotWindow.Show();

        if (e.Args.Any(a => string.Equals(a, "--dev-hop-test", StringComparison.OrdinalIgnoreCase)))
        {
            _mascotWindow.TriggerDevAutonomousHop(exitOnComplete: true);
        }
        else if (verifyNaturalAutonomy)
        {
            _mascotWindow.EnableExitOnNextNaturalHopCompletion();
        }
        else if (verifyM4Runtime)
        {
            _ = _mascotWindow.RunMilestone4VerificationSequenceAsync();
        }
        else if (verifyM5Runtime)
        {
            _ = _mascotWindow.RunMilestone5VerificationSequenceAsync(m5ScreenshotDir);
        }
        else if (verifyM51Runtime)
        {
            _ = _mascotWindow.RunMilestone5_1VerificationSequenceAsync(m5ScreenshotDir);
        }
        else if (verifyM6Runtime)
        {
            _ = _mascotWindow.RunMilestone6VerificationSequenceAsync(m5ScreenshotDir);
        }
        else if (verifyM61M7Runtime)
        {
            _ = _mascotWindow.RunMilestone6_1And7VerificationSequenceAsync(m5ScreenshotDir);
        }
        else if (verifyM74Runtime)
        {
            _ = _mascotWindow.RunMilestone7_4VerificationSequenceAsync(m5ScreenshotDir);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _awarenessCoordinator?.Dispose();
        _awarenessProvider?.Dispose();
        _notificationCoordinator?.Dispose();
        _notificationChangeSignal?.Dispose();
        _notificationService?.Dispose();
        _databaseService?.Dispose();
        if (!string.IsNullOrWhiteSpace(_tempVerifyDbPath) && System.IO.File.Exists(_tempVerifyDbPath))
        {
            try { System.IO.File.Delete(_tempVerifyDbPath); } catch { }
        }
        base.OnExit(e);
    }
}
