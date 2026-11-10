namespace QTools.Host.Configuration;

/// <summary>
/// 运行期路径解析。
/// <para>
/// 默认根目录使用**系统推荐位置**（而非可执行文件同级目录），避免污染安装目录、
/// 也避免安装到 Program Files 等只读位置时无法写入：
/// Windows → <c>%APPDATA%\QTools</c>；macOS → <c>~/Library/Application Support/QTools</c>；
/// Linux → <c>${XDG_DATA_HOME:-~/.local/share}/QTools</c>。
/// 可用 <c>--root</c> 显式覆盖。
/// </para>
/// </summary>
public static class AppPaths
{
    public static string RootDirectory { get; private set; } = string.Empty;

    /// <summary>用户工具目录（每个工具一个子目录，含 manifest.json）。</summary>
    public static string ToolsDirectory => Path.Combine(RootDirectory, "tools");

    /// <summary>本机程序启动项目录（每个启动项一个子目录，含 app.json）。</summary>
    public static string AppsDirectory => Path.Combine(RootDirectory, "apps");

    /// <summary>在线工具目录（每个在线工具一个子目录，含 web.json）。</summary>
    public static string WebToolsDirectory => Path.Combine(RootDirectory, "webtools");

    /// <summary>运行期数据目录。</summary>
    public static string DataDirectory => Path.Combine(RootDirectory, "data");

    /// <summary>按 toolId 隔离的工具存储。</summary>
    public static string StorageDirectory => Path.Combine(DataDirectory, "storage");

    /// <summary>日志目录。</summary>
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>WebView2 用户数据文件夹（统一管理，避免 %TEMP% 污染与环境配置冲突）。</summary>
    public static string WebViewDataDirectory => Path.Combine(DataDirectory, "webview");

    /// <summary>
    /// 本次进程实际使用的 WebView2 用户数据目录。
    /// 默认与共享目录一致；若上次进程非正常结束，则由
    /// <see cref="PrepareWebViewDataDirectory"/> 切换为独立会话目录。
    /// </summary>
    public static string WebViewUserDataDirectory
        => _webViewUserDataDirectory ?? WebViewDataDirectory;

    /// <summary>本次进程专属的 WebView2 用户数据目录（仅在非正常退出后启用）。</summary>
    private static string? _webViewUserDataDirectory;

    /// <summary>本次进程的运行标记文件；正常退出时删除，残留即代表上次进程非正常结束。</summary>
    private static string? _runningMarkerPath;

    /// <summary>宿主配置文件。</summary>
    public static string ConfigFilePath => Path.Combine(DataDirectory, "config.json");

    /// <summary>敏感信息存储目录。</summary>
    public static string SecretsDirectory => Path.Combine(DataDirectory, "secrets");

    /// <summary>解压的 PhotinoX 原生库目录。</summary>
    public static string NativeDirectory => Path.Combine(DataDirectory, "native");

    /// <summary>本机程序自动索引库（后台扫描完成后原子覆盖）。</summary>
    public static string ProgramIndexFilePath => Path.Combine(DataDirectory, "program-index.json");

    /// <summary>使用频次统计（列表默认排序依据）。</summary>
    public static string UsageFilePath => Path.Combine(DataDirectory, "usage.json");

    /// <summary>索引程序隐藏列表（右键「从列表隐藏」的持久化记录）。</summary>
    public static string HiddenProgramsFilePath => Path.Combine(DataDirectory, "hidden-programs.json");

    /// <summary>索引程序图标缓存（绝不能放进 apps/，否则会被当成手动启动项扫进注册表）。</summary>
    public static string IconCacheDirectory => Path.Combine(DataDirectory, "icon-cache");

    /// <summary>窗口图标文件（PhotinoX SetIconFile 需要真实文件路径，启动时从内置资源解压）。</summary>
    public static string WindowIconFilePath => Path.Combine(DataDirectory, "icon.ico");

    /// <summary>把内嵌的品牌图标解压到数据目录（已存在且长度一致则跳过）。失败只记日志，不影响启动。</summary>
    public static void EnsureWindowIconExtracted()
    {
        try
        {
            var assembly = typeof(AppPaths).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".brand.icon.ico", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                return;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return;
            }

            if (File.Exists(WindowIconFilePath) && new FileInfo(WindowIconFilePath).Length == stream.Length)
            {
                return;
            }

            Directory.CreateDirectory(DataDirectory);
            using var file = File.Create(WindowIconFilePath);
            stream.CopyTo(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.Log.Warn($"解压窗口图标失败：{ex.Message}");
        }
    }

