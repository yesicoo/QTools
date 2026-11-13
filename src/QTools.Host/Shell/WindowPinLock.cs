using System.Runtime.InteropServices;
using QTools.Host.Logging;

namespace QTools.Host.Shell;

/// <summary>
/// 「钉住」的 Windows 实现：锁定窗口位置与大小。
/// <para>
/// Photino 的 <c>SetResizable(false)</c> 只能挡缩放，挡不住鼠标拖标题栏移动；
/// 这里子类化窗口过程（GWLP_WNDPROC），在钉住状态下取消 SC_MOVE/SC_SIZE/最大化，
/// 并把 WM_MOVING / WM_SIZING 的候选矩形改回当前矩形。窗口销毁时（WM_NCDESTROY）恢复原过程。
/// 非 Windows 平台返回 null，调用方只做 SetResizable(false) 兜底（位置不锁定）。
/// </para>
/// </summary>
internal sealed class WindowPinLock
{
    private const int GwlWndProc = -4;
    private const int HtCaption = 2;
    private const uint WmSizing = 0x0214;
    private const uint WmMoving = 0x0216;
    private const uint WmNcLButtonDblClk = 0x00A3;
    private const uint WmSysCommand = 0x0112;
    private const uint WmNcDestroy = 0x0082;
    private const uint ScSize = 0xF000;
    private const uint ScMove = 0xF010;
    private const uint ScMaximize = 0xF030;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private readonly IntPtr _handle;
    private readonly WindowProc _windowProc;
    private readonly Action? _onDestroyed;
    private IntPtr _previousProc;
    private bool _pinned;

    private WindowPinLock(IntPtr handle, Action? onDestroyed)
    {
        _handle = handle;
        _onDestroyed = onDestroyed;
        _windowProc = OnWindowProc;
    }

    /// <summary>安装窗口位置锁定（必须在窗口所属 UI 线程调用）；非 Windows 或失败返回 null。</summary>
    public static WindowPinLock? Install(IntPtr handle, Action? onDestroyed = null)
    {
        if (!OperatingSystem.IsWindows() || handle == IntPtr.Zero)
        {
            return null;
        }

        var pin = new WindowPinLock(handle, onDestroyed);
        var previous = SetWindowProc(handle, Marshal.GetFunctionPointerForDelegate(pin._windowProc));
        if (previous == IntPtr.Zero)
        {
            Log.Warn("窗口位置锁定安装失败：无法替换窗口过程");
            return null;
        }

        pin._previousProc = previous;
        return pin;
    }

    /// <summary>切换钉住状态（UI 线程调用）。</summary>
    public void SetPinned(bool pinned) => _pinned = pinned;

    private IntPtr OnWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (_pinned)
        {
            switch (message)
            {
                case WmMoving:
                case WmSizing:
                    FreezeRect(hwnd, lParam);
                    return new IntPtr(1);
                case WmSysCommand when IsBlockedSystemCommand(wParam):
                    return IntPtr.Zero;
                case WmNcLButtonDblClk when (int)wParam.ToInt64() == HtCaption:
                    return IntPtr.Zero;
            }
        }

        if (message == WmNcDestroy)
        {
            var previous = _previousProc;
            Restore();
            _onDestroyed?.Invoke();
            return previous == IntPtr.Zero
                ? DefWindowProcW(hwnd, message, wParam, lParam)
                : CallWindowProcW(previous, hwnd, message, wParam, lParam);
        }

        return _previousProc == IntPtr.Zero
            ? DefWindowProcW(hwnd, message, wParam, lParam)
            : CallWindowProcW(_previousProc, hwnd, message, wParam, lParam);
    }

    private static bool IsBlockedSystemCommand(IntPtr wParam)
    {
        var command = (uint)wParam.ToInt64() & 0xFFF0;
        return command is ScMove or ScSize or ScMaximize;
    }

    /// <summary>把拖动/缩放候选矩形改回当前窗口矩形，用户操作不会改变窗口位置和大小。</summary>
    private static void FreezeRect(IntPtr hwnd, IntPtr rectPointer)
    {
        if (rectPointer == IntPtr.Zero || !GetWindowRect(hwnd, out var current))
        {
            return;
        }

        var proposed = new NativeRect
        {
            Left = current.Left,
            Top = current.Top,
            Right = current.Right,
            Bottom = current.Bottom,
        };
        Marshal.StructureToPtr(proposed, rectPointer, false);
    }

    private void Restore()
    {
        if (_previousProc != IntPtr.Zero && _handle != IntPtr.Zero)
        {
            SetWindowProc(_handle, _previousProc);
            _previousProc = IntPtr.Zero;
        }
    }

    private static IntPtr SetWindowProc(IntPtr hwnd, IntPtr newProc) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, GwlWndProc, newProc)
            : new IntPtr(SetWindowLong32(hwnd, GwlWndProc, newProc.ToInt32()));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(
        IntPtr previousProc, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
