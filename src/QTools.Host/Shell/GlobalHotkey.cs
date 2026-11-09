using System.Runtime.InteropServices;

namespace QTools.Host.Shell;

/// <summary>
/// 全局热键 Alt+Space：呼出/隐藏主窗口（仅 Windows）。
/// 独立线程跑 Win32 消息循环——RegisterHotKey 绑定到调用线程，必须在该线程泵消息；
/// 回调在此线程触发，由调用方负责 marshal 到 UI 线程。停止经 PostThreadMessage 优雅退出。
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x5154; // "QT"
    private const uint MOD_ALT = 0x1;
    private const uint VK_SPACE = 0x20;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP_STOP = 0x8001;

    private readonly Action _onPressed;
    private Thread? _thread;
    private uint _threadId;
    private volatile bool _registered;

    public GlobalHotkey(Action onPressed)
    {
        _onPressed = onPressed;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsRegistered => _registered;

    /// <summary>注册热键并启动消息循环；返回是否注册成功（被其他程序占用时失败，不抛异常）。</summary>
    public bool Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null)
        {
            return _registered;
        }

        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _registered = RegisterHotKey(IntPtr.Zero, HotkeyId, MOD_ALT, VK_SPACE);
            ready.Set();

            if (!_registered)
            {
                return;
            }

            // GetMessageW 返回 0 = WM_QUIT，-1 = 错误；两者都退出循环
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY && msg.wParam.ToInt32() == HotkeyId)
                {
                    try { _onPressed(); }
                    catch { /* 回调异常不能击穿消息循环 */ }
                }
                else if (msg.message == WM_APP_STOP)
                {
                    UnregisterHotKey(IntPtr.Zero, HotkeyId);
                    _registered = false;
                    PostQuitMessage(0);
                }
            }
        })
        {
            IsBackground = true,
            Name = "QTools-Hotkey",
        };

        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
        return _registered;
    }

    /// <summary>注销热键并结束消息循环（幂等）。</summary>
    public void Stop()
    {
        var thread = _thread;
        if (thread is null)
        {
            return;
        }

        _thread = null;
        if (_registered && _threadId != 0)
        {
            PostThreadMessageW(_threadId, WM_APP_STOP, IntPtr.Zero, IntPtr.Zero);
        }

        thread.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}