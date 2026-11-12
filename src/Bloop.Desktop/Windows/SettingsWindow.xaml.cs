using System.Globalization;
using System.Windows;
using Bloop.Core.Interfaces;
using Bloop.Core.Movement;
using Bloop.Desktop.Services;

namespace Bloop.Desktop.Windows;

public partial class SettingsWindow : Window
{
    private readonly MascotWindow _mascotWindow;
    private readonly WindowService _windowService;
    private readonly IMovementController _movementController;
    private readonly ISettingsRepository _settingsRepository;
    private bool _isLoadingUi = true;

    public SettingsWindow(
        MascotWindow mascotWindow,
        WindowService windowService,
        IMovementController movementController,
        ISettingsRepository settingsRepository)
    {
        InitializeComponent();
        _mascotWindow = mascotWindow;
        _windowService = windowService;
        _movementController = movementController;
        _settingsRepository = settingsRepository;

        LoadControlsFromCurrentSettings();
        _isLoadingUi = false;
    }

    private void LoadControlsFromCurrentSettings()
    {
        _isLoadingUi = true;
        var s = _movementController.Settings;

        AlwaysOnTopCheckBox.IsChecked = _mascotWindow.Topmost;
        EdgeDockingCheckBox.IsChecked = s.EdgeDockingEnabled;
        AutonomousMovementCheckBox.IsChecked = s.AutonomousMovementEnabled;
        DockedVisibilitySlider.Value = Math.Round(s.DockedVisibleRatio * 100.0);
        AutonomyCooldownSlider.Value = s.AutonomyCooldownMinutes;
        MovementSpeedSlider.Value = s.MovementSpeedMultiplier;

        UpdateLabels();
        _isLoadingUi = false;
    }

    private void UpdateLabels()
    {
        if (VisibilityValueText is not null)
            VisibilityValueText.Text = $"{DockedVisibilitySlider.Value:0}%";
        if (FrequencyValueText is not null)
            FrequencyValueText.Text = $"{AutonomyCooldownSlider.Value:0.0} min";
        if (SpeedValueText is not null)
            SpeedValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{MovementSpeedSlider.Value:0.0}x");
    }

    private void ApplyAndPersistSettings()
    {
        if (_isLoadingUi) return;

        _mascotWindow.Topmost = AlwaysOnTopCheckBox.IsChecked == true;

        double cooldown = Math.Clamp(AutonomyCooldownSlider.Value, 1.0, 15.0);
        var updated = _movementController.Settings with
        {
            EdgeDockingEnabled = EdgeDockingCheckBox.IsChecked == true,
            AutonomousMovementEnabled = AutonomousMovementCheckBox.IsChecked == true,
            DockedVisibleRatio = Math.Clamp(DockedVisibilitySlider.Value / 100.0, 0.35, 0.90),
            AutonomyCooldownMinutes = cooldown,
            MinAutonomyIntervalMinutes = Math.Max(1.0, cooldown * 0.6),
            MaxAutonomyIntervalMinutes = Math.Max(2.0, cooldown * 1.6),
            MovementSpeedMultiplier = Math.Clamp(MovementSpeedSlider.Value, 0.5, 2.0)
        };

        _movementController.UpdateSettings(updated);
        _settingsRepository.SaveMovementSettings(updated);
        _windowService.SavePosition(_mascotWindow);
        _mascotWindow.SyncDockingConfigToRenderer();
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        ApplyAndPersistSettings();
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        ApplyAndPersistSettings();
    }

    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        var defaults = MovementSettings.Default;
        _movementController.UpdateSettings(defaults);
        _settingsRepository.SaveMovementSettings(defaults);
        LoadControlsFromCurrentSettings();
        _mascotWindow.SyncDockingConfigToRenderer();
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        _windowService.PlaceAtBottomRight(_mascotWindow);
        _mascotWindow.SyncDockingConfigToRenderer();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
