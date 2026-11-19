using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bloop.Core.Appearance;
using Bloop.Core.Notifications;
using Bloop.Desktop.Services;
using Bloop.Windows.System;

namespace Bloop.Desktop.Windows;

/// <summary>
/// Transient, non-activating Liquid Glass toast bubble window.
/// Renders an organic deep-teal / sapphire translucent capsule beside Bloop with
/// priority-differentiated edge illumination, smooth entrance/exit animations,
/// DWM Acrylic backdrop support (with opaque-calibrated fallback), and zero idle overhead when hidden.
/// </summary>
public partial class ToastBubbleWindow : Window
{
    private readonly Window _mascotWindow;
    private readonly WindowService _windowService;
    private readonly DiagnosticsLogService _diagnostics;
    private readonly Action _onToastBodyClicked;
    private readonly DispatcherTimer _autoDismissTimer;

    private IntPtr _hwnd;
    private bool _isPointerHovered;
    private bool _isExiting;
    private double _lastEntranceOffsetX = 14.0;
    private DateTimeOffset _currentToastTimestampUtc = DateTimeOffset.UtcNow;

    public bool IsToastVisible => IsVisible && !_isExiting;

    public ToastBubbleWindow(
        Window mascotWindow,
        WindowService windowService,
        DiagnosticsLogService diagnostics,
        Action onToastBodyClicked)
    {
        InitializeComponent();
        _mascotWindow = mascotWindow;
        _windowService = windowService;
        _diagnostics = diagnostics;
        _onToastBodyClicked = onToastBodyClicked;

        _autoDismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4.6)
        };
        _autoDismissTimer.Tick += OnAutoDismissTimerTick;

        SourceInitialized += OnWindowSourceInitialized;
        Closed += (_, _) => _autoDismissTimer.Stop();
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        var source = HwndSource.FromHwnd(_hwnd);
        if (source?.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        source?.AddHook(WndProc);

        _ = NativeMethods.ApplyGlassWindowBackdrop(
            _hwnd,
            enableAcrylicBackdrop: true,
            isLightTheme: false,
            isToolWindow: true,
            isNoActivate: true);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(NativeMethods.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    public void ShowToast(
        NotificationReactionDecision decision,
        CharacterAppearance characterAppearance,
        PanelAppearance panelAppearance)
    {
        _autoDismissTimer.Stop();
        _isExiting = false;

        string appName = string.IsNullOrWhiteSpace(decision.PreviewAppDisplayName)
            ? "Bloop Companion"
            : decision.PreviewAppDisplayName.Trim();
        string title = string.IsNullOrWhiteSpace(decision.PreviewTitle)
            ? (decision.BatchCount > 1 ? $"{decision.BatchCount} new notifications" : "New notification")
            : decision.PreviewTitle.Trim();
        string body = decision.PreviewBody?.Trim() ?? string.Empty;

        AppNameText.Text = appName;
        ToastTitleText.Text = title;

        if (string.IsNullOrWhiteSpace(body))
        {
            ToastBodyText.Visibility = Visibility.Collapsed;
            ToastBodyText.Text = string.Empty;
        }
        else
        {
            ToastBodyText.Visibility = Visibility.Visible;
            ToastBodyText.Text = body;
        }

        _currentToastTimestampUtc = decision.PreviewTimestampUtc ?? DateTimeOffset.UtcNow;
        TimestampText.Text = FormatRelativeTime(_currentToastTimestampUtc);

        if (decision.BatchCount > 1)
        {
            BatchCountPillText.Text = $"+{decision.BatchCount - 1} more";
            BatchCountPillText.Visibility = Visibility.Visible;
        }
        else
        {
            BatchCountPillText.Visibility = Visibility.Collapsed;
        }

        // App icon or monogram
        var logoImg = TryDecodeIcon(decision.PreviewLogoPngBytes);
        if (logoImg is not null)
        {
            AppLogoImage.Source = logoImg;
            AppLogoImage.Visibility = Visibility.Visible;
            AppMonogramText.Visibility = Visibility.Collapsed;
        }
        else
        {
            AppLogoImage.Source = null;
            AppLogoImage.Visibility = Visibility.Collapsed;
            AppMonogramText.Text = BuildMonogram(appName);
            AppMonogramText.Visibility = Visibility.Visible;
        }

        ApplyPriorityVisualTreatment(decision.HighestImportance, characterAppearance, panelAppearance);

        if (!IsVisible)
        {
            Show();
        }
        if (_hwnd != IntPtr.Zero)
        {
            _ = NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }

        UpdateLayout();
        PositionBesideMascot();
        PlayEntranceAnimation(decision.HighestImportance);

        double displaySec = decision.HighestImportance switch
        {
            NotificationImportance.Critical => 6.2,
            NotificationImportance.High => 5.2,
            _ => 4.2
        };
        _autoDismissTimer.Interval = TimeSpan.FromSeconds(displaySec);
        _autoDismissTimer.Start();

        _diagnostics.RecordCustom(
            "LiquidGlassToastShown",
            $"App='{appName}', Importance={decision.HighestImportance}, Batch={decision.BatchCount}, Pos=({Left:0},{Top:0}), Size=({ActualWidth:0}x{ActualHeight:0})");
    }

    public void PositionBesideMascot()
    {
        if (!IsVisible && !_isExiting) return;

        var workArea = _windowService.GetWorkAreaForWindow(_mascotWindow);
        double toastW = ActualWidth > 0 ? ActualWidth : Width;
        double toastH = ActualHeight > 0 ? ActualHeight : 108.0;

        var (left, top, offsetX) = PanelPlacementCalculator.CalculateToastBubblePosition(
            _mascotWindow.Left,
            _mascotWindow.Top,
            _mascotWindow.Width,
            _mascotWindow.Height,
            toastW,
            toastH,
            workArea);

        Left = left;
        Top = top;
        _lastEntranceOffsetX = offsetX;
    }

    public void DismissImmediately()
    {
        _autoDismissTimer.Stop();
        _isExiting = false;
        AnimatedRootGrid.BeginAnimation(OpacityProperty, null);
        BubbleTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        BubbleTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        if (IsVisible)
        {
            Hide();
        }
    }

    public void DismissWithAnimation()
    {
        _autoDismissTimer.Stop();
        if (!IsVisible || _isExiting)
        {
            return;
        }

        if (SystemParameters.ClientAreaAnimation == false)
        {
            DismissImmediately();
            return;
        }

        _isExiting = true;
        var dur = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fadeOut = new DoubleAnimation(AnimatedRootGrid.Opacity, 0.0, dur) { EasingFunction = ease };
        var slideX = new DoubleAnimation(0.0, _lastEntranceOffsetX * 0.65, dur) { EasingFunction = ease };
        var slideY = new DoubleAnimation(0.0, 5.0, dur) { EasingFunction = ease };

        fadeOut.Completed += (_, _) =>
        {
            if (_isExiting)
            {
                _isExiting = false;
                Hide();
            }
        };

        AnimatedRootGrid.BeginAnimation(OpacityProperty, fadeOut);
        BubbleTranslateTransform.BeginAnimation(TranslateTransform.XProperty, slideX);
        BubbleTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideY);
    }

    private void PlayEntranceAnimation(NotificationImportance importance)
    {
        AnimatedRootGrid.BeginAnimation(OpacityProperty, null);
        BubbleTranslateTransform.BeginAnimation(TranslateTransform.XProperty, null);
        BubbleTranslateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        BubbleScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BubbleScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (SystemParameters.ClientAreaAnimation == false)
        {
            AnimatedRootGrid.Opacity = 1.0;
            BubbleTranslateTransform.X = 0.0;
            BubbleTranslateTransform.Y = 0.0;
            BubbleScaleTransform.ScaleX = 1.0;
            BubbleScaleTransform.ScaleY = 1.0;
            return;
        }

        bool isPriority = importance >= NotificationImportance.High;
        var dur = new Duration(TimeSpan.FromMilliseconds(isPriority ? 290 : 240));
        var cubicOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var settleOut = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = isPriority ? 0.24 : 0.14 };

        var fadeIn = new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = cubicOut };
        var slideX = new DoubleAnimation(_lastEntranceOffsetX, 0.0, dur) { EasingFunction = settleOut };
        var slideY = new DoubleAnimation(8.0, 0.0, dur) { EasingFunction = cubicOut };
        var scaleIn = new DoubleAnimation(isPriority ? 0.94 : 0.96, 1.0, dur) { EasingFunction = settleOut };

        AnimatedRootGrid.BeginAnimation(OpacityProperty, fadeIn);
        BubbleTranslateTransform.BeginAnimation(TranslateTransform.XProperty, slideX);
        BubbleTranslateTransform.BeginAnimation(TranslateTransform.YProperty, slideY);
        BubbleScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        BubbleScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleIn);
    }

    private void ApplyPriorityVisualTreatment(
        NotificationImportance importance,
        CharacterAppearance character,
        PanelAppearance panel)
    {
        // Deep teal / aquatic sapphire base surface with restrained luminance across both bright and dark wallpapers
        SurfaceStop0.Color = System.Windows.Media.Color.FromArgb(236, 7, 38, 56);
        SurfaceStop1.Color = System.Windows.Media.Color.FromArgb(228, 5, 27, 43);
        SurfaceStop2.Color = System.Windows.Media.Color.FromArgb(240, 4, 19, 32);

        if (importance == NotificationImportance.Critical)
        {
            // Critical: Luminous cyan-amber/coral edge highlight + CRITICAL pill
            RimStop0.Color = System.Windows.Media.Color.FromArgb(220, 251, 113, 133);
            RimStop1.Color = System.Windows.Media.Color.FromArgb(160, 56, 189, 248);
            RimStop2.Color = System.Windows.Media.Color.FromArgb(190, 244, 63, 94);
            InnerGlowStop0.Color = System.Windows.Media.Color.FromArgb(64, 251, 113, 133);
            TopSpecularStop.Color = System.Windows.Media.Color.FromArgb(180, 255, 228, 230);

            PriorityLeftAccentBar.Background = CreateFrozenBrush(251, 113, 133);
            PriorityPillBorder.Background = CreateFrozenBrush(64, 244, 63, 94);
            PriorityPillBorder.BorderBrush = CreateFrozenBrush(160, 251, 113, 133);
            PriorityPillText.Text = "CRITICAL";
            PriorityPillText.Foreground = CreateFrozenBrush(254, 205, 211);
            PriorityPillBorder.Visibility = Visibility.Visible;
            AppNameText.Foreground = CreateFrozenBrush(253, 164, 175);
        }
        else if (importance == NotificationImportance.High)
        {
            // High Priority: Stronger electric cyan / aqua edge illumination + PRIORITY pill
            RimStop0.Color = System.Windows.Media.Color.FromArgb(230, 103, 232, 249);
            RimStop1.Color = System.Windows.Media.Color.FromArgb(170, 56, 189, 248);
            RimStop2.Color = System.Windows.Media.Color.FromArgb(195, 14, 165, 233);
            InnerGlowStop0.Color = System.Windows.Media.Color.FromArgb(72, 56, 189, 248);
            TopSpecularStop.Color = System.Windows.Media.Color.FromArgb(190, 224, 242, 254);

            PriorityLeftAccentBar.Background = CreateFrozenBrush(103, 232, 249);
            PriorityPillBorder.Background = CreateFrozenBrush(56, 14, 165, 233);
            PriorityPillBorder.BorderBrush = CreateFrozenBrush(155, 103, 232, 249);
            PriorityPillText.Text = "PRIORITY";
            PriorityPillText.Foreground = CreateFrozenBrush(186, 230, 253);
            PriorityPillBorder.Visibility = Visibility.Visible;
            AppNameText.Foreground = CreateFrozenBrush(125, 211, 252);
        }
        else
        {
            // Normal Priority: Calm deep-teal liquid glass with subtle cyan rim
            RimStop0.Color = System.Windows.Media.Color.FromArgb(150, 56, 189, 248);
            RimStop1.Color = System.Windows.Media.Color.FromArgb(75, 34, 211, 238);
            RimStop2.Color = System.Windows.Media.Color.FromArgb(115, 2, 132, 199);
            InnerGlowStop0.Color = System.Windows.Media.Color.FromArgb(46, 56, 189, 248);
            TopSpecularStop.Color = System.Windows.Media.Color.FromArgb(135, 255, 255, 255);

            PriorityLeftAccentBar.Background = CreateFrozenBrush(14, 165, 233);
            PriorityPillBorder.Visibility = Visibility.Collapsed;
            AppNameText.Foreground = CreateFrozenBrush(125, 211, 252);
        }
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private void OnAutoDismissTimerTick(object? sender, EventArgs e)
    {
        if (_isPointerHovered)
        {
            return;
        }
        DismissWithAnimation();
    }

    private void OnWindowMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPointerHovered = true;
    }

    private void OnWindowMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _isPointerHovered = false;
    }

    private void OnDismissButtonClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _diagnostics.RecordCustom("LiquidGlassToastDismissed", "Toast dismissed by user click");
        DismissWithAnimation();
    }

    private void OnToastBodyClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && IsInsideButton(dep))
        {
            return;
        }
        DismissWithAnimation();
        _onToastBodyClicked();
    }

    private static bool IsInsideButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private static string BuildMonogram(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "B";
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

    private static string FormatRelativeTime(DateTimeOffset createdUtc)
    {
        var local = createdUtc.ToLocalTime();
        var age = DateTimeOffset.Now - local;
        if (age.TotalSeconds < 45) return "Just now";
        if (age.TotalMinutes < 60) return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        if (local.Date == DateTimeOffset.Now.Date) return local.ToString("HH:mm");
        return local.ToString("MMM d, HH:mm");
    }
}
