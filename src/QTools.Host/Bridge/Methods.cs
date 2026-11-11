using System.Text;
using System.Text.Json;
using QTools.Host.Ai;
using QTools.Host.Configuration;
using QTools.Host.Creator;
using QTools.Host.Launcher;
using QTools.Host.Logging;
using QTools.Host.Security;
using QTools.Host.Serialization;
using QTools.Host.Storage;
using QTools.Host.Tools;

namespace QTools.Host.Bridge;

/// <summary>storage.* 方法实现（权限：storage，按 toolId 隔离）。</summary>
internal static class StorageMethods
{
    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement;

    public static async System.Threading.Tasks.Task<JsonElement> GetAsync(BridgeContext context)
    {
        var key = PayloadReader.ReadKey(context.Payload);
        var value = await context.Storage!.GetAsync(context.ToolId, key).ConfigureAwait(false);

        // 不存在时返回 null（区分「键不存在」与「值为 JSON null」由 found 标记表达）
        var found = value is not null;
        return JsonSerializer.SerializeToElement(
            new StorageGetResult(found, found ? value!.Value : NullElement),
            AppJsonContext.Default.StorageGetResult);
    }

    public static async System.Threading.Tasks.Task<JsonElement> SetAsync(BridgeContext context)
    {
        var key = PayloadReader.ReadKey(context.Payload);
        var value = PayloadReader.ReadValue(context.Payload);
        await context.Storage!.SetAsync(context.ToolId, key, value).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new OkResult(true), AppJsonContext.Default.OkResult);
    }

    public static async System.Threading.Tasks.Task<JsonElement> RemoveAsync(BridgeContext context)
    {
        var key = PayloadReader.ReadKey(context.Payload);
        await context.Storage!.RemoveAsync(context.ToolId, key).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new OkResult(true), AppJsonContext.Default.OkResult);
    }

    public static async System.Threading.Tasks.Task<JsonElement> ListAsync(BridgeContext context)
    {
        var keys = await context.Storage!.ListAsync(context.ToolId).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new StorageListResult(keys), AppJsonContext.Default.StorageListResult);
    }
}

/// <summary>tool.* 方法实现（无需权限）。</summary>
internal static class ToolMethods
{
    public static System.Threading.Tasks.Task<JsonElement> GetManifestAsync(BridgeContext context)
    {
        if (context.Tool is null)
        {
            throw new BridgeException("TOOL_NOT_FOUND", $"未找到工具 '{context.ToolId}'");
        }

        var manifestJson = JsonSerializer.Serialize(context.Tool.Manifest, AppJsonContext.Default.ToolManifest);
        return System.Threading.Tasks.Task.FromResult(JsonDocument.Parse(manifestJson).RootElement);
    }
}

/// <summary>ai.* 方法实现（权限：ai）。宿主代理调用，API Key 不出宿主。</summary>
internal static class AiMethods
{
    public static async System.Threading.Tasks.Task<JsonElement> ChatAsync(BridgeContext context)
    {
        var (messages, temperature) = PayloadReader.ReadChatOptions(context.Payload);

        if (context.Ai is null)
        {
            throw new BridgeException("AI_NOT_CONFIGURED", "宿主尚未配置 AI 接口（baseUrl / model / API Key）");
        }

        var content = await context.Ai.ChatAsync(messages, temperature, CancellationToken.None).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new AiChatResult(content), AppJsonContext.Default.AiChatResult);
    }
}

/// <summary>manager.* 方法实现（仅宿主管理界面）。</summary>
internal static class ManagerMethods
{
    public static System.Threading.Tasks.Task<JsonElement> GetServerInfoAsync(BridgeContext context)
    {
        return System.Threading.Tasks.Task.FromResult(
            JsonSerializer.SerializeToElement(context.ServerInfo!, AppJsonContext.Default.ServerInfoView));
    }

    public static System.Threading.Tasks.Task<JsonElement> ListToolsAsync(BridgeContext context)
    {
        var items = context.Registry!.GetAll().Select(t => new ToolListItem(
            t.Id,
            t.Name,
            t.Manifest.Version,
            t.Manifest.Description,
            t.Permissions.Select(p => p.ToString().ToLowerInvariant()).ToArray(),
            new DateTimeOffset(t.LastModifiedUtc).ToString("o"),
            t.IsValid,
            t.Errors,
            t.HasStorageData,
            QTools.Host.Text.PinyinInitials.BuildSearchKey(t.Name, t.Id, t.Manifest.Description))).ToArray();

        return System.Threading.Tasks.Task.FromResult(
            JsonSerializer.SerializeToElement(new ToolListResult(items), AppJsonContext.Default.ToolListResult));
    }

    public static async System.Threading.Tasks.Task<JsonElement> OpenToolAsync(BridgeContext context)
    {
        var toolId = PayloadReader.ReadToolId(context.Payload);
        if (!context.Registry!.TryGet(toolId, out var tool) || tool is null || !tool.IsValid)
        {
            throw new BridgeException("TOOL_NOT_FOUND", $"工具不存在或 manifest 无效：{toolId}");
        }

        await context.Shell!.OpenToolAsync(tool).ConfigureAwait(false);
        context.Usage?.Record(toolId);
        return JsonSerializer.SerializeToElement(new OkResult(true), AppJsonContext.Default.OkResult);
    }

