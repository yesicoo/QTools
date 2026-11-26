using System.Reflection;
using System.Text.RegularExpressions;

namespace QTools.Source;

public static class WebAssets
{
    private const string ResourcePrefix = "Web/";
    private static readonly Lazy<Dictionary<string, byte[]>> Files = new(Load);

    public static byte[]? Get(string relative)
    {
        relative = relative.TrimStart('/').Replace('\\', '/');
        return Files.Value.TryGetValue(relative, out var data) ? data : null;
    }

    public static string ContentType(string relative) => Path.GetExtension(relative).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static Dictionary<string, byte[]> Load()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) { continue; }
            var relative = name[ResourcePrefix.Length..].Replace('\\', '/');
            result[relative] = name;
        }
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var (relative, resourceName) in result)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var output = new MemoryStream();
            stream.CopyTo(output);
            files[relative] = output.ToArray();
        }
        return files;
    }
}

public static partial class PreviewService
{
    [GeneratedRegex(@"(?i)<head(?:\s[^>]*)?>")]
    private static partial Regex HeadPattern();

    public static SourceHttpResponse? Serve(SourceStore store, SourceAuth auth, SourceHttpRequest request, string id, string? relative, bool admin)
    {
        if (id.Length != 24 || !id.All(char.IsAsciiHexDigit)) { return null; }
        if (admin)
        {
            var token = SourceAuth.ReadCookie(request, SourceAuth.CookieName);
            if (token is null || auth.Validate(token) is null) { return null; }
        }

        Submission submission;
        try { submission = store.GetSubmission(id); }
        catch (KeyNotFoundException) { return null; }
        if (!admin)
        {
            if (submission.Status != "approved") { return null; }
            try
            {
                var tool = store.GetTool(submission.Manifest.Id);
                if (!tool.Published || SourceStore.FindRevision(tool, submission.Manifest.Version)?.SubmissionId != id) { return null; }
            }
            catch (KeyNotFoundException) { return null; }
        }

        relative ??= submission.Manifest.Entry;
        if (!PackageValidator.IsSafeRelativePath(relative)) { return null; }
        var root = Path.GetFullPath(submission.ExtractPath);
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, comparison)) { return null; }
        if (!File.Exists(target) || new FileInfo(target).Length > SourceLimits.MaxPackageBytes) { return null; }

        var data = File.ReadAllBytes(target);
        var response = new SourceHttpResponse
        {
            StatusCode = 200,
            ContentType = WebAssets.ContentType(relative),
            Body = data,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Security-Policy-Sandbox"] = "allow-scripts",
                ["Content-Security-Policy"] = PreviewCsp(submission.Manifest.AllowEval),
            },
        };
        if (Path.GetExtension(relative) is ".html" or ".htm")
        {
            var prefix = admin ? "/admin/preview/" : "/preview/";
            var script = $"<script src=\"{System.Text.Encodings.Web.HtmlEncoder.Default.Encode(prefix + "bridge.js?sid=" + id)}\"></script>";
            var html = System.Text.Encoding.UTF8.GetString(data);
            html = HeadPattern().Replace(html, match => match.Value + script);
            if (html == System.Text.Encoding.UTF8.GetString(data)) { html = script + html; }
            response = response with
            {
                ContentType = "text/html; charset=utf-8",
                Body = System.Text.Encoding.UTF8.GetBytes(html),
            };
        }
        return response;
    }

    public static SourceHttpResponse Entry(SourceStore store, SourceAuth auth, SourceHttpRequest request, string id, bool admin)
    {
        var response = Serve(store, auth, request, id, null, admin);
        if (response is not null) { return response; }
        if (id.Length != 24) { return NotFound(); }
        Submission submission;
        try { submission = store.GetSubmission(id); }
        catch (KeyNotFoundException) { return NotFound(); }
        var preview = Serve(store, auth, request, id, submission.Manifest.Entry, admin);
        if (preview is not null) { return preview; }
        if (submission.Manifest.Entry != "index.html" && admin)
        {
            var token = SourceAuth.ReadCookie(request, SourceAuth.CookieName);
            if (token is not null && auth.Validate(token) is not null)
            {
                return SourceHttpResponse.Redirect(submission.Manifest.Entry);
            }
        }
        return NotFound();
    }

    private static SourceHttpResponse NotFound() => new() { StatusCode = 404, ContentType = "text/plain; charset=utf-8", Body = [] };

    private static string PreviewCsp(bool allowEval)
    {
        var script = "script-src 'self' https: 'unsafe-inline'" + (allowEval ? " 'unsafe-eval'" : "");
        return "sandbox allow-scripts; default-src 'self' https: data: blob:; " + script +
               "; style-src 'self' https: 'unsafe-inline'; img-src 'self' https: data: blob:; font-src 'self' https: data:; " +
               "media-src 'self' https: data: blob:; connect-src 'self' https: wss:; frame-src https:; worker-src 'self' blob:; " +
               "object-src 'none'; base-uri 'none'; form-action 'none'";
    }
}
