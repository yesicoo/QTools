using System.Text.Json;
using System.Text.Json.Serialization;
using QTools.Host.Bridge;
using QTools.Host.Storage;
using QTools.Host.Tools;

namespace QTools.Host.Serialization;

/// <summary>
/// 全宿主唯一的 JSON 源生成上下文。所有序列化/反序列化必须经过这里——
/// Native AOT 下反射式 System.Text.Json 不可用，且已在 csproj 中禁用反射序列化特性开关。
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(BridgeError))]
[JsonSerializable(typeof(BridgeErrorResponse))]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(BridgeStreamEnvelope))]
[JsonSerializable(typeof(BridgeStreamBody))]
[JsonSerializable(typeof(BridgeEventEnvelope))]
[JsonSerializable(typeof(ToolManifest))]
[JsonSerializable(typeof(ToolWindowSettings))]
[JsonSerializable(typeof(StorageFile))]
[JsonSerializable(typeof(OkResult))]
[JsonSerializable(typeof(StorageGetResult))]
[JsonSerializable(typeof(StorageListResult))]
[JsonSerializable(typeof(ToolListItem))]
[JsonSerializable(typeof(ToolListResult))]
[JsonSerializable(typeof(WindowStateView))]
[JsonSerializable(typeof(ToolShortcutResult))]
[JsonSerializable(typeof(AiConfigView))]
[JsonSerializable(typeof(ServerInfoView))]
[JsonSerializable(typeof(Configuration.AppConfig))]
[JsonSerializable(typeof(Configuration.AiConfigSection))]
[JsonSerializable(typeof(Configuration.ServerConfigSection))]
[JsonSerializable(typeof(Configuration.StorageConfigSection))]
[JsonSerializable(typeof(Configuration.BackupConfigSection))]
[JsonSerializable(typeof(Configuration.ShellConfigSection))]
[JsonSerializable(typeof(Configuration.ToolWindowState))]
[JsonSerializable(typeof(FilesSavePayload))]
[JsonSerializable(typeof(ImportToolPayload))]
[JsonSerializable(typeof(ImportToolView))]
[JsonSerializable(typeof(ToolVersionView))]
[JsonSerializable(typeof(ToolVersionListView))]
[JsonSerializable(typeof(Launcher.AppEntry))]
[JsonSerializable(typeof(Launcher.AddAppPayload))]
[JsonSerializable(typeof(Launcher.WebToolEntry))]
[JsonSerializable(typeof(Launcher.AddWebToolPayload))]
[JsonSerializable(typeof(Launcher.HiddenProgramFile))]
[JsonSerializable(typeof(Launcher.AppListItemView))]
[JsonSerializable(typeof(Launcher.AppListView))]
[JsonSerializable(typeof(Launcher.DiscoveredProgramView))]
[JsonSerializable(typeof(Launcher.DiscoveredProgramListView))]
[JsonSerializable(typeof(Launcher.LaunchResultView))]
[JsonSerializable(typeof(Launcher.PickedFileView))]
[JsonSerializable(typeof(Launcher.IndexEntry))]
[JsonSerializable(typeof(Launcher.ProgramIndexFile))]
[JsonSerializable(typeof(Launcher.UsageEntry))]
[JsonSerializable(typeof(Launcher.UsageFile))]
[JsonSerializable(typeof(Launcher.LauncherItemView))]
[JsonSerializable(typeof(Launcher.LauncherIndexStatus))]
[JsonSerializable(typeof(Launcher.LauncherListView))]
[JsonSerializable(typeof(Shell.TrayEntry))]
[JsonSerializable(typeof(Shell.TrayMenuListView))]
[JsonSerializable(typeof(FilesSaveResult))]
[JsonSerializable(typeof(FilesListResult))]
[JsonSerializable(typeof(FilesReadResult))]
[JsonSerializable(typeof(FilesDeleteResult))]
[JsonSerializable(typeof(Backup.BackupManifest))]
[JsonSerializable(typeof(Backup.BackupEntryView))]
[JsonSerializable(typeof(Backup.BackupCreateResult))]
[JsonSerializable(typeof(Backup.BackupRestoreResult))]
[JsonSerializable(typeof(Backup.BackupListView))]
[JsonSerializable(typeof(Backup.BackupConfigView))]
[JsonSerializable(typeof(Backup.ImportBackupResult))]
[JsonSerializable(typeof(Backup.GitPushResult))]
[JsonSerializable(typeof(Ai.AiChatRequest))]
[JsonSerializable(typeof(Ai.AiMessage))]
[JsonSerializable(typeof(Ai.AiChatResponse))]
[JsonSerializable(typeof(Ai.AiChoice))]
[JsonSerializable(typeof(Ai.AiResponseMessage))]
[JsonSerializable(typeof(Ai.AiStreamChunk))]
[JsonSerializable(typeof(Ai.AiStreamChoice))]
[JsonSerializable(typeof(Ai.AiStreamDelta))]
[JsonSerializable(typeof(Ai.AnthropicChatRequest))]
[JsonSerializable(typeof(Ai.AnthropicMessage))]
[JsonSerializable(typeof(Ai.AnthropicChatResponse))]
[JsonSerializable(typeof(Ai.AnthropicContentBlock))]
[JsonSerializable(typeof(Ai.AnthropicStreamEvent))]
[JsonSerializable(typeof(Ai.AnthropicStreamDelta))]
[JsonSerializable(typeof(AiChatResult))]
[JsonSerializable(typeof(Ai.GeneratedTool))]
[JsonSerializable(typeof(Creator.GeneratedToolResult))]
[JsonSerializable(typeof(Creator.SecurityFinding))]
[JsonSerializable(typeof(GeneratedToolView))]
[JsonSerializable(typeof(CreateToolResult))]
[JsonSerializable(typeof(EmbedToolView))]
internal partial class AppJsonContext : JsonSerializerContext;

