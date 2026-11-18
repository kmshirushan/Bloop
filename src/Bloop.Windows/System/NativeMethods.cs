using System.Runtime.InteropServices;

namespace Bloop.Windows.System;

/// <summary>
/// P/Invoke definitions for transparent, non-activating floating companion window
/// and multi-monitor DPI-aware cursor tracking.
/// </summary>
public static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TRANSPARENT = 0x00000020;

    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int MA_NOACTIVATE = 3;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public bool fEnable;
        public IntPtr hRgnBlur;
        public bool fTransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, global::System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    public const int DWMWCP_DEFAULT = 0;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWCP_ROUND = 2;

    public const int DWMSBT_AUTO = 0;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2;      // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3; // Desktop Acrylic
    public const int DWMSBT_TABBEDWINDOW = 4;    // Tabbed Mica

    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    public static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND pBlurBehind);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    public static void ApplyCompanionWindowStyles(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // Strip standard window caption/frame while keeping DWM hardware composition active for WebView2
        int style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU);
        SetWindowLong(hwnd, GWL_STYLE, style);

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        // Enable per-pixel DWM glass/transparency without a visible border
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        IntPtr emptyRgn = CreateRectRgn(0, 0, -1, -1);
        var bb = new DWM_BLURBEHIND
        {
            dwFlags = 0x00000003, // DWM_BB_ENABLE | DWM_BB_BLURREGION
            fEnable = true,
            hRgnBlur = emptyRgn,
            fTransitionOnMaximized = false
        };
        DwmEnableBlurBehindWindow(hwnd, ref bb);
        if (emptyRgn != IntPtr.Zero)
        {
            DeleteObject(emptyRgn);
        }
    }

    /// <summary>
    /// Configures documented Windows 11 DWM Desktop Acrylic / Mica backdrop composition
    /// for NotificationWindow and SettingsWindow (requires AllowsTransparency=False + WindowChrome).
    /// Returns whether the native DWM backdrop attribute succeeded and a diagnostic description.
    /// </summary>
    public static (bool IsBackdropActive, string EffectSummary) ApplyGlassWindowBackdrop(
        IntPtr hwnd,
        bool enableAcrylicBackdrop,
        bool isLightTheme,
        bool isToolWindow,
        bool isNoActivate)
    {
        if (hwnd == IntPtr.Zero)
        {
            return (false, "InvalidHwnd");
        }

        int style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_SYSMENU);
        SetWindowLong(hwnd, GWL_STYLE, style);

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (isToolWindow)
        {
            exStyle |= WS_EX_TOOLWINDOW;
        }
        else
        {
            exStyle &= ~WS_EX_TOOLWINDOW;
        }

        if (isNoActivate)
        {
            exStyle |= WS_EX_NOACTIVATE;
        }
        else
        {
            exStyle &= ~WS_EX_NOACTIVATE;
        }
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        int hrExtend = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        int darkMode = isLightTheme ? 0 : 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        int cornerPref = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        int borderNone = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderNone, sizeof(int));

        int backdropType = enableAcrylicBackdrop ? DWMSBT_TRANSIENTWINDOW : DWMSBT_NONE;
        int hrBackdrop = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

        if (enableAcrylicBackdrop && hrExtend == 0 && hrBackdrop == 0)
        {
            return (true, "DWM_SYSTEMBACKDROP_ACRYLIC (DWMSBT_TRANSIENTWINDOW)");
        }

        if (enableAcrylicBackdrop)
        {
            // Fallback for older OS builds: enable DWM blur-behind across client area
            var bb = new DWM_BLURBEHIND
            {
                dwFlags = 0x00000001, // DWM_BB_ENABLE
                fEnable = true,
                hRgnBlur = IntPtr.Zero,
                fTransitionOnMaximized = false
            };
            int hrBlur = DwmEnableBlurBehindWindow(hwnd, ref bb);
            return (hrBlur == 0, $"Fallback_DwmBlurBehind (hrBackdrop=0x{hrBackdrop:X8}, hrBlur=0x{hrBlur:X8})");
        }

        return (false, "LayeredGlassSurface (BackdropBlurDisabledByTheme)");
    }

    public static (int Left, int Top, int Right, int Bottom) GetWorkAreaForPoint(int x, int y)
    {
        var info = GetMonitorWorkAreaAndDevice(x, y);
        return (info.Left, info.Top, info.Right, info.Bottom);
    }

    public static (int Left, int Top, int Right, int Bottom, string DeviceName) GetMonitorWorkAreaAndDevice(int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var miEx = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref miEx))
        {
            var dev = string.IsNullOrWhiteSpace(miEx.szDevice) ? "primary" : miEx.szDevice.Trim();
            return (miEx.rcWork.Left, miEx.rcWork.Top, miEx.rcWork.Right, miEx.rcWork.Bottom, dev);
        }
        return (0, 0, 1920, 1080, "primary");
    }

    public static (bool IsValid, RECT Bounds, bool IsMinimized, bool IsMaximized, bool IsFullscreen, string DeviceName)
        GetForegroundWindowInfo(IntPtr ignoreHwnd1, IntPtr ignoreHwnd2, IntPtr ignoreHwnd3)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero ||
            fg == ignoreHwnd1 ||
            fg == ignoreHwnd2 ||
            fg == ignoreHwnd3 ||
            fg == GetDesktopWindow() ||
            fg == GetShellWindow() ||
            !IsWindowVisible(fg))
        {
            return (false, default, false, false, false, "primary");
        }

        bool isMin = IsIconic(fg);
        bool isMax = IsZoomed(fg);

        if (DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, Marshal.SizeOf<RECT>()) != 0)
        {
            if (!GetWindowRect(fg, out rect))
            {
                return (false, default, isMin, isMax, false, "primary");
            }
        }

        int centerX = (rect.Left + rect.Right) / 2;
        int centerY = (rect.Top + rect.Bottom) / 2;
        var pt = new POINT { X = centerX, Y = centerY };
        IntPtr hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var miEx = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        bool isFullscreen = false;
        string dev = "primary";

        if (hMonitor != IntPtr.Zero && GetMonitorInfo(hMonitor, ref miEx))
        {
            dev = string.IsNullOrWhiteSpace(miEx.szDevice) ? "primary" : miEx.szDevice.Trim();
            int monW = miEx.rcMonitor.Right - miEx.rcMonitor.Left;
            int monH = miEx.rcMonitor.Bottom - miEx.rcMonitor.Top;
            int winW = rect.Right - rect.Left;
            int winH = rect.Bottom - rect.Top;
            if (winW >= monW && winH >= monH)
            {
                isFullscreen = true;
            }
        }

        return (true, rect, isMin, isMax, isFullscreen, dev);
    }
}
