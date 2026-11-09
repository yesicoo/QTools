using System.Text.RegularExpressions;
using QTools.Host.Tools;

namespace QTools.Host.Creator;

/// <summary>安全审查发现项的严重级别。</summary>
public enum SecuritySeverity
{
    /// <summary>提示级：不阻止保存，但需向用户明示。</summary>
    Warning,

    /// <summary>高危：需用户显式勾选确认才能保存。</summary>
    High,

    /// <summary>致命：直接拒绝保存（如 manifest 结构非法）。</summary>
    Fatal,
}

public sealed record SecurityFinding(SecuritySeverity Severity, string Code, string Message);

/// <summary>
/// 生成内容静态安全审查（设计文档 §8.2 第四道边界）。
/// <para>
/// 规则：禁 eval/new Function/动态 import、禁疑似硬编码 API Key、manifest 权限最小化；
/// 远程脚本与外部网络请求为提示级（不阻止保存，但向用户明示——工具允许引用 CDN/外部资源与联网）。
/// 全部用 <c>[GeneratedRegex]</c> 源生成正则——AOT 兼容且无需反射。
/// </para>
/// </summary>
public static partial class ContentSecurityScanner
{
    // ---- 高危模式 ----
    [GeneratedRegex(@"\beval\s*\(")]
    private static partial Regex EvalPattern();

    [GeneratedRegex(@"\bnew\s+Function\s*\(")]
    private static partial Regex NewFunctionPattern();

    [GeneratedRegex(@"\bimport\s*\(")]
    private static partial Regex DynamicImportPattern();

    [GeneratedRegex(@"(?i)<script[^>]+src\s*=\s*[""']?\s*(?:https?:)?//")]
    private static partial Regex RemoteScriptPattern();

    [GeneratedRegex(@"(?i)\bfetch\s*\(\s*[""']\s*(?:https?:)?//")]
    private static partial Regex RemoteFetchPattern();

    [GeneratedRegex(@"(?i)\bXMLHttpRequest\b")]
    private static partial Regex XhrPattern();

    [GeneratedRegex(@"(?i)\bWebSocket\s*\(")]
    private static partial Regex WebSocketPattern();

    [GeneratedRegex(@"(?i)\bnavigator\s*\.\s*sendBeacon\s*\(")]
    private static partial Regex SendBeaconPattern();

    [GeneratedRegex(@"(?i)<\s*iframe[^>]+src\s*=\s*[""']?\s*(?:https?:)?//")]
    private static partial Regex RemoteIframePattern();

    [GeneratedRegex(@"(?i)\bdocument\s*\.\s*write\s*\(")]
    private static partial Regex DocumentWritePattern();

    [GeneratedRegex(@"(?i)\bsrcdoc\s*=")]
    private static partial Regex SrcDocPattern();

    // ---- 疑似硬编码 Key ----
    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{16,}")]
    private static partial Regex OpenAiKeyPattern();

