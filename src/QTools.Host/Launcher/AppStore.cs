using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Logging;
using QTools.Host.Serialization;

namespace QTools.Host.Launcher;

/// <summary>
/// 启动项存储：新增 / 删除 apps/{id}/app.json，并维护启动项注册表。
/// </summary>
public sealed class AppStore
{
    private readonly AppRegistry _registry;
    private readonly string _appsRoot;

    public AppStore(AppRegistry registry, string appsRoot)
    {
        _registry = registry;
        _appsRoot = appsRoot;
    }

    public string AppsRoot => _appsRoot;

    /// <summary>由名称生成合法的启动项 ID（与工具 ID 同规则；中文名退化为哈希）。</summary>
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
            slug = "app-" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
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

    /// <summary>新增一个启动项。</summary>
    public async System.Threading.Tasks.Task<LoadedApp> AddAsync(
        string name,
        string target,
        string? arguments,
        string? workingDirectory,
        string? category,
        string? glyph,
        System.Threading.CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BridgeException("APP_NAME_REQUIRED", "请填写程序名称");
        }

        name = name.Trim();
        if (name.Length > 60)
        {
            throw new BridgeException("APP_NAME_TOO_LONG", "程序名称不超过 60 字符");
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            throw new BridgeException("APP_TARGET_REQUIRED", "请填写程序路径");
        }

        target = target.Trim();
        if (target.Contains('\0'))
        {
            throw new BridgeException("APP_TARGET_INVALID", "程序路径包含非法字符");
        }

        var entry = new AppEntry(
            GenerateId(name),
            name,
            target,
            string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim(),
            string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim(),
            string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            string.IsNullOrWhiteSpace(glyph) ? null : glyph.Trim(),
            DateTimeOffset.Now.ToString("o"));

        var errors = AppEntryValidator.Validate(entry, entry.Id);
        if (errors.Count > 0)
        {
            throw new BridgeException("APP_INVALID", string.Join("；", errors));
        }

        var directory = Path.Combine(_appsRoot, entry.Id);
        Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "app.json"),
            JsonSerializer.Serialize(entry, AppJsonContext.Default.AppEntry),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);

        var loaded = _registry.Reload(entry.Id);
        if (loaded is null || !loaded.IsValid)
        {
            var reason = loaded?.Errors is { Count: > 0 } ? string.Join("；", loaded.Errors) : "未知原因";
            Log.Warn($"新增启动项未通过运行时校验（{entry.Id}）：{reason}");
            throw new BridgeException("APP_INVALID", $"启动项未通过校验：{reason}");
        }

        Log.Info($"已添加本机程序 {entry.Id}（{name}）→ {target}");
        return loaded;
    }

    /// <summary>删除一个启动项（只删启动配置，不动系统里的程序本身）。</summary>
    public bool Delete(string appId)
    {
        var app = _registry.Find(appId);
        if (app is null) { return false; }

        try
        {
            if (Directory.Exists(app.Directory))
            {
                Directory.Delete(app.Directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"删除启动项失败 {appId}", ex);
            throw new BridgeException("APP_DELETE_FAILED", $"删除失败：{ex.Message}");
        }

        _registry.Remove(appId);
        Log.Info($"已删除启动项 {appId}");
        return true;
    }
}
