using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Bloop.Core.Appearance;
using Bloop.Core.Awareness;
using Bloop.Core.Behaviours;
using Bloop.Core.Interfaces;
using Bloop.Core.Movement;
using Bloop.Core.Notifications;
using Bloop.Desktop.Appearance;
using Bloop.Desktop.Awareness;
using Bloop.Desktop.Services;
using Bloop.Windows.System;

namespace Bloop.Desktop.Windows;

public partial class SettingsWindow : Window
{
    private readonly MascotWindow _mascotWindow;
    private readonly WindowService _windowService;
    private readonly IMovementController _movementController;
    private readonly BehaviourScheduler _behaviourScheduler;
    private readonly ISettingsRepository _settingsRepository;
    private readonly NotificationSyncCoordinator _notificationCoordinator;
    private readonly INotificationRepository _notificationRepository;
    private readonly IAppearanceRepository _appearanceRepository;
    private readonly AwarenessCoordinator _awarenessCoordinator;

    private IntPtr _hwnd;
    private bool _isLoadingUi = true;
    private bool _hasUncommittedPreview;
    private bool _isDwmBackdropActive = true;

    public SettingsWindow(
        MascotWindow mascotWindow,
        WindowService windowService,
        IMovementController movementController,
        BehaviourScheduler behaviourScheduler,
        ISettingsRepository settingsRepository,
        NotificationSyncCoordinator notificationCoordinator,
        INotificationRepository notificationRepository,
        IAppearanceRepository appearanceRepository,
        AwarenessCoordinator awarenessCoordinator)
    {
        InitializeComponent();
        _mascotWindow = mascotWindow;
        _windowService = windowService;
        _movementController = movementController;
        _behaviourScheduler = behaviourScheduler;
        _settingsRepository = settingsRepository;
        _notificationCoordinator = notificationCoordinator;
        _notificationRepository = notificationRepository;
        _appearanceRepository = appearanceRepository;
        _awarenessCoordinator = awarenessCoordinator;

        CharacterPresetComboBox.ItemsSource = Enum.GetValues<CharacterMaterialPreset>();
        EyeStyleComboBox.ItemsSource = Enum.GetValues<EyeStyle>();
        PanelThemeComboBox.ItemsSource = Enum.GetValues<PanelThemePreset>();
        CardDensityComboBox.ItemsSource = Enum.GetValues<CardDensity>();

        SourceInitialized += OnWindowSourceInitialized;
        Closed += (_, _) =>
        {
            if (_hasUncommittedPreview)
            {
                _mascotWindow.CancelAppearancePreview();
                _hasUncommittedPreview = false;
            }
        };

        LoadControlsFromCurrentSettings();
        _isLoadingUi = false;
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd != IntPtr.Zero)
        {
            var source = HwndSource.FromHwnd(_hwnd);
            if (source?.CompositionTarget is not null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
            ApplySettingsWindowBackdrop(_appearanceRepository.GetActiveProfile());
        }
    }

    public void ApplySettingsWindowBackdrop(AppearanceProfile profile)
    {
        bool isLight = profile.Panel.Theme == PanelThemePreset.Light;
        bool wantAcrylic = profile.Panel.Theme is PanelThemePreset.Glass or PanelThemePreset.LiquidGlass ||
                           profile.Panel.EnableGlassBlur ||
                           profile.Panel.BackgroundOpacity < 0.98;

        if (_hwnd != IntPtr.Zero)
        {
            var (active, summary) = NativeMethods.ApplyGlassWindowBackdrop(
                _hwnd,
                enableAcrylicBackdrop: wantAcrylic,
                isLightTheme: isLight,
                isToolWindow: false,
                isNoActivate: false);
            _isDwmBackdropActive = active;
            if (DwmStatusBadgeText is not null)
            {
                DwmStatusBadgeText.Text = active ? "DWM Acrylic Active" : "Layered Glass Surface";
            }
            _ = summary;
        }

        GlassDesignSystem.ApplyToApplicationResources(profile.Panel, profile.Character, _isDwmBackdropActive);
        if (TitleBarProfileBadgeText is not null)
        {
            TitleBarProfileBadgeText.Text = profile.Name;
        }
    }

