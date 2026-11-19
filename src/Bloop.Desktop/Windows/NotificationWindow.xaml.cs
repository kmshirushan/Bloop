using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bloop.Core.Appearance;
using Bloop.Core.Interfaces;
using Bloop.Core.Notifications;
using Bloop.Core.Personality;
using Bloop.Core.Productivity;
using Bloop.Desktop.Appearance;
using Bloop.Desktop.Services;
using Bloop.Windows.System;

namespace Bloop.Desktop.Windows;

public sealed record NotificationCardViewModel(
    string Id,
    uint OsNotificationId,
    string Title,
    string Body,
    string TimestampText,
    bool IsRead,
    bool IsPresentInOs,
    NotificationImportance Importance,
    System.Windows.Media.Brush CardBackgroundBrush,
    System.Windows.Media.Brush CardBorderBrush,
    System.Windows.Media.Brush LeftAccentBarBrush,
    System.Windows.Media.Brush TitleForeground,
    System.Windows.Media.Brush BodyForeground,
    System.Windows.Media.Brush AccentBrush,
    CornerRadius CardCornerRadius,
    Thickness CardPadding,
    Thickness CardMargin
)
{
    public Visibility UnreadDotVisibility => IsRead ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LeftAccentBarVisibility => (!IsRead || Importance >= NotificationImportance.High)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility PresentInOsVisibility => IsPresentInOs ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BodyVisibility => string.IsNullOrWhiteSpace(Body) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ImportanceBadgeVisibility => Importance >= NotificationImportance.High ? Visibility.Visible : Visibility.Collapsed;
    public string ImportanceBadge => Importance == NotificationImportance.Critical ? "CRITICAL" : "HIGH";
    public string ToggleReadLabel => IsRead ? "Unread" : "Read";
    public FontWeight TitleFontWeight => IsRead ? FontWeights.Normal : FontWeights.SemiBold;
}

public sealed record NotificationGroupViewModel(
    string AppUserModelId,
    string DisplayName,
    string HeaderTitle,
    string Monogram,
    ImageSource? IconImage,
    IReadOnlyList<NotificationCardViewModel> Items,
    System.Windows.Media.Brush GroupBackgroundBrush,
    System.Windows.Media.Brush GroupBorderBrush,
    System.Windows.Media.Brush GroupTitleBrush,
    System.Windows.Media.Brush AccentBrush,
    CornerRadius GroupCornerRadius,
    Thickness GroupPadding
);

public partial class NotificationWindow : Window
{
    private readonly Window _mascotWindow;
    private readonly WindowService _windowService;
    private readonly IPersonalityEngine _personality;
    private readonly NotificationSyncCoordinator _notificationCoordinator;
    private readonly INotificationRepository _notificationRepository;
    private readonly DiagnosticsLogService _diagnostics;
    private readonly ILauncherCatalogService? _launcherService;
    private readonly IProductivityRepository? _productivityRepository;
    private readonly CountdownTimerEngine? _timerEngine;
    private readonly Action _onDevHopTrigger;
    private readonly Action _onPeekabooTrigger;
    private readonly Action<int> _onUnreadCountChanged;

    private IntPtr _hwnd;
    private NotificationPanelPlacement _placement;
    private bool _suppressPlacementSave;
    private PanelAppearance _panelAppearance = AppearanceDefaults.DefaultPanel;
    private CharacterAppearance _characterAppearance = AppearanceDefaults.DefaultCharacter;
    private GlassDesignTokens _currentTokens;
    private bool _isDwmBackdropActive = true;
    private int _activeMainTabIndex; // 0 = Inbox, 1 = Workspace, 2 = Companion
    private int _activeWorkspaceSubTabIndex; // 0 = Launcher, 1 = Tasks & Timer, 2 = Notes
    private bool _suppressNoteEditorEvents;
    private string? _activeNoteId;
    private readonly DispatcherTimer _noteAutoSaveDebounceTimer;

    public bool IsDetached => _placement.IsDetached;
    public PanelAppearance CurrentPanelAppearance => _panelAppearance;
    public bool IsDwmBackdropActive => _isDwmBackdropActive;

