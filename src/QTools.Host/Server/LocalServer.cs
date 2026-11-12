using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QTools.Host.Ai;
using QTools.Host.Bridge;
using QTools.Host.Creator;
using QTools.Host.Logging;
using QTools.Host.Security;
using QTools.Host.Serialization;
using QTools.Host.Shell;
using QTools.Host.Storage;
using QTools.Host.Tools;

namespace QTools.Host.Server;

/// <summary>
/// Kestrel 本地静态服务：仅绑定 127.0.0.1、随机端口；只提供 /health 与只读静态资源，零写端点。
/// 中间件顺序即安全边界（见设计文档 §6.2）。
/// </summary>
internal sealed partial class LocalServer : IDisposable
{
    private const string HostUiPrefix = "/__host__";
    private const long MaxHtmlBytes = 1024 * 1024;
    private const long MaxRequestBodyBytes = 1024 * 1024;
    private const long MaxFileBytes = 8 * 1024 * 1024;

    // 宿主管理界面 CSP 更严：不允许任何网络请求（能力全部走桥接）
    private const string HostUiCsp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; connect-src 'none'; frame-src 'self'; base-uri 'none'; object-src 'none'; form-action 'none'";

    private readonly WebApplication _app;
    private readonly CancellationTokenSource _shutdown;
    private int _port;

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

    /// <summary>本地备份服务。</summary>
    public Backup.BackupService Backup { get; }

    /// <summary>Git 远程备份。</summary>
    public Backup.GitBackup GitBackup { get; }

    /// <summary>宿主配置。</summary>
    public Configuration.AppConfig Config { get; }

    private LocalServer(WebApplication app, CancellationTokenSource shutdown)
    {
        _app = app;
        _shutdown = shutdown;

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

        var webToolsRoot = QTools.Host.Configuration.AppPaths.WebToolsDirectory;
        WebTools = new Launcher.WebToolRegistry(webToolsRoot);
        WebTools.Rescan();
        WebToolStore = new Launcher.WebToolStore(WebTools, webToolsRoot);

        Backup = new Backup.BackupService(QTools.Host.Configuration.AppPaths.RootDirectory);
        GitBackup = new Backup.GitBackup(
            Backup,
            QTools.Host.Configuration.AppPaths.RootDirectory,
            QTools.Host.Configuration.AppPaths.DataDirectory);

        Dispatcher = new BridgeDispatcher(
            Tools, Storage, Ai, Secrets, Creator, Importer, Apps, Launcher, AppStore, Discovery,
            Backup, GitBackup, ProgramIndex, Usage, WebTools, WebToolStore, HiddenPrograms);
    }

    public int Port => _port;

    public static LocalServer Start(int? fixedPort)
    {
        var listenPort = fixedPort ?? 0;
        var shutdown = new CancellationTokenSource();

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://127.0.0.1:{listenPort}");
        builder.WebHost.UseKestrel(options => options.Limits.MaxRequestBodySize = MaxRequestBodyBytes);

        // 应用日志统一走自研 FileLogger；关闭框架日志，避免噪音与潜在敏感信息
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        var server = new LocalServer(app, shutdown);

        // 管道顺序即安全边界
        app.Use(async (context, next) => await server.HostGuardAsync(context, next));
        app.Use(async (context, next) => await server.RequestLogAsync(context, next));
        app.Use(server.SecurityHeadersAsync);
        app.Use(async (context, next) => await server.HtmlTransformAsync(context, next));

        // /tools/** 安全静态文件（七条穿越防护）
        app.Use(next => new ToolStaticFileMiddleware(
            next,
            server.Tools,
            QTools.Host.Configuration.AppPaths.ToolsDirectory,
            MaxFileBytes).InvokeAsync);

        app.MapGet("/health", () => Results.Json(
            new HealthResponse("ok", AppInfo.Version, server.Port),
            AppJsonContext.Default.HealthResponse));

        // 启动项真实图标（apps/{id}/icon.png，由 WindowsAppIcons 提取缓存）
        app.MapGet($"{HostUiPrefix}/app-icon/{{appId}}", HostAssetEndpoint.ServeAppIconAsync);

        app.Map($"{HostUiPrefix}/{{**path}}", HostAssetEndpoint.HandleAsync);

        app.StartAsync(shutdown.Token).GetAwaiter().GetResult();

        server._port = ResolvePort(app);

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

        return server;
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        try
        {
            _shutdown.Cancel();
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warn($"停止 Kestrel 时出现异常：{ex.Message}");
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    // ---------------- 中间件 ----------------

    /// <summary>Host 头校验（防 DNS rebinding）：只接受 127.0.0.1:{port} 与 localhost:{port}。</summary>
    private Task HostGuardAsync(HttpContext context, RequestDelegate next)
    {
        var host = context.Request.Headers.Host.ToString();
        if (IsAllowedHost(host))
        {
            return next(context);
        }

        Log.Warn($"Host 头校验失败，已拒绝：Host='{Log.Redact(host)}' Path={context.Request.Path}");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsync("Forbidden: invalid Host header");
    }

    private bool IsAllowedHost(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var normalized = host.Trim().ToLowerInvariant();
        return normalized == $"127.0.0.1:{_port}" || normalized == $"localhost:{_port}";
    }

    /// <summary>请求日志：只记 method/path/status/耗时，绝不记请求体（可能含用户数据）。</summary>
    private async Task RequestLogAsync(HttpContext context, RequestDelegate next)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await next(context);
        watch.Stop();
        Log.Info($"{context.Request.Method} {context.Request.Path} -> {context.Response.StatusCode} ({watch.ElapsedMilliseconds}ms)");
    }

    private Task SecurityHeadersAsync(HttpContext context, Func<Task> next)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store";
        headers["Content-Security-Policy"] = context.Request.Path.StartsWithSegments(HostUiPrefix, StringComparison.OrdinalIgnoreCase)
            ? HostUiCsp
            : ToolCspFor(context);

        return next();
    }

