using System.Runtime.InteropServices;
using QTools.Host.Configuration;
using QTools.Host.Logging;

namespace QTools.Host.Shell;

/// <summary>
/// Windows 系统托盘图标：Shell_NotifyIcon + 隐藏消息窗口（独立 STA 消息线程，不占 UI 线程）。
/// 左键点击 → onShow；右键 → onMenu(光标坐标)（菜单由 ShellHost 的 HTML 弹层负责，不再用原生菜单）。
/// 非 Windows 平台 <see cref="TryCreate"/> 返回 null，主窗口关闭行为保持原样。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    private const int TrayMessage = 0x8000 + 20;
    private const int TrayIconId = 1;
    private const int WmClose = 0x0010;
    private const int WmDestroy = 0x0002;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmLeftButtonDblClk = 0x0203;
    private const int WmRightButtonUp = 0x0205;
    private const int NimAdd = 0;
    private const int NimDelete = 2;
    private const int NifMessage = 0x1;
    private const int NifIcon = 0x2;
    private const int NifTip = 0x4;
    private const int ImageIcon = 1;
    private const int LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const int IdiApplication = 32512;

    private readonly string _className = "QToolsTrayMessageWindow";
    private readonly string _tooltip;
    private readonly Action _onShow;
    private readonly Action _onMenu;
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private IntPtr _window;
    private IntPtr _icon;
    private uint _taskbarCreatedMessage;
    private WndProcDelegate? _wndProcDelegate;
    private bool _iconAdded;
    private bool _disposed;

    private TrayIcon(string tooltip, Action onShow, Action onMenu)
    {
        _tooltip = tooltip;
        _onShow = onShow;
        _onMenu = onMenu;
    }

    /// <summary>创建并显示托盘图标；失败（或非 Windows）返回 null。必须在窗口图标已解压后调用。</summary>
    public static TrayIcon? TryCreate(string tooltip, Action onShow, Action onMenu)
    {
        if (!IsSupported)
        {
            return null;
        }

        var tray = new TrayIcon(tooltip, onShow, onMenu);
        tray._thread = new Thread(tray.RunMessageLoop)
        {
            IsBackground = true,
            Name = "QTools.Tray",
        };
        if (OperatingSystem.IsWindows())
        {
            tray._thread.SetApartmentState(ApartmentState.STA);
        }

        tray._thread.Start();

        if (!tray._ready.Wait(3000) || !tray._iconAdded)
        {
            Log.Warn("系统托盘图标创建失败（Shell_NotifyIcon 未成功）");
            tray.Dispose();
            return null;
        }

        return tray;
    }

    private void RunMessageLoop()
    {
        try
        {
            var handle = GetModuleHandleW(null);
            _wndProcDelegate = WndProc;
            var windowClass = new WindowClass
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = handle,
                lpszClassName = _className,
            };

            if (RegisterClassW(ref windowClass) == 0)
            {
                Log.Warn($"托盘消息窗口注册失败：Win32 错误 {Marshal.GetLastWin32Error()}");
                return;
            }

            _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
            _window = CreateWindowExW(
                0, _className, "QToolsTray", 0,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, handle, IntPtr.Zero);
            if (_window == IntPtr.Zero)
            {
                Log.Warn($"托盘消息窗口创建失败：Win32 错误 {Marshal.GetLastWin32Error()}");
                return;
            }

            _icon = LoadTrayIcon();
            if (_icon == IntPtr.Zero)
            {
                Log.Warn("托盘图标加载失败（品牌 ICO 与系统默认图标都不可用）");
                return;
            }

            _iconAdded = AddIcon();
            if (!_iconAdded)
            {
                Log.Warn($"托盘图标注册失败：Win32 错误 {Marshal.GetLastWin32Error()}");
                return;
            }

            _ready.Set();

            while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘消息线程异常：{ex.Message}");
        }
        finally
        {
            RemoveIcon();
            if (_icon != IntPtr.Zero)
            {
                DestroyIcon(_icon);
                _icon = IntPtr.Zero;
            }

            _ready.Set();
        }
    }

    private IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == TrayMessage)
        {
            var mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMessage is WmLeftButtonUp or WmLeftButtonDblClk)
            {
                SafeInvoke(_onShow, "显示主窗口");
            }
            else if (mouseMessage == WmRightButtonUp)
            {
                SafeInvoke(_onMenu, "打开托盘菜单");
            }

            return IntPtr.Zero;
        }

        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            // 资源管理器重启后托盘图标会消失，需要重新注册
            _iconAdded = AddIcon();
            return IntPtr.Zero;
        }

        if (message == WmClose)
        {
            DestroyWindow(window);
            return IntPtr.Zero;
        }

        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private static void SafeInvoke(Action action, string description)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘回调失败（{description}）：{ex.Message}");
        }
    }

    private bool AddIcon()
    {
        var data = CreateIconData();
        return Shell_NotifyIconW(NimAdd, ref data);
    }

    private void RemoveIcon()
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateIconData();
        Shell_NotifyIconW(NimDelete, ref data);
        _iconAdded = false;
    }

    private NotifyIconData CreateIconData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window,
        uID = TrayIconId,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = TrayMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static IntPtr LoadTrayIcon()
    {
        var path = AppPaths.WindowIconFilePath;
        if (File.Exists(path))
        {
            var icon = LoadImageW(
                IntPtr.Zero, path, ImageIcon,
                GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrLoadFromFile);
            if (icon != IntPtr.Zero)
            {
                return icon;
            }
        }

        return LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveIcon();
        if (_window != IntPtr.Zero)
        {
            PostMessageW(_window, WmClose, IntPtr.Zero, IntPtr.Zero);
            _window = IntPtr.Zero;
        }

        _thread?.Join(3000);
    }

    private delegate IntPtr WndProcDelegate(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WindowClass windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out Message message, IntPtr window, uint messageFilterMin, uint messageFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(
        IntPtr instance, string name, uint type, int width, int height, uint load);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
}
