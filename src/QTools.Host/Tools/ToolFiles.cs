using System.Text.Json;
using QTools.Host.Bridge;
using QTools.Host.Logging;

namespace QTools.Host.Tools;

/// <summary>
/// 工具目录内的二进制文件存储（`window.host.files.*`）。
/// <para>
/// 存在的意义：`storage` 只能存 JSON 键值，存不了录音/照片这类二进制产物。
/// 文件落在 <c>tools/{toolId}/files/</c>，并可通过
/// <c>/tools/{toolId}/files/{name}</c> 直接回放（audio/img/video 标签可用）。
/// </para>
/// </summary>
public sealed class ToolFiles
{
    private const long MaxFileBytes = 50L * 1024 * 1024;
    private const long MaxTotalBytesPerTool = 200L * 1024 * 1024;
    private const int MaxNameLength = 128;

    /// <summary>允许保存的扩展名（与静态服务 MIME 白名单对齐）。</summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webm", ".wav", ".mp3", ".ogg", ".m4a", ".aac", ".flac", ".opus",
        ".mp4", ".mov", ".avi", ".mkv",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg",
        ".pdf", ".txt", ".csv", ".json", ".zip",
    };

    private readonly string _filesRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ToolFiles(string toolDirectory)
    {
        _filesRoot = Path.Combine(toolDirectory, "files");
        Directory.CreateDirectory(_filesRoot);
    }

    public string FilesRoot => _filesRoot;

    /// <summary>校验文件名（只允许扁平、安全的名字）。</summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            throw new BridgeException("FILE_NAME_INVALID", $"文件名必填且不超过 {MaxNameLength} 字符");
        }

        if (name.Contains('/') || name.Contains('\\') || name.Contains('\0') || name.Contains(".."))
        {
            throw new BridgeException("FILE_NAME_INVALID", "文件名不能包含路径分隔符或 ..");
        }

        if (name.StartsWith('.') || name.EndsWith('.') || name.EndsWith(' '))
        {
            throw new BridgeException("FILE_NAME_INVALID", "文件名不能以 . 或空格结尾");
        }

        var extension = Path.GetExtension(name);
        if (extension.Length == 0 || !AllowedExtensions.Contains(extension))
        {
            throw new BridgeException(
                "FILE_TYPE_NOT_ALLOWED",
                $"不支持的扩展名 {extension}（允许：{string.Join(" ", AllowedExtensions.OrderBy(e => e, StringComparer.Ordinal))}）");
        }
    }

    private string PathFor(string name) => Path.Combine(_filesRoot, name);

    public async System.Threading.Tasks.Task<SavedFileView> SaveAsync(
        string name,
        byte[] data,
        string? contentType,
        System.Threading.CancellationToken cancellationToken)
    {
        ValidateName(name);

        if (data.Length == 0)
        {
            throw new BridgeException("FILE_EMPTY", "文件内容不能为空");
        }

        if (data.Length > MaxFileBytes)
        {
            throw new BridgeException("FILE_TOO_LARGE", $"单个文件不超过 {MaxFileBytes / 1024 / 1024} MB");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var total = Directory.GetFiles(_filesRoot).Sum(f => new FileInfo(f).Length) - ExistingSize(name);
            if (total + data.Length > MaxTotalBytesPerTool)
            {
                throw new BridgeException(
                    "TOOL_QUOTA_EXCEEDED",
                    $"工具文件总容量不超过 {MaxTotalBytesPerTool / 1024 / 1024} MB");
            }

            var path = PathFor(name);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, data, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);

            var info = new FileInfo(path);
            Log.Info($"已保存工具文件 {name}（{info.Length} 字节）");
            return new SavedFileView(name, info.Length, info.LastWriteTimeUtc);
        }
        catch (IOException ex)
        {
            throw new BridgeException("FILE_IO_ERROR", $"写入文件失败：{ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private long ExistingSize(string name)
    {
        var path = PathFor(name);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public System.Threading.Tasks.Task<IReadOnlyList<SavedFileView>> ListAsync()
    {
        var result = Directory.GetFiles(_filesRoot)
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var info = new FileInfo(f);
                return new SavedFileView(info.Name, info.Length, info.LastWriteTimeUtc);
            })
            .OrderByDescending(f => f.ModifiedUtc)
            .ToArray();

        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<SavedFileView>>(result);
    }

    public async System.Threading.Tasks.Task<FileContent> ReadAsync(string name)
    {
        ValidateName(name);
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            throw new BridgeException("FILE_NOT_FOUND", $"文件不存在：{name}");
        }

        var data = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var mime = QTools.Host.Server.ContentTypes.For(name);
        return new FileContent(name, data, mime);
    }

    public System.Threading.Tasks.Task<bool> DeleteAsync(string name)
    {
        ValidateName(name);
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            return System.Threading.Tasks.Task.FromResult(false);
        }

        try
        {
            File.Delete(path);
            Log.Info($"已删除工具文件 {name}");
            return System.Threading.Tasks.Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Warn($"删除工具文件失败 {name}：{ex.Message}");
            return System.Threading.Tasks.Task.FromResult(false);
        }
    }

    /// <summary>删除整个工具时清理其文件目录。</summary>
    public bool DeleteAll()
    {
        if (!Directory.Exists(_filesRoot))
        {
            return false;
        }

        try
        {
            Directory.Delete(_filesRoot, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"删除工具文件目录失败：{ex.Message}");
            return false;
        }
    }
}

/// <summary>文件列表项 / 保存结果。</summary>
public sealed record SavedFileView(string Name, long SizeBytes, DateTime ModifiedUtc);

/// <summary>文件内容（读取结果；data 由桥接层转 base64）。</summary>
public sealed record FileContent(string Name, byte[] Data, string ContentType);

/// <summary>files.* 桥接方法的载荷。</summary>
public sealed record FilesSavePayload(string Name, string DataBase64, string? ContentType);

/// <summary>manager.importTool 的载荷。</summary>
public sealed record ImportToolPayload(
    string Name,
    string Html,
    string? Description,
    bool AcceptHighSeverity,
    string? Category = null,
    string? Glyph = null);

/// <summary>manager.listVersions 的结果。</summary>
public sealed record ToolVersionView(string Version, string CreatedAt, long SizeBytes, bool IsActive, string? Note);

public sealed record ToolVersionListView(
    string ToolId,
    string ActiveVersion,
    IReadOnlyList<ToolVersionView> Versions);

/// <summary>manager.importTool 的结果。</summary>
public sealed record ImportToolView(
    string ToolId,
    string ToolName,
    string Url,
    bool Saved,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Creator.SecurityFinding> Findings,
    bool HasFatal,
    bool RequiresExplicitAcceptance,
    IReadOnlyList<string> ExternalAssets);

/// <summary>files.* 桥接方法的结果。</summary>
public sealed record FilesSaveResult(string Name, long SizeBytes, string Url);

public sealed record FilesListResult(IReadOnlyList<SavedFileView> Files);

public sealed record FilesReadResult(string Name, string ContentType, string DataBase64);

public sealed record FilesDeleteResult(bool Deleted);