    public static async System.Threading.Tasks.Task<JsonElement> DeleteToolAsync(BridgeContext context)
    {
        var toolId = PayloadReader.ReadToolId(context.Payload);
        var deleteStorage = PayloadReader.ReadDeleteStorage(context.Payload);

        if (!context.Registry!.TryGet(toolId, out var tool) || tool is null)
        {
            throw new BridgeException("TOOL_NOT_FOUND", $"工具不存在：{toolId}");
        }

        await context.Shell!.DeleteToolAsync(tool, deleteStorage).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new OkResult(true), AppJsonContext.Default.OkResult);
    }

    /// <summary>重命名小工具：只改显示名（manifest.name），工具 id/目录/版本保持不变。</summary>
    public static System.Threading.Tasks.Task<JsonElement> RenameToolAsync(BridgeContext context)
    {
        var toolId = PayloadReader.ReadToolId(context.Payload);
        var name = PayloadReader.ReadStringPayload(context.Payload, "name", required: true, maxLength: 60);
        context.Registry!.Rename(toolId, name);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new OkResult(true), AppJsonContext.Default.OkResult));
    }

    public static System.Threading.Tasks.Task<JsonElement> GetAiConfigAsync(BridgeContext context)
    {
        var ai = context.Ai;
        var config = QTools.Host.Ai.ConfigStore.Load(
            QTools.Host.Configuration.AppPaths.ConfigFilePath);

        // 绝不返回 API Key；只回「是否已配置」与可安全展示的字段
        return System.Threading.Tasks.Task.FromResult(
            JsonSerializer.SerializeToElement(
                new AiConfigView(
                    ai?.IsConfigured ?? false,
                    config.Ai.BaseUrl,
                    config.Ai.Model,
                    config.Ai.Temperature,
                    config.Ai.TimeoutSeconds)
                {
                    SecretStoreMode = ai?.ModeName,
                },
                AppJsonContext.Default.AiConfigView));
    }

    /// <summary>
    /// 保存 AI 配置。apiKey 为空表示不变；clearKey=true 表示清除。
    /// Key 只写入系统保护存储，config.json 与日志中绝不出现。
    /// </summary>
    public static async System.Threading.Tasks.Task<JsonElement> SetAiConfigAsync(BridgeContext context)
    {
        var payload = PayloadReader.RequireObject(context.Payload);

        var baseUrl = ReadOptionalAiString(payload, "baseUrl")?.Trim();
        if (baseUrl is not null &&
            (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
             string.IsNullOrEmpty(uri.Host)))
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "baseUrl 必须是绝对 HTTP(S) 地址");
        }

        var model = ReadOptionalAiString(payload, "model")?.Trim();
        if (model is not null && model.Length == 0)
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "model 不能为空");
        }

        double? temperature = null;
        if (payload.TryGetProperty("temperature", out var temperatureElement))
        {
            if (temperatureElement.ValueKind != JsonValueKind.Number ||
                !temperatureElement.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 2)
            {
                throw new BridgeException("BRIDGE_BAD_REQUEST", "temperature 必须是 0~2 之间的有限数值");
            }

            temperature = value;
        }

        int? timeoutSeconds = null;
        if (payload.TryGetProperty("timeoutSeconds", out var timeoutElement))
        {
            if (timeoutElement.ValueKind != JsonValueKind.Number ||
                !timeoutElement.TryGetInt32(out var value) || value is < 1 or > 600)
            {
                throw new BridgeException("BRIDGE_BAD_REQUEST", "timeoutSeconds 必须是 1~600 之间的整数");
            }

            timeoutSeconds = value;
        }

        var apiKey = ReadOptionalAiString(payload, "apiKey");
        var clearKey = false;
        if (payload.TryGetProperty("clearKey", out var clearKeyElement))
        {
            if (clearKeyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new BridgeException("BRIDGE_BAD_REQUEST", "clearKey 必须是布尔值");
            }

            clearKey = clearKeyElement.GetBoolean();
        }

        if (!clearKey)
        {
            var current = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
            var updated = current with
            {
                Ai = current.Ai with
                {
                    BaseUrl = baseUrl ?? current.Ai.BaseUrl,
                    Model = model ?? current.Ai.Model,
                    Temperature = temperature ?? current.Ai.Temperature,
                    TimeoutSeconds = timeoutSeconds ?? current.Ai.TimeoutSeconds,
                },
            };

            QTools.Host.Ai.ConfigStore.Save(QTools.Host.Configuration.AppPaths.ConfigFilePath, updated);
            context.Ai?.UpdateConfig(updated);
        }

        if (context.Secrets is not null)
        {
            if (clearKey)
            {
                context.Secrets.Clear(
                    QTools.Host.Security.SecretStoreFactory.AiServiceName,
                    QTools.Host.Security.SecretStoreFactory.AiAccountName);
                Log.Info("已清除 AI API Key");
            }
            else if (!string.IsNullOrWhiteSpace(apiKey))
            {
                context.Secrets.Set(
                    QTools.Host.Security.SecretStoreFactory.AiServiceName,
                    QTools.Host.Security.SecretStoreFactory.AiAccountName,
                    apiKey);
                Log.Info("已保存 AI API Key 到系统保护存储");
            }
        }

        await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new OkResult(true), AppJsonContext.Default.OkResult);
    }

    private static string? ReadOptionalAiString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", $"{name} 必须是字符串");
        }

        return element.GetString();
    }
}

/// <summary>本地 HTML 导入（manager.importTool，仅宿主管理界面可用）。</summary>
internal static class ImportMethods
{
    public static async System.Threading.Tasks.Task<JsonElement> ImportToolAsync(BridgeContext context)
    {
        if (context.Importer is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "导入器未就绪");
        }

        var payload = JsonSerializer.Deserialize(
            PayloadReader.RequireObject(context.Payload).GetRawText(),
            AppJsonContext.Default.ImportToolPayload)
            ?? throw new BridgeException("BRIDGE_BAD_REQUEST", "payload 解析失败");

        var result = await context.Importer
            .ImportAsync(payload.Name, payload.Html, payload.Description, payload.AcceptHighSeverity, CancellationToken.None)
            .ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(
            new ImportToolView(
                result.ToolId,
                result.ToolName,
                result.Url,
                result.Saved,
                result.Permissions,
                result.Findings,
                result.HasFatal,
                result.RequiresExplicitAcceptance,
                result.ExternalAssets),
            AppJsonContext.Default.ImportToolView);
    }
}

