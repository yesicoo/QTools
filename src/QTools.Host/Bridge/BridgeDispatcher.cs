using System.Text;
using System.Text.Json;
using QTools.Host.Logging;
using QTools.Host.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;

namespace QTools.Host.Bridge;

/// <summary>
/// 桥接分发器：实现设计文档 §5.4 的七步校验与 §5.5 的方法-权限矩阵。
/// <para>
/// 关键安全属性：toolId 以宿主侧窗口绑定为准（<see cref="WindowRegistry"/>），
/// 客户端消息里的 toolId 仅作一致性记录——恶意工具无法伪称别的 toolId 越权。
/// </para>
/// </summary>
public sealed class BridgeDispatcher
{
    /// <summary>宿主管理界面的特权 toolId（拥有 manager.* 方法）。</summary>
    public const string HostToolId = "__host__";

    private const long MaxMessageBytes = 1024 * 1024;

    private readonly ToolRegistry _tools;
    private readonly ToolStorage _storage;
    private readonly Ai.AiClient? _ai;
    private readonly Security.ISecretStore? _secrets;
    private readonly Creator.ToolCreator? _creator;
    private readonly Dictionary<string, MethodEntry> _methods = new(StringComparer.Ordinal);

    /// <summary>toolId → 文件存储实例（惰性创建，随工具目录生命周期）。</summary>
    private readonly Dictionary<string, Tools.ToolFiles> _filesByTool = new(StringComparer.Ordinal);
    private readonly object _filesGate = new();

    /// <summary>本地 HTML 导入器（manager.importTool 用）。</summary>
    private readonly Creator.ToolImporter? _importer;
    private readonly Launcher.AppRegistry? _apps;
    private readonly Launcher.AppLauncher? _launcher;
    private readonly Launcher.AppStore? _appStore;
    private readonly Launcher.ProgramDiscovery? _discovery;
    private readonly Launcher.ProgramIndex? _programIndex;
    private readonly Launcher.UsageStore? _usage;
    private readonly Backup.BackupService? _backup;
    private readonly Backup.GitBackup? _gitBackup;

    /// <summary>正在执行的请求 → 取消令牌源（manager.cancelRequest 按 id 取消）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.CancellationTokenSource> _pendingRequests =
        new(StringComparer.Ordinal);

    /// <summary>窗口壳（manager.openTool / deleteTool 用；由 ShellHost 启动时注入）。</summary>
    public Shell.ShellHost? Shell { get; set; }

    /// <summary>内嵌凭证注册表（主窗口 iframe 内嵌工具的身份机制）。</summary>
    public Shell.EmbedTokenRegistry EmbedTokens { get; } = new();

    /// <summary>服务器信息（manager.getServerInfo 用；由 ShellHost 启动时注入）。</summary>
    public ServerInfoView? ServerInfo { get; set; }

    public BridgeDispatcher(
        ToolRegistry tools,
        ToolStorage storage,
        Ai.AiClient? ai = null,
        Security.ISecretStore? secrets = null,
        Creator.ToolCreator? creator = null,
        Creator.ToolImporter? importer = null,
        Launcher.AppRegistry? apps = null,
        Launcher.AppLauncher? launcher = null,
        Launcher.AppStore? appStore = null,
        Launcher.ProgramDiscovery? discovery = null,
        Backup.BackupService? backup = null,
        Backup.GitBackup? gitBackup = null,
        Launcher.ProgramIndex? programIndex = null,
        Launcher.UsageStore? usage = null)
    {
        _tools = tools;
        _storage = storage;
        _ai = ai;
        _secrets = secrets;
        _creator = creator;
        _importer = importer;
        _apps = apps;
        _launcher = launcher;
        _appStore = appStore;
        _discovery = discovery;
        _backup = backup;
        _gitBackup = gitBackup;
        _programIndex = programIndex;
        _usage = usage;
        RegisterMethods();
    }

    private Tools.ToolFiles GetOrCreateFiles(string toolId)
    {
        lock (_filesGate)
        {
            if (!_filesByTool.TryGetValue(toolId, out var files))
            {
                var tool = _tools.Find(toolId);
                if (tool is null)
                {
                    throw new BridgeException("TOOL_NOT_FOUND", $"未找到工具 '{toolId}'");
                }

                files = new Tools.ToolFiles(tool.Directory);
                _filesByTool[toolId] = files;
            }

            return files;
        }
    }

    /// <summary>删除工具时同步清理其文件存储实例与磁盘文件。</summary>
    public void OnToolDeleted(string toolId)
    {
        lock (_filesGate)
        {
            if (_filesByTool.Remove(toolId, out var files))
            {
                files.DeleteAll();
            }
        }
    }