    public void ScrollToFraction(double fraction)
    {
        UpdateLayout();
        SettingsScrollViewer.UpdateLayout();
        double max = Math.Max(0, SettingsScrollViewer.ScrollableHeight);
        SettingsScrollViewer.ScrollToVerticalOffset(Math.Clamp(fraction, 0.0, 1.0) * max);
        UpdateLayout();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnMaximizeRestoreClick(sender, e);
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
            }
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeRestoreButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void LoadControlsFromCurrentSettings()
    {
        _isLoadingUi = true;
        var m = _movementController.Settings;
        var b = _behaviourScheduler.Settings;
        var n = _notificationCoordinator.Settings;
        var a = _awarenessCoordinator.Settings;

        DesktopAwarenessEnabledCheckBox.IsChecked = a.IsEnabled;
        FullScreenDetectionCheckBox.IsChecked = a.FullScreenDetectionEnabled;
        AutoHideFullScreenCheckBox.IsChecked = a.AutoHideOnFullScreen;
        CatchUpAfterFullScreenCheckBox.IsChecked = a.CatchUpNotificationReactionAfterFullScreen;
        ManualFocusModeCheckBox.IsChecked = a.ManualFocusModeEnabled;
        AutoFocusInferenceCheckBox.IsChecked = a.AutoFocusInferenceEnabled;
        SuppressRandomClickInFocusCheckBox.IsChecked = a.SuppressRandomClickInFocus;
        InactivityDetectionCheckBox.IsChecked = a.InactivityDetectionEnabled;
        AutoQuietModeCheckBox.IsChecked = a.AutoQuietModeEnabled;
        SessionPowerAwarenessCheckBox.IsChecked = a.SessionAwarenessEnabled && a.PowerAwarenessEnabled;
        FullScreenRestoreDelaySlider.Value = a.FullScreenRestoreDelayMs;
        QuietThresholdSlider.Value = a.QuietThresholdSeconds;
        AwayThresholdSlider.Value = a.AwayThresholdSeconds;
        FullScreenExclusionsTextBox.Text = a.FullScreenExcludedProcessesCsv;
        AwarenessAppRulesTextBox.Text = AwarenessValidator.FormatAppRules(a.AppRules);
        if (AwarenessLiveBadgeText is not null)
        {
            AwarenessLiveBadgeText.Text = $"Context: {_awarenessCoordinator.CurrentSnapshot.Context}";
        }

        AlwaysOnTopCheckBox.IsChecked = _mascotWindow.Topmost;
        NotificationIntegrationCheckBox.IsChecked = n.IntegrationEnabled;
        StoreLocalHistoryCheckBox.IsChecked = n.StoreLocalHistoryEnabled;
        NotificationReactionsCheckBox.IsChecked = b.NotificationReactionsEnabled && n.ReactionsEnabled;
        HighImportanceEntranceCheckBox.IsChecked = n.HighImportanceCenterEntranceEnabled;
        HighPriorityKeywordsTextBox.Text = n.HighPriorityKeywordsCsv;
        AppPriorityRulesTextBox.Text = FormatAppRules(n.AppPriorityRules);

        GlobalPauseCheckBox.IsChecked = b.GlobalBehavioursPaused;
        EdgeDockingCheckBox.IsChecked = m.EdgeDockingEnabled;
        AutonomousMovementCheckBox.IsChecked = b.AutonomousEdgeHoppingEnabled;
        CornerExplorationCheckBox.IsChecked = b.CornerExplorationEnabled;
        WindowExplorationCheckBox.IsChecked = b.WindowBoundaryExplorationEnabled;
        PeekabooCheckBox.IsChecked = b.PeekabooEnabled;
        EdgeBalancingCheckBox.IsChecked = b.EdgeBalancingEnabled;
        CursorChasingCheckBox.IsChecked = b.CursorChasingEnabled;
        AttentionSeekingCheckBox.IsChecked = b.AttentionSeekingEnabled;
        SleepModeCheckBox.IsChecked = b.SleepModeEnabled;
        WelcomeBackCheckBox.IsChecked = b.WelcomeBackReactionEnabled;
        RandomClickCheckBox.IsChecked = b.RandomClickReactionsEnabled;
        RepeatedClickAnnoyanceCheckBox.IsChecked = b.RepeatedClickAnnoyanceEnabled;

        DockedVisibilitySlider.Value = Math.Round(m.DockedVisibleRatio * 100.0);
        AutonomyCooldownSlider.Value = m.AutonomyCooldownMinutes;
        MovementSpeedSlider.Value = m.MovementSpeedMultiplier;
        AnnoyanceThresholdSlider.Value = b.AnnoyancePeakThreshold;

        ReloadProfileListAndAppearanceControls();
        UpdateLabels();
        _isLoadingUi = false;
    }