    public NotificationWindow(
        Window mascotWindow,
        WindowService windowService,
        IPersonalityEngine personality,
        NotificationSyncCoordinator notificationCoordinator,
        INotificationRepository notificationRepository,
        DiagnosticsLogService diagnostics,
        Action onDevHopTrigger,
        Action onPeekabooTrigger,
        Action<int> onUnreadCountChanged,
        ILauncherCatalogService? launcherService = null,
        IProductivityRepository? productivityRepository = null,
        CountdownTimerEngine? timerEngine = null)
    {
        InitializeComponent();
        _mascotWindow = mascotWindow;
        _windowService = windowService;
        _personality = personality;
        _notificationCoordinator = notificationCoordinator;
        _notificationRepository = notificationRepository;
        _diagnostics = diagnostics;
        _launcherService = launcherService;
        _productivityRepository = productivityRepository;
        _timerEngine = timerEngine;
        _onDevHopTrigger = onDevHopTrigger;
        _onPeekabooTrigger = onPeekabooTrigger;
        _onUnreadCountChanged = onUnreadCountChanged;

        _noteAutoSaveDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _noteAutoSaveDebounceTimer.Tick += (_, _) =>
        {
            _noteAutoSaveDebounceTimer.Stop();
            FlushActiveNoteAutoSave();
        };

        _placement = _notificationRepository.LoadPanelPlacement();
        _currentTokens = GlassDesignSystem.ComputeTokens(_panelAppearance, _characterAppearance, true);

        _diagnostics.EntryAdded += OnDiagnosticEntryAdded;
        _notificationCoordinator.Synchronized += OnNotificationsSynchronized;
        _notificationCoordinator.PermissionStatusChanged += OnPermissionStatusChanged;
        if (_timerEngine is not null)
        {
            _timerEngine.StateChanged += OnTimerEngineStateChanged;
        }

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += (_, _) =>
        {
            ApplyAttachmentModeUi(restoreGeometry: true);
            ApplyAppearance(_panelAppearance, _characterAppearance);
            RefreshInboxView();
            RefreshWorkspaceViews();
            RefreshDiagnosticsView();
        };
        Closed += (_, _) =>
        {
            FlushActiveNoteAutoSave();
            _diagnostics.EntryAdded -= OnDiagnosticEntryAdded;
            _notificationCoordinator.Synchronized -= OnNotificationsSynchronized;
            _notificationCoordinator.PermissionStatusChanged -= OnPermissionStatusChanged;
            if (_timerEngine is not null)
            {
                _timerEngine.StateChanged -= OnTimerEngineStateChanged;
            }
        };
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
            ConfigureDwmWindowBackdrop();
        }
    }

    private void ConfigureDwmWindowBackdrop()
    {
        if (_hwnd == IntPtr.Zero) return;

        bool isLight = _panelAppearance.Theme == PanelThemePreset.Light;
        bool wantAcrylic = _panelAppearance.Theme is PanelThemePreset.Glass or PanelThemePreset.LiquidGlass ||
                           _panelAppearance.EnableGlassBlur ||
                           _panelAppearance.BackgroundOpacity < 0.98;

        var (active, summary) = NativeMethods.ApplyGlassWindowBackdrop(
            _hwnd,
            enableAcrylicBackdrop: wantAcrylic,
            isLightTheme: isLight,
            isToolWindow: true,
            isNoActivate: !_placement.IsDetached);

        _isDwmBackdropActive = active;
        _diagnostics.RecordCustom("PanelBackdrop", $"Mode={summary}, Detached={_placement.IsDetached}, Theme={_panelAppearance.Theme}");
    }

    public void PositionBesideMascotIfAttached()
    {
        if (_placement.IsDetached)
        {
            return;
        }

        _suppressPlacementSave = true;
        try
        {
            var workArea = _windowService.GetWorkAreaForWindow(_mascotWindow);
            var (left, top) = PanelPlacementCalculator.CalculateAttachedPosition(
                _mascotWindow.Left,
                _mascotWindow.Top,
                _mascotWindow.Width,
                _mascotWindow.Height,
                Width,
                Height,
                workArea);

            Left = left;
            Top = top;
        }
        finally
        {
            _suppressPlacementSave = false;
        }
    }

    public void RecoverToVisibleMonitorOnDisplayChange()
    {
        if (_placement.IsDetached)
        {
            var workArea = _windowService.GetWorkAreaForWindow(this);
            _placement = PanelPlacementCalculator.RecoverDetachedPlacement(_placement, workArea);
            _suppressPlacementSave = true;
            try
            {
                Left = _placement.DetachedLeftDip;
                Top = _placement.DetachedTopDip;
                Width = _placement.DetachedWidthDip;
                Height = _placement.DetachedHeightDip;
            }
            finally
            {
                _suppressPlacementSave = false;
            }
            _notificationRepository.SavePanelPlacement(_placement);
        }
        else
        {
            PositionBesideMascotIfAttached();
        }
    }

    private void ApplyAttachmentModeUi(bool restoreGeometry)
    {
        _suppressPlacementSave = true;
        try
        {
            if (_placement.IsDetached)
            {
                Topmost = false;
                ResizeMode = ResizeMode.CanResize;
                PanelWindowChrome.ResizeBorderThickness = new Thickness(6);
                AttachDetachButton.Content = "Reattach ↙";

                if (restoreGeometry)
                {
                    var workArea = _windowService.GetWorkAreaForDipPoint(_placement.DetachedLeftDip, _placement.DetachedTopDip);
                    _placement = PanelPlacementCalculator.RecoverDetachedPlacement(_placement, workArea);
                    Width = _placement.DetachedWidthDip;
                    Height = _placement.DetachedHeightDip;
                    Left = _placement.DetachedLeftDip;
                    Top = _placement.DetachedTopDip;
                }
            }
            else
            {
                Topmost = true;
                ResizeMode = ResizeMode.NoResize;
                PanelWindowChrome.ResizeBorderThickness = new Thickness(0);
                AttachDetachButton.Content = "Detach ↗";
                Width = 376;
                Height = 500;
                PositionBesideMascotIfAttached();
            }

            ConfigureDwmWindowBackdrop();
        }
        finally
        {
            _suppressPlacementSave = false;
        }
    }

    private void OnAttachDetachClick(object sender, RoutedEventArgs e)
    {
        if (_placement.IsDetached)
        {
            _placement = _placement with { IsDetached = false };
            _notificationRepository.SavePanelPlacement(_placement);
            ApplyAttachmentModeUi(restoreGeometry: true);
        }
        else
        {
            var workArea = _windowService.GetWorkAreaForWindow(this);
            _placement = new NotificationPanelPlacement(
                IsDetached: true,
                DetachedLeftDip: Left,
                DetachedTopDip: Top,
                DetachedWidthDip: Math.Max(360, Width),
                DetachedHeightDip: Math.Max(460, Height),
                MonitorId: workArea.MonitorId);

            _placement = PanelPlacementCalculator.RecoverDetachedPlacement(_placement, workArea);
            _notificationRepository.SavePanelPlacement(_placement);
            ApplyAttachmentModeUi(restoreGeometry: true);
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        if (!_placement.IsDetached)
        {
            var workArea = _windowService.GetWorkAreaForWindow(this);
            _placement = new NotificationPanelPlacement(
                IsDetached: true,
                DetachedLeftDip: Left,
                DetachedTopDip: Top,
                DetachedWidthDip: Width,
                DetachedHeightDip: Height,
                MonitorId: workArea.MonitorId);
            ApplyAttachmentModeUi(restoreGeometry: false);
        }

        try
        {
            DragMove();
            SaveCurrentDetachedPlacement();
        }
        catch
        {
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_suppressPlacementSave || !_placement.IsDetached || !IsLoaded) return;
        SaveCurrentDetachedPlacement();
    }

    private void SaveCurrentDetachedPlacement()
    {
        if (!_placement.IsDetached) return;
        var workArea = _windowService.GetWorkAreaForWindow(this);
        _placement = new NotificationPanelPlacement(
            IsDetached: true,
            DetachedLeftDip: Left,
            DetachedTopDip: Top,
            DetachedWidthDip: ActualWidth > 0 ? ActualWidth : Width,
            DetachedHeightDip: ActualHeight > 0 ? ActualHeight : Height,
            MonitorId: workArea.MonitorId);
        _notificationRepository.SavePanelPlacement(_placement);
    }

    public void ApplyAppearance(PanelAppearance panel, CharacterAppearance character)
    {
        _characterAppearance = AppearanceValidator.Normalize(character);
        _panelAppearance = AppearanceValidator.Normalize(panel, _characterAppearance.AccentColor);

        ConfigureDwmWindowBackdrop();
        _currentTokens = GlassDesignSystem.ApplyToApplicationResources(
            _panelAppearance,
            _characterAppearance,
            _isDwmBackdropActive);

        if (!IsLoaded)
        {
            return;
        }

        OuterPanelBorder.Background = _currentTokens.Level1WindowFillBrush;
        OuterPanelBorder.BorderBrush = _currentTokens.Level1WindowRimBrush;
        OuterPanelBorder.CornerRadius = _currentTokens.WindowCornerRadius;
        HeaderTitleText.Foreground = _currentTokens.PrimaryTextBrush;
        EmptyInboxState.Background = _currentTokens.Level2ContainerFillBrush;
        EmptyInboxState.BorderBrush = _currentTokens.Level2ContainerBorderBrush;
        EmptyInboxState.CornerRadius = _currentTokens.SectionCornerRadius;

        UpdateTabButtonStyles();
        RefreshInboxView();
    }

    private void UpdateTabButtonStyles()
    {
        var primaryStyle = TryFindResource("GlassPrimaryButtonStyle") as Style;
        var secondaryStyle = TryFindResource("GlassSecondaryButtonStyle") as Style;
        if (primaryStyle is null || secondaryStyle is null) return;

        InboxTabButton.Style = _activeMainTabIndex == 0 ? primaryStyle : secondaryStyle;
        WorkspaceTabButton.Style = _activeMainTabIndex == 1 ? primaryStyle : secondaryStyle;
        CompanionTabButton.Style = _activeMainTabIndex == 2 ? primaryStyle : secondaryStyle;

        SubLauncherButton.Style = _activeWorkspaceSubTabIndex == 0 ? primaryStyle : secondaryStyle;
        SubTasksButton.Style = _activeWorkspaceSubTabIndex == 1 ? primaryStyle : secondaryStyle;
        SubNotesButton.Style = _activeWorkspaceSubTabIndex == 2 ? primaryStyle : secondaryStyle;
    }

    private void OnShowInboxTabClick(object sender, RoutedEventArgs e)
    {
        FlushActiveNoteAutoSave();
        _activeMainTabIndex = 0;
        InboxViewGrid.Visibility = Visibility.Visible;
        WorkspaceViewGrid.Visibility = Visibility.Collapsed;
        CompanionViewPanel.Visibility = Visibility.Collapsed;
        UpdateTabButtonStyles();
        RefreshInboxView();
    }

    private void OnShowWorkspaceTabClick(object sender, RoutedEventArgs e) =>
        ShowWorkspaceSubTab(_activeWorkspaceSubTabIndex);

    private void OnShowCompanionTabClick(object sender, RoutedEventArgs e) => ShowCompanionTab();

    public void ShowCompanionTab()
    {
        FlushActiveNoteAutoSave();
        _activeMainTabIndex = 2;
        InboxViewGrid.Visibility = Visibility.Collapsed;
        WorkspaceViewGrid.Visibility = Visibility.Collapsed;
        CompanionViewPanel.Visibility = Visibility.Visible;
        UpdateTabButtonStyles();
        RefreshDiagnosticsView();
    }

    public void ShowWorkspaceSubTab(int subTabIndex)
    {
        FlushActiveNoteAutoSave();
        _activeMainTabIndex = 1;
        _activeWorkspaceSubTabIndex = Math.Clamp(subTabIndex, 0, 2);

        InboxViewGrid.Visibility = Visibility.Collapsed;
        WorkspaceViewGrid.Visibility = Visibility.Visible;
        CompanionViewPanel.Visibility = Visibility.Collapsed;

        LauncherSubView.Visibility = _activeWorkspaceSubTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        TasksSubView.Visibility = _activeWorkspaceSubTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        NotesSubView.Visibility = _activeWorkspaceSubTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        UpdateTabButtonStyles();
        RefreshWorkspaceViews();

        if (_activeWorkspaceSubTabIndex == 0)
        {
            _diagnostics.RecordCustom("LauncherOpened", "Opened Quick Launcher in Companion Panel");
            LauncherSearchTextBox.Focus();
        }
    }

    private void OnShowLauncherSubTabClick(object sender, RoutedEventArgs e) => ShowWorkspaceSubTab(0);
    private void OnShowTasksSubTabClick(object sender, RoutedEventArgs e) => ShowWorkspaceSubTab(1);
    private void OnShowNotesSubTabClick(object sender, RoutedEventArgs e) => ShowWorkspaceSubTab(2);

    private void OnNotificationsSynchronized(object? sender, NotificationSyncResult e)
    {
        Dispatcher.InvokeAsync(RefreshInboxView);
    }

    private void OnPermissionStatusChanged(object? sender, NotificationPermissionStatus e)
    {
        Dispatcher.InvokeAsync(RefreshInboxView);
    }

    public void RefreshInboxView()
    {
        var settings = _notificationCoordinator.Settings;
        var perm = _notificationCoordinator.PermissionStatus;

        if (!settings.IntegrationEnabled)
        {
            PermissionBanner.Visibility = Visibility.Visible;
            PermissionBannerTitle.Text = "Enable Windows Notification Inbox (Opt-In)";
            PermissionBannerBody.Text = "Bloop reads notification titles and messages locally on your PC to show your grouped inbox and animate. Message contents are never logged or sent externally.";
            EnableNotificationsButton.Content = "Enable & Grant Access";
            EnableNotificationsButton.Visibility = Visibility.Visible;
        }
        else if (perm is NotificationPermissionStatus.Denied or NotificationPermissionStatus.Revoked)
        {
            PermissionBanner.Visibility = Visibility.Visible;
            PermissionBannerTitle.Text = perm == NotificationPermissionStatus.Revoked
                ? "Notification Access Revoked"
                : "Notification Access Denied";
            PermissionBannerBody.Text = "Windows notification access is currently turned off. Existing local history is preserved below. Grant notification access in Windows Privacy Settings and click Retry.";
            EnableNotificationsButton.Content = "Retry Access Check";
            EnableNotificationsButton.Visibility = Visibility.Visible;
        }
        else if (perm == NotificationPermissionStatus.Unsupported)
        {
            PermissionBanner.Visibility = Visibility.Visible;
            PermissionBannerTitle.Text = "Notification Listener Unavailable";
            PermissionBannerBody.Text = "Windows UserNotificationListener is not supported on this OS version.";
            EnableNotificationsButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            PermissionBanner.Visibility = Visibility.Collapsed;
        }

        var items = _notificationRepository.GetInboxNotifications(includeLocallyDismissed: false);
        int unreadCount = items.Count(x => !x.IsRead);
        _onUnreadCountChanged(unreadCount);

        if (unreadCount > 0)
        {
            HeaderUnreadPill.Visibility = Visibility.Visible;
            HeaderUnreadText.Text = unreadCount > 99 ? "99+" : unreadCount.ToString();
        }
        else
        {
            HeaderUnreadPill.Visibility = Visibility.Collapsed;
        }

        if (items.Count == 0)
        {
            EmptyInboxState.Visibility = Visibility.Visible;
            NotificationsScrollViewer.Visibility = Visibility.Collapsed;
            NotificationGroupsItemsControl.ItemsSource = null;
            return;
        }

        EmptyInboxState.Visibility = Visibility.Collapsed;
        NotificationsScrollViewer.Visibility = Visibility.Visible;

        var tokens = _currentTokens;
        var highPriorityBarBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 63, 94));
        highPriorityBarBrush.Freeze();

        bool isCompact = _panelAppearance.CardDensity == CardDensity.Compact;
        var groupPadding = isCompact ? new Thickness(7, 6, 7, 6) : new Thickness(10, 9, 10, 9);
        var cardPadding = isCompact ? new Thickness(8, 5, 8, 5) : new Thickness(10, 8, 10, 8);
        var cardMargin = isCompact ? new Thickness(0, 0, 0, 4) : new Thickness(0, 0, 0, 6);

        var groups = items
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Source.AppUserModelId) ? x.Source.DisplayName : x.Source.AppUserModelId, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                string displayName = string.IsNullOrWhiteSpace(first.Source.DisplayName) ? "Windows App" : first.Source.DisplayName;
                string monogram = BuildMonogram(displayName);
                var icon = TryDecodeIcon(first.Source.LogoPngBytes);
                var cardItems = g
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .Select(x => new NotificationCardViewModel(
                        x.Id,
                        x.OsNotificationId,
                        x.Title,
                        x.Body,
                        FormatTimestamp(x.CreatedAtUtc),
                        x.IsRead,
                        x.IsPresentInOs,
                        x.Importance,
                        x.IsRead ? tokens.Level3CardFillBrush : tokens.Level3CardUnreadFillBrush,
                        x.IsRead ? tokens.Level3CardBorderBrush : tokens.Level3CardUnreadBorderBrush,
                        x.Importance >= NotificationImportance.High ? highPriorityBarBrush : tokens.PrimaryAccentFillBrush,
                        x.IsRead ? tokens.SecondaryTextBrush : tokens.PrimaryTextBrush,
                        tokens.SecondaryTextBrush,
                        tokens.AccentTextBrush,
                        tokens.CardCornerRadius,
                        cardPadding,
                        cardMargin))
                    .ToList();

                return new NotificationGroupViewModel(
                    first.Source.AppUserModelId,
                    displayName,
                    $"{displayName} ({cardItems.Count})",
                    monogram,
                    icon,
                    cardItems,
                    tokens.Level2ContainerFillBrush,
                    tokens.Level2ContainerBorderBrush,
                    tokens.AccentTextBrush,
                    tokens.PrimaryAccentFillBrush,
                    tokens.SectionCornerRadius,
                    groupPadding);
            })
            .ToList();

        NotificationGroupsItemsControl.ItemsSource = groups;
    }

    private static string BuildMonogram(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "N";
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
        }
        return char.ToUpperInvariant(displayName[0]).ToString();
    }

    private static ImageSource? TryDecodeIcon(byte[]? pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(pngBytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTimestamp(DateTimeOffset createdUtc)
    {
        var local = createdUtc.ToLocalTime();
        var age = DateTimeOffset.Now - local;
        if (age.TotalMinutes < 1) return "Just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (local.Date == DateTimeOffset.Now.Date) return local.ToString("HH:mm");
        return local.ToString("MMM d, HH:mm");
    }

    private async void OnEnableNotificationsClick(object sender, RoutedEventArgs e)
    {
        await _notificationCoordinator.EnableAndRequestPermissionAsync();
        RefreshInboxView();
    }

    private async void OnSyncNowClick(object sender, RoutedEventArgs e)
    {
        await _notificationCoordinator.SynchronizeNowAsync(NotificationSyncTrigger.Manual);
        RefreshInboxView();
    }

    private void OnMarkAllReadClick(object sender, RoutedEventArgs e)
    {
        _notificationRepository.MarkAllRead();
        _notificationCoordinator.NotifyUserInteraction();
        RefreshInboxView();
    }

    private void OnClearLocalHistoryClick(object sender, RoutedEventArgs e)
    {
        _notificationRepository.ClearLocalHistory();
        _notificationCoordinator.NotifyUserInteraction();
        RefreshInboxView();
    }

    private void OnDismissGroupClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string aumid } && !string.IsNullOrWhiteSpace(aumid))
        {
            _notificationRepository.DismissAppGroupLocally(aumid);
            _notificationCoordinator.NotifyUserInteraction();
            RefreshInboxView();
        }
    }

    private void OnToggleReadClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string id } && !string.IsNullOrWhiteSpace(id))
        {
            var current = _notificationRepository.GetInboxNotifications().FirstOrDefault(x => x.Id == id);
            if (current is not null)
            {
                _notificationRepository.MarkRead(id, !current.IsRead);
                _notificationCoordinator.NotifyUserInteraction();
                RefreshInboxView();
            }
        }
    }

    private void OnDismissLocallyClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string id } && !string.IsNullOrWhiteSpace(id))
        {
            _notificationRepository.DismissLocally(id);
            _notificationCoordinator.NotifyUserInteraction();
            RefreshInboxView();
        }
    }

    private void OnDismissOsClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: NotificationCardViewModel card })
        {
            _notificationCoordinator.DismissFromWindowsAndMarkLocally(card.Id, card.OsNotificationId, alsoDismissLocally: false);
            RefreshInboxView();
        }
    }

    private void OnDiagnosticEntryAdded(object? sender, string line)
    {
        Dispatcher.InvokeAsync(RefreshDiagnosticsView);
    }

    private void RefreshDiagnosticsView()
    {
        var entries = _diagnostics.GetRecentEntries();
        if (entries.Count == 0) return;
        var tail = entries.TakeLast(10);
        DiagnosticsText.Text = string.Join(Environment.NewLine, tail);
    }

    public void UpdateSchedulerStatus(string statusLine)
    {
        Dispatcher.InvokeAsync(() =>
        {
            SchedulerStatusText.Text = statusLine;
        });
    }

    public void UpdateAwarenessStatus(string awarenessSummary)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (AwarenessStatusText is not null)
            {
                AwarenessStatusText.Text = awarenessSummary;
            }
        });
    }

    private void OnHappyClick(object sender, RoutedEventArgs e) =>
        _personality.TryTransition(MascotState.Happy, force: true);

    private void OnSurprisedClick(object sender, RoutedEventArgs e) =>
        _personality.TryTransition(MascotState.Surprised, force: true);

    private void OnCuriousClick(object sender, RoutedEventArgs e) =>
        _personality.TryTransition(MascotState.Curious, force: true);

    private void OnSleepyClick(object sender, RoutedEventArgs e) =>
        _personality.TryTransition(MascotState.Sleepy, force: true);

    private void OnAnnoyedClick(object sender, RoutedEventArgs e) =>
        _personality.TryTransition(MascotState.Annoyed, force: true);

    private void OnPeekabooClick(object sender, RoutedEventArgs e) =>
        _onPeekabooTrigger();

    private void OnDevAutonomousHopClick(object sender, RoutedEventArgs e) =>
        _onDevHopTrigger();

    public void ReportAnimationAck(string stateName, string? commandId)
    {
        Dispatcher.Invoke(() =>
        {
            AckStatusText.Text = $"Ack: '{stateName}' active ({DateTime.Now:HH:mm:ss})";
        });
    }

    public void RefreshWorkspaceViews()
    {
        RefreshLauncherResults(forceRefreshCatalog: false);
        RefreshTasksAndTimerUi();
        RefreshNotesListAndEditor(keepActiveSelection: true);
    }

    // =========================================================================
    // M7.1: Quick Launcher Handlers
    // =========================================================================

    private void RefreshLauncherResults(bool forceRefreshCatalog)
    {
        if (_launcherService is null || LauncherResultsListBox is null) return;

        if (forceRefreshCatalog)
        {
            _ = _launcherService.GetCatalogSnapshot(forceRefresh: true);
        }

        string query = LauncherSearchTextBox?.Text?.Trim() ?? string.Empty;
        var results = _launcherService.Search(query, maxResults: 30);

        if (results.Count == 0)
        {
            LauncherEmptyState.Visibility = Visibility.Visible;
            LauncherResultsListBox.Visibility = Visibility.Collapsed;
            LauncherResultsListBox.ItemsSource = null;
            LauncherEmptyText.Text = string.IsNullOrEmpty(query)
                ? "No Start Menu applications or recent items found."
                : $"No matching results for '{query}'.";
            return;
        }

        LauncherEmptyState.Visibility = Visibility.Collapsed;
        LauncherResultsListBox.Visibility = Visibility.Visible;
        LauncherResultsListBox.ItemsSource = results;
        LauncherResultsListBox.SelectedIndex = 0;
    }

    private void OnLauncherSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshLauncherResults(forceRefreshCatalog: false);
    }

    private void OnLauncherSearchPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(LauncherSearchTextBox.Text))
            {
                LauncherSearchTextBox.Clear();
            }
            else
            {
                _diagnostics.RecordCustom("LauncherClosed", "Closed Quick Launcher via Escape");
                Hide();
            }
            return;
        }

        if (e.Key == Key.Down && LauncherResultsListBox.Items.Count > 0)
        {
            e.Handled = true;
            int next = Math.Min(LauncherResultsListBox.Items.Count - 1, Math.Max(0, LauncherResultsListBox.SelectedIndex + 1));
            LauncherResultsListBox.SelectedIndex = next;
            LauncherResultsListBox.ScrollIntoView(LauncherResultsListBox.SelectedItem);
        }
        else if (e.Key == Key.Up && LauncherResultsListBox.Items.Count > 0)
        {
            e.Handled = true;
            int prev = Math.Max(0, LauncherResultsListBox.SelectedIndex - 1);
            LauncherResultsListBox.SelectedIndex = prev;
            LauncherResultsListBox.ScrollIntoView(LauncherResultsListBox.SelectedItem);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            LaunchSelectedLauncherEntry();
        }
    }

    private void OnLauncherListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            LaunchSelectedLauncherEntry();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _diagnostics.RecordCustom("LauncherClosed", "Closed Quick Launcher via Escape");
            Hide();
        }
    }

    private void OnLauncherResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        LaunchSelectedLauncherEntry();
    }

    private void OnLaunchItemButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: LauncherEntry entry })
        {
            ExecuteLaunchEntry(entry);
        }
    }

    private void LaunchSelectedLauncherEntry()
    {
        if (LauncherResultsListBox.SelectedItem is LauncherEntry entry)
        {
            ExecuteLaunchEntry(entry);
        }
    }

    private void ExecuteLaunchEntry(LauncherEntry entry)
    {
        if (_launcherService is null) return;

        if (_launcherService.TryLaunchEntry(entry, out var err))
        {
            LauncherStatusText.Text = $"Launched: {entry.DisplayName}";
            _personality.TryTransition(MascotState.Happy, force: true);
            RefreshLauncherResults(forceRefreshCatalog: false);
        }
        else
        {
            LauncherStatusText.Text = err ?? "Failed to launch target.";
        }
    }

    private void OnLauncherOpenFileClick(object sender, RoutedEventArgs e)
    {
        if (_launcherService is null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Bloop Quick Launcher — Open File",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            if (_launcherService.TryOpenFilePath(dlg.FileName, out var added, out var err))
            {
                LauncherStatusText.Text = $"Opened file: {added?.DisplayName}";
                RefreshLauncherResults(forceRefreshCatalog: false);
            }
            else
            {
                LauncherStatusText.Text = err ?? "Could not open file.";
            }
        }
    }

    private void OnLauncherOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_launcherService is null) return;

        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Bloop Quick Launcher — Open Folder"
        };
        if (dlg.ShowDialog(this) == true)
        {
            if (_launcherService.TryOpenFolderPath(dlg.FolderName, out var added, out var err))
            {
                LauncherStatusText.Text = $"Opened folder: {added?.DisplayName}";
                RefreshLauncherResults(forceRefreshCatalog: false);
            }
            else
            {
                LauncherStatusText.Text = err ?? "Could not open folder.";
            }
        }
    }

    private void OnLauncherRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshLauncherResults(forceRefreshCatalog: true);
        LauncherStatusText.Text = "Application catalog refreshed.";
    }

    // =========================================================================
    // M7.2: Tasks, Countdown Timer & Reminders Handlers
    // =========================================================================

    private void OnTimerEngineStateChanged(object? sender, CountdownTimerSnapshot snap)
    {
        Dispatcher.InvokeAsync(UpdateTimerDisplayOnly);
    }

    public void UpdateTimerDisplayOnly()
    {
        if (_timerEngine is null || TimerDisplayClockText is null) return;

        var snap = _timerEngine.Current;
        TimerDisplayClockText.Text = CountdownTimerEngine.FormatDisplayClock(snap.RemainingDuration);

        TimerStateBadgeText.Text = snap.State.ToString();
        TimerPauseResumeButton.Content = snap.State == CountdownTimerState.Paused ? "Resume" : "Pause";
    }

    public void RefreshTasksAndTimerUi()
    {
        UpdateTimerDisplayOnly();

        if (_productivityRepository is null || TasksItemsControl is null) return;

        var filter = TaskFilterComboBox?.SelectedIndex switch
        {
            1 => TaskListFilter.Active,
            2 => TaskListFilter.Completed,
            _ => TaskListFilter.All
        };

        var tasks = _productivityRepository.GetTasks(filter);
        TasksItemsControl.ItemsSource = tasks;
        TasksEmptyText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int pendingRems = _productivityRepository.GetReminders(includeDelivered: false).Count;
        if (PendingRemindersBadgeText is not null)
        {
            PendingRemindersBadgeText.Text = $"{pendingRems} reminder{(pendingRems == 1 ? "" : "s")}";
        }
    }

    private TimeSpan ParseRequestedTimerDuration()
    {
        var raw = TimerMinutesTextBox?.Text?.Trim() ?? "25";
        if (raw.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1.0, 24 * 3600.0));
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            return TimeSpan.FromMinutes(Math.Clamp(minutes, 0.05, 24 * 60.0));
        }

        return TimeSpan.FromMinutes(25);
    }

    private void OnTimerStartClick(object sender, RoutedEventArgs e)
    {
        if (_timerEngine is null || _productivityRepository is null) return;

        var dur = ParseRequestedTimerDuration();
        var label = TimerLabelTextBox?.Text?.Trim();
        var snap = _timerEngine.Start(dur, label, DateTimeOffset.UtcNow);
        _productivityRepository.SaveTimerSnapshot(snap);
        UpdateTimerDisplayOnly();
    }

    private void OnTimerPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (_timerEngine is null || _productivityRepository is null) return;

        var now = DateTimeOffset.UtcNow;
        var snap = _timerEngine.Current.State == CountdownTimerState.Paused
            ? _timerEngine.Resume(now)
            : _timerEngine.Pause(now);
        _productivityRepository.SaveTimerSnapshot(snap);
        UpdateTimerDisplayOnly();
    }

    private void OnTimerResetClick(object sender, RoutedEventArgs e)
    {
        if (_timerEngine is null || _productivityRepository is null) return;

        var snap = _timerEngine.Reset(DateTimeOffset.UtcNow);
        _productivityRepository.SaveTimerSnapshot(snap);
        UpdateTimerDisplayOnly();
    }

    private void OnTimerCancelClick(object sender, RoutedEventArgs e)
    {
        if (_timerEngine is null || _productivityRepository is null) return;

        var snap = _timerEngine.Cancel(DateTimeOffset.UtcNow);
        _productivityRepository.SaveTimerSnapshot(snap);
        UpdateTimerDisplayOnly();
    }

    private void OnTaskFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshTasksAndTimerUi();
    }

    private void OnNewTaskTitleKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CreateNewTaskFromUi();
        }
    }

    private void OnAddTaskClick(object sender, RoutedEventArgs e) => CreateNewTaskFromUi();

    private void CreateNewTaskFromUi()
    {
        if (_productivityRepository is null) return;

        var title = NewTaskTitleTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        var prio = NewTaskPriorityComboBox?.SelectedIndex switch
        {
            0 => TaskPriority.Low,
            2 => TaskPriority.High,
            _ => TaskPriority.Normal
        };

        var now = DateTimeOffset.UtcNow;
        var saved = _productivityRepository.SaveTask(new TodoTaskItem(
            Id: string.Empty,
            Title: title,
            Priority: prio,
            CreatedAtUtc: now,
            UpdatedAtUtc: now));

        _diagnostics.RecordCustom("TaskCreated", $"Created task '{saved.Title}' (Priority={saved.Priority})");
        NewTaskTitleTextBox!.Clear();
        RefreshTasksAndTimerUi();
    }

    private void OnAddQuickReminderClick(object sender, RoutedEventArgs e)
    {
        if (_productivityRepository is null) return;

        var title = NewTaskTitleTextBox?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Quick Desktop Reminder";
        }
        else
        {
            NewTaskTitleTextBox!.Clear();
        }

        var prio = NewTaskPriorityComboBox?.SelectedIndex switch
        {
            0 => TaskPriority.Low,
            2 => TaskPriority.High,
            _ => TaskPriority.Normal
        };

        var now = DateTimeOffset.UtcNow;
        var rem = _productivityRepository.SaveReminder(new ReminderItem(
            Id: string.Empty,
            Title: title,
            TriggerAtUtc: now.AddMinutes(10),
            Priority: prio,
            CreatedAtUtc: now));

        _diagnostics.RecordCustom("ReminderScheduled", $"Scheduled reminder '{rem.Title}' for {rem.TriggerAtUtc.ToLocalTime():HH:mm:ss}");
        RefreshTasksAndTimerUi();
    }

    private void OnToggleTaskCompletedClick(object sender, RoutedEventArgs e)
    {
        if (_productivityRepository is null) return;
        if (sender is System.Windows.Controls.CheckBox { Tag: string id } && !string.IsNullOrWhiteSpace(id))
        {
            var existing = _productivityRepository.GetTaskById(id);
            if (existing is not null)
            {
                bool nextCompleted = !existing.IsCompleted;
                var updated = _productivityRepository.SetTaskCompleted(id, nextCompleted, DateTimeOffset.UtcNow);
                _diagnostics.RecordCustom(
                    nextCompleted ? "TaskCompleted" : "TaskUpdated",
                    $"{(nextCompleted ? "Completed" : "Reopened")} task '{updated.Title}'");
                RefreshTasksAndTimerUi();
            }
        }
    }

    private void OnDeleteTaskClick(object sender, RoutedEventArgs e)
    {
        if (_productivityRepository is null) return;
        if (sender is System.Windows.Controls.Button { Tag: string id } && !string.IsNullOrWhiteSpace(id))
        {
            if (_productivityRepository.DeleteTask(id))
            {
                _diagnostics.RecordCustom("TaskDeleted", $"Deleted task '{id}'");
                RefreshTasksAndTimerUi();
            }
        }
    }

    // =========================================================================
    // M7.3: Quick Notes Handlers (with Debounced Auto-Save + Close Flush)
    // =========================================================================

    private void RefreshNotesListAndEditor(bool keepActiveSelection)
    {
        if (_productivityRepository is null || NotesListBox is null) return;

        string query = NotesSearchTextBox?.Text?.Trim() ?? string.Empty;
        var notes = _productivityRepository.GetNotes(query);

        _suppressNoteEditorEvents = true;
        try
        {
            NotesListBox.ItemsSource = notes;

            QuickNoteItem? selected = null;
            if (keepActiveSelection && !string.IsNullOrWhiteSpace(_activeNoteId))
            {
                selected = notes.FirstOrDefault(n => n.Id == _activeNoteId);
            }
            selected ??= notes.FirstOrDefault();

            if (selected is not null)
            {
                _activeNoteId = selected.Id;
                NotesListBox.SelectedItem = selected;
                NoteTitleEditorTextBox.Text = selected.Title;
                NoteContentEditorTextBox.Text = selected.Content;
                NoteAutoSaveStatusText.Text = $"Saved • Last modified {selected.UpdatedAtUtc.ToLocalTime():HH:mm:ss}";
            }
            else
            {
                _activeNoteId = null;
                NoteTitleEditorTextBox.Text = string.Empty;
                NoteContentEditorTextBox.Text = string.Empty;
                NoteAutoSaveStatusText.Text = "Type below or click '+ New' to create a note";
            }
        }
        finally
        {
            _suppressNoteEditorEvents = false;
        }
    }

    private void OnNotesSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        FlushActiveNoteAutoSave();
        string query = NotesSearchTextBox?.Text?.Trim() ?? string.Empty;
        _diagnostics.RecordCustom("NoteSearchExecuted", $"Note search query='{query}'");
        RefreshNotesListAndEditor(keepActiveSelection: true);
    }

    private void OnSelectedNoteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNoteEditorEvents) return;

        FlushActiveNoteAutoSave();
        if (NotesListBox.SelectedItem is QuickNoteItem note)
        {
            _suppressNoteEditorEvents = true;
            try
            {
                _activeNoteId = note.Id;
                NoteTitleEditorTextBox.Text = note.Title;
                NoteContentEditorTextBox.Text = note.Content;
                NoteAutoSaveStatusText.Text = $"Loaded • Last modified {note.UpdatedAtUtc.ToLocalTime():HH:mm:ss}";
            }
            finally
            {
                _suppressNoteEditorEvents = false;
            }
        }
    }

    private void OnNewNoteClick(object sender, RoutedEventArgs e)
    {
        if (_productivityRepository is null) return;

        FlushActiveNoteAutoSave();
        var now = DateTimeOffset.UtcNow;
        var created = _productivityRepository.SaveNote(new QuickNoteItem(
            Id: string.Empty,
            Title: "New Quick Note",
            Content: string.Empty,
            CreatedAtUtc: now,
            UpdatedAtUtc: now));

        _activeNoteId = created.Id;
        _diagnostics.RecordCustom("NoteCreated", $"Created quick note '{created.Id}'");
        RefreshNotesListAndEditor(keepActiveSelection: true);
        NoteContentEditorTextBox.Focus();
    }

    private void OnDeleteNoteClick(object sender, RoutedEventArgs e)
    {
        if (_productivityRepository is null || string.IsNullOrWhiteSpace(_activeNoteId)) return;

        _noteAutoSaveDebounceTimer.Stop();
        string idToDelete = _activeNoteId;
        _activeNoteId = null;

        if (_productivityRepository.DeleteNote(idToDelete))
        {
            _diagnostics.RecordCustom("NoteDeleted", $"Deleted quick note '{idToDelete}'");
            RefreshNotesListAndEditor(keepActiveSelection: false);
        }
    }

    private void OnNoteEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressNoteEditorEvents || _productivityRepository is null) return;

        NoteAutoSaveStatusText.Text = "Auto-saving...";
        _noteAutoSaveDebounceTimer.Stop();
        _noteAutoSaveDebounceTimer.Start();
    }

    public void FlushActiveNoteAutoSave()
    {
        _noteAutoSaveDebounceTimer.Stop();
        if (_suppressNoteEditorEvents || _productivityRepository is null) return;

        string title = NoteTitleEditorTextBox?.Text ?? string.Empty;
        string content = NoteContentEditorTextBox?.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_activeNoteId) &&
            string.IsNullOrWhiteSpace(title) &&
            string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var existing = !string.IsNullOrWhiteSpace(_activeNoteId)
            ? _productivityRepository.GetNoteById(_activeNoteId)
            : null;

        if (existing is not null &&
            string.Equals(existing.Title, title.Trim(), StringComparison.Ordinal) &&
            string.Equals(existing.Content, content, StringComparison.Ordinal))
        {
            return;
        }

        var saved = _productivityRepository.SaveNote(new QuickNoteItem(
            Id: _activeNoteId ?? string.Empty,
            Title: title,
            Content: content,
            CreatedAtUtc: existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc: now));

        bool wasNew = string.IsNullOrWhiteSpace(_activeNoteId);
        _activeNoteId = saved.Id;
        _diagnostics.RecordCustom(
            wasNew ? "NoteCreated" : "NoteAutoSaved",
            $"{(wasNew ? "Created" : "Auto-saved")} note '{saved.Title}' ({saved.Content.Length} chars)");

        if (NoteAutoSaveStatusText is not null)
        {
            NoteAutoSaveStatusText.Text = $"Auto-saved at {saved.UpdatedAtUtc.ToLocalTime():HH:mm:ss}";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        FlushActiveNoteAutoSave();
        Hide();
    }
}
