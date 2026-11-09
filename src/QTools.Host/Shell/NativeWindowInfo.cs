using System.Runtime.InteropServices;

namespace QTools.Host.Shell;

/// <summary>
/// Windows 原生窗口辅助信息：显示器工作区（MonitorFromWindow + GetMonitorInfo）与光标位置。
/// 非 Windows 平台一律返回 null、绝不触发 P/Invoke（贴边隐藏仅 Windows 已实现并验证）。
/// </summary>
internal static class NativeWindowInfo
{
    private const uint MonitorDefaultToNearest = 2;

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);
}