    private void ReloadProfileListAndAppearanceControls()
    {
        bool prevLoading = _isLoadingUi;
        _isLoadingUi = true;
        try
        {
            var profiles = _appearanceRepository.GetProfiles();
            var active = _appearanceRepository.GetActiveProfile();

            ProfileComboBox.ItemsSource = profiles;
            ProfileComboBox.SelectedValue = active.ProfileId;
            ProfileNameTextBox.Text = active.Name;
            if (TitleBarProfileBadgeText is not null)
            {
                TitleBarProfileBadgeText.Text = active.Name;
            }

            PopulateAppearanceControlsFromProfile(active);
            ApplySettingsWindowBackdrop(active);
        }
        finally
        {
            _isLoadingUi = prevLoading;
        }
    }

    private void PopulateAppearanceControlsFromProfile(AppearanceProfile profile)
    {
        var c = profile.Character;
        var p = profile.Panel;

        CharacterPresetComboBox.SelectedItem = c.Preset;
        EyeStyleComboBox.SelectedItem = c.EyeStyle;
        BodyColorTextBox.Text = c.BodyColor;
        AccentColorTextBox.Text = c.AccentColor;
        CharacterScaleSlider.Value = c.CharacterScale;
        RoughnessSlider.Value = c.Roughness;
        MetalnessSlider.Value = c.Metalness;
        OpacitySlider.Value = c.Opacity;
        EnableGlowCheckBox.IsChecked = c.EnableGlow;
        GlowIntensitySlider.Value = c.GlowIntensity;
        AccessoryTintTextBox.Text = c.AccessoryTintColor;

        var equipped = new HashSet<string>(c.EquippedAccessories ?? [], StringComparer.OrdinalIgnoreCase);
        AccWizardHatCheckBox.IsChecked = equipped.Contains("wizard_hat");
        AccCapCheckBox.IsChecked = equipped.Contains("cap");
        AccCrownCheckBox.IsChecked = equipped.Contains("crown");
        AccHeadphonesCheckBox.IsChecked = equipped.Contains("headphones");
        AccSunglassesCheckBox.IsChecked = equipped.Contains("sunglasses");
        AccGlassesCheckBox.IsChecked = equipped.Contains("glasses");
        AccScarfCheckBox.IsChecked = equipped.Contains("scarf");
        AccWingsCheckBox.IsChecked = equipped.Contains("wings");

        PanelThemeComboBox.SelectedItem = p.Theme;
        CardDensityComboBox.SelectedItem = p.CardDensity;
        SyncPanelAccentCheckBox.IsChecked = p.SynchronizeAccent;
        PanelBgColorTextBox.Text = p.BackgroundColor;
        PanelAccentColorTextBox.Text = p.AccentColor;
        PanelBgOpacitySlider.Value = p.BackgroundOpacity;
        PanelCardOpacitySlider.Value = p.CardOpacity;
        PanelCornerRadiusSlider.Value = p.CornerRadius;

        UpdateAppearanceValueLabels();
    }

