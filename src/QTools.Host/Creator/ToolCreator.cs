using System.Text;
using System.Text.Json;
using QTools.Host.Ai;
using QTools.Host.Bridge;
using QTools.Host.Logging;
using QTools.Host.Tools;

namespace QTools.Host.Creator;

/// <summary>一次生成的结果（未经保存，供用户审查）。</summary>
public sealed record GeneratedToolResult(
    ToolManifest Manifest,
    string IndexHtml,
    IReadOnlyList<SecurityFinding> Findings,
    bool HasFatal,
    bool RequiresExplicitAcceptance,
    string? RawAiText = null);

/// <summary>
/// 工具创造器：自然语言 → AI 生成 manifest + index.html → 宿主安全审查 → 用户确认 → 保存。
/// </summary>
public sealed class ToolCreator
{
    private readonly Ai.AiClient _ai;
    private readonly ToolRegistry _registry;

    private const string SystemPrompt = """
        你是 QTools 桌面小工具宿主的工具生成器。用户会用自然语言描述需求，你必须只输出一个 JSON 对象，不要有任何解释文字或 Markdown 代码围栏。

        输出格式（严格遵守）：
        {
          "manifest": {
            "id": "小写字母/数字/短横线，1-63 字符，首尾不能是短横线",
            "name": "工具显示名",
            "version": "1.0.0",
            "description": "一句话描述",
            "entry": "index.html",
            "permissions": ["storage"/"ai"/"media"/"files" 的子集，按需最小化],
            "window": { "width": 520, "height": 720, "resizable": true },
            "createdAt": "ISO-8601 时间",
            "allowEval": false,
            "csp": null
          },
          "indexHtml": "完整的单文件 HTML（UTF-8，内联 CSS/JS）"
        }

        ===== 可用能力（window.host，由宿主注入，页面可直接使用）=====

        1) storage —— 需要 "storage" 权限。键值存储，按工具隔离。
           window.host.storage.get(key)        -> Promise<{ found, value } | null>
           window.host.storage.set(key, value) -> Promise<{ ok: true }>   value 为任意 JSON
           window.host.storage.remove(key)     -> Promise<{ ok: true }>
           window.host.storage.list()          -> Promise<{ keys: string[] }>
           key 只允许字母数字 . _ - ，最长 128；单个 value 不超过 256KB。

        2) ai —— 需要 "ai" 权限。宿主代理的 AI 对话，Key 由宿主保管，页面拿不到。
           window.host.ai.chat({ messages: [{role:"user"|"system"|"assistant", content:"..."}], temperature })
             -> Promise<{ content }>

        3) files —— 需要 "files" 权限。二进制文件存取，存放在 tools/{工具id}/files/ 下。
           window.host.files.save({ name, dataBase64, contentType? }) -> Promise<{ name, sizeBytes, url }>
           window.host.files.list()    -> Promise<{ files: [{ name, sizeBytes, modifiedUtc }] }>
           window.host.files.read({ name })    -> Promise<{ name, contentType, dataBase64 }>
           window.host.files.delete({ name })  -> Promise<{ deleted }>
           返回的 url 形如 /tools/{工具id}/files/xxx，可直接用于 <audio src> <img src> <a href>。
           允许的扩展名：webm wav mp3 ogg m4a aac flac opus mp4 mov avi mkv png jpg jpeg gif webp bmp svg pdf txt csv json zip。
           单个文件 50MB，单工具总计 200MB。文件名只能是字母数字 . _ - ，不能带路径。

        4) media —— 需要 "media" 权限。声明后宿主会为该工具开启麦克风/摄像头/屏幕捕获，
           页面可直接使用标准 Web API（无需再请求权限）：
           navigator.mediaDevices.getUserMedia({ audio: true })      // 麦克风
           navigator.mediaDevices.getUserMedia({ video: true })      // 摄像头
           navigator.mediaDevices.getDisplayMedia({ video: true })   // 屏幕
           配合 new MediaRecorder(stream) 录制，停止后得到 Blob。

        ===== 典型配方：录音工具 =====
        - manifest.permissions 必须包含 "media" 和 "files"。
        - getUserMedia({audio:true}) 拿流 → new MediaRecorder(stream, {mimeType:'audio/webm'}) 录制；
          收集 chunk，停止时组成 Blob；
        - 用 FileReader.readAsDataURL 得到 base64（去掉 "data:...;base64," 前缀），调 window.host.files.save；
        - 用返回的 url 作为 <audio controls src> 播放；files.list() 展示历史录音。
        - DataURL 体积比原文件大约 33%，录音较长要提示用户；可先用 URL.createObjectURL 本地试听，
          仅在用户点击「保存」时才转 base64 上传。

        ===== 硬性规则（违反任何一条都算失败）=====
        1. permissions 只能从 ["storage","ai","media","files"] 中选择，只声明真正用到的。
           用到 getUserMedia/getDisplayMedia 就必须有 media；用到 window.host.files 就必须有 files。
        2. indexHtml 中禁止出现 eval(、new Function、动态 import(。
        3. indexHtml 中禁止硬编码任何 API Key 或令牌。
        4. 本地键值存储用 window.host.storage；二进制文件用 window.host.files；
          需要 AI 能力时优先用 window.host.ai.chat（Key 由宿主保管，页面拿不到）；硬件捕获用 media 权限 + 标准 Web API。
        5. window.host 由宿主注入，页面可直接使用；不要自己定义或引用其他桥接对象。
        6. 单文件 HTML（全部 CSS/JS 内联，目的：支持宿主的导入/导出与备份）、无构建步骤；
           允许引用 CDN 或外部资源，允许 fetch/XHR/WebSocket 访问外部接口，请求失败时要有降级提示。
        """;

