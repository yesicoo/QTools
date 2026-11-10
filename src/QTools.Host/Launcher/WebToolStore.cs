using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Logging;
using QTools.Host.Serialization;

namespace QTools.Host.Launcher;

/// <summary>
/// 在线工具存储：新增 / 删除 webtools/{id}/web.json，并维护在线工具注册表。
/// </summary>
public sealed class WebToolStore
{
    private readonly WebToolRegistry _registry;
    private readonly string _webToolsRoot;

    public WebToolStore(WebToolRegistry registry, string webToolsRoot)
    {
        _registry = registry;
        _webToolsRoot = webToolsRoot;
    }

    public string WebToolsRoot => _webToolsRoot;

    /// <summary>由名称生成合法的在线工具 ID（与工具 ID 同规则；中文名退化为哈希）。</summary>
    internal string GenerateId(string name)
    {
        var builder = new StringBuilder();
        var lastWasDash = false;

        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9')
            {
                builder.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > 63) { slug = slug[..63].Trim('-'); }

        if (slug.Length == 0)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
            slug = "web-" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
        }

        return EnsureUnique(slug);
    }

    private string EnsureUnique(string candidate)
    {
        if (!_registry.Contains(candidate)) { return candidate; }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var next = $"{candidate}-{suffix}";
            if (!_registry.Contains(next)) { return next; }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate + DateTimeOffset.UtcNow.Ticks));
        return $"{candidate}-{Convert.ToHexString(hash)[..6].ToLowerInvariant()}";
    }

    /// <summary>新增一个在线工具。</summary>
    public async System.Threading.Tasks.Task<LoadedWebTool> AddAsync(
        string name,
        string url,
        string? category,
        string? glyph,
        System.Threading.CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BridgeException("WEB_NAME_REQUIRED", "请填写工具名称");
        }

        name = name.Trim();
        if (name.Length > 60)
        {
            throw new BridgeException("WEB_NAME_TOO_LONG", "工具名称不超过 60 字符");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new BridgeException("WEB_URL_REQUIRED", "请填写工具网址");
        }

        url = url.Trim();
        if (url.Contains('\0') || url.Any(char.IsControl))
        {
            throw new BridgeException("WEB_URL_INVALID", "网址包含非法字符");
        }

        var entry = new WebToolEntry(
            GenerateId(name),
            name,
            url,
            string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            string.IsNullOrWhiteSpace(glyph) ? null : glyph.Trim(),
            DateTimeOffset.Now.ToString("o"));

        var errors = WebToolValidator.Validate(entry, entry.Id);
        if (errors.Count > 0)
        {
            throw new BridgeException("WEB_INVALID", string.Join("；", errors));
        }

        var directory = Path.Combine(_webToolsRoot, entry.Id);
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "web.json"),
            JsonSerializer.Serialize(entry, AppJsonContext.Default.WebToolEntry),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        var loaded = _registry.Reload(entry.Id);
        if (loaded is null || !loaded.IsValid)
        {
            var reason = loaded?.Errors is { Count: > 0 } ? string.Join("；", loaded.Errors) : "未知原因";
            Log.Warn($"新增在线工具未通过运行时校验（{entry.Id}）：{reason}");
            throw new BridgeException("WEB_INVALID", $"在线工具未通过校验：{reason}");
        }

        Log.Info($"已添加在线工具 {entry.Id}（{name}）→ {url}");
        return loaded;
    }

    /// <summary>删除一个在线工具（只删启动配置，不影响目标网站）。</summary>
    public bool Delete(string webId)
    {
        var tool = _registry.Find(webId);
        if (tool is null) { return false; }

        try
        {
            if (Directory.Exists(tool.Directory))
            {
                Directory.Delete(tool.Directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"删除在线工具失败 {webId}", ex);
            throw new BridgeException("WEB_DELETE_FAILED", $"删除失败：{ex.Message}");
        }

        _registry.Remove(webId);
        Log.Info($"已删除在线工具 {webId}");
        return true;
    }
}
