using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using QTools.Source;
using Xunit;

namespace QTools.Source.Tests;

public sealed class SourceTests : IDisposable
{
    private const string Password = "source-test-password";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qtools-source-tests-" + Guid.NewGuid().ToString("n"));
    private readonly SourceStore _store;
    private readonly SourceAuth _auth;
    private readonly SourceApplication _application;
    private readonly string _token;
    private readonly string _csrf;

    public SourceTests()
    {
        _store = new SourceStore(_directory);
        _auth = new SourceAuth(_directory, Password);
        _application = new SourceApplication(_store, _auth, secureCookie: false);
        (_token, _csrf) = _auth.Login(Password);
    }

    [Fact]
    public async Task 首页隐藏管理入口并提供最新发布下载()
    {
        var response = await Request("GET", "/");
        Assert.Equal(200, response.StatusCode);
        var html = Encoding.UTF8.GetString(response.Body);
        Assert.DoesNotContain("href=\"/admin\"", html);
        Assert.Contains("https://github.com/yesicoo/QTools/releases/latest", html);
        Assert.Contains("data-source-url", html);
        Assert.Contains("data-copy-source-url", html);
        Assert.Contains("设置 → 工具源", html);
    }

    [Fact]
    public async Task 管理端可编辑描述分类并置顶()
    {
        await Review(await Submit(Archive(Manifest("alpha", "1.0.0", name: "工具 A"), "<h1>A</h1>")), "approve");
        await Review(await Submit(Archive(Manifest("beta", "1.0.0", name: "工具 B"), "<h1>B</h1>")), "approve");

        var response = await AdminRequest("POST", "/admin/api/tools/alpha/metadata",
            JsonHelper.Serialize(new ToolMetadataRequest("管理端描述", "办公", true)));
        Assert.Equal(200, response.StatusCode);

        var catalog = Json<CatalogResponse>(await Request("GET", "/api/v1/tools"));
        Assert.Equal("alpha", catalog.Items[0].Id);
        Assert.True(catalog.Items[0].Pinned);
        Assert.Equal("管理端描述", catalog.Items[0].Description);
        Assert.Equal("办公", catalog.Items[0].Category);
        Assert.Contains("办公", catalog.Categories);

        var second = await Submit(Archive(Manifest("alpha", "1.1.0", name: "工具 A"), "<h1>A2</h1>"));
        await Review(second, "approve");
        var detail = Json<ToolDetailResponse>(await Request("GET", "/api/v1/tools/alpha"));
        Assert.Equal("管理端描述", detail.Tool.Description);
        Assert.True(detail.Tool.Pinned);

        var tooLong = await AdminRequest("POST", "/admin/api/tools/alpha/metadata",
            JsonHelper.Serialize(new ToolMetadataRequest(new string('x', 501), null, null)));
        Assert.Equal(400, tooLong.StatusCode);
    }

    [Fact]
    public async Task 下载计数与下载排行()
    {
        await Review(await Submit(Archive(Manifest("alpha", "1.0.0", name: "工具 A"), "<h1>A</h1>")), "approve");
        await Review(await Submit(Archive(Manifest("beta", "1.0.0", name: "工具 B"), "<h1>B</h1>")), "approve");

        Assert.Equal(200, (await Request("GET", "/api/v1/tools/beta/package")).StatusCode);
        Assert.Equal(200, (await Request("GET", "/api/v1/tools/beta/package")).StatusCode);
        Assert.Equal(200, (await Request("GET", "/api/v1/tools/alpha/package")).StatusCode);

        var ranked = Json<CatalogResponse>(await Request("GET", "/api/v1/tools", query: "sort=downloads"));
        Assert.Equal("beta", ranked.Items[0].Id);
        Assert.Equal(2L, ranked.Items[0].Downloads);
        Assert.Equal(1, ranked.Items[0].Rank);
        Assert.Equal(2, ranked.Items[1].Rank);

        var detail = Json<ToolDetailResponse>(await Request("GET", "/api/v1/tools/beta"));
        Assert.Equal(2L, detail.Tool.Downloads);
        Assert.Equal(1, detail.Tool.Rank);
        Assert.Equal(2, detail.Tool.ToolCount);

        var pin = await AdminRequest("POST", "/admin/api/tools/alpha/metadata",
            JsonHelper.Serialize(new ToolMetadataRequest(null, null, true)));
        Assert.Equal(200, pin.StatusCode);
        var recommended = Json<CatalogResponse>(await Request("GET", "/api/v1/tools"));
        Assert.Equal("alpha", recommended.Items[0].Id);

        var stats = Json<AdminStats>(await AdminRequest("GET", "/admin/api/stats"));
        Assert.Equal(3L, stats.Downloads);
    }