    [GeneratedRegex(@"(?i)\b(?:api[_-]?key|apikey|access[_-]?token)\b\s*[:=]\s*[""'][^""']{12,}[""']")]
    private static partial Regex ApiKeyAssignmentPattern();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsKeyPattern();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._\-]{20,}")]
    private static partial Regex BearerPattern();

    // ---- 能力使用检测（核对 manifest 权限与实际用法是否一致）----
    [GeneratedRegex(@"\bgetUserMedia\s*\(")]
    private static partial Regex GetUserMediaPattern();

    [GeneratedRegex(@"\bgetDisplayMedia\s*\(")]
    private static partial Regex GetDisplayMediaPattern();

    [GeneratedRegex(@"\bwindow\s*\.\s*host\s*\.\s*files\s*\.")]
    private static partial Regex HostFilesPattern();

    [GeneratedRegex(@"\bwindow\s*\.\s*host\s*\.\s*storage\s*\.")]
    private static partial Regex HostStoragePattern();

    [GeneratedRegex(@"\bwindow\s*\.\s*host\s*\.\s*ai\s*\.")]
    private static partial Regex HostAiPattern();

    /// <summary>审查 HTML 内容，返回发现项列表（按严重级别排序）。</summary>
    public static IReadOnlyList<SecurityFinding> ScanHtml(string html)
    {
        var findings = new List<SecurityFinding>();

        AddIf(findings, SecuritySeverity.High, "DYNAMIC_CODE", "检测到 eval(——动态执行代码", EvalPattern(), html);
        AddIf(findings, SecuritySeverity.High, "DYNAMIC_CODE", "检测到 new Function(——动态执行代码", NewFunctionPattern(), html);
        AddIf(findings, SecuritySeverity.High, "DYNAMIC_CODE", "检测到动态 import(——运行时加载模块", DynamicImportPattern(), html);
        AddIf(findings, SecuritySeverity.Warning, "REMOTE_SCRIPT", "检测到远程 script 标签——引用了外部脚本，请确认来源可信", RemoteScriptPattern(), html);
        AddIf(findings, SecuritySeverity.Warning, "EXTERNAL_NETWORK", "检测到 fetch 外部地址——该工具会访问网络", RemoteFetchPattern(), html);
        AddIf(findings, SecuritySeverity.Warning, "EXTERNAL_NETWORK", "检测到 XMLHttpRequest——该工具会访问网络", XhrPattern(), html);
        AddIf(findings, SecuritySeverity.Warning, "EXTERNAL_NETWORK", "检测到 WebSocket(——该工具会建立外部长连接", WebSocketPattern(), html);
        AddIf(findings, SecuritySeverity.Warning, "EXTERNAL_NETWORK", "检测到 navigator.sendBeacon(——该工具会向外部上报数据", SendBeaconPattern(), html);
        AddIf(findings, SecuritySeverity.Warning, "REMOTE_IFRAME", "检测到远程 iframe——内容来自外部站点", RemoteIframePattern(), html);
        AddIf(findings, SecuritySeverity.Warning, "DOCUMENT_WRITE", "检测到 document.write(——建议改用 DOM API", DocumentWritePattern(), html);
        AddIf(findings, SecuritySeverity.Warning, "SRCDOC", "检测到 srcdoc 属性", SrcDocPattern(), html);

        AddIf(findings, SecuritySeverity.High, "HARDCODED_KEY", "检测到疑似 OpenAI 风格 API Key（sk-...）", OpenAiKeyPattern(), html);
        AddIf(findings, SecuritySeverity.High, "HARDCODED_KEY", "检测到疑似硬编码 apiKey/accessToken", ApiKeyAssignmentPattern(), html);
        AddIf(findings, SecuritySeverity.High, "HARDCODED_KEY", "检测到疑似 AWS Access Key（AKIA...）", AwsKeyPattern(), html);
        AddIf(findings, SecuritySeverity.High, "HARDCODED_KEY", "检测到疑似 Bearer 令牌", BearerPattern(), html);

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Code, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 从 HTML 中检测它实际用到哪些宿主能力，返回 manifest 应声明的权限集合。
    /// 用于「导入本地 HTML」：自动补齐权限，避免用户手动声明时遗漏或过度声明。
    /// </summary>
    public static IReadOnlyList<string> DetectPermissions(string html)
    {
        var permissions = new List<string>(4);

        if (HostStoragePattern().IsMatch(html)) { permissions.Add("storage"); }
        if (HostAiPattern().IsMatch(html)) { permissions.Add("ai"); }
        if (HostFilesPattern().IsMatch(html)) { permissions.Add("files"); }
        if (GetUserMediaPattern().IsMatch(html) || GetDisplayMediaPattern().IsMatch(html))
        {
            permissions.Add("media");
        }

        return permissions;
    }

    /// <summary>
    /// 检测 HTML 是否引用了工具目录外的相对资源（会被 CSP/静态服务拦截，导入时应提示）。
    /// </summary>
    public static IReadOnlyList<string> DetectExternalAssetReferences(string html)
    {
        var findings = new List<string>();
        var matches = RelativeAssetPattern().Matches(html);
        foreach (Match match in matches)
        {
            var value = match.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(value) ||
                value.StartsWith('#') ||
                value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (!findings.Contains(value, StringComparer.Ordinal))
            {
                findings.Add(value);
            }
        }

        return findings;
    }

    [GeneratedRegex(@"(?i)(?:src|href)\s*=\s*[""'](?<path>[^""']+)[""']")]
    private static partial Regex RelativeAssetPattern();

    /// <summary>审查 manifest：结构合法性 + 权限最小化。Fatal 级问题直接拒绝保存。</summary>
    public static IReadOnlyList<SecurityFinding> ScanManifest(ToolManifest manifest, string expectedDirectoryName)
    {
        var findings = new List<SecurityFinding>();

        var errors = ToolManifestValidator.Validate(manifest, expectedDirectoryName);
        foreach (var error in errors)
        {
            findings.Add(new SecurityFinding(SecuritySeverity.Fatal, "MANIFEST_INVALID", error));
        }

        // 权限最小化：ai/media/files 权限意味着额外能力，需显式提示
        foreach (var permission in manifest.Permissions ?? Array.Empty<string>())
        {
            if (string.Equals(permission, "ai", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new SecurityFinding(
                    SecuritySeverity.Warning,
                    "PERMISSION_AI",
                    "manifest 声明了 ai 权限：该工具可调用宿主 AI 接口（消耗你的配额）"));
            }

            if (string.Equals(permission, "media", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new SecurityFinding(
                    SecuritySeverity.Warning,
                    "PERMISSION_MEDIA",
                    "manifest 声明了 media 权限：该工具可使用麦克风 / 摄像头 / 屏幕捕获"));
            }

            if (string.Equals(permission, "files", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new SecurityFinding(
                    SecuritySeverity.Warning,
                    "PERMISSION_FILES",
                    "manifest 声明了 files 权限：该工具可在自己的目录内读写二进制文件"));
            }
        }

        if (manifest.AllowEval)
        {
            findings.Add(new SecurityFinding(
                SecuritySeverity.High,
                "ALLOW_EVAL",
                "manifest 启用了 allowEval：CSP 将放开 'unsafe-eval'，允许 eval/new Function"));
        }

        if (!string.IsNullOrWhiteSpace(manifest.Csp))
        {
            findings.Add(new SecurityFinding(
                SecuritySeverity.High,
                "CSP_OVERRIDE",
                "manifest 自定义了 CSP：可能削弱 script-src / connect-src 限制"));
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Code, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>综合审查：manifest + HTML + 权限使用一致性。返回 (findings, hasFatal)。</summary>
    public static (IReadOnlyList<SecurityFinding> Findings, bool HasFatal) Scan(ToolManifest manifest, string html)
    {
        var findings = new List<SecurityFinding>();
        findings.AddRange(ScanManifest(manifest, manifest.Id));
        findings.AddRange(ScanHtml(html));
        findings.AddRange(CheckPermissionUsage(manifest, html));

        var hasFatal = findings.Any(f => f.Severity == SecuritySeverity.Fatal);
        return (findings, hasFatal);
    }

    /// <summary>
    /// 核对「代码实际用到的能力」与「manifest 声明的权限」是否一致。
    /// 用了硬件捕获却没声明 media、用了 files 却没声明 files，都属于越权尝试，直接拒绝。
    /// </summary>
    public static IReadOnlyList<SecurityFinding> CheckPermissionUsage(ToolManifest manifest, string html)
    {
        var findings = new List<SecurityFinding>();
        var declared = new HashSet<string>(
            (manifest.Permissions ?? Array.Empty<string>()).Select(p => p.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);

        if ((GetUserMediaPattern().IsMatch(html) || GetDisplayMediaPattern().IsMatch(html)) &&
            !declared.Contains("media"))
        {
            findings.Add(new SecurityFinding(
                SecuritySeverity.Fatal,
                "PERMISSION_MISSING",
                "页面使用了 getUserMedia/getDisplayMedia，但 manifest 未声明 media 权限"));
        }

        if (HostFilesPattern().IsMatch(html) && !declared.Contains("files"))
        {
            findings.Add(new SecurityFinding(
                SecuritySeverity.Fatal,
                "PERMISSION_MISSING",
                "页面使用了 window.host.files，但 manifest 未声明 files 权限"));
        }

        if (HostStoragePattern().IsMatch(html) && !declared.Contains("storage"))
        {
            findings.Add(new SecurityFinding(
                SecuritySeverity.Fatal,
                "PERMISSION_MISSING",
                "页面使用了 window.host.storage，但 manifest 未声明 storage 权限"));
        }

        if (HostAiPattern().IsMatch(html) && !declared.Contains("ai"))
        {
            findings.Add(new SecurityFinding(
                SecuritySeverity.Fatal,
                "PERMISSION_MISSING",
                "页面使用了 window.host.ai，但 manifest 未声明 ai 权限"));
        }

        return findings;
    }

    private static void AddIf(
        List<SecurityFinding> findings,
        SecuritySeverity severity,
        string code,
        string message,
        Regex pattern,
        string html)
    {
        if (pattern.IsMatch(html))
        {
            findings.Add(new SecurityFinding(severity, code, message));
        }
    }
}
