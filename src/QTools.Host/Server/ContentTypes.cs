namespace QTools.Host.Server;

/// <summary>显式 MIME 表：只服务白名单扩展名，未知类型按 application/octet-stream 处理（配合 nosniff 响应头）。</summary>
internal static class ContentTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".bmp"] = "image/bmp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".txt"] = "text/plain; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".wasm"] = "application/wasm",

        // 音频 / 视频 / 文档（工具文件存储 files.* 的回放与下载）
        [".webm"] = "audio/webm",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".opus"] = "audio/ogg",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo",
        [".mkv"] = "video/x-matroska",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".csv"] = "text/csv; charset=utf-8",
    };

    public static string For(string path)
    {
        var extension = Path.GetExtension(path);
        return Map.TryGetValue(extension, out var contentType) ? contentType : "application/octet-stream";
    }
}