    [Fact]
    public async Task 在线工具提交审核后按目录与下载分发()
    {
        var first = WebArchive("site", "1.0.0", "https://first.example/tool");
        await Review(await Submit(first), "approve");

        var catalog = Json<CatalogResponse>(await Request("GET", "/api/v1/tools"));
        var site = Assert.Single(catalog.Items);
        Assert.Equal("web", site.Kind);
        Assert.Equal("https://first.example/tool", site.Url);
        Assert.Equal("", site.PreviewUrl);
        Assert.Equal(new[] { "1.0.0" }, site.Versions);
        Assert.Equal(200, (await Request("GET", site.DownloadUrl)).StatusCode);

        var second = WebArchive("site", "1.0.0", "https://second.example/tool");
        await Review(await Submit(second), "approve");
        var detail = Json<ToolDetailResponse>(await Request("GET", "/api/v1/tools/site"));
        Assert.Equal("web", detail.Tool.Kind);
        Assert.Equal("https://second.example/tool", detail.Tool.Url);
        Assert.Single(detail.Tool.Versions);

        var toolPage = Encoding.UTF8.GetString((await Request("GET", "/tools/site")).Body);
        Assert.Contains("data-source-url", toolPage);
        Assert.Contains("设置 → 工具源", toolPage);

        var toolConflict = await Submit(Archive(Manifest("site", "1.0.0"), "<h1>小工具</h1>"));
        await ReviewResponse(toolConflict, "approve", 409);

        var invalid = await SubmitResponse(WebArchive("bad", "1.0.0", "ftp://example.com"));
        Assert.Equal(400, invalid.StatusCode);
    }

