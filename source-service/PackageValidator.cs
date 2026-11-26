using System;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QTools.Source;

public sealed record PackageContent(
    ExportManifest Export,
    ToolManifest Manifest,
    WebToolInfo? Web,
    string Html,
    string Sha256,
    long Size,
    IReadOnlyList<Warning> Warnings);

public static partial class PackageValidator
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?i)\beval\s*\(|\bnew\s+Function\s*\(|\bimport\s*\(")]
    private static partial Regex DynamicCodePattern();

    [GeneratedRegex(@"(?i)sk-[A-Za-z0-9_-]{16,}|\b(?:api[_-]?key|apikey|access[_-]?token)\b\s*[:=]\s*[""'][^""']{12,}[""']|\bAKIA[0-9A-Z]{16}\b|\bbearer\s+[A-Za-z0-9._-]{20,}")]
    private static partial Regex HardcodedKeyPattern();

    [GeneratedRegex(@"(?i)<script[^>]+src\s*=\s*[""']?\s*(?:https?:)?//")]
    private static partial Regex RemoteScriptPattern();

    [GeneratedRegex(@"(?i)\bfetch\s*\(|\bXMLHttpRequest\b|\bWebSocket\s*\(|\bsendBeacon\s*\(|<iframe[^>]+src\s*=\s*[""']?\s*(?:https?:)?//")]
    private static partial Regex NetworkPattern();

    [GeneratedRegex(@"(?i)\bdocument\.write\s*\(|\bsrcdoc\s*=")]
    private static partial Regex DocumentWritePattern();

    public static PackageContent ValidateAndExtract(byte[] package, string destination)
    {
        if (package.Length is 0 or > SourceLimits.MaxPackageBytes)
        {
            throw new InvalidDataException("安装包大小必须在 1 字节~8 MiB 之间");
        }

        using var input = new MemoryStream(package, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count > SourceLimits.MaxPackageFiles)
        {
            throw new InvalidDataException($"导出包条目过多（上限 {SourceLimits.MaxPackageFiles}）");
        }
        var layout = ParseZipLayout(package);
        if (layout.Entries.Count != archive.Entries.Count)
        {
            throw new InvalidDataException("导出包中央目录与条目数量不一致");
        }

        ExportManifest? export = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[81920];
        long declaredTotal = 0;
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            var rawName = entry.FullName;
            if (!names.Add(rawName))
            {
                throw new InvalidDataException($"导出包包含重复条目：{rawName}");
            }
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new InvalidDataException("导出包不能包含符号链接");
            }
            var name = rawName.TrimEnd('/');
            if (!IsSafePackagePath(name, allowExportRoot: true))
            {
                throw new InvalidDataException($"非法路径条目：{rawName}");
            }
            var isDirectory = rawName.EndsWith('/') || string.IsNullOrEmpty(entry.Name);
            if (isDirectory) { continue; }
            if (entry.Length > SourceLimits.MaxPackageBytes)
            {
                throw new InvalidDataException($"单个文件过大：{name}");
            }
            declaredTotal += entry.Length;
            if (declaredTotal > SourceLimits.MaxExtractedBytes)
            {
                throw new InvalidDataException("导出包解压后体积过大");
            }

            if (name == "qtools-export.json")
            {
                const long maxManifestBytes = 64 * 1024;
                if (entry.Length > maxManifestBytes) { throw new InvalidDataException("导出清单过大"); }
                using var content = new MemoryStream(capacity: (int)entry.Length);
                var written = ExtractEntry(package, layout.Entries[index], layout.CentralDirectoryOffset,
                    content, buffer, name, maxManifestBytes);
                if (written != layout.Entries[index].UncompressedLength)
                {
                    throw new InvalidDataException("导出清单实际解压长度与中央目录不一致");
                }
                export = JsonHelper.Deserialize<ExportManifest>(content.ToArray());
            }
        }

        if (export is null || export.SchemaVersion != 1 || !IsToolId(export.Id) ||
            (export.Kind != "tool" && export.Kind != "web"))
        {
            throw new InvalidDataException("仅支持 schemaVersion=1 的小工具或在线工具导出包");
        }

        if (export.Kind == "web")
        {
            return ValidateWebPackage(package, archive, layout, export, buffer);
        }

        var prefix = "tools/" + export.Id + "/";
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            if (name != "qtools-export.json" && !name.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"导出包包含非本工具文件：{entry.FullName}");
            }

            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                var relative = name[prefix.Length..];
                var firstSegment = relative.Split('/', 2)[0];
                if (firstSegment.Equals("files", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("提交包不能包含 files 用户数据");
                }
            }
        }

        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".extract-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(staging);
        try
        {
            long extractedTotal = 0;
            var lengthMismatch = false;
            for (var index = 0; index < archive.Entries.Count; index++)
            {
                var entry = archive.Entries[index];
                var name = entry.FullName.TrimEnd('/');
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name)) { continue; }
                var relative = name[prefix.Length..];
                var target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var written = ExtractEntry(package, layout.Entries[index], layout.CentralDirectoryOffset,
                    output, buffer, relative, SourceLimits.MaxPackageBytes);
                extractedTotal += written;
                if (extractedTotal > SourceLimits.MaxExtractedBytes)
                {
                    throw new InvalidDataException("导出包解压后体积过大");
                }
                lengthMismatch |= written != layout.Entries[index].UncompressedLength;
            }
            if (lengthMismatch)
            {
                throw new InvalidDataException("导出包实际解压长度与中央目录不一致");
            }

            var manifestPath = Path.Combine(staging, "manifest.json");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 64 * 1024)
            {
                throw new InvalidDataException("导出包缺少 manifest.json 或清单过大");
            }
            var manifest = JsonHelper.Deserialize<ToolManifest>(File.ReadAllBytes(manifestPath))
                ?? throw new InvalidDataException("manifest.json 解析失败");
            ValidateManifest(export, manifest);
            if (!IsSafeRelativePath(manifest.Entry))
            {
                throw new InvalidDataException("工具入口路径不合法");
            }

            var entryPath = Path.Combine(staging, manifest.Entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(entryPath) || new FileInfo(entryPath).Length > SourceLimits.MaxPackageBytes)
            {
                throw new InvalidDataException($"缺少入口文件 {manifest.Entry} 或文件过大");
            }
            var html = File.ReadAllText(entryPath);
            var warnings = Scan(manifest, html);
            var result = new PackageContent(
                export, manifest, null, html, Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
                package.LongLength, warnings);
            if (Directory.Exists(destination)) { Directory.Delete(destination, recursive: true); }
            Directory.Move(staging, destination);
            return result;
        }
        finally
        {
            if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
        }
    }

    private static PackageContent ValidateWebPackage(
        byte[] package, ZipArchive archive, ZipLayout layout, ExportManifest export, byte[] buffer)
    {
        var prefix = "webtools/" + export.Id + "/";
        var webEntryName = prefix + "web.json";
        var webIndex = -1;
        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            var name = entry.FullName.TrimEnd('/');
            if (string.IsNullOrEmpty(entry.Name)) { continue; }
            if (name == "qtools-export.json")
            {
                continue;
            }
            if (!string.Equals(name, webEntryName, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"在线工具导出包只能包含 {webEntryName}");
            }
            webIndex = index;
        }

        if (webIndex < 0) { throw new InvalidDataException("在线工具导出包缺少 web.json"); }
        const long maxWebBytes = 64 * 1024;
        if (archive.Entries[webIndex].Length > maxWebBytes) { throw new InvalidDataException("web.json 过大"); }

        using var content = new MemoryStream(capacity: (int)archive.Entries[webIndex].Length);
        var written = ExtractEntry(package, layout.Entries[webIndex], layout.CentralDirectoryOffset,
            content, buffer, "web.json", maxWebBytes);
        if (written != layout.Entries[webIndex].UncompressedLength)
        {
            throw new InvalidDataException("web.json 实际解压长度与中央目录不一致");
        }

        var web = JsonHelper.Deserialize<WebToolInfo>(content.ToArray())
            ?? throw new InvalidDataException("web.json 解析失败");
        ValidateWebManifest(export, web);
        var manifest = new ToolManifest
        {
            Id = web.Id,
            Name = web.Name,
            Version = web.Version,
            Description = web.Description,
            Entry = "",
            Permissions = [],
            CreatedAt = web.CreatedAt,
            Category = web.Category,
            Glyph = web.Glyph,
        };
        var warnings = new List<Warning>
        {
            new() { Level = "info", Code = "WEB_EXTERNAL", Message = "在线工具仅保存外部网址，源站无法审查目标网站内容" },
        };
        return new PackageContent(
            export, manifest, web, "", Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
            package.LongLength, warnings);
    }

    private static void ValidateWebManifest(ExportManifest export, WebToolInfo web)
    {
        if (string.IsNullOrEmpty(web.Id) || web.Id != export.Id || !IsToolId(web.Id))
        {
            throw new InvalidDataException("web.json 的 id 与导出清单不一致");
        }
        if (string.IsNullOrWhiteSpace(web.Name) || TextUtility.RuneCount(web.Name.Trim()) > 60)
        {
            throw new InvalidDataException("在线工具名称必填且不超过 60 字符");
        }
        if (string.IsNullOrEmpty(web.Version) || !VersionPattern().IsMatch(web.Version) ||
            !string.Equals(web.Version, export.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("在线工具版本必须是语义化版本，并与导出清单一致");
        }
        if (TextUtility.RuneCount(web.Description ?? "") > 300 ||
            TextUtility.RuneCount(web.Category ?? "") > 30 ||
            TextUtility.RuneCount(web.Glyph ?? "") > 4)
        {
            throw new InvalidDataException("在线工具描述、分类或图标超出长度限制");
        }
        var url = web.Url?.Trim() ?? "";
        if (url.Length is 0 or > 2048 || url.Any(char.IsControl) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("在线工具网址必须是合法的 http(s) 地址，且不能包含账号信息");
        }
        if (!DateTimeOffset.TryParse(web.CreatedAt, out _))
        {
            throw new InvalidDataException("在线工具创建时间不合法");
        }
    }

    private static long ExtractEntry(byte[] package, ZipEntryLayout entry, int centralOffset,
        Stream output, byte[] buffer, string relative, long maxBytes)
    {
        if ((entry.Flags & 1) != 0) { throw new InvalidDataException("导出包不能包含加密条目"); }
        if (entry.Method is not (0 or 8)) { throw new InvalidDataException($"不支持的压缩方法：{entry.Name}"); }
        if (entry.LocalOffset > package.Length - 30 || ReadU32(package, entry.LocalOffset) != 0x04034B50)
        {
            throw new InvalidDataException("导出包本地文件头不合法");
        }
        var flags = ReadU16(package, entry.LocalOffset + 6);
        var method = ReadU16(package, entry.LocalOffset + 8);
        var nameLength = ReadU16(package, entry.LocalOffset + 26);
        var extraLength = ReadU16(package, entry.LocalOffset + 28);
        var dataOffset = entry.LocalOffset + 30 + nameLength + extraLength;
        if (flags != entry.Flags || method != entry.Method || dataOffset > package.Length ||
            !package.AsSpan(entry.LocalOffset + 30, nameLength).SequenceEqual(entry.NameBytes))
        {
            throw new InvalidDataException("导出包本地文件头与中央目录不一致");
        }
        if (entry.CompressedLength > package.Length - dataOffset || dataOffset + entry.CompressedLength > centralOffset)
        {
            throw new InvalidDataException("导出包压缩数据范围不合法");
        }

        using var compressed = new MemoryStream(package, dataOffset, entry.CompressedLength, writable: false);
        using Stream source = method == 8 ? new DeflateStream(compressed, CompressionMode.Decompress) : compressed;
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > maxBytes)
            {
                throw new InvalidDataException($"单个文件过大：{relative}");
            }
            output.Write(buffer, 0, read);
        }
        return written;
    }

    private sealed record ZipEntryLayout(
        ushort Flags,
        ushort Method,
        int CompressedLength,
        long UncompressedLength,
        int LocalOffset,
        byte[] NameBytes,
        string Name);

    private sealed record ZipLayout(int CentralDirectoryOffset, IReadOnlyList<ZipEntryLayout> Entries);

    private static ZipLayout ParseZipLayout(byte[] package)
    {
        var end = -1;
        for (var i = package.Length - 22; i >= Math.Max(0, package.Length - 65557); i--)
        {
            if (ReadU32(package, i) != 0x06054B50) { continue; }
            if (ReadU16(package, i + 20) <= package.Length - i - 22) { end = i; break; }
        }
        if (end < 0)
        {
            throw new InvalidDataException("ZIP 结束目录不合法");
        }
        if (ReadU16(package, end + 4) != 0 || ReadU16(package, end + 6) != 0)
        {
            throw new InvalidDataException("不支持多磁盘或损坏的 ZIP 目录");
        }        var diskCount = ReadU16(package, end + 8);
        var count = ReadU16(package, end + 10);
        var centralOffset = ReadU32(package, end + 16);
        if (diskCount is not (0 or 65535) && diskCount != count)
        {
            throw new InvalidDataException("不支持多磁盘或损坏的 ZIP 目录");
        }
        if (count == 0 || centralOffset > end || count > SourceLimits.MaxPackageFiles)
        {
            throw new InvalidDataException("ZIP 中央目录不合法");
        }
        var entries = new List<ZipEntryLayout>(count);
        var position = checked((int)centralOffset);
        for (var index = 0; index < count; index++)
        {
            if (position > package.Length - 46 || ReadU32(package, position) != 0x02014B50)
            {
                throw new InvalidDataException("ZIP 中央目录条目不合法");
            }
            var flags = ReadU16(package, position + 8);
            var method = ReadU16(package, position + 10);
            var compressed = ReadU32(package, position + 20);
            var uncompressed = ReadU32(package, position + 24);
            var nameLength = ReadU16(package, position + 28);
            var extraLength = ReadU16(package, position + 30);
            var commentLength = ReadU16(package, position + 32);
            var localOffset = ReadU32(package, position + 42);
            var next = position + 46 + nameLength + extraLength + commentLength;
            if (compressed == uint.MaxValue || uncompressed == uint.MaxValue || localOffset == uint.MaxValue ||
                next > end || localOffset >= centralOffset)
            {
                throw new InvalidDataException("不支持 ZIP64 或非法的 ZIP 条目偏移");
            }
            var nameBytes = package.AsSpan(position + 46, nameLength).ToArray();
            entries.Add(new ZipEntryLayout(flags, method, checked((int)compressed), uncompressed,
                checked((int)localOffset), nameBytes, Encoding.UTF8.GetString(nameBytes)));
            position = next;
        }
        if (position != end) { throw new InvalidDataException("ZIP 中央目录长度不一致"); }
        return new ZipLayout(checked((int)centralOffset), entries);
    }

    private static ushort ReadU16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    public static bool IsToolId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 63) { return false; }
        if (!char.IsAsciiLetterOrDigit(value[0]) || !char.IsAsciiLetterOrDigit(value[^1])) { return false; }
        return value.All(character => character is >= 'a' and <= 'z' || character is >= '0' and <= '9' || character == '-');
    }

    private static void ValidateManifest(ExportManifest export, ToolManifest manifest)
    {
        if (string.IsNullOrEmpty(manifest.Id) || manifest.Id != export.Id || !IsToolId(manifest.Id))
        {
            throw new InvalidDataException("manifest.id 与导出清单或目录不一致");
        }
        if (string.IsNullOrWhiteSpace(manifest.Name) || TextUtility.RuneCount(manifest.Name) > 60)
        {
            throw new InvalidDataException("工具名称必填且不超过 60 字");
        }
        if (string.IsNullOrEmpty(manifest.Version) || !VersionPattern().IsMatch(manifest.Version) || manifest.Version != export.Version)
        {
            throw new InvalidDataException("工具版本无效或与导出清单不一致");
        }
        if (TextUtility.RuneCount(manifest.Description ?? "") > 300 ||
            TextUtility.RuneCount(manifest.Category ?? "") > 30 ||
            TextUtility.RuneCount(manifest.Glyph ?? "") > 4)
        {
            throw new InvalidDataException("描述、分类或图标字段超长");
        }
        if (manifest.Window is null || manifest.Window.Width is < 320 or > 3840 || manifest.Window.Height is < 320 or > 3840)
        {
            throw new InvalidDataException("窗口尺寸必须在 320~3840 之间");
        }
        if (string.IsNullOrEmpty(manifest.CreatedAt) || !DateTimeOffset.TryParse(manifest.CreatedAt, out _))
        {
            throw new InvalidDataException("createdAt 必须是 ISO-8601 日期时间");
        }
        if (manifest.Permissions is null)
        {
            throw new InvalidDataException("permissions 不能为 null");
        }
        foreach (var permission in manifest.Permissions)
        {
            if (permission?.Trim().ToLowerInvariant() is not ("storage" or "ai" or "media" or "files"))
            {
                throw new InvalidDataException($"未知权限：{permission}");
            }
        }
    }

    public static bool IsSafeRelativePath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\\') || value.Contains('\0') || value.StartsWith('/')) { return false; }
        var segments = value.Split('/');
        return segments.All(segment => !string.IsNullOrEmpty(segment) && segment is not ("." or "..") && IsSafeSegment(segment));
    }

    private static bool IsSafePackagePath(string value, bool allowExportRoot)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\\') || value.Contains('\0') || value.StartsWith('/') || value.Contains(':')) { return false; }
        if (allowExportRoot && value == "qtools-export.json") { return true; }
        var segments = value.Split('/');
        return segments.All(segment => !string.IsNullOrEmpty(segment) && segment is not ("." or "..") && !segment.StartsWith('.') && IsSafeSegment(segment));
    }

    private static bool IsSafeSegment(string segment)
    {
        if (segment.EndsWith('.') || segment.EndsWith(' ')) { return false; }
        foreach (var character in segment)
        {
            if (character is < ' ' or '<' or '>' or '"' or '|' or '?' or '*' || character == 0x7f) { return false; }
        }
        var name = segment.Split('.')[0].ToUpperInvariant();
        return name is not ("CON" or "PRN" or "AUX" or "NUL") &&
               !(name.StartsWith("COM", StringComparison.Ordinal) && name.Length == 4 && name[3] is >= '1' and <= '9') &&
               !(name.StartsWith("LPT", StringComparison.Ordinal) && name.Length == 4 && name[3] is >= '1' and <= '9');
    }

    private static IReadOnlyList<Warning> Scan(ToolManifest manifest, string html)
    {
        var warnings = new List<Warning>();
        void Add(string level, string code, string message) => warnings.Add(new Warning { Level = level, Code = code, Message = message });
        if (DynamicCodePattern().IsMatch(html)) { Add("high", "DYNAMIC_CODE", "检测到动态执行代码"); }
        if (HardcodedKeyPattern().IsMatch(html)) { Add("high", "HARDCODED_KEY", "检测到疑似硬编码密钥"); }
        if (RemoteScriptPattern().IsMatch(html)) { Add("warning", "REMOTE_SCRIPT", "工具引用了远程脚本"); }
        if (NetworkPattern().IsMatch(html)) { Add("warning", "EXTERNAL_NETWORK", "工具可能访问外部网络或嵌入远程页面"); }
        if (DocumentWritePattern().IsMatch(html)) { Add("warning", "DOCUMENT_WRITE", "工具使用 document.write 或 srcdoc"); }
        if (manifest.AllowEval) { Add("high", "ALLOW_EVAL", "manifest 允许 eval"); }
        if (!string.IsNullOrWhiteSpace(manifest.Csp)) { Add("high", "CSP_OVERRIDE", "manifest 自定义了 CSP；在线预览不会采用该覆盖"); }
        foreach (var permission in manifest.Permissions ?? [])
        {
            switch (permission.Trim().ToLowerInvariant())
            {
                case "ai": Add("warning", "PERMISSION_AI", "安装后可调用宿主 AI（消耗本机配额）"); break;
                case "media": Add("warning", "PERMISSION_MEDIA", "安装后可请求摄像头、麦克风或屏幕捕获"); break;
                case "files": Add("warning", "PERMISSION_FILES", "安装后可读写工具目录内的二进制文件"); break;
            }
        }
        return warnings;
    }
}
