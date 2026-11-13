using System.Text.Json;
using System.Text.Json.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;

namespace QTools.Host.Bridge;

/// <summary>桥接错误：携带给页面的错误码与可读信息（绝不泄露内部堆栈/路径/Key）。</summary>
public sealed record BridgeError(string Code, string Message);

/// <summary>
/// 桥接异常：由方法处理器抛出，被 <see cref="BridgeDispatcher"/> 转成 { id, error } 响应。
/// </summary>
public sealed class BridgeException : Exception
{
    public BridgeException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// 桥接请求信封（页面 → 宿主）。
/// <para>
/// toolId 仅作一致性记录；身份以宿主侧窗口绑定为准。token 是主窗口内嵌工具时的
/// 「内嵌凭证」：<see cref="BridgeDispatcher"/> 校验通过后将以凭证映射的 toolId 作为权威身份
/// （见 Shell.EmbedTokenRegistry）。
/// </para>
/// </summary>
public sealed record BridgeRequest(
    string Id, string ToolId, string Method, JsonElement? Payload, string? Token = null);

/// <summary>方法处理器的返回：结果 JSON（由分发器包进 { id, result }）。</summary>
public delegate System.Threading.Tasks.Task<JsonElement> BridgeMethodHandler(
    BridgeContext context);

/// <summary>一次桥接调用的上下文（toolId 为宿主侧权威值）。</summary>
public sealed class BridgeContext
{
    public required string ToolId { get; init; }

    public required string Method { get; init; }

    public required string RequestId { get; init; }

    public required JsonElement? Payload { get; init; }

    /// <summary>实际发送请求的窗口 Id（window.* 控制方法据此定位窗口；内嵌 iframe 的发送者是内容窗口）。</summary>
    public Guid? WindowId { get; init; }

    public required LoadedTool? Tool { get; init; }

    /// <summary>存储服务（storage.* 用）。</summary>
    public Storage.ToolStorage? Storage { get; init; }

    /// <summary>工具注册表（manager.* 用）。</summary>
    public Tools.ToolRegistry? Registry { get; init; }

    /// <summary>窗口壳（manager.openTool / deleteTool 用）。</summary>
    public Shell.ShellHost? Shell { get; init; }

    /// <summary>服务器信息（manager.getServerInfo 用）。</summary>
    public ServerInfoView? ServerInfo { get; init; }

    /// <summary>AI 客户端（ai.chat 用；未注入时返回 AI_NOT_CONFIGURED）。</summary>
    public Ai.AiClient? Ai { get; init; }

    /// <summary>敏感信息存储（manager.setAiConfig 用）。</summary>
    public Security.ISecretStore? Secrets { get; init; }

    /// <summary>工具创造器（manager.generateTool / createTool / fixTool 用）。</summary>
    public Creator.ToolCreator? Creator { get; init; }

    /// <summary>工具文件存储（files.* 用；按 toolId 惰性创建）。</summary>
    public Func<Tools.ToolFiles>? FilesFactory { get; init; }

    /// <summary>本地 HTML 导入器（manager.importTool 用）。</summary>
    public Creator.ToolImporter? Importer { get; init; }

    /// <summary>本机程序启动项注册表（manager.listApps / launchApp 用）。</summary>
    public Launcher.AppRegistry? Apps { get; init; }

    /// <summary>本机程序启动器（manager.launchApp 用）。</summary>
    public Launcher.AppLauncher? Launcher { get; init; }

    /// <summary>启动项存储（manager.addApp / deleteApp 用）。</summary>
    public Launcher.AppStore? AppStore { get; init; }

    /// <summary>在线工具注册表（manager.openWebTool / launcherList 用）。</summary>
    public Launcher.WebToolRegistry? WebTools { get; init; }

    /// <summary>在线工具存储（manager.addWebTool / deleteWebTool 用）。</summary>
    public Launcher.WebToolStore? WebToolStore { get; init; }

    /// <summary>本机程序扫描器（manager.discoverPrograms 用）。</summary>
    public Launcher.ProgramDiscovery? Discovery { get; init; }

    /// <summary>本机程序自动索引（manager.launcherList / reindexPrograms / launchApp 回退用）。</summary>
    public Launcher.ProgramIndex? ProgramIndex { get; init; }

    /// <summary>索引程序隐藏列表（manager.hideProgram / unhideProgram 用）。</summary>
    public Launcher.HiddenProgramStore? HiddenPrograms { get; init; }

    /// <summary>使用频次统计（列表排序 + 启动/打开计数用）。</summary>
    public Launcher.UsageStore? Usage { get; init; }

    /// <summary>内嵌凭证注册表（manager.embedTool 签发用）。</summary>
    public Shell.EmbedTokenRegistry? EmbedTokens { get; init; }

    /// <summary>本地备份服务（manager.backup* 用）。</summary>
    public Backup.BackupService? Backup { get; init; }

    /// <summary>Git 远程备份（manager.gitPush / gitRestore 用）。</summary>
    public Backup.GitBackup? GitBackup { get; init; }

    /// <summary>本次请求的取消令牌（manager.cancelRequest 触发；长任务必须透传）。</summary>
    public System.Threading.CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// 流式增量推送：把一段文本作为 {"id","stream":{"delta","reasoning"}} 信封发给页面；
    /// reasoning=true 表示模型的思考过程，页面需要与正文分区展示。
    /// 无推送通道（无头/自检）时为 null，调用方应回退到非流式路径。
    /// </summary>
    public Action<string, bool>? PushDelta { get; init; }

    /// <summary>只要正文的推送通道（Git 等非 AI 流复用，思考标记固定 false）。</summary>
    public Action<string>? PushOutput => PushDelta is null ? null : text => PushDelta(text, false);

    /// <summary>按请求 id 取消一个正在执行的桥接请求（manager.cancelRequest 用）。</summary>
    public Func<string, bool>? CancelRequest { get; init; }
}

/// <summary>流式增量信封：与最终 { id, result } 共用同一请求 id，页面按 stream 字段分流。</summary>
public sealed record BridgeStreamEnvelope(string Id, BridgeStreamBody Stream);

/// <summary>宿主主动事件信封（如索引完成 launcher-changed）；页面按 event 字段分流为 host-event。</summary>
public sealed record BridgeEventEnvelope(string Id, string Event);

/// <summary>流式增量载荷；Reasoning=true 表示思考过程，需与正文分开显示。</summary>
public sealed record BridgeStreamBody(string Delta, bool Reasoning = false);

/// <summary>桥接响应信封（错误形态；成功形态由分发器用 Utf8JsonWriter 直写以承载任意结果）。</summary>
public sealed record BridgeErrorResponse(string Id, BridgeError Error);

/// <summary>/health 与 manager.getServerInfo 的载荷。</summary>
public sealed record ServerInfoView(
    string Version,
    int Port,
    string Platform,
    string WebView,
    string? WebViewRuntimeVersion,
    string? NativeVersion)
{
    /// <summary>数据根目录（设置页展示）。</summary>
    public string? DataDirectory { get; init; }
}