    [Fact]
    public async Task 提交审核发布下载预览与历史版本往返()
    {
        var first = Archive(Manifest("demo", "1.0.0"), "<html><head><title>第一版</title></head><body><script src=\"https://cdn.example.com/tool.js\"></script><script>fetch(\"https://api.example.com\")</script></body></html>",
            ("tools/demo/icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray()));
        var firstId = await Submit(first);
        Assert.Equal(404, (await Request("GET", "/preview/" + firstId + "/")).StatusCode);
        Assert.Equal(404, (await Request("GET", "/admin/preview/" + firstId + "/")).StatusCode);

        var preview = await AdminRequest("GET", "/admin/preview/" + firstId + "/");
        Assert.Equal(200, preview.StatusCode);
        Assert.Equal("allow-scripts", preview.Headers["Content-Security-Policy-Sandbox"]);
        Assert.StartsWith("sandbox allow-scripts;", preview.Headers["Content-Security-Policy"]);
        Assert.Contains("/admin/preview/bridge.js?sid=" + firstId, Encoding.UTF8.GetString(preview.Body));
        Assert.Contains("object-src 'none'", preview.Headers["Content-Security-Policy"]);
        var svg = await AdminRequest("GET", "/admin/preview/" + firstId + "/icon.svg");
        Assert.Equal(200, svg.StatusCode);
        Assert.StartsWith("sandbox allow-scripts;", svg.Headers["Content-Security-Policy"]);

        var noCsrf = await Request("POST", "/admin/api/submissions/" + firstId + "/review",
            "{\"action\":\"approve\"}"u8.ToArray(), AdminHeaders(includeCsrf: false));
        Assert.Equal(403, noCsrf.StatusCode);
        var badOrigin = await Request("POST", "/admin/api/submissions/" + firstId + "/review",
            "{\"action\":\"approve\"}"u8.ToArray(), AdminHeaders(origin: "https://attacker.example"));
        Assert.Equal(403, badOrigin.StatusCode);

        var pending = Json<Submission>(await AdminRequest("GET", "/admin/api/submissions/" + firstId));
        Assert.Contains(pending.Warnings, item => item.Code == "REMOTE_SCRIPT");
        Assert.Contains(pending.Warnings, item => item.Code == "EXTERNAL_NETWORK");
        await Review(firstId, "approve");

        var catalog = Json<CatalogResponse>(await Request("GET", "/api/v1/tools"));
        Assert.Equal(1, catalog.Total);
        Assert.Equal("1.0.0", catalog.Items[0].Version);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(first)).ToLowerInvariant(), catalog.Items[0].Sha256);
        var detail = Json<ToolDetailResponse>(await Request("GET", "/api/v1/tools/demo"));
        Assert.Equal("演示工具", detail.Manifest.Name);
        var download = await Request("GET", "/api/v1/tools/demo/package");
        Assert.Equal(first, download.Body);
        Assert.Equal(catalog.Items[0].Sha256, download.Headers["X-QTools-SHA256"]);
        Assert.Equal(200, (await Request("GET", "/preview/" + firstId + "/")).StatusCode);

        var second = Archive(Manifest("demo", "1.1.0"), "<h1>第二版</h1>");
        var secondId = await Submit(second);
        await Review(secondId, "approve");
        detail = Json<ToolDetailResponse>(await Request("GET", "/api/v1/tools/demo"));
        Assert.Equal("1.1.0", detail.Tool.Version);
        Assert.Equal(["1.1.0", "1.0.0"], detail.Tool.Versions);
        var oldDetail = Json<ToolDetailResponse>(await Request("GET", "/api/v1/tools/demo", query: "version=1.0.0"));
        Assert.Equal("1.0.0", oldDetail.Tool.Version);
        Assert.Equal(2, oldDetail.Tool.Versions.Count);
        Assert.Equal(first, (await Request("GET", "/api/v1/tools/demo/package", query: "version=1.0.0")).Body);

        var duplicate = await Submit(Archive(Manifest("demo", "1.0.0"), "<h1>重复</h1>"));
        Assert.Equal(409, (await ReviewResponse(duplicate, "approve", 409)).StatusCode);
        var older = await Submit(Archive(Manifest("demo", "0.9.0"), "<h1>降级</h1>"));
        Assert.Equal(409, (await ReviewResponse(older, "approve", 409)).StatusCode);

        await Publish("demo", published: false);
        Assert.Equal(0, Json<CatalogResponse>(await Request("GET", "/api/v1/tools")).Total);
        Assert.Equal(404, (await Request("GET", "/api/v1/tools/demo")).StatusCode);
        Assert.Equal(404, (await Request("GET", "/api/v1/tools/demo/package")).StatusCode);
        Assert.Equal(404, (await Request("GET", "/preview/" + secondId + "/")).StatusCode);
        Assert.Equal(200, (await AdminRequest("GET", "/admin/preview/" + secondId + "/")).StatusCode);
        await Publish("demo", published: true);
        Assert.Equal(200, (await Request("GET", "/preview/" + secondId + "/")).StatusCode);
    }

    [Fact]
    public async Task 嵌套入口会重定向且相对资源可访问()
    {
        var id = await Submit(Archive(Manifest("nested", "1.0.0", "app/index.html"), "<html><head></head><body>嵌套入口<script src=\"app.js\"></script></body></html>",
            ("tools/nested/app/app.js", "console.log('ok')"u8.ToArray())));
        Assert.Equal(302, (await AdminRequest("GET", "/admin/preview/" + id + "/")).StatusCode);
        await Review(id, "approve");
        var entry = await Request("GET", "/preview/" + id + "/");
        Assert.Equal(302, entry.StatusCode);
        Assert.Equal("app/index.html", entry.Headers["Location"]);
        Assert.Equal(200, (await Request("GET", "/preview/" + id + "/app/index.html")).StatusCode);
        Assert.Equal("console.log('ok')", Encoding.UTF8.GetString((await Request("GET", "/preview/" + id + "/app/app.js")).Body));
    }