    private AppearanceProfile BuildAppearanceProfileFromUi()
    {
        var active = _appearanceRepository.GetActiveProfile();
        var equipped = new List<string>();
        if (AccWizardHatCheckBox.IsChecked == true) equipped.Add("wizard_hat");
        if (AccCapCheckBox.IsChecked == true) equipped.Add("cap");
        if (AccCrownCheckBox.IsChecked == true) equipped.Add("crown");
        if (AccHeadphonesCheckBox.IsChecked == true) equipped.Add("headphones");
        if (AccSunglassesCheckBox.IsChecked == true) equipped.Add("sunglasses");
        if (AccGlassesCheckBox.IsChecked == true) equipped.Add("glasses");
        if (AccScarfCheckBox.IsChecked == true) equipped.Add("scarf");
        if (AccWingsCheckBox.IsChecked == true) equipped.Add("wings");

        var preset = CharacterPresetComboBox.SelectedItem is CharacterMaterialPreset cp
            ? cp
            : CharacterMaterialPreset.Classic;
        var eyeStyle = EyeStyleComboBox.SelectedItem is EyeStyle es
            ? es
            : EyeStyle.Classic;

        var character = new CharacterAppearance(
            Preset: preset,
            BodyColor: BodyColorTextBox.Text?.Trim() ?? "#0284C7",
            AccentColor: AccentColorTextBox.Text?.Trim() ?? "#38BDF8",
            EyeStyle: eyeStyle,
            Roughness: RoughnessSlider.Value,
            Metalness: MetalnessSlider.Value,
            Opacity: OpacitySlider.Value,
            EnableGlow: EnableGlowCheckBox.IsChecked == true,
            GlowIntensity: GlowIntensitySlider.Value,
            CharacterScale: CharacterScaleSlider.Value,
            EquippedAccessories: equipped,
            AccessoryTintColor: AccessoryTintTextBox.Text?.Trim() ?? "#FACC15"
        );

        var panelTheme = PanelThemeComboBox.SelectedItem is PanelThemePreset pt
            ? pt
            : PanelThemePreset.Dark;
        var cardDensity = CardDensityComboBox.SelectedItem is CardDensity cd
            ? cd
            : CardDensity.Comfortable;

        var panel = new PanelAppearance(
            Theme: panelTheme,
            BackgroundColor: PanelBgColorTextBox.Text?.Trim() ?? "#0F172A",
            BackgroundOpacity: PanelBgOpacitySlider.Value,
            AccentColor: PanelAccentColorTextBox.Text?.Trim() ?? character.AccentColor,
            CardOpacity: PanelCardOpacitySlider.Value,
            CornerRadius: PanelCornerRadiusSlider.Value,
            CardDensity: cardDensity,
            EnableGlassBlur: panelTheme is PanelThemePreset.Glass or PanelThemePreset.LiquidGlass,
            SynchronizeAccent: SyncPanelAccentCheckBox.IsChecked == true
        );

        return AppearanceValidator.Normalize(active with
        {
            Name = string.IsNullOrWhiteSpace(ProfileNameTextBox.Text) ? active.Name : ProfileNameTextBox.Text.Trim(),
            Character = character,
            Panel = panel,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private void UpdateAppearanceValueLabels()
    {
        if (CharacterScaleValueText is not null)
            CharacterScaleValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{CharacterScaleSlider.Value:0.00}x");
        if (RoughnessValueText is not null)
            RoughnessValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{RoughnessSlider.Value:0.00}");
        if (MetalnessValueText is not null)
            MetalnessValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{MetalnessSlider.Value:0.00}");
        if (OpacityValueText is not null)
            OpacityValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{OpacitySlider.Value:0.00}");
        if (GlowIntensityValueText is not null)
            GlowIntensityValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{GlowIntensitySlider.Value:0.00}");
        if (PanelBgOpacityValueText is not null)
            PanelBgOpacityValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{PanelBgOpacitySlider.Value:0.00}");
        if (PanelCardOpacityValueText is not null)
            PanelCardOpacityValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{PanelCardOpacitySlider.Value:0.00}");
        if (PanelCornerRadiusValueText is not null)
            PanelCornerRadiusValueText.Text = $"{PanelCornerRadiusSlider.Value:0} px";
    }

    private void TriggerLiveAppearancePreview()
    {
        if (_isLoadingUi) return;
        var previewProfile = BuildAppearanceProfileFromUi();
        _hasUncommittedPreview = true;
        ApplySettingsWindowBackdrop(previewProfile);
        _mascotWindow.ApplyAppearanceProfile(previewProfile, isPreview: true);
    }

    private void CommitAndPersistAppearance()
    {
        if (_isLoadingUi) return;
        var updatedProfile = BuildAppearanceProfileFromUi();
        var saved = _appearanceRepository.SaveProfile(updatedProfile, setActive: true);
        _hasUncommittedPreview = false;
        ApplySettingsWindowBackdrop(saved);
        _mascotWindow.ApplyAppearanceProfile(saved, isPreview: false);
        ReloadProfileListAndAppearanceControls();
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi) return;
        if (ProfileComboBox.SelectedValue is string profileId && !string.IsNullOrWhiteSpace(profileId))
        {
            var activated = _appearanceRepository.SetActiveProfile(profileId);
            _hasUncommittedPreview = false;
            ApplySettingsWindowBackdrop(activated);
            _mascotWindow.ApplyAppearanceProfile(activated, isPreview: false);
            ReloadProfileListAndAppearanceControls();
        }
    }

    private void OnCreateProfileClick(object sender, RoutedEventArgs e)
    {
        var current = BuildAppearanceProfileFromUi();
        string desiredName = string.IsNullOrWhiteSpace(ProfileNameTextBox.Text) ||
                             string.Equals(ProfileNameTextBox.Text.Trim(), _appearanceRepository.GetActiveProfile().Name, StringComparison.OrdinalIgnoreCase)
            ? $"Custom {DateTime.Now:HHmmss}"
            : ProfileNameTextBox.Text.Trim();

        var created = _appearanceRepository.CreateProfile(desiredName, current.Character, current.Panel, setActive: true);
        _hasUncommittedPreview = false;
        ApplySettingsWindowBackdrop(created);
        _mascotWindow.ApplyAppearanceProfile(created, isPreview: false);
        ReloadProfileListAndAppearanceControls();
    }

    private void OnDuplicateProfileClick(object sender, RoutedEventArgs e)
    {
        var active = _appearanceRepository.GetActiveProfile();
        var duplicated = _appearanceRepository.DuplicateProfile(active.ProfileId, $"{active.Name} Copy", setActive: true);
        _hasUncommittedPreview = false;
        ApplySettingsWindowBackdrop(duplicated);
        _mascotWindow.ApplyAppearanceProfile(duplicated, isPreview: false);
        ReloadProfileListAndAppearanceControls();
    }

    private void OnRenameProfileClick(object sender, RoutedEventArgs e)
    {
        var active = _appearanceRepository.GetActiveProfile();
        var newName = ProfileNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(newName)) return;
        var renamed = _appearanceRepository.RenameProfile(active.ProfileId, newName);
        ApplySettingsWindowBackdrop(renamed);
        _mascotWindow.ApplyAppearanceProfile(renamed, isPreview: false);
        ReloadProfileListAndAppearanceControls();
    }

    private void OnResetProfileClick(object sender, RoutedEventArgs e)
    {
        var active = _appearanceRepository.GetActiveProfile();
        var reset = _appearanceRepository.ResetProfileToDefaults(active.ProfileId);
        _hasUncommittedPreview = false;
        ApplySettingsWindowBackdrop(reset);
        _mascotWindow.ApplyAppearanceProfile(reset, isPreview: false);
        ReloadProfileListAndAppearanceControls();
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        var active = _appearanceRepository.GetActiveProfile();
        if (_appearanceRepository.DeleteProfile(active.ProfileId))
        {
            var nextActive = _appearanceRepository.GetActiveProfile();
            _hasUncommittedPreview = false;
            ApplySettingsWindowBackdrop(nextActive);
            _mascotWindow.ApplyAppearanceProfile(nextActive, isPreview: false);
            ReloadProfileListAndAppearanceControls();
        }
    }

    private void OnCharacterPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi) return;
        if (CharacterPresetComboBox.SelectedItem is CharacterMaterialPreset preset)
        {
            var current = BuildAppearanceProfileFromUi();
            var updatedChar = AppearancePresets.ApplyCharacterPreset(current.Character, preset);
            _isLoadingUi = true;
            PopulateAppearanceControlsFromProfile(current with { Character = updatedChar });
            _isLoadingUi = false;
            TriggerLiveAppearancePreview();
        }
    }

    private void OnPanelThemePresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingUi) return;
        if (PanelThemeComboBox.SelectedItem is PanelThemePreset theme)
        {
            var current = BuildAppearanceProfileFromUi();
            var updatedPanel = AppearancePresets.ApplyPanelPreset(current.Panel, theme);
            _isLoadingUi = true;
            PopulateAppearanceControlsFromProfile(current with { Panel = updatedPanel });
            _isLoadingUi = false;
            TriggerLiveAppearancePreview();
        }
    }

    private void OnQuickSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string tag })
        {
            var parts = tag.Split(',', 2);
            if (parts.Length == 2)
            {
                _isLoadingUi = true;
                BodyColorTextBox.Text = parts[0];
                AccentColorTextBox.Text = parts[1];
                if (SyncPanelAccentCheckBox.IsChecked == true)
                {
                    PanelAccentColorTextBox.Text = parts[1];
                }
                _isLoadingUi = false;
                TriggerLiveAppearancePreview();
            }
        }
    }

    private void OnAccessoryCheckChanged(object sender, RoutedEventArgs e) => TriggerLiveAppearancePreview();
    private void OnAppearanceControlChanged(object sender, RoutedEventArgs e) => TriggerLiveAppearancePreview();
    private void OnAppearanceTextChanged(object sender, TextChangedEventArgs e) => TriggerLiveAppearancePreview();

    private void OnAppearanceSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateAppearanceValueLabels();
        TriggerLiveAppearancePreview();
    }

    private void OnPreviewAppearanceClick(object sender, RoutedEventArgs e) => TriggerLiveAppearancePreview();
    private void OnApplyAndSaveAppearanceClick(object sender, RoutedEventArgs e) => CommitAndPersistAppearance();

    private void OnCancelAppearancePreviewClick(object sender, RoutedEventArgs e)
    {
        _hasUncommittedPreview = false;
        _mascotWindow.CancelAppearancePreview();
        ReloadProfileListAndAppearanceControls();
    }

    private static string FormatAppRules(IReadOnlyDictionary<string, NotificationImportance> rules)
    {
        if (rules.Count == 0) return string.Empty;
        return string.Join(", ", rules.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static IReadOnlyDictionary<string, NotificationImportance> ParseAppRules(string? raw)
    {
        var dict = new Dictionary<string, NotificationImportance>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return dict;

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                !string.IsNullOrWhiteSpace(parts[0]) &&
                Enum.TryParse<NotificationImportance>(parts[1], ignoreCase: true, out var imp))
            {
                dict[parts[0]] = imp;
            }
        }
        return dict;
    }

    private void UpdateLabels()
    {
        if (VisibilityValueText is not null)
            VisibilityValueText.Text = $"{DockedVisibilitySlider.Value:0}%";
        if (FrequencyValueText is not null)
            FrequencyValueText.Text = $"{AutonomyCooldownSlider.Value:0.0} min";
        if (SpeedValueText is not null)
            SpeedValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{MovementSpeedSlider.Value:0.0}x");
        if (AnnoyanceThresholdValueText is not null)
            AnnoyanceThresholdValueText.Text = $"{AnnoyanceThresholdSlider.Value:0} clicks";
        if (FullScreenRestoreDelayValueText is not null)
            FullScreenRestoreDelayValueText.Text = $"{FullScreenRestoreDelaySlider.Value:0} ms";
        if (QuietThresholdValueText is not null)
            QuietThresholdValueText.Text = $"{QuietThresholdSlider.Value:0} s";
        if (AwayThresholdValueText is not null)
            AwayThresholdValueText.Text = $"{AwayThresholdSlider.Value:0} s";
    }

    private async void OnNotificationIntegrationToggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi) return;

        if (NotificationIntegrationCheckBox.IsChecked == true && !_notificationCoordinator.Settings.ConsentExplained)
        {
            var result = System.Windows.MessageBox.Show(
                this,
                "Bloop uses the Windows UserNotificationListener API to display your notification inbox and perform mascot reactions.\n\n" +
                "• Notification titles and bodies are kept strictly on your computer.\n" +
                "• Notification message content is never written to diagnostic logs or sent over the network.\n\n" +
                "Do you want to enable notification integration and request Windows notification access?",
                "Bloop — Notification Privacy & Permission",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
            {
                _isLoadingUi = true;
                NotificationIntegrationCheckBox.IsChecked = false;
                _isLoadingUi = false;
                return;
            }

            await _notificationCoordinator.EnableAndRequestPermissionAsync();
        }

        ApplyAndPersistSettings();
    }

    private void ApplyAndPersistSettings()
    {
        if (_isLoadingUi) return;

        _mascotWindow.Topmost = AlwaysOnTopCheckBox.IsChecked == true;

        bool edgeHopEnabled = AutonomousMovementCheckBox.IsChecked == true;
        bool cornerExploreEnabled = CornerExplorationCheckBox.IsChecked == true;
        bool winExploreEnabled = WindowExplorationCheckBox.IsChecked == true;
        double cooldown = Math.Clamp(AutonomyCooldownSlider.Value, 1.0, 15.0);

        var prevMovement = _movementController.Settings;
        bool cooldownChanged = Math.Abs(prevMovement.AutonomyCooldownMinutes - cooldown) > 1e-4;

        var updatedMovement = prevMovement with
        {
            EdgeDockingEnabled = EdgeDockingCheckBox.IsChecked == true,
            AutonomousMovementEnabled = edgeHopEnabled || cornerExploreEnabled || winExploreEnabled,
            DockedVisibleRatio = Math.Clamp(DockedVisibilitySlider.Value / 100.0, 0.35, 0.90),
            AutonomyCooldownMinutes = cooldown,
            MinAutonomyIntervalMinutes = cooldownChanged ? Math.Max(1.0, cooldown * 0.6) : prevMovement.MinAutonomyIntervalMinutes,
            MaxAutonomyIntervalMinutes = cooldownChanged ? Math.Max(2.0, cooldown * 1.6) : prevMovement.MaxAutonomyIntervalMinutes,
            MovementSpeedMultiplier = Math.Clamp(MovementSpeedSlider.Value, 0.5, 2.0)
        };

        int peakClicks = Math.Clamp((int)Math.Round(AnnoyanceThresholdSlider.Value), 3, 10);
        bool reactionsOn = NotificationReactionsCheckBox.IsChecked == true;

        var updatedBehaviours = _behaviourScheduler.Settings with
        {
            GlobalBehavioursPaused = GlobalPauseCheckBox.IsChecked == true,
            AutonomousEdgeHoppingEnabled = edgeHopEnabled,
            CornerExplorationEnabled = cornerExploreEnabled,
            WindowBoundaryExplorationEnabled = winExploreEnabled,
            PeekabooEnabled = PeekabooCheckBox.IsChecked == true,
            EdgeBalancingEnabled = EdgeBalancingCheckBox.IsChecked == true,
            CursorChasingEnabled = CursorChasingCheckBox.IsChecked == true,
            AttentionSeekingEnabled = AttentionSeekingCheckBox.IsChecked == true,
            SleepModeEnabled = SleepModeCheckBox.IsChecked == true,
            WelcomeBackReactionEnabled = WelcomeBackCheckBox.IsChecked == true,
            RandomClickReactionsEnabled = RandomClickCheckBox.IsChecked == true,
            RepeatedClickAnnoyanceEnabled = RepeatedClickAnnoyanceCheckBox.IsChecked == true,
            NotificationReactionsEnabled = reactionsOn,
            AnnoyancePeakThreshold = peakClicks,
            AnnoyanceMildThreshold = Math.Max(2, peakClicks - 2)
        };

        var updatedNotificationSettings = _notificationCoordinator.Settings with
        {
            IntegrationEnabled = NotificationIntegrationCheckBox.IsChecked == true,
            StoreLocalHistoryEnabled = StoreLocalHistoryCheckBox.IsChecked == true,
            ReactionsEnabled = reactionsOn,
            HighImportanceCenterEntranceEnabled = HighImportanceEntranceCheckBox.IsChecked == true,
            HighPriorityKeywordsCsv = HighPriorityKeywordsTextBox.Text?.Trim() ?? string.Empty,
            AppPriorityRules = ParseAppRules(AppPriorityRulesTextBox.Text)
        };

        _movementController.UpdateSettings(updatedMovement);
        _behaviourScheduler.UpdateSettings(updatedBehaviours);
        _settingsRepository.SaveMovementSettings(updatedMovement);
        _settingsRepository.SaveBehaviourSettings(updatedBehaviours);
        _notificationCoordinator.UpdateSettings(updatedNotificationSettings);

        double quietSec = QuietThresholdSlider.Value;
        double awaySec = Math.Max(quietSec + 15.0, AwayThresholdSlider.Value);
        double sleepSec = Math.Max(awaySec + 30.0, updatedBehaviours.SleepInactivityMinutes * 60.0);

        bool sessionPower = SessionPowerAwarenessCheckBox.IsChecked == true;
        var updatedAwareness = _awarenessCoordinator.Settings with
        {
            IsEnabled = DesktopAwarenessEnabledCheckBox.IsChecked == true,
            FullScreenDetectionEnabled = FullScreenDetectionCheckBox.IsChecked == true,
            AutoHideOnFullScreen = AutoHideFullScreenCheckBox.IsChecked == true,
            CatchUpNotificationReactionAfterFullScreen = CatchUpAfterFullScreenCheckBox.IsChecked == true,
            FullScreenRestoreDelayMs = (int)Math.Round(FullScreenRestoreDelaySlider.Value),
            ManualFocusModeEnabled = ManualFocusModeCheckBox.IsChecked == true,
            AutoFocusInferenceEnabled = AutoFocusInferenceCheckBox.IsChecked == true,
            SuppressRandomClickInFocus = SuppressRandomClickInFocusCheckBox.IsChecked == true,
            InactivityDetectionEnabled = InactivityDetectionCheckBox.IsChecked == true,
            AutoQuietModeEnabled = AutoQuietModeCheckBox.IsChecked == true,
            AutoSleepEnabled = SleepModeCheckBox.IsChecked == true,
            WelcomeBackReactionEnabled = WelcomeBackCheckBox.IsChecked == true,
            QuietThresholdSeconds = quietSec,
            AwayThresholdSeconds = awaySec,
            SleepThresholdSeconds = sleepSec,
            SessionAwarenessEnabled = sessionPower,
            PowerAwarenessEnabled = sessionPower,
            FullScreenExcludedProcessesCsv = FullScreenExclusionsTextBox.Text?.Trim() ?? string.Empty,
            AppRules = AwarenessValidator.ParseAppRules(AwarenessAppRulesTextBox.Text)
        };
        _awarenessCoordinator.UpdateSettings(updatedAwareness, persist: true);
        _windowService.UpdateFocusTrayCheckedState(updatedAwareness.ManualFocusModeEnabled);
        if (AwarenessLiveBadgeText is not null)
        {
            AwarenessLiveBadgeText.Text = $"Context: {_awarenessCoordinator.CurrentSnapshot.Context}";
        }

        _windowService.SavePosition(_mascotWindow);
        _mascotWindow.SyncDockingConfigToRenderer();
    }

    private void OnForceWakeClick(object sender, RoutedEventArgs e)
    {
        _awarenessCoordinator.SetManualIdleOverride(ManualIdleOverride.ForceAwake);
        if (AwarenessLiveBadgeText is not null)
        {
            AwarenessLiveBadgeText.Text = $"Context: {_awarenessCoordinator.CurrentSnapshot.Context} (Forced Awake)";
        }
    }

    private void OnForceSleepClick(object sender, RoutedEventArgs e)
    {
        _awarenessCoordinator.SetManualIdleOverride(ManualIdleOverride.ForceSleep);
        if (AwarenessLiveBadgeText is not null)
        {
            AwarenessLiveBadgeText.Text = $"Context: {_awarenessCoordinator.CurrentSnapshot.Context} (Forced Sleep)";
        }
    }

    private void OnAutoIdleClick(object sender, RoutedEventArgs e)
    {
        _awarenessCoordinator.SetManualIdleOverride(ManualIdleOverride.None);
        if (AwarenessLiveBadgeText is not null)
        {
            AwarenessLiveBadgeText.Text = $"Context: {_awarenessCoordinator.CurrentSnapshot.Context}";
        }
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e) => ApplyAndPersistSettings();
    private void OnTextSettingChanged(object sender, TextChangedEventArgs e) => ApplyAndPersistSettings();

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        ApplyAndPersistSettings();
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        _notificationRepository.ClearLocalHistory();
        _mascotWindow.RefreshNotificationInboxAndBadge();
    }

    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        var defaultMovement = MovementSettings.Default;
        var defaultBehaviour = BehaviourSettings.Default;
        var defaultNotif = NotificationSettings.Default with
        {
            IntegrationEnabled = _notificationCoordinator.Settings.IntegrationEnabled,
            ConsentExplained = _notificationCoordinator.Settings.ConsentExplained
        };

        _movementController.UpdateSettings(defaultMovement);
        _behaviourScheduler.UpdateSettings(defaultBehaviour);
        _settingsRepository.SaveMovementSettings(defaultMovement);
        _settingsRepository.SaveBehaviourSettings(defaultBehaviour);
        _notificationCoordinator.UpdateSettings(defaultNotif);
        _awarenessCoordinator.UpdateSettings(AwarenessSettings.Default, persist: true);
        _windowService.UpdateFocusTrayCheckedState(false);

        var activeProfile = _appearanceRepository.GetActiveProfile();
        var resetProfile = _appearanceRepository.ResetProfileToDefaults(activeProfile.ProfileId);
        _hasUncommittedPreview = false;
        ApplySettingsWindowBackdrop(resetProfile);
        _mascotWindow.ApplyAppearanceProfile(resetProfile, isPreview: false);

        LoadControlsFromCurrentSettings();
        _mascotWindow.SyncDockingConfigToRenderer();
    }

    private void OnResetPositionClick(object sender, RoutedEventArgs e)
    {
        _windowService.PlaceAtBottomRight(_mascotWindow);
        _mascotWindow.SyncDockingConfigToRenderer();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_hasUncommittedPreview)
        {
            CommitAndPersistAppearance();
        }
        Close();
    }
}
