using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bloop.Core.Events;
using Bloop.Core.Interfaces;
using Bloop.Core.Models;
using Bloop.Core.Movement;
using Bloop.Core.Personality;
using Bloop.Desktop.Services;
using Bloop.Windows.System;
using Microsoft.Win32;

namespace Bloop.Desktop.Windows;

public partial class MascotWindow : Window
{
    private readonly WebViewService _webViewService;
    private readonly WindowService _windowService;
    private readonly IPersonalityEngine _personality;
    private readonly IMovementController _movement;
    private readonly ISettingsRepository _settingsRepository;
    private readonly DispatcherTimer _companionTimer;

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

    // Active hop flight interpolation state (driven by WPF CompositionTarget.Rendering)
    private bool _isWindowHopActive;
    private HopPlanEventArgs? _activeHopPlan;
    private readonly Stopwatch _hopStopwatch = new();

    private NotificationWindow? _demoPanelWindow;
    private SettingsWindow? _settingsWindow;

    public MascotWindow(
        WebViewService webViewService,
        WindowService windowService,
        IPersonalityEngine personality,
        IMovementController movement,
        ISettingsRepository settingsRepository)
    {
        InitializeComponent();

        _webViewService = webViewService;
        _windowService = windowService;
        _personality = personality;
        _movement = movement;
        _settingsRepository = settingsRepository;

        _webViewService.MessageReceived += OnRendererMessageReceived;
        _webViewService.RendererReady += OnRendererReady;
        _personality.StateChanged += OnPersonalityStateChanged;

        _movement.EdgeCandidateChanged += OnEdgeCandidateChanged;
        _movement.HopRequested += OnHopRequested;

        _windowService.ResetPositionRequested += (_, _) => ResetToFreeBottomRight();
        _windowService.ToggleDemoPanelRequested += (_, _) => ToggleDemoPanel();
        _windowService.OpenSettingsRequested += (_, _) => ShowSettingsWindow();
        _windowService.ExitRequested += (_, _) => System.Windows.Application.Current.Shutdown();

        // Single coordinated 30 Hz companion timer (handles cursor tracking + autonomous movement checks)
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
            // Zero out non-client border area so no frame is drawn around the transparent window
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _windowService.RestoreOrPlaceDefault(this);
        await _webViewService.InitializeAsync(MascotWebView);
        _companionTimer.Start();
    }

    private void OnRendererReady(object? sender, EventArgs e)
    {
        SyncDockingConfigToRenderer();
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

    private void ResetToFreeBottomRight()
    {
        CancelActiveHop();
        _windowService.PlaceAtBottomRight(this);
        SyncDockingConfigToRenderer();
        PositionDemoPanelRelativeToMascot();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            CancelActiveHop();
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
            PositionDemoPanelRelativeToMascot();
        });
    }

    private void OnCompanionTimerTick(object? sender, EventArgs e)
    {
        // 1. Periodic autonomous movement check (~once per second, every 30 ticks)
        _autonomyTickCounter++;
        if (_autonomyTickCounter >= 30)
        {
            _autonomyTickCounter = 0;
            bool panelActive = (_demoPanelWindow?.IsVisible == true) || (_settingsWindow?.IsVisible == true);
            _movement.SetControlPanelActive(panelActive);

            if (!_isDraggingWindow && !_isWindowHopActive)
            {
                var workArea = _windowService.GetWorkAreaForWindow(this);
                _movement.TryTriggerAutonomousHop(DateTimeOffset.UtcNow, Width, Height, workArea);
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
        CancelActiveHop();
        _activeHopPlan = plan;
        _isWindowHopActive = true;
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

        CompositionTarget.Rendering += OnHopFrameRendering;
    }

    private void OnHopFrameRendering(object? sender, EventArgs e)
    {
        if (!_isWindowHopActive || _activeHopPlan is null)
        {
            CancelActiveHop();
            return;
        }

        var plan = _activeHopPlan;
        double elapsedMs = _hopStopwatch.Elapsed.TotalMilliseconds;
        double flightStartMs = plan.AnticipationMs;
        double flightEndMs = plan.AnticipationMs + plan.FlightMs;
        double totalMs = flightEndMs + plan.LandingMs;

        if (elapsedMs <= flightStartMs)
        {
            // Anticipation phase: window remains anchored while character crouches
            return;
        }

        if (elapsedMs <= flightEndMs)
        {
            double t = Math.Clamp((elapsedMs - flightStartMs) / Math.Max(1.0, plan.FlightMs), 0.0, 1.0);
            double smoothT = t * t * (3.0 - 2.0 * t);

            // Add a subtle vertical arc in screen space during long flights
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
            CancelActiveHop();
            _movement.CompleteHop(finalLeft, finalTop, finalEdge);
            _windowService.SavePosition(this);
        }
    }

    private void CancelActiveHop()
    {
        if (_isWindowHopActive)
        {
            CompositionTarget.Rendering -= OnHopFrameRendering;
            _isWindowHopActive = false;
            _activeHopPlan = null;
            _hopStopwatch.Stop();
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
                CancelActiveHop();
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
                    _personality.TryTransition(MascotState.Happy, force: true);
                }
                break;
            }
            case "interaction:double_click":
            {
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

    private void ToggleDemoPanel()
    {
        if (_demoPanelWindow is not null && _demoPanelWindow.IsVisible)
        {
            _demoPanelWindow.Hide();
            _movement.SetControlPanelActive(false);
            return;
        }

        _demoPanelWindow ??= new NotificationWindow(_personality);
        PositionDemoPanelRelativeToMascot();
        _demoPanelWindow.Show();
        _movement.SetControlPanelActive(true);
    }

    private void PositionDemoPanelRelativeToMascot()
    {
        if (_demoPanelWindow is null || !_demoPanelWindow.IsVisible) return;

        var workArea = _windowService.GetWorkAreaForWindow(this);
        double targetLeft = Left - _demoPanelWindow.Width - 12;
        if (targetLeft < workArea.Left + 12)
        {
            targetLeft = Left + Width + 12;
        }
        double targetTop = Math.Clamp(
            Top + (Height - _demoPanelWindow.Height) * 0.5,
            workArea.Top + 12,
            Math.Max(workArea.Top + 12, workArea.Bottom - _demoPanelWindow.Height - 12)
        );
        _demoPanelWindow.Left = targetLeft;
        _demoPanelWindow.Top = targetTop;
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(this, _windowService, _movement, _settingsRepository);
        _settingsWindow.Show();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        CancelActiveHop();
        _companionTimer.Stop();
        _demoPanelWindow?.Close();
        _settingsWindow?.Close();
        _webViewService.Dispose();
        _windowService.Dispose();
    }
}
