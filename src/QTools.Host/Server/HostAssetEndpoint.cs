using System.Reflection;
using System.Text.RegularExpressions;
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

    /// <summary>把 /__host__/ 之后的相对路径解析为内嵌资源；非法路径或不存在返回 false。</summary>
    public static bool TryResolveResource(string relativePath, out string resourceName, out string normalized)
    {
        resourceName = string.Empty;
        normalized = string.Empty;

        var path = Normalize(relativePath);
        if (path is null || !Resources.TryGetValue(path.ToLowerInvariant(), out var found))
        {
            Log.Debug($"宿主资源不存在：{ResourcePrefix}{path?.Replace('/', '.') ?? relativePath}");
            return false;
        }

        resourceName = found;
        normalized = path;
        return true;
    }

    /// <summary>读取已解析的内嵌资源；资源缺失返回 null。</summary>
    public static async Task<byte[]?> ReadResourceAsync(string resourceName)
    {
        await using var stream = Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>
    /// 程序真实图标：手动启动项读 apps/{id}/icon.png，自动索引程序（idx- 前缀）读 data/icon-cache/{id}.png。
    /// 不存在返回 false，前端回退字母头像。
    /// </summary>
    public static bool TryResolveAppIcon(string appId, out string path)
    {
        path = string.Empty;
        if (!Launcher.AppEntryValidator.AppIdPattern().IsMatch(appId))
        {
            return false;
        }

        path = appId.StartsWith("idx-", StringComparison.Ordinal)
            ? Path.Combine(Configuration.AppPaths.IconCacheDirectory, appId + ".png")
            : Path.Combine(Configuration.AppPaths.AppsDirectory, appId, "icon.png");
        return File.Exists(path);
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
