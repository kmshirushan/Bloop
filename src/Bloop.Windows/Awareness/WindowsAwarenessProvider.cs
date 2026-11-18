using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bloop.Core.Awareness;
using Bloop.Windows.System;
using Microsoft.Win32;

namespace Bloop.Windows.Awareness;

/// <summary>
/// Documented Win32 & SystemEvents provider for foreground window metadata,
/// physical monitor geometry, GetLastInputInfo user idle duration, and session/power state.
/// Strictly metadata-only: zero screen capture, zero OCR, zero keyboard/mouse hooks.
/// </summary>
public sealed class WindowsAwarenessProvider : IAwarenessProvider
{
    private static readonly HashSet<string> ShellWindowClasses = new(
        [
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "NotifyIconOverflowWindow",
            "MultitaskingViewFrame",
            "TaskListThumbnailWnd",
            "ForegroundStaging"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ShellProcessNames = new(
        [
            "SearchHost",
            "StartMenuExperienceHost",
            "ShellExperienceHost",
            "LockApp",
            "TextInputHost"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly StringBuilder _classBuffer = new(256);

    // Cached process identity for the last observed HWND + PID
    private IntPtr _cachedHwnd;
    private uint _cachedPid;
    private string? _cachedProcessName;
    private string? _cachedAppName;
    private string? _cachedClassName;

    private bool _isSessionLocked;
    private bool _isSystemSuspended;
    private bool _disposed;

    public bool IsSessionLocked => _isSessionLocked;
    public bool IsSystemSuspended => _isSystemSuspended;

    public event EventHandler<bool>? SessionLockChanged;
    public event EventHandler<bool>? PowerSuspendChanged;

    public WindowsAwarenessProvider()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public ForegroundWindowObservation ObserveForegroundWindow(ReadOnlySpan<IntPtr> bloopOwnedWindowHandles)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return default;
        }

        for (int i = 0; i < bloopOwnedWindowHandles.Length; i++)
        {
            if (bloopOwnedWindowHandles[i] != IntPtr.Zero && fg == bloopOwnedWindowHandles[i])
            {
                return new ForegroundWindowObservation(
                    WindowHandle: fg,
                    ProcessId: _ownProcessId,
                    ProcessName: "Bloop.Desktop",
                    ApplicationName: "Bloop",
                    WindowClassName: null,
                    IsValidExternalWindow: false,
                    IsBloopOwnedWindow: true,
                    IsShellOrDesktopSurface: false,
                    IsVisible: true,
                    IsMinimized: false,
                    IsMaximized: false,
                    HasCaptionOrThickFrame: false,
                    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    "primary");
            }
        }

        _ = NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == _ownProcessId)
        {
            return new ForegroundWindowObservation(
                WindowHandle: fg,
                ProcessId: pid,
                ProcessName: "Bloop.Desktop",
                ApplicationName: "Bloop",
                WindowClassName: null,
                IsValidExternalWindow: false,
                IsBloopOwnedWindow: true,
                IsShellOrDesktopSurface: false,
                IsVisible: true,
                IsMinimized: false,
                IsMaximized: false,
                HasCaptionOrThickFrame: false,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                "primary");
        }

        bool isDesktopOrShellHwnd = fg == NativeMethods.GetDesktopWindow() || fg == NativeMethods.GetShellWindow();
        var (procName, appName, className) = ResolveWindowIdentityCached(fg, pid);

        bool isShellSurface = isDesktopOrShellHwnd ||
                              (!string.IsNullOrWhiteSpace(className) && ShellWindowClasses.Contains(className)) ||
                              (!string.IsNullOrWhiteSpace(procName) && ShellProcessNames.Contains(procName));

        // Note: Explorer.exe hosting Progman/WorkerW/Shell_TrayWnd is a shell surface,
        // whereas an Explorer file-manager window ("CabinetWClass") is a normal application window.
        if (string.Equals(procName, "explorer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(className, "CabinetWClass", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(className, "ExploreWClass", StringComparison.OrdinalIgnoreCase))
        {
            isShellSurface = true;
        }

        bool isVisible = NativeMethods.IsWindowVisible(fg);
        bool isMinimized = NativeMethods.IsIconic(fg);
        bool isMaximized = NativeMethods.IsZoomed(fg);

        int style = NativeMethods.GetWindowLong(fg, NativeMethods.GWL_STYLE);
        bool hasCaptionOrThickFrame = (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;

        if (NativeMethods.DwmGetWindowAttribute(
                fg,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var winRect,
                Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            _ = NativeMethods.GetWindowRect(fg, out winRect);
        }

        IntPtr hMonitor = NativeMethods.MonitorFromWindow(fg, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var miEx = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        int monLeft = 0, monTop = 0, monRight = 1920, monBottom = 1080;
        int workLeft = 0, workTop = 0, workRight = 1920, workBottom = 1040;
        string monitorId = "primary";

        if (hMonitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(hMonitor, ref miEx))
        {
            monLeft = miEx.rcMonitor.Left;
            monTop = miEx.rcMonitor.Top;
            monRight = miEx.rcMonitor.Right;
            monBottom = miEx.rcMonitor.Bottom;

            workLeft = miEx.rcWork.Left;
            workTop = miEx.rcWork.Top;
            workRight = miEx.rcWork.Right;
            workBottom = miEx.rcWork.Bottom;

            if (!string.IsNullOrWhiteSpace(miEx.szDevice))
            {
                monitorId = miEx.szDevice.Trim();
            }
        }

        return new ForegroundWindowObservation(
            WindowHandle: fg,
            ProcessId: pid,
            ProcessName: procName,
            ApplicationName: appName,
            WindowClassName: className,
            IsValidExternalWindow: !isShellSurface && isVisible,
            IsBloopOwnedWindow: false,
            IsShellOrDesktopSurface: isShellSurface,
            IsVisible: isVisible,
            IsMinimized: isMinimized,
            IsMaximized: isMaximized,
            HasCaptionOrThickFrame: hasCaptionOrThickFrame,
            WindowLeftPx: winRect.Left,
            WindowTopPx: winRect.Top,
            WindowRightPx: winRect.Right,
            WindowBottomPx: winRect.Bottom,
            MonitorLeftPx: monLeft,
            MonitorTopPx: monTop,
            MonitorRightPx: monRight,
            MonitorBottomPx: monBottom,
            WorkAreaLeftPx: workLeft,
            WorkAreaTopPx: workTop,
            WorkAreaRightPx: workRight,
            WorkAreaBottomPx: workBottom,
            MonitorId: monitorId);
    }

    public TimeSpan GetUserIdleDuration()
    {
        var lii = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>()
        };

        if (!NativeMethods.GetLastInputInfo(ref lii))
        {
            return TimeSpan.Zero;
        }

        uint currentTick = unchecked((uint)Environment.TickCount);
        uint elapsedMs = unchecked(currentTick - lii.dwTime);
        return TimeSpan.FromMilliseconds(elapsedMs);
    }

    private (string? ProcessName, string? AppName, string? ClassName) ResolveWindowIdentityCached(IntPtr hwnd, uint pid)
    {
        if (hwnd == _cachedHwnd && pid == _cachedPid && _cachedProcessName is not null)
        {
            return (_cachedProcessName, _cachedAppName, _cachedClassName);
        }

        _classBuffer.Clear();
        int len = NativeMethods.GetClassName(hwnd, _classBuffer, _classBuffer.Capacity);
        string? className = len > 0 ? _classBuffer.ToString(0, len) : null;

        string? procName = null;
        string? appName = null;

        if (pid != 0)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                procName = AwarenessValidator.NormalizeProcessKey(proc.ProcessName);
                appName = procName;
            }
            catch
            {
                // Process may have exited or restricted access; safe fallback
                procName = $"pid-{pid}";
                appName = procName;
            }
        }

        _cachedHwnd = hwnd;
        _cachedPid = pid;
        _cachedProcessName = procName;
        _cachedAppName = appName;
        _cachedClassName = className;

        return (procName, appName, className);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (_disposed) return;

        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect)
        {
            _isSessionLocked = true;
            SessionLockChanged?.Invoke(this, true);
        }
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect)
        {
            _isSessionLocked = false;
            SessionLockChanged?.Invoke(this, false);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_disposed) return;

        if (e.Mode == PowerModes.Suspend)
        {
            _isSystemSuspended = true;
            PowerSuspendChanged?.Invoke(this, true);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _isSystemSuspended = false;
            PowerSuspendChanged?.Invoke(this, false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
