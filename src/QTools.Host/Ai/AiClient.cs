using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QTools.Host.Bridge;
using QTools.Host.Logging;
using QTools.Host.Security;

namespace QTools.Host.Ai;

/// <summary>OpenAI 兼容的聊天请求/响应 DTO（源生成 JSON）。</summary>
public sealed record AiChatRequest(
    string Model,
    double Temperature,
    IReadOnlyList<AiMessage> Messages,
    bool Stream = false);

public sealed record AiMessage(string Role, string Content);

public sealed record AiChatResponse(IReadOnlyList<AiChoice>? Choices);

public sealed record AiChoice(AiResponseMessage? Message);

public sealed record AiResponseMessage(string? Content);

/// <summary>SSE 流式响应的单个 chunk（OpenAI 兼容：choices[0].delta.content）。</summary>
public sealed record AiStreamChunk(IReadOnlyList<AiStreamChoice>? Choices);

public sealed record AiStreamChoice(AiStreamDelta? Delta);

public sealed record AiStreamDelta(string? Content);

/// <summary>工具创造器使用的生成结果（AI 输出经容错解析后得到）。</summary>
/// <remarks>
/// 字段名用 <see cref="JsonPropertyName"/> 固定：源生成上下文默认 camelCase，
/// 否则 ManifestJson 会被映射成 manifestJson，与 AI 输出中的 manifest 对不上。
/// manifest 用 <see cref="JsonElement"/> 承接：AI 输出的是嵌套 JSON 对象而非字符串。
/// </remarks>
public sealed record GeneratedTool(
    [property: JsonPropertyName("manifest")] JsonElement Manifest,
    [property: JsonPropertyName("indexHtml")] string IndexHtml);

/// <summary>
/// OpenAI 兼容 AI 客户端。API Key 只从 <see cref="ISecretStore"/> 读取，
/// 且**只在请求头中出现**——绝不写日志、绝不进桥接响应。
/// </summary>
public sealed class AiClient
{
    private const string ServiceName = SecretStoreFactory.AiServiceName;
    private const string AccountName = SecretStoreFactory.AiAccountName;

    private readonly HttpClient _http;
    private readonly ISecretStore _secrets;
    private volatile Configuration.AppConfig _config;

    /// <summary>自持 HttpClient：不用 IHttpClientFactory（其日志 handler 会在 Debug 级打印 Authorization 头）。</summary>
    public AiClient(HttpClient http, ISecretStore secrets, Configuration.AppConfig config)
    {
        _http = http;
        // 超时统一交给每次请求的 CTS，避免 HttpClient 默认的 100 秒上限截断配置。
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _secrets = secrets;
        _config = config;
    }

    internal void UpdateConfig(Configuration.AppConfig config) => _config = config;

    public bool IsConfigured =>
        _secrets.TryGet(ServiceName, AccountName, out var key) &&
        !string.IsNullOrWhiteSpace(key) &&
        !string.IsNullOrWhiteSpace(_config.Ai.BaseUrl);

    public string ModeName => _secrets.ModeName;

