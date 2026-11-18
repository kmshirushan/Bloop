using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bloop.Core.Appearance;
using Bloop.Core.Awareness;
using Bloop.Core.Behaviours;
using Bloop.Core.Events;
using Bloop.Core.Interfaces;
using Bloop.Core.Models;
using Bloop.Core.Movement;
using Bloop.Core.Notifications;
using Bloop.Core.Personality;
using Bloop.Desktop.Appearance;
using Bloop.Desktop.Awareness;
using Bloop.Desktop.Services;
using Bloop.Windows.System;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Bloop.Desktop.Windows;

public partial class MascotWindow : Window
{
    private readonly WebViewService _webViewService;
    private readonly WindowService _windowService;
    private readonly IPersonalityEngine _personality;
    private readonly IMovementController _movement;
    private readonly BehaviourScheduler _behaviourScheduler;
    private readonly ISettingsRepository _settingsRepository;
    private readonly NotificationSyncCoordinator _notificationCoordinator;
    private readonly INotificationRepository _notificationRepository;
    private readonly INotificationReactionScheduler _notificationReactionScheduler;
    private readonly IAppearanceRepository _appearanceRepository;
    private readonly AwarenessCoordinator _awarenessCoordinator;
    private readonly DiagnosticsLogService _diagnostics;
    private readonly DispatcherTimer _companionTimer;

    private AppearanceProfile _currentAppearanceProfile = AppearanceDefaults.CreateDefaultProfile();
    private double? _pendingDeferredScale;
    private bool _isHiddenByAwareness;

    private IntPtr _hwnd;
    private bool _isDraggingWindow;
    private double _dragStartWindowLeft;
    private double _dragStartWindowTop;
    private double _dragStartCursorScreenX;
    private double _dragStartCursorScreenY;
    private int _lastCursorPxX = int.MinValue;
    private int _lastCursorPxY = int.MinValue;
    private int _autonomyTickCounter;
    private DockingEdge _lastPreviewEdge = DockingEdge.None;

    // Return-to-edge tracking after a High/Critical notification center-screen entrance
    private DateTimeOffset? _returnFromEntranceDueUtc;
    private DockingEdge _returnEdgeAfterEntrance = DockingEdge.None;
    private double _returnLeftAfterEntrance;
    private double _returnTopAfterEntrance;

    // Active hop flight interpolation state (driven by a temporary 60 Hz DispatcherTimer + Stopwatch)
    private DispatcherTimer? _hopTimer;
    private HopPlanEventArgs? _activeHopPlan;
    private readonly Stopwatch _hopStopwatch = new();

    private NotificationWindow? _demoPanelWindow;
    private SettingsWindow? _settingsWindow;