/// <summary>本机程序启动器（manager.*，仅宿主管理界面可用）。</summary>
internal static class LauncherMethods
{
    private static Launcher.AppRegistry RequireApps(BridgeContext context)
    {
        if (context.Apps is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "启动项注册表未就绪");
        }

        return context.Apps;
    }

    public static System.Threading.Tasks.Task<JsonElement> ListAppsAsync(BridgeContext context)
    {
        var apps = RequireApps(context);
        var items = apps.GetAll().Select(a => new AppListItemView(
            a.Id,
            a.Name,
            a.Entry.Target,
            a.Entry.Category,
            a.Entry.Glyph,
            a.IsValid,
            a.TargetExists,
            a.Entry.Arguments,
            Launcher.WindowsAppIcons.EnsureIcon(a) ? $"/__host__/app-icon/{Uri.EscapeDataString(a.Id)}" : null,
            QTools.Host.Text.PinyinInitials.BuildSearchKey(a.Name, a.Id, a.Entry.Category, a.Entry.Target))).ToArray();

        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new AppListView(items), AppJsonContext.Default.AppListView));
    }

    /// <summary>统一启动列表：小工具 + 手动启动项 + 自动索引程序，按使用频次排序。</summary>
    public static System.Threading.Tasks.Task<JsonElement> LauncherListAsync(BridgeContext context)
    {
        var view = LauncherListBuilder.Build(
            context.Registry!,
            RequireApps(context),
            context.ProgramIndex,
            context.Usage,
            Configuration.AppPaths.IconCacheDirectory,
            context.WebTools,
            context.HiddenPrograms);

        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            view, AppJsonContext.Default.LauncherListView));
    }

    /// <summary>手动触发一次后台重建索引（立即返回，完成后宿主推送 launcher-changed 事件）。</summary>
    public static System.Threading.Tasks.Task<JsonElement> ReindexProgramsAsync(BridgeContext context)
    {
        if (context.ProgramIndex is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "程序索引未就绪");
        }

        context.ProgramIndex.StartBackgroundRefresh();
        var status = new LauncherIndexStatus(
            context.ProgramIndex.IsIndexing, context.ProgramIndex.LastIndexedUtc, context.ProgramIndex.Count);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            status, AppJsonContext.Default.LauncherIndexStatus));
    }

    public static async System.Threading.Tasks.Task<JsonElement> LaunchAppAsync(BridgeContext context)
    {
        var appId = PayloadReader.ReadStringPayload(context.Payload, "appId", required: true, maxLength: 63);
        var apps = RequireApps(context);

        var app = apps.Find(appId);
        if (app is null && context.ProgramIndex is not null)
        {
            // 自动索引程序：没有 app.json，按索引条目临时构造启动项直接启动
            var entry = context.ProgramIndex.Find(appId);
            if (entry is not null)
            {
                app = new LoadedApp
                {
                    Entry = new AppEntry(entry.Id, entry.Name, entry.Target, null, null, entry.Category, null, ""),
                    Directory = Configuration.AppPaths.IconCacheDirectory,
                    IsValid = true,
                };
            }
        }

        if (app is null)
        {
            throw new BridgeException("APP_NOT_FOUND", $"未找到启动项 '{appId}'");
        }

        if (context.Launcher is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "启动器未就绪");
        }

        var result = context.Launcher.Launch(app);
        if (result.Started)
        {
            context.Usage?.Record(appId);
        }

        return JsonSerializer.SerializeToElement(
            new LaunchResultView(result.AppId, result.AppName, result.Started, result.ProcessId, result.Message),
            AppJsonContext.Default.LaunchResultView);
    }

    public static async System.Threading.Tasks.Task<JsonElement> AddAppAsync(BridgeContext context)
    {
        if (context.AppStore is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "启动项存储未就绪");
        }

        var payload = JsonSerializer.Deserialize(
            PayloadReader.RequireObject(context.Payload).GetRawText(),
            AppJsonContext.Default.AddAppPayload)
            ?? throw new BridgeException("BRIDGE_BAD_REQUEST", "payload 解析失败");

        var app = await context.AppStore
            .AddAsync(payload.Name, payload.Target, payload.Arguments, payload.WorkingDirectory,
                payload.Category, payload.Glyph, CancellationToken.None)
            .ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(
            new OkResult(true), AppJsonContext.Default.OkResult);
    }

    public static System.Threading.Tasks.Task<JsonElement> DeleteAppAsync(BridgeContext context)
    {
        var appId = PayloadReader.ReadStringPayload(context.Payload, "appId", required: true, maxLength: 63);
        if (context.AppStore is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "启动项存储未就绪");
        }

        var deleted = context.AppStore.Delete(appId);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new OkResult(deleted), AppJsonContext.Default.OkResult));
    }

    public static System.Threading.Tasks.Task<JsonElement> DiscoverProgramsAsync(BridgeContext context)
    {
        if (context.Discovery is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "程序扫描器未就绪");
        }

        var found = context.Discovery.Scan();
        var items = found.Select(p => new DiscoveredProgramView(p.Name, p.Target, p.Source, p.Category)).ToArray();

        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new DiscoveredProgramListView(items), AppJsonContext.Default.DiscoveredProgramListView));
    }

    /// <summary>打开原生文件选择对话框，让用户挑一个可执行文件。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> PickProgramAsync(BridgeContext context)
    {
        if (context.Shell is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "宿主壳未就绪");
        }

        var picked = await context.Shell.PickProgramFileAsync().ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(
            new PickedFileView(picked), AppJsonContext.Default.PickedFileView);
    }

    /// <summary>新增在线工具（录入网址，保存为 webtools/{id}/web.json）。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> AddWebToolAsync(BridgeContext context)
    {
        if (context.WebToolStore is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "在线工具存储未就绪");
        }

        var payload = JsonSerializer.Deserialize(
            PayloadReader.RequireObject(context.Payload).GetRawText(),
            AppJsonContext.Default.AddWebToolPayload)
            ?? throw new BridgeException("BRIDGE_BAD_REQUEST", "payload 解析失败");

        await context.WebToolStore
            .AddAsync(payload.Name, payload.Url, payload.Category, payload.Glyph, CancellationToken.None)
            .ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(
            new OkResult(true), AppJsonContext.Default.OkResult);
    }

    /// <summary>删除在线工具：先关其窗口（若开着），再删除 webtools/{id}/。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> DeleteWebToolAsync(BridgeContext context)
    {
        var webId = PayloadReader.ReadStringPayload(context.Payload, "webId", required: true, maxLength: 63);
        if (context.WebToolStore is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "在线工具存储未就绪");
        }

        if (context.Shell is not null)
        {
            await context.Shell.CloseToolWindowsAsync(Shell.ShellHost.WebWindowKey(webId)).ConfigureAwait(false);
        }

        var deleted = context.WebToolStore.Delete(webId);
        return JsonSerializer.SerializeToElement(
            new OkResult(deleted), AppJsonContext.Default.OkResult);
    }

    /// <summary>重命名在线工具：只改显示名（web.json 的 name），id/网址/目录保持不变。</summary>
    public static System.Threading.Tasks.Task<JsonElement> RenameWebToolAsync(BridgeContext context)
    {
        if (context.WebToolStore is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "在线工具存储未就绪");
        }

        var webId = PayloadReader.ReadStringPayload(context.Payload, "webId", required: true, maxLength: 63);
        var name = PayloadReader.ReadStringPayload(context.Payload, "name", required: true, maxLength: 60);
        context.WebToolStore.Rename(webId, name);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new OkResult(true), AppJsonContext.Default.OkResult));
    }

    /// <summary>在独立 WebView 窗口在线打开一个已录入的网址。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> OpenWebToolAsync(BridgeContext context)
    {
        var webId = PayloadReader.ReadStringPayload(context.Payload, "webId", required: true, maxLength: 63);
        if (context.WebTools is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "在线工具注册表未就绪");
        }

        var web = context.WebTools.Find(webId)
            ?? throw new BridgeException("WEB_NOT_FOUND", $"未找到在线工具 '{webId}'");

        if (!web.IsValid)
        {
            throw new BridgeException("WEB_INVALID", $"在线工具配置校验失败：{string.Join("；", web.Errors)}");
        }

        if (context.Shell is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "宿主壳未就绪");
        }

        await context.Shell.OpenWebToolAsync(web).ConfigureAwait(false);
        context.Usage?.Record(webId);

        return JsonSerializer.SerializeToElement(
            new OkResult(true), AppJsonContext.Default.OkResult);
    }

    /// <summary>右键索引程序「从列表隐藏」：持久化记录，跨重新索引保留。</summary>
    public static System.Threading.Tasks.Task<JsonElement> HideProgramAsync(BridgeContext context)
    {
        var programId = PayloadReader.ReadStringPayload(context.Payload, "programId", required: true, maxLength: 63);
        if (context.HiddenPrograms is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "隐藏列表未就绪");
        }

        var hidden = context.HiddenPrograms.Hide(programId);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new OkResult(hidden), AppJsonContext.Default.OkResult));
    }

    /// <summary>恢复显示一个被隐藏的索引程序。</summary>
    public static System.Threading.Tasks.Task<JsonElement> UnhideProgramAsync(BridgeContext context)
    {
        var programId = PayloadReader.ReadStringPayload(context.Payload, "programId", required: true, maxLength: 63);
        if (context.HiddenPrograms is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "隐藏列表未就绪");
        }

        var restored = context.HiddenPrograms.Unhide(programId);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new OkResult(restored), AppJsonContext.Default.OkResult));
    }

    /// <summary>右键菜单「属性」：在系统文件管理器中显示条目所在位置（文件选中、目录直接打开）。</summary>
    public static System.Threading.Tasks.Task<JsonElement> RevealItemAsync(BridgeContext context)
    {
        var kind = PayloadReader.ReadStringPayload(context.Payload, "kind", required: true, maxLength: 15);
        var id = PayloadReader.ReadStringPayload(context.Payload, "id", required: true, maxLength: 63);

        var target = kind switch
        {
            LauncherListBuilder.KindTool => context.Registry?.Find(id)?.Directory,
            LauncherListBuilder.KindApp => context.Apps?.Find(id)?.Entry.Target,
            LauncherListBuilder.KindProgram => context.ProgramIndex?.Find(id)?.Target,
            _ => throw new BridgeException("BRIDGE_BAD_REQUEST", $"不支持的定位类型 '{kind}'"),
        };

        if (string.IsNullOrWhiteSpace(target))
        {
            throw new BridgeException("ITEM_NOT_FOUND", $"未找到条目 '{id}'");
        }

        if (!File.Exists(target) && !Directory.Exists(target))
        {
            throw new BridgeException("APP_TARGET_NOT_FOUND", $"目标不存在：{target}");
        }

        LauncherRevealer.Reveal(target);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new OkResult(true), AppJsonContext.Default.OkResult));
    }
}

