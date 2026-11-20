using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QTools.Host.Ai;
using QTools.Host.Bridge;
using QTools.Host.Creator;
using QTools.Host.Logging;
using QTools.Host.Serialization;
using QTools.Host.Shell;
using QTools.Host.Storage;
using QTools.Host.Tools;

namespace QTools.Host.Server;

/// <summary>
/// 本地静态服务：仅绑定 127.0.0.1、随机端口；只提供 /health 与只读静态资源，零写端点。
/// 使用轻量 TCP HTTP/1.1 服务，避免为几个只读资源引入 ASP.NET Core/Kestrel。
/// </summary>
internal sealed class LocalServer : IDisposable
{
    private const string HostUiPrefix = "/__host__";
    private const string ToolsPrefix = "/tools/";
    private const long MaxHtmlBytes = 8 * 1024 * 1024;
    private const long MaxFileBytes = 8 * 1024 * 1024;

    // 宿主管理界面 CSP 更严：不允许任何网络请求（能力全部走桥接）
    private const string HostUiCsp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; connect-src 'none'; frame-src 'self'; base-uri 'none'; object-src 'none'; form-action 'none'";

    private LocalHttpServer? _http;

    /// <summary>工具注册表（桥接与静态文件服务共用）。</summary>
    public ToolRegistry Tools { get; }

    /// <summary>工具存储。</summary>
    public ToolStorage Storage { get; }

    /// <summary>桥接分发器。</summary>
    public BridgeDispatcher Dispatcher { get; }

    /// <summary>敏感信息存储（API Key）。</summary>
    public Security.ISecretStore Secrets { get; }

    /// <summary>AI 客户端。</summary>
    public Ai.AiClient Ai { get; }

    /// <summary>工具创造器。</summary>
    public Creator.ToolCreator Creator { get; }

    /// <summary>本地 HTML 导入器。</summary>
    public Creator.ToolImporter Importer { get; }

    /// <summary>本机程序启动项注册表。</summary>
    public Launcher.AppRegistry Apps { get; }

    /// <summary>本机程序启动器。</summary>
    public Launcher.AppLauncher Launcher { get; }

    /// <summary>启动项存储。</summary>
    public Launcher.AppStore AppStore { get; }

    /// <summary>在线工具注册表（录入的网址）。</summary>
    public Launcher.WebToolRegistry WebTools { get; }

    /// <summary>在线工具存储。</summary>
    public Launcher.WebToolStore WebToolStore { get; }

    /// <summary>本机程序扫描器。</summary>
    public Launcher.ProgramDiscovery Discovery { get; }

    /// <summary>本机程序自动索引（旧索引立即可用，后台扫描完成后原子覆盖）。</summary>
    public Launcher.ProgramIndex ProgramIndex { get; }

    /// <summary>使用频次统计。</summary>
    public Launcher.UsageStore Usage { get; }

    /// <summary>索引程序隐藏列表（右键「从列表隐藏」）。</summary>
    public Launcher.HiddenProgramStore HiddenPrograms { get; }

    /// <summary>列表置顶记录（右键「置顶」）。</summary>
    public Launcher.PinnedItemStore PinnedItems { get; }

    /// <summary>本地备份服务。</summary>
    public Backup.BackupService Backup { get; }

    /// <summary>Git 远程备份。</summary>
    public Backup.GitBackup GitBackup { get; }

    /// <summary>单条目导出/导入包（小工具当前运行版本 / 启动项 / 在线工具）。</summary>
    public Backup.ItemPackageService ItemPackages { get; }

    /// <summary>宿主配置。</summary>
    public Configuration.AppConfig Config { get; }

