using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using QTools.Host.Logging;
using QTools.Host.Serialization;
using QTools.Host.Tools;

namespace QTools.Host.Server;

/// <summary>
/// /tools/{toolId}/** 安全静态文件服务。
/// 实现设计文档 §6.5 的七条路径穿越防护 + §6.6 MIME 白名单 + §6.7 CSP + §6.8 HTML 注入。
/// </summary>
public sealed partial class ToolStaticFileMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ToolRegistry _tools;
    private readonly string _toolsRoot;
    private readonly long _maxFileBytes;

    private const string ToolsPrefix = "/tools/";

    // 工具页面默认 CSP（manifest 可通过 csp/allowEval 调整）。
    // 政策（2026-11 调整）：单文件 HTML 的目的在于导入导出与备份，不是禁网——
    // 放开 https: 的 CDN / 外部资源与联网；eval 仍默认禁止，只有 allowEval=true 才放开。
    public const string DefaultToolCsp =
        "default-src 'self'; base-uri 'none'; object-src 'none'; frame-src 'self' https:; " +
        "script-src 'self' 'unsafe-inline' https:; style-src 'self' 'unsafe-inline' https:; " +
        "img-src 'self' data: blob: https:; font-src 'self' data: https:; media-src 'self' data: blob: https:; " +
        "connect-src 'self' https: wss:; form-action 'none'; frame-ancestors 'self'";

    /// <summary>manifest.allowEval=true 时启用（创建流程中需用户显式确认）。</summary>
    public const string ToolCspAllowEval =
        "default-src 'self'; base-uri 'none'; object-src 'none'; frame-src 'self' https:; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' https:; style-src 'self' 'unsafe-inline' https:; " +
        "img-src 'self' data: blob: https:; font-src 'self' data: https:; media-src 'self' data: blob: https:; " +
        "connect-src 'self' https: wss:; form-action 'none'; frame-ancestors 'self'";

    public ToolStaticFileMiddleware(RequestDelegate next, ToolRegistry tools, string toolsRoot, long maxFileBytes)
    {
        _next = next;
        _tools = tools;
        _toolsRoot = Path.GetFullPath(toolsRoot);
        _maxFileBytes = maxFileBytes;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith(ToolsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var relative = path[ToolsPrefix.Length..];
        var slash = relative.IndexOf('/');
        var toolId = slash < 0 ? relative : relative[..slash];
        var rest = slash < 0 ? "" : relative[(slash + 1)..];

        // 规则 1：toolId 必须匹配白名单正则
        if (!ToolIdPattern().IsMatch(toolId))
        {
            await RejectAsync(context, $"非法 toolId '{toolId}'");
            return;
        }

        // 规则 2：拒绝反斜杠与空字节（防 Windows 分隔符混淆）
        if (rest.Contains('\\') || rest.Contains('\0'))
        {
            await RejectAsync(context, "路径包含非法字符");
            return;
        }

        // 规则 3：拒绝 . 与 .. 段
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Contains('\0'))
            {
                await RejectAsync(context, "路径包含 . 或 .. 段");
                return;
            }

            // 规则 3b：拒绝点号前缀目录（.versions 等宿主内部目录，绝不对外暴露）
            if (segment.StartsWith('.') && segment.Length > 1)
            {
                await RejectAsync(context, "路径包含宿主内部目录");
                return;
            }
        }

        if (segments.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var toolDirectory = Path.Combine(_toolsRoot, toolId);

        // 规则 4：规范化后必须仍位于工具目录内
        var fullPath = Path.GetFullPath(Path.Combine(toolDirectory, string.Join(Path.DirectorySeparatorChar, segments)));
        var expectedPrefix = Path.GetFullPath(toolDirectory) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(expectedPrefix, comparison))
        {
            await RejectAsync(context, "路径穿越被拒绝");
            return;
        }

        // 规则 5：必须是已存在的普通文件（目录 → 404，不提供目录列表）
        if (!File.Exists(fullPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // 规则 6：单文件大小上限
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > _maxFileBytes)
        {
            await RejectAsync(context, $"文件超过大小上限 {_maxFileBytes} 字节");
            return;
        }

        // 规则 7：符号链接逃逸——真实路径必须仍在工具目录内
        var realPath = fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
        if (!Path.GetFullPath(realPath).StartsWith(expectedPrefix, comparison))
        {
            await RejectAsync(context, "符号链接逃逸被拒绝");
            return;
        }

        var extension = Path.GetExtension(fullPath);
        var contentType = ContentTypes.For(fullPath);
        var isHtml = extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);

        // CSP：按 manifest 覆盖（allowEval / csp）
        context.Response.Headers["Content-Security-Policy"] = BuildCsp(toolId);

        if (!isHtml)
        {
            context.Response.ContentType = contentType;
            await context.Response.SendFileAsync(fullPath);
            return;
        }

        // HTML：读入 → 注入桥接垫层与独立窗口控制条 → 输出
        var html = await File.ReadAllTextAsync(fullPath);
        var transformed = HtmlRewriter.InjectWindowControlsScript(
            HtmlRewriter.InjectBridgeScript(html, toolId, QTools.Host.AppInfo.Version));
        var bytes = System.Text.Encoding.UTF8.GetBytes(transformed);
        context.Response.ContentType = contentType;
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes);
    }

    private string BuildCsp(string toolId)
    {
        if (!_tools.TryGet(toolId, out var tool) || tool is null)
        {
            return DefaultToolCsp;
        }

        if (!string.IsNullOrWhiteSpace(tool.Manifest.Csp))
        {
            // 整串覆盖：高级用法，创建流程中标记为高风险
            return tool.Manifest.Csp!;
        }

        return tool.Manifest.AllowEval ? ToolCspAllowEval : DefaultToolCsp;
    }

    private static async Task RejectAsync(HttpContext context, string reason)
    {
        Log.Warn($"静态文件请求被拒绝：{reason} Path={context.Request.Path}");
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("Not Found");
    }

    [GeneratedRegex(ToolManifestValidator.ToolIdRegexPattern)]
    private static partial Regex ToolIdPattern();
}
