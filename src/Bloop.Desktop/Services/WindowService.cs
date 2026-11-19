using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using Bloop.Core.Behaviours;
using Bloop.Core.Interfaces;
using Bloop.Core.Movement;
using Bloop.Core.Personality;
using Bloop.Windows.System;

namespace Bloop.Desktop.Services;

/// <summary>
/// Manages multi-monitor DPI-aware window placement, screen work-area queries,
/// docking persistence, and the Windows System Tray NotifyIcon.
/// </summary>
public sealed class WindowService : IDisposable
{
    private readonly ISettingsRepository _settings;
    private readonly IPersonalityEngine _personality;
    private readonly IMovementController _movement;
    private readonly BehaviourScheduler _behaviourScheduler;
    private NotifyIcon? _notifyIcon;
    private Icon? _generatedTrayIcon;
    private ToolStripMenuItem? _pauseMenuItem;
    private ToolStripMenuItem? _focusMenuItem;

    public event EventHandler? ResetPositionRequested;
    public event EventHandler? ToggleDemoPanelRequested;
    public event EventHandler<int>? OpenProductivitySubTabRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? DevAutonomousHopRequested;
    public event EventHandler? ToggleFocusModeRequested;
    public event EventHandler? ExitRequested;

    public WindowService(
        ISettingsRepository settings,
        IPersonalityEngine personality,
        IMovementController movement,
        BehaviourScheduler behaviourScheduler)
    {
        _settings = settings;
        _personality = personality;
        _movement = movement;
        _behaviourScheduler = behaviourScheduler;
    }