    private LocalServer()
    {
        Config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
        Secrets = Security.SecretStoreFactory.Create(QTools.Host.Configuration.AppPaths.DataDirectory);

        Tools = new ToolRegistry(QTools.Host.Configuration.AppPaths.ToolsDirectory);
        Storage = new ToolStorage(QTools.Host.Configuration.AppPaths.StorageDirectory);
        Ai = new AiClient(new HttpClient(), Secrets, Config);
        Creator = new Creator.ToolCreator(Ai, Tools);
        Importer = new Creator.ToolImporter(Tools, QTools.Host.Configuration.AppPaths.ToolsDirectory);

        var appsRoot = QTools.Host.Configuration.AppPaths.AppsDirectory;
        Apps = new Launcher.AppRegistry(appsRoot);
        Apps.Rescan();
        Launcher = new Launcher.AppLauncher();
        AppStore = new Launcher.AppStore(Apps, appsRoot);
        Discovery = new Launcher.ProgramDiscovery();
        ProgramIndex = new Launcher.ProgramIndex(
            QTools.Host.Configuration.AppPaths.ProgramIndexFilePath,
            Discovery,
            QTools.Host.Configuration.AppPaths.IconCacheDirectory);
        Usage = new Launcher.UsageStore(QTools.Host.Configuration.AppPaths.UsageFilePath);
        HiddenPrograms = new Launcher.HiddenProgramStore(
            QTools.Host.Configuration.AppPaths.HiddenProgramsFilePath);
        PinnedItems = new Launcher.PinnedItemStore(
            QTools.Host.Configuration.AppPaths.PinnedItemsFilePath);

        var webToolsRoot = QTools.Host.Configuration.AppPaths.WebToolsDirectory;
        WebTools = new Launcher.WebToolRegistry(webToolsRoot);
        WebTools.Rescan();
        WebToolStore = new Launcher.WebToolStore(WebTools, webToolsRoot);

        Backup = new Backup.BackupService(QTools.Host.Configuration.AppPaths.RootDirectory);
        GitBackup = new Backup.GitBackup(
            Backup,
            QTools.Host.Configuration.AppPaths.RootDirectory,
            QTools.Host.Configuration.AppPaths.DataDirectory);
        ItemPackages = new Backup.ItemPackageService(
            Tools, Importer, Apps, AppStore, WebTools, WebToolStore,
            QTools.Host.Configuration.AppPaths.RootDirectory,
            QTools.Host.Configuration.AppPaths.ImportStagingDirectory);

        Dispatcher = new BridgeDispatcher(
            Tools, Storage, Ai, Secrets, Creator, Importer, Apps, Launcher, AppStore, Discovery,
            Backup, GitBackup, ProgramIndex, Usage, WebTools, WebToolStore, HiddenPrograms, PinnedItems,
            ItemPackages);
    }

    public int Port => _http?.Port ?? 0;

    public static LocalServer Start(int? fixedPort)
    {
        var server = new LocalServer();
        var http = new LocalHttpServer(fixedPort ?? 0, server.HandleRequestAsync);
        server._http = http;

        // 启动即扫描一次工具（首启会把内嵌示例工具释放到 tools/）
        SampleTools.EnsureSamples(QTools.Host.Configuration.AppPaths.ToolsDirectory);
        server.Tools.Rescan();

        // 小工具 / 手动启动项 / 在线工具默认计入 1 次，首页可直接看到；索引程序仍从 0 次开始（只能搜索到）
        server.Usage.EnsureSeeded(
            server.Tools.GetAll().Select(t => t.Id)
                .Concat(server.Apps.GetAll().Select(a => a.Id))
                .Concat(server.WebTools.GetAll().Select(w => w.Id)));

        // 后台重建本机程序索引：旧索引在扫描期间持续可用，完成后原子覆盖并推送列表刷新
        server.ProgramIndex.StartBackgroundRefresh();

        http.Start();
        return server;
    }

    public void Dispose()
    {
        _http?.Dispose();
    }

    // ---------------- HTTP 请求处理 ----------------

    private async Task HandleRequestAsync(LocalHttpRequest request, LocalHttpResponse response)
    {
        var watch = Stopwatch.StartNew();
        ApplySecurityHeaders(request.Path, response);

        if (!IsAllowedHost(request.Host))
        {
            Log.Warn($"Host 头校验失败，已拒绝：Host='{Log.Redact(request.Host)}' Path={request.Path}");
            response.SetText(403, "Forbidden: invalid Host header");
        }
        else if (!request.IsGet && !request.IsHead)
        {
            response.Headers["Allow"] = "GET, HEAD";
            response.SetText(405, "Method Not Allowed");
        }
        else
        {
            try
            {
                await RouteAsync(request, response).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"本地 HTTP 请求处理失败：{ex}");
                response.SetText(500, "Internal Server Error");
            }
        }