    private sealed record MethodEntry(BridgeMethodHandler Handler, ToolPermission? RequiredPermission, bool HostOnly);

    private void RegisterMethods()
    {
        // storage.* —— 需要 storage 权限
        Register("storage.get", StorageMethods.GetAsync, ToolPermission.Storage);
        Register("storage.set", StorageMethods.SetAsync, ToolPermission.Storage);
        Register("storage.remove", StorageMethods.RemoveAsync, ToolPermission.Storage);
        Register("storage.list", StorageMethods.ListAsync, ToolPermission.Storage);

        // ai.chat —— 需要 ai 权限（M2 实现，M1 返回 AI_NOT_CONFIGURED）
        Register("ai.chat", AiMethods.ChatAsync, ToolPermission.Ai);

        // files.* —— 需要 files 权限（二进制文件存取）
        Register("files.save", FilesMethods.SaveAsync, ToolPermission.Files);
        Register("files.list", FilesMethods.ListAsync, ToolPermission.Files);
        Register("files.read", FilesMethods.ReadAsync, ToolPermission.Files);
        Register("files.delete", FilesMethods.DeleteAsync, ToolPermission.Files);

        // tool.getManifest —— 无需权限
        Register("tool.getManifest", ToolMethods.GetManifestAsync, null);

        // manager.* —— 仅宿主管理界面
        Register("manager.getServerInfo", ManagerMethods.GetServerInfoAsync, null, hostOnly: true);
        Register("manager.listTools", ManagerMethods.ListToolsAsync, null, hostOnly: true);
        Register("manager.openTool", ManagerMethods.OpenToolAsync, null, hostOnly: true);
        Register("manager.deleteTool", ManagerMethods.DeleteToolAsync, null, hostOnly: true);
        Register("manager.getAiConfig", ManagerMethods.GetAiConfigAsync, null, hostOnly: true);
        Register("manager.setAiConfig", ManagerMethods.SetAiConfigAsync, null, hostOnly: true);
        Register("manager.openSettings", SettingsMethods.OpenSettingsAsync, null, hostOnly: true);
        Register("manager.importTool", ImportMethods.ImportToolAsync, null, hostOnly: true);
        Register("manager.listVersions", VersionMethods.ListVersionsAsync, null, hostOnly: true);
        Register("manager.switchVersion", VersionMethods.SwitchVersionAsync, null, hostOnly: true);
        Register("manager.deleteVersion", VersionMethods.DeleteVersionAsync, null, hostOnly: true);
        Register("manager.generateUpdate", VersionMethods.GenerateUpdateAsync, null, hostOnly: true);
        Register("manager.applyUpdate", VersionMethods.ApplyUpdateAsync, null, hostOnly: true);
        Register("manager.listApps", LauncherMethods.ListAppsAsync, null, hostOnly: true);
        Register("manager.launchApp", LauncherMethods.LaunchAppAsync, null, hostOnly: true);
        Register("manager.addApp", LauncherMethods.AddAppAsync, null, hostOnly: true);
        Register("manager.deleteApp", LauncherMethods.DeleteAppAsync, null, hostOnly: true);
        Register("manager.discoverPrograms", LauncherMethods.DiscoverProgramsAsync, null, hostOnly: true);
        Register("manager.pickProgram", LauncherMethods.PickProgramAsync, null, hostOnly: true);
        Register("manager.launcherList", LauncherMethods.LauncherListAsync, null, hostOnly: true);
        Register("manager.reindexPrograms", LauncherMethods.ReindexProgramsAsync, null, hostOnly: true);
        Register("manager.generateTool", ToolCreatorMethods.GenerateToolAsync, null, hostOnly: true);
        Register("manager.createTool", ToolCreatorMethods.CreateToolAsync, null, hostOnly: true);
        Register("manager.fixTool", ToolCreatorMethods.FixToolAsync, null, hostOnly: true);
        Register("manager.embedTool", EmbedMethods.EmbedToolAsync, null, hostOnly: true);
        Register("manager.cancelRequest", CancelMethods.CancelRequestAsync, null, hostOnly: true);

        // 备份恢复 + Git 远程备份 + 壳配置 —— 仅宿主管理界面
        Register("manager.listBackups", BackupMethods.ListBackupsAsync, null, hostOnly: true);
        Register("manager.createBackup", BackupMethods.CreateBackupAsync, null, hostOnly: true);
        Register("manager.restoreBackup", BackupMethods.RestoreBackupAsync, null, hostOnly: true);
        Register("manager.deleteBackup", BackupMethods.DeleteBackupAsync, null, hostOnly: true);
        Register("manager.importBackupFile", BackupMethods.ImportBackupFileAsync, null, hostOnly: true);
        Register("manager.openBackupDirectory", BackupMethods.OpenBackupDirectoryAsync, null, hostOnly: true);
        Register("manager.getBackupConfig", BackupMethods.GetBackupConfigAsync, null, hostOnly: true);
        Register("manager.setBackupConfig", BackupMethods.SetBackupConfigAsync, null, hostOnly: true);
        Register("manager.gitPush", BackupMethods.GitPushAsync, null, hostOnly: true);
        Register("manager.gitRestore", BackupMethods.GitRestoreAsync, null, hostOnly: true);
        Register("manager.setHotkeyEnabled", ShellMethods.SetHotkeyEnabledAsync, null, hostOnly: true);
        Register("manager.openContent", ShellMethods.OpenContentAsync, null, hostOnly: true);
        Register("manager.closeContent", ShellMethods.CloseContentAsync, null, hostOnly: true);
    }

