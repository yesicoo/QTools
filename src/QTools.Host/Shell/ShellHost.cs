using Photino.NET;
using QTools.Host.Bridge;
using QTools.Host.Logging;
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

    /// <summary>启动消息循环（阻塞直至主窗口关闭）。</summary>
    public int Run(int port, bool devMode)
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
            _application
                .SetName("QTools")
                .SetShutdownMode(PhotinoShutdownMode.OnMainWindowClose)
                .SetNotificationsEnabled(false)
                .RegisterStartupHandler((_, _) =>
                {
                    LogRuntimeInfo();
                    DrainPendingTools();
                })
                .RegisterExitHandler((_, e) => Log.Info($"应用退出，退出码 {e.ApplicationExitCode}"));

            Log.Info($"创建 PhotinoX 主窗口（{PlatformInfo.WebViewName}）→ {HostUiUrl}");

            return _application.Run(mainWindow);
        }
        finally
        {
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
        await InvokeOnUiThreadAsync(() =>
        {
            var window = _mainWindow;
            if (window is null || window.IsClosed)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            if (_hiddenByHotkey || !window.IsVisible)
            {
                _hiddenByHotkey = false;
                window.Show();
                if (window.WindowState == PhotinoWindowState.Minimized)
                {
                    window.Restore();
                }

                window.Activate();
                window.BringToFront();
                ShowContentAfterMainShow();
                Log.Info("热键：主窗口已呼出");
            }
            else
            {
                window.Hide();
                _hiddenByHotkey = true;
                HideContentForMainHide();
                Log.Info("热键：主窗口已隐藏");
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
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

    private PhotinoWindow CreateHostWindow()
    {
        var window = new PhotinoWindow()
            .SetTitle("QTools")
            .SetUseOsDefaultSize(false)
            .SetSize(340, 720)
            .SetMinSize(280, 480)
            .SetResizable(true)
            .SetContextMenuEnabled(_devMode)
            .SetDevToolsEnabled(_devMode)
            .SetUserDataFolder(QTools.Host.Configuration.AppPaths.WebViewUserDataDirectory)
            .Center()
            .RegisterClosingHandler((_, _) => Log.Info("主窗口请求关闭"))
            .RegisterClosedHandler((_, _) => Log.Info("主窗口已关闭"))
            .RegisterInitialContentLoadedHandler((_, _) => Log.Info($"主窗口初始内容已加载：{HostUiUrl}"))
            .Load(HostUiUrl);

        ApplyWindowIcon(window);
        AttachBridge(window, BridgeDispatcher.HostToolId);
        return window;
    }

    /// <summary>
    /// 打开（或复用）内容窗口：工具/AI创建/导入在独立窗口中展示，打开时定位到主窗口旁边。
    /// 之后各窗口自由移动，互不联动。
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
                DockContentToMain(content, main);
                content.Show();
                content.Activate();
                Log.Info($"内容窗口复用 → {url}");
                return System.Threading.Tasks.Task.CompletedTask;
            }

            const int contentWidth = 920;
            const int contentHeight = 680;
            var mainRect = new WindowRect(main.Left, main.Top, main.Width, main.Height);
            var workArea = NativeWindowInfo.GetWorkArea(main.WindowHandle);
            var position = WindowDock.DockPosition(mainRect, contentWidth, contentHeight, workArea);

            var window = new PhotinoWindow()
                .SetTitle(title)
                .SetUseOsDefaultSize(false)
                .SetUseOsDefaultLocation(false)
                .SetSize(contentWidth, contentHeight)
                .SetMinSize(560, 420)
                .SetLeft(position.Left)
                .SetTop(position.Top)
                .SetResizable(true)
                .SetContextMenuEnabled(_devMode)
                .SetDevToolsEnabled(_devMode)
                .SetUserDataFolder(QTools.Host.Configuration.AppPaths.WebViewUserDataDirectory)
                .RegisterClosingHandler((_, _) => Log.Info("内容窗口请求关闭"))
                .RegisterClosedHandler((_, _) =>
                {
                    Log.Info("内容窗口已关闭");
                    _contentWindow = null;
                })
                .Load(url);

            _contentWindow = window;
            ApplyWindowIcon(window);
            AttachBridge(window, BridgeDispatcher.HostToolId);
            window.Show();

            Log.Info($"已打开内容窗口（{position.Left},{position.Top}）→ {url}");
            return System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
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

    /// <summary>内容窗口（重新）打开时定位到主窗口旁边；只做一次初始定位，之后互不联动。</summary>
    private void DockContentToMain(PhotinoWindow content, PhotinoWindow main)
    {
        var mainRect = new WindowRect(main.Left, main.Top, main.Width, main.Height);
        var workArea = NativeWindowInfo.GetWorkArea(main.WindowHandle);
        var position = WindowDock.DockPosition(mainRect, content.Width, content.Height, workArea);
        content.MoveTo(position.Left, position.Top, allowOutsideWorkArea: true);
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

            var window = new PhotinoWindow()
                .SetTitle($"{tool.Name} — QTools")
                .SetUseOsDefaultSize(false)
                .SetSize(tool.Manifest.Window.Width, tool.Manifest.Window.Height)
                .SetResizable(tool.Manifest.Window.Resizable)
                .SetContextMenuEnabled(_devMode)
                .SetDevToolsEnabled(_devMode)
                .SetUserDataFolder(QTools.Host.Configuration.AppPaths.WebViewUserDataDirectory)
                .Center()
                .RegisterClosingHandler((_, _) => Log.Info($"工具窗口请求关闭：{tool.Id}"))
                .RegisterClosedHandler((_, _) =>
                {
                    Log.Info($"工具窗口已关闭：{tool.Id}");
                    _toolWindows.Remove(tool.Id);
                })
                .Load(ToolUrl(tool));

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

            Log.Info($"已打开工具窗口 {tool.Id} → {ToolUrl(tool)}");

            await System.Threading.Tasks.Task.CompletedTask;
        }).ConfigureAwait(false);
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
                    toolId, message, push: envelope => _ = SendToWindowAsync(window, envelope))
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