    [Theory]
    [InlineData("tools/demo/../../evil.txt")]
    [InlineData("tools/demo/.versions/1.0.0/index.html")]
    [InlineData("tools/demo/files/private.bin")]
    [InlineData("tools/demo/Files/private.bin")]
    [InlineData("apps/other/app.json")]
    [InlineData("/tmp/evil.txt")]
    [InlineData("tools\\demo\\evil.txt")]
    [InlineData("C:/evil.txt")]
    public async Task 上传拒绝非法路径(string path)
    {
        var response = await SubmitResponse(Archive(Manifest("demo", "1.0.0"), "<h1>工具</h1>", (path, "x"u8.ToArray())));
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("PACKAGE_INVALID", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public void 校验失败不破坏既有目标目录()
    {
        var destination = Path.Combine(_directory, "existing");
        Directory.CreateDirectory(destination);
        var sentinel = Path.Combine(destination, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        var package = Archive(Manifest("demo", "1.0.0"), "<h1>工具</h1>", ("tools/demo/../evil", "x"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => PackageValidator.ValidateAndExtract(package, destination));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.False(Directory.Exists(Path.Combine(destination, "evil")));
    }

    [Fact]
    public void 拒绝重复条目与符号链接()
    {
        var export = Export("demo", "1.0.0");
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "qtools-export.json", JsonHelper.Serialize(export));
            Write(archive, "qtools-export.json", JsonHelper.Serialize(export));
            Write(archive, "tools/demo/manifest.json", JsonHelper.Serialize(Manifest("demo", "1.0.0")));
            Write(archive, "tools/demo/index.html", "<h1>工具</h1>"u8.ToArray());
        }
        Assert.Throws<InvalidDataException>(() => PackageValidator.ValidateAndExtract(stream.ToArray(), Path.Combine(_directory, "duplicate")));

        stream.SetLength(0);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "qtools-export.json", JsonHelper.Serialize(export));
            Write(archive, "tools/demo/manifest.json", JsonHelper.Serialize(Manifest("demo", "1.0.0")));
            Write(archive, "tools/demo/index.html", "<h1>工具</h1>"u8.ToArray());
            var link = archive.CreateEntry("tools/demo/link");
            link.ExternalAttributes = unchecked((int)(0xA1ED << 16));
            using var target = link.Open();
            target.Write("index.html"u8);
        }
        Assert.Throws<InvalidDataException>(() => PackageValidator.ValidateAndExtract(stream.ToArray(), Path.Combine(_directory, "symlink")));
    }

