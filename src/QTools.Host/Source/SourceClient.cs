using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QTools.Host.Serialization;

namespace QTools.Host.Source;

internal sealed class SourceClient
{
    public const long MaxPackageBytes = 64L * 1024 * 1024;
    public const long MaxSubmitPackageBytes = 8L * 1024 * 1024;
    private const long MaxApiBytes = 2L * 1024 * 1024;

    private readonly HttpClient _http;

    public SourceClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
    }

    public async Task<SourceConfigView> MetaAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var uri = Endpoint(baseUrl, "api/v1/meta");
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("version", out var version))
        {
            throw new SourceException("SOURCE_PROTOCOL_INVALID", "工具源响应格式不兼容");
        }

        return new SourceConfigView(baseUrl)
        {
            Connected = true,
            ServiceVersion = version.GetString(),
        };
    }

    public async Task<SourceCatalogView> CatalogAsync(
        string baseUrl, string? query, string? category, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["q"] = query,
            ["category"] = category,
            ["page"] = Math.Max(1, page).ToString(),
            ["pageSize"] = Math.Clamp(pageSize, 1, 100).ToString(),
        };
        var uri = Endpoint(baseUrl, "api/v1/tools", parameters);
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Deserialize(AppJsonContext.Default.SourceCatalogView) is { Items: not null, Total: >= 0, Page: > 0, PageSize: > 0 } catalog
            ? catalog
            : throw new SourceException("SOURCE_PROTOCOL_INVALID", "工具目录响应格式不兼容");
    }

    public async Task<SourceToolDetailView> ToolAsync(
        string baseUrl, string id, string? version, CancellationToken cancellationToken)
    {
        var parameters = string.IsNullOrWhiteSpace(version)
            ? null
            : new Dictionary<string, string?> { ["version"] = version };
        var uri = Endpoint(baseUrl, "api/v1/tools/" + Uri.EscapeDataString(id), parameters);
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Deserialize(AppJsonContext.Default.SourceToolDetailView) is { Tool: not null, Manifest: not null } detail
            ? detail
            : throw new SourceException("SOURCE_PROTOCOL_INVALID", "工具详情响应格式不兼容");
    }

    public async Task<(long Size, string Sha256)> DownloadPackageAsync(
        string baseUrl, string id, string? version, string targetPath,
        CancellationToken cancellationToken)
    {
        var parameters = string.IsNullOrWhiteSpace(version)
            ? null
            : new Dictionary<string, string?> { ["version"] = version };
        var uri = Endpoint(baseUrl, "api/v1/tools/" + Uri.EscapeDataString(id) + "/package", parameters);
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is > MaxPackageBytes)
        {
            throw new SourceException("SOURCE_PACKAGE_TOO_LARGE", "源安装包超过 64 MiB 上限");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxPackageBytes)
            {
                throw new SourceException("SOURCE_PACKAGE_TOO_LARGE", "源安装包超过 64 MiB 上限");
            }

            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public async Task<SourceSubmitResult> SubmitAsync(
        string baseUrl, string packagePath, CancellationToken cancellationToken)
    {
        if (new FileInfo(packagePath).Length > MaxSubmitPackageBytes)
        {
            throw new SourceException("SOURCE_PACKAGE_TOO_LARGE", "提交包超过 8 MiB 上限");
        }

        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, "package", Path.GetFileName(packagePath));
        using var response = await _http.PostAsync(Endpoint(baseUrl, "api/v1/submissions"), content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var id) || !root.TryGetProperty("status", out var status))
        {
            throw new SourceException("SOURCE_PROTOCOL_INVALID", "提交响应格式不兼容");
        }

        var toolId = root.TryGetProperty("toolId", out var toolIdElement) ? toolIdElement.GetString() ?? "" : "";
        var name = root.TryGetProperty("toolName", out var nameElement) ? nameElement.GetString() ?? "" : "";
        var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? "" : "";
        return new SourceSubmitResult(id.GetString() ?? "", status.GetString() ?? "", toolId, name, version);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = "SOURCE_HTTP_ERROR";
        var message = $"工具源返回 HTTP {(int)response.StatusCode}";
        try
        {
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetString() ?? code : code;
                message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? message : message;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }

        throw new SourceException(code, message);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxApiBytes)
        {
            throw new SourceException("SOURCE_RESPONSE_TOO_LARGE", "工具源响应超过 2 MiB 上限");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[128 * 1024];
        using var output = new MemoryStream();
        int read;
        long total = 0;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxApiBytes)
            {
                throw new SourceException("SOURCE_RESPONSE_TOO_LARGE", "工具源响应超过 2 MiB 上限");
            }

            output.Write(buffer, 0, read);
        }

        output.Position = 0;
        return await JsonDocument.ParseAsync(output, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static Uri Endpoint(string baseUrl, string relativePath, Dictionary<string, string?>? query = null)
    {
        if (!TryNormalizeBaseUrl(baseUrl, out var root))
        {
            throw new SourceException("SOURCE_URL_INVALID", "工具源地址必须是有效的 HTTP(S) 地址");
        }

        var uri = new Uri(new Uri(root + "/"), relativePath);
        if (query is not null)
        {
            var builder = new UriBuilder(uri);
            var parameters = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var (key, value) in query)
            {
                if (string.IsNullOrEmpty(value)) { continue; }
                parameters.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
            }

            builder.Query = string.Join("&", parameters);
            uri = builder.Uri;
        }

        return uri;
    }

    public static bool TryNormalizeBaseUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) { return false; }
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    public static Uri ResolveUrl(string baseUrl, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (!TryNormalizeBaseUrl(baseUrl, out var normalized))
            {
                throw new SourceException("SOURCE_URL_INVALID", "工具源地址无效");
            }

            absolute = new Uri(new Uri(normalized + "/"), value);
        }

        if ((absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(absolute.UserInfo))
        {
            throw new SourceException("SOURCE_PREVIEW_URL_INVALID", "预览地址必须是 HTTP(S) 且不能包含用户信息");
        }

        return absolute;
    }
}

internal sealed class SourceException : Exception
{
    public SourceException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
