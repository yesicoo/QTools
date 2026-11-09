using System.Text;
using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Logging;
using QTools.Host.Tools;

namespace QTools.Host.Creator;

/// <summary>
/// 本地 HTML 导入器：用户选一个 .html 文件 + 起个名字，就变成一个工具。
/// <para>
/// 与 AI 创建共用同一套安全标准（<see cref="ContentSecurityScanner"/>），但权限由代码用法
/// **自动检测补齐**——用户不需要懂权限体系，也不会遗漏或过度声明。
/// </para>
/// </summary>
public sealed class ToolImporter
{
    /// <summary>导入的 HTML 体积上限（与桥接消息上限保持一致，留出余量给 manifest）。</summary>
    public const int MaxHtmlBytes = 512 * 1024;

    private const int DefaultWindowWidth = 960;
    private const int DefaultWindowHeight = 720;

    private readonly ToolRegistry _registry;
    private readonly string _toolsRoot;

    public ToolImporter(ToolRegistry registry, string toolsRoot)
    {
        _registry = registry;
        _toolsRoot = toolsRoot;
    }

    /// <summary>导入结果。<see cref="Saved"/> 为 false 表示需用户确认高危项后重试。</summary>
    public sealed record ImportResult(
        string ToolId,
        string ToolName,
        string Url,
        bool Saved,
        IReadOnlyList<string> Permissions,
        IReadOnlyList<SecurityFinding> Findings,
        bool HasFatal,
        bool RequiresExplicitAcceptance,
        IReadOnlyList<string> ExternalAssets);

    /// <summary>导入一个本地 HTML 文件为工具。</summary>
    public async System.Threading.Tasks.Task<ImportResult> ImportAsync(
        string name,
        string html,
        string? description,
        bool acceptHighSeverity,
        System.Threading.CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BridgeException("IMPORT_NAME_REQUIRED", "请填写工具名称");
        }

        name = name.Trim();
        if (name.Length > 60)
        {
            throw new BridgeException("IMPORT_NAME_TOO_LONG", "工具名称不超过 60 字符");
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            throw new BridgeException("IMPORT_HTML_REQUIRED", "HTML 内容为空");
        }

        var htmlBytes = Encoding.UTF8.GetByteCount(html);
        if (htmlBytes > MaxHtmlBytes)
        {
            throw new BridgeException(
                "IMPORT_HTML_TOO_LARGE",
                $"HTML 文件过大（{htmlBytes / 1024} KB），上限 {MaxHtmlBytes / 1024} KB");
        }

        // 权限按代码实际用法自动检测（不猜、不让用户手填）
        var permissions = ContentSecurityScanner.DetectPermissions(html);
        var externalAssets = ContentSecurityScanner.DetectExternalAssetReferences(html);

        var manifest = new ToolManifest(
            GenerateToolId(name),
            name,
            "1.0.0",
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            "index.html",
            permissions,
            new ToolWindowSettings(DefaultWindowWidth, DefaultWindowHeight, true),
            DateTimeOffset.Now.ToString("o"),
            AllowEval: false,
            Csp: null);

        var (findings, hasFatal) = ContentSecurityScanner.Scan(manifest, html);

        if (hasFatal)
        {
            var fatal = string.Join("；", findings
                .Where(f => f.Severity == SecuritySeverity.Fatal)
                .Select(f => f.Message));
            Log.Warn($"导入被拒绝（{manifest.Id}）：{fatal}");
            throw new BridgeException("IMPORT_REJECTED", $"内容未通过安全审查：{fatal}");
        }

        var requiresAcceptance = findings.Any(f => f.Severity == SecuritySeverity.High);
        if (requiresAcceptance && !acceptHighSeverity)
        {
            return new ImportResult(
                manifest.Id, name, string.Empty, false, permissions, findings, false, true, externalAssets);
        }

        var directory = Path.Combine(_toolsRoot, manifest.Id);
        Directory.CreateDirectory(directory);

        var indexPath = Path.Combine(directory, "index.html");
        await File.WriteAllTextAsync(indexPath, html, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        var manifestJson = JsonSerializer.Serialize(
            manifest,
            QTools.Host.Serialization.AppJsonContext.Default.ToolManifest);
        await File.WriteAllTextAsync(
                Path.Combine(directory, "manifest.json"),
                manifestJson,
                new UTF8Encoding(false),
                cancellationToken)
            .ConfigureAwait(false);

        // 重新扫描注册表，让新工具立刻出现在列表里
        var loaded = _registry.Reload(manifest.Id);
        if (loaded is null || !loaded.IsValid)
        {
            var errors = loaded?.Errors is { Count: > 0 }
                ? string.Join("；", loaded.Errors)
                : "未知原因";
            Log.Warn($"导入的工具未通过运行时校验（{manifest.Id}）：{errors}");
            throw new BridgeException("IMPORT_REJECTED", $"工具未通过运行时校验：{errors}");
        }

        Log.Info($"已导入工具 {manifest.Id}（{name}，权限：{(permissions.Count == 0 ? "无" : string.Join(",", permissions))}）");

        var url = $"/tools/{manifest.Id}/index.html";
        return new ImportResult(
            manifest.Id, name, url, true, permissions, findings, false, false, externalAssets);
    }

    /// <summary>
    /// 由工具名称生成合法的工具 ID：优先取名称中的 ASCII 字母数字（中文名则退化为哈希后缀），
    /// 并保证在 tools/ 下唯一。
    /// </summary>
    internal string GenerateToolId(string name)
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
        if (slug.Length > 63)
        {
            slug = slug[..63].Trim('-');
        }

        if (slug.Length == 0)
        {
            // 名称全是中文等非 ASCII：用稳定哈希后缀，保证同名工具每次得到同一个 ID
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(name));
            slug = "imported-" + Convert.ToHexString(hash)[..8].ToLowerInvariant();
        }

        return EnsureUnique(slug);
    }

    private string EnsureUnique(string candidate)
    {
        if (!_registry.Contains(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var next = $"{candidate}-{suffix}";
            if (!_registry.Contains(next))
            {
                return next;
            }
        }

        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(candidate + DateTimeOffset.UtcNow.Ticks));
        return $"{candidate}-{Convert.ToHexString(hash)[..6].ToLowerInvariant()}";
    }
}
