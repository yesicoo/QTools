using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QTools.Source;

public sealed class SourceApplication
{
    private readonly SourceStore _store;
    private readonly SourceAuth _auth;
    private readonly RateLimiter _limiter = new();
    private readonly bool _secureCookie;
    private const string SiteCsp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-src 'self'; base-uri 'none'; object-src 'none'; form-action 'self'; frame-ancestors 'self'";

    public SourceApplication(SourceStore store, SourceAuth auth, bool secureCookie)
    {
        _store = store;
        _auth = auth;
        _secureCookie = secureCookie;
    }

    public Task<SourceHttpResponse> HandleAsync(SourceHttpRequest request, CancellationToken cancellationToken)
    {
        SourceHttpResponse response;
        try
        {
            response = Route(request, cancellationToken);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"请求处理失败：{exception.Message}");
            response = Error(500, "INTERNAL_ERROR", "服务暂时不可用");
        }
        if (!request.Path.StartsWith("/preview/", StringComparison.Ordinal) &&
            !request.Path.StartsWith("/admin/preview/", StringComparison.Ordinal))
        {
            response = response with
            {
                Headers = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Security-Policy"] = SiteCsp,
                    ["X-Frame-Options"] = "SAMEORIGIN",
                },
            };
        }
        return Task.FromResult(response);
    }

    private SourceHttpResponse Route(SourceHttpRequest request, CancellationToken cancellationToken)
    {
        var path = request.Path;
        if (path == "/health") { return Read(request, () => Json(200, new HealthResponse(true, SourceLimits.ServiceVersion))); }
        if (path == "/api/v1/meta") { return Read(request, Meta); }
        if (path == "/api/v1/tools") { return Read(request, () => PublicTools(request)); }
        if (path == "/api/v1/submissions") { return Post(request, () => CreateSubmission(request, cancellationToken)); }
        if (path.StartsWith("/api/v1/tools/", StringComparison.Ordinal))
        {
            var rest = path["/api/v1/tools/".Length..];
            if (rest.EndsWith("/package", StringComparison.Ordinal))
            {
                var id = rest[..^"/package".Length];
                return Read(request, () => PublicPackage(request, id));
            }
            if (!rest.Contains('/')) { return Read(request, () => PublicTool(request, rest)); }
        }
        if (path.StartsWith("/api/v1/submissions/", StringComparison.Ordinal))
        {
            var id = path["/api/v1/submissions/".Length..];
            if (!id.Contains('/')) { return Read(request, () => PublicStatus(id)); }
        }

        if (path.StartsWith("/admin/api/", StringComparison.Ordinal))
        {
            if (path == "/admin/api/session") { return Read(request, () => Session(request)); }
            if (path == "/admin/api/login") { return Post(request, () => Login(request)); }
            if (path == "/admin/api/logout") { return Post(request, () => Logout(request)); }
            var session = RequireAdmin(request);
            if (session is null) { return Error(401, "AUTH_REQUIRED", "请先登录管理后台"); }
            if (path == "/admin/api/stats") { return Read(request, () => Json(200, _store.Stats())); }
            if (path == "/admin/api/submissions")
            {
                return Read(request, () => Json(200, new SubmissionListResponse(_store.Submissions(request.Query.GetValueOrDefault("status")))));
            }
            if (path.StartsWith("/admin/api/submissions/", StringComparison.Ordinal))
            {
                var rest = path["/admin/api/submissions/".Length..];
                if (rest.EndsWith("/review", StringComparison.Ordinal))
                {
                    return AuthorizedPost(request, session, () => Review(rest[..^"/review".Length], request));
                }
                if (!rest.Contains('/')) { return Read(request, () => AdminSubmission(rest)); }
            }
            if (path == "/admin/api/tools")
            {
                return Read(request, () => Json(200, new CatalogToolListResponse(_store.Catalog(includeUnpublished: true))));
            }
            if (path.StartsWith("/admin/api/tools/", StringComparison.Ordinal))
            {
                var rest = path["/admin/api/tools/".Length..];
                if (rest.EndsWith("/metadata", StringComparison.Ordinal))
                {
                    var metadataId = rest[..^"/metadata".Length];
                    return AuthorizedPost(request, session, () => UpdateToolMetadata(metadataId, request));
                }
                var action = rest.EndsWith("/publish", StringComparison.Ordinal) ? "publish"
                    : rest.EndsWith("/unpublish", StringComparison.Ordinal) ? "unpublish" : "";
                if (action.Length > 0)
                {
                    var id = rest[..^(action.Length + 1)];
                    return AuthorizedPost(request, session, () => SetPublished(id, action == "publish"));
                }
            }
            return Error(404, "NOT_FOUND", "接口不存在");
        }

        if (path is "/admin" or "/admin/") { return Page("admin.html", request); }
        if (path == "/submit") { return Page("submit.html", request); }
        if (path == "/") { return Page("index.html", request); }
        if (path.StartsWith("/submissions/", StringComparison.Ordinal) && !path["/submissions/".Length..].Contains('/'))
        {
            return Page("status.html", request);
        }
        if (path.StartsWith("/tools/", StringComparison.Ordinal) && !path["/tools/".Length..].Contains('/'))
        {
            return Page("tool.html", request);
        }
        if (path.StartsWith("/assets/", StringComparison.Ordinal))
        {
            return Asset(path["/assets/".Length..], request);
        }
        if (path is "/preview/bridge.js" or "/admin/preview/bridge.js")
        {
            return Bytes(WebAssets.Get("assets/preview-bridge.js") ?? [], "application/javascript; charset=utf-8", request);
        }
        if (path.StartsWith("/admin/preview/", StringComparison.Ordinal))
        {
            return Preview(request, path["/admin/preview/".Length..], admin: true);
        }
        if (path.StartsWith("/preview/", StringComparison.Ordinal))
        {
            return Preview(request, path["/preview/".Length..], admin: false);
        }
        return Error(404, "NOT_FOUND", "页面不存在");
    }

    private SourceHttpResponse Meta() => Json(200, new MetaResponse
    {
        Service = "QTools 工具源",
        ApiVersion = "v1",
        Version = SourceLimits.ServiceVersion,
        PackageFormat = ".qtools",
        MaxPackageBytes = SourceLimits.MaxPackageBytes,
    });

    private SourceHttpResponse PublicTools(SourceHttpRequest request)
    {
        var query = request.Query.GetValueOrDefault("q")?.Trim().ToLowerInvariant() ?? "";
        var category = request.Query.GetValueOrDefault("category")?.Trim().ToLowerInvariant() ?? "";
        var sort = request.Query.GetValueOrDefault("sort")?.Trim().ToLowerInvariant() ?? "";
        var page = PositiveInt(request.Query.GetValueOrDefault("page"), 1);
        var pageSize = Math.Clamp(PositiveInt(request.Query.GetValueOrDefault("pageSize"), 24), 1, 100);
        var all = _store.Catalog(includeUnpublished: false);
        var ranks = DownloadRanks(all);
        var categories = all.Where(tool => !string.IsNullOrEmpty(tool.Category)).Select(tool => tool.Category!)
            .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var filtered = all.Where(tool =>
            (category.Length == 0 || string.Equals(tool.Category, category, StringComparison.OrdinalIgnoreCase)) &&
            (query.Length == 0 || $"{tool.Name} {tool.Description} {tool.Id} {tool.Category}".ToLowerInvariant().Contains(query, StringComparison.Ordinal)))
            .ToArray();
        filtered = sort switch
        {
            "downloads" => filtered.OrderByDescending(tool => tool.Downloads).ThenBy(tool => tool.Name, StringComparer.Ordinal).ToArray(),
            "updated" => filtered.OrderByDescending(tool => tool.UpdatedAtUtc).ThenBy(tool => tool.Name, StringComparer.Ordinal).ToArray(),
            "name" => filtered.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray(),
            _ => filtered,
        };
        var total = filtered.Length;
        var start = page - 1 > total / pageSize ? total : (page - 1) * pageSize;
        var items = filtered.Skip(start).Take(pageSize).Select(tool => PublicView(tool, null, ranks, all.Count)).ToArray();
        return Json(200, new CatalogResponse { Items = items, Total = total, Page = page, PageSize = pageSize, Categories = categories });
    }

    private SourceHttpResponse PublicTool(SourceHttpRequest request, string id)
    {
        CatalogTool tool;
        try { tool = _store.GetTool(id); }
        catch (KeyNotFoundException) { return Error(404, "TOOL_NOT_FOUND", "工具不存在或未公开"); }
        var version = request.Query.GetValueOrDefault("version") ?? "";
        if (version.Length > 0 && SourceStore.FindRevision(tool, version) is null)
        {
            return Error(404, "VERSION_NOT_FOUND", "版本不存在");
        }
        var revision = SourceStore.FindRevision(tool, version) ?? SourceStore.CurrentRevision(tool);
        var published = _store.Catalog(includeUnpublished: false);
        return Json(200, new ToolDetailResponse(PublicView(tool, revision, DownloadRanks(published), published.Count), revision.Manifest));
    }

    private SourceHttpResponse PublicPackage(SourceHttpRequest request, string id)
    {
        CatalogTool tool;
        try { tool = _store.GetTool(id); }
        catch (KeyNotFoundException) { return Error(404, "TOOL_NOT_FOUND", "工具不存在或未公开"); }
        var version = request.Query.GetValueOrDefault("version") ?? "";
        var revision = SourceStore.FindRevision(tool, version);
        if (revision is null) { return Error(404, "VERSION_NOT_FOUND", "版本不存在"); }
        var submission = _store.GetSubmission(revision.SubmissionId);
        var package = submission.PackagePath;
        if (!File.Exists(package)) { return Error(404, "PACKAGE_NOT_FOUND", "安装包不存在"); }
        if (request.Method == "GET") { _store.RecordDownload(tool.Id); }
        return new SourceHttpResponse
        {
            StatusCode = 200,
            ContentType = "application/zip",
            Body = File.ReadAllBytes(package),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Disposition"] = $"attachment; filename=\"{tool.Id}-{revision.Version}.qtools\"",
                ["X-QTools-SHA256"] = revision.Sha256,
            },
        };
    }

    private SourceHttpResponse CreateSubmission(SourceHttpRequest request, CancellationToken cancellationToken)
    {
        if (!_limiter.Allow("submit:" + request.RemoteIp, 10, TimeSpan.FromHours(1)))
        {
            return Error(429, "RATE_LIMITED", "提交过于频繁，请稍后再试");
        }
        byte[] package;
        try { package = MultipartPackage(request); }
        catch (InvalidDataException exception)
        {
            return Error(400, "PACKAGE_REQUIRED" == exception.Message ? "PACKAGE_REQUIRED" : "PACKAGE_INVALID", exception.Message);
        }
        if (package.Length is 0 or > SourceLimits.MaxPackageBytes)
        {
            return Error(400, "PACKAGE_SIZE", "安装包大小必须在 1 字节~8 MiB 之间");
        }

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var temporary = Path.Combine(_store.DataDirectory, "upload-" + id + ".qtools");
        var destination = _store.ExtractPath(id);
        try
        {
            File.WriteAllBytes(temporary, package);
            var content = PackageValidator.ValidateAndExtract(package, destination);
            var final = _store.PackagePath(id);
            File.Move(temporary, final);
            var submission = new Submission
            {
                Id = id,
                Status = "pending",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                PackagePath = final,
                ExtractPath = destination,
                Sha256 = content.Sha256,
                SizeBytes = content.Size,
                Kind = content.Export.Kind,
                WebUrl = content.Web?.Url,
                Export = content.Export,
                Manifest = content.Manifest,
                Warnings = content.Warnings,
            };
            if (!_store.AddSubmission(submission))
            {
                File.Delete(final);
                if (Directory.Exists(destination)) { Directory.Delete(destination, recursive: true); }
                return Error(500, "INTERNAL_ERROR", "保存提交记录失败");
            }
            return Json(202, new SubmitResponse
            {
                Id = id,
                Status = "pending",
                StatusUrl = "/api/v1/submissions/" + id,
                ToolId = submission.Manifest.Id,
                ToolName = submission.Manifest.Name,
                Version = submission.Manifest.Version,
            });
        }
        catch (InvalidDataException exception)
        {
            return Error(400, "PACKAGE_INVALID", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Error(500, "INTERNAL_ERROR", "保存安装包失败");
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }

    private SourceHttpResponse PublicStatus(string id)
    {
        Submission submission;
        try { submission = _store.GetSubmission(id); }
        catch (KeyNotFoundException) { return Error(404, "SUBMISSION_NOT_FOUND", "提交记录不存在"); }
        return Json(200, new PublicStatus
        {
            Id = submission.Id,
            Status = submission.Status,
            CreatedAtUtc = submission.CreatedAtUtc,
            ReviewedAtUtc = submission.ReviewedAtUtc,
            ReviewNote = submission.ReviewNote,
            ToolId = submission.Manifest.Id,
            ToolName = submission.Manifest.Name,
            Version = submission.Manifest.Version,
        });
    }

    private SourceHttpResponse Session(SourceHttpRequest request)
    {
        var token = SourceAuth.ReadCookie(request, SourceAuth.CookieName);
        var session = token is null ? null : _auth.Validate(token);
        return Json(200, new AdminSession { Authenticated = session is not null, CsrfToken = session?.CsrfToken });
    }

    private SourceHttpResponse Login(SourceHttpRequest request)
    {
        if (!_limiter.Allow("login:" + request.RemoteIp, 5, TimeSpan.FromMinutes(1)))
        {
            return Error(429, "RATE_LIMITED", "登录尝试过于频繁");
        }
        LoginRequest? input;
        try { input = DecodeJson<LoginRequest>(request.Body, 1024); }
        catch (JsonException) { return Error(400, "BAD_REQUEST", "请输入管理员密码"); }
        if (string.IsNullOrEmpty(input?.Password)) { return Error(400, "BAD_REQUEST", "请输入管理员密码"); }
        try
        {
            var (token, csrf) = _auth.Login(input.Password);
            return Json(200, new AdminSession { Authenticated = true, CsrfToken = csrf }, SourceAuth.BuildCookie(token, _secureCookie));
        }
        catch (UnauthorizedAccessException) { return Error(401, "LOGIN_FAILED", "管理员密码错误"); }
    }

    private SourceHttpResponse Logout(SourceHttpRequest request)
    {
        var token = SourceAuth.ReadCookie(request, SourceAuth.CookieName);
        if (token is not null) { _auth.Logout(token); }
        return Json(200, new OkResponse(true), SourceAuth.BuildCookie("", _secureCookie, clear: true));
    }

    private AuthSession? RequireAdmin(SourceHttpRequest request)
    {
        var token = SourceAuth.ReadCookie(request, SourceAuth.CookieName);
        return token is null ? null : _auth.Validate(token);
    }

    private SourceHttpResponse AdminSubmission(string id)
    {
        Submission submission;
        try { submission = _store.GetSubmission(id); }
        catch (KeyNotFoundException) { return Error(404, "SUBMISSION_NOT_FOUND", "提交记录不存在"); }
        return Json(200, submission);
    }

    private SourceHttpResponse Review(string id, SourceHttpRequest request)
    {
        ReviewRequest? input;
        try { input = DecodeJson<ReviewRequest>(request.Body, 2048); }
        catch (JsonException) { return Error(400, "BAD_REQUEST", "审核参数不合法"); }
        if (TextUtility.RuneCount(input?.Note ?? "") > 500) { return Error(400, "BAD_REQUEST", "审核参数不合法"); }
        try
        {
            switch (input?.Action)
            {
                case "approve": _store.Approve(id, input.Note); break;
                case "reject": _store.Reject(id, input.Note); break;
                default: return Error(400, "ACTION_INVALID", "审核动作必须是 approve 或 reject");
            }
            return Json(200, new OkResponse(true));
        }
        catch (KeyNotFoundException) { return Error(404, "SUBMISSION_NOT_FOUND", "提交记录不存在"); }
        catch (InvalidOperationException exception) { return Error(409, "REVIEW_CONFLICT", exception.Message); }
    }

    private SourceHttpResponse SetPublished(string id, bool published)
    {
        try { _store.SetPublished(id, published); }
        catch (KeyNotFoundException) { return Error(404, "TOOL_NOT_FOUND", "工具不存在"); }
        return Json(200, new OkResponse(true));
    }

    private SourceHttpResponse UpdateToolMetadata(string id, SourceHttpRequest request)
    {
        ToolMetadataRequest? input;
        try { input = DecodeJson<ToolMetadataRequest>(request.Body, 8192); }
        catch (JsonException) { return Error(400, "BAD_REQUEST", "工具信息参数不合法"); }
        if (input is null) { return Error(400, "BAD_REQUEST", "工具信息参数不合法"); }
        if (input.Description is { } description && TextUtility.RuneCount(description) > 500)
        {
            return Error(400, "BAD_REQUEST", "描述不能超过 500 个字符");
        }
        if (input.Category is { } category && TextUtility.RuneCount(category) > 40)
        {
            return Error(400, "BAD_REQUEST", "分类不能超过 40 个字符");
        }
        try
        {
            return Json(200, _store.UpdateMetadata(id, input.Description, input.Category, input.Pinned));
        }
        catch (KeyNotFoundException) { return Error(404, "TOOL_NOT_FOUND", "工具不存在"); }
    }

    private SourceHttpResponse Preview(SourceHttpRequest request, string rest, bool admin)
    {
        var slash = rest.IndexOf('/');
        var id = slash < 0 ? rest : rest[..slash];
        var relative = slash < 0 || slash == rest.Length - 1 ? null : rest[(slash + 1)..];
        if (relative is null)
        {
            var response = PreviewService.Serve(_store, _auth, request, id, null, admin);
            if (response is null) { return Error(404, "NOT_FOUND", "预览不存在"); }
            var submission = _store.GetSubmission(id);
            return submission.Manifest.Entry == "index.html" ? response : SourceHttpResponse.Redirect(submission.Manifest.Entry);
        }
        return PreviewService.Serve(_store, _auth, request, id, relative, admin) ?? Error(404, "NOT_FOUND", "预览不存在");
    }

    private SourceHttpResponse Page(string name, SourceHttpRequest request) =>
        Bytes(WebAssets.Get(name) ?? [], "text/html; charset=utf-8", request);

    private SourceHttpResponse Asset(string relative, SourceHttpRequest request)
    {
        if (!PackageValidator.IsSafeRelativePath(relative)) { return Error(404, "NOT_FOUND", "资源不存在"); }
        var data = WebAssets.Get("assets/" + relative);
        return data is null ? Error(404, "NOT_FOUND", "资源不存在") : Bytes(data, WebAssets.ContentType(relative), request);
    }

    private static PublicTool PublicView(CatalogTool tool, Revision? selected, IReadOnlyDictionary<string, int> ranks, int toolCount)
    {
        var revision = selected ?? SourceStore.CurrentRevision(tool);
        return new PublicTool
        {
            Id = tool.Id,
            Name = revision.Manifest.Name,
            Version = revision.Version,
            Kind = tool.Kind,
            Url = tool.WebUrl,
            Description = tool.Description ?? revision.Manifest.Description,
            Category = tool.Category ?? revision.Manifest.Category,
            Glyph = tool.Glyph ?? revision.Manifest.Glyph,
            Permissions = revision.Manifest.Permissions,
            SizeBytes = revision.SizeBytes,
            Sha256 = revision.Sha256,
            UpdatedAtUtc = tool.UpdatedAtUtc,
            PreviewUrl = tool.Kind == "web" ? "" : "/preview/" + revision.SubmissionId + "/",
            DownloadUrl = "/api/v1/tools/" + tool.Id + "/package",
            Versions = tool.Versions.Select(item => item.Version).ToArray(),
            Pinned = tool.Pinned,
            Downloads = tool.Downloads,
            Rank = ranks.TryGetValue(tool.Id, out var rank) ? rank : 0,
            ToolCount = toolCount,
        };
    }

    private static Dictionary<string, int> DownloadRanks(IReadOnlyList<CatalogTool> tools)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = tools
            .OrderByDescending(tool => tool.Downloads)
            .ThenByDescending(tool => tool.UpdatedAtUtc)
            .ThenBy(tool => tool.Name, StringComparer.Ordinal);
        var index = 0;
        foreach (var tool in ordered) { ranks[tool.Id] = ++index; }
        return ranks;
    }

    private static int PositiveInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : fallback;

    private static T? DecodeJson<T>(byte[] body, int limit) where T : class
    {
        if (body.Length > limit) { throw new JsonException("请求体过大"); }
        return JsonHelper.Deserialize<T>(body);
    }

    private static byte[] MultipartPackage(SourceHttpRequest request)
    {
        if (!request.Headers.TryGetValue("Content-Type", out var contentType) ||
            !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("请使用 multipart/form-data 上传 .qtools 文件");
        }
        var boundary = MultipartBoundary(contentType);
        var marker = Encoding.ASCII.GetBytes("--" + boundary);
        byte[]? package = null;
        var position = 0;
        while (position < request.Body.Length)
        {
            var delimiter = Find(request.Body, marker, position);
            if (delimiter < 0) { break; }
            var after = delimiter + marker.Length;
            if (after + 2 <= request.Body.Length && request.Body.AsSpan(after, 2).SequenceEqual("--"u8)) { break; }
            if (after + 2 > request.Body.Length || !request.Body.AsSpan(after, 2).SequenceEqual("\r\n"u8)) { return InvalidMultipart(); }
            var headerEnd = Find(request.Body, "\r\n\r\n"u8, after + 2);
            if (headerEnd < 0 || headerEnd - after > 32 * 1024) { return InvalidMultipart(); }
            var headerText = Encoding.Latin1.GetString(request.Body, after + 2, headerEnd - after - 2);
            var dataStart = headerEnd + 4;
            var delimiterStart = Find(request.Body, marker, dataStart);
            if (delimiterStart < 0) { return InvalidMultipart(); }
            var dataEnd = delimiterStart;
            if (dataEnd >= 2 && request.Body.AsSpan(dataEnd - 2, 2).SequenceEqual("\r\n"u8)) { dataEnd -= 2; }
            var disposition = HeaderValue(headerText, "Content-Disposition");
            if (disposition.StartsWith("form-data", StringComparison.OrdinalIgnoreCase) &&
                HeaderParameter(disposition, "name") == "package")
            {
                if (package is not null) { throw new InvalidDataException("只能上传一个 package 文件"); }
                var length = dataEnd - dataStart;
                if (length is < 0 or > SourceLimits.MaxPackageBytes) { throw new InvalidDataException("安装包大小必须在 1 字节~8 MiB 之间"); }
                package = request.Body[dataStart..dataEnd].ToArray();
            }
            position = delimiterStart + marker.Length;
        }
        return package ?? throw new InvalidDataException("PACKAGE_REQUIRED");
    }

    private static string MultipartBoundary(string contentType)
    {
        foreach (var part in contentType.Split(';').Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals < 0 || !part[..equals].Trim().Equals("boundary", StringComparison.OrdinalIgnoreCase)) { continue; }
            var value = part[(equals + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') { value = value[1..^1]; }
            if (value.Length is < 1 or > 200 || value.Any(character => character is < ' ' or > '\u007e')) { break; }
            return value;
        }
        throw new InvalidDataException("multipart boundary 不合法");
    }

    private static string HeaderValue(string headers, string name)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) { return line[(colon + 1)..].Trim(); }
        }
        return "";
    }

    private static string? HeaderParameter(string value, string name)
    {
        foreach (var part in value.Split(';').Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals > 0 && part[..equals].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(equals + 1)..].Trim().Trim('"');
            }
        }
        return null;
    }

    private static int Find(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value, int start)
    {
        var index = data[start..].IndexOf(value);
        return index < 0 ? -1 : start + index;
    }

    private static byte[] InvalidMultipart() => throw new InvalidDataException("multipart 数据不完整或不合法");

    private static SourceHttpResponse Read(SourceHttpRequest request, Func<SourceHttpResponse> handler) =>
        request.Method is "GET" or "HEAD" ? handler() : Error(405, "METHOD_NOT_ALLOWED", "该接口仅支持 GET 或 HEAD");

    private static SourceHttpResponse Post(SourceHttpRequest request, Func<SourceHttpResponse> handler) =>
        request.Method == "POST" ? handler() : Error(405, "METHOD_NOT_ALLOWED", "该接口仅支持 POST");

    private SourceHttpResponse AuthorizedPost(SourceHttpRequest request, AuthSession session, Func<SourceHttpResponse> handler)
    {
        if (request.Method != "POST") { return Error(405, "METHOD_NOT_ALLOWED", "该接口仅支持 POST"); }
        if (!SourceAuth.ValidOrigin(request) ||
            !request.Headers.TryGetValue("X-CSRF-Token", out var csrf) ||
            !TextUtility.ConstantTimeEquals(csrf, session.CsrfToken))
        {
            return Error(403, "CSRF_INVALID", "管理请求校验失败");
        }
        return handler();
    }

    private static SourceHttpResponse Json(int status, object value, string? cookie = null) => new()
    {
        StatusCode = status,
        ContentType = "application/json; charset=utf-8",
        Body = JsonHelper.SerializeRuntime(value),
        Headers = cookie is null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) :
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Set-Cookie"] = cookie },
    };

    private static SourceHttpResponse Bytes(byte[] data, string contentType, SourceHttpRequest request) => new()
    {
        StatusCode = 200,
        ContentType = contentType,
        Body = data,
    };

    private static SourceHttpResponse Error(int status, string code, string message) =>
        Json(status, new ApiErrorResponse(new ApiError(code, message)));
}