    public ToolCreator(Ai.AiClient ai, ToolRegistry registry)
    {
        _ai = ai;
        _registry = registry;
    }

    /// <summary>
    /// AI 更新已有工具：把当前版本源码 + 用户的修改诉求交给 AI，返回新版本内容（不保存）。
    /// </summary>
    public async System.Threading.Tasks.Task<GeneratedToolResult> UpdateAsync(
        LoadedTool tool,
        string instruction,
        VersionBump bump,
        System.Threading.CancellationToken cancellationToken,
        Action<string, bool>? onRawDelta = null)
    {
        if (string.IsNullOrWhiteSpace(instruction))
        {
            throw new BridgeException("AI_BAD_REQUEST", "修改诉求不能为空");
        }

        if (instruction.Length > 4000)
        {
            throw new BridgeException("AI_BAD_REQUEST", "修改诉求不超过 4000 字符");
        }

        var currentHtml = await File.ReadAllTextAsync(tool.EntryFullPath, cancellationToken).ConfigureAwait(false);
        if (currentHtml.Length > 512 * 1024)
        {
            throw new BridgeException("AI_BAD_REQUEST", "当前版本源码过大（>512KB），无法交给 AI 更新");
        }

        var nextVersion = ToolVersionStore.NextVersion(tool.Versions, bump);

        var userMessage = $"""
            下面是一个已存在工具的当前版本，请按我的要求改造成新版本。

            【工具标识（必须原样保留，不得更改）】
            id = {tool.Id}
            当前版本 = {tool.ActiveVersion}
            新版本号 = {nextVersion}（manifest.version 必须填这个）

            【当前 manifest.json】
            {JsonSerializer.Serialize(tool.Manifest, QTools.Host.Serialization.AppJsonContext.Default.ToolManifest)}

            【当前 index.html】
            {currentHtml}

            【我的修改要求】
            {instruction.Trim()}

            请输出完整的 JSON 对象（格式同系统提示）：manifest 用上面这份、只把 version 改成「新版本号」，
            indexHtml 输出改造后的完整单文件 HTML。其余字段（id/name/entry/window/createdAt/allowEval/csp）保持不变。
            permissions 按改造后代码的实际用法重新确定，用不到的要去掉，新用到的要补上。
            """;

        var messages = new[]
        {
            ("system", SystemPrompt),
            ("user", userMessage),
        };

        var raw = onRawDelta is null
            ? await _ai.ChatAsync(messages, temperature: null, cancellationToken).ConfigureAwait(false)
            : await _ai.ChatStreamingAsync(messages, temperature: null, onRawDelta, cancellationToken).ConfigureAwait(false);
        Log.Info($"AI 更新生成完成（{tool.Id} → v{nextVersion}），返回 {raw.Length} 字符，开始解析与安全审查");

        var generated = AiOutputParser.Parse(raw);
        var manifest = DeserializeManifest(generated.Manifest.GetRawText());

        // 身份字段必须保持不变：防止 AI 把工具改成另一个东西
        manifest = manifest with { Id = tool.Id, Entry = "index.html", Name = tool.Name };
        if (!string.Equals(manifest.Version, nextVersion, StringComparison.Ordinal))
        {
            manifest = manifest with { Version = nextVersion };
        }

        var (findings, hasFatal) = ContentSecurityScanner.Scan(manifest, generated.IndexHtml);
        var requiresAcceptance = findings.Any(f => f.Severity == SecuritySeverity.High);

        Log.Info($"安全审查完成：{findings.Count} 项发现（致命={hasFatal}，需显式确认={requiresAcceptance}）");

        return new GeneratedToolResult(
            manifest,
            generated.IndexHtml,
            findings,
            hasFatal,
            requiresAcceptance,
            raw);
    }

