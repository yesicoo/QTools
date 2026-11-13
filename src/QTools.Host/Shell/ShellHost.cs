using Photino.NET;
using QTools.Host.Bridge;
using QTools.Host.Logging;
using QTools.Host.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;

namespace QTools.Host.Shell;

/// <summary>
/// 桌面壳：管理主窗口（宿主管理界面）与工具窗口的生命周期。
/// 这一层把 PhotinoX 隔离在内——若将来更换桌面壳，只需重写本类（见设计文档 §3）。
/// </summary>
public sealed class ShellHost
{
    private readonly PhotinoApplication _application;
    private readonly ToolRegistry _registry;
    private readonly ToolStorage _storage;
    private readonly BridgeDispatcher _dispatcher;
    private readonly WindowRegistry _windows = new();
    private readonly Dictionary<string, PhotinoWindow> _toolWindows = new(StringComparer.Ordinal);

    /// <summary>主窗口（原生文件对话框等需要它作为父窗口）。</summary>
    private PhotinoWindow? _mainWindow;

    /// <summary>内容窗口（工具/AI创建/导入的独立窗口，打开时定位到主窗口旁边）。</summary>
    private PhotinoWindow? _contentWindow;

    /// <summary>主窗口隐藏（热键）时内容窗口是否可见（恢复时随之恢复）。</summary>
    private bool _contentWasVisible;

    /// <summary>全局热键（Alt+Space 呼出/隐藏主窗口，仅 Windows）。</summary>
    private GlobalHotkey? _hotkey;

    /// <summary>主窗口是否被热键隐藏（区别于用户最小化）。</summary>
    private bool _hiddenByHotkey;

    /// <summary>系统托盘图标（仅 Windows；创建失败时为 null，关闭主窗口直接退出）。</summary>
    private TrayIcon? _tray;

    /// <summary>托盘菜单 HTML 弹层窗口（无边框、失焦自动收起）。</summary>
    private PhotinoWindow? _trayMenuWindow;

    private bool _trayMenuPendingShow;
    private long _trayMenuRequestSerial;
    private long _trayMenuShownAt;
    private int _trayMenuLeft;
    private int _trayMenuTop;
    private int _trayMenuHeight;

    private const int TrayMenuWidth = 288;

    /// <summary>刚显示的弹层可能因前台锁拿不到焦点而立刻触发失焦，这段宽限期内忽略收起。</summary>
    private const int TrayMenuDeactivateGraceMs = 800;

    /// <summary>页面就绪回传迟迟不来时的兜底显示时限（防止页面脚本出错导致菜单永远不出现）。</summary>
    private const int TrayMenuReadyFallbackMs = 1200;

    /// <summary>是否已经由「退出 QTools」放行主窗口关闭。</summary>
    private bool _exitRequested;

    /// <summary>托盘菜单条目提供者（组合根注入统一列表前五项，每次打开实时读取）。</summary>
    public Func<IReadOnlyList<TrayEntry>>? TrayEntriesProvider { get; set; }

    /// <summary>主窗口尺寸保存防抖序号（连续拖动时只在停下约 500ms 后落一次盘）。</summary>
    private long _mainWindowSizeSaveSerial;

    /// <summary>内容窗口位置/尺寸保存防抖序号。</summary>
    private long _contentWindowSaveSerial;

    /// <summary>独立工具窗口位置/尺寸保存防抖序号（按 toolId，连续拖动只在停下后落一次盘）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _toolWindowSaveSerials =
        new(StringComparer.Ordinal);

    /// <summary>窗口置顶/钉住状态（按窗口 Id；钉住 = 锁定位置和大小）。</summary>
    private readonly Dictionary<Guid, WindowStateEntry> _windowStates = new();

    private sealed class WindowStateEntry
    {
        public bool Pinned;
        public bool BaseResizable = true;
        public WindowPinLock? Lock;
    }

    private int _port;
    private ServerInfoView _serverInfo = null!;
    private bool _devMode;

    /// <summary>启动后待打开的工具窗口队列（--open-tool 用）。</summary>
    private readonly Queue<LoadedTool> _pendingToolsToOpen = new();

    /// <summary>启动后是否打开设置窗口（--open-settings 用）。</summary>
    private bool _pendingSettingsToOpen;

    public ShellHost(
        PhotinoApplication application,
        ToolRegistry registry,
        ToolStorage storage,
        BridgeDispatcher dispatcher)
    {
        _application = application;
        _registry = registry;
        _storage = storage;
        _dispatcher = dispatcher;
    }

    public PhotinoApplication Application => _application;

    public WindowRegistry Windows => _windows;

    /// <summary>当前平台是否支持全局热键（仅 Windows 已实现并验证）。</summary>
    public bool HotkeySupported => OperatingSystem.IsWindows();

    /// <summary>全局热键当前是否注册成功。</summary>
    public bool HotkeyRegistered => _hotkey?.IsRegistered ?? false;

    /// <summary>登记一个需要在启动后立即打开的工具窗口（供 --open-tool 与自动化验收使用）。</summary>
    public void QueueToolToOpen(LoadedTool tool) => _pendingToolsToOpen.Enqueue(tool);

    /// <summary>登记启动后立即打开设置窗口（供 --open-settings 与自动化验收使用）。</summary>
    public void QueueSettingsToOpen() => _pendingSettingsToOpen = true;

    /// <summary>启动消息循环（阻塞直至主窗口关闭）。enableTray 仅正常桌面模式启用；无头预览保持关闭即退出。</summary>
    public int Run(int port, bool devMode, bool enableTray = true)
    {
        _port = port;
        _devMode = devMode;

        // 把壳与服务器信息注入分发器：manager.* 方法依赖它们
        _dispatcher.Shell = this;
        _serverInfo = new ServerInfoView(
            QTools.Host.AppInfo.Version,
            port,
            PlatformInfo.PlatformName,
            PlatformInfo.WebViewName,
            null,
            null)
        {
            DataDirectory = QTools.Host.Configuration.AppPaths.RootDirectory,
        };
        _dispatcher.ServerInfo = _serverInfo;

        // 全局热键按配置启动（热键走独立线程消息循环，不依赖 UI 消息泵）
        var shellConfig = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
        if (shellConfig.Shell?.HotkeyEnabled ?? true)
        {
            StartHotkey();
        }

        // 必须在创建任何窗口之前确定 WebView2 用户数据目录：
        // 上次进程被强制结束时，残留的 WebView2 子进程可能仍占用共享目录，
        // 复用会触发 ERROR_SHARING_VIOLATION(32)，表现为空白窗口 + 原生错误弹窗。
        if (QTools.Host.Configuration.AppPaths.PrepareWebViewDataDirectory())
        {
            Log.Warn("检测到上次进程未正常退出，本次改用独立的 WebView2 数据目录，避免残留占用导致窗口创建失败");
        }

        QTools.Host.Configuration.AppPaths.EnsureWindowIconExtracted();

        try
        {
            var mainWindow = CreateHostWindow();
            _mainWindow = mainWindow;
            if (enableTray)
            {
                StartTray();
            }
            _application
                .SetName("QTools")
                .SetShutdownMode(PhotinoShutdownMode.OnMainWindowClose)
                .SetNotificationsEnabled(false)
                .RegisterStartupHandler((_, _) =>
                {
                    LogRuntimeInfo();
                    DrainPendingTools();
                })
                .RegisterExitHandler((_, e) =>
                {
                    DisposeTray();
                    Log.Info($"应用退出，退出码 {e.ApplicationExitCode}");
                });

            Log.Info($"创建 PhotinoX 主窗口（{PlatformInfo.WebViewName}）→ {HostUiUrl}");

            return _application.Run(mainWindow);
        }
        finally
        {
            DisposeTray();
            StopHotkey();
            QTools.Host.Configuration.AppPaths.CleanupWebViewDataDirectory();
        }
    }

