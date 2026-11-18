using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bloop.Core.Appearance;
using Bloop.Core.Interfaces;
using Bloop.Core.Notifications;
using Bloop.Core.Personality;
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
    private bool _isInboxTabActive = true;

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
        Action<int> onUnreadCountChanged)
    {
        InitializeComponent();
        _mascotWindow = mascotWindow;
        _windowService = windowService;
        _personality = personality;
        _notificationCoordinator = notificationCoordinator;
        _notificationRepository = notificationRepository;
        _diagnostics = diagnostics;
        _onDevHopTrigger = onDevHopTrigger;
        _onPeekabooTrigger = onPeekabooTrigger;
        _onUnreadCountChanged = onUnreadCountChanged;

        _placement = _notificationRepository.LoadPanelPlacement();
        _currentTokens = GlassDesignSystem.ComputeTokens(_panelAppearance, _characterAppearance, true);

        _diagnostics.EntryAdded += OnDiagnosticEntryAdded;
        _notificationCoordinator.Synchronized += OnNotificationsSynchronized;
        _notificationCoordinator.PermissionStatusChanged += OnPermissionStatusChanged;

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += (_, _) =>
        {
            ApplyAttachmentModeUi(restoreGeometry: true);
            ApplyAppearance(_panelAppearance, _characterAppearance);
            RefreshInboxView();
            RefreshDiagnosticsView();
        };
        Closed += (_, _) =>
        {
            _diagnostics.EntryAdded -= OnDiagnosticEntryAdded;
            _notificationCoordinator.Synchronized -= OnNotificationsSynchronized;
            _notificationCoordinator.PermissionStatusChanged -= OnPermissionStatusChanged;
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

        if (_isInboxTabActive)
        {
            InboxTabButton.Style = primaryStyle;
            CompanionTabButton.Style = secondaryStyle;
        }
        else
        {
            CompanionTabButton.Style = primaryStyle;
            InboxTabButton.Style = secondaryStyle;
        }
    }

    private void OnShowInboxTabClick(object sender, RoutedEventArgs e)
    {
        _isInboxTabActive = true;
        InboxViewGrid.Visibility = Visibility.Visible;
        CompanionViewPanel.Visibility = Visibility.Collapsed;
        UpdateTabButtonStyles();
        RefreshInboxView();
    }

    private void OnShowCompanionTabClick(object sender, RoutedEventArgs e) => ShowCompanionTab();

    public void ShowCompanionTab()
    {
        _isInboxTabActive = false;
        InboxViewGrid.Visibility = Visibility.Collapsed;
        CompanionViewPanel.Visibility = Visibility.Visible;
        UpdateTabButtonStyles();
        RefreshDiagnosticsView();
    }

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

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();
}
