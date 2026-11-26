using System.Text;
using System.Text.Json;
using QTools.Host.Serialization;

namespace QTools.Host.Bridge;

internal static class SourceMethods
{
    private static Source.ToolSourceService RequireService(BridgeContext context) =>
        context.SourceService ?? throw new BridgeException("INTERNAL_ERROR", "工具源服务未就绪");

    public static System.Threading.Tasks.Task<JsonElement> GetConfigAsync(BridgeContext context) =>
        System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            RequireService(context).GetConfig(), AppJsonContext.Default.SourceConfigView));

    public static System.Threading.Tasks.Task<JsonElement> SetConfigAsync(BridgeContext context)
    {
        var baseUrl = PayloadReader.ReadStringPayload(context.Payload, "baseUrl", required: false, maxLength: 1000);
        return System.Threading.Tasks.Task.FromResult(JsonSerializer.SerializeToElement(
            RequireService(context).SetConfig(baseUrl), AppJsonContext.Default.SourceConfigView));
    }

    public static async System.Threading.Tasks.Task<JsonElement> TestAsync(BridgeContext context)
    {
        var result = await RequireService(context).TestAsync(context.CancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, AppJsonContext.Default.SourceConfigView);
    }

    public static async System.Threading.Tasks.Task<JsonElement> CatalogAsync(BridgeContext context)
    {
        var payload = PayloadReader.RequireObject(context.Payload);
        var query = PayloadReader.ReadStringPayload(payload, "q", required: false, maxLength: 120);
        var category = PayloadReader.ReadStringPayload(payload, "category", required: false, maxLength: 40);
        var page = ReadPositiveInt(payload, "page", 1);
        var pageSize = ReadPositiveInt(payload, "pageSize", 24);
        var result = await RequireService(context).CatalogAsync(query, category, page, pageSize, context.CancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, AppJsonContext.Default.SourceCatalogView);
    }

    public static async System.Threading.Tasks.Task<JsonElement> ToolAsync(BridgeContext context)
    {
        var payload = PayloadReader.RequireObject(context.Payload);
        var id = PayloadReader.ReadStringPayload(payload, "id", required: true, maxLength: 63);
        var version = PayloadReader.ReadStringPayload(payload, "version", required: false, maxLength: 80);
        var result = await RequireService(context).ToolAsync(id, version, context.CancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, AppJsonContext.Default.SourceToolDetailView);
    }

    public static async System.Threading.Tasks.Task<JsonElement> InstallAsync(BridgeContext context)
    {
        var payload = PayloadReader.RequireObject(context.Payload);
        var id = PayloadReader.ReadStringPayload(payload, "id", required: true, maxLength: 63);
        var version = PayloadReader.ReadStringPayload(payload, "version", required: false, maxLength: 80);
        var accept = payload.TryGetProperty("acceptHighSeverity", out var acceptElement) &&
                     acceptElement.ValueKind == JsonValueKind.True;
        var reviewedSha256 = PayloadReader.ReadStringPayload(payload, "reviewedSha256", required: false, maxLength: 64);
        var result = await RequireService(context).InstallAsync(id, version, accept, reviewedSha256, context.CancellationToken).ConfigureAwait(false);
        if (result.Saved)
        {
            context.Shell?.NotifyLauncherChanged();
        }

        return JsonSerializer.SerializeToElement(result, AppJsonContext.Default.SourceInstallResult);
    }

    public static async System.Threading.Tasks.Task<JsonElement> DownloadAsync(BridgeContext context)
    {
        var payload = PayloadReader.RequireObject(context.Payload);
        var id = PayloadReader.ReadStringPayload(payload, "id", required: true, maxLength: 63);
        var version = PayloadReader.ReadStringPayload(payload, "version", required: false, maxLength: 80);
        if (context.Shell is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "宿主壳未就绪");
        }

        var service = RequireService(context);
        var detail = await service.ToolAsync(id, version, context.CancellationToken).ConfigureAwait(false);
        var picked = await context.Shell.PickExportFileAsync(
            GetDefaultFileName(detail.Tool), "下载工具安装包").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(picked))
        {
            return JsonSerializer.SerializeToElement(
                new Source.SourceDownloadResult(null, null, 0, string.Empty, string.Empty),
                AppJsonContext.Default.SourceDownloadResult);
        }

        var result = await service.DownloadAsync(detail, picked, context.CancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, AppJsonContext.Default.SourceDownloadResult);
    }

    public static async System.Threading.Tasks.Task<JsonElement> SubmitAsync(BridgeContext context)
    {
        var payload = PayloadReader.RequireObject(context.Payload);
        var kind = PayloadReader.ReadStringPayload(payload, "kind", required: false, maxLength: 8) ?? "tool";
        if (kind != "tool" && kind != "web")
        {
            throw new BridgeException("BRIDGE_BAD_REQUEST", "提交类型必须是 tool 或 web");
        }
        var id = PayloadReader.ReadStringPayload(payload, "id", required: false, maxLength: 63)
            ?? PayloadReader.ReadStringPayload(payload, "toolId", required: true, maxLength: 63);
        var result = await RequireService(context).SubmitAsync(kind, id, context.CancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, AppJsonContext.Default.SourceSubmitResult);
    }

    public static async System.Threading.Tasks.Task<JsonElement> PreviewAsync(BridgeContext context)
    {
        var payload = PayloadReader.RequireObject(context.Payload);
        var id = PayloadReader.ReadStringPayload(payload, "id", required: true, maxLength: 63);
        var version = PayloadReader.ReadStringPayload(payload, "version", required: false, maxLength: 80);
        if (context.Shell is null)
        {
            throw new BridgeException("INTERNAL_ERROR", "宿主壳未就绪");
        }

        var service = RequireService(context);
        var detail = await service.ToolAsync(id, version, context.CancellationToken).ConfigureAwait(false);
        var url = service.ResolvePreviewUrl(detail);
        await context.Shell.OpenSourcePreviewAsync(id, detail.Tool.Name, url).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new OkResult(true), AppJsonContext.Default.OkResult);
    }

    private static string GetDefaultFileName(Source.SourceToolView tool)
    {
        var builder = new StringBuilder();
        foreach (var ch in $"{tool.Name}-v{tool.Version}")
        {
            builder.Append(ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(ch)
                ? '_'
                : ch);
        }

        var stem = builder.ToString().Trim().TrimEnd('.', ' ');
        if (stem.Length > 100) { stem = stem[..100].TrimEnd('.', ' '); }
        if (stem.Length == 0) { stem = tool.Id; }
        return stem + Backup.ItemPackageService.FileExtension;
    }

    private static int ReadPositiveInt(JsonElement payload, string name, int fallback)
    {
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var value) || value < 1)
        {
            return fallback;
        }

        return value;
    }
}
