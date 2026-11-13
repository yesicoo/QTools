using System.Net;
using System.Text;
using System.Text.Json;
using QTools.Host.Ai;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Security;
using QTools.Host.Storage;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>AI 客户端：URL 规整与参数校验（错误码映射的纯函数部分）。</summary>
public sealed class AiClientTests
{
    [Theory]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/", "https://api.deepseek.com/chat/completions")]
    [InlineData("https://api.deepseek.com/v1", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.deepseek.com/v1/", "https://api.deepseek.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("http://127.0.0.1:11434/v1", "http://127.0.0.1:11434/v1/chat/completions")]
    [InlineData("https://example.com/v1/chat/completions", "https://example.com/v1/chat/completions")]
    [InlineData("https://example.com/v1/chat/completions/", "https://example.com/v1/chat/completions")]
    public void BuildChatCompletionsUrlNormalizes(string input, string expected)
    {
        Assert.Equal(expected, AiClient.BuildChatCompletionsUrl(input));
    }

    [Theory]
    [InlineData("https://api.deepseek.com/anthropic", true)]
    [InlineData("https://api.deepseek.com/anthropic/", true)]
    [InlineData("https://api.deepseek.com/anthropic/v1", true)]
    [InlineData("https://api.deepseek.com/anthropic/v1/messages", true)]
    [InlineData(" https://api.deepseek.com/Anthropic ", true)]
    [InlineData("https://api.deepseek.com", false)]
    [InlineData("https://api.deepseek.com/v1", false)]
    [InlineData("", false)]
    public void DetectsAnthropicEndpoint(string input, bool expected)
    {
        Assert.Equal(expected, AiClient.IsAnthropicEndpoint(input));
    }

    [Theory]
    [InlineData("https://api.deepseek.com/anthropic", "https://api.deepseek.com/anthropic/v1/messages")]
    [InlineData("https://api.deepseek.com/anthropic/", "https://api.deepseek.com/anthropic/v1/messages")]
    [InlineData("https://api.deepseek.com/anthropic/v1", "https://api.deepseek.com/anthropic/v1/messages")]
    [InlineData("https://api.deepseek.com/anthropic/v1/messages", "https://api.deepseek.com/anthropic/v1/messages")]
    public void BuildAnthropicMessagesUrlNormalizes(string input, string expected)
    {
        Assert.Equal(expected, AiClient.BuildAnthropicMessagesUrl(input));
    }

    [Fact]
    public async Task AnthropicEndpointUsesMessagesApi()
    {
        string? url = null;
        string? apiKey = null;
        string? version = null;
        string? authorization = null;
        string? rawBody = null;
        using var handler = new AiTestHandler(async (request, _) =>
        {
            url = request.RequestUri!.ToString();
            apiKey = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.FirstOrDefault() : null;
            version = request.Headers.TryGetValues("anthropic-version", out var versions) ? versions.FirstOrDefault() : null;
            authorization = request.Headers.Authorization?.ToString();
            rawBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"content":[{"type":"text","text":"完成"}]}""", Encoding.UTF8, "application/json"),
            };
        });
        using var http = new HttpClient(handler);
        var config = AppConfig.Default with
        {
            Ai = AppConfig.Default.Ai with { BaseUrl = "https://api.deepseek.com/anthropic" },
        };
        var client = new AiClient(http, new AiTestSecrets(), config);

        Assert.Equal("完成", await client.ChatAsync([("system", "规则"), ("user", "测试")], null, CancellationToken.None));

        Assert.Equal("https://api.deepseek.com/anthropic/v1/messages", url);
        Assert.Equal(AiTestSecrets.TestKey, apiKey);
        Assert.Equal("2023-06-01", version);
        Assert.Null(authorization);

        using var body = JsonDocument.Parse(rawBody!);
        Assert.Equal(32768, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("规则", body.RootElement.GetProperty("system").GetString());
        var messageRoles = body.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString()).ToArray();
        Assert.Equal(new[] { "user" }, messageRoles);
    }

    [Fact]
    public async Task OpenAiStreamingForwardsReasoningBeforeContent()
    {
        var sse = string.Join("\n", new[]
        {
            """data: {"choices":[{"delta":{"reasoning_content":"先想一下"}}]}""",
            "",
            """data: {"choices":[{"delta":{"content":"答案"}}]}""",
            "",
            "data: [DONE]",
            "",
        });
        using var handler = new AiTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        }));
        using var http = new HttpClient(handler);
        var client = new AiClient(http, new AiTestSecrets(), AppConfig.Default);
        var deltas = new List<(string Text, bool Reasoning)>();

        var content = await client.ChatStreamingAsync(
            [("user", "测试")], null, (text, reasoning) => deltas.Add((text, reasoning)), CancellationToken.None);

        Assert.Equal("答案", content);
        Assert.Equal(new[] { ("先想一下", true), ("答案", false) }, deltas);
    }

    [Fact]
    public async Task AnthropicStreamingForwardsThinkingAndText()
    {
        var sse = string.Join("\n", new[]
        {
            "event: content_block_delta",
            """data: {"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"想"}}""",
            "",
            "event: content_block_delta",
            """data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"答"}}""",
            "",
            "event: message_stop",
            """data: {"type":"message_stop"}""",
            "",
        });
        string? url = null;
        using var handler = new AiTestHandler((request, _) =>
        {
            url = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        });
        using var http = new HttpClient(handler);
        var config = AppConfig.Default with
        {
            Ai = AppConfig.Default.Ai with { BaseUrl = "https://api.deepseek.com/anthropic" },
        };
        var client = new AiClient(http, new AiTestSecrets(), config);
        var deltas = new List<(string Text, bool Reasoning)>();

        var content = await client.ChatStreamingAsync(
            [("user", "测试")], null, (text, reasoning) => deltas.Add((text, reasoning)), CancellationToken.None);

        Assert.Equal("https://api.deepseek.com/anthropic/v1/messages", url);
        Assert.Equal("答", content);
        Assert.Equal(new[] { ("想", true), ("答", false) }, deltas);
    }

    [Fact]
    public void ChatOptionsRequireMessagesArray()
    {
        var payload = JsonDocument.Parse("{}").RootElement;
        var ex = Assert.Throws<BridgeException>(() => PayloadReader.ReadChatOptions(payload));
        Assert.Equal("AI_BAD_REQUEST", ex.Code);
    }

    [Fact]
    public void ChatOptionsRejectEmptyMessages()
    {
        var payload = JsonDocument.Parse("{\"messages\":[]}").RootElement;
        var ex = Assert.Throws<BridgeException>(() => PayloadReader.ReadChatOptions(payload));
        Assert.Equal("AI_BAD_REQUEST", ex.Code);
    }

    [Fact]
    public void ChatOptionsRejectUnknownRole()
    {
        var payload = JsonDocument.Parse("{\"messages\":[{\"role\":\"tool\",\"content\":\"x\"}]}").RootElement;
        var ex = Assert.Throws<BridgeException>(() => PayloadReader.ReadChatOptions(payload));
        Assert.Equal("AI_BAD_REQUEST", ex.Code);
    }

    [Fact]
    public void ChatOptionsRejectTooManyMessages()
    {
        var messages = string.Join(",", Enumerable.Range(0, 51).Select(i => "{\"role\":\"user\",\"content\":\"x\"}"));
        var payload = JsonDocument.Parse($"{{\"messages\":[{messages}]}}").RootElement;
        var ex = Assert.Throws<BridgeException>(() => PayloadReader.ReadChatOptions(payload));
        Assert.Equal("AI_BAD_REQUEST", ex.Code);
    }

    [Fact]
    public void ChatOptionsRejectOutOfRangeTemperature()
    {
        var payload = JsonDocument.Parse("{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"temperature\":9}").RootElement;
        var ex = Assert.Throws<BridgeException>(() => PayloadReader.ReadChatOptions(payload));
        Assert.Equal("AI_BAD_REQUEST", ex.Code);
    }

    [Fact]
    public void ChatOptionsAcceptValidInput()
    {
        var payload = JsonDocument.Parse(
            "{\"messages\":[{\"role\":\"system\",\"content\":\"a\"},{\"role\":\"user\",\"content\":\"b\"}],\"temperature\":0.5}")
            .RootElement;

        var (messages, temperature) = PayloadReader.ReadChatOptions(payload);

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal(0.5d, temperature);
    }

    [Fact]
    public void ChatOptionsTemperatureIsOptional()
    {
        var payload = JsonDocument.Parse("{\"messages\":[{\"role\":\"user\",\"content\":\"b\"}]}").RootElement;
        var (messages, temperature) = PayloadReader.ReadChatOptions(payload);

        Assert.Single(messages);
        Assert.Null(temperature);
    }

    [Fact]
    public async Task RequestsKeepSnapshotAndApplyAllUpdatedSettings()
    {
        var original = AppConfig.Default with { Ai = AppConfig.Default.Ai with { TimeoutSeconds = 1 } };
        var updated = original with { Ai = new AiConfigSection("http://127.0.0.1:11434/v1", "新模型", 1.25, 600) };
        var calls = 0;
        using var waitingContent = new AiTestContent((_, token) => Task.Delay(Timeout.Infinite, token));
        using var handler = new AiTestHandler(async (request, token) =>
        {
            var expected = ++calls == 1 ? original.Ai : updated.Ai;
            await AssertRequestAsync(request, expected);
            if (calls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = waitingContent };
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1200), token);
            return AiTestHandler.Success();
        });
        using var http = new HttpClient(handler);
        var secrets = new AiTestSecrets();
        var client = new AiClient(http, secrets, original);
        secrets.BeforeRead = () => client.UpdateConfig(updated);

        var error = await Assert.ThrowsAsync<BridgeException>(() => ChatAsync(client));
        Assert.Equal("AI_TIMEOUT", error.Code);
        Assert.Contains("1s", error.Message);
        Assert.True(waitingContent.Disposed);
        Assert.Equal("完成", await ChatAsync(client));
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NetworkFailuresDuringSendOrBodyAreMapped(bool duringBody, bool ioFailure)
    {
        Exception failure = ioFailure
            ? new IOException(AiTestSecrets.TestKey)
            : new HttpRequestException(AiTestSecrets.TestKey);
        using var content = new AiTestContent((_, _) => Task.FromException(failure));
        using var handler = new AiTestHandler((_, _) => duringBody
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })
            : Task.FromException<HttpResponseMessage>(failure));
        using var http = new HttpClient(handler);
        var client = new AiClient(http, new AiTestSecrets(), AppConfig.Default);

