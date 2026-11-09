using System.Net;
using System.Text;
using System.Text.Json;
using QTools.Host.Ai;
using QTools.Host.Bridge;
using QTools.Host.Configuration;
using QTools.Host.Creator;
using QTools.Host.Security;
using QTools.Host.Tools;
using Xunit;

namespace QTools.Tests;

/// <summary>
/// AI 创建链路的提示词增强：用户只写需求，宿主规范（单文件 HTML 便于导入导出、中文界面、桥接存储等）
/// 必须由 GenerateAsync 自动附加到用户消息里，不能指望用户记得写。
/// </summary>
public sealed class AiCreatePromptTests
{
    /// <summary>记录请求体的假 OpenAI 端点：回包是可解析的最小合法生成结果。</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string RequestBody = "";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).Result;

            const string manifest = """{"id":"todo","name":"todo","version":"1.0.0","description":"d","entry":"index.html","permissions":["storage"],"window":{"width":520,"height":720,"resizable":true},"createdAt":"2026-11-09T00:00:00+08:00","allowEval":false,"csp":null}""";
            var payload = "{\"manifest\":" + manifest + ",\"indexHtml\":" +
                          JsonSerializer.Serialize("<html><body>ok</body></html>") + "}";
            var wrapped = "```json\n" + payload + "\n```";
            var body = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" +
                       JsonSerializer.Serialize(wrapped) + "}}]}";

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private static (ToolCreator Creator, CapturingHandler Handler) Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "qtools-create-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(root);
        AppPaths.Initialize(root);

        var config = new AppConfig(
            new AiConfigSection("https://api.example.com/v1", "mock", 0.7, 30),
            new ServerConfigSection(1024 * 1024, 8 * 1024 * 1024),
            new StorageConfigSection(256 * 1024, 5 * 1024 * 1024));
        var handler = new CapturingHandler();
        var secrets = new EncryptedFileSecretStore(Path.Combine(root, "secrets"));
        secrets.Set(SecretStoreFactory.AiServiceName, SecretStoreFactory.AiAccountName, "sk-fake-key-000");
        var ai = new AiClient(new HttpClient(handler), secrets, config);
        return (new ToolCreator(ai, new ToolRegistry(Path.Combine(root, "tools"))), handler);
    }

    [Fact]
    public async Task GenerateAsyncAutoAttachesHostRequirements()
    {
        var (creator, handler) = Create();

        var result = await creator.GenerateAsync("做一个待办清单，能添加、勾选、删除", CancellationToken.None);

        // 请求体是 JSON（中文被转义），解析后取最后一条 user 消息再断言
        using var request = JsonDocument.Parse(handler.RequestBody);
        var userMessage = request.RootElement.GetProperty("messages")
            .EnumerateArray().Last().GetProperty("content").GetString();

        // 用户消息 = 宿主规范 + 用户原文，关键规范点必须出现
        Assert.NotNull(userMessage);
        Assert.Contains("【我的需求】", userMessage);
        Assert.Contains("做一个待办清单，能添加、勾选、删除", userMessage);
        Assert.Contains("单个 index.html", userMessage);
        Assert.Contains("导入 / 导出", userMessage);
        Assert.Contains("CDN", userMessage);
        Assert.DoesNotContain("离线", userMessage);
        Assert.Contains("window.host.storage", userMessage);

        // 生成链路本身不受影响：解析 + 审查正常出结果
        Assert.False(result.HasFatal);
        Assert.Equal("todo", result.Manifest.Id);
    }

    [Fact]
    public async Task GenerateAsyncStillRejectsEmptyAndOversizedPrompt()
    {
        var (creator, _) = Create();

        await Assert.ThrowsAsync<BridgeException>(() => creator.GenerateAsync("   ", CancellationToken.None));
        await Assert.ThrowsAsync<BridgeException>(() => creator.GenerateAsync(new string('x', 4001), CancellationToken.None));
    }
}
