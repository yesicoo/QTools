using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QTools.Host.Launcher;

/// <summary>一个在线工具（webtools/{id}/web.json）：录入网址后由独立 WebView 窗口在线打开。</summary>
public sealed record WebToolEntry(
    string Id,
    string Name,
    string Url,
    string? Category,
    string? Glyph,
    string CreatedAt,
    string? Description = null);

/// <summary>已加载的在线工具 + 校验状态。</summary>
public sealed class LoadedWebTool
{
    public required WebToolEntry Entry { get; init; }

    public required string Directory { get; init; }

    public bool IsValid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public string Id => Entry.Id;

    public string Name => string.IsNullOrWhiteSpace(Entry.Name) ? Entry.Id : Entry.Name;
}

/// <summary>manager.addWebTool 的载荷（字段与导入工具表单对齐：名称/描述/分类/自定义图标 + 网址）。</summary>
public sealed record AddWebToolPayload(
    string Name,
    string Url,
    string? Category,
    string? Glyph,
    string? Description = null);

/// <summary>在线工具静态校验规则（纯函数，便于单测）。</summary>
public static partial class WebToolValidator
{
    /// <summary>网址允许的最大长度（浏览器与服务器实践上限取整）。</summary>
    public const int MaxUrlLength = 2048;

    public static IReadOnlyList<string> Validate(WebToolEntry entry, string expectedDirectoryName)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(entry.Id) || !AppEntryValidator.AppIdPattern().IsMatch(entry.Id))
        {
            errors.Add("id 必须匹配 ^[a-z0-9][a-z0-9-]{0,62}$（小写字母/数字/短横线）");
        }
        else if (!string.Equals(entry.Id, expectedDirectoryName, StringComparison.Ordinal))
        {
            errors.Add($"id({entry.Id}) 必须与目录名({expectedDirectoryName}) 一致");
        }

        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Trim().Length > 60)
        {
            errors.Add("name 必填且不超过 60 字符");
        }

        if (entry.Description is { Length: > 300 })
        {
            errors.Add("description 不超过 300 字符");
        }

        if (string.IsNullOrWhiteSpace(entry.Url))
        {
            errors.Add("url 必填（http:// 或 https:// 开头的网址）");
        }
        else
        {
            var url = entry.Url.Trim();
            if (url.Length > MaxUrlLength)
            {
                errors.Add($"url 不超过 {MaxUrlLength} 字符");
            }
            else if (url.Any(char.IsControl))
            {
                errors.Add("url 包含非法控制字符");
            }
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                     (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                errors.Add("url 必须是 http:// 或 https:// 开头的合法网址");
            }
        }

        if (entry.Category is { Length: > 30 })
        {
            errors.Add("category 不超过 30 字符");
        }

        if (entry.Glyph is { Length: > 4 })
        {
            errors.Add("glyph 不超过 4 字符");
        }

        return errors;
    }
}
