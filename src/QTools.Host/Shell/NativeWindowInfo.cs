using System.Runtime.InteropServices;

namespace QTools.Host.Shell;

/// <summary>
/// Windows 原生窗口辅助信息：显示器工作区（MonitorFromWindow + GetMonitorInfo）与光标位置。
/// 非 Windows 平台一律返回 null、绝不触发 P/Invoke（贴边隐藏仅 Windows 已实现并验证）。
/// </summary>
internal static class NativeWindowInfo
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToNull = 0;

    public static WindowRect? GetWorkArea(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return null;
        }

        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return null;
        }

        var work = info.rcWork;
        return new WindowRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    public static (int X, int Y)? GetCursorPosition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return GetCursorPos(out var point) ? (point.X, point.Y) : null;
    }

    /// <summary>窗口当前物理像素矩形（DPI 感知线程调用；失败返回 false）。</summary>
    public static bool TryGetWindowRect(IntPtr windowHandle, out WindowRect rect)
    {
        rect = default;
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero || !GetWindowRect(windowHandle, out var native))
        {
            return false;
        }

        rect = new WindowRect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
        return true;
    }

    /// <summary>该点是否落在任一显示器上（用于判断保存过的窗口位置是否仍然可见）。</summary>
    public static bool IsPointOnAnyMonitor(int x, int y)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNull) != IntPtr.Zero;
    }

    /// <summary>光标所在显示器的工作区（多显示器下托盘菜单要贴在当前屏幕内）。</summary>
    public static WindowRect? GetWorkAreaAtPoint(int x, int y)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
        {
            return null;
        }

        var work = info.rcWork;
        return new WindowRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    /// <summary>
    /// 指定点所在显示器的有效 DPI（96 = 100%，144 = 150%）。必须在 DPI 感知线程
    /// （Photino 窗口所在 UI 线程）上用物理坐标调用，结果才与窗口 DPI 一致。
    /// </summary>
    public static int GetDpiAtPoint(int x, int y)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 96;
        }

        var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return 96;
        }

        return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? (int)dpiX : 96;
    }

    /// <summary>把窗口裁成圆角矩形（无边框弹层用；非 Windows 静默跳过）。</summary>
    public static void ApplyRoundedCorners(IntPtr windowHandle, int radius)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return;
        }

        if (!GetWindowRect(windowHandle, out var rect))
        {
            return;
        }

        var dpi = GetDpiForWindow(windowHandle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var radiusPx = Math.Max(6, radius * (int)dpi / 96);
        var region = CreateRoundRectRgn(
            0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1, radiusPx * 2, radiusPx * 2);
        if (region == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(windowHandle, region, true) == 0)
        {
            DeleteObject(region);
        }
    }

    /// <summary>
    /// 尝试把窗口提到前台。托盘右键来自资源管理器，前台锁通常允许本进程调用；
    /// 拿不到前台也继续显示（弹层不依赖焦点，只是失焦自动收起会因此延迟）。
    /// </summary>
    public static bool TryForceForeground(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return false;
        }

        return SetForegroundWindow(windowHandle);
    }

    /// <summary>标记为工具窗口：不出现在任务栏与 Alt+Tab（托盘弹层用）。</summary>
    public static void MarkAsToolWindow(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
        {
            return;
        }

        const int GwlExStyle = -20;
        const int WsExToolWindow = 0x00000080;
        var style = GetWindowLongW(windowHandle, GwlExStyle);
        SetWindowLongW(windowHandle, GwlExStyle, style | WsExToolWindow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLongW(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}