    /// <summary>调用聊天接口，返回 assistant 的 content。</summary>
    public async Task<string> ChatAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        double? temperature,
        CancellationToken cancellationToken)
    {
        // 一次请求只读取一次不可变配置，保存新配置不影响在途请求。
        var config = _config.Ai;
        if (!_secrets.TryGet(ServiceName, AccountName, out var apiKey) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(config.BaseUrl) ||
            string.IsNullOrWhiteSpace(config.Model))
        {
            throw new BridgeException("AI_NOT_CONFIGURED", "宿主尚未配置 AI 接口（baseUrl / model / API Key）");
        }

        var temperatureValue = temperature ?? config.Temperature;
        var request = new AiChatRequest(
            config.Model,
            Math.Clamp(temperatureValue, 0d, 2d),
            messages.Select(m => new AiMessage(m.Role, m.Content)).ToArray());

        var json = JsonSerializer.Serialize(request, QTools.Host.Serialization.AppJsonContext.Default.AiChatRequest);
        var url = BuildChatCompletionsUrl(config.BaseUrl);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds)));

        try
        {
            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

            // 只记录状态与耗时，绝不记录请求体（messages 可能含隐私）与 Authorization 头
            Log.Info($"AI 请求完成：HTTP {status}，模型 {config.Model}，响应 {body.Length} 字节");

            if (!response.IsSuccessStatusCode)
            {
                throw MapHttpError(status);
            }

            AiChatResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(body, QTools.Host.Serialization.AppJsonContext.Default.AiChatResponse);
            }
            catch (JsonException)
            {
                throw new BridgeException("AI_INVALID_RESPONSE", "AI 响应不是合法 JSON");
            }

            var content = parsed?.Choices is { Count: > 0 } ? parsed.Choices[0].Message?.Content : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new BridgeException("AI_INVALID_RESPONSE", "AI 响应缺少 choices[0].message.content");
            }

            return content;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new BridgeException("AI_TIMEOUT", $"AI 请求超时（{config.TimeoutSeconds}s）");
        }
        catch (OperationCanceledException)
        {
            throw new BridgeException("AI_TIMEOUT", "AI 请求被取消");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // 上游错误可能回显鉴权信息，不把原始异常或响应体带回页面。
            throw new BridgeException("AI_NETWORK_ERROR", "无法连接或读取 AI 服务，请检查接口地址和网络连接");
        }
    }

    /// <summary>
    /// 流式聊天：以 SSE 逐块接收，每收到一段增量就调用 <paramref name="onDelta"/>，最终返回完整内容。
    /// 超时按「空闲」计（每收到数据即重置），避免长生成被误杀；服务端不支持流式时自动回退整体响应。
    /// </summary>
    public async Task<string> ChatStreamingAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        double? temperature,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        var config = _config.Ai;
        if (!_secrets.TryGet(ServiceName, AccountName, out var apiKey) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(config.BaseUrl) ||
            string.IsNullOrWhiteSpace(config.Model))
        {
            throw new BridgeException("AI_NOT_CONFIGURED", "宿主尚未配置 AI 接口（baseUrl / model / API Key）");
        }

        var temperatureValue = temperature ?? config.Temperature;
        var request = new AiChatRequest(
            config.Model,
            Math.Clamp(temperatureValue, 0d, 2d),
            messages.Select(m => new AiMessage(m.Role, m.Content)).ToArray(),
            Stream: true);

        var json = JsonSerializer.Serialize(request, QTools.Host.Serialization.AppJsonContext.Default.AiChatRequest);
        var url = BuildChatCompletionsUrl(config.BaseUrl);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        var idleSeconds = Math.Max(1, Math.Max(config.TimeoutSeconds, 60));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(idleSeconds));

        try
        {
            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                Log.Info($"AI 流式请求失败：HTTP {status}，模型 {config.Model}，响应 {errorBody.Length} 字节");
                throw MapHttpError(status);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                // 服务端忽略 stream 参数：回退整体响应，增量回调一次全文，UI 行为保持一致
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                Log.Info($"AI 请求完成（非流式回退）：HTTP {status}，模型 {config.Model}，响应 {body.Length} 字节");
                var full = ParseChatContent(body);
                onDelta(full);
                return full;
            }

            var builder = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var dataBuilder = new StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)) is not null)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(idleSeconds));

                if (line.Length == 0)
                {
                    if (dataBuilder.Length > 0)
                    {
                        var data = dataBuilder.ToString();
                        dataBuilder.Clear();
                        if (data == "[DONE]")
                        {
                            break;
                        }

                        AppendChunk(data, builder, onDelta);
                    }

                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (dataBuilder.Length > 0)
                    {
                        dataBuilder.Append('\n');
                    }

                    dataBuilder.Append(line[5..].TrimStart());
                }
                // event:/id:/retry: 与注释行（keep-alive）忽略
            }

            if (dataBuilder.Length > 0 && dataBuilder.ToString() != "[DONE]")
            {
                AppendChunk(dataBuilder.ToString(), builder, onDelta);
            }

            var content = builder.ToString();
            Log.Info($"AI 流式请求完成：HTTP {status}，模型 {config.Model}，共 {content.Length} 字符");
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new BridgeException("AI_INVALID_RESPONSE", "AI 流式响应没有内容");
            }

            return content;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new BridgeException("AI_TIMEOUT", $"AI 请求超时（{idleSeconds}s 内未收到数据）");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new BridgeException("AI_CANCELLED", "AI 请求已取消");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new BridgeException("AI_NETWORK_ERROR", "无法连接或读取 AI 服务，请检查接口地址和网络连接");
        }
    }

    private static void AppendChunk(string data, StringBuilder builder, Action<string> onDelta)
    {
        AiStreamChunk? chunk;
        try
        {
            chunk = JsonSerializer.Deserialize(data, QTools.Host.Serialization.AppJsonContext.Default.AiStreamChunk);
        }
        catch (JsonException)
        {
            return; // 单个坏块不中断整个流
        }

        var delta = chunk?.Choices is { Count: > 0 } ? chunk.Choices[0].Delta?.Content : null;
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        builder.Append(delta);
        onDelta(delta);
    }

    private static string ParseChatContent(string body)
    {
        AiChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(body, QTools.Host.Serialization.AppJsonContext.Default.AiChatResponse);
        }
        catch (JsonException)
        {
            throw new BridgeException("AI_INVALID_RESPONSE", "AI 响应不是合法 JSON");
        }

        var content = parsed?.Choices is { Count: > 0 } ? parsed.Choices[0].Message?.Content : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new BridgeException("AI_INVALID_RESPONSE", "AI 响应缺少 choices[0].message.content");
        }

        return content;
    }

    private static BridgeException MapHttpError(int status)
    {
        return status switch
        {
            401 or 403 => new BridgeException("AI_UNAUTHORIZED", $"AI 服务拒绝访问（HTTP {status}）：请检查 API Key"),
            429 => new BridgeException("AI_RATE_LIMITED", $"AI 服务限流（HTTP 429）"),
            >= 500 => new BridgeException("AI_UPSTREAM_ERROR", $"AI 服务内部错误（HTTP {status}）"),
            _ => new BridgeException("AI_BAD_REQUEST", $"AI 服务拒绝请求（HTTP {status}）：请检查接口地址与请求参数"),
        };
    }

    /// <summary>把用户配置的 baseUrl 规整为 /chat/completions 端点（容忍末尾斜杠与是否已含 /v1）。</summary>
    public static string BuildChatCompletionsUrl(string baseUrl)
    {
        var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return trimmed + "/chat/completions";
    }
}