    /// <summary>计算系统推荐的数据根目录。</summary>
    public static string GetRecommendedRoot()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            return Path.Combine(appData, "QTools");
        }

        // Linux / 其他：遵循 XDG 规范
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = !string.IsNullOrWhiteSpace(xdgDataHome)
            ? xdgDataHome
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");

        return Path.Combine(baseDir, "QTools");
    }

    public static void Initialize(string? explicitRoot)
    {
        RootDirectory = string.IsNullOrWhiteSpace(explicitRoot)
            ? GetRecommendedRoot()
            : Path.GetFullPath(explicitRoot);

        _webViewUserDataDirectory = null;
        _runningMarkerPath = null;

        Directory.CreateDirectory(ToolsDirectory);
        Directory.CreateDirectory(AppsDirectory);
        Directory.CreateDirectory(WebToolsDirectory);
        Directory.CreateDirectory(StorageDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(WebViewDataDirectory);
        Directory.CreateDirectory(IconCacheDirectory);
    }

    /// <summary>
    /// 为本次启动准备 WebView2 用户数据目录（必须在创建任何窗口之前调用）。
    /// <para>
    /// 若上次进程非正常结束（运行标记残留），其 WebView2 子进程可能仍占用共享目录下的
    /// EBWebView。此时复用共享目录会让 CreateCoreWebView2Controller 返回
    /// ERROR_SHARING_VIOLATION(32)，表现为窗口空白并弹出
    /// “Error creating webview controller / 请求的资源在使用中”。
    /// 这种情况本次改用独立会话目录，彻底避开残留占用。
    /// </para>
    /// </summary>
    /// <returns>是否因上次非正常退出而启用了独立会话目录。</returns>
    public static bool PrepareWebViewDataDirectory()
    {
        Directory.CreateDirectory(WebViewDataDirectory);

        // 历史会话目录能删就删；仍被残留 WebView2 子进程占用时跳过，留给下次启动清理
        foreach (var directory in Directory.EnumerateDirectories(
                     WebViewDataDirectory, "session-*", SearchOption.TopDirectoryOnly))
        {
            TryDeleteDirectory(directory);
        }

        var uncleanExit = false;
        foreach (var marker in Directory.EnumerateFiles(
                     WebViewDataDirectory, ".running-*", SearchOption.TopDirectoryOnly))
        {
            uncleanExit = true;
            TryDeleteFile(marker);
        }

        _webViewUserDataDirectory = uncleanExit
            ? CreateSessionDirectory()
            : WebViewDataDirectory;

        var currentMarker = Path.Combine(WebViewDataDirectory, $".running-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(currentMarker, Environment.ProcessId.ToString());
            _runningMarkerPath = currentMarker;
        }
        catch (IOException)
        {
            // 标记写失败不影响运行，只是下次无法判断是否为非正常退出
        }
        catch (UnauthorizedAccessException)
        {
            // 同上：只读或受限目录下不阻断启动
        }

        return uncleanExit;
    }

    /// <summary>正常退出时清理运行标记与本次专属会话目录（删不掉则留给下次启动清理）。</summary>
    public static void CleanupWebViewDataDirectory()
    {
        if (_runningMarkerPath is not null)
        {
            TryDeleteFile(_runningMarkerPath);
            _runningMarkerPath = null;
        }

        var current = _webViewUserDataDirectory;
        _webViewUserDataDirectory = null;

        if (current is null ||
            string.Equals(current, WebViewDataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryDeleteDirectory(current);
    }

    private static string CreateSessionDirectory()
    {
        var directory = Path.Combine(
            WebViewDataDirectory,
            $"session-{Environment.ProcessId}-{DateTime.Now:yyyyMMddHHmmss}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 仍被占用：忽略
        }
        catch (UnauthorizedAccessException)
        {
            // 权限不足：忽略
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // 仍被占用：忽略
        }
        catch (UnauthorizedAccessException)
        {
            // 权限不足：忽略
        }
    }

    /// <summary>检测可执行文件同级是否残留旧版数据目录（用于迁移提示）。</summary>
    public static string? DetectLegacyRoot()
    {
        var legacy = Path.Combine(AppContext.BaseDirectory, "data");
        return Directory.Exists(legacy) ? legacy : null;
    }
}