        watch.Stop();
        Log.Info($"{request.Method} {request.Path} -> {response.StatusCode} ({watch.ElapsedMilliseconds}ms)");
    }

    private async Task RouteAsync(LocalHttpRequest request, LocalHttpResponse response)
    {
        if (request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(
                new HealthResponse("ok", AppInfo.Version, Port), AppJsonContext.Default.HealthResponse);
            response.ContentType = "application/json; charset=utf-8";
            response.Body = Encoding.UTF8.GetBytes(json);
            return;
        }

        if (request.Path.StartsWith(HostUiPrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            await ServeHostAssetAsync(request, response).ConfigureAwait(false);
            return;
        }

        if (request.Path.StartsWith(ToolsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await ServeToolAsync(request, response).ConfigureAwait(false);
            return;
        }

        response.SetText(404, "Not Found");
    }

    private async Task ServeHostAssetAsync(LocalHttpRequest request, LocalHttpResponse response)
    {
        var relative = request.Path[(HostUiPrefix.Length + 1)..];
        if (relative.StartsWith("app-icon/", StringComparison.OrdinalIgnoreCase))
        {
            var appId = relative["app-icon/".Length..];
            if (!HostAssetEndpoint.TryResolveAppIcon(appId, out var iconPath))
            {
                response.SetText(404, "Not Found");
                return;
            }

            response.ContentType = "image/png";
            response.Body = await File.ReadAllBytesAsync(iconPath).ConfigureAwait(false);
            return;
        }

        if (!HostAssetEndpoint.TryResolveResource(relative, out var resourceName, out var normalizedPath))
        {
            response.SetText(404, "Not Found");
            return;
        }

        var bytes = await HostAssetEndpoint.ReadResourceAsync(resourceName).ConfigureAwait(false);
        if (bytes is null)
        {
            Log.Warn($"内嵌资源读取失败：{resourceName}");
            response.SetText(404, "Not Found");
            return;
        }

        var extension = Path.GetExtension(normalizedPath);
        var isHtml = extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
        if (isHtml)
        {
            bytes = TransformHtml(bytes, "__host__");
        }

        response.ContentType = ContentTypes.For(normalizedPath);
        response.Body = bytes;
    }

    private async Task ServeToolAsync(LocalHttpRequest request, LocalHttpResponse response)
    {
        var file = await ToolStaticFiles.ReadAsync(
            request.Path,
            Tools,
            QTools.Host.Configuration.AppPaths.ToolsDirectory,
            MaxFileBytes).ConfigureAwait(false);
        if (file is null)
        {
            response.SetText(404, "Not Found");
            return;
        }

        response.StatusCode = file.StatusCode;
        response.ContentType = file.ContentType;
        response.Body = file.IsHtml && file.StatusCode == 200
            ? TransformHtml(file.Body, file.ToolId)
            : file.Body;
    }

    /// <summary>HTML 注入：桥接垫层 + 主题契约（宿主页面只注入主题）。</summary>
    private static byte[] TransformHtml(byte[] htmlBytes, string toolId)
    {
        if (htmlBytes.Length > MaxHtmlBytes)
        {
            Log.Warn($"HTML 响应超过 {MaxHtmlBytes} 字节，跳过注入：{toolId}");
            return htmlBytes;
        }

        var html = Encoding.UTF8.GetString(htmlBytes);
        var theme = QTools.Host.Configuration.ThemeSetting.Normalize(
            QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath).Shell?.Theme);

        var transformed = HtmlRewriter.InjectBridgeScript(html, toolId, AppInfo.Version);
        if (toolId == "__host__")
        {
            // 宿主页面：只注入显式主题，首屏即正确配色（system 交给 CSS 媒体查询）
            transformed = HtmlRewriter.InjectThemeAttribute(
                transformed, QTools.Host.Configuration.ThemeSetting.Explicit(theme));
        }
        else
        {
            // 工具页面：窗口控制条 + 主题契约（data-theme / 旧工具反色兜底 / 实时切换脚本）
            transformed = HtmlRewriter.InjectWindowControlsScript(transformed);
            transformed = HtmlRewriter.InjectToolTheme(
                transformed, theme, QTools.Host.Configuration.ThemeSetting.IsDarkNow(theme));
        }

        return Encoding.UTF8.GetBytes(transformed);
    }

    private void ApplySecurityHeaders(string path, LocalHttpResponse response)
    {
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Content-Security-Policy"] = CspFor(path);
    }

    private bool IsAllowedHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var normalized = host.Trim().ToLowerInvariant();
        return normalized == $"127.0.0.1:{Port}" || normalized == $"localhost:{Port}";
    }

    /// <summary>工具页面 CSP（含 manifest 的 allowEval / csp 覆盖）。</summary>
    private string CspFor(string path)
    {
        var isHost = path.Equals(HostUiPrefix, StringComparison.OrdinalIgnoreCase) ||
                     path.StartsWith(HostUiPrefix + "/", StringComparison.OrdinalIgnoreCase);
        if (isHost)
        {
            return HostUiCsp;
        }

        if (!path.StartsWith(ToolsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ToolStaticFiles.DefaultToolCsp;
        }

        var rest = path[ToolsPrefix.Length..];
        var slash = rest.IndexOf('/');
        var toolId = slash < 0 ? rest : rest[..slash];

        if (Tools.TryGet(toolId, out var tool) && tool is not null)
        {
            if (!string.IsNullOrWhiteSpace(tool.Manifest.Csp))
            {
                return tool.Manifest.Csp!;
            }

            return tool.Manifest.AllowEval
                ? ToolStaticFiles.ToolCspAllowEval
                : ToolStaticFiles.DefaultToolCsp;
        }

        return ToolStaticFiles.DefaultToolCsp;
    }

}