    /// <summary>
    /// 把审查通过的更新结果存成新版本并激活。旧版本快照已存在，可随时回滚。
    /// </summary>
    public async System.Threading.Tasks.Task<LoadedTool> ApplyUpdateAsync(
        LoadedTool tool,
        GeneratedToolResult result,
        bool acceptHighSeverity,
        System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (result.HasFatal)
        {
            var fatal = result.Findings.First(f => f.Severity == SecuritySeverity.Fatal);
            throw new BridgeException("MANIFEST_INVALID", $"生成内容存在致命问题，已拒绝保存：{fatal.Message}");
        }

        if (result.RequiresExplicitAcceptance && !acceptHighSeverity)
        {
            var high = result.Findings.First(f => f.Severity == SecuritySeverity.High);
            throw new BridgeException(
                "CONTENT_SECURITY_VIOLATION",
                $"生成内容包含高危项（{high.Code}: {high.Message}），需显式确认后才能保存");
        }

        var version = result.Manifest.Version;
        var snapshot = ToolVersionStore.VersionDirectory(tool.Directory, version);
        if (Directory.Exists(snapshot))
        {
            throw new BridgeException("VERSION_EXISTS", $"版本 {version} 已存在，请改换递增粒度");
        }

        // 1) 先固化当前版本快照（Activate 内部会做，这里显式一次以保证顺序清晰）
        _registry.VersionStore.Snapshot(tool.Directory, tool.ActiveVersion, note: "更新前自动备份");

        // 2) 写入新版本快照
        Directory.CreateDirectory(snapshot);
        await File.WriteAllTextAsync(
            Path.Combine(snapshot, "index.html"), result.IndexHtml, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(snapshot, "manifest.json"),
                JsonSerializer.Serialize(result.Manifest, QTools.Host.Serialization.AppJsonContext.Default.ToolManifest),
                new UTF8Encoding(false),
                cancellationToken)
            .ConfigureAwait(false);

        // 3) 激活新版本（覆盖根目录）
        var reloaded = _registry.SwitchVersion(tool.Id, version);

        Log.Info($"已应用工具 {tool.Id} 的更新：v{tool.ActiveVersion} → v{version}");
        return reloaded;
    }

    /// <summary>
    /// 创建链路自动附加到用户需求前面的宿主规范：
    /// 用户只需描述功能，不必自己记得「单文件 HTML / 中文界面 / 权限最小化」等要求。
    /// </summary>
    private const string GenerateRequirements = """
        【生成要求（QTools 宿主规范，必须全部满足）】
        1. 成品必须是单个 index.html 文件，全部 CSS / JS 内联——目的在于支持宿主的导入 / 导出与备份，不限制联网。
        2. 允许引用 CDN 或外部资源（脚本、样式、字体、图片），允许 fetch / XMLHttpRequest / WebSocket 访问外部接口；请求失败时要有降级提示，不要让页面卡死或白屏。
        3. 本地数据持久化用 window.host.storage，二进制文件用 window.host.files，需要 AI 能力时优先用 window.host.ai（Key 由宿主保管，页面拿不到）；禁止在代码里硬编码任何 API Key 或令牌。
        4. 界面文案用简体中文，图标用内联 SVG 或字符；禁止 eval / new Function / 动态 import。
        5. 布局自适应 manifest.window 声明的尺寸并随窗口缩放；window.width/height 必须是 320~3840 的整数（常用 520×720、resizable=true）。
        6. permissions 按实际用量最小化声明；allowEval 固定 false、csp 固定 null。
        7. 交互完整：输入校验、空态、成功/失败反馈都要有，错误不静默。

        【我的需求】
        """;

    /// <summary>根据自然语言需求生成工具（不保存）。</summary>
    public async System.Threading.Tasks.Task<GeneratedToolResult> GenerateAsync(
        string prompt,
        System.Threading.CancellationToken cancellationToken,
        Action<string, bool>? onRawDelta = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new BridgeException("AI_BAD_REQUEST", "需求描述不能为空");
        }

