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

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

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
}