/// <summary>files.* 方法实现（权限：files）。二进制文件存取，按 toolId 隔离在 tools/{id}/files/。</summary>
internal static class FilesMethods
{
    private static Tools.ToolFiles RequireFiles(BridgeContext context)
    {
        if (context.FilesFactory is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "文件存储未就绪");
        }

        return context.FilesFactory();
    }

    public static async System.Threading.Tasks.Task<JsonElement> SaveAsync(BridgeContext context)
    {
        var payload = JsonSerializer.Deserialize(
            PayloadReader.RequireObject(context.Payload).GetRawText(),
            AppJsonContext.Default.FilesSavePayload)
            ?? throw new BridgeException("BRIDGE_BAD_REQUEST", "payload 解析失败");

        var data = Convert.FromBase64String(payload.DataBase64 ?? string.Empty);
        var files = RequireFiles(context);
        var saved = await files.SaveAsync(payload.Name, data, payload.ContentType, CancellationToken.None)
            .ConfigureAwait(false);

        var url = $"/tools/{context.ToolId}/files/{Uri.EscapeDataString(saved.Name)}";
        return JsonSerializer.SerializeToElement(
            new FilesSaveResult(saved.Name, saved.SizeBytes, url),
            AppJsonContext.Default.FilesSaveResult);
    }

    public static async System.Threading.Tasks.Task<JsonElement> ListAsync(BridgeContext context)
    {
        var files = RequireFiles(context);
        var list = await files.ListAsync().ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new FilesListResult(list), AppJsonContext.Default.FilesListResult);
    }

    public static async System.Threading.Tasks.Task<JsonElement> ReadAsync(BridgeContext context)
    {
        var name = PayloadReader.ReadStringPayload(context.Payload, "name", required: true, maxLength: 128);
        var files = RequireFiles(context);
        var content = await files.ReadAsync(name).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(
            new FilesReadResult(content.Name, content.ContentType, Convert.ToBase64String(content.Data)),
            AppJsonContext.Default.FilesReadResult);
    }

    public static async System.Threading.Tasks.Task<JsonElement> DeleteAsync(BridgeContext context)
    {
        var name = PayloadReader.ReadStringPayload(context.Payload, "name", required: true, maxLength: 128);
        var files = RequireFiles(context);
        var deleted = await files.DeleteAsync(name).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new FilesDeleteResult(deleted), AppJsonContext.Default.FilesDeleteResult);
    }
}