    public void InitializeSystemTray()
    {
        _generatedTrayIcon = CreateBlueOrbIcon();
        var menu = new ContextMenuStrip();

        menu.Items.Add("Trigger Happy Bounce", null, (_, _) =>
            _personality.TryTransition(MascotState.Happy, force: true));
        menu.Items.Add("Dev: Trigger Autonomous Hop", null, (_, _) =>
            DevAutonomousHopRequested?.Invoke(this, EventArgs.Empty));
        _pauseMenuItem = new ToolStripMenuItem("Pause Autonomous Behaviours", null, (_, _) =>
        {
            bool nextPaused = !_behaviourScheduler.Settings.GlobalBehavioursPaused;
            var updated = _behaviourScheduler.Settings with { GlobalBehavioursPaused = nextPaused };
            _behaviourScheduler.UpdateSettings(updated);
            _settings.SaveBehaviourSettings(updated);
            if (_pauseMenuItem is not null) _pauseMenuItem.Checked = nextPaused;
        })
        {
            Checked = _behaviourScheduler.Settings.GlobalBehavioursPaused
        };
        menu.Items.Add(_pauseMenuItem);
        _focusMenuItem = new ToolStripMenuItem("Focus Mode (Quiet Companion)", null, (_, _) =>
            ToggleFocusModeRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(_focusMenuItem);
        menu.Items.Add("Toggle Companion Panel", null, (_, _) =>
            ToggleDemoPanelRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Quick Launcher...", null, (_, _) =>
            OpenProductivitySubTabRequested?.Invoke(this, 0));
        menu.Items.Add("Tasks, Timer & Reminders...", null, (_, _) =>
            OpenProductivitySubTabRequested?.Invoke(this, 1));
        menu.Items.Add("Quick Notes...", null, (_, _) =>
            OpenProductivitySubTabRequested?.Invoke(this, 2));
        menu.Items.Add("Reset Mascot Position", null, (_, _) =>
            ResetPositionRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Settings...", null, (_, _) =>
            OpenSettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Bloop", null, (_, _) =>
            ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new NotifyIcon
        {
            Icon = _generatedTrayIcon,
            Text = "Bloop — Desktop Companion",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) =>
            ToggleDemoPanelRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateFocusTrayCheckedState(bool isFocusModeActive)
    {
        if (_focusMenuItem is not null)
        {
            _focusMenuItem.Checked = isFocusModeActive;
        }
    }

    public WorkAreaBounds GetWorkAreaForWindow(Window window, double? overrideLeftDip = null, double? overrideTopDip = null)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        double leftDip = overrideLeftDip ?? window.Left;
        double topDip = overrideTopDip ?? window.Top;

        int centerPxX = (int)((leftDip + window.Width * 0.5) * scaleX);
        int centerPxY = (int)((topDip + window.Height * 0.5) * scaleY);

        var (wLeft, wTop, wRight, wBottom, devName) = NativeMethods.GetMonitorWorkAreaAndDevice(centerPxX, centerPxY);
        return new WorkAreaBounds(
            wLeft / scaleX,
            wTop / scaleY,
            wRight / scaleX,
            wBottom / scaleY,
            devName
        );
    }

    public WorkAreaBounds GetWorkAreaForDipPoint(double leftDip, double topDip)
    {
        var (wLeft, wTop, wRight, wBottom, devName) = NativeMethods.GetMonitorWorkAreaAndDevice((int)leftDip + 160, (int)topDip + 160);
        return new WorkAreaBounds(wLeft, wTop, wRight, wBottom, devName);
    }

    public ForegroundWindowGeometry GetForegroundWindowGeometry(Window mascotWindow, IntPtr mascotHwnd, IntPtr panelHwnd, IntPtr settingsHwnd)
    {
        var dpi = VisualTreeHelper.GetDpi(mascotWindow);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        var info = NativeMethods.GetForegroundWindowInfo(mascotHwnd, panelHwnd, settingsHwnd);
        if (!info.IsValid)
        {
            return default;
        }

        return new ForegroundWindowGeometry(
            IsValid: true,
            LeftDip: info.Bounds.Left / scaleX,
            TopDip: info.Bounds.Top / scaleY,
            RightDip: info.Bounds.Right / scaleX,
            BottomDip: info.Bounds.Bottom / scaleY,
            IsMinimized: info.IsMinimized,
            IsMaximized: info.IsMaximized,
            IsFullscreen: info.IsFullscreen,
            MonitorId: info.DeviceName
        );
    }

    public PersistedMascotPlacement RestoreOrPlaceDefault(Window window)
    {
        var placement = _settings.LoadMascotPlacement();
        window.Topmost = placement.AlwaysOnTop;

        if (placement.HasSavedPosition)
        {
            var workArea = GetWorkAreaForWindow(window, placement.Left, placement.Top);
            if (placement.DockedEdge != DockingEdge.None && _movement.Settings.EdgeDockingEnabled)
            {
                var dockPos = _movement.CalculateDockingPosition(
                    placement.DockedEdge,
                    placement.Left,
                    placement.Top,
                    window.Width,
                    window.Height,
                    workArea
                );
                window.Left = dockPos.WindowLeftDip;
                window.Top = dockPos.WindowTopDip;
                _movement.SyncPosition(window.Left, window.Top, workArea.MonitorId, placement.DockedEdge);
                SavePlacement(window, placement.DockedEdge, workArea.MonitorId);
                return placement with { Left = window.Left, Top = window.Top, MonitorId = workArea.MonitorId };
            }

            var clamped = ClampToNearestMonitorDip(window, placement.Left, placement.Top);
            window.Left = clamped.Left;
            window.Top = clamped.Top;
            _movement.SyncPosition(window.Left, window.Top, workArea.MonitorId, DockingEdge.None);
            SavePlacement(window, DockingEdge.None, workArea.MonitorId);
            return placement with { Left = window.Left, Top = window.Top, DockedEdge = DockingEdge.None, MonitorId = workArea.MonitorId };
        }

        PlaceAtBottomRight(window);
        return _settings.LoadMascotPlacement();
    }

    public void PlaceAtBottomRight(Window window)
    {
        var workArea = SystemParameters.WorkArea;
        window.Left = workArea.Right - window.Width - 36;
        window.Top = workArea.Bottom - window.Height - 36;
        var monitorBounds = GetWorkAreaForWindow(window, window.Left, window.Top);
        _movement.SyncPosition(window.Left, window.Top, monitorBounds.MonitorId, DockingEdge.None);
        SavePlacement(window, DockingEdge.None, monitorBounds.MonitorId);
    }

    public (double Left, double Top) ClampToNearestMonitorDip(Window window, double desiredLeftDip, double desiredTopDip)
    {
        var workArea = GetWorkAreaForWindow(window, desiredLeftDip, desiredTopDip);
        double maxLeftDip = workArea.Right - window.Width;
        double maxTopDip = workArea.Bottom - window.Height;

        double clampedLeft = Math.Clamp(desiredLeftDip, workArea.Left, Math.Max(workArea.Left, maxLeftDip));
        double clampedTop = Math.Clamp(desiredTopDip, workArea.Top, Math.Max(workArea.Top, maxTopDip));
        return (clampedLeft, clampedTop);
    }

    public void SavePosition(Window window)
    {
        var bounds = GetWorkAreaForWindow(window);
        SavePlacement(window, _movement.CurrentEdge, bounds.MonitorId);
    }

    public void SavePlacement(Window window, DockingEdge edge, string monitorId)
    {
        _settings.SaveMascotPlacement(window.Left, window.Top, edge, monitorId, window.Topmost);
    }

    private static Icon CreateBlueOrbIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bodyBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(2, 2, 28, 28),
                System.Drawing.Color.FromArgb(255, 56, 189, 248),
                System.Drawing.Color.FromArgb(255, 29, 78, 216),
                65f
            );
            g.FillEllipse(bodyBrush, 2, 2, 28, 28);

            using var eyeBrush = new SolidBrush(System.Drawing.Color.White);
            g.FillEllipse(eyeBrush, 10, 11, 4, 8);
            g.FillEllipse(eyeBrush, 18, 11, 4, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _generatedTrayIcon?.Dispose();
    }
}
