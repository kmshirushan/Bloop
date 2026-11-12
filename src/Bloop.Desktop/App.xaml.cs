using System.Windows;
using Bloop.Core.Movement;
using Bloop.Core.Personality;
using Bloop.Desktop.Services;
using Bloop.Desktop.Windows;
using Bloop.Storage;

namespace Bloop.Desktop;

public partial class App : System.Windows.Application
{
    private DatabaseService? _databaseService;
    private MascotWindow? _mascotWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _databaseService = new DatabaseService();
        var settingsRepo = new SettingsRepository(_databaseService);
        var personalityEngine = new PersonalityEngine();
        var movementController = new MovementController();
        movementController.UpdateSettings(settingsRepo.LoadMovementSettings());

        var webViewService = new WebViewService();
        var windowService = new WindowService(settingsRepo, personalityEngine, movementController);

        windowService.InitializeSystemTray();

        _mascotWindow = new MascotWindow(
            webViewService,
            windowService,
            personalityEngine,
            movementController,
            settingsRepo
        );
        _mascotWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _databaseService?.Dispose();
        base.OnExit(e);
    }
}