    public MascotWindow(
        WebViewService webViewService,
        WindowService windowService,
        IPersonalityEngine personality,
        IMovementController movement,
        BehaviourScheduler behaviourScheduler,
        ISettingsRepository settingsRepository,
        NotificationSyncCoordinator notificationCoordinator,
        INotificationRepository notificationRepository,
        INotificationReactionScheduler notificationReactionScheduler,
        IAppearanceRepository appearanceRepository,
        AwarenessCoordinator awarenessCoordinator,
        DiagnosticsLogService diagnostics)
    {
        InitializeComponent();

        _webViewService = webViewService;
        _windowService = windowService;
        _personality = personality;
        _movement = movement;
        _behaviourScheduler = behaviourScheduler;
        _settingsRepository = settingsRepository;
        _notificationCoordinator = notificationCoordinator;
        _notificationRepository = notificationRepository;
        _notificationReactionScheduler = notificationReactionScheduler;
        _appearanceRepository = appearanceRepository;
        _awarenessCoordinator = awarenessCoordinator;
        _diagnostics = diagnostics;
        _currentAppearanceProfile = _appearanceRepository.GetActiveProfile();

        _webViewService.MessageReceived += OnRendererMessageReceived;
        _webViewService.RendererReady += OnRendererReady;
        _personality.StateChanged += OnPersonalityStateChanged;

        _movement.EdgeCandidateChanged += OnEdgeCandidateChanged;
        _movement.HopRequested += OnHopRequested;
        _movement.DiagnosticLogged += (_, diag) => _diagnostics.Record(diag);

        _behaviourScheduler.PeekabooRequested += OnPeekabooRequested;
        _behaviourScheduler.EdgeBalanceRequested += OnEdgeBalanceRequested;
        _behaviourScheduler.DiagnosticLogged += (_, diag) => _diagnostics.Record(diag);
        _behaviourScheduler.Notifications.NotificationReactionTriggered += (_, state) =>
            _personality.TryTransition(state, force: true);

        _notificationCoordinator.DiagnosticLogged += (_, diag) => _diagnostics.RecordCustom(diag.Stage, diag.Summary);
        _notificationCoordinator.Synchronized += OnNotificationsSynchronized;

        _windowService.ResetPositionRequested += (_, _) => ResetToFreeBottomRight();
        _windowService.ToggleDemoPanelRequested += (_, _) => ToggleDemoPanel();
        _windowService.OpenSettingsRequested += (_, _) => ShowSettingsWindow();
        _windowService.DevAutonomousHopRequested += (_, _) => TriggerDevAutonomousHop();
        _windowService.ToggleFocusModeRequested += (_, _) =>
        {
            bool nextFocus = !_awarenessCoordinator.Settings.ManualFocusModeEnabled;
            _awarenessCoordinator.SetManualFocusMode(nextFocus);
            _windowService.UpdateFocusTrayCheckedState(nextFocus);
        };
        _windowService.ExitRequested += (_, _) => System.Windows.Application.Current.Shutdown();

        _awarenessCoordinator.MascotVisibilityPolicyChanged += OnMascotVisibilityPolicyChanged;
        _awarenessCoordinator.StateChanged += OnAwarenessStateChanged;

        // Single coordinated 30 Hz companion timer (cursor tracking + 1 Hz autonomous behaviour evaluation)
        _companionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _companionTimer.Tick += OnCompanionTimerTick;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.Handle;

        // Prevent stealing keyboard focus & hide from Alt+Tab
        NativeMethods.ApplyCompanionWindowStyles(_hwnd);

        var source = HwndSource.FromHwnd(_hwnd);
        if (source?.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        if (msg == NativeMethods.WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _currentAppearanceProfile = _appearanceRepository.GetActiveProfile();
        double initialWindowSize = AppearanceDefaults.CalculateWindowSizeDip(_currentAppearanceProfile.Character.CharacterScale);
        Width = initialWindowSize;
        Height = initialWindowSize;

        _windowService.RestoreOrPlaceDefault(this);
        _windowService.UpdateFocusTrayCheckedState(_awarenessCoordinator.Settings.ManualFocusModeEnabled);
        _companionTimer.Start();
        _awarenessCoordinator.Start(GetBloopOwnedWindowHandles);
        _ = _notificationCoordinator.InitializeAsync();

        try
        {
            await _webViewService.InitializeAsync(MascotWebView);
        }
        catch (Exception ex)
        {
            _diagnostics.RecordCustom("WebViewWarning", $"WebView2 init warning: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnRendererReady(object? sender, EventArgs e)
    {
        SyncDockingConfigToRenderer();
        SendUnreadBadgeToRenderer(_notificationRepository.GetUnreadCount());
        SendAppearanceToRenderer(_currentAppearanceProfile, isPreview: false);
        SendAwarenessStateToRenderer(_awarenessCoordinator.CurrentPolicy);
    }

    private IntPtr[] GetBloopOwnedWindowHandles()
    {
        IntPtr panelHwnd = _demoPanelWindow is not null ? new WindowInteropHelper(_demoPanelWindow).Handle : IntPtr.Zero;
        IntPtr settingsHwnd = _settingsWindow is not null ? new WindowInteropHelper(_settingsWindow).Handle : IntPtr.Zero;
        return [_hwnd, panelHwnd, settingsHwnd];
    }

    private void OnMascotVisibilityPolicyChanged(object? sender, (bool ShouldShowMascot, AwarenessPolicy Policy) e)
    {
        if (!e.ShouldShowMascot)
        {
            _isHiddenByAwareness = true;
            CancelActiveHop($"HiddenByAwareness({e.Policy.Reason})", notifyController: true);
            _returnFromEntranceDueUtc = null;
            _companionTimer.Stop();

            SendAwarenessStateToRenderer(e.Policy);

            if (_hwnd != IntPtr.Zero)
            {
                _ = NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            }
            Visibility = Visibility.Hidden;

            _diagnostics.RecordCustom(
                e.Policy.EffectiveContext == DesktopContext.FullScreen ? "MascotHiddenByFullScreen" : "MascotHiddenByAwareness",
                $"Mascot hidden (Reason={e.Policy.Reason}, Pos=({Left:0},{Top:0}), Edge={_movement.CurrentEdge})");
        }
        else if (_isHiddenByAwareness)
        {
            _isHiddenByAwareness = false;
            Visibility = Visibility.Visible;
            if (_hwnd != IntPtr.Zero)
            {
                _ = NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            }

            SyncDockingConfigToRenderer();
            SendAwarenessStateToRenderer(e.Policy);
            _companionTimer.Start();

            _diagnostics.RecordCustom(
                "MascotRestoredAfterFullScreen",
                $"Mascot restored without focus theft (Reason={e.Policy.Reason}, Pos=({Left:0},{Top:0}), Edge={_movement.CurrentEdge})");

            // Check if notifications arrived while hidden -> play a single SubtleEdgeGlance catch-up reaction if enabled
            var catchUp = _notificationReactionScheduler.TryConsumeCatchUpReaction(
                _notificationRepository.GetUnreadCount(),
                _awarenessCoordinator.Settings.CatchUpNotificationReactionAfterFullScreen,
                DateTimeOffset.UtcNow);
            if (catchUp is not null)
            {
                var workArea = _windowService.GetWorkAreaForWindow(this);
                ExecuteNotificationReactionDecision(catchUp, DateTimeOffset.UtcNow, workArea);
            }
        }
    }

    private void OnAwarenessStateChanged(object? sender, (AwarenessSnapshot Snapshot, AwarenessPolicy Policy) e)
    {
        SendAwarenessStateToRenderer(e.Policy);
        _demoPanelWindow?.UpdateAwarenessStatus(_awarenessCoordinator.FormatDiagnosticsSummary());
    }

    private void SendAwarenessStateToRenderer(AwarenessPolicy policy)
    {
        _webViewService.SendMessage(
            "host:awareness_state",
            new HostAwarenessStatePayload(
                policy.EffectiveContext.ToString(),
                !policy.ShouldShowMascot,
                policy.IsQuietMode,
                policy.Reason));
    }

    public AppearanceProfile CurrentAppearanceProfile => _currentAppearanceProfile;

    public void ApplyAppearanceProfile(AppearanceProfile profile, bool isPreview = false)
    {
        var normalized = AppearanceValidator.Normalize(profile);
        if (!isPreview)
        {
            _currentAppearanceProfile = normalized;
        }

        GlassDesignSystem.ApplyToApplicationResources(normalized.Panel, normalized.Character, true);
        ApplyCharacterScaleToWindow(normalized.Character.CharacterScale, savePosition: !isPreview);
        SendAppearanceToRenderer(normalized, isPreview);
        _demoPanelWindow?.ApplyAppearance(normalized.Panel, normalized.Character);
    }

    public void CancelAppearancePreview()
    {
        _webViewService.SendMessage("appearance:preview_cancel", new { });
        GlassDesignSystem.ApplyToApplicationResources(
            _currentAppearanceProfile.Panel,
            _currentAppearanceProfile.Character,
            true);
        ApplyCharacterScaleToWindow(_currentAppearanceProfile.Character.CharacterScale, savePosition: false);
        _demoPanelWindow?.ApplyAppearance(_currentAppearanceProfile.Panel, _currentAppearanceProfile.Character);
    }

    private void SendAppearanceToRenderer(AppearanceProfile profile, bool isPreview)
    {
        _webViewService.SendMessage(
            isPreview ? "appearance:preview" : "appearance:apply",
            new HostAppearancePayload(profile.ProfileId, profile.Character, isPreview));
    }

    private void ApplyCharacterScaleToWindow(double characterScale, bool savePosition)
    {
        double newSize = AppearanceDefaults.CalculateWindowSizeDip(characterScale);
        if (Math.Abs(Width - newSize) < 0.5 && Math.Abs(Height - newSize) < 0.5)
        {
            _webViewService.SendMessage("appearance:scale", new HostAppearanceScalePayload(characterScale));
            return;
        }

        var workArea = _windowService.GetWorkAreaForWindow(this);
        if (_movement is MovementController mc)
        {
            var res = mc.RecalculatePositionForScaleChange(Width, newSize, workArea);
            if (res.IsDeferredUntilHopComplete)
            {
                _pendingDeferredScale = characterScale;
                _diagnostics.RecordCustom("AppearanceScale", $"Deferred scale={characterScale:F2} ({newSize:0}px) until active hop completes");
                return;
            }

            Width = newSize;
            Height = newSize;
            Left = res.NewLeftDip;
            Top = res.NewTopDip;
        }
        else
        {
            Width = newSize;
            Height = newSize;
        }

        if (savePosition)
        {
            _windowService.SavePosition(this);
        }

        SyncDockingConfigToRenderer();
        PositionDemoPanelRelativeToMascot();
        _webViewService.SendMessage("appearance:scale", new HostAppearanceScalePayload(characterScale));
    }

    public void RefreshNotificationInboxAndBadge()
    {
        int unread = _notificationRepository.GetUnreadCount();
        SendUnreadBadgeToRenderer(unread);
        _demoPanelWindow?.RefreshInboxView();
    }

    private void OnNotificationsSynchronized(object? sender, NotificationSyncResult e)
    {
        Dispatcher.InvokeAsync(() => SendUnreadBadgeToRenderer(e.UnreadCount));
    }

    private void SendUnreadBadgeToRenderer(int unreadCount)
    {
        _webViewService.SendMessage("host:notification_badge", new HostNotificationBadgePayload(unreadCount));
    }

    public void SyncDockingConfigToRenderer()
    {
        var edge = _movement.CurrentEdge;
        var (offsetX, offsetY) = MovementController.CalculateViewportOffset(edge, _movement.Settings.DockedVisibleRatio);
        _webViewService.SendMessage(
            "host:dock_config",
            new HostDockConfigPayload(
                edge.ToString().ToLowerInvariant(),
                offsetX,
                offsetY,
                _movement.Settings.DockedVisibleRatio
            )
        );
    }

    private bool _exitOnHopComplete;

    public void EnableExitOnNextNaturalHopCompletion()
    {
        _exitOnHopComplete = true;
    }

    public async Task RunMilestone4VerificationSequenceAsync()
    {
        try
        {
            _diagnostics.RecordCustom("M4Verify", "Starting Milestone 4 end-to-end runtime verification...");

            // 1. Enable opt-in notification integration & verify startup snapshot import
            var perm = await _notificationCoordinator.EnableAndRequestPermissionAsync();
            int initialInboxCount = _notificationRepository.GetInboxNotifications().Count;
            _diagnostics.RecordCustom(
                "M4Verify",
                $"Step 1 (Startup Sync): Permission={perm}, InitialImportDone={_notificationCoordinator.HasCompletedInitialImport}, StoredInboxCount={initialInboxCount}");

            // 2. Open NotificationWindow in Attached mode & verify geometry beside Mascot
            ToggleDemoPanel();
            await Task.Delay(150);
            if (_demoPanelWindow is not null)
            {
                _diagnostics.RecordCustom(
                    "M4Verify",
                    $"Step 2 (Panel Attached): IsDetached={_demoPanelWindow.IsDetached}, Topmost={_demoPanelWindow.Topmost}, Pos=({_demoPanelWindow.Left:0},{_demoPanelWindow.Top:0}), Size=({_demoPanelWindow.Width:0}x{_demoPanelWindow.Height:0})");

                // 3. Detach panel, move/resize, persist, and reattach
                var workArea = _windowService.GetWorkAreaForWindow(this);
                var detachedPlacement = new NotificationPanelPlacement(
                    IsDetached: true,
                    DetachedLeftDip: workArea.Left + 180,
                    DetachedTopDip: workArea.Top + 140,
                    DetachedWidthDip: 410,
                    DetachedHeightDip: 520,
                    MonitorId: workArea.MonitorId);
                _notificationRepository.SavePanelPlacement(detachedPlacement);
                var reloadedPlacement = _notificationRepository.LoadPanelPlacement();
                _diagnostics.RecordCustom(
                    "M4Verify",
                    $"Step 3 (Panel Detached Persistence): SavedAndLoaded IsDetached={reloadedPlacement.IsDetached}, Pos=({reloadedPlacement.DetachedLeftDip:0},{reloadedPlacement.DetachedTopDip:0}), Size=({reloadedPlacement.DetachedWidthDip:0}x{reloadedPlacement.DetachedHeightDip:0})");

                // Restore to attached mode
                _notificationRepository.SavePanelPlacement(detachedPlacement with { IsDetached = false });
            }

            // 4. Dispatch a real external OS toast notification & verify adaptive sync + batch reaction
            const string powershellAumid = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";
            var xml = new global::Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>Bloop M4 Runtime Check</text><text>Adaptive notification test</text></binding></visual></toast>");
            global::Windows.UI.Notifications.ToastNotificationManager
                .CreateToastNotifier(powershellAumid)
                .Show(new global::Windows.UI.Notifications.ToastNotification(xml));

            await Task.Delay(500);
            var syncResult = await _notificationCoordinator.SynchronizeNowAsync(NotificationSyncTrigger.AdaptiveFastPoll);
            _diagnostics.RecordCustom(
                "M4Verify",
                $"Step 4 (Live OS Toast Sync): Added={syncResult?.AddedItems.Count ?? 0}, TotalActiveInOs={syncResult?.TotalActiveInOs ?? 0}, Unread={syncResult?.UnreadCount ?? 0}");

            // Clean up the synthetic OS notification from Action Center while verifying local history remains
            var synthetic = _notificationRepository
                .GetInboxNotifications()
                .FirstOrDefault(x => x.Title == "Bloop M4 Runtime Check" && x.IsPresentInOs);

            if (synthetic is not null)
            {
                bool osRemoved = _notificationCoordinator.DismissFromWindowsAndMarkLocally(synthetic.Id, synthetic.OsNotificationId, alsoDismissLocally: false);
                var storedAfterOsRemove = _notificationRepository.GetInboxNotifications().FirstOrDefault(x => x.Id == synthetic.Id);
                _diagnostics.RecordCustom(
                    "M4Verify",
                    $"Step 5 (OS Removal vs Local History): OsRemoved={osRemoved}, PreservedInLocalSqlite={storedAfterOsRemove is not null}, IsPresentInOs={storedAfterOsRemove?.IsPresentInOs}");

                // Locally dismiss synthetic test item so user inbox stays clean
                _notificationRepository.DismissLocally(synthetic.Id);
            }

            // Wait 4.5 seconds so the 3.0-second batch window expires and the 1Hz companion timer executes the adaptive reaction
            await Task.Delay(4500);

            var proc = Process.GetCurrentProcess();
            _diagnostics.RecordCustom(
                "M4VerifyComplete",
                $"WorkingSet={proc.WorkingSet64 / (1024.0 * 1024.0):F1}MB, ManagedHeap={GC.GetTotalMemory(false) / (1024.0 * 1024.0):F2}MB, TotalCpuMs={proc.TotalProcessorTime.TotalMilliseconds:F0}ms");
        }
        finally
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    public async Task RunMilestone5VerificationSequenceAsync(string? screenshotOutputDir = null)
    {
        try
        {
            var outDir = !string.IsNullOrWhiteSpace(screenshotOutputDir)
                ? screenshotOutputDir
                : Path.Combine(AppContext.BaseDirectory, "m5_verification");
            Directory.CreateDirectory(outDir);

            _diagnostics.RecordCustom("M5Verify", $"Starting Milestone 5 runtime verification (Output={outDir})...");
            await Task.Delay(900); // Ensure WebView2 & Three.js scene are initialized and rendered

            // Place mascot cleanly in free mode near center-right so full character + panel are visible
            ResetToFreeBottomRight();

            // Temporarily seed 2 sample notifications so panel screenshots showcase card density, opacity, and themes
            var nowUtc = DateTimeOffset.UtcNow;
            var sample1 = new NotificationItem
            {
                Id = "m5-verify-sample-1",
                OsNotificationId = 99001,
                Source = new NotificationSource("Bloop.Verification.Calendar", "Calendar"),
                Title = "Design Review: Milestone 5",
                Body = "Appearance, LiquidGlass shader & 3D accessories ready.",
                CreatedAtUtc = nowUtc.AddMinutes(-2),
                ReceivedAtUtc = nowUtc.AddMinutes(-2),
                Importance = NotificationImportance.High
            };
            var sample2 = new NotificationItem
            {
                Id = "m5-verify-sample-2",
                OsNotificationId = 99002,
                Source = new NotificationSource("Bloop.Verification.System", "Bloop Companion"),
                Title = "Profile Synchronized",
                Body = "Active appearance profile persisted to SQLite.",
                CreatedAtUtc = nowUtc.AddMinutes(-8),
                ReceivedAtUtc = nowUtc.AddMinutes(-8),
                Importance = NotificationImportance.Normal,
                IsRead = true
            };
            _notificationRepository.SynchronizeSnapshot(
                [sample1, sample2],
                NotificationSyncTrigger.Manual,
                isInitialStartupImport: true,
                NotificationSettings.Default,
                item => item.Importance,
                1L);

            ToggleDemoPanel();
            await Task.Delay(350);

            // 1. Default Classic Blue Profile + Dark Panel
            var defaultProfile = _appearanceRepository.ResetProfileToDefaults(AppearanceDefaults.DefaultProfileId);
            ApplyAppearanceProfile(defaultProfile, isPreview: false);
            await Task.Delay(350);
            await CaptureVerificationScreenshotAsync(outDir, "m5_01_classic_blue_default.png");
            _diagnostics.RecordCustom(
                "M5Verify",
                $"Step 1 (Classic Blue Default): Window=({Width:0}x{Height:0}), PanelTheme={defaultProfile.Panel.Theme}");

            // 2. LiquidGlass Material Preset + Wizard Hat + Sunglasses + Wings + LiquidGlass Panel Theme
            var liquidChar = AppearancePresets.ApplyCharacterPreset(defaultProfile.Character, CharacterMaterialPreset.LiquidGlass) with
            {
                BodyColor = "#0284C7",
                AccentColor = "#38BDF8",
                EyeStyle = EyeStyle.Cyber,
                EquippedAccessories = ["wizard_hat", "sunglasses", "wings"],
                AccessoryTintColor = "#6366F1"
            };
            var liquidPanel = AppearancePresets.ApplyPanelPreset(defaultProfile.Panel, PanelThemePreset.LiquidGlass) with
            {
                CardDensity = CardDensity.Compact,
                CornerRadius = 22.0
            };
            var liquidProfile = _appearanceRepository.CreateProfile("M5 Liquid Glass Wizard", liquidChar, liquidPanel, setActive: true);
            ApplyAppearanceProfile(liquidProfile, isPreview: false);
            await Task.Delay(400);
            await CaptureVerificationScreenshotAsync(outDir, "m5_02_liquid_glass_accessories.png");
            _diagnostics.RecordCustom(
                "M5Verify",
                $"Step 2 (LiquidGlass + Accessories + Compact Panel): Profile={liquidProfile.Name}, Accessories=[{string.Join(",", liquidProfile.Character.EquippedAccessories)}]");

            // 3. Metallic Gold + Royal Crown + Scarf + Light Panel Theme
            var metallicChar = AppearancePresets.ApplyCharacterPreset(defaultProfile.Character, CharacterMaterialPreset.Metallic) with
            {
                BodyColor = "#D97706",
                AccentColor = "#FDE047",
                EyeStyle = EyeStyle.Warm,
                EquippedAccessories = ["crown", "scarf"],
                AccessoryTintColor = "#FACC15"
            };
            var lightPanel = AppearancePresets.ApplyPanelPreset(defaultProfile.Panel, PanelThemePreset.Light) with
            {
                CardDensity = CardDensity.Comfortable,
                SynchronizeAccent = true
            };
            var royalProfile = _appearanceRepository.SaveProfile(liquidProfile with
            {
                Name = "M5 Royal Metallic Light",
                Character = metallicChar,
                Panel = lightPanel
            }, setActive: true);
            ApplyAppearanceProfile(royalProfile, isPreview: false);
            await Task.Delay(400);
            await CaptureVerificationScreenshotAsync(outDir, "m5_03_metallic_gold_light_panel.png");
            _diagnostics.RecordCustom(
                "M5Verify",
                $"Step 3 (Metallic Gold + Crown/Scarf + Light Panel): Body={metallicChar.BodyColor}, PanelBg={lightPanel.BackgroundColor}");

            // 4. Character Scaling Verification (0.65x, 1.00x, 1.50x) + Hop While Scaled (1.50x)
            var scale065Profile = royalProfile with
            {
                Character = royalProfile.Character with { CharacterScale = 0.65, EquippedAccessories = ["cap", "glasses"] }
            };
            ApplyAppearanceProfile(scale065Profile, isPreview: true);
            await Task.Delay(350);
            await CaptureVerificationScreenshotAsync(outDir, "m5_04_scale_065_preview.png");
            _diagnostics.RecordCustom(
                "M5Verify",
                $"Step 4a (Scale 0.65x Preview): WindowSize=({Width:0}x{Height:0}) at Pos=({Left:0},{Top:0})");

            var scale150Profile = royalProfile with
            {
                Character = AppearancePresets.ApplyCharacterPreset(defaultProfile.Character, CharacterMaterialPreset.Soft) with
                {
                    BodyColor = "#7C3AED",
                    AccentColor = "#C4B5FD",
                    EyeStyle = EyeStyle.Starry,
                    CharacterScale = 1.50,
                    EquippedAccessories = ["headphones", "wings"],
                    AccessoryTintColor = "#38BDF8"
                },
                Panel = AppearancePresets.ApplyPanelPreset(defaultProfile.Panel, PanelThemePreset.Glass)
            };
            ApplyAppearanceProfile(scale150Profile, isPreview: false);
            await Task.Delay(350);
            await CaptureVerificationScreenshotAsync(outDir, "m5_05_scale_150_soft_headphones.png");
            _diagnostics.RecordCustom(
                "M5Verify",
                $"Step 4b (Scale 1.50x Applied): WindowSize=({Width:0}x{Height:0}) at Pos=({Left:0},{Top:0})");

            // Trigger an autonomous edge hop while at 1.50x scale and request a mid-hop scale change to 1.00x to verify deferred scale handling!
            TriggerDevAutonomousHop(exitOnComplete: false);
            await Task.Delay(180); // Mid-hop!
            ApplyCharacterScaleToWindow(1.00, savePosition: true); // Must defer until hop completes
            await Task.Delay(1100); // Wait for hop to finish and deferred scale to apply
            await CaptureVerificationScreenshotAsync(outDir, "m5_06_docked_after_scaled_hop.png");
            _diagnostics.RecordCustom(
                "M5Verify",
                $"Step 5 (Hop + Deferred Scale Complete): State={_movement.CurrentState}, Edge={_movement.CurrentEdge}, WindowSize=({Width:0}x{Height:0}), Pos=({Left:0},{Top:0})");

            // 5. Clean up test profile, temporary sample notifications, and restore Classic Blue default
            _notificationRepository.DismissLocally("m5-verify-sample-1");
            _notificationRepository.DismissLocally("m5-verify-sample-2");
            _appearanceRepository.SetActiveProfile(AppearanceDefaults.DefaultProfileId);
            _appearanceRepository.DeleteProfile(liquidProfile.ProfileId);
            var restored = _appearanceRepository.GetActiveProfile();
            ApplyAppearanceProfile(restored, isPreview: false);
            ResetToFreeBottomRight();
            await Task.Delay(250);

            var proc = Process.GetCurrentProcess();
            _diagnostics.RecordCustom(
                "M5VerifyComplete",
                $"RestoredProfile={restored.Name}, WorkingSet={proc.WorkingSet64 / (1024.0 * 1024.0):F1}MB, ManagedHeap={GC.GetTotalMemory(false) / (1024.0 * 1024.0):F2}MB, TotalCpuMs={proc.TotalProcessorTime.TotalMilliseconds:F0}ms");
        }
        finally
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    private async Task CaptureVerificationScreenshotAsync(string outDir, string fileName)
    {
        try
        {
            using var mascotMs = new MemoryStream();
            if (MascotWebView.CoreWebView2 is not null)
            {
                await MascotWebView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png,
                    mascotMs);
                mascotMs.Position = 0;
            }

            Bitmap? mascotBmp = mascotMs.Length > 0 ? new Bitmap(mascotMs) : null;
            Bitmap? panelBmp = null;

            if (_demoPanelWindow is { IsVisible: true })
            {
                _demoPanelWindow.UpdateLayout();
                int pw = Math.Max(1, (int)Math.Round(_demoPanelWindow.ActualWidth > 0 ? _demoPanelWindow.ActualWidth : _demoPanelWindow.Width));
                int ph = Math.Max(1, (int)Math.Round(_demoPanelWindow.ActualHeight > 0 ? _demoPanelWindow.ActualHeight : _demoPanelWindow.Height));
                var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(_demoPanelWindow);

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var pms = new MemoryStream();
                enc.Save(pms);
                pms.Position = 0;
                panelBmp = new Bitmap(pms);
            }

            int pad = 24;
            int mw = mascotBmp?.Width ?? (int)Width;
            int mh = mascotBmp?.Height ?? (int)Height;
            int panelW = panelBmp?.Width ?? 0;
            int panelH = panelBmp?.Height ?? 0;

            int totalW = pad + panelW + (panelW > 0 ? 16 : 0) + mw + pad;
            int totalH = Math.Max(mh, panelH) + pad * 2;

            using var composite = new Bitmap(totalW, totalH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(composite))
            {
                // Draw subtle desktop backdrop checkerboard/gradient so glass translucency is clearly visible
                using var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new System.Drawing.Rectangle(0, 0, totalW, totalH),
                    System.Drawing.Color.FromArgb(255, 15, 23, 42),
                    System.Drawing.Color.FromArgb(255, 30, 58, 138),
                    35f);
                g.FillRectangle(bgBrush, 0, 0, totalW, totalH);

                if (panelBmp is not null)
                {
                    g.DrawImage(panelBmp, pad, pad, panelW, panelH);
                }

                if (mascotBmp is not null)
                {
                    int mx = pad + (panelW > 0 ? panelW + 16 : 0);
                    int my = pad + Math.Max(0, (totalH - pad * 2 - mh) / 2);
                    g.DrawImage(mascotBmp, mx, my, mw, mh);
                }
            }

            composite.Save(Path.Combine(outDir, fileName), ImageFormat.Png);
            mascotBmp?.Dispose();
            panelBmp?.Dispose();
        }
        catch (Exception ex)
        {
            _diagnostics.RecordCustom("M5ScreenshotWarn", $"{fileName}: {ex.Message}");
        }
    }

    public async Task RunMilestone5_1VerificationSequenceAsync(string? screenshotOutputDir = null)
    {
        try
        {
            var outDir = !string.IsNullOrWhiteSpace(screenshotOutputDir)
                ? screenshotOutputDir
                : Path.Combine(AppContext.BaseDirectory, "m5_1_verification");
            Directory.CreateDirectory(outDir);

            var procBefore = Process.GetCurrentProcess();
            _diagnostics.RecordCustom(
                "M51PerfBaseline",
                $"Before M5.1 Sequence: WorkingSet={procBefore.WorkingSet64 / (1024.0 * 1024.0):F1}MB, ManagedHeap={GC.GetTotalMemory(false) / (1024.0 * 1024.0):F2}MB, TotalCpuMs={procBefore.TotalProcessorTime.TotalMilliseconds:F0}ms");

            await Task.Delay(900);
            ResetToFreeBottomRight();

            // Mark notification consent explained in the isolated verification DB so the opt-in banner doesn't obscure empty inbox captures
            _notificationCoordinator.UpdateSettings(_notificationCoordinator.Settings with
            {
                IntegrationEnabled = true,
                ConsentExplained = true
            });

            var baseProfile = _appearanceRepository.ResetProfileToDefaults(AppearanceDefaults.DefaultProfileId);
            var liquidChar = AppearancePresets.ApplyCharacterPreset(baseProfile.Character, CharacterMaterialPreset.LiquidGlass) with
            {
                BodyColor = "#0284C7",
                AccentColor = "#38BDF8",
                EyeStyle = EyeStyle.Classic,
                EquippedAccessories = []
            };
            var liquidPanel = AppearancePresets.ApplyPanelPreset(baseProfile.Panel, PanelThemePreset.LiquidGlass) with
            {
                CornerRadius = 18.0,
                CardDensity = CardDensity.Comfortable,
                SynchronizeAccent = true
            };
            var m51Profile = _appearanceRepository.CreateProfile("M5.1 Sapphire Liquid Glass", liquidChar, liquidPanel, setActive: true);
            ApplyAppearanceProfile(m51Profile, isPreview: false);

            // Seed 3 realistic notification groups (High/Critical/Normal)
            var nowUtc = DateTimeOffset.UtcNow;
            var n1 = new NotificationItem
            {
                Id = "m51-sample-1",
                OsNotificationId = 99101,
                Source = new NotificationSource("Bloop.M51.Calendar", "Outlook Calendar"),
                Title = "Architecture Sync — Unified Liquid Glass",
                Body = "Review DWM Acrylic backdrop diffusion, 5-level surface hierarchy & Fresnel glass shader.",
                CreatedAtUtc = nowUtc.AddMinutes(-1),
                ReceivedAtUtc = nowUtc.AddMinutes(-1),
                Importance = NotificationImportance.High,
                IsRead = false
            };
            var n2 = new NotificationItem
            {
                Id = "m51-sample-2",
                OsNotificationId = 99102,
                Source = new NotificationSource("Bloop.M51.Security", "Windows Security"),
                Title = "SmartScreen & Firewall Active",
                Body = "All local SQLite notification stores and DWM window handles verified healthy.",
                CreatedAtUtc = nowUtc.AddMinutes(-6),
                ReceivedAtUtc = nowUtc.AddMinutes(-6),
                Importance = NotificationImportance.Critical,
                IsRead = false
            };
            var n3 = new NotificationItem
            {
                Id = "m51-sample-3",
                OsNotificationId = 99103,
                Source = new NotificationSource("Bloop.M51.Companion", "Bloop Companion"),
                Title = "Profile Synchronized",
                Body = "Sapphire Liquid Glass tokens applied to NotificationWindow and SettingsWindow.",
                CreatedAtUtc = nowUtc.AddMinutes(-14),
                ReceivedAtUtc = nowUtc.AddMinutes(-14),
                Importance = NotificationImportance.Normal,
                IsRead = true
            };

            _notificationRepository.SynchronizeSnapshot(
                [n1, n2, n3],
                NotificationSyncTrigger.Manual,
                isInitialStartupImport: true,
                NotificationSettings.Default,
                item => item.Importance,
                1L);

            ToggleDemoPanel();
            await Task.Delay(350);

            // 1. Notification panel over a bright wallpaper
            await CaptureM51SceneAsync(outDir, "m51_01_panel_bright_wallpaper.png", BackdropKind.BrightDaylight, includePanel: true, includeMascot: true, includeSettings: false);
            // 2. Notification panel over a dark wallpaper
            await CaptureM51SceneAsync(outDir, "m51_02_panel_dark_wallpaper.png", BackdropKind.DarkSapphire, includePanel: true, includeMascot: true, includeSettings: false);
            // 3. Notification panel with multiple notification cards (over high-detail desktop windows)
            await CaptureM51SceneAsync(outDir, "m51_03_panel_multiple_cards.png", BackdropKind.DesktopWindowsBehind, includePanel: true, includeMascot: false, includeSettings: false);

            // 4. Empty notification panel
            _notificationRepository.ClearLocalHistory();
            _demoPanelWindow?.RefreshInboxView();
            await Task.Delay(250);
            await CaptureM51SceneAsync(outDir, "m51_04_panel_empty_inbox.png", BackdropKind.DarkSapphire, includePanel: true, includeMascot: true, includeSettings: false);

            // Restore notifications for Attached/Detached captures
            _notificationRepository.SynchronizeSnapshot(
                [n1, n2, n3],
                NotificationSyncTrigger.Manual,
                isInitialStartupImport: true,
                NotificationSettings.Default,
                item => item.Importance,
                1L);
            _demoPanelWindow?.RefreshInboxView();
            await Task.Delay(250);

            // 5, 6, 7. Complete SettingsWindow at Top, Middle, and Bottom
            ShowSettingsWindow();
            await Task.Delay(350);
            if (_settingsWindow is not null)
            {
                _settingsWindow.ScrollToFraction(0.0);
                await Task.Delay(200);
                await CaptureM51SceneAsync(outDir, "m51_05_settings_top.png", BackdropKind.DesktopWindowsBehind, includePanel: false, includeMascot: false, includeSettings: true);

                _settingsWindow.ScrollToFraction(0.50);
                await Task.Delay(200);
                await CaptureM51SceneAsync(outDir, "m51_06_settings_middle.png", BackdropKind.DesktopWindowsBehind, includePanel: false, includeMascot: false, includeSettings: true);

                _settingsWindow.ScrollToFraction(1.0);
                await Task.Delay(200);
                await CaptureM51SceneAsync(outDir, "m51_07_settings_bottom.png", BackdropKind.DesktopWindowsBehind, includePanel: false, includeMascot: false, includeSettings: true);

                _settingsWindow.Close();
                _settingsWindow = null;
            }

            // 8. Character with LiquidGlass material
            await Task.Delay(250);
            await CaptureM51SceneAsync(outDir, "m51_08_character_liquid_glass.png", BackdropKind.DarkSapphire, includePanel: false, includeMascot: true, includeSettings: false);

            // 9. Character with Classic material
            var classicCharProfile = m51Profile with
            {
                Character = AppearancePresets.ApplyCharacterPreset(m51Profile.Character, CharacterMaterialPreset.Classic)
            };
            ApplyAppearanceProfile(classicCharProfile, isPreview: true);
            await Task.Delay(350);
            await CaptureM51SceneAsync(outDir, "m51_09_character_classic.png", BackdropKind.DarkSapphire, includePanel: false, includeMascot: true, includeSettings: false);

            // 10. Character with LiquidGlass + Accessories (Wizard Hat, Cool Shades, Angel Wings)
            var accCharProfile = m51Profile with
            {
                Character = liquidChar with
                {
                    EquippedAccessories = ["wizard_hat", "sunglasses", "wings"],
                    AccessoryTintColor = "#6366F1"
                }
            };
            ApplyAppearanceProfile(accCharProfile, isPreview: false);
            await Task.Delay(350);
            await CaptureM51SceneAsync(outDir, "m51_10_character_accessories.png", BackdropKind.DarkSapphire, includePanel: false, includeMascot: true, includeSettings: false);

            // 11. Attached panel
            await CaptureM51SceneAsync(outDir, "m51_11_panel_attached_mode.png", BackdropKind.DesktopWindowsBehind, includePanel: true, includeMascot: true, includeSettings: false);

            // 12. Detached panel
            if (_demoPanelWindow is not null)
            {
                var workArea = _windowService.GetWorkAreaForWindow(this);
                var detachedPlacement = new NotificationPanelPlacement(
                    IsDetached: true,
                    DetachedLeftDip: workArea.Left + 180,
                    DetachedTopDip: workArea.Top + 140,
                    DetachedWidthDip: 410,
                    DetachedHeightDip: 520,
                    MonitorId: workArea.MonitorId);
                _notificationRepository.SavePanelPlacement(detachedPlacement);
                _demoPanelWindow.Close();
                _demoPanelWindow = null;
                ToggleDemoPanel();
                await Task.Delay(350);
                await CaptureM51SceneAsync(outDir, "m51_12_panel_detached_mode.png", BackdropKind.DesktopWindowsBehind, includePanel: true, includeMascot: false, includeSettings: false);

                // Restore attached placement
                _notificationRepository.SavePanelPlacement(detachedPlacement with { IsDetached = false });
            }

            // Clean up synthetic notifications and temporary profile, restoring Classic Blue default
            _notificationRepository.DismissLocally("m51-sample-1");
            _notificationRepository.DismissLocally("m51-sample-2");
            _notificationRepository.DismissLocally("m51-sample-3");
            _appearanceRepository.SetActiveProfile(AppearanceDefaults.DefaultProfileId);
            _appearanceRepository.DeleteProfile(m51Profile.ProfileId);
            var restored = _appearanceRepository.GetActiveProfile();
            ApplyAppearanceProfile(restored, isPreview: false);
            await Task.Delay(200);

            var procAfter = Process.GetCurrentProcess();
            _diagnostics.RecordCustom(
                "M51VerifyComplete",
                $"RestoredProfile={restored.Name}, WorkingSet={procAfter.WorkingSet64 / (1024.0 * 1024.0):F1}MB, ManagedHeap={GC.GetTotalMemory(false) / (1024.0 * 1024.0):F2}MB, TotalCpuMs={procAfter.TotalProcessorTime.TotalMilliseconds:F0}ms");
        }
        finally
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    public async Task RunMilestone6VerificationSequenceAsync(string? screenshotOutputDir = null)
    {
        try
        {
            var outDir = !string.IsNullOrWhiteSpace(screenshotOutputDir)
                ? screenshotOutputDir
                : Path.Combine(AppContext.BaseDirectory, "m6_verification");
            Directory.CreateDirectory(outDir);

            await Task.Delay(900);
            _awarenessCoordinator.Stop(); // Pause live OS timer so deterministic scenario timestamps run without race conditions
            ResetToFreeBottomRight();

            double savedLeft = Left;
            double savedTop = Top;

            var t0 = DateTimeOffset.UtcNow;

            // Scenario A: Real OS Foreground Window & Idle Check via WindowsAwarenessProvider
            _awarenessCoordinator.EvaluateNow(t0, sampleIdleNow: true);
            var realSnap = _awarenessCoordinator.CurrentSnapshot;
            _diagnostics.RecordCustom(
                "M6ScenarioA_RealDesktop",
                $"RealForeground={realSnap.ForegroundProcessName ?? "Desktop"}, RealIdle={realSnap.IdleDuration.TotalSeconds:F1}s, Context={realSnap.Context}, Visible={Visibility == Visibility.Visible}");

            // Scenario B: Maximized Application Exclusion (Browser & IDE)
            var maxIdeObs = new ForegroundWindowObservation(
                WindowHandle: new IntPtr(0x20010),
                ProcessId: 5100,
                ProcessName: "devenv",
                ApplicationName: "Visual Studio",
                WindowClassName: "HwndWrapper",
                IsValidExternalWindow: true,
                IsBloopOwnedWindow: false,
                IsShellOrDesktopSurface: false,
                IsVisible: true,
                IsMinimized: false,
                IsMaximized: true,
                HasCaptionOrThickFrame: true,
                WindowLeftPx: 0, WindowTopPx: 0, WindowRightPx: 1920, WindowBottomPx: 1040,
                MonitorLeftPx: 0, MonitorTopPx: 0, MonitorRightPx: 1920, MonitorBottomPx: 1080,
                WorkAreaLeftPx: 0, WorkAreaTopPx: 0, WorkAreaRightPx: 1920, WorkAreaBottomPx: 1040,
                MonitorId: "\\\\.\\DISPLAY1");

            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.FromSeconds(2), false, false, t0.AddSeconds(1));
            _diagnostics.RecordCustom(
                "M6ScenarioB_MaximizedIDE",
                $"Context={_awarenessCoordinator.CurrentSnapshot.Context}, IsFullScreen={_awarenessCoordinator.CurrentSnapshot.IsFullScreen}, MascotVisible={Visibility == Visibility.Visible}");

            // Scenario C & D: Full-Screen Video / Game Detection -> Hysteresis -> Auto-Hide -> Notification Deferral -> Restore + Single Catch-Up
            var fsGameObs = maxIdeObs with
            {
                WindowHandle = new IntPtr(0x30020),
                ProcessName = "eldenring",
                ApplicationName = "Elden Ring",
                IsMaximized = false,
                HasCaptionOrThickFrame = false,
                WindowBottomPx = 1080
            };

            // Candidate enter at t+2s, confirmed at t+2.5s (>= 400ms hysteresis)
            _awarenessCoordinator.EvaluateSyntheticObservation(fsGameObs, TimeSpan.FromSeconds(1), false, false, t0.AddSeconds(2));
            _awarenessCoordinator.EvaluateSyntheticObservation(fsGameObs, TimeSpan.FromSeconds(1), false, false, t0.AddMilliseconds(2500));

            // Enqueue 2 notifications while hidden in FullScreen
            var fsNotif1 = new NotificationItem
            {
                Id = "m6-fs-notif-1",
                OsNotificationId = 88101,
                Source = new NotificationSource("Bloop.M6.Test", "Steam"),
                Title = "Achievement Unlocked",
                Body = "Received during full-screen gameplay",
                CreatedAtUtc = t0.AddSeconds(3),
                ReceivedAtUtc = t0.AddSeconds(3),
                Importance = NotificationImportance.High
            };
            var fsNotif2 = fsNotif1 with { Id = "m6-fs-notif-2", OsNotificationId = 88102, Title = "Friend Online" };
            _notificationRepository.SynchronizeSnapshot(
                [fsNotif1, fsNotif2],
                NotificationSyncTrigger.Manual,
                isInitialStartupImport: false,
                NotificationSettings.Default,
                item => item.Importance,
                1L);
            _notificationReactionScheduler.EnqueueNewNotifications([fsNotif1, fsNotif2], t0.AddSeconds(3), NotificationSettings.Default);
            var fsReaction = _notificationReactionScheduler.EvaluatePendingBatch(
                t0.AddSeconds(7),
                NotificationSettings.Default,
                _behaviourScheduler.Settings,
                _behaviourScheduler.PriorityResolver,
                _movement.CurrentState,
                _personality.CurrentState,
                totalUnreadCount: 2,
                forceFlush: true,
                awarenessPolicy: _awarenessCoordinator.CurrentPolicy);

            _diagnostics.RecordCustom(
                "M6ScenarioCD_FullScreenHidden",
                $"Context={_awarenessCoordinator.CurrentSnapshot.Context}, MascotVisible={Visibility == Visibility.Visible}, ReactionSuppressed={!fsReaction!.ShouldReact}, DeferredCount={_notificationReactionScheduler.DeferredWhileHiddenCount}, PosPreserved=({Left:0},{Top:0})==({savedLeft:0},{savedTop:0})");

            // Exit FullScreen -> clear hysteresis (700ms) -> Restore Mascot & Single Catch-Up Glance
            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.FromSeconds(1), false, false, t0.AddSeconds(8));
            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.FromSeconds(1), false, false, t0.AddMilliseconds(8800));
            await Task.Delay(250);
            _diagnostics.RecordCustom(
                "M6ScenarioCD_FullScreenRestored",
                $"Context={_awarenessCoordinator.CurrentSnapshot.Context}, MascotVisible={Visibility == Visibility.Visible}, DeferredRemaining={_notificationReactionScheduler.DeferredWhileHiddenCount}, Pos=({Left:0},{Top:0})");

            // Scenario E: Focus Session (Manual Focus Mode + Companion Panel Live Status Capture)
            _awarenessCoordinator.SetManualFocusMode(true);
            ToggleDemoPanel();
            await Task.Delay(250);
            if (_demoPanelWindow is not null)
            {
                // Switch to Companion & Dev tab so the live Awareness Status box is visible in the screenshot!
                _demoPanelWindow.ShowCompanionTab();
                _demoPanelWindow.UpdateAwarenessStatus(_awarenessCoordinator.FormatDiagnosticsSummary());
                await Task.Delay(150);
            }
            await CaptureM51SceneAsync(outDir, "m6_01_focus_mode_companion_panel.png", BackdropKind.DesktopWindowsBehind, includePanel: true, includeMascot: true, includeSettings: false);
            _diagnostics.RecordCustom(
                "M6ScenarioE_FocusMode",
                $"Context={_awarenessCoordinator.CurrentSnapshot.Context}, MascotVisible={Visibility == Visibility.Visible}, AllowMove={_awarenessCoordinator.CurrentPolicy.AllowAutonomousMovement}, Quiet={_awarenessCoordinator.CurrentPolicy.IsQuietMode}");
            _awarenessCoordinator.SetManualFocusMode(false);

            // Scenario F: Inactivity (Quiet -> Away -> Sleeping -> Welcome-Back Wake)
            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.FromSeconds(95), false, false, t0.AddSeconds(15));
            _diagnostics.RecordCustom("M6ScenarioF_Quiet", $"Inactivity={_awarenessCoordinator.CurrentSnapshot.InactivityState}, Context={_awarenessCoordinator.CurrentSnapshot.Context}");

            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.FromSeconds(380), false, false, t0.AddSeconds(16));
            _diagnostics.RecordCustom("M6ScenarioF_Away", $"Inactivity={_awarenessCoordinator.CurrentSnapshot.InactivityState}, Context={_awarenessCoordinator.CurrentSnapshot.Context}");

            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.FromSeconds(1100), false, false, t0.AddSeconds(17));
            await Task.Delay(250);
            await CaptureM51SceneAsync(outDir, "m6_02_inactivity_sleep_state.png", BackdropKind.DarkSapphire, includePanel: true, includeMascot: true, includeSettings: false);
            _diagnostics.RecordCustom("M6ScenarioF_Sleeping", $"Inactivity={_awarenessCoordinator.CurrentSnapshot.InactivityState}, IsSleeping={_behaviourScheduler.IsSleeping}, Mood={_personality.CurrentState}");

            // User returns (Idle = 0s) -> Welcome-Back Happy reaction
            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.Zero, false, false, t0.AddSeconds(18));
            await Task.Delay(200);
            _diagnostics.RecordCustom("M6ScenarioF_WakeWelcomeBack", $"Inactivity={_awarenessCoordinator.CurrentSnapshot.InactivityState}, IsSleeping={_behaviourScheduler.IsSleeping}, Mood={_personality.CurrentState}");

            // Scenario G & H: Session Lock/Unlock & System Suspend/Resume
            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.Zero, isSessionLocked: true, isSystemSuspended: false, t0.AddSeconds(19));
            _diagnostics.RecordCustom("M6ScenarioG_SessionLocked", $"Context={_awarenessCoordinator.CurrentSnapshot.Context}, MascotVisible={Visibility == Visibility.Visible}");
            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.Zero, isSessionLocked: false, isSystemSuspended: false, t0.AddSeconds(20));
            _diagnostics.RecordCustom("M6ScenarioG_SessionUnlocked", $"Context={_awarenessCoordinator.CurrentSnapshot.Context}, MascotVisible={Visibility == Visibility.Visible}");

            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.Zero, isSessionLocked: false, isSystemSuspended: true, t0.AddSeconds(21));
            _diagnostics.RecordCustom("M6ScenarioH_SystemSuspended", $"Context={_awarenessCoordinator.CurrentSnapshot.Context}, MascotVisible={Visibility == Visibility.Visible}");
            _awarenessCoordinator.EvaluateSyntheticObservation(maxIdeObs, TimeSpan.Zero, isSessionLocked: false, isSystemSuspended: false, t0.AddSeconds(22));
            _diagnostics.RecordCustom("M6ScenarioH_SystemResumed", $"Context={_awarenessCoordinator.CurrentSnapshot.Context}, MascotVisible={Visibility == Visibility.Visible}");

            // Scenario I: Capture SettingsWindow showing the new Desktop Awareness section
            ShowSettingsWindow();
            await Task.Delay(300);
            if (_settingsWindow is not null)
            {
                _settingsWindow.ScrollToFraction(0.58);
                await Task.Delay(200);
                await CaptureM51SceneAsync(outDir, "m6_03_settings_desktop_awareness.png", BackdropKind.DesktopWindowsBehind, includePanel: false, includeMascot: false, includeSettings: true);
                _settingsWindow.Close();
                _settingsWindow = null;
            }

            // Clean up synthetic notifications
            _notificationRepository.DismissLocally("m6-fs-notif-1");
            _notificationRepository.DismissLocally("m6-fs-notif-2");

            var procEnd = Process.GetCurrentProcess();
            _diagnostics.RecordCustom(
                "M6VerifyComplete",
                $"WorkingSet={procEnd.WorkingSet64 / (1024.0 * 1024.0):F1}MB, ManagedHeap={GC.GetTotalMemory(false) / (1024.0 * 1024.0):F2}MB, TotalCpuMs={procEnd.TotalProcessorTime.TotalMilliseconds:F0}ms");
        }
        finally
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    private enum BackdropKind
    {
        BrightDaylight,
        DarkSapphire,
        DesktopWindowsBehind
    }

    private async Task CaptureM51SceneAsync(
        string outDir,
        string fileName,
        BackdropKind backdropKind,
        bool includePanel,
        bool includeMascot,
        bool includeSettings)
    {
        try
        {
            Bitmap? mascotBmp = null;
            if (includeMascot && MascotWebView.CoreWebView2 is not null)
            {
                using var mascotMs = new MemoryStream();
                await MascotWebView.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png,
                    mascotMs);
                mascotMs.Position = 0;
                if (mascotMs.Length > 0)
                {
                    mascotBmp = new Bitmap(mascotMs);
                }
            }

            Bitmap? panelBmp = null;
            if (includePanel && _demoPanelWindow is { IsVisible: true })
            {
                panelBmp = RenderWpfWindowToBitmap(_demoPanelWindow);
            }

            Bitmap? settingsBmp = null;
            if (includeSettings && _settingsWindow is { IsVisible: true })
            {
                settingsBmp = RenderWpfWindowToBitmap(_settingsWindow);
            }

            int pad = 28;
            int contentW = 0;
            int contentH = 0;

            if (settingsBmp is not null)
            {
                contentW = settingsBmp.Width;
                contentH = settingsBmp.Height;
            }
            else
            {
                int pw = panelBmp?.Width ?? 0;
                int ph = panelBmp?.Height ?? 0;
                int mw = mascotBmp?.Width ?? 0;
                int mh = mascotBmp?.Height ?? 0;
                contentW = pw + (pw > 0 && mw > 0 ? 18 : 0) + mw;
                contentH = Math.Max(ph, mh);
            }

            int totalW = Math.Max(320, contentW + pad * 2);
            int totalH = Math.Max(320, contentH + pad * 2);

            using var composite = new Bitmap(totalW, totalH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(composite))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                DrawBackdropPattern(g, totalW, totalH, backdropKind);

                if (settingsBmp is not null)
                {
                    BlurBackdropRegion(composite, new System.Drawing.Rectangle(pad, pad, settingsBmp.Width, settingsBmp.Height), radius: 12);
                    g.DrawImage(settingsBmp, pad, pad, settingsBmp.Width, settingsBmp.Height);
                }
                else
                {
                    int curX = pad;
                    if (panelBmp is not null)
                    {
                        BlurBackdropRegion(composite, new System.Drawing.Rectangle(curX, pad, panelBmp.Width, panelBmp.Height), radius: 12);
                        g.DrawImage(panelBmp, curX, pad, panelBmp.Width, panelBmp.Height);
                        curX += panelBmp.Width + 18;
                    }
                    if (mascotBmp is not null)
                    {
                        int my = pad + Math.Max(0, (contentH - mascotBmp.Height) / 2);
                        g.DrawImage(mascotBmp, curX, my, mascotBmp.Width, mascotBmp.Height);
                    }
                }
            }

            composite.Save(Path.Combine(outDir, fileName), ImageFormat.Png);
            mascotBmp?.Dispose();
            panelBmp?.Dispose();
            settingsBmp?.Dispose();
        }
        catch (Exception ex)
        {
            _diagnostics.RecordCustom("M51ScreenshotWarn", $"{fileName}: {ex.Message}");
        }
    }

    private static Bitmap RenderWpfWindowToBitmap(Window win)
    {
        win.UpdateLayout();
        int w = Math.Max(1, (int)Math.Round(win.ActualWidth > 0 ? win.ActualWidth : win.Width));
        int h = Math.Max(1, (int)Math.Round(win.ActualHeight > 0 ? win.ActualHeight : win.Height));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    private static void DrawBackdropPattern(Graphics g, int w, int h, BackdropKind kind)
    {
        if (kind == BackdropKind.BrightDaylight)
        {
            using var sky = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Color.FromArgb(255, 186, 230, 253),
                System.Drawing.Color.FromArgb(255, 254, 240, 138),
                40f);
            g.FillRectangle(sky, 0, 0, w, h);

            using var sunBrush = new SolidBrush(System.Drawing.Color.FromArgb(140, 255, 255, 255));
            g.FillEllipse(sunBrush, w / 5, h / 6, 220, 220);

            using var hillBrush = new SolidBrush(System.Drawing.Color.FromArgb(130, 56, 189, 248));
            g.FillEllipse(hillBrush, -40, h / 2, w + 80, h);
        }
        else if (kind == BackdropKind.DesktopWindowsBehind)
        {
            using var darkBg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Color.FromArgb(255, 15, 23, 42),
                System.Drawing.Color.FromArgb(255, 23, 37, 84),
                35f);
            g.FillRectangle(darkBg, 0, 0, w, h);

            // Simulated desktop code editor / document window behind Bloop to test background diffusion
            using var winFill = new SolidBrush(System.Drawing.Color.FromArgb(235, 30, 41, 59));
            using var winHeader = new SolidBrush(System.Drawing.Color.FromArgb(255, 51, 65, 85));
            using var codeLineCyan = new SolidBrush(System.Drawing.Color.FromArgb(210, 56, 189, 248));
            using var codeLineAmber = new SolidBrush(System.Drawing.Color.FromArgb(210, 251, 191, 36));
            using var codeLineWhite = new SolidBrush(System.Drawing.Color.FromArgb(190, 226, 232, 240));

            g.FillRectangle(winFill, 12, 36, w - 24, h - 64);
            g.FillRectangle(winHeader, 12, 36, w - 24, 28);

            for (int row = 0; row < (h - 110) / 22; row++)
            {
                int y = 80 + row * 22;
                g.FillRectangle(row % 3 == 0 ? codeLineCyan : codeLineAmber, 32, y, 85 + (row % 4) * 35, 8);
                g.FillRectangle(codeLineWhite, 165 + (row % 3) * 20, y, 140 + (row % 5) * 28, 8);
            }
        }
        else
        {
            using var darkSapphire = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Color.FromArgb(255, 8, 18, 38),
                System.Drawing.Color.FromArgb(255, 18, 42, 82),
                45f);
            g.FillRectangle(darkSapphire, 0, 0, w, h);

            using var orb1 = new SolidBrush(System.Drawing.Color.FromArgb(42, 56, 189, 248));
            using var orb2 = new SolidBrush(System.Drawing.Color.FromArgb(34, 99, 102, 241));
            g.FillEllipse(orb1, 35, 45, 210, 210);
            g.FillEllipse(orb2, w - 240, h - 240, 200, 200);
        }
    }

    private static void BlurBackdropRegion(Bitmap bmp, System.Drawing.Rectangle region, int radius)
    {
        var rect = System.Drawing.Rectangle.Intersect(region, new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
        if (rect.Width <= 4 || rect.Height <= 4) return;

        using var clone = bmp.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        // Fast multi-pass downscale/upscale bicubic diffusion matching DWM Acrylic blur kernel
        int smallW = Math.Max(4, rect.Width / Math.Max(2, radius / 2));
        int smallH = Math.Max(4, rect.Height / Math.Max(2, radius / 2));
        using var small = new Bitmap(smallW, smallH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var gs = Graphics.FromImage(small))
        {
            gs.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            gs.DrawImage(clone, 0, 0, smallW, smallH);
        }

        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.SetClip(rect);
        g.DrawImage(small, rect);
        g.ResetClip();
    }

    public void TriggerDevAutonomousHop(bool exitOnComplete = false)
    {
        _exitOnHopComplete = exitOnComplete;
        var workArea = _windowService.GetWorkAreaForWindow(this);
        _movement.TriggerManualAutonomousHop(Width, Height, workArea);
    }

    public void TriggerManualPeekaboo()
    {
        if (_movement.CurrentState == MovementState.Docked && _movement.CurrentEdge != DockingEdge.None)
        {
            _behaviourScheduler.TriggerPeekaboo(DateTimeOffset.UtcNow);
        }
        else
        {
            // If free, dock to closest or next edge first or do a curious peek reaction
            _personality.TryTransition(MascotState.Curious, force: true);
            _diagnostics.RecordCustom("Peekaboo", "Mascot is Free (not Docked) — played Curious reaction; dock to an edge for bezel Peekaboo.");
        }
    }

    private void OnPeekabooRequested(object? sender, (DockingEdge Edge, int DurationMs) e)
    {
        _webViewService.SendMessage(
            "host:peekaboo",
            new HostPeekabooPayload(e.Edge.ToString().ToLowerInvariant(), e.DurationMs)
        );
    }

    private void OnEdgeBalanceRequested(object? sender, (DockingEdge Edge, int DurationMs) e)
    {
        _webViewService.SendMessage(
            "host:edge_balance",
            new HostEdgeBalancePayload(e.Edge.ToString().ToLowerInvariant(), e.DurationMs)
        );
    }

    private void ResetToFreeBottomRight()
    {
        CancelActiveHop("ResetToFreeBottomRight");
        _windowService.PlaceAtBottomRight(this);
        SyncDockingConfigToRenderer();
        PositionDemoPanelRelativeToMascot();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            CancelActiveHop("DisplaySettingsChanged");
            var workArea = _windowService.GetWorkAreaForWindow(this);
            if (_movement.CurrentEdge != DockingEdge.None && _movement.Settings.EdgeDockingEnabled)
            {
                var dockPos = _movement.CalculateDockingPosition(
                    _movement.CurrentEdge,
                    Left,
                    Top,
                    Width,
                    Height,
                    workArea
                );
                Left = dockPos.WindowLeftDip;
                Top = dockPos.WindowTopDip;
                _movement.SyncPosition(Left, Top, workArea.MonitorId, _movement.CurrentEdge);
            }
            else
            {
                var clamped = _windowService.ClampToNearestMonitorDip(this, Left, Top);
                Left = clamped.Left;
                Top = clamped.Top;
                _movement.SyncPosition(Left, Top, workArea.MonitorId, DockingEdge.None);
            }
            _windowService.SavePosition(this);
            SyncDockingConfigToRenderer();
            _demoPanelWindow?.RecoverToVisibleMonitorOnDisplayChange();
        });
    }

    private void OnCompanionTimerTick(object? sender, EventArgs e)
    {
        // 1. Periodic 1 Hz autonomous behaviour & notification batch evaluation
        _autonomyTickCounter++;
        if (_autonomyTickCounter >= 30)
        {
            _autonomyTickCounter = 0;
            var nowUtc = DateTimeOffset.UtcNow;

            if (!_isDraggingWindow && _hopTimer is null)
            {
                var workArea = _windowService.GetWorkAreaForWindow(this);

                // Check if Bloop needs to return to its docked edge after a High/Critical notification entrance
                if (_returnFromEntranceDueUtc.HasValue && nowUtc >= _returnFromEntranceDueUtc.Value)
                {
                    _returnFromEntranceDueUtc = null;
                    _movement.TryHopToCoordinate(
                        _returnLeftAfterEntrance,
                        _returnTopAfterEntrance,
                        _returnEdgeAfterEntrance,
                        workArea,
                        "ReturnAfterNotificationEntrance");
                }
                else
                {
                    // Evaluate pending 3-second notification batch
                    var reactionDecision = _notificationReactionScheduler.EvaluatePendingBatch(
                        nowUtc,
                        _notificationCoordinator.Settings,
                        _behaviourScheduler.Settings,
                        _behaviourScheduler.PriorityResolver,
                        _movement.CurrentState,
                        _personality.CurrentState,
                        _notificationRepository.GetUnreadCount(),
                        forceFlush: false,
                        awarenessPolicy: _awarenessCoordinator.CurrentPolicy);

                    if (reactionDecision is not null)
                    {
                        ExecuteNotificationReactionDecision(reactionDecision, nowUtc, workArea);
                    }
                    else
                    {
                        IntPtr panelHwnd = _demoPanelWindow is not null ? new WindowInteropHelper(_demoPanelWindow).Handle : IntPtr.Zero;
                        IntPtr settingsHwnd = _settingsWindow is not null ? new WindowInteropHelper(_settingsWindow).Handle : IntPtr.Zero;
                        var fgWindow = _windowService.GetForegroundWindowGeometry(this, _hwnd, panelHwnd, settingsHwnd);

                        _behaviourScheduler.EvaluateTick(nowUtc, Width, Height, workArea, fgWindow);
                    }
                }
            }

            if (_demoPanelWindow?.IsVisible == true)
            {
                var dueInSec = Math.Max(0, (_movement.NextAutonomyDueUtc - nowUtc).TotalSeconds);
                _demoPanelWindow.UpdateSchedulerStatus(
                    $"State: {_movement.CurrentState} ({_movement.CurrentEdge}) | Next Due: {dueInSec:0}s"
                );
                _demoPanelWindow.UpdateAwarenessStatus(_awarenessCoordinator.FormatDiagnosticsSummary());
            }
        }

        // 2. Cursor tracking for eye movement & docked peek proximity
        if (!NativeMethods.GetCursorPos(out var pt))
        {
            return;
        }

        if (pt.X == _lastCursorPxX && pt.Y == _lastCursorPxY)
        {
            return;
        }
        _lastCursorPxX = pt.X;
        _lastCursorPxY = pt.Y;

        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        double cursorDipX = pt.X / scaleX;
        double cursorDipY = pt.Y / scaleY;

        var workAreaForCursor = _windowService.GetWorkAreaForWindow(this);
        _behaviourScheduler.NotifyUserActivity(
            DateTimeOffset.UtcNow,
            cursorDipX,
            cursorDipY,
            Width,
            Height,
            workAreaForCursor
        );

        double centerDipX = Left + (Width * 0.5);
        double centerDipY = Top + (Height * 0.5);

        double normX = Math.Clamp((cursorDipX - centerDipX) / 420.0, -1.0, 1.0);
        double normY = Math.Clamp(-(cursorDipY - centerDipY) / 420.0, -1.0, 1.0);

        bool inside = cursorDipX >= Left && cursorDipX <= Left + Width &&
                      cursorDipY >= Top && cursorDipY <= Top + Height;

        _webViewService.SendMessage("host:cursor_position", new HostCursorPayload(normX, normY, inside));
    }

    private void OnEdgeCandidateChanged(object? sender, DockingEdge candidateEdge)
    {
        if (_lastPreviewEdge == candidateEdge) return;
        _lastPreviewEdge = candidateEdge;
        _webViewService.SendMessage(
            "host:dock_preview",
            new HostDockPreviewPayload(candidateEdge.ToString().ToLowerInvariant())
        );
    }

    private void OnHopRequested(object? sender, HopPlanEventArgs plan)
    {
        CancelActiveHop(notifyController: false);
        _activeHopPlan = plan;
        _hopStopwatch.Restart();

        double dx = plan.TargetWindowLeftDip - plan.StartWindowLeftDip;
        double dy = -(plan.TargetWindowTopDip - plan.StartWindowTopDip);
        double len = Math.Sqrt(dx * dx + dy * dy);
        double dirX = len > 1e-3 ? dx / len : 0;
        double dirY = len > 1e-3 ? dy / len : 0;

        _webViewService.SendMessage(
            "host:hop_start",
            new HostHopStartPayload(
                plan.TargetEdge.ToString().ToLowerInvariant(),
                plan.TargetViewportOffsetX,
                plan.TargetViewportOffsetY,
                dirX,
                dirY,
                plan.AnticipationMs,
                plan.FlightMs,
                plan.LandingMs
            )
        );

        // Start a temporary 60 Hz (~16 ms) DispatcherTimer driven by Stopwatch elapsed time
        _hopTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _hopTimer.Tick += OnHopTimerTick;
        _hopTimer.Start();
    }

    private void OnHopTimerTick(object? sender, EventArgs e)
    {
        if (_activeHopPlan is null)
        {
            CancelActiveHop(notifyController: false);
            return;
        }

        var plan = _activeHopPlan;
        double elapsedMs = _hopStopwatch.Elapsed.TotalMilliseconds;
        double flightStartMs = plan.AnticipationMs;
        double flightEndMs = plan.AnticipationMs + plan.FlightMs;
        double totalMs = flightEndMs + plan.LandingMs;

        if (elapsedMs <= flightStartMs)
        {
            // Anticipation phase: window stays anchored while character crouches
            return;
        }

        if (elapsedMs <= flightEndMs)
        {
            double t = Math.Clamp((elapsedMs - flightStartMs) / Math.Max(1.0, plan.FlightMs), 0.0, 1.0);
            double smoothT = t * t * (3.0 - 2.0 * t);
            double arcDip = Math.Sin(t * Math.PI) * (plan.IsUndockPopOut ? 14.0 : 24.0);

            Left = plan.StartWindowLeftDip + (plan.TargetWindowLeftDip - plan.StartWindowLeftDip) * smoothT;
            Top = plan.StartWindowTopDip + (plan.TargetWindowTopDip - plan.StartWindowTopDip) * smoothT - arcDip;
            PositionDemoPanelRelativeToMascot();
            return;
        }

        // Landing phase & completion
        Left = plan.TargetWindowLeftDip;
        Top = plan.TargetWindowTopDip;
        PositionDemoPanelRelativeToMascot();

        if (elapsedMs >= totalMs)
        {
            var finalEdge = plan.TargetEdge;
            double finalLeft = plan.TargetWindowLeftDip;
            double finalTop = plan.TargetWindowTopDip;
            CancelActiveHop(notifyController: false);
            _movement.CompleteHop(finalLeft, finalTop, finalEdge);
            if (_pendingDeferredScale.HasValue)
            {
                double deferred = _pendingDeferredScale.Value;
                _pendingDeferredScale = null;
                ApplyCharacterScaleToWindow(deferred, savePosition: true);
            }
            else
            {
                _windowService.SavePosition(this);
            }

            var proc = Process.GetCurrentProcess();
            _diagnostics.RecordCustom(
                "ResourceTelemetry",
                $"WorkingSet={proc.WorkingSet64 / (1024.0 * 1024.0):F1}MB, ManagedHeap={GC.GetTotalMemory(false) / (1024.0 * 1024.0):F2}MB, TotalCpuMs={proc.TotalProcessorTime.TotalMilliseconds:F0}ms, HopTimerActive={_hopTimer is not null}"
            );

            if (_exitOnHopComplete)
            {
                _exitOnHopComplete = false;
                System.Windows.Application.Current.Shutdown();
            }
        }
    }

    private void CancelActiveHop(string? reason = null, bool notifyController = true)
    {
        if (_hopTimer is not null)
        {
            _hopTimer.Stop();
            _hopTimer.Tick -= OnHopTimerTick;
            _hopTimer = null;
        }
        _activeHopPlan = null;
        _hopStopwatch.Stop();

        if (notifyController && !string.IsNullOrEmpty(reason))
        {
            _movement.CancelActiveMovement(reason);
        }
    }

    private void OnRendererMessageReceived(object? sender, RawIpcMessage msg)
    {
        switch (msg.Type)
        {
            case "pointer:drag_start":
            {
                var payload = msg.Payload.Deserialize<PointerCoordinatesPayload>();
                if (payload is null) break;
                CancelActiveHop("UserDragStart", notifyController: false);
                _returnFromEntranceDueUtc = null;
                _isDraggingWindow = true;
                _dragStartWindowLeft = Left;
                _dragStartWindowTop = Top;
                _dragStartCursorScreenX = payload.ScreenX;
                _dragStartCursorScreenY = payload.ScreenY;
                _movement.BeginDrag(Left, Top);
                _personality.TryTransition(MascotState.Dragging, force: true);
                break;
            }
            case "pointer:drag_move":
            {
                if (!_isDraggingWindow) break;
                var payload = msg.Payload.Deserialize<PointerCoordinatesPayload>();
                if (payload is null) break;

                double deltaX = payload.ScreenX - _dragStartCursorScreenX;
                double deltaY = payload.ScreenY - _dragStartCursorScreenY;
                Left = _dragStartWindowLeft + deltaX;
                Top = _dragStartWindowTop + deltaY;

                var workArea = _windowService.GetWorkAreaForWindow(this, Left, Top);
                _movement.EvaluateDragProximity(Left, Top, Width, Height, workArea);
                PositionDemoPanelRelativeToMascot();
                break;
            }
            case "pointer:drag_end":
            {
                if (!_isDraggingWindow) break;
                _isDraggingWindow = false;

                var workArea = _windowService.GetWorkAreaForWindow(this, Left, Top);
                _movement.CompleteDrag(Left, Top, Width, Height, workArea);

                if (_movement.CurrentState == MovementState.Free)
                {
                    Left = _movement.CurrentWindowLeftDip;
                    Top = _movement.CurrentWindowTopDip;
                    _windowService.SavePosition(this);
                }

                PositionDemoPanelRelativeToMascot();
                _personality.TryTransition(MascotState.Settling, force: true);
                break;
            }
            case "interaction:click":
            {
                if (_movement.CurrentState == MovementState.Docked)
                {
                    var workArea = _windowService.GetWorkAreaForWindow(this);
                    _movement.RequestUndockPopOut(Width, Height, workArea);
                    _personality.TryTransition(MascotState.Happy, force: true);
                }
                else
                {
                    var reaction = _behaviourScheduler.Interaction.EvaluateSingleClick(
                        DateTimeOffset.UtcNow,
                        _behaviourScheduler.Settings,
                        _awarenessCoordinator.CurrentPolicy
                    );
                    _diagnostics.RecordCustom("ClickInteraction", reaction.Description);
                    _personality.TryTransition(reaction.ReactionState, force: true);
                }
                break;
            }
            case "interaction:double_click":
            {
                _behaviourScheduler.Interaction.NotifyDoubleClick();
                ToggleDemoPanel();
                break;
            }
            case "renderer:animation_ack":
            {
                var ack = msg.Payload.Deserialize<AnimationAckPayload>();
                if (ack is not null)
                {
                    _demoPanelWindow?.ReportAnimationAck(ack.State, ack.CommandId);
                }
                break;
            }
            case "appearance:applied":
            {
                var ack = msg.Payload.Deserialize<AppearanceAckPayload>();
                if (ack is not null)
                {
                    var accList = ack.EquippedAccessories is { Count: > 0 }
                        ? string.Join(",", ack.EquippedAccessories)
                        : "none";
                    _diagnostics.RecordCustom(
                        "AppearanceAck",
                        $"Profile={ack.ProfileId}, Preset={ack.Preset}, Scale={ack.CharacterScale:F2}, Accessories=[{accList}]");
                }
                break;
            }
        }
    }

    private void OnPersonalityStateChanged(object? sender, MascotStateChangedEventArgs e)
    {
        var stateName = e.CurrentState.ToString().ToLowerInvariant();
        var cmdId = Guid.NewGuid().ToString("N")[..8];
        _webViewService.SendMessage(
            "host:state_change",
            new HostStatePayload(stateName, (int)e.CurrentState, cmdId)
        );
    }

    private void ExecuteNotificationReactionDecision(
        NotificationReactionDecision decision,
        DateTimeOffset nowUtc,
        WorkAreaBounds workArea)
    {
        if (!decision.ShouldReact)
        {
            _diagnostics.RecordCustom(
                "NotificationReaction",
                $"Suppressed ({decision.SuppressionReason}, Batch={decision.BatchCount}, Highest={decision.HighestImportance}, Unread={decision.UnreadCount})");
            return;
        }

        string cmdId = Guid.NewGuid().ToString("N")[..8];

        if (decision.ReactionKind == NotificationReactionKind.HighImportanceEntrance)
        {
            _returnEdgeAfterEntrance = _movement.CurrentEdge;
            _returnLeftAfterEntrance = Left;
            _returnTopAfterEntrance = Top;
            _returnFromEntranceDueUtc = nowUtc.AddSeconds(4.2);

            double entranceLeft = Math.Clamp(
                workArea.Left + (workArea.Width - Width) * 0.5,
                workArea.Left + 24.0,
                Math.Max(workArea.Left + 24.0, workArea.Right - Width - 24.0));
            double entranceTop = Math.Clamp(
                workArea.Top + Math.Min(160.0, workArea.Height * 0.22),
                workArea.Top + 24.0,
                Math.Max(workArea.Top + 24.0, workArea.Bottom - Height - 24.0));

            _movement.TryHopToCoordinate(
                entranceLeft,
                entranceTop,
                DockingEdge.None,
                workArea,
                "HighImportanceNotificationEntrance");

            _personality.TryTransition(MascotState.Surprised, force: true);
            _webViewService.SendMessage(
                "host:notification_reaction",
                new HostNotificationReactionPayload(
                    "entrance",
                    decision.UnreadCount,
                    decision.PreviewAppDisplayName,
                    decision.PreviewTitle,
                    cmdId));

            _diagnostics.RecordCustom(
                "NotificationReaction",
                $"HighImportanceEntrance (Batch={decision.BatchCount}, Importance={decision.HighestImportance}, Unread={decision.UnreadCount})");
        }
        else
        {
            _personality.TryTransition(MascotState.Notification, force: true);
            _webViewService.SendMessage(
                "host:notification_reaction",
                new HostNotificationReactionPayload(
                    "subtle",
                    decision.UnreadCount,
                    decision.PreviewAppDisplayName,
                    decision.PreviewTitle,
                    cmdId));

            _diagnostics.RecordCustom(
                "NotificationReaction",
                $"SubtleEdgeGlance (Batch={decision.BatchCount}, Importance={decision.HighestImportance}, Unread={decision.UnreadCount})");
        }
    }

    private void ToggleDemoPanel()
    {
        if (_demoPanelWindow is not null && _demoPanelWindow.IsVisible)
        {
            _demoPanelWindow.Hide();
            return;
        }

        _demoPanelWindow ??= new NotificationWindow(
            this,
            _windowService,
            _personality,
            _notificationCoordinator,
            _notificationRepository,
            _diagnostics,
            () => TriggerDevAutonomousHop(false),
            TriggerManualPeekaboo,
            SendUnreadBadgeToRenderer
        );
        PositionDemoPanelRelativeToMascot();
        _demoPanelWindow.Show();
        _demoPanelWindow.ApplyAppearance(_currentAppearanceProfile.Panel, _currentAppearanceProfile.Character);
        _demoPanelWindow.UpdateAwarenessStatus(_awarenessCoordinator.FormatDiagnosticsSummary());
        _notificationCoordinator.NotifyPanelOpened();
    }

    private void PositionDemoPanelRelativeToMascot()
    {
        if (_demoPanelWindow is null || !_demoPanelWindow.IsVisible) return;
        _demoPanelWindow.PositionBesideMascotIfAttached();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            this,
            _windowService,
            _movement,
            _behaviourScheduler,
            _settingsRepository,
            _notificationCoordinator,
            _notificationRepository,
            _appearanceRepository,
            _awarenessCoordinator
        );
        _settingsWindow.Show();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _notificationCoordinator.Synchronized -= OnNotificationsSynchronized;
        _awarenessCoordinator.MascotVisibilityPolicyChanged -= OnMascotVisibilityPolicyChanged;
        _awarenessCoordinator.StateChanged -= OnAwarenessStateChanged;
        CancelActiveHop("ApplicationShutdown");
        _companionTimer.Stop();
        _awarenessCoordinator.Dispose();
        _demoPanelWindow?.Close();
        _settingsWindow?.Close();
        _webViewService.Dispose();
        _windowService.Dispose();
    }
}