/// <summary>打开独立的设置窗口（仅宿主管理界面可用）。</summary>
internal static class SettingsMethods
{
    public static async System.Threading.Tasks.Task<JsonElement> OpenSettingsAsync(BridgeContext context)
    {
        if (context.Shell is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "宿主壳未就绪");
        }

        await context.Shell.OpenSettingsAsync().ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new OkResult(true), AppJsonContext.Default.OkResult);
    }
}

/// <summary>工具版本管理 + AI 更新（manager.*，仅宿主管理界面可用）。</summary>
internal static class VersionMethods
{
    private static LoadedTool RequireTool(BridgeContext context, string toolId)
    {
        if (context.Registry is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "工具注册表未就绪");
        }

        var tool = context.Registry.Find(toolId);
        if (tool is null)
        {
            throw new BridgeException("TOOL_NOT_FOUND", $"未找到工具 '{toolId}'");
        }

        return tool;
    }

    public static System.Threading.Tasks.Task<JsonElement> ListVersionsAsync(BridgeContext context)
    {
        var toolId = PayloadReader.ReadStringPayload(context.Payload, "toolId", required: true, maxLength: 63);
        var tool = RequireTool(context, toolId);

        var versions = tool.Versions.Select(v => new ToolVersionView(
            v.Version, v.CreatedAt, v.SizeBytes, v.IsActive, v.Note)).ToArray();

        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new ToolVersionListView(toolId, tool.ActiveVersion, versions),
            AppJsonContext.Default.ToolVersionListView));
    }

    public static System.Threading.Tasks.Task<JsonElement> SwitchVersionAsync(BridgeContext context)
    {
        var toolId = PayloadReader.ReadStringPayload(context.Payload, "toolId", required: true, maxLength: 63);
        var version = PayloadReader.ReadStringPayload(context.Payload, "version", required: true, maxLength: 63);
        RequireTool(context, toolId);

        var reloaded = context.Registry!.SwitchVersion(toolId, version);
        if (!reloaded.IsValid)
        {
            throw new BridgeException(
                "VERSION_SWITCH_FAILED",
                $"版本 {version} 切换后未通过校验：{string.Join("；", reloaded.Errors)}");
        }

        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new OkResult(true), AppJsonContext.Default.OkResult));
    }

    public static System.Threading.Tasks.Task<JsonElement> DeleteVersionAsync(BridgeContext context)
    {
        var toolId = PayloadReader.ReadStringPayload(context.Payload, "toolId", required: true, maxLength: 63);
        var version = PayloadReader.ReadStringPayload(context.Payload, "version", required: true, maxLength: 63);
        RequireTool(context, toolId);

        context.Registry!.DeleteVersion(toolId, version);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new OkResult(true), AppJsonContext.Default.OkResult));
    }

    /// <summary>AI 更新：返回新版本内容供审查（不保存）。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> GenerateUpdateAsync(BridgeContext context)
    {
        var toolId = PayloadReader.ReadStringPayload(context.Payload, "toolId", required: true, maxLength: 63);
        var instruction = PayloadReader.ReadStringPayload(context.Payload, "instruction", required: true, maxLength: 4000);
        var bumpText = PayloadReader.ReadStringPayload(context.Payload, "versionBump", required: false, maxLength: 16);
        var bump = Enum.TryParse<Tools.VersionBump>(bumpText, ignoreCase: true, out var parsed)
            ? parsed
            : Tools.VersionBump.Patch;

        var tool = RequireTool(context, toolId);
        if (context.Creator is null)
        {
            throw new BridgeException("AI_NOT_CONFIGURED", "宿主尚未配置 AI 接口");
        }

        var result = await context.Creator
            .UpdateAsync(tool, instruction, bump, context.CancellationToken, context.PushDelta)
            .ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(
            new GeneratedToolView(result.Manifest, result.IndexHtml, result.Findings,
                result.HasFatal, result.RequiresExplicitAcceptance),
            AppJsonContext.Default.GeneratedToolView);
    }

    /// <summary>应用审查通过的 AI 更新：存为新版本并激活。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> ApplyUpdateAsync(BridgeContext context)
    {
        var toolId = PayloadReader.ReadStringPayload(context.Payload, "toolId", required: true, maxLength: 63);
        var tool = RequireTool(context, toolId);
        if (context.Creator is null)
        {
            throw new BridgeException("AI_NOT_CONFIGURED", "宿主尚未配置 AI 接口");
        }

        var generated = DeserializeGenerated(PayloadReader.RequireObject(context.Payload));
        var payload = PayloadReader.RequireObject(context.Payload);
        var acceptHigh = payload.TryGetProperty("acceptHighSeverity", out var acceptEl) &&
                         acceptEl.ValueKind == JsonValueKind.True;

        var reloaded = await context.Creator
            .ApplyUpdateAsync(tool, generated, acceptHigh, CancellationToken.None)
            .ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(
            new OkResult(true), AppJsonContext.Default.OkResult);
    }

    private static Creator.GeneratedToolResult DeserializeGenerated(JsonElement payload)
    {
        if (!payload.TryGetProperty("generated", out var element) ||
            element.ValueKind != JsonValueKind.Object)        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "payload.generated 必填且为对象");
        }

        var view = JsonSerializer.Deserialize(
            element.GetRawText(), AppJsonContext.Default.GeneratedToolView)
            ?? throw new BridgeException("BRIDGE_BAD_REQUEST", "payload.generated 解析失败");

        return new Creator.GeneratedToolResult(
            view.Manifest, view.IndexHtml, view.Findings,
            view.HasFatal, view.RequiresExplicitAcceptance, null);
    }
}