        if (prompt.Length > 4000)
        {
            throw new BridgeException("AI_BAD_REQUEST", "需求描述不超过 4000 字符");
        }

        var messages = new[]
        {
            ("system", SystemPrompt),
            ("user", $"{GenerateRequirements}\n{prompt.Trim()}"),
        };

        var raw = onRawDelta is null
            ? await _ai.ChatAsync(messages, temperature: null, cancellationToken).ConfigureAwait(false)
            : await _ai.ChatStreamingAsync(messages, temperature: null, onRawDelta, cancellationToken).ConfigureAwait(false);
        Log.Info($"AI 生成完成，返回 {raw.Length} 字符，开始解析与安全审查");

        var generated = AiOutputParser.Parse(raw);
        var manifest = DeserializeManifest(generated.Manifest.GetRawText());

        var (findings, hasFatal) = ContentSecurityScanner.Scan(manifest, generated.IndexHtml);
        var requiresAcceptance = findings.Any(f => f.Severity == SecuritySeverity.High);

        Log.Info($"安全审查完成：{findings.Count} 项发现（致命={hasFatal}，需显式确认={requiresAcceptance}）");

        return new GeneratedToolResult(
            manifest,
            generated.IndexHtml,
            findings,
            hasFatal,
            requiresAcceptance,
            raw);
    }

    /// <summary>
    /// 把审查通过的生成结果保存为工具。高危发现需 <paramref name="acceptHighSeverity"/> 为 true。
    /// </summary>
    public async System.Threading.Tasks.Task<string> CreateAsync(
        GeneratedToolResult result,
        bool acceptHighSeverity,
        System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (result.HasFatal)
        {
            var fatal = result.Findings.First(f => f.Severity == SecuritySeverity.Fatal);
            throw new BridgeException("MANIFEST_INVALID", $"生成内容存在致命问题，已拒绝保存：{fatal.Message}");
        }

        if (result.RequiresExplicitAcceptance && !acceptHighSeverity)
        {
            var high = result.Findings.First(f => f.Severity == SecuritySeverity.High);
            throw new BridgeException(
                "CONTENT_SECURITY_VIOLATION",
                $"生成内容包含高危项（{high.Code}: {high.Message}），需显式确认后才能保存");
        }

        var toolId = result.Manifest.Id;

        if (_registry.Find(toolId) is not null)
        {
            throw new BridgeException("TOOL_ALREADY_EXISTS", $"工具 ID 已存在：{toolId}");
        }

        var toolDirectory = Path.Combine(QTools.Host.Configuration.AppPaths.ToolsDirectory, toolId);
        if (Directory.Exists(toolDirectory) && File.Exists(Path.Combine(toolDirectory, "manifest.json")))
        {
            throw new BridgeException("TOOL_ALREADY_EXISTS", $"工具目录已存在：{toolId}");
        }

        // 覆盖 createdAt 为保存时间（AI 给的时间不可信）
        var manifest = result.Manifest with { CreatedAt = DateTimeOffset.Now.ToString("o") };

        Directory.CreateDirectory(toolDirectory);

        var manifestPath = Path.Combine(toolDirectory, "manifest.json");
        var manifestJson = JsonSerializer.Serialize(manifest, QTools.Host.Serialization.AppJsonContext.Default.ToolManifest);
        WriteAtomically(manifestPath, manifestJson);

        var entryRelative = string.IsNullOrWhiteSpace(manifest.Entry) ? "index.html" : manifest.Entry;
        var entryPath = Path.Combine(toolDirectory, entryRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        WriteAtomically(entryPath, result.IndexHtml);

        Log.Info($"已保存新工具 {toolId} 到 {toolDirectory}");

        // 重新扫描并登记，便于立即打开
        var loaded = _registry.Rescan().FirstOrDefault(t => string.Equals(t.Id, toolId, StringComparison.Ordinal));
        if (loaded is null)
        {
            throw new BridgeException("MANIFEST_INVALID", "工具已写入磁盘但重新扫描失败，请手动检查");
        }

        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
        return toolId;
    }

    /// <summary>
    /// 修复工具：把错误信息与现有源码回传给 AI，生成完整修复版。
    /// </summary>
    public async System.Threading.Tasks.Task<GeneratedToolResult> FixAsync(
        LoadedTool tool,
        string errorDescription,
        System.Threading.CancellationToken cancellationToken,
        Action<string, bool>? onRawDelta = null)
    {
        if (string.IsNullOrWhiteSpace(errorDescription))
        {
            throw new BridgeException("AI_BAD_REQUEST", "错误描述不能为空");
        }

        var entryPath = tool.EntryFullPath;
        if (!File.Exists(entryPath))
        {
            throw new BridgeException("ENTRY_FILE_MISSING", $"工具入口文件不存在：{tool.Entry}");
        }

        var existingHtml = await File.ReadAllTextAsync(entryPath, cancellationToken).ConfigureAwait(false);
        var manifestJson = await File.ReadAllTextAsync(
            Path.Combine(tool.Directory, "manifest.json"), cancellationToken).ConfigureAwait(false);

        var prompt = $"""
            请修复以下 QTools 工具。用户报告的问题：
            ---
            {errorDescription.Trim()}
            ---

            当前 manifest.json：
            ---
            {manifestJson}
            ---

            当前 {tool.Entry}：
            ---
            {existingHtml}
            ---

            请输出修复后的完整 JSON（manifest + indexHtml），规则与之前一致。
            """;

        var messages = new[]
        {
            ("system", SystemPrompt),
            ("user", prompt),
        };

        var raw = onRawDelta is null
            ? await _ai.ChatAsync(messages, temperature: null, cancellationToken).ConfigureAwait(false)
            : await _ai.ChatStreamingAsync(messages, temperature: null, onRawDelta, cancellationToken).ConfigureAwait(false);
        Log.Info($"AI 修复完成，返回 {raw.Length} 字符，开始解析与安全审查");

        var generated = AiOutputParser.Parse(raw);
        var manifest = DeserializeManifest(generated.Manifest.GetRawText());

        // 修复不得改变工具 ID
        if (!string.Equals(manifest.Id, tool.Id, StringComparison.Ordinal))
        {
            throw new BridgeException(
                "MANIFEST_INVALID",
                $"修复结果的工具 ID 必须是 {tool.Id}（收到 {manifest.Id}）");
        }

        var (findings, hasFatal) = ContentSecurityScanner.Scan(manifest, generated.IndexHtml);
        var requiresAcceptance = findings.Any(f => f.Severity == SecuritySeverity.High);

        return new GeneratedToolResult(manifest, generated.IndexHtml, findings, hasFatal, requiresAcceptance, raw);
    }

    /// <summary>用修复结果覆盖现有工具（高危需显式确认）。</summary>
    public async System.Threading.Tasks.Task OverwriteAsync(
        LoadedTool tool,
        GeneratedToolResult result,
        bool acceptHighSeverity,
        System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (result.HasFatal)
        {
            var fatal = result.Findings.First(f => f.Severity == SecuritySeverity.Fatal);
            throw new BridgeException("MANIFEST_INVALID", $"修复内容存在致命问题，已拒绝保存：{fatal.Message}");
        }

        if (result.RequiresExplicitAcceptance && !acceptHighSeverity)
        {
            var high = result.Findings.First(f => f.Severity == SecuritySeverity.High);
            throw new BridgeException(
                "CONTENT_SECURITY_VIOLATION",
                $"修复内容包含高危项（{high.Code}: {high.Message}），需显式确认后才能保存");
        }

        var manifestPath = Path.Combine(tool.Directory, "manifest.json");
        var manifestJson = JsonSerializer.Serialize(result.Manifest, QTools.Host.Serialization.AppJsonContext.Default.ToolManifest);
        WriteAtomically(manifestPath, manifestJson);

        var entryRelative = string.IsNullOrWhiteSpace(result.Manifest.Entry) ? "index.html" : result.Manifest.Entry;
        var entryPath = Path.Combine(tool.Directory, entryRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        WriteAtomically(entryPath, result.IndexHtml);

        Log.Info($"已用修复版本覆盖工具 {tool.Id}");
        _registry.Rescan();

        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
    }

    private static ToolManifest DeserializeManifest(string manifestJson)
    {
        ToolManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                manifestJson,
                QTools.Host.Serialization.AppJsonContext.Default.ToolManifest);
        }
        catch (JsonException ex)
        {
            throw new BridgeException("MANIFEST_INVALID", $"生成的 manifest 不是合法 JSON：{ex.Message}");
        }

        if (manifest is null)
        {
            throw new BridgeException("MANIFEST_INVALID", "生成的 manifest 为空");
        }

        return manifest;
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
