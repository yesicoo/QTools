using System.Net;

namespace QTools.Host.Server;

/// <summary>
/// CI 冒烟自检：--self-test 启动服务后请求关键端点并立即退出（退出码 0 = 通过）。
/// 覆盖设计文档 §10 的验收标准 #1（/health）与 #9（路径穿越拒绝）。
/// </summary>
internal static class SelfTest
{
    private static int _failures;

    public static int Run(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var baseUrl = $"http://127.0.0.1:{port}";

            Check(client, $"{baseUrl}/health", HttpStatusCode.OK, bodyMustContain: "\"status\":\"ok\"", label: "health 200");
            Check(client, $"{baseUrl}/__host__/does-not-exist.js", HttpStatusCode.NotFound, label: "未知宿主资源 404");
            Check(client, $"{baseUrl}/__host__/index.html", HttpStatusCode.OK, label: "宿主 UI 200");
            Check(client, $"{baseUrl}/__host__/app.js", HttpStatusCode.OK, bodyMustContain: "manager.listTools", label: "主界面脚本 200");
            Check(client, $"{baseUrl}/__host__/app.css", HttpStatusCode.OK, bodyMustContain: "[hidden]", label: "主界面样式 200");
            Check(client, $"{baseUrl}/__host__/bridge.js", HttpStatusCode.OK, bodyMustContain: "window.host", label: "桥接脚本 200");
            Check(client, $"{baseUrl}/__host__/icon.png", HttpStatusCode.OK, label: "品牌图标 200");

            // Host 头校验（防 DNS rebinding）
            using (var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/health"))
            {
                request.Headers.Host = "evil.example.com";
                using var response = client.SendAsync(request).GetAwaiter().GetResult();
                Report($"Host: evil.example.com -> {(int)response.StatusCode}", response.StatusCode == HttpStatusCode.Forbidden);
            }

            // 路径穿越（七条防护的抽样验证）
            CheckTraversal(client, baseUrl);

            // 工具静态资源（若已有工具）
            CheckToolAssets(client, baseUrl);

            // 独立的设置页面
            Check(client, $"{baseUrl}/__host__/settings.html", HttpStatusCode.OK, label: "settings page 200");
            Check(client, $"{baseUrl}/__host__/settings.js", HttpStatusCode.OK, label: "settings script 200");
            Check(client, $"{baseUrl}/__host__/does-not-exist.html", HttpStatusCode.NotFound, label: "unknown host page 404");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"自检异常：{ex}");
            return 1;
        }

        Console.Out.WriteLine(_failures == 0 ? "自检通过" : $"自检失败（{_failures} 项）");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckToolAssets(HttpClient client, string baseUrl)
    {
        var toolsRoot = QTools.Host.Configuration.AppPaths.ToolsDirectory;
        if (!Directory.Exists(toolsRoot))
        {
            return;
        }

        // 工具就是 tools/{id}/ 这一层；绝不递归，否则会把 .versions/{version}/ 快照误当工具
        foreach (var toolDirectory in Directory.EnumerateDirectories(toolsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var directoryName = Path.GetFileName(toolDirectory);
            if (directoryName.StartsWith('.') && directoryName.Length > 1)
            {
                continue;
            }

            var manifestPath = Path.Combine(toolDirectory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var toolId = directoryName;
            var url = $"{baseUrl}/tools/{toolId}/index.html";
            using var response = client.GetAsync(url).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            Report($"GET /tools/{toolId}/index.html -> {(int)response.StatusCode}", response.IsSuccessStatusCode);
            Report($"工具页面注入了桥接垫层（{toolId}）",
                body.Contains("/__host__/bridge.js", StringComparison.Ordinal));
            Report($"工具页面带有 CSP 头（{toolId}）",
                response.Headers.TryGetValues("Content-Security-Policy", out _) ||
                response.Content.Headers.TryGetValues("Content-Security-Policy", out _));
        }
    }

    private static void CheckTraversal(HttpClient client, string baseUrl)
    {
        // 客户端不规范化路径，确保 .. 与编码穿越都到达服务端
        var cases = new[]
        {
            "/tools/hello-notes/../../data/config.json",
            "/tools/hello-notes/..%2f..%2fdata%2fconfig.json",
            "/tools/hello-notes/%2e%2e/%2e%2e/data/config.json",
            "/tools/hello-notes/....//data/config.json",
            "/tools/hello-notes/../../../../Windows/win.ini",
            "/tools/HELLO-NOTES/../../data/config.json",
        };

        foreach (var path in cases)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            var status = (int)response.StatusCode;
            // 穿越必须被拒（404）；绝不允许 200 泄露 tools/ 之外的内容
            Report($"穿越 {path} -> {status}", status is 404 or 400 or 403);
        }

        // 非法 toolId
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/tools/Bad_Id!/index.html"))
        {
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            Report($"非法 toolId -> {(int)response.StatusCode}",
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
        }
    }

    private static void Check(HttpClient client, string url, HttpStatusCode expected, string? bodyMustContain = null, string? label = null)
    {
        using var response = client.GetAsync(url).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        var ok = response.StatusCode == expected &&
                 (bodyMustContain is null || body.Contains(bodyMustContain, StringComparison.OrdinalIgnoreCase));

        Report($"{(label ?? url)} -> {(int)response.StatusCode}", ok);
    }

    private static void Report(string description, bool passed)
    {
        Console.Out.WriteLine($"{(passed ? "PASS" : "FAIL")}  {description}");
        if (!passed)
        {
            _failures++;
        }
    }
}