/// <summary>自然语言 → 生成工具（不保存，返回供审查的 manifest/源码/风险项）。</summary>
internal static class ToolCreatorMethods
{
    public static async System.Threading.Tasks.Task<JsonElement> GenerateToolAsync(BridgeContext context)
    {
        var prompt = PayloadReader.ReadStringPayload(context.Payload, "prompt", required: true, maxLength: 4000);

        if (context.Creator is null)
        {
            throw new BridgeException("AI_NOT_CONFIGURED", "宿主尚未配置 AI 接口");
        }

        var result = await context.Creator.GenerateAsync(prompt, context.CancellationToken, context.PushDelta).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(
            new GeneratedToolView(
                result.Manifest,
                result.IndexHtml,
                result.Findings,
                result.HasFatal,
                result.RequiresExplicitAcceptance),
            AppJsonContext.Default.GeneratedToolView);
    }

    /// <summary>保存审查通过的生成结果；高危项需 acceptHighSeverity=true。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> CreateToolAsync(BridgeContext context)
    {
        var root = PayloadReader.RequireObject(context.Payload);
        var view = DeserializeView(root, "generated");

        var acceptHigh = root.TryGetProperty("acceptHighSeverity", out var acceptElement) &&
                         acceptElement.ValueKind == JsonValueKind.True;

        if (context.Creator is null)
        {
            throw new BridgeException("AI_NOT_CONFIGURED", "宿主尚未配置 AI 接口");
        }

        var result = RebuildResult(view);
        var toolId = await context.Creator.CreateAsync(result, acceptHigh, CancellationToken.None).ConfigureAwait(false);

        // 不再由宿主主动弹独立窗口（「保存并打开」的弹窗行为已取消）；
        // 前端拿到 toolId 后自行决定：默认请求 manager.embedTool 在右侧内嵌展示。
        return JsonSerializer.SerializeToElement(
            new CreateToolResult(toolId, Opened: false),
            AppJsonContext.Default.CreateToolResult);
    }

    /// <summary>把错误信息与现有代码回传 AI，生成修复版（不保存）。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> FixToolAsync(BridgeContext context)
    {
        var root = PayloadReader.RequireObject(context.Payload);
        var toolId = PayloadReader.ReadToolId(root);
        var errorDescription = PayloadReader.ReadStringPayload(root, "error", required: true, maxLength: 8000);

        if (context.Creator is null)
        {
            throw new BridgeException("AI_NOT_CONFIGURED", "宿主尚未配置 AI 接口");
        }

        if (context.Registry is null || !context.Registry.TryGet(toolId, out var tool) || tool is null)
        {
            throw new BridgeException("TOOL_NOT_FOUND", $"工具不存在：{toolId}");
        }

        var result = await context.Creator.FixAsync(tool, errorDescription, context.CancellationToken, context.PushDelta).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(
            new GeneratedToolView(
                result.Manifest,
                result.IndexHtml,
                result.Findings,
                result.HasFatal,
                result.RequiresExplicitAcceptance),
            AppJsonContext.Default.GeneratedToolView);
    }

    private static Creator.GeneratedToolResult RebuildResult(GeneratedToolView view) =>
        new(view.Manifest, view.IndexHtml, view.Findings, view.HasFatal, view.RequiresExplicitAcceptance, null);

    private static GeneratedToolView DeserializeView(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", $"payload.{propertyName} 必填且为对象");
        }

        var json = element.GetRawText();
        var view = JsonSerializer.Deserialize(json, AppJsonContext.Default.GeneratedToolView);
        if (view is null)
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", $"payload.{propertyName} 解析失败");
        }

        return view;
    }
}

/// <summary>
/// 内嵌凭证签发（manager.embedTool，仅宿主管理界面）：返回带凭证的工具入口 URL，
/// 供主窗口以 iframe 内嵌打开；工具页面经 bridge.js 携带凭证调用桥接，
/// 分发器凭凭证把身份解析为该 toolId（见 Shell.EmbedTokenRegistry）。
/// </summary>
internal static class EmbedMethods
{
    public static System.Threading.Tasks.Task<JsonElement> EmbedToolAsync(BridgeContext context)
    {
        var toolId = PayloadReader.ReadToolId(context.Payload);

        if (context.Registry is null || context.EmbedTokens is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "内嵌组件未就绪");
        }