        var error = await Assert.ThrowsAsync<BridgeException>(() => ChatAsync(client));
        Assert.Equal("AI_NETWORK_ERROR", error.Code);
        Assert.DoesNotContain(AiTestSecrets.TestKey, error.Message);
        if (duringBody) { Assert.True(content.Disposed); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationDuringSendOrBodyIsMapped(bool duringBody)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WaitAsync(CancellationToken token)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }

        using var content = new AiTestContent((_, token) => WaitAsync(token));
        using var handler = new AiTestHandler(async (_, token) =>
        {
            if (!duringBody) { await WaitAsync(token); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        var client = new AiClient(http, new AiTestSecrets(), AppConfig.Default);
        using var cancellation = new CancellationTokenSource();
        var request = client.ChatAsync([("user", "测试")], null, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var error = await Assert.ThrowsAsync<BridgeException>(() => request);
        Assert.Equal("AI_TIMEOUT", error.Code);
        Assert.Contains("取消", error.Message);
        if (duringBody) { Assert.True(content.Disposed); }
    }

    [Theory]
    [InlineData(400, "AI_BAD_REQUEST")]
    [InlineData(401, "AI_UNAUTHORIZED")]
    [InlineData(402, "AI_INSUFFICIENT_BALANCE")]
    [InlineData(403, "AI_UNAUTHORIZED")]
    [InlineData(404, "AI_INVALID_BASE_URL")]
    [InlineData(422, "AI_BAD_REQUEST")]
    [InlineData(429, "AI_RATE_LIMITED")]
    [InlineData(500, "AI_UPSTREAM_ERROR")]
    [InlineData(550, "AI_UPSTREAM_ERROR")]
    [InlineData(503, "AI_UPSTREAM_ERROR")]
    public async Task HttpErrorsKeepCodesWithoutExposingKeys(int status, string code)
    {
        using var handler = new AiTestHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(AiTestSecrets.TestKey),
        }));
        using var http = new HttpClient(handler);
        var client = new AiClient(http, new AiTestSecrets(), AppConfig.Default);

        var error = await Assert.ThrowsAsync<BridgeException>(() => ChatAsync(client));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain(AiTestSecrets.TestKey, error.Message);
    }