/// <summary>配置加载/保存（源生成 JSON；缺失或损坏时用默认值并备份坏文件）。</summary>
public static class ConfigStore
{
    public static Configuration.AppConfig Load(string configFilePath)
    {
        if (!File.Exists(configFilePath))
        {
            var defaults = Configuration.AppConfig.Default;
            Save(configFilePath, defaults);
            return defaults;
        }

        try
        {
            var config = JsonSerializer.Deserialize(
                File.ReadAllText(configFilePath),
                QTools.Host.Serialization.AppJsonContext.Default.AppConfig);

            if (config is null)
            {
                return Configuration.AppConfig.Default;
            }

            // 逐字段回退默认值：配置文件可能来自旧版本
            var ai = config.Ai ?? Configuration.AppConfig.Default.Ai;
            return config with
            {
                Ai = ai with
                {
                    BaseUrl = string.IsNullOrWhiteSpace(ai.BaseUrl) ? Configuration.AppConfig.Default.Ai.BaseUrl : ai.BaseUrl,
                    Model = string.IsNullOrWhiteSpace(ai.Model) ? Configuration.AppConfig.Default.Ai.Model : ai.Model,
                    Temperature = ai.Temperature is >= 0 and <= 2 ? ai.Temperature : Configuration.AppConfig.Default.Ai.Temperature,
                    TimeoutSeconds = ai.TimeoutSeconds is > 0 and <= 600 ? ai.TimeoutSeconds : Configuration.AppConfig.Default.Ai.TimeoutSeconds,
                },
                Backup = config.Backup ?? Configuration.AppConfig.Default.Backup,
                Shell = config.Shell ?? Configuration.AppConfig.Default.Shell,
            };
        }
        catch (JsonException)
        {
            var backup = $"{configFilePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            try
            {
                File.Move(configFilePath, backup, overwrite: true);
                Log.Warn($"配置文件损坏，已备份为 {Path.GetFileName(backup)} 并使用默认配置");
            }
            catch (Exception)
            {
                Log.Warn("配置文件损坏且备份失败，使用默认配置");
            }

            return Configuration.AppConfig.Default;
        }
    }

    public static void Save(string configFilePath, Configuration.AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, QTools.Host.Serialization.AppJsonContext.Default.AppConfig);
        var temp = configFilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, configFilePath, overwrite: true);
    }
}