    /// <summary>设置全局热键开关（manager.setHotkeyEnabled 用；不支持的平台抛错）。</summary>
    public void SetHotkeyEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new BridgeException("HOTKEY_UNSUPPORTED", "全局热键目前仅支持 Windows");
        }

        if (enabled)
        {
            StartHotkey();
        }
        else
        {
            StopHotkey();
        }
    }

    private void StartHotkey()
    {
        if (!OperatingSystem.IsWindows() || _hotkey is not null)
        {
            return;
        }

        var hotkey = new GlobalHotkey(OnHotkeyPressed);
        _hotkey = hotkey;
        if (hotkey.Start())
        {
            Log.Info("全局热键已注册：Alt+Space 呼出/隐藏主窗口");
        }
        else
        {
            Log.Warn("全局热键注册失败（Alt+Space 可能被其他程序占用）");
        }
    }

    private void StopHotkey()
    {
        if (_hotkey is null)
        {
            return;
        }

        _hotkey.Stop();
        _hotkey = null;
        _hiddenByHotkey = false;
        Log.Info("全局热键已注销");
    }

    /// <summary>热键回调在热键线程触发：marshal 到 UI 线程切换主窗口可见性。</summary>
    private void OnHotkeyPressed()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await ToggleMainWindowAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"热键切换主窗口失败：{ex.Message}");
            }
        });
    }

    private async System.Threading.Tasks.Task ToggleMainWindowAsync()
    {
        var shouldShow = await InvokeOnUiThreadAsync(() =>
        {
            var window = _mainWindow;
            return window is null || window.IsClosed || _hiddenByHotkey || !window.IsVisible;
        }).ConfigureAwait(false);

        if (shouldShow)
        {
            await ShowMainWindowAsync().ConfigureAwait(false);
        }
        else
        {
            await HideMainWindowAsync().ConfigureAwait(false);
        }
    }

    /// <summary>显示主窗口（热键呼出 / 托盘左键）。</summary>
    private async System.Threading.Tasks.Task ShowMainWindowAsync()
    {
        await InvokeOnUiThreadAsync(() =>
        {
            var window = _mainWindow;
            if (window is null || window.IsClosed)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            HideTrayMenuNow();
            _hiddenByHotkey = false;
            window.Show();
            if (window.WindowState == PhotinoWindowState.Minimized)
            {
                window.Restore();
            }

            window.Activate();
            window.BringToFront();
            ShowContentAfterMainShow();
            Log.Info("主窗口已显示");
            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>隐藏主窗口（热键收起 / 关闭到托盘），内容窗口随之隐藏。</summary>
    private async System.Threading.Tasks.Task HideMainWindowAsync()
    {
        await InvokeOnUiThreadAsync(() =>
        {
            var window = _mainWindow;
            if (window is null || window.IsClosed)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            window.Hide();
            _hiddenByHotkey = true;
            HideContentForMainHide();
            Log.Info("主窗口已隐藏");
            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /* ================= 系统托盘（仅 Windows） ================= */

    private void StartTray()
    {
        if (!TrayIcon.IsSupported)
        {
            Log.Info("当前平台无系统托盘实现，关闭主窗口将直接退出");
            return;
        }

        var tray = TrayIcon.TryCreate(
            "QTools — 左键显示主窗口，右键打开工具",
            ShowMainWindowFromTray,
            ShowTrayMenuFromTray);
        if (tray is null)
        {
            Log.Warn("系统托盘不可用，关闭主窗口将直接退出");
            return;
        }

        _tray = tray;
        Log.Info("系统托盘图标已创建（关闭主窗口将收起到托盘）");
    }

    private void ShowMainWindowFromTray()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await ShowMainWindowAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"托盘显示主窗口失败：{ex.Message}");
            }
        });
    }

    /// <summary>托盘右键：在光标处显示 HTML 弹层菜单（样式与主界面一致，失焦自动收起）。</summary>
    private void ShowTrayMenuFromTray()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await ShowTrayMenuAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"托盘菜单显示失败：{ex.Message}");
            }
        });
    }

    private async System.Threading.Tasks.Task ShowTrayMenuAsync()
    {
        await InvokeOnUiThreadAsync(() =>
        {
            var count = GetTrayEntries().Count;

            // 光标/工作区必须在 Photino 窗口所在 UI 线程读取（物理像素）；托盘消息线程是
            // 96-DPI 上下文，在缩放屏上取到的是逻辑坐标，会让弹层错位。
            var cursor = NativeWindowInfo.GetCursorPosition() ?? (0, 0);
            var workArea = NativeWindowInfo.GetWorkAreaAtPoint(cursor.X, cursor.Y)
                ?? new WindowRect(0, 0, 1920, 1080);

            // 条目多时高度不能超出工作区，否则底部「退出」会被任务栏挡住；超出部分由列表滚动
            var scale = NativeWindowInfo.GetDpiAtPoint(cursor.X, cursor.Y) / 96.0;
            var maxHeight = (int)Math.Floor(workArea.Height / scale) - 24;
            var height = Math.Min(TrayMenuHeight(count), Math.Max(240, maxHeight));

            // 弹层尺寸是 DIP（SetSize 按窗口 DPI 放大），定位是物理像素（MoveTo 原样使用），
            // 夹回工作区时要用缩放后的实际像素尺寸。
            var pixelWidth = (int)Math.Round(TrayMenuWidth * scale);
            var pixelHeight = (int)Math.Round(height * scale);
            var (left, top) = TrayMenuPlacement.Compute(cursor.X, cursor.Y, pixelWidth, pixelHeight, workArea);

            Log.Debug($"托盘菜单定位：光标=({cursor.X},{cursor.Y}) " +
                $"工作区=({workArea.Left},{workArea.Top},{workArea.Width}x{workArea.Height}) " +
                $"缩放={scale:0.##} 像素尺寸={pixelWidth}x{pixelHeight} 位置=({left},{top})");

            _trayMenuLeft = left;
            _trayMenuTop = top;
            _trayMenuHeight = height;
            _trayMenuPendingShow = true;
            var serial = ++_trayMenuRequestSerial;

            var url = $"{TrayMenuUrl}?t={Environment.TickCount64}";
            var menu = _trayMenuWindow;
            if (menu is null || menu.IsClosed)
            {
                menu = new PhotinoWindow()
                    .SetTitle("QTools 托盘菜单")
                    .SetUseOsDefaultSize(false)
                    .SetUseOsDefaultLocation(false)
                    .SetChromeless(true)
                    .SetResizable(false)
                    .SetTopmost(true)
                    .SetSize(TrayMenuWidth, height)
                    .SetContextMenuEnabled(_devMode)
                    .SetDevToolsEnabled(_devMode)
                    .SetUserDataFolder(QTools.Host.Configuration.AppPaths.WebViewUserDataDirectory)
                    // InitialContentLoaded 只在窗口首次导航时触发一次；复用窗口靠页面回传
                    // manager.trayMenuReady 显示（见 NotifyTrayMenuReadyAsync）。
                    .RegisterInitialContentLoadedHandler((_, _) => ShowTrayMenuWindowIfPending())
                    .RegisterDeactivatedHandler((_, _) =>
                    {
                        // 先用离屏 Show 初始化 WebView、刚显示时拿不到前台都会触发失焦，
                        // 这些不算用户点击外部，宽限期后真正的失焦才收起。
                        if (_trayMenuPendingShow
                            || Environment.TickCount64 - _trayMenuShownAt < TrayMenuDeactivateGraceMs)
                        {
                            return;
                        }

                        HideTrayMenuNow();
                    })
                    .RegisterClosedHandler((_, _) =>
                    {
                        _trayMenuWindow = null;
                        Log.Info("托盘菜单窗口已关闭");
                    })
                    .Load(url);

                _trayMenuWindow = menu;
                AttachBridge(menu, BridgeDispatcher.HostToolId);

                // PhotinoX 运行期新建的窗口必须先 Show 才会初始化 WebView；
                // 先放到屏幕外，等页面就绪再移动到光标处，避免白底闪一下。
                menu.MoveTo(workArea.Left - pixelWidth - 200, workArea.Top, allowOutsideWorkArea: true);
                menu.Show();
                NativeWindowInfo.MarkAsToolWindow(menu.WindowHandle);
            }
            else
            {
                menu.Hide();
                menu.SetSize(TrayMenuWidth, height);
                menu.Load(url);
            }

            ScheduleTrayMenuFallbackShow(serial);
            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private static int TrayMenuHeight(int count) => 90 + 42 * Math.Max(count, 1);

    private void ShowTrayMenuWindowIfPending()
    {
        if (!_trayMenuPendingShow)
        {
            return;
        }

        _trayMenuPendingShow = false;
        var menu = _trayMenuWindow;
        if (menu is null || menu.IsClosed)
        {
            return;
        }

        NativeWindowInfo.MarkAsToolWindow(menu.WindowHandle);
        menu.MoveTo(_trayMenuLeft, _trayMenuTop, allowOutsideWorkArea: true);
        menu.SetSize(TrayMenuWidth, _trayMenuHeight);
        menu.Show();
        NativeWindowInfo.TryForceForeground(menu.WindowHandle);
        menu.Activate();
        menu.BringToFront();
        NativeWindowInfo.ApplyRoundedCorners(menu.WindowHandle, 12);
        _trayMenuShownAt = Environment.TickCount64;
        Log.Info("托盘菜单已显示");
    }

    /// <summary>页面渲染完成回传（manager.trayMenuReady）：此时才把离屏窗口移到光标处显示。</summary>
    public async System.Threading.Tasks.Task NotifyTrayMenuReadyAsync()
    {
        await InvokeOnUiThreadAsync(() =>
        {
            ShowTrayMenuWindowIfPending();
            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private void ScheduleTrayMenuFallbackShow(long serial)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(TrayMenuReadyFallbackMs).ConfigureAwait(false);
                await InvokeOnUiThreadAsync(() =>
                {
                    if (_trayMenuPendingShow && _trayMenuRequestSerial == serial)
                    {
                        Log.Warn("托盘菜单页面就绪回传超时，已兜底显示");
                        ShowTrayMenuWindowIfPending();
                    }

                    return System.Threading.Tasks.Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug($"托盘菜单兜底显示失败：{ex.Message}");
            }
        });
    }

    private void HideTrayMenuNow()
    {
        _trayMenuPendingShow = false;
        if (_trayMenuWindow is { IsClosed: false, IsVisible: true } menu)
        {
            menu.Hide();
        }
    }

    /// <summary>桥接（manager.hideTrayMenu）：Esc / 点击条目后收起弹层。</summary>
    public async System.Threading.Tasks.Task HideTrayMenuAsync()
    {
        await InvokeOnUiThreadAsync(() =>
        {
            HideTrayMenuNow();
            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>桥接（manager.trayMenuList）：实时读取前五项。</summary>
    public IReadOnlyList<TrayEntry> GetTrayEntries()
    {
        var provider = TrayEntriesProvider;
        if (provider is null)
        {
            return Array.Empty<TrayEntry>();
        }

        try
        {
            return provider().Take(TrayMenuPlanner.MaxItems).ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取托盘菜单失败：{ex.Message}");
            return Array.Empty<TrayEntry>();
        }
    }

    /// <summary>托盘菜单「退出 QTools」/ 桥接 manager.exitApp：真正结束进程。</summary>
    public void RequestExit()
    {
        _exitRequested = true;
        Log.Info("托盘：退出 QTools");
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await InvokeOnUiThreadAsync(() =>
                {
                    if (_mainWindow is { IsClosed: false } main)
                    {
                        main.Close();
                    }
                    else
                    {
                        _application.Shutdown(0, force: true);
                    }

                    return System.Threading.Tasks.Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"托盘退出失败：{ex.Message}");
                _application.Shutdown(0, force: true);
            }
        });
    }

    private void DisposeTray()
    {
        try
        {
            HideTrayMenuNow();
        }
        catch (Exception ex)
        {
            Log.Debug($"关闭托盘菜单失败：{ex.Message}");
        }

        if (_tray is null)
        {
            return;
        }

        _tray.Dispose();
        _tray = null;
        Log.Info("系统托盘图标已移除");
    }

    /// <summary>应用启动后（UI 线程、消息循环已就绪）排空待打开的工具窗口队列。</summary>
    private void DrainPendingTools()
    {
        while (_pendingToolsToOpen.TryDequeue(out var tool))
        {
            _ = OpenToolAsync(tool);
        }

        if (_pendingSettingsToOpen)
        {
            _pendingSettingsToOpen = false;
            _ = OpenSettingsAsync();
        }
    }

    private string HostUiUrl => $"http://127.0.0.1:{_port}/__host__/index.html";

    private string TrayMenuUrl => $"http://127.0.0.1:{_port}/__host__/tray-menu.html";

    private string SettingsUrl => $"http://127.0.0.1:{_port}/__host__/settings.html";

    private string ToolUrl(LoadedTool tool) => $"http://127.0.0.1:{_port}{tool.EntryUrl}";

    /// <summary>窗口图标：统一使用解压到数据目录的品牌 ICO（文件缺失时静默跳过）。</summary>
    private static void ApplyWindowIcon(PhotinoWindow window)
    {
        var path = QTools.Host.Configuration.AppPaths.WindowIconFilePath;
        if (File.Exists(path))
        {
            window.SetIconFile(path);
        }
    }

    /// <summary>启动时取上次手动调整的主窗口尺寸（DIP），并夹到当前显示器工作区内。</summary>
    private static (int Width, int Height) ResolveMainWindowSize()
    {
        var config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
        var (width, height) = MainWindowSize.Resolve(
            config.Shell?.MainWindowWidth, config.Shell?.MainWindowHeight);

        if (NativeWindowInfo.GetWorkAreaAtPoint(0, 0) is not { } work)
        {
            return (width, height);
        }

        var scale = NativeWindowInfo.GetDpiAtPoint(0, 0) / 96.0;
        var maxWidth = Math.Max(MainWindowSize.MinWidth, (int)Math.Round(work.Width / scale));
        var maxHeight = Math.Max(MainWindowSize.MinHeight, (int)Math.Round(work.Height / scale));
        return (Math.Min(width, maxWidth), Math.Min(height, maxHeight));
    }

    private void OnMainWindowSizeChanged(int width, int height)
    {
        // 窗口尚未交给 Run、最大化/最小化/全屏都不是用户想记住的普通窗口尺寸
        if (_mainWindow is null or { IsClosed: true } || _mainWindow.WindowState != PhotinoWindowState.Normal)
        {
            return;
        }

        if (!MainWindowSize.ShouldPersist(width, height))
        {
            return;
        }

        var serial = Interlocked.Increment(ref _mainWindowSizeSaveSerial);
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(500).ConfigureAwait(false);
                if (Interlocked.Read(ref _mainWindowSizeSaveSerial) != serial)
                {
                    return;
                }

                PersistMainWindowSize(width, height);
            }
            catch (Exception ex)
            {
                Log.Warn($"保存主窗口尺寸失败：{ex.Message}");
            }
        });
    }

    private void SaveCurrentMainWindowSize()
    {
        if (_mainWindow is { IsClosed: false } window && window.WindowState == PhotinoWindowState.Normal)
        {
            PersistMainWindowSize(window.Width, window.Height);
        }
    }

    private static void PersistMainWindowSize(int width, int height)
    {
        if (!MainWindowSize.ShouldPersist(width, height))
        {
            return;
        }

        var path = QTools.Host.Configuration.AppPaths.ConfigFilePath;
        var config = QTools.Host.Ai.ConfigStore.Load(path);
        var shell = config.Shell ?? new QTools.Host.Configuration.ShellConfigSection();
        if (shell.MainWindowWidth == width && shell.MainWindowHeight == height)
        {
            return;
        }

        QTools.Host.Ai.ConfigStore.Save(
            path, config with { Shell = shell with { MainWindowWidth = width, MainWindowHeight = height } });
        Log.Debug($"主窗口尺寸已保存：{width}×{height}");
    }

    private PhotinoWindow CreateHostWindow()
    {
        var (width, height) = ResolveMainWindowSize();
        var window = new PhotinoWindow()
            .SetTitle("QTools")
            .SetUseOsDefaultSize(false)
            .SetSize(width, height)
            .SetMinSize(MainWindowSize.MinWidth, MainWindowSize.MinHeight)
            .SetResizable(true)
            .SetContextMenuEnabled(_devMode)
            .SetDevToolsEnabled(_devMode)
            .SetUserDataFolder(QTools.Host.Configuration.AppPaths.WebViewUserDataDirectory)
            .Center()
            .RegisterSizeChangedHandler((_, e) => OnMainWindowSizeChanged(e.Width, e.Height))
            .RegisterClosingHandler((_, e) =>
            {
                SaveCurrentMainWindowSize();

                // 托盘可用时，点关闭按钮只是收起主窗口；只有托盘「退出 QTools」才真正结束进程
                if (_exitRequested || _tray is null)
                {
                    Log.Info("主窗口关闭 → 退出应用");
                    return;
                }

                e.Cancel = true;
                Log.Info("主窗口关闭 → 收起到系统托盘");
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await HideMainWindowAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"收起到托盘失败：{ex.Message}");
                    }
                });
            })
            .RegisterClosedHandler((_, _) => Log.Info("主窗口已关闭"))
            .RegisterInitialContentLoadedHandler((_, _) => Log.Info($"主窗口初始内容已加载：{HostUiUrl}"))
            .Load(HostUiUrl);

        ApplyWindowIcon(window);
        AttachBridge(window, BridgeDispatcher.HostToolId);
        return window;
    }

    /// <summary>
    /// 打开（或复用）内容窗口：工具/AI创建/导入在独立窗口中展示。首次打开贴主窗口右侧；
    /// 之后记住用户手动调整的位置与尺寸，下次打开恢复（位置失效时回退贴主窗口）。
    /// </summary>
    public async System.Threading.Tasks.Task OpenContentAsync(string view, string? toolId)
    {
        await InvokeOnUiThreadAsync(() =>
        {
            var main = _mainWindow;
            if (main is null || main.IsClosed)
            {
                throw new BridgeException("INTERNAL_ERROR", "主窗口尚未就绪");
            }

            var url = $"{HostUiUrl}?mode=content&view={Uri.EscapeDataString(view)}";
            if (!string.IsNullOrEmpty(toolId))
            {
                url += $"&toolId={Uri.EscapeDataString(toolId)}";
            }

            var title = view switch
            {
                "tool" => $"{_registry.Find(toolId ?? "")?.Name ?? "工具"} — QTools",
                "version" => $"{_registry.Find(toolId ?? "")?.Name ?? "工具"} — 版本管理",
                "create" => "AI 创建工具 — QTools",
                "import" => "导入工具 — QTools",
                "web" => "添加在线工具 — QTools",
                _ => "QTools",
            };

            var content = _contentWindow;
            if (content is not null && !content.IsClosed)
            {
                content.SetTitle(title);
                content.Load(url);
                content.Show();
                content.Activate();
                Log.Info($"内容窗口复用（保持上次位置与尺寸）→ {url}");
                return System.Threading.Tasks.Task.CompletedTask;
            }

            var config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
            var shellConfig = config.Shell ?? new QTools.Host.Configuration.ShellConfigSection();
            var (contentWidth, contentHeight) = ContentWindowBounds.ResolveSize(
                shellConfig.ContentWindowWidth, shellConfig.ContentWindowHeight);
            var savedOrigin = ResolveContentWindowOrigin(
                shellConfig.ContentWindowLeft, shellConfig.ContentWindowTop, ref contentWidth, ref contentHeight);

            var mainRect = new WindowRect(main.Left, main.Top, main.Width, main.Height);
            var workArea = NativeWindowInfo.GetWorkArea(main.WindowHandle);
            var position = WindowDock.DockPosition(mainRect, contentWidth, contentHeight, workArea);

            var window = new PhotinoWindow()
                .SetTitle(title)
                .SetUseOsDefaultSize(false)
                .SetUseOsDefaultLocation(false)
                .SetSize(contentWidth, contentHeight)
                .SetMinSize(ContentWindowBounds.MinWidth, ContentWindowBounds.MinHeight)
                .SetResizable(true)
                .SetContextMenuEnabled(_devMode)
                .SetDevToolsEnabled(_devMode)
                .SetUserDataFolder(QTools.Host.Configuration.AppPaths.WebViewUserDataDirectory)
                .RegisterLocationChangedHandler((_, _) => ScheduleContentWindowSave())
                .RegisterSizeChangedHandler((_, _) => ScheduleContentWindowSave())
                .RegisterClosingHandler((_, _) =>
                {
                    SaveContentWindowNow();
                    Log.Info("内容窗口请求关闭");
                })
                .RegisterClosedHandler((sender, _) =>
                {
                    Log.Info("内容窗口已关闭");
                    _contentWindow = null;
                    ForgetWindowState(sender);
                })
                .Load(url);

            if (savedOrigin is { } origin)
            {
                // 在首次 Show 前应用避免闪一下：Windows 用 MoveTo（物理像素），其他平台用 SetLeft/SetTop
                RestoreWindowOrigin(window, origin.Left, origin.Top);
                Log.Info($"已打开内容窗口（恢复上次位置 {origin.Left},{origin.Top}，" +
                    $"{contentWidth}×{contentHeight}）→ {url}");
            }
            else
            {
                window.SetLeft(position.Left);
                window.SetTop(position.Top);
                Log.Info($"已打开内容窗口（贴主窗口 {position.Left},{position.Top}，" +
                    $"{contentWidth}×{contentHeight}）→ {url}");
            }

            _contentWindow = window;
            ApplyWindowIcon(window);
            AttachBridge(window, BridgeDispatcher.HostToolId);
            window.Show();

            var flags = LoadContentWindowFlags();
            GetWindowState(window);
            window.SetTopmost(flags.Topmost);
            ApplyPin(window, flags.Pinned);

            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>取内容窗口上次的位置（物理像素）；位置已不在任何显示器上时返回 null 并回退贴主窗口。</summary>
    private static (int Left, int Top)? ResolveContentWindowOrigin(
        int? savedLeft, int? savedTop, ref int width, ref int height)
    {
        if (savedLeft is null || savedTop is null)
        {
            return null;
        }

        var origin = ResolveSavedWindowOrigin(
            savedLeft, savedTop, ContentWindowBounds.MinWidth, ContentWindowBounds.MinHeight,
            ref width, ref height);
        if (origin is null)
        {
            Log.Info("内容窗口上次位置已不在任何显示器上，回退贴主窗口打开");
        }

        return origin;
    }

    /// <summary>
    /// 校验上次保存的窗口位置是否仍可见；Windows 用显示器探测，并把尺寸按该显示器工作区
    /// 夹一次（DIP）；其他平台拿不到显示器信息，按保存值直接恢复。位置无效返回 null，尺寸保持不变。
    /// </summary>
    private static (int Left, int Top)? ResolveSavedWindowOrigin(
        int? savedLeft, int? savedTop, int minWidth, int minHeight, ref int width, ref int height)
    {
        if (savedLeft is not int left || savedTop is not int top)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            // macOS/Linux 没有跨平台显示器探测（Photino 不暴露屏幕布局）：信任保存值，不做工作区夹取
            return (left, top);
        }

        // 用窗口左上角内部一点探测所在显示器：整块屏幕被拔掉时无法命中
        var probeX = left + 80;
        var probeY = top + 40;
        if (!NativeWindowInfo.IsPointOnAnyMonitor(probeX, probeY) ||
            NativeWindowInfo.GetWorkAreaAtPoint(probeX, probeY) is not { } work)
        {
            return null;
        }

        var scale = NativeWindowInfo.GetDpiAtPoint(probeX, probeY) / 96.0;
        var maxWidth = Math.Max(minWidth, (int)Math.Round(work.Width / scale));
        var maxHeight = Math.Max(minHeight, (int)Math.Round(work.Height / scale));
        width = Math.Min(width, maxWidth);
        height = Math.Min(height, maxHeight);
        return (left, top);
    }

    /// <summary>按保存的坐标恢复窗口位置：Windows 用物理像素 MoveTo，其他平台用 Photino 的 SetLeft/SetTop。</summary>
    private static void RestoreWindowOrigin(PhotinoWindow window, int left, int top)
    {
        if (OperatingSystem.IsWindows())
        {
            window.MoveTo(left, top, allowOutsideWorkArea: true);
        }
        else
        {
            window.SetLeft(left);
            window.SetTop(top);
        }
    }

    private void ScheduleContentWindowSave()
    {
        var serial = Interlocked.Increment(ref _contentWindowSaveSerial);
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(500).ConfigureAwait(false);
                if (Interlocked.Read(ref _contentWindowSaveSerial) != serial)
                {
                    return;
                }

                (int Left, int Top, int Width, int Height)? bounds = null;
                await InvokeOnUiThreadAsync(() =>
                {
                    bounds = CaptureContentWindowBounds();
                    return System.Threading.Tasks.Task.CompletedTask;
                }).ConfigureAwait(false);

                if (bounds is { } value)
                {
                    PersistContentWindowBounds(value);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"保存内容窗口位置失败：{ex.Message}");
            }
        });
    }

    private void SaveContentWindowNow()
    {
        if (CaptureContentWindowBounds() is { } bounds)
        {
            PersistContentWindowBounds(bounds);
        }
    }

    /// <summary>在 UI 线程读取内容窗口矩形；最大化/最小化或尺寸越界时不落盘。</summary>
    private (int Left, int Top, int Width, int Height)? CaptureContentWindowBounds()
    {
        var window = _contentWindow;
        if (window is null || CaptureWindowBounds(window) is not { } bounds)
        {
            return null;
        }

        return ContentWindowBounds.ShouldPersist(bounds.Width, bounds.Height) ? bounds : null;
    }

    /// <summary>
    /// 在 UI 线程读取窗口矩形（最大化/最小化返回 null）。Windows 用 Win32 物理像素定位、
    /// DPI 折算尺寸（DIP）；其他平台用 Photino 的 Left/Top/Width/Height，
    /// 与恢复用的 SetLeft/SetTop/SetSize 处于同一坐标空间。
    /// </summary>
    private static (int Left, int Top, int Width, int Height)? CaptureWindowBounds(PhotinoWindow window)
    {
        if (window.IsClosed || window.WindowState != PhotinoWindowState.Normal)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            var width = window.Width;
            var height = window.Height;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            return (window.Left, window.Top, width, height);
        }

        if (!NativeWindowInfo.TryGetWindowRect(window.WindowHandle, out var rect))
        {
            return null;
        }

        var scale = NativeWindowInfo.GetDpiAtPoint(
            rect.Left + rect.Width / 2, rect.Top + rect.Height / 2) / 96.0;
        return (rect.Left, rect.Top, (int)Math.Round(rect.Width / scale), (int)Math.Round(rect.Height / scale));
    }

    private static void PersistContentWindowBounds((int Left, int Top, int Width, int Height) bounds)
    {
        var path = QTools.Host.Configuration.AppPaths.ConfigFilePath;
        var config = QTools.Host.Ai.ConfigStore.Load(path);
        var shell = config.Shell ?? new QTools.Host.Configuration.ShellConfigSection();
        if (shell.ContentWindowLeft == bounds.Left &&
            shell.ContentWindowTop == bounds.Top &&
            shell.ContentWindowWidth == bounds.Width &&
            shell.ContentWindowHeight == bounds.Height)
        {
            return;
        }

        QTools.Host.Ai.ConfigStore.Save(
            path,
            config with
            {
                Shell = shell with
                {
                    ContentWindowLeft = bounds.Left,
                    ContentWindowTop = bounds.Top,
                    ContentWindowWidth = bounds.Width,
                    ContentWindowHeight = bounds.Height,
                },
            });
        Log.Debug(
            $"内容窗口位置/尺寸已保存：({bounds.Left},{bounds.Top}) {bounds.Width}×{bounds.Height}");
    }

    /// <summary>工具窗口位置/尺寸防抖保存：连续拖动/缩放时只在停下约 500ms 后落一次盘。</summary>
    private void ScheduleToolWindowBoundsSave(string toolId)
    {
        var serial = _toolWindowSaveSerials.AddOrUpdate(toolId, 1, (_, value) => value + 1);
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(500).ConfigureAwait(false);
                if (!_toolWindowSaveSerials.TryGetValue(toolId, out var latest) || latest != serial)
                {
                    return;
                }

                await InvokeOnUiThreadAsync(() =>
                {
                    if (CaptureToolWindowBounds(toolId) is { } bounds)
                    {
                        PersistToolWindowBounds(toolId, bounds);
                    }

                    return System.Threading.Tasks.Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"保存工具窗口位置失败：{ex.Message}");
            }
        });
    }

    private void SaveToolWindowBoundsNow(object? sender, string toolId)
    {
        if (sender is PhotinoWindow window && CaptureToolWindowBounds(window) is { } bounds)
        {
            PersistToolWindowBounds(toolId, bounds);
        }
    }

    private (int Left, int Top, int Width, int Height)? CaptureToolWindowBounds(string toolId) =>
        _toolWindows.TryGetValue(toolId, out var window) ? CaptureToolWindowBounds(window) : null;

    /// <summary>在 UI 线程读取工具窗口矩形；最大化/最小化或尺寸越界时不落盘。</summary>
    private static (int Left, int Top, int Width, int Height)? CaptureToolWindowBounds(PhotinoWindow window) =>
        CaptureWindowBounds(window) is { } bounds && ToolWindowBounds.ShouldPersist(bounds.Width, bounds.Height)
            ? bounds
            : null;

    private static void PersistToolWindowBounds(string toolId, (int Left, int Top, int Width, int Height) bounds)
    {
        var state = LoadToolWindowState(toolId);
        if (state.Left == bounds.Left &&
            state.Top == bounds.Top &&
            state.Width == bounds.Width &&
            state.Height == bounds.Height)
        {
            return;
        }

        SaveToolWindowState(toolId, state with
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
        });
        Log.Debug(
            $"工具窗口 {toolId} 位置/尺寸已保存：({bounds.Left},{bounds.Top}) {bounds.Width}×{bounds.Height}");
    }

    /// <summary>关闭内容窗口（前端「收起」按钮 / manager.closeContent）。</summary>
    public async System.Threading.Tasks.Task CloseContentAsync()
    {
        await InvokeOnUiThreadAsync(() =>
        {
            if (_contentWindow is { IsClosed: false } content)
            {
                content.Close();
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>索引完成等后台事件 → 通知主窗口刷新统一列表（线程池线程调用，内部 marshal 到 UI）。</summary>
    public void NotifyLauncherChanged()
    {
        var main = _mainWindow;
        if (main is null)
        {
            return;
        }

        var envelope = System.Text.Json.JsonSerializer.Serialize(
            new BridgeEventEnvelope("evt-" + Guid.NewGuid().ToString("N"), "launcher-changed"),
            QTools.Host.Serialization.AppJsonContext.Default.BridgeEventEnvelope);
        _ = SendToWindowAsync(main, envelope);
    }

    /// <summary>主窗口被热键隐藏时内容窗口一起隐藏（呼出时一起恢复）。</summary>
    private void HideContentForMainHide()
    {
        var content = _contentWindow;
        if (content is not null && !content.IsClosed && content.IsVisible)
        {
            content.Hide();
            _contentWasVisible = true;
        }
    }

    private void ShowContentAfterMainShow()
    {
        if (!_contentWasVisible)
        {
            return;
        }

        _contentWasVisible = false;
        var content = _contentWindow;
        if (content is not null && !content.IsClosed)
        {
            content.Show();
        }
    }

    /// <summary>打开（或聚焦）一个工具窗口。</summary>
    public async System.Threading.Tasks.Task OpenToolAsync(LoadedTool tool)
    {
        await InvokeOnUiThreadAsync(async () =>
        {
            if (_toolWindows.TryGetValue(tool.Id, out var existing))
            {
                if (!existing.IsClosed)
                {
                    existing.Activate();
                    return;
                }

                _toolWindows.Remove(tool.Id);
            }

            var windowPrefs = LoadToolWindowState(tool.Id);
            var (windowWidth, windowHeight) = ToolWindowBounds.ResolveSize(
                tool.Manifest.Window.Width, tool.Manifest.Window.Height,
                windowPrefs.Width, windowPrefs.Height);
            var savedOrigin = ResolveSavedWindowOrigin(
                windowPrefs.Left, windowPrefs.Top,
                ToolWindowBounds.MinWidth, ToolWindowBounds.MinHeight,
                ref windowWidth, ref windowHeight);
            if (savedOrigin is null && windowPrefs.Left is not null && windowPrefs.Top is not null)
            {
                Log.Info($"工具窗口 {tool.Id} 上次位置已不在任何显示器上，改为居中打开");
            }

            var window = new PhotinoWindow()
                .SetTitle($"{tool.Name} — QTools")
                .SetUseOsDefaultSize(false)
                .SetUseOsDefaultLocation(false)
                .SetSize(windowWidth, windowHeight)
                .SetResizable(tool.Manifest.Window.Resizable)
                .SetContextMenuEnabled(_devMode)
                .SetDevToolsEnabled(_devMode)
                .SetUserDataFolder(QTools.Host.Configuration.AppPaths.WebViewUserDataDirectory)
                .RegisterLocationChangedHandler((_, _) => ScheduleToolWindowBoundsSave(tool.Id))
                .RegisterSizeChangedHandler((_, _) => ScheduleToolWindowBoundsSave(tool.Id))
                .RegisterClosingHandler((sender, _) =>
                {
                    SaveToolWindowBoundsNow(sender, tool.Id);
                    Log.Info($"工具窗口请求关闭：{tool.Id}");
                })
                .RegisterClosedHandler((sender, _) =>
                {
                    Log.Info($"工具窗口已关闭：{tool.Id}");
                    _toolWindows.Remove(tool.Id);
                    ForgetWindowState(sender);
                })
                .Load(ToolUrl(tool));

            if (savedOrigin is { } origin)
            {
                // 在首次 Show 前应用避免闪一下：Windows 用 MoveTo（物理像素），其他平台用 SetLeft/SetTop
                RestoreWindowOrigin(window, origin.Left, origin.Top);
                Log.Info($"已打开工具窗口 {tool.Id}（恢复上次位置 {origin.Left},{origin.Top}，" +
                    $"{windowWidth}×{windowHeight}）");
            }
            else
            {
                window.Center();
            }

            // 声明了 media 权限才开启麦克风/摄像头/屏幕捕获能力（宿主侧授权，避免浏览器弹窗）
            if (tool.HasPermission(ToolPermission.Media))
            {
                window.SetMediaStreamEnabled(true);
                window.SetGrantBrowserPermissions(true);
                Log.Info($"工具 {tool.Id} 声明 media 权限，已开启媒体捕获");
            }

            _toolWindows[tool.Id] = window;
            ApplyWindowIcon(window);
            AttachBridge(window, tool.Id);

            // PhotinoX：Run() 只显示主窗口；运行期创建的窗口必须显式 Show() 才会初始化 WebView 并可见
            window.Show();

            GetWindowState(window).BaseResizable = tool.Manifest.Window.Resizable;
            window.SetTopmost(windowPrefs.Topmost);
            ApplyPin(window, windowPrefs.Pinned);

            Log.Info($"已打开工具窗口 {tool.Id} → {ToolUrl(tool)}");

            await System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /* ================= 窗口控制：置顶 / 钉住 ================= */

    /// <summary>独立工具窗口状态（window.getState）；调用者必须确实是该工具的独立窗口。</summary>
    internal System.Threading.Tasks.Task<WindowStateView> GetToolWindowStateAsync(Guid windowId, string toolId) =>
        InvokeOnUiThreadAsync(() =>
        {
            var window = RequireToolWindow(windowId, toolId);
            var prefs = LoadToolWindowState(toolId);
            return ReadWindowState(window) with { ChipLeft = prefs.ChipLeft, ChipTop = prefs.ChipTop };
        });

    /// <summary>设置独立工具窗口置顶（window.setTopmost）。</summary>
    internal System.Threading.Tasks.Task<WindowStateView> SetToolWindowTopmostAsync(
        Guid windowId, string toolId, bool enabled) =>
        InvokeOnUiThreadAsync(() =>
        {
            var window = RequireToolWindow(windowId, toolId);
            window.SetTopmost(enabled);
            SaveToolWindowState(toolId, LoadToolWindowState(toolId) with { Topmost = enabled });
            Log.Info($"工具窗口 {toolId} 置顶：{(enabled ? "开" : "关")}");
            return ReadWindowState(window);
        });

    /// <summary>设置独立工具窗口钉住（window.setPinned）：锁定位置和大小。</summary>
    internal System.Threading.Tasks.Task<WindowStateView> SetToolWindowPinnedAsync(
        Guid windowId, string toolId, bool enabled) =>
        InvokeOnUiThreadAsync(() =>
        {
            var window = RequireToolWindow(windowId, toolId);
            ApplyPin(window, enabled);
            SaveToolWindowState(toolId, LoadToolWindowState(toolId) with { Pinned = enabled });
            Log.Info($"工具窗口 {toolId} 钉住：{(enabled ? "开" : "关")}");
            return ReadWindowState(window);
        });

    /// <summary>保存「⋯」按钮在窗口内的位置（window.saveChipPosition），按工具记忆。</summary>
    internal System.Threading.Tasks.Task<WindowStateView> SetToolWindowChipPositionAsync(
        Guid windowId, string toolId, double left, double top) =>
        InvokeOnUiThreadAsync(() =>
        {
            var window = RequireToolWindow(windowId, toolId);
            var prefs = LoadToolWindowState(toolId) with { ChipLeft = left, ChipTop = top };
            SaveToolWindowState(toolId, prefs);
            return ReadWindowState(window) with { ChipLeft = prefs.ChipLeft, ChipTop = prefs.ChipTop };
        });

    /// <summary>内容窗口状态（manager.getContentWindowState）；仅内容窗口自身可调用。</summary>
    internal System.Threading.Tasks.Task<WindowStateView> GetContentWindowStateAsync(Guid windowId) =>
        InvokeOnUiThreadAsync(() => ReadWindowState(RequireContentWindow(windowId)));

    /// <summary>从独立工具窗口打开该工具的版本管理（window.openVersionManager）。</summary>
    internal async System.Threading.Tasks.Task OpenVersionManagerAsync(Guid windowId, string toolId)
    {
        await InvokeOnUiThreadAsync(() =>
        {
            RequireToolWindow(windowId, toolId);
            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);

        await OpenContentAsync("version", toolId).ConfigureAwait(false);
    }

    /// <summary>设置内容窗口置顶（manager.setContentWindowTopmost）。</summary>
    internal System.Threading.Tasks.Task<WindowStateView> SetContentWindowTopmostAsync(Guid windowId, bool enabled) =>
        InvokeOnUiThreadAsync(() =>
        {
            var window = RequireContentWindow(windowId);
            window.SetTopmost(enabled);
            SaveContentWindowFlags(enabled, IsPinned(window.Id));
            Log.Info($"内容窗口置顶：{(enabled ? "开" : "关")}");
            return ReadWindowState(window);
        });

    /// <summary>设置内容窗口钉住（manager.setContentWindowPinned）：锁定位置和大小。</summary>
    internal System.Threading.Tasks.Task<WindowStateView> SetContentWindowPinnedAsync(Guid windowId, bool enabled) =>
        InvokeOnUiThreadAsync(() =>
        {
            var window = RequireContentWindow(windowId);
            ApplyPin(window, enabled);
            SaveContentWindowFlags(window.Topmost, enabled);
            Log.Info($"内容窗口钉住：{(enabled ? "开" : "关")}");
            return ReadWindowState(window);
        });

    private PhotinoWindow RequireToolWindow(Guid windowId, string toolId)
    {
        if (!_toolWindows.TryGetValue(toolId, out var window) || window.IsClosed || window.Id != windowId)
        {
            throw new BridgeException("WINDOW_NOT_FOUND", "该操作仅支持独立工具窗口");
        }

        return window;
    }

    private PhotinoWindow RequireContentWindow(Guid windowId)
    {
        if (_contentWindow is not { IsClosed: false } window || window.Id != windowId)
        {
            throw new BridgeException("WINDOW_NOT_FOUND", "内容窗口未打开");
        }

        return window;
    }

    private WindowStateEntry GetWindowState(PhotinoWindow window)
    {
        if (!_windowStates.TryGetValue(window.Id, out var state))
        {
            state = new WindowStateEntry();
            _windowStates[window.Id] = state;
        }

        return state;
    }

    private bool IsPinned(Guid windowId) =>
        _windowStates.TryGetValue(windowId, out var state) && state.Pinned;

    private WindowStateView ReadWindowState(PhotinoWindow window) =>
        new(window.Topmost, IsPinned(window.Id));

    /// <summary>应用钉住：锁缩放 + 锁移动（Windows 子类化窗口过程；其他平台仅锁缩放）。</summary>
    private void ApplyPin(PhotinoWindow window, bool pinned)
    {
        var state = GetWindowState(window);
        state.Pinned = pinned;
        if (pinned && state.Lock is null)
        {
            state.Lock = WindowPinLock.Install(window.WindowHandle, () => _windowStates.Remove(window.Id));
        }

        state.Lock?.SetPinned(pinned);
        window.SetResizable(!pinned && state.BaseResizable);
    }

    private void ForgetWindowState(object? sender)
    {
        // 已安装窗口过程锁的条目交给 WM_NCDESTROY 回调移除，避免委托提前被回收
        if (sender is PhotinoWindow window &&
            _windowStates.TryGetValue(window.Id, out var state) &&
            state.Lock is null)
        {
            _windowStates.Remove(window.Id);
        }
    }

    private static (bool Topmost, bool Pinned) LoadContentWindowFlags()
    {
        var config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
        return (config.Shell?.ContentWindowTopmost ?? false, config.Shell?.ContentWindowPinned ?? false);
    }

    private static void SaveContentWindowFlags(bool topmost, bool pinned)
    {
        var path = QTools.Host.Configuration.AppPaths.ConfigFilePath;
        var config = QTools.Host.Ai.ConfigStore.Load(path);
        var shell = config.Shell ?? new QTools.Host.Configuration.ShellConfigSection();
        if (shell.ContentWindowTopmost == topmost && shell.ContentWindowPinned == pinned)
        {
            return;
        }

        QTools.Host.Ai.ConfigStore.Save(
            path, config with { Shell = shell with { ContentWindowTopmost = topmost, ContentWindowPinned = pinned } });
    }

    private static QTools.Host.Configuration.ToolWindowState LoadToolWindowState(string toolId)
    {
        var config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
        return config.Shell?.ToolWindowStates is { } states && states.TryGetValue(toolId, out var state)
            ? state
            : new QTools.Host.Configuration.ToolWindowState();
    }

    private static void SaveToolWindowState(string toolId, QTools.Host.Configuration.ToolWindowState state)
    {
        var path = QTools.Host.Configuration.AppPaths.ConfigFilePath;
        var config = QTools.Host.Ai.ConfigStore.Load(path);
        var shell = config.Shell ?? new QTools.Host.Configuration.ShellConfigSection();
        var states = shell.ToolWindowStates is null
            ? new Dictionary<string, QTools.Host.Configuration.ToolWindowState>(StringComparer.Ordinal)
            : new Dictionary<string, QTools.Host.Configuration.ToolWindowState>(
                shell.ToolWindowStates, StringComparer.Ordinal);
        states[toolId] = state;
        QTools.Host.Ai.ConfigStore.Save(path, config with { Shell = shell with { ToolWindowStates = states } });
    }

    /// <summary>打开原生文件选择对话框，让用户挑一个可执行文件（未选择返回 null）。</summary>
    public System.Threading.Tasks.Task<string?> PickProgramFileAsync()
    {
        return InvokeOnUiThreadAsync(() =>
        {
            if (_mainWindow is null)
            {
                throw new BridgeException("INTERNAL_ERROR", "主窗口尚未就绪");
            }

            var filters = new (string, string[])[]
            {
                ("可执行文件", OperatingSystem.IsWindows()
                    ? new[] { "exe", "lnk", "bat", "cmd" }
                    : new[] { "app", "desktop", "sh", "bin" }),
                ("所有文件", new[] { "*" }),
            };

            var defaultPath = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                : OperatingSystem.IsMacOS() ? "/Applications" : "/usr/bin";

            var picked = _mainWindow.ShowOpenFile(
                "选择要添加的程序",
                string.IsNullOrWhiteSpace(defaultPath) ? string.Empty : defaultPath,
                multiSelect: false,
                filters);

            var first = picked is { Length: > 0 } ? picked[0] : null;
            if (!string.IsNullOrWhiteSpace(first))
            {
                Log.Info($"用户通过文件对话框选择程序：{first}");
            }

            return first;
        });
    }

    /// <summary>打开原生文件选择对话框，让用户挑一个备份包（未选择返回 null）。</summary>
    public System.Threading.Tasks.Task<string?> PickBackupFileAsync()
    {
        return InvokeOnUiThreadAsync(() =>
        {
            if (_mainWindow is null)
            {
                throw new BridgeException("INTERNAL_ERROR", "主窗口尚未就绪");
            }

            var filters = new (string, string[])[]
            {
                ("QTools 备份包", new[] { "zip" }),
                ("所有文件", new[] { "*" }),
            };

            var picked = _mainWindow.ShowOpenFile(
                "选择要导入的备份包",
                string.Empty,
                multiSelect: false,
                filters);

            var first = picked is { Length: > 0 } ? picked[0] : null;
            if (!string.IsNullOrWhiteSpace(first))
            {
                Log.Info($"用户通过文件对话框选择备份包：{first}");
            }

            return first;
        });
    }

    /// <summary>打开（或聚焦）独立的设置窗口。</summary>
    public async System.Threading.Tasks.Task OpenSettingsAsync()
    {
        const string settingsKey = "__settings__";

        await InvokeOnUiThreadAsync(() =>
        {
            if (_toolWindows.TryGetValue(settingsKey, out var existing))
            {
                if (!existing.IsClosed)
                {
                    existing.Activate();
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                _toolWindows.Remove(settingsKey);
            }

            var window = new PhotinoWindow()
                .SetTitle("设置 — QTools")
                .SetUseOsDefaultSize(false)
                .SetSize(680, 760)
                .SetMinSize(560, 520)
                .SetResizable(true)
                .SetContextMenuEnabled(_devMode)
                .SetDevToolsEnabled(_devMode)
                .SetUserDataFolder(QTools.Host.Configuration.AppPaths.WebViewUserDataDirectory)
                .Center()
                .RegisterClosingHandler((_, _) => Log.Info("设置窗口请求关闭"))
                .RegisterClosedHandler((_, _) =>
                {
                    Log.Info("设置窗口已关闭");
                    _toolWindows.Remove(settingsKey);
                })
                .Load(SettingsUrl);

            _toolWindows[settingsKey] = window;
            ApplyWindowIcon(window);
            AttachBridge(window, BridgeDispatcher.HostToolId);

            window.Show();

            Log.Info($"已打开设置窗口 → {SettingsUrl}");
            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>在线工具窗口在窗口表中的键（与 toolId 同一命名空间，避免冲突）。</summary>
    public static string WebWindowKey(string webId) => "web:" + webId;

    /// <summary>
    /// 打开（或聚焦）一个在线工具窗口：WebView 直接加载外部网址。
    /// 不注册桥接、不注入 window.host——在线工具与宿主能力完全隔离。
    /// </summary>
    public async System.Threading.Tasks.Task OpenWebToolAsync(Launcher.LoadedWebTool tool)
    {
        // 磁盘上的 web.json 可被手工改动，打开前再校验一次协议白名单
        var url = tool.Entry.Url?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new BridgeException("WEB_URL_INVALID", $"在线工具网址必须是 http/https：{tool.Id}");
        }

        var key = WebWindowKey(tool.Id);

        await InvokeOnUiThreadAsync(() =>
        {
            if (_toolWindows.TryGetValue(key, out var existing))
            {
                if (!existing.IsClosed)
                {
                    existing.Activate();
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                _toolWindows.Remove(key);
            }

            var window = new PhotinoWindow()
                .SetTitle($"{tool.Name} — QTools")
                .SetUseOsDefaultSize(false)
                .SetSize(1200, 800)
                .SetMinSize(720, 540)
                .SetResizable(true)
                .SetContextMenuEnabled(_devMode)
                .SetDevToolsEnabled(_devMode)
                .SetUserDataFolder(QTools.Host.Configuration.AppPaths.WebViewUserDataDirectory)
                .Center()
                .RegisterClosingHandler((_, _) => Log.Info($"在线工具窗口请求关闭：{tool.Id}"))
                .RegisterClosedHandler((_, _) =>
                {
                    Log.Info($"在线工具窗口已关闭：{tool.Id}");
                    _toolWindows.Remove(key);
                })
                .Load(url);

            _toolWindows[key] = window;
            ApplyWindowIcon(window);
            // 故意不 AttachBridge：外部页面拿不到 window.host，也无法进入桥接分发

            window.Show();

            Log.Info($"已打开在线工具窗口 {tool.Id} → {url}");
            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除工具：关闭其窗口、删除目录、按需删除存储。
    /// 注意：关窗口需要在 UI 线程；无头模式下没有消息循环，因此调用方应使用
    /// <see cref="DeleteToolFilesAsync"/>（纯 I/O，不依赖消息循环）。
    /// </summary>
    public async System.Threading.Tasks.Task DeleteToolAsync(LoadedTool tool, bool deleteStorage)
    {
        await CloseToolWindowsAsync(tool.Id).ConfigureAwait(false);
        await DeleteToolFilesAsync(tool, deleteStorage).ConfigureAwait(false);
    }

    /// <summary>关闭指定工具的所有窗口（需在 UI 线程/消息循环内调用）。</summary>
    public async System.Threading.Tasks.Task CloseToolWindowsAsync(string toolId)
    {
        await InvokeOnUiThreadAsync(() =>
        {
            if (_toolWindows.TryGetValue(toolId, out var window) && !window.IsClosed)
            {
                window.Close();
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 删除工具目录与（按需）存储数据，并从注册表移除。
    /// 纯文件 I/O，不依赖消息循环——无头模式与单测均可直接调用。
    /// </summary>
    public async System.Threading.Tasks.Task DeleteToolFilesAsync(LoadedTool tool, bool deleteStorage)
    {
        try
        {
            if (Directory.Exists(tool.Directory))
            {
                Directory.Delete(tool.Directory, recursive: true);
                Log.Info($"已删除工具目录：{tool.Directory}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"删除工具目录失败 {tool.Id}", ex);
            throw new BridgeException("TOOL_DELETE_FAILED", $"删除工具目录失败：{ex.Message}");
        }

        if (deleteStorage)
        {
            _storage.Delete(tool.Id);
            Log.Info($"已删除工具存储数据：{tool.Id}");
        }

        // 文件存储实例随目录一起清理（目录删除已包含 files/，这里只释放实例）
        _dispatcher.OnToolDeleted(tool.Id);
        _registry.Remove(tool.Id);
        // 内嵌凭证随之作废：已删除工具的 iframe 不再持有有效身份
        _dispatcher.EmbedTokens.Revoke(tool.Id);

        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>关闭指定工具窗口（重命名等场景）。</summary>
    public async System.Threading.Tasks.Task CloseToolAsync(string toolId)
    {
        await InvokeOnUiThreadAsync(() =>
        {
            if (_toolWindows.TryGetValue(toolId, out var window) && !window.IsClosed)
            {
                window.Close();
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    private void AttachBridge(PhotinoWindow window, string toolId)
    {
        _windows.Register(window.Id, toolId);

        window.RegisterWebMessageReceivedHandler((_, e) => OnWebMessage(window, e.Message));
    }

    /// <summary>
    /// 收到桥接消息。刻意不在原生回调栈内同步回写（Photino 已知的重入风险，见 issue #87）：
    /// 后台处理 → 经 Dispatcher 回 UI 线程发送响应。
    /// </summary>
    private void OnWebMessage(PhotinoWindow window, string message)
    {
        var toolId = _windows.GetToolId(window.Id);
        if (toolId is null)
        {
            Log.Warn("收到未登记窗口的桥接消息，已忽略");
            return;
        }

        Log.Info($"收到桥接消息：tool={toolId} 长度={message.Length}");
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            string responseJson;
            try
            {
                // 流式通道：AI 生成 / Git 推送的增量信封经此直达页面（与最终响应同一窗口、同一 id）
                responseJson = await _dispatcher.DispatchAsync(
                    toolId, message,
                    push: envelope => _ = SendToWindowAsync(window, envelope),
                    senderWindowId: window.Id)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("桥接分发未处理异常", ex);
                responseJson = System.Text.Json.JsonSerializer.Serialize(
                    new BridgeErrorResponse("unknown", new BridgeError("INTERNAL_ERROR", "宿主内部错误，详情见日志")),
                    QTools.Host.Serialization.AppJsonContext.Default.BridgeErrorResponse);
            }

            try
            {
                await InvokeOnUiThreadAsync(() =>
                {
                    if (!window.IsClosed)
                    {
                        window.SendWebMessage(responseJson);
                    }

                    return System.Threading.Tasks.Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"回传桥接响应失败：{ex.Message}");
            }
        });
    }

    /// <summary>把一条流式信封送到窗口（UI 线程）；窗口已关闭等失败只记调试日志，不影响主流程。</summary>
    private async System.Threading.Tasks.Task SendToWindowAsync(PhotinoWindow window, string json)
    {
        try
        {
            await InvokeOnUiThreadAsync(() =>
            {
                if (!window.IsClosed)
                {
                    window.SendWebMessage(json);
                }

                return System.Threading.Tasks.Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug($"流式推送失败（窗口可能已关闭）：{ex.Message}");
        }
    }

    private void LogRuntimeInfo()
    {
        try
        {
            var info = _application.GetRuntimeInfo();
            _serverInfo = _serverInfo with
            {
                WebViewRuntimeVersion = info.WebViewRuntimeVersion,
                NativeVersion = info.NativeVersion,
            };
            _dispatcher.ServerInfo = _serverInfo;

            Log.Info($"WebView 引擎={info.WebViewEngine} | 运行时版本={info.WebViewRuntimeVersion ?? "未知"} | 原生库={info.NativeVersion} | .NET={info.FrameworkDescription}");
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 WebView 运行时信息失败：{ex.Message}");
        }
    }

    /// <summary>确保在 UI 线程执行（PhotinoX 窗口创建/发送消息必须在 UI 线程）。</summary>
    private async System.Threading.Tasks.Task InvokeOnUiThreadAsync(Func<System.Threading.Tasks.Task> action)
    {
        if (_application.Dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(false);
            return;
        }

        var completion = new System.Threading.Tasks.TaskCompletionSource();
        _application.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>在 UI 线程执行一个返回值的动作（用于原生对话框等需要同步返回的场景）。</summary>
    private async System.Threading.Tasks.Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
    {
        if (_application.Dispatcher.CheckAccess())
        {
            return action();
        }

        var completion = new System.Threading.Tasks.TaskCompletionSource<T>();
        _application.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return await completion.Task.ConfigureAwait(false);
    }
}
