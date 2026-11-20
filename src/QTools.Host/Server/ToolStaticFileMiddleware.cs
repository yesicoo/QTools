using System.Text;
using System.Text.RegularExpressions;
using QTools.Host.Logging;
using QTools.Host.Tools;

namespace QTools.Host.Server;

/// <summary>
/// /tools/{toolId}/** 安全静态文件服务。
/// 实现设计文档 §6.5 的七条路径穿越防护 + §6.6 MIME 白名单 + §6.7 CSP + §6.8 HTML 注入。
/// 由轻量本地 HTTP 服务调用，不再依赖 ASP.NET Core 中间件。
/// </summary>
internal static partial class ToolStaticFiles
{
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

    /// <summary>
    /// 读取 /tools/** 文件；非该前缀返回 null。
    /// 返回的 HTML 由 LocalServer 统一做桥接/主题注入。
    /// </summary>
    public static async Task<ToolFileResult?> ReadAsync(
        string path, ToolRegistry tools, string toolsRoot, long maxFileBytes)
    {
        if (!path.StartsWith(ToolsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var result = new ToolFileResult();
        var relative = path[ToolsPrefix.Length..];
        var slash = relative.IndexOf('/');
        var toolId = slash < 0 ? relative : relative[..slash];
        var rest = slash < 0 ? "" : relative[(slash + 1)..];
        result.ToolId = toolId;

        // 规则 1：toolId 必须匹配白名单正则
        if (!ToolIdPattern().IsMatch(toolId))
        {
            return Reject(result, $"非法 toolId '{toolId}'");
        }

        // 规则 2：拒绝反斜杠与空字节（防 Windows 分隔符混淆）
        if (rest.Contains('\\') || rest.Contains('\0'))
        {
            return Reject(result, "路径包含非法字符");
        }

        // 规则 3：拒绝 . 与 .. 段
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.Contains('\0'))
            {
                return Reject(result, "路径包含 . 或 .. 段");
            }

            // 规则 3b：拒绝点号前缀目录（.versions 等宿主内部目录，绝不对外暴露）
            if (segment.StartsWith('.') && segment.Length > 1)
            {
                return Reject(result, "路径包含宿主内部目录");
            }
        }

        if (segments.Length == 0)
        {
            result.SetText(404, "Not Found");
            return result;
        }

        var toolDirectory = Path.Combine(toolsRoot, toolId);

        // 规则 4：规范化后必须仍位于工具目录内
        var fullPath = Path.GetFullPath(
            Path.Combine(toolDirectory, string.Join(Path.DirectorySeparatorChar, segments)));
        var expectedPrefix = Path.GetFullPath(toolDirectory) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(expectedPrefix, comparison))
        {
            return Reject(result, "路径穿越被拒绝");
        }

        // 规则 5：必须是已存在的普通文件（目录 → 404，不提供目录列表）
        if (!File.Exists(fullPath))
        {
            result.SetText(404, "Not Found");
            return result;
        }

        // 规则 6：单文件大小上限
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > maxFileBytes)
        {
            return Reject(result, $"文件超过大小上限 {maxFileBytes} 字节");
        }

        // 规则 7：符号链接逃逸——真实路径必须仍在工具目录内
        var realPath = fileInfo.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
        if (!Path.GetFullPath(realPath).StartsWith(expectedPrefix, comparison))
        {
            return Reject(result, "符号链接逃逸被拒绝");
        }

        var extension = Path.GetExtension(fullPath);
        var isHtml = extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);

        if (!isHtml)
        {
            result.StatusCode = 200;
            result.ContentType = ContentTypes.For(fullPath);
            result.Body = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
            return result;
        }

        // HTML：读入后原样返回；桥接 / 窗口控制 / 主题注入由 LocalServer 统一完成
        var html = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
        result.StatusCode = 200;
        result.ContentType = ContentTypes.For(fullPath);
        result.Body = Encoding.UTF8.GetBytes(html);
        result.IsHtml = true;
        return result;
    }

    private static ToolFileResult Reject(ToolFileResult result, string reason)
    {
        Log.Warn($"静态文件请求被拒绝：{reason} Path={result.ToolId}");
        result.SetText(404, "Not Found");
        return result;
    }

    [GeneratedRegex(ToolManifestValidator.ToolIdRegexPattern)]
    private static partial Regex ToolIdPattern();
}

/// <summary>工具静态文件读取结果。</summary>
internal sealed class ToolFileResult
{
    public int StatusCode { get; set; }
    public string? ContentType { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public bool IsHtml { get; set; }
    public string ToolId { get; set; } = string.Empty;

    public void SetText(int status, string text)
    {
        StatusCode = status;
        ContentType = "text/plain; charset=utf-8";
        Body = Encoding.UTF8.GetBytes(text);
    }
}
