using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using QTools.Host.Logging;

namespace QTools.Host.Server;

/// <summary>
/// 宿主管理界面静态资源：全部来自 EmbeddedResource（保证单文件自包含），白名单式文件名校验。
/// </summary>
/// <remarks>
/// 资源名不硬编码：MSBuild 会把目录名中的 '-' 转成 '_'（host-ui → host_ui），
/// 且不同 SDK 版本行为可能有差异。这里启动时枚举一次，按「URL 路径 → 资源名」建表，
/// 对连字符/下划线/大小写差异免疫。URL 路径规则：去掉资源前缀后再去掉第一个段（目录名）。
/// </remarks>
internal static partial class HostAssetEndpoint
{
    private const string ResourcePrefix = "QTools.Host.Assets.";

    private static readonly Assembly Assembly = typeof(HostAssetEndpoint).Assembly;

    /// <summary>URL 路径（小写） → 内嵌资源名。</summary>
    private static readonly Dictionary<string, string> Resources = BuildIndex();

    public static async Task HandleAsync(HttpContext context)
    {
        var relativePath = context.Request.RouteValues["path"]?.ToString() ?? string.Empty;
        var normalized = Normalize(relativePath);
        if (normalized is null || !Resources.TryGetValue(normalized.ToLowerInvariant(), out var resourceName))
        {
            Log.Debug($"宿主资源不存在：{ResourcePrefix}{normalized?.Replace('/', '.') ?? relativePath}");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            Log.Warn($"内嵌资源读取失败：{resourceName}");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = ContentTypes.For(normalized);
        await stream.CopyToAsync(context.Response.Body);
    }

    /// <summary>
    /// 程序真实图标：手动启动项读 apps/{id}/icon.png，自动索引程序（idx- 前缀）读 data/icon-cache/{id}.png。
    /// 不存在时 404，前端回退字母头像。
    /// </summary>
    public static async Task ServeAppIconAsync(HttpContext context)
    {
        var appId = context.Request.RouteValues["appId"]?.ToString() ?? string.Empty;
        if (!Launcher.AppEntryValidator.AppIdPattern().IsMatch(appId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var path = appId.StartsWith("idx-", StringComparison.Ordinal)
            ? Path.Combine(Configuration.AppPaths.IconCacheDirectory, appId + ".png")
            : Path.Combine(Configuration.AppPaths.AppsDirectory, appId, "icon.png");
        if (!File.Exists(path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "image/png";
        await context.Response.SendFileAsync(path);
    }

    /// <summary>枚举内嵌资源，建立 URL 路径映射（只收录一级目录下的文件，如 host-ui/index.html）。</summary>
    private static Dictionary<string, string> BuildIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in Assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = name[ResourcePrefix.Length..];

            // relative 形如 "host-ui.index.html" / "host_ui.index.html"：去掉第一个段（目录名）得到 URL 路径
            var separator = relative.IndexOf('.');
            if (separator <= 0 || separator == relative.Length - 1)
            {
                continue;
            }

            var urlPath = relative[(separator + 1)..];
            index[urlPath.ToLowerInvariant()] = name;
        }

        return index;
    }

    private static string? Normalize(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.StartsWith('.') || !FileNamePattern().IsMatch(segment))
            {
                return null;
            }
        }

        return string.Join('/', segments);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex FileNamePattern();
}