/// <summary>/health 响应。</summary>
internal sealed record HealthResponse(string Status, string Version, int Port);

/// <summary>通用 { ok: true } 结果。</summary>
internal sealed record OkResult(bool Ok);

/// <summary>storage.get 结果：found 区分「键不存在」与「值为 JSON null」。</summary>
internal sealed record StorageGetResult(bool Found, JsonElement Value);

/// <summary>storage.list 结果。</summary>
internal sealed record StorageListResult(IReadOnlyList<string> Keys);

/// <summary>工具列表项（manager.listTools）。SearchKey 含拼音首字母与全拼，供前端子串匹配。</summary>
internal sealed record ToolListItem(
    string Id,
    string Name,
    string Version,
    string? Description,
    IReadOnlyList<string> Permissions,
    string LastModifiedUtc,
    bool IsValid,
    IReadOnlyList<string> Errors,
    bool HasStorageData,
    string SearchKey = "");

/// <summary>工具列表结果。</summary>
internal sealed record ToolListResult(IReadOnlyList<ToolListItem> Tools);

/// <summary>窗口控制状态（window.* / manager.*Window*）：置顶 + 钉住；工具窗口另带「⋯」按钮位置。</summary>
internal sealed record WindowStateView(
    bool Topmost,
    bool Pinned,
    double? ChipLeft = null,
    double? ChipTop = null);

/// <summary>桌面快捷方式创建结果（manager.createToolShortcut）。</summary>
internal sealed record ToolShortcutResult(string Path, string FileName);

/// <summary>AI 配置视图——绝不包含 API Key。</summary>
internal sealed record AiConfigView(
    bool Configured,
    string? BaseUrl,
    string? Model,
    double Temperature,
    int TimeoutSeconds)
{
    /// <summary>密钥存储模式名（如 "Windows DPAPI（当前用户）"），不含任何密钥内容。</summary>
    public string? SecretStoreMode { get; init; }
}

/// <summary>ai.chat 结果。</summary>
internal sealed record AiChatResult(string Content);

/// <summary>生成结果视图（给宿主 UI；不含原始 AI 文本，避免泄露 system prompt）。</summary>
internal sealed record GeneratedToolView(
    ToolManifest Manifest,
    string IndexHtml,
    IReadOnlyList<Creator.SecurityFinding> Findings,
    bool HasFatal,
    bool RequiresExplicitAcceptance);

/// <summary>createTool / fixTool 结果。</summary>
internal sealed record CreateToolResult(string ToolId, bool Opened);

/// <summary>
/// manager.embedTool 结果：带内嵌凭证的工具入口 URL。
/// credentials 在 URL 查询段（bt=），绝不经日志/其他响应外泄。
/// </summary>
internal sealed record EmbedToolView(
    string ToolId,
    string Name,
    string Url,
    IReadOnlyList<string> Permissions);