        var tool = context.Registry.Find(toolId);
        if (tool is null || !tool.IsValid)
        {
            throw new BridgeException("TOOL_NOT_FOUND", $"工具不存在或 manifest 无效：{toolId}");
        }

        var token = context.EmbedTokens.Issue(tool.Id);
        var url = $"http://127.0.0.1:{context.ServerInfo!.Port}{tool.EntryUrl}?bt={Uri.EscapeDataString(token)}";

        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new EmbedToolView(
                tool.Id,
                tool.Name,
                url,
                tool.Permissions.Select(p => p.ToString().ToLowerInvariant()).ToArray()),
            AppJsonContext.Default.EmbedToolView));
    }
}

/// <summary>取消正在执行的桥接请求（manager.cancelRequest，仅宿主管理界面）。</summary>
internal static class CancelMethods
{
    public static System.Threading.Tasks.Task<JsonElement> CancelRequestAsync(BridgeContext context)
    {
        var requestId = PayloadReader.ReadStringPayload(context.Payload, "id", required: true, maxLength: 128);
        var cancelled = context.CancelRequest?.Invoke(requestId) ?? false;
        return System.Threading.Tasks.Task.FromResult(
            JsonSerializer.SerializeToElement(new OkResult(cancelled), AppJsonContext.Default.OkResult));
    }
}

/// <summary>壳配置（manager.setHotkeyEnabled，仅宿主管理界面）。</summary>
internal static class ShellMethods
{
    public static System.Threading.Tasks.Task<JsonElement> SetHotkeyEnabledAsync(BridgeContext context)
    {
        var payload = PayloadReader.RequireObject(context.Payload);
        if (!payload.TryGetProperty("enabled", out var enabledElement) ||
            enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "payload.enabled 必填且为布尔值");
        }

        if (context.Shell is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "宿主壳未就绪");
        }

        var enabled = enabledElement.GetBoolean();
        context.Shell.SetHotkeyEnabled(enabled);

        var config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
        var shellSection = config.Shell ?? new Configuration.ShellConfigSection(true);
        QTools.Host.Ai.ConfigStore.Save(
            QTools.Host.Configuration.AppPaths.ConfigFilePath,
            config with { Shell = shellSection with { HotkeyEnabled = enabled } });

        return System.Threading.Tasks.Task.FromResult(
            JsonSerializer.SerializeToElement(BackupMethods.BuildConfigView(context), AppJsonContext.Default.BackupConfigView));
    }

    /// <summary>打开内容窗口（工具/AI创建/导入/在线工具）：独立窗口贴主窗口边缘，自动吸附。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> OpenContentAsync(BridgeContext context)
    {
        var view = PayloadReader.ReadStringPayload(context.Payload, "view", required: true, maxLength: 20);
        if (view is not ("tool" or "create" or "import" or "web"))
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "view 必须是 tool / create / import / web");
        }

        string? toolId = null;
        if (view == "tool")
        {
            toolId = PayloadReader.ReadToolId(context.Payload);
            if (!context.Registry!.TryGet(toolId, out var tool) || tool is null || !tool.IsValid)
            {
                throw new BridgeException("TOOL_NOT_FOUND", $"工具不存在或 manifest 无效：{toolId}");
            }

            context.Usage?.Record(toolId);
        }

        if (context.Shell is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "宿主壳未就绪");
        }

        await context.Shell.OpenContentAsync(view, toolId).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new OkResult(true), AppJsonContext.Default.OkResult);
    }

    /// <summary>关闭内容窗口（内容页「收起」按钮）。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> CloseContentAsync(BridgeContext context)
    {
        if (context.Shell is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "宿主壳未就绪");
        }

        await context.Shell.CloseContentAsync().ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new OkResult(true), AppJsonContext.Default.OkResult);
    }
}

/// <summary>备份恢复 + Git 远程备份（manager.*，仅宿主管理界面）。</summary>
internal static class BackupMethods
{
    private static Backup.BackupService RequireBackup(BridgeContext context)
    {
        if (context.Backup is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "备份服务未就绪");
        }