    internal static Task<string> ChatAsync(AiClient client) => client.ChatAsync([("user", "测试")], null, CancellationToken.None);

    internal static async Task AssertRequestAsync(HttpRequestMessage request, AiConfigSection expected)
    {
        Assert.Equal(AiClient.BuildChatCompletionsUrl(expected.BaseUrl), request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal(expected.Model, body.RootElement.GetProperty("model").GetString());
        Assert.Equal(expected.Temperature, body.RootElement.GetProperty("temperature").GetDouble());
    }
}

// AppPaths 是进程全局状态，此集合必须与其他测试互斥。
[CollectionDefinition("AI 配置串行", DisableParallelization = true)]
public sealed class AiConfigCollection { }

[Collection("AI 配置串行")]
public sealed class AiConfigBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtools-ai-config-" + Guid.NewGuid().ToString("n"));
    private readonly AiTestSecrets _secrets = new();
    private readonly HttpClient _http;
    private readonly AiClient _client;
    private readonly BridgeDispatcher _dispatcher;
    private AiConfigSection _expected = AppConfig.Default.Ai;
    private bool _waitForBody;

    public AiConfigBridgeTests()
    {
        AppPaths.Initialize(_root);
        ConfigStore.Save(ConfigPath, AppConfig.Default);
        _http = new HttpClient(new AiTestHandler(async (request, _) =>
        {
            await AiClientTests.AssertRequestAsync(request, _expected);
            return _waitForBody
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new AiTestContent((_, token) => Task.Delay(Timeout.Infinite, token)),
                }
                : AiTestHandler.Success();
        }));
        _client = new AiClient(_http, _secrets, AppConfig.Default);
        _dispatcher = new BridgeDispatcher(new ToolRegistry(AppPaths.ToolsDirectory),
            new ToolStorage(AppPaths.StorageDirectory), _client, _secrets);
    }

    private string ConfigPath => Path.Combine(_root, "data", "config.json");

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private async Task<JsonElement> DispatchAsync(string payload, string method = "manager.setAiConfig")
    {
        var response = await _dispatcher.DispatchAsync(BridgeDispatcher.HostToolId,
            $$"""{"id":"ai-config","method":"{{method}}","toolId":"__host__","payload":{{payload}}}""");
        using var document = JsonDocument.Parse(response);
        Assert.DoesNotContain(AiTestSecrets.TestKey, response);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task SavedConfigurationImmediatelyChangesAddressModelTemperatureAndTimeout()
    {
        Assert.Equal("完成", await AiClientTests.ChatAsync(_client));
        var response = await DispatchAsync("""{"baseUrl":" http://localhost:11434/v1/ ","model":" 新模型 ","temperature":0.25,"timeoutSeconds":1,"apiKey":""}""");
        Assert.True(response.GetProperty("result").GetProperty("ok").GetBoolean());
        _expected = new AiConfigSection("http://localhost:11434/v1/", "新模型", 0.25, 1);
        Assert.Equal(_expected, ConfigStore.Load(ConfigPath).Ai);
        Assert.Equal("完成", await AiClientTests.ChatAsync(_client));
        Assert.Equal(0, _secrets.Mutations);

        _waitForBody = true;
        var error = await Assert.ThrowsAsync<BridgeException>(() => AiClientTests.ChatAsync(_client));
        Assert.Equal("AI_TIMEOUT", error.Code);
        Assert.Contains("1s", error.Message);
    }

    [Theory]
    [InlineData("\"baseUrl\":\"\"")]
    [InlineData("\"baseUrl\":\"   \"")]
    [InlineData("\"baseUrl\":\"/v1\"")]
    [InlineData("\"baseUrl\":\"ftp://example.com/v1\"")]
    [InlineData("\"baseUrl\":\"file:///tmp/ai\"")]
    [InlineData("\"baseUrl\":\"https://\"")]
    [InlineData("\"baseUrl\":null")]
    [InlineData("\"baseUrl\":123")]
    [InlineData("\"model\":\" \"")]
    [InlineData("\"model\":null")]
    [InlineData("\"model\":123")]
    [InlineData("\"temperature\":-0.01")]
    [InlineData("\"temperature\":2.01")]
    [InlineData("\"temperature\":1e999")]
    [InlineData("\"temperature\":\"NaN\"")]
    [InlineData("\"temperature\":null")]
    [InlineData("\"timeoutSeconds\":0")]
    [InlineData("\"timeoutSeconds\":601")]
    [InlineData("\"timeoutSeconds\":1.5")]
    [InlineData("\"timeoutSeconds\":2147483648")]
    [InlineData("\"timeoutSeconds\":1e999")]
    [InlineData("\"timeoutSeconds\":\"60\"")]
    [InlineData("\"timeoutSeconds\":null")]
    public async Task InvalidConfigurationDoesNotChangeDiskKeyOrClient(string field)
    {
        var original = File.ReadAllText(ConfigPath);
        var response = await DispatchAsync("{" + field + ",\"apiKey\":\"替换密钥\"}");

        Assert.Equal("BRIDGE_BAD_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.Equal(0, _secrets.Mutations);
        Assert.Equal("完成", await AiClientTests.ChatAsync(_client));
    }

    [Theory]
    [InlineData("{\"model\":\"\",\"clearKey\":true}")]
    [InlineData("{\"model\":\"新模型\",\"apiKey\":null}")]
    [InlineData("{\"model\":\"新模型\",\"apiKey\":123}")]
    [InlineData("{\"model\":\"新模型\",\"clearKey\":\"true\"}")]
    [InlineData("{\"model\":\"新模型\",\"clearKey\":null}")]
    public async Task InvalidPayloadIsRejectedBeforeAnyDiskOrKeyWrite(string payload)
    {
        File.Delete(ConfigPath);
        var response = await DispatchAsync(payload);

        Assert.Equal("BRIDGE_BAD_REQUEST", response.GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal(0, _secrets.Mutations);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 600)]
    public async Task BoundaryValuesAreAccepted(double temperature, int timeoutSeconds)
    {
        var payload = FormattableString.Invariant($"{{\"temperature\":{temperature},\"timeoutSeconds\":{timeoutSeconds}}}");
        var response = await DispatchAsync(payload);

        Assert.True(response.GetProperty("result").GetProperty("ok").GetBoolean());
        var saved = ConfigStore.Load(ConfigPath).Ai;
        Assert.Equal(temperature, saved.Temperature);
        Assert.Equal(timeoutSeconds, saved.TimeoutSeconds);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"apiKey\":\"\"}")]
    [InlineData("{\"apiKey\":\"   \"}")]
    public async Task MissingOrEmptyKeyPreservesStoredKey(string payload)
    {
        var response = await DispatchAsync(payload);
        Assert.True(response.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal(0, _secrets.Mutations);
        Assert.True(_client.IsConfigured);
        Assert.Equal(AppConfig.Default, ConfigStore.Load(ConfigPath));
    }

    [Fact]
    public async Task ClearKeyOnlyClearsSecretEvenWhenConfigCannotBeWritten()
    {
        var original = File.ReadAllText(ConfigPath);
        Directory.CreateDirectory(ConfigPath + ".tmp");
        var response = await DispatchAsync("""{"clearKey":true,"apiKey":"忽略此密钥","model":"未保存模型"}""");

        Assert.True(response.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.False(_client.IsConfigured);
        Assert.Equal(1, _secrets.Mutations);
        Assert.Equal(original, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task StoredKeyNeverAppearsInConfigOrConfigResponse()
    {
        var response = await DispatchAsync("""{"apiKey":"仅用于回归的密钥"}""");
        Assert.True(response.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.DoesNotContain("仅用于回归的密钥", response.GetRawText());
        Assert.DoesNotContain("apiKey", File.ReadAllText(ConfigPath), StringComparison.OrdinalIgnoreCase);

        var view = await DispatchAsync("{}", "manager.getAiConfig");
        Assert.DoesNotContain("仅用于回归的密钥", view.GetRawText());
        Assert.True(view.GetProperty("result").GetProperty("configured").GetBoolean());
        Assert.Equal(1, _secrets.Mutations);
    }

    [Fact]
    public async Task FailedConfigWritePreservesClientAndKey()
    {
        var original = File.ReadAllText(ConfigPath);
        Directory.CreateDirectory(ConfigPath + ".tmp");
        var response = await DispatchAsync("""{"model":"不应生效","apiKey":"不应保存"}""");

        Assert.Equal("INTERNAL_ERROR", response.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.Equal(0, _secrets.Mutations);
        Assert.Equal("完成", await AiClientTests.ChatAsync(_client));
    }
}

internal sealed class AiTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);

    internal static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"choices":[{"message":{"content":"完成"}}]}""", Encoding.UTF8, "application/json"),
    };
}

internal sealed class AiTestContent(Func<Stream, CancellationToken, Task> write) : HttpContent
{
    internal bool Disposed { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => write(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        write(stream, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class AiTestSecrets : ISecretStore
{
    internal const string TestKey = "sk-regression-only-not-real";
    private string? _key = TestKey;
    internal Action? BeforeRead { get; set; }
    internal int Mutations { get; private set; }
    public string ModeName => "测试内存存储";
    public bool IsConfigured => _key is not null;

    public bool TryGet(string service, string account, out string? secret)
    {
        var callback = BeforeRead;
        BeforeRead = null;
        callback?.Invoke();
        secret = _key;
        return IsConfigured;
    }

    public void Set(string service, string account, string secret)
    {
        _key = secret;
        Mutations++;
    }

    public void Clear(string service, string account)
    {
        _key = null;
        Mutations++;
    }
}

/// <summary>配置存取：默认值、损坏容错、原子写。</summary>
public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _directory;

    public ConfigStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qtools-cfg-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private string ConfigPath => Path.Combine(_directory, "config.json");

    [Fact]
    public void MissingFileYieldsDefaultsAndCreatesFile()
    {
        var config = ConfigStore.Load(ConfigPath);
        Assert.Equal("https://api.deepseek.com", config.Ai.BaseUrl);
        Assert.Equal("deepseek-flash", config.Ai.Model);
        Assert.True(File.Exists(ConfigPath));
    }

    [Fact]
    public void RoundTripPreservesValues()
    {
        var original = AppConfig.Default with
        {
            Ai = AppConfig.Default.Ai with { BaseUrl = "https://example.com/v1", Model = "m1", Temperature = 1.2, TimeoutSeconds = 30 },
        };

        ConfigStore.Save(ConfigPath, original);
        var loaded = ConfigStore.Load(ConfigPath);

        Assert.Equal("https://example.com/v1", loaded.Ai.BaseUrl);
        Assert.Equal("m1", loaded.Ai.Model);
        Assert.Equal(1.2d, loaded.Ai.Temperature);
        Assert.Equal(30, loaded.Ai.TimeoutSeconds);
    }

    [Fact]
    public void WindowPreferencesRoundTrip()
    {
        var original = AppConfig.Default with
        {
            Shell = AppConfig.Default.Shell! with
            {
                ContentWindowTopmost = true,
                ContentWindowPinned = true,
                ToolWindowStates = new Dictionary<string, ToolWindowState>
                {
                    ["pet"] = new(
                        Topmost: true, Pinned: false, ChipLeft: 320.5, ChipTop: 18,
                        Left: 1280, Top: 96, Width: 860, Height: 640),
                    ["doc"] = new(Topmost: false, Pinned: true),
                },
            },
        };

        ConfigStore.Save(ConfigPath, original);
        var shell = ConfigStore.Load(ConfigPath).Shell!;

        Assert.True(shell.ContentWindowTopmost);
        Assert.True(shell.ContentWindowPinned);
        Assert.NotNull(shell.ToolWindowStates);
        Assert.True(shell.ToolWindowStates!["pet"].Topmost);
        Assert.False(shell.ToolWindowStates["pet"].Pinned);
        Assert.Equal(320.5, shell.ToolWindowStates["pet"].ChipLeft);
        Assert.Equal(18, shell.ToolWindowStates["pet"].ChipTop);
        Assert.Equal(1280, shell.ToolWindowStates["pet"].Left);
        Assert.Equal(96, shell.ToolWindowStates["pet"].Top);
        Assert.Equal(860, shell.ToolWindowStates["pet"].Width);
        Assert.Equal(640, shell.ToolWindowStates["pet"].Height);
        Assert.False(shell.ToolWindowStates["doc"].Topmost);
        Assert.True(shell.ToolWindowStates["doc"].Pinned);
        Assert.Null(shell.ToolWindowStates["doc"].ChipLeft);
        Assert.Null(shell.ToolWindowStates["doc"].Left);
        Assert.Null(shell.ToolWindowStates["doc"].Width);
    }

    [Fact]
    public void CorruptFileIsBackedUpAndDefaultsUsed()
    {
        File.WriteAllText(ConfigPath, "{ not json");
        var config = ConfigStore.Load(ConfigPath);

        Assert.Equal("deepseek-flash", config.Ai.Model);
        Assert.True(Directory.GetFiles(_directory, "config.json.corrupt-*").Length > 0);
    }

    [Fact]
    public void EmptyFieldsFallBackToDefaults()
    {
        File.WriteAllText(ConfigPath, "{\"ai\":{\"baseUrl\":\"\",\"model\":\"\",\"temperature\":99,\"timeoutSeconds\":-5}}");
        var config = ConfigStore.Load(ConfigPath);

        Assert.Equal("https://api.deepseek.com", config.Ai.BaseUrl);
        Assert.Equal("deepseek-flash", config.Ai.Model);
        Assert.Equal(0.7d, config.Ai.Temperature);
        Assert.Equal(60, config.Ai.TimeoutSeconds);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ai\":null}")]
    [InlineData("null")]
    public void MissingOrNullAiSectionUsesDefaults(string json)
    {
        File.WriteAllText(ConfigPath, json);

        Assert.Equal(AppConfig.Default.Ai, ConfigStore.Load(ConfigPath).Ai);
        Assert.Empty(Directory.GetFiles(_directory, "config.json.corrupt-*"));
        Assert.Equal(json, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void ConfigFileContainsNoApiKeyField()
    {
        ConfigStore.Save(ConfigPath, AppConfig.Default);
        var json = File.ReadAllText(ConfigPath);

        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", json, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>加密文件密钥存储：往返、清除、损坏容错。</summary>
public sealed class EncryptedFileSecretStoreTests : IDisposable
{
    private readonly string _directory;

    public EncryptedFileSecretStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "qtools-secret-" + Guid.NewGuid().ToString("n"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    [Fact]
    public void RoundTrip()
    {
        var store = new EncryptedFileSecretStore(_directory);
        const string secret = "sk-super-secret-value-123456";

        // IsConfigured 只反映 AI 专用的 service/account
        Assert.False(store.IsConfigured);

        store.Set("svc", "acct", secret);

        Assert.True(store.TryGet("svc", "acct", out var read));
        Assert.Equal(secret, read);
    }

    [Fact]
    public void IsConfiguredReflectsAiKeyPresence()
    {
        var store = new EncryptedFileSecretStore(_directory);
        Assert.False(store.IsConfigured);

        store.Set(SecretStoreFactory.AiServiceName, SecretStoreFactory.AiAccountName, "sk-xxx");
        Assert.True(store.IsConfigured);

        store.Clear(SecretStoreFactory.AiServiceName, SecretStoreFactory.AiAccountName);
        Assert.False(store.IsConfigured);
    }

    [Fact]
    public void DifferentAccountsAreSeparate()
    {
        var store = new EncryptedFileSecretStore(_directory);
        store.Set("svc", "a", "value-a");
        store.Set("svc", "b", "value-b");

        Assert.Equal("value-a", Read(store, "svc", "a"));
        Assert.Equal("value-b", Read(store, "svc", "b"));
    }

    [Fact]
    public void ClearRemovesSecret()
    {
        var store = new EncryptedFileSecretStore(_directory);
        store.Set("svc", "acct", "v");

        store.Clear("svc", "acct");

        Assert.False(store.TryGet("svc", "acct", out _));
        Assert.False(store.IsConfigured);
    }

    [Fact]
    public void TamperedCiphertextIsTreatedAsMissing()
    {
        var store = new EncryptedFileSecretStore(_directory);
        store.Set("svc", "acct", "v");

        var path = Path.Combine(_directory, "svc.acct.bin");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;   // 破坏最后一个字节
        File.WriteAllBytes(path, bytes);

        Assert.False(store.TryGet("svc", "acct", out _));
    }

    [Fact]
    public void StoredFileIsNotPlaintext()
    {
        var store = new EncryptedFileSecretStore(_directory);
        const string secret = "sk-plaintext-should-not-appear-987654";
        store.Set("svc", "acct", secret);

        var raw = File.ReadAllText(Path.Combine(_directory, "svc.acct.bin"));
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
    }

    private static string? Read(EncryptedFileSecretStore store, string service, string account)
    {
        return store.TryGet(service, account, out var value) ? value : null;
    }
}
