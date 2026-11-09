using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QTools.Host.Tools;

/// <summary>
/// 工具权限白名单（manifest 权限最小化的枚举边界）。
/// <list type="bullet">
/// <item><see cref="Storage"/> — `window.host.storage.*` 键值存储</item>
/// <item><see cref="Ai"/> — `window.host.ai.chat` 宿主 AI 代理</item>
/// <item><see cref="Media"/> — `getUserMedia` / `getDisplayMedia` 麦克风、摄像头、屏幕捕获</item>
/// <item><see cref="Files"/> — `window.host.files.*` 工具目录内的二进制文件存取</item>
/// </list>
/// </summary>
public enum ToolPermission
{
    Storage,
    Ai,
    Media,
    Files,
}

/// <summary>manifest.json 中 window 段。</summary>
public sealed record ToolWindowSettings(int Width, int Height, bool Resizable);

/// <summary>
/// manifest.json 数据模型（源生成 JSON；未知字段忽略以保证前向兼容）。
/// 规范见 docs/02-manifest规范.md 与设计文档 §4.1。
/// </summary>
public sealed record ToolManifest(
    string Id,
    string Name,
    string Version,
    string? Description,
    string Entry,
    IReadOnlyList<string> Permissions,
    ToolWindowSettings Window,
    string CreatedAt,
    bool AllowEval,
    string? Csp);

/// <summary>manifest 静态校验规则（纯函数，便于单测）。</summary>
public static partial class ToolManifestValidator
{
    public const int MinWindowSize = 320;
    public const int MaxWindowSize = 3840;

    /// <summary>
    /// 工具 ID 规则（全宿主唯一来源，Server 层的路由校验也用它，避免规则漂移）：
    /// 小写字母/数字/短横线，1~63 字符，首尾都不能是短横线。
    /// </summary>
    public const string ToolIdRegexPattern = "^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$";

    [GeneratedRegex(ToolIdRegexPattern)]
    public static partial Regex ToolIdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    public static partial Regex VersionPattern();

    /// <summary>校验 manifest；返回空列表表示通过。</summary>
    public static IReadOnlyList<string> Validate(ToolManifest manifest, string expectedDirectoryName)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(manifest.Id) || !ToolIdPattern().IsMatch(manifest.Id))
        {
            errors.Add("id 必须匹配 ^[a-z0-9][a-z0-9-]{0,62}$（小写字母/数字/短横线）");
        }
        else if (!string.Equals(manifest.Id, expectedDirectoryName, StringComparison.Ordinal))
        {
            errors.Add($"id({manifest.Id}) 必须与工具目录名({expectedDirectoryName}) 一致");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Trim().Length > 60)
        {
            errors.Add("name 必填且不超过 60 字符");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version) || !VersionPattern().IsMatch(manifest.Version))
        {
            errors.Add("version 必填且为语义化版本（如 1.0.0）");
        }

        if (manifest.Description is { Length: > 300 })
        {
            errors.Add("description 不超过 300 字符");
        }

        if (string.IsNullOrWhiteSpace(manifest.Entry))
        {
            errors.Add("entry 必填（默认 index.html）");
        }
        else if (!IsSafeRelativePath(manifest.Entry))
        {
            errors.Add("entry 必须是工具目录内的相对路径（禁止 ..、绝对路径、盘符、反斜杠）");
        }

        foreach (var permission in manifest.Permissions ?? Array.Empty<string>())
        {
            if (!TryParsePermission(permission, out _))
            {
                errors.Add($"未知权限 '{permission}'（仅支持 storage / ai / media / files）");
            }
        }

        if (manifest.Window is null)
        {
            errors.Add("window 段缺失");
        }
        else
        {
            if (manifest.Window.Width is < MinWindowSize or > MaxWindowSize ||
                manifest.Window.Height is < MinWindowSize or > MaxWindowSize)
            {
                errors.Add($"window.width/height 必须在 {MinWindowSize}~{MaxWindowSize} 之间");
            }
        }

        if (string.IsNullOrWhiteSpace(manifest.CreatedAt) ||
            !DateTimeOffset.TryParse(manifest.CreatedAt, out _))
        {
            errors.Add("createdAt 必填且为 ISO-8601 日期时间");
        }

        return errors;
    }

    /// <summary>入口路径安全校验：规范化后必须仍在工具目录内。</summary>
    public static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        if (relativePath.Contains('\\') || relativePath.Contains('\0') || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.StartsWith('.') || segment.Contains('\0'))
            {
                return false;
            }
        }

        var normalized = Path.GetFullPath(Path.Combine("tool-root", relativePath));
        return normalized.StartsWith(Path.GetFullPath("tool-root") + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static bool TryParsePermission(string? value, out ToolPermission permission)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "storage":
                permission = ToolPermission.Storage;
                return true;
            case "ai":
                permission = ToolPermission.Ai;
                return true;
            case "media":
                permission = ToolPermission.Media;
                return true;
            case "files":
                permission = ToolPermission.Files;
                return true;
            default:
                permission = default;
                return false;
        }
    }

    public static IReadOnlyList<ToolPermission> ParsePermissions(IEnumerable<string>? values)
    {
        var result = new HashSet<ToolPermission>();
        foreach (var value in values ?? Array.Empty<string>())
        {
            if (TryParsePermission(value, out var permission))
            {
                result.Add(permission);
            }
        }

        return result.ToArray();
    }
}