    [Fact]
    public void 导出清单按实际解压字节限制()
    {
        var exportBytes = JsonHelper.Serialize(Export("demo", "1.0.0"));
        var paddedExport = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(exportBytes) + new string(' ', 65 * 1024));
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "qtools-export.json", paddedExport);
            Write(archive, "tools/demo/manifest.json", JsonHelper.Serialize(Manifest("demo", "1.0.0")));
            Write(archive, "tools/demo/index.html", "<h1>工具</h1>"u8.ToArray());
        }
        var package = stream.ToArray();
        ForgeEntryLength(package, "qtools-export.json", 64 * 1024);
        var exception = Assert.Throws<InvalidDataException>(() =>
            PackageValidator.ValidateAndExtract(package, Path.Combine(_directory, "manifest-bomb")));
        Assert.Contains("qtools-export.json", exception.Message);
    }

    [Fact]
    public async Task 上传拒绝null权限()
    {
        var manifestJson = Encoding.UTF8.GetString(JsonHelper.Serialize(Manifest("demo", "1.0.0")))
            .Replace("\"permissions\":[\"storage\"]", "\"permissions\":null", StringComparison.Ordinal);
        var response = await SubmitResponse(ArchiveRawManifest(manifestJson, "<h1>工具</h1>"));
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("permissions", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public void 按实际解压字节拒绝伪造中央目录长度()
    {
        var zeros = new byte[7 * 1024 * 1024];
        var entries = new List<(string, byte[])>
        {
            ("tools/demo/blob0.bin", zeros),
            ("tools/demo/blob1.bin", zeros),
            ("tools/demo/blob2.bin", zeros),
            ("tools/demo/blob3.bin", zeros),
        };
        var package = Archive(Manifest("demo", "1.0.0"), "<h1>压缩炸弹</h1>", [.. entries]);
        ForgeCentralDirectoryLengths(package, "tools/demo/blob");
        var exception = Assert.Throws<InvalidDataException>(() => PackageValidator.ValidateAndExtract(package, Path.Combine(_directory, "bomb")));
        Assert.Contains("解压后体积过大", exception.Message);
    }

    [Fact]
    public async Task 审核持久化失败不改变内存状态()
    {
        var id = await Submit(Archive(Manifest("demo", "1.0.0"), "<h1>工具</h1>"));
        using (var stateLock = new FileStream(Path.Combine(_directory, "state.json"),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = Record.Exception(() => _store.Approve(id, null));
            Assert.True(exception is IOException or UnauthorizedAccessException, $"未触发持久化失败：{exception}");
            Assert.Equal("pending", _store.GetSubmission(id).Status);
            Assert.Empty(_store.Catalog(includeUnpublished: true));
        }

        var reloaded = new SourceStore(_directory);
        Assert.Equal("pending", reloaded.GetSubmission(id).Status);
        Assert.Empty(reloaded.Catalog(includeUnpublished: true));
    }

    [Fact]
    public async Task 拒绝状态持久化且不能再次审核()
    {
        var id = await Submit(Archive(Manifest("demo", "1.0.0"), "<h1>工具</h1>"));
        await Review(id, "reject");
        var status = Json<PublicStatus>(await Request("GET", "/api/v1/submissions/" + id));
        Assert.Equal("rejected", status.Status);
        Assert.NotNull(status.ReviewedAtUtc);
        Assert.Equal(409, (await ReviewResponse(id, "approve", 409)).StatusCode);
        Assert.Equal(0, Json<CatalogResponse>(await Request("GET", "/api/v1/tools")).Total);
    }

    [Fact]
    public async Task 登录限流与凭据持久化()
    {
        for (var i = 0; i < 5; i++)
        {
            var response = await Request("POST", "/admin/api/login", "{\"password\":\"wrong-password\"}"u8.ToArray());
            Assert.Equal(401, response.StatusCode);
        }
        Assert.Equal(429, (await Request("POST", "/admin/api/login", "{\"password\":\"wrong-password\"}"u8.ToArray())).StatusCode);

        var persisted = new SourceAuth(_directory, "replacement-password");
        Assert.Throws<UnauthorizedAccessException>(() => persisted.Login("replacement-password"));
        Assert.NotEmpty(persisted.Login(Password).Token);
        Assert.DoesNotContain(Password, File.ReadAllText(Path.Combine(_directory, "admin.json")));
    }

    [Fact]
    public void 限流表有界且拒绝空键()
    {
        var limiter = new RateLimiter();
        Assert.False(limiter.Allow("", 1, TimeSpan.FromMinutes(1)));
        Assert.False(limiter.Allow("127.0.0.1", 0, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, limiter.TrackedKeyCount);

        for (var i = 0; i < RateLimiter.MaxTrackedKeys; i++)
        {
            Assert.True(limiter.Allow("ip-" + i, 1, TimeSpan.FromMinutes(1)));
        }
        Assert.Equal(RateLimiter.MaxTrackedKeys, limiter.TrackedKeyCount);
        Assert.False(limiter.Allow("new-ip", 1, TimeSpan.FromMinutes(1)));
        Assert.Equal(RateLimiter.MaxTrackedKeys, limiter.TrackedKeyCount);
    }

    [Fact]
    public async Task HTTPS反代模式显式设置SecureCookie()
    {
        var secure = new SourceApplication(_store, _auth, secureCookie: true);
        var response = await secure.HandleAsync(CreateRequest("POST", "/admin/api/login",
            "{\"password\":\"source-test-password\"}"u8.ToArray()), CancellationToken.None);
        Assert.Equal(200, response.StatusCode);
        Assert.Contains("Secure;", response.Headers["Set-Cookie"]);
        Assert.Contains("HttpOnly", response.Headers["Set-Cookie"]);
        Assert.Contains("SameSite=Strict", response.Headers["Set-Cookie"]);
    }

    [Fact]
    public async Task 数据目录迁移后包和预览仍可用且不保存绝对路径()
    {
        var id = await Submit(Archive(Manifest("demo", "1.0.0"), "<h1>迁移</h1>"));
        await Review(id, "approve");
        var moved = Path.Combine(Path.GetTempPath(), "qtools-source-moved-" + Guid.NewGuid().ToString("n"));
        Directory.Move(_directory, moved);
        try
        {
            var store = new SourceStore(moved);
            Assert.Equal(store.PackagePath(id), store.GetSubmission(id).PackagePath);
            Assert.Equal(store.ExtractPath(id), store.GetSubmission(id).ExtractPath);
            var auth = new SourceAuth(moved, Password);
            var application = new SourceApplication(store, auth, false);
            var package = await application.HandleAsync(CreateRequest("GET", "/api/v1/tools/demo/package"), CancellationToken.None);
            Assert.Equal(200, package.StatusCode);
            var preview = await application.HandleAsync(CreateRequest("GET", "/preview/" + id + "/"), CancellationToken.None);
            Assert.Equal(200, preview.StatusCode);
            var state = File.ReadAllText(Path.Combine(moved, "state.json"));
            Assert.DoesNotContain(moved, state);
            Assert.DoesNotContain("packagePath", state);
            Assert.DoesNotContain("extractPath", state);
        }
        finally
        {
            if (Directory.Exists(moved)) { Directory.Delete(moved, recursive: true); }
        }
    }

    [Fact]
    public async Task 预发布版本按SemVer数值比较()
    {
        Assert.True(SemanticVersion.Compare("1.0.0-beta.10", "1.0.0-beta.2") > 0);
        Assert.True(SemanticVersion.Compare("1.0.0", "1.0.0-rc.1") > 0);
        var beta2 = await Submit(Archive(Manifest("demo", "1.0.0-beta.2"), "<h1>beta2</h1>"));
        await Review(beta2, "approve");
        var beta10 = await Submit(Archive(Manifest("demo", "1.0.0-beta.10"), "<h1>beta10</h1>"));
        await Review(beta10, "approve");
        var detail = Json<ToolDetailResponse>(await Request("GET", "/api/v1/tools/demo"));
        Assert.Equal("1.0.0-beta.10", detail.Tool.Version);
        Assert.Equal(["1.0.0-beta.10", "1.0.0-beta.2"], detail.Tool.Versions);
    }

    [Fact]
    public async Task 超大分页回退且查询加号解码()
    {
        var huge = Json<CatalogResponse>(await Request("GET", "/api/v1/tools", query: "page=9223372036854775807&pageSize=100"));
        Assert.Equal(1, huge.Page);
        Assert.Empty(huge.Items);
        var id = await Submit(Archive(Manifest("demo", "1.0.0", name: "hello world"), "<h1>工具</h1>"));
        await Review(id, "approve");
        var found = Json<CatalogResponse>(await Request("GET", "/api/v1/tools", query: "q=hello+world"));
        Assert.Equal(1, found.Total);
    }

    [Fact]
    public async Task JSON字段保持createdAt契约()
    {
        var id = await Submit(Archive(Manifest("demo", "1.0.0"), "<h1>工具</h1>"));
        await Review(id, "approve");
        var response = await Request("GET", "/api/v1/tools/demo");
        using var document = System.Text.Json.JsonDocument.Parse(response.Body);
        var adminResponse = await AdminRequest("GET", "/admin/api/tools");
        var adminCatalog = Json<CatalogToolListResponse>(adminResponse);
        Assert.True(document.RootElement.GetProperty("manifest").TryGetProperty("createdAt", out _));
        Assert.False(document.RootElement.GetProperty("manifest").TryGetProperty("createdAtUtc", out _));
        Assert.Equal(id, adminCatalog.Items[0].CurrentSubmissionId);
        using var adminDocument = System.Text.Json.JsonDocument.Parse(adminResponse.Body);
        Assert.True(adminDocument.RootElement.GetProperty("items")[0].TryGetProperty("currentSubmissionId", out _));
    }

    [Fact]
    public async Task 真实HTTP支持HEAD查询参数并拒绝非法百分号()
    {
        await using var server = new SourceHttpServer(IPAddress.Loopback, 0, _application.HandleAsync);
        server.Start();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.True(head.Content.Headers.ContentLength > 0);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/tools?q=hello+world")).StatusCode);

        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = socket.GetStream();
        await stream.WriteAsync("GET /api/v1/tools?q=%A HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"u8.ToArray());
        using var reader = new StreamReader(stream);
        var raw = await reader.ReadToEndAsync();
        Assert.StartsWith("HTTP/1.1 400", raw);
        Assert.Contains("BAD_REQUEST", raw);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) { Directory.Delete(_directory, recursive: true); }
    }

    private async Task<string> Submit(byte[] package)
    {
        var response = await SubmitResponse(package);
        Assert.True(response.StatusCode == 202, $"提交状态 {response.StatusCode}：{Encoding.UTF8.GetString(response.Body)}");
        return Json<SubmitResponse>(response).Id;
    }

    private Task<SourceHttpResponse> SubmitResponse(byte[] package) => Request("POST", "/api/v1/submissions", Multipart(package),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "multipart/form-data; boundary=qtools-test-boundary" });

    private Task Review(string id, string action) => ReviewResponse(id, action);

    private async Task<SourceHttpResponse> ReviewResponse(string id, string action, int expected = 200)
    {
        var response = await AdminRequest("POST", "/admin/api/submissions/" + id + "/review",
            JsonHelper.Serialize(new ReviewRequest(action, "审核完成")));
        Assert.Equal(expected, response.StatusCode);
        return response;
    }

    private async Task Publish(string id, bool published)
    {
        var action = published ? "publish" : "unpublish";
        var response = await AdminRequest("POST", "/admin/api/tools/" + id + "/" + action, "{}"u8.ToArray());
        Assert.Equal(200, response.StatusCode);
    }

    private Task<SourceHttpResponse> AdminRequest(string method, string path, byte[]? body = null, bool includeCsrf = true) =>
        Request(method, path, body, AdminHeaders(includeCsrf));

    private Dictionary<string, string> AdminHeaders(bool includeCsrf = true, string origin = "http://127.0.0.1:58088") => new()
    {
        ["Cookie"] = SourceAuth.CookieName + "=" + _token,
        ["Content-Type"] = "application/json",
        ["Origin"] = origin,
        ["X-CSRF-Token"] = includeCsrf ? _csrf : "",
    };

    private Task<SourceHttpResponse> Request(string method, string path, byte[]? body = null,
        Dictionary<string, string>? headers = null, string query = "") =>
        _application.HandleAsync(CreateRequest(method, path, body, headers, query), CancellationToken.None);

    private static SourceHttpRequest CreateRequest(string method, string path, byte[]? body = null,
        Dictionary<string, string>? headers = null, string query = "") => new()
    {
        Method = method,
        Path = path,
        Host = "127.0.0.1:58088",
        RemoteIp = "127.0.0.1",
        Body = body ?? [],
        Headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Query = query.Split('&', StringSplitOptions.RemoveEmptyEntries).ToDictionary(
            part => part.Split('=', 2)[0].Replace("+", " "),
            part => part.Contains('=') ? part.Split('=', 2)[1].Replace("+", " ") : null,
            StringComparer.OrdinalIgnoreCase),
    };

    private static T Json<T>(SourceHttpResponse response) where T : class =>
        JsonHelper.Deserialize<T>(response.Body) ?? throw new InvalidDataException("响应为空");

    private static ToolManifest Manifest(string id, string version, string entry = "index.html", string name = "演示工具") => new()
    {
        Id = id,
        Name = name,
        Version = version,
        Description = "源站集成测试工具",
        Entry = entry,
        Permissions = ["storage"],
        Window = new ToolWindow(640, 480, true),
        CreatedAt = "2026-01-01T00:00:00Z",
        Category = "效率",
        Glyph = "测",
    };

    private static ExportManifest Export(string id, string version) => new()
    {
        SchemaVersion = 1,
        Kind = "tool",
        Id = id,
        Name = "演示工具",
        Version = version,
        ExportedAtUtc = "2026-01-01T00:00:00Z",
        AppVersion = "test",
    };

    private static byte[] Archive(ToolManifest manifest, string html, params (string Name, byte[] Data)[] extra)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "qtools-export.json", JsonHelper.Serialize(Export(manifest.Id, manifest.Version)));
            Write(archive, "tools/" + manifest.Id + "/manifest.json", JsonHelper.Serialize(manifest));
            Write(archive, "tools/" + manifest.Id + "/" + manifest.Entry, Encoding.UTF8.GetBytes(html));
            foreach (var (name, data) in extra) { Write(archive, name, data); }
        }
        return stream.ToArray();
    }

    private static byte[] WebArchive(string id, string version, string url, string name = "在线站点")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "qtools-export.json", JsonHelper.Serialize(new ExportManifest
            {
                SchemaVersion = 1,
                Kind = "web",
                Id = id,
                Name = name,
                Version = version,
                ExportedAtUtc = "2026-01-01T00:00:00Z",
                AppVersion = "test",
            }));
            Write(archive, $"webtools/{id}/web.json", JsonHelper.Serialize(new WebToolInfo
            {
                Id = id,
                Name = name,
                Url = url,
                Version = version,
                CreatedAt = "2026-01-01T00:00:00Z",
                Description = "在线工具测试",
                Category = "资讯",
                Glyph = "网",
            }));
        }
        return stream.ToArray();
    }

    private static byte[] ArchiveRawManifest(string manifestJson, string html)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "qtools-export.json", JsonHelper.Serialize(Export("demo", "1.0.0")));
            Write(archive, "tools/demo/manifest.json", Encoding.UTF8.GetBytes(manifestJson));
            Write(archive, "tools/demo/index.html", Encoding.UTF8.GetBytes(html));
        }
        return stream.ToArray();
    }

    private static void Write(ZipArchive archive, string name, byte[] data)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(data);
    }

    private static byte[] Multipart(byte[] package)
    {
        const string boundary = "qtools-test-boundary";
        using var stream = new MemoryStream();
        void Text(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
        Text("--" + boundary + "\r\nContent-Disposition: form-data; name=\"package\"; filename=\"tool.qtools\"\r\nContent-Type: application/zip\r\n\r\n");
        stream.Write(package);
        Text("\r\n--" + boundary + "--\r\n");
        return stream.ToArray();
    }

    private static void ForgeEntryLength(byte[] package, string name, int length)
    {
        for (var i = 0; i + 46 <= package.Length; i++)
        {
            if (package[i] != 0x50 || package[i + 1] != 0x4b ||
                package[i + 2] != 0x01 || package[i + 3] != 0x02)
            {
                continue;
            }

            var nameLength = BitConverter.ToUInt16(package, i + 28);
            if (Encoding.UTF8.GetString(package, i + 46, nameLength) != name) { continue; }
            BitConverter.GetBytes((uint)length).CopyTo(package, i + 24);
            return;
        }
        throw new InvalidOperationException("测试 ZIP 条目不存在");
    }

    private static void ForgeCentralDirectoryLengths(byte[] package, string prefix)
    {
        for (var i = 0; i + 46 <= package.Length; i++)
        {
            if (package[i] != 0x50 || package[i + 1] != 0x4b || package[i + 2] != 0x01 || package[i + 3] != 0x02) { continue; }
            var nameLength = BitConverter.ToUInt16(package, i + 28);
            var name = Encoding.ASCII.GetString(package, i + 46, nameLength);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            package[i + 24] = 1;
            package[i + 25] = 0;
            package[i + 26] = 0;
            package[i + 27] = 0;
        }
    }
}