    /// <summary>工具页面 CSP（含 manifest 的 allowEval / csp 覆盖）。</summary>
    private string ToolCspFor(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        const string prefix = "/tools/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return ToolStaticFileMiddleware.DefaultToolCsp;
        }

        var rest = path[prefix.Length..];
        var slash = rest.IndexOf('/');
        var toolId = slash < 0 ? rest : rest[..slash];

        if (Tools.TryGet(toolId, out var tool) && tool is not null)
        {
            if (!string.IsNullOrWhiteSpace(tool.Manifest.Csp))
            {
                return tool.Manifest.Csp!;
            }

            return tool.Manifest.AllowEval
                ? ToolStaticFileMiddleware.ToolCspAllowEval
                : ToolStaticFileMiddleware.DefaultToolCsp;
        }

        return ToolStaticFileMiddleware.DefaultToolCsp;
    }

    /// <summary>
    /// HTML 响应重写：注入 window.host 桥接垫层。
    /// PhotinoX 未提供托管侧 JS 注入 API，因此在 HTTP 层注入（设计文档 §5.3/§6.8）。
    /// </summary>
    private async Task HtmlTransformAsync(HttpContext context, RequestDelegate next)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
            buffer.Position = 0;
        }

        var contentType = context.Response.ContentType ?? string.Empty;
        if (!contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
            context.Response.StatusCode != StatusCodes.Status200OK)
        {
            await buffer.CopyToAsync(originalBody);
            return;
        }

        if (buffer.Length > MaxHtmlBytes)
        {
            Log.Warn($"HTML 响应超过 {MaxHtmlBytes} 字节，跳过桥接注入：{context.Request.Path}");
            await buffer.CopyToAsync(originalBody);
            return;
        }

        var html = await new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEndAsync();
        var transformed = HtmlRewriter.InjectBridgeScript(html, ToolIdOf(context.Request.Path), AppInfo.Version);

        var bytes = Encoding.UTF8.GetBytes(transformed);
        context.Response.ContentLength = bytes.Length;
        await originalBody.WriteAsync(bytes);
    }

    /// <summary>从请求路径推导工具 ID（宿主侧权威来源，客户端自述的 toolId 仅作一致性记录）。</summary>
    private static string ToolIdOf(PathString path)
    {
        var value = path.Value ?? string.Empty;
        const string toolsPrefix = "/tools/";
        if (value.StartsWith(toolsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = value[toolsPrefix.Length..];
            var slash = rest.IndexOf('/');
            var id = slash < 0 ? rest : rest[..slash];
            return ToolIdPattern().IsMatch(id) ? id : "__host__";
        }

        return "__host__";
    }

    [GeneratedRegex(ToolManifestValidator.ToolIdRegexPattern)]
    private static partial Regex ToolIdPattern();

    private static int ResolvePort(WebApplication app)
    {
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var feature = server.Features.Get<IServerAddressesFeature>();
            if (feature is not null)
            {
                foreach (var address in feature.Addresses)
                {
                    if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port is > 0 and <= 65535)
                    {
                        return uri.Port;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"通过 IServer 特性读取监听端口失败：{ex.Message}");
        }

        throw new InvalidOperationException("无法确定 Kestrel 实际监听端口（IServerAddressesFeature 未提供地址）");
    }
}