        return context.Backup;
    }

    private static Backup.GitBackup RequireGitBackup(BridgeContext context)
    {
        if (context.GitBackup is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "Git 备份服务未就绪");
        }

        return context.GitBackup;
    }

    /// <summary>备份 + 壳配置视图（不含任何凭证）。</summary>
    internal static Backup.BackupConfigView BuildConfigView(BridgeContext context)
    {
        var config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
        var backup = config.Backup ?? new Configuration.BackupConfigSection(null, null, null);
        var shell = config.Shell ?? new Configuration.ShellConfigSection(true);
        return new Backup.BackupConfigView(
            backup.GitRemoteUrl,
            backup.LastPushUtc,
            backup.LastPushStatus,
            shell.HotkeyEnabled,
            context.Shell?.HotkeySupported ?? false,
            context.Shell?.HotkeyRegistered ?? false);
    }

    public static System.Threading.Tasks.Task<JsonElement> ListBackupsAsync(BridgeContext context)
    {
        var list = RequireBackup(context).List();
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new Backup.BackupListView(list), AppJsonContext.Default.BackupListView));
    }

    public static System.Threading.Tasks.Task<JsonElement> CreateBackupAsync(BridgeContext context)
    {
        var note = PayloadReader.ReadStringPayload(context.Payload, "note", required: false, maxLength: 200);
        var result = RequireBackup(context).Create(note);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            result, AppJsonContext.Default.BackupCreateResult));
    }

    public static System.Threading.Tasks.Task<JsonElement> RestoreBackupAsync(BridgeContext context)
    {
        var name = PayloadReader.ReadStringPayload(context.Payload, "name", required: true, maxLength: 160);
        var result = RequireBackup(context).Restore(name);
        RefreshAfterRestore(context);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            result, AppJsonContext.Default.BackupRestoreResult));
    }

    public static System.Threading.Tasks.Task<JsonElement> DeleteBackupAsync(BridgeContext context)
    {
        var name = PayloadReader.ReadStringPayload(context.Payload, "name", required: true, maxLength: 160);
        var deleted = RequireBackup(context).Delete(name);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new OkResult(deleted), AppJsonContext.Default.OkResult));
    }

    /// <summary>弹原生文件对话框选一个 zip，校验后导入备份目录（未选择返回 name=null）。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> ImportBackupFileAsync(BridgeContext context)
    {
        if (context.Shell is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "宿主壳未就绪");
        }

        var picked = await context.Shell.PickBackupFileAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(picked))
        {
            return JsonSerializer.SerializeToElement(
                new Backup.ImportBackupResult(null), AppJsonContext.Default.ImportBackupResult);
        }

        var name = RequireBackup(context).ImportFile(picked);
        return JsonSerializer.SerializeToElement(
            new Backup.ImportBackupResult(name), AppJsonContext.Default.ImportBackupResult);
    }

    public static System.Threading.Tasks.Task<JsonElement> OpenBackupDirectoryAsync(BridgeContext context)
    {
        RequireBackup(context).OpenDirectory();
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            new OkResult(true), AppJsonContext.Default.OkResult));
    }

    public static System.Threading.Tasks.Task<JsonElement> GetBackupConfigAsync(BridgeContext context)
    {
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            BuildConfigView(context), AppJsonContext.Default.BackupConfigView));
    }

    /// <summary>保存 Git 仓库地址（null/空串 = 清除）。凭证由 git 自身管理，宿主不接触。</summary>
    public static System.Threading.Tasks.Task<JsonElement> SetBackupConfigAsync(BridgeContext context)
    {
        var payload = PayloadReader.RequireObject(context.Payload);
        string? url = null;
        if (payload.TryGetProperty("gitRemoteUrl", out var urlElement) &&
            urlElement.ValueKind != JsonValueKind.Null)
        {
            if (urlElement.ValueKind != JsonValueKind.String)
            {
                throw new BridgeException("BRIDGE_BAD_REQUEST", "gitRemoteUrl 必须是字符串或 null");
            }

            url = urlElement.GetString()?.Trim();
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            Backup.GitBackup.ValidateRemoteUrl(url);
        }
        else
        {
            url = null;
        }

        var config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
        var backup = config.Backup ?? new Configuration.BackupConfigSection(null, null, null);
        QTools.Host.Ai.ConfigStore.Save(
            QTools.Host.Configuration.AppPaths.ConfigFilePath,
            config with { Backup = backup with { GitRemoteUrl = url } });

        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            BuildConfigView(context), AppJsonContext.Default.BackupConfigView));
    }

    /// <summary>推送到 Git：地址取 payload 或已保存配置；进度经流式通道推送；结果写回配置。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> GitPushAsync(BridgeContext context)
    {
        var url = ResolveRemoteUrl(context);
        var git = RequireGitBackup(context);

        Backup.GitPushResult result;
        try
        {
            result = await git.PushAsync(url, context.CancellationToken, context.PushDelta).ConfigureAwait(false);
        }
        catch (BridgeException ex)
        {
            SavePushStatus(context, $"失败：{ex.Message}");
            throw;
        }

        SavePushStatus(context, result.Message, DateTimeOffset.UtcNow.ToString("o"));
        return JsonSerializer.SerializeToElement(result, AppJsonContext.Default.GitPushResult);
    }

    /// <summary>从 Git 恢复：浅克隆 → 快照 → 安全恢复；随后重扫注册表并重载 AI 配置。</summary>
    public static async System.Threading.Tasks.Task<JsonElement> GitRestoreAsync(BridgeContext context)
    {
        var url = ResolveRemoteUrl(context);
        var result = await RequireGitBackup(context)
            .RestoreAsync(url, context.CancellationToken, context.PushDelta)
            .ConfigureAwait(false);
        RefreshAfterRestore(context);
        return JsonSerializer.SerializeToElement(result, AppJsonContext.Default.BackupRestoreResult);
    }

    private static string ResolveRemoteUrl(BridgeContext context)
    {
        var payload = PayloadReader.RequireObject(context.Payload);
        string? url = null;
        if (payload.TryGetProperty("gitRemoteUrl", out var urlElement) &&
            urlElement.ValueKind == JsonValueKind.String)
        {
            url = urlElement.GetString()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            var config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
            url = config.Backup?.GitRemoteUrl;
        }

        Backup.GitBackup.ValidateRemoteUrl(url);
        return url!.Trim();
    }

    private static void SavePushStatus(BridgeContext context, string status, string? lastPushUtc = null)
    {
        try
        {
            var config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
            var backup = config.Backup ?? new Configuration.BackupConfigSection(null, null, null);
            var trimmed = status.Length > 300 ? status[..300] + "…" : status;
            QTools.Host.Ai.ConfigStore.Save(
                QTools.Host.Configuration.AppPaths.ConfigFilePath,
                config with
                {
                    Backup = backup with
                    {
                        LastPushUtc = lastPushUtc ?? backup.LastPushUtc,
                        LastPushStatus = trimmed,
                    },
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"写回 Git 推送状态失败：{ex.Message}");
        }
    }

    /// <summary>恢复后让运行期状态与磁盘一致：重扫工具/启动项、重载 AI 配置（存储本身无内存缓存）。</summary>
    private static void RefreshAfterRestore(BridgeContext context)
    {
        context.Registry?.Rescan();
        context.Apps?.Rescan();
        var config = QTools.Host.Ai.ConfigStore.Load(QTools.Host.Configuration.AppPaths.ConfigFilePath);
        context.Ai?.UpdateConfig(config);
        Log.Info("恢复完成：已重扫工具与启动项，并重载 AI 配置");
    }
}

