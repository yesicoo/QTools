using System.Text.Json;
using QTools.Host.Logging;

namespace QTools.Host.Storage;

/// <summary>存储文件 DTO：<c>{ "version":1, "items": { key: &lt;json value&gt; } }</c>。</summary>
public sealed record StorageFile(int Version, Dictionary<string, JsonElement> Items);

/// <summary>
/// 按 toolId 隔离的工具存储。JSON 文件 + 「写临时文件 + 原子替换」，避免崩溃损坏。
/// 所有序列化走源生成 JSON。
/// </summary>
public sealed class ToolStorage
{
    private const int CurrentVersion = 1;
    private const long MaxValueBytes = 256 * 1024;
    private const long MaxTotalBytesPerTool = 5 * 1024 * 1024;
    private const int MaxKeyLength = 128;

    private readonly string _storageDirectory;
    private readonly Dictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly object _lockGate = new();

    public ToolStorage(string storageDirectory)
    {
        _storageDirectory = storageDirectory;
        Directory.CreateDirectory(_storageDirectory);
    }

    public string StorageDirectory => _storageDirectory;

    private SemaphoreSlim GetLock(string toolId)
    {
        lock (_lockGate)
        {
            if (!_locks.TryGetValue(toolId, out var semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _locks[toolId] = semaphore;
            }

            return semaphore;
        }
    }

    private string ResolvePath(string toolId) => Path.Combine(_storageDirectory, toolId + ".json");

    public static void ValidateKey(string key)
    {        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
        {
            throw new Bridge.BridgeException("STORAGE_KEY_INVALID", $"key 必填且不超过 {MaxKeyLength} 字符");
        }

        foreach (var ch in key)
        {
            var ok = ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-';
            if (!ok)
            {
                throw new Bridge.BridgeException("STORAGE_KEY_INVALID", "key 只允许字母、数字、. _ -");
            }
        }
    }

    private static StorageFile Empty() => new(CurrentVersion, new Dictionary<string, JsonElement>(StringComparer.Ordinal));

    private static StorageFile Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty();
        }

        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), QTools.Host.Serialization.AppJsonContext.Default.StorageFile);
            if (file?.Items is null)
            {
                Log.Warn($"存储文件内容异常，已重建：{path}");
                return Empty();
            }

            return file;
        }
        catch (JsonException ex)
        {
            // 损坏文件备份后重建，绝不静默
            var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            try
            {
                File.Move(path, backup, overwrite: true);
                Log.Warn($"存储文件损坏，已备份为 {Path.GetFileName(backup)} 并重建：{ex.Message}");
            }
            catch (Exception moveEx)
            {
                Log.Warn($"存储文件损坏且备份失败（{moveEx.Message}）：{path}");
            }

            return Empty();
        }
    }

    private static void Save(string path, StorageFile file)
    {
        var json = JsonSerializer.Serialize(file, QTools.Host.Serialization.AppJsonContext.Default.StorageFile);
        var temp = path + ".tmp";

        File.WriteAllText(temp, json);
        // File.Move + overwrite 在同一文件系统上等价于原子替换
        File.Move(temp, path, overwrite: true);
    }

    public async System.Threading.Tasks.Task<JsonElement?> GetAsync(string toolId, string key)
    {
        ValidateKey(key);
        var semaphore = GetLock(toolId);
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var file = Load(ResolvePath(toolId));
            return file.Items.TryGetValue(key, out var value) ? value : null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async System.Threading.Tasks.Task SetAsync(string toolId, string key, JsonElement value)
    {
        ValidateKey(key);
        var raw = value.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(raw) > MaxValueBytes)
        {
            throw new Bridge.BridgeException("STORAGE_VALUE_TOO_LARGE", $"单个值不超过 {MaxValueBytes / 1024} KB");
        }

        var semaphore = GetLock(toolId);
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath(toolId);
            var file = Load(path);
            file.Items[key] = value;

            if (System.Text.Encoding.UTF8.GetByteCount(
                    JsonSerializer.Serialize(file, QTools.Host.Serialization.AppJsonContext.Default.StorageFile)) > MaxTotalBytesPerTool)
            {
                throw new Bridge.BridgeException("STORAGE_QUOTA_EXCEEDED", $"单个工具存储总量不超过 {MaxTotalBytesPerTool / 1024 / 1024} MB");
            }

            Save(path, file);
        }
        catch (IOException ex)
        {
            throw new Bridge.BridgeException("STORAGE_IO_ERROR", $"写入存储失败：{ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async System.Threading.Tasks.Task RemoveAsync(string toolId, string key)
    {
        ValidateKey(key);
        var semaphore = GetLock(toolId);
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolvePath(toolId);
            var file = Load(path);
            if (file.Items.Remove(key))
            {
                Save(path, file);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    public async System.Threading.Tasks.Task<IReadOnlyList<string>> ListAsync(string toolId)
    {
        var semaphore = GetLock(toolId);
        await semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var file = Load(ResolvePath(toolId));
            return file.Items.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            semaphore.Release();
        }
    }

    public System.Threading.Tasks.Task<long> GetSizeBytesAsync(string toolId)
    {
        var path = ResolvePath(toolId);
        return System.Threading.Tasks.Task.FromResult(
            File.Exists(path) ? new FileInfo(path).Length : 0L);
    }

    /// <summary>删除工具的存储数据（删除工具时的可选项）。</summary>
    public bool Delete(string toolId)
    {
        var path = ResolvePath(toolId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"删除工具存储失败 {toolId}：{ex.Message}");
            return false;
        }
    }
}