    private void Register(
        string method,
        BridgeMethodHandler handler,
        ToolPermission? requiredPermission,
        bool hostOnly = false)
    {
        _methods[method] = new MethodEntry(handler, requiredPermission, hostOnly);
    }

    /// <summary>
    /// 处理一条桥接消息，返回要发回页面的响应 JSON。
    /// </summary>
    /// <param name="authoritativeToolId">宿主侧窗口绑定的 toolId（权威来源）。</param>
    /// <param name="rawMessage">页面传来的原始 JSON 字符串。</param>
    /// <param name="push">流式推送通道（可选）：收到已序列化的 {"id","stream":...} 信封，直接发给页面。</param>
    public async System.Threading.Tasks.Task<string> DispatchAsync(
        string authoritativeToolId, string rawMessage, Action<string>? push = null)
    {
        var requestId = "unknown";
        var methodName = "unknown";
        System.Threading.CancellationTokenSource? cts = null;

        try
        {
            // 5a. 消息大小限制
            if (Encoding.UTF8.GetByteCount(rawMessage) > MaxMessageBytes)
            {
                return BuildError(requestId, "BRIDGE_MESSAGE_TOO_LARGE", $"单条桥接消息不超过 {MaxMessageBytes / 1024 / 1024} MB");
            }

            // 1. 反序列化请求信封
            BridgeRequest? request;
            try
            {
                request = JsonSerializer.Deserialize(rawMessage, AppJsonContext.Default.BridgeRequest);
            }
            catch (JsonException ex)
            {
                return BuildError(requestId, "BRIDGE_BAD_REQUEST", $"消息不是合法 JSON：{ex.Message}");
            }

            if (request is null || string.IsNullOrEmpty(request.Id) || string.IsNullOrEmpty(request.Method))
            {
                return BuildError(requestId, "BRIDGE_BAD_REQUEST", "缺少 id 或 method 字段");
            }

            requestId = request.Id;
            methodName = request.Method;

            // 2. toolId 以宿主侧绑定为准
            var toolId = authoritativeToolId;
            if (!string.Equals(request.ToolId, toolId, StringComparison.Ordinal))
            {
                // 不一致只记录不阻断：客户端字段本就不可信
                Log.Debug($"桥接消息 toolId 不一致：客户端='{request.ToolId}' 宿主绑定='{toolId}'（以宿主为准）");
            }

            // 2b. 内嵌凭证：主窗口与被内嵌的工具 iframe 共用一个原生回调（绑定都是 __host__），
            // 无法用窗口绑定区分来源，改由宿主签发的凭证建立身份。
            // 凭证只在「宿主绑定 == __host__」时参与解析；独立工具窗口忽略 token 字段，
            // 恶意工具无法借凭证提权（它自己窗口的绑定已是它自己的 toolId）。
            if (string.Equals(toolId, HostToolId, StringComparison.Ordinal) && request.Token is not null)
            {
                var embedded = EmbedTokens.Resolve(request.Token);
                if (embedded is null)
                {
                    return BuildError(requestId, "PERMISSION_DENIED", "内嵌凭证无效或已过期");
                }

                toolId = embedded;
            }

            // 3. 方法必须在方法表内
            if (!_methods.TryGetValue(request.Method, out var entry))
            {
                return BuildError(requestId, "UNKNOWN_METHOD", $"未知方法 '{request.Method}'");
            }

            // 4. 权限校验
            var tool = _tools.Find(toolId);
            if (string.Equals(toolId, HostToolId, StringComparison.Ordinal))
            {
                if (!entry.HostOnly)
                {
                    return BuildError(requestId, "PERMISSION_DENIED", "该方法不对宿主管理界面开放");
                }
            }
            else
            {
                if (entry.HostOnly)
                {
                    return BuildError(requestId, "PERMISSION_DENIED", "该方法仅限宿主管理界面调用");
                }

                if (tool is null)
                {
                    return BuildError(requestId, "TOOL_NOT_FOUND", $"未找到工具 '{toolId}'");
                }

                if (entry.RequiredPermission is { } required && !tool.HasPermission(required))
                {
                    return BuildError(requestId, "PERMISSION_DENIED", $"权限不足：manifest 未声明 {required.ToString().ToLowerInvariant()}");
                }
            }

            // 5. 载荷大小（消息级已限，这里按方法做业务校验）
            // 每个请求一个取消令牌源：长任务（AI 生成 / Git 推送）经 context.CancellationToken 透传，
            // manager.cancelRequest 按 id 触发取消。
            cts = new System.Threading.CancellationTokenSource();
            _pendingRequests[requestId] = cts;

            var context = new BridgeContext
            {
                ToolId = toolId,
                Method = request.Method,
                RequestId = requestId,
                Payload = request.Payload,
                Tool = tool,
                Storage = _storage,
                Registry = _tools,
                Shell = Shell,
                ServerInfo = ServerInfo,
                Ai = _ai,
                Secrets = _secrets,
                Creator = _creator,
                FilesFactory = () => GetOrCreateFiles(toolId),
                Importer = _importer,
                EmbedTokens = EmbedTokens,
                Apps = _apps,
                Launcher = _launcher,
                AppStore = _appStore,
                Discovery = _discovery,
                ProgramIndex = _programIndex,
                Usage = _usage,
                Backup = _backup,
                GitBackup = _gitBackup,
                CancellationToken = cts.Token,
                PushDelta = push is null ? null : delta => push(BuildStreamChunk(requestId, delta)),
                CancelRequest = CancelPendingRequest,
            };

            // 6+7. 执行业务逻辑并回包
            var result = await entry.Handler(context).ConfigureAwait(false);
            Log.Debug($"桥接 {toolId}.{methodName} -> ok");
            return BuildSuccess(requestId, result);
        }
        catch (BridgeException ex)
        {
            Log.Debug($"桥接 {methodName} 业务拒绝：{ex.Code} {ex.Message}");
            return BuildError(requestId, ex.Code, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Log.Debug($"桥接 {methodName} 已取消");
            return BuildError(requestId, "REQUEST_CANCELLED", "请求已取消");
        }
        catch (Exception ex)
        {
            // 内部异常只进日志，页面只收到 INTERNAL_ERROR（不泄露堆栈）
            Log.Error($"桥接调用 {requestId} 内部异常", ex);
            return BuildError(requestId, "INTERNAL_ERROR", "宿主内部错误，详情见日志");
        }
        finally
        {
            if (cts is not null)
            {
                // 只移除本次登记的实例：同 id 并发时不碰后来者
                ((ICollection<KeyValuePair<string, System.Threading.CancellationTokenSource>>)_pendingRequests)
                    .Remove(new KeyValuePair<string, System.Threading.CancellationTokenSource>(requestId, cts));
                cts.Dispose();
            }
        }
    }

    /// <summary>按请求 id 取消正在执行的桥接请求；不存在（已结束/未知）返回 false。</summary>
    public bool CancelPendingRequest(string requestId)
    {
        if (!_pendingRequests.TryGetValue(requestId, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        Log.Info($"已请求取消桥接请求 {requestId}");
        return true;
    }

    private static string BuildStreamChunk(string requestId, string delta) =>
        JsonSerializer.Serialize(
            new BridgeStreamEnvelope(requestId, new BridgeStreamBody(delta)),
            AppJsonContext.Default.BridgeStreamEnvelope);

    private static string BuildSuccess(string requestId, JsonElement result)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", requestId);
            writer.WritePropertyName("result");
            result.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildError(string requestId, string code, string message)
    {
        // 统一记录每条错误响应：权限拒绝、未知方法、参数错误都可追溯（绝不记录敏感内容）
        Log.Debug($"桥接 {requestId} -> {code}: {message}");

        return JsonSerializer.Serialize(
            new BridgeErrorResponse(requestId, new BridgeError(code, message)),
            AppJsonContext.Default.BridgeErrorResponse);
    }
}
