using System.Text.Json;
using QTools.Host.Logging;
using QTools.Host.Serialization;

namespace QTools.Host.Launcher;

/// <summary>一个启动项的使用记录（次数 + 最近使用时间）。</summary>
public sealed record UsageEntry(string Id, int Count, string LastUsedUtc);

/// <summary>使用频次文件（data/usage.json）。</summary>
public sealed record UsageFile(int SchemaVersion, IReadOnlyList<UsageEntry> Entries);

/// <summary>
/// 使用频次统计：每次成功启动/打开记一次，列表默认按「次数降序 → 最近使用降序 → 名称升序」排序。
/// 写入原子覆盖；读写加锁，UI 线程与后台线程都可安全调用。
/// </summary>
public sealed class UsageStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageEntry> _entries = new(StringComparer.Ordinal);

    public UsageStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public UsageEntry? Get(string id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) ? entry : null;
        }
    }

    /// <summary>记录一次使用并立即持久化（文件很小，同步写开销可忽略）。</summary>
    public void Record(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (_gate)
        {
            var count = _entries.TryGetValue(id, out var existing) ? existing.Count + 1 : 1;
            _entries[id] = new UsageEntry(id, count, DateTimeOffset.UtcNow.ToString("o"));

            try
            {
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var file = new UsageFile(CurrentSchemaVersion, _entries.Values.ToArray());
                var tempPath = _filePath + ".tmp-" + Path.GetRandomFileName();
                try
                {
                    File.WriteAllText(tempPath, JsonSerializer.Serialize(file, AppJsonContext.Default.UsageFile));
                    File.Move(tempPath, _filePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); }
                        catch (IOException) { /* 残留不影响正确性 */ }
                    }
                }
            }
            catch (Exception ex)
            {
                // 持久化失败不阻断启动：内存计数仍在，下次记录会再尝试写
                Log.Warn($"保存使用频次失败：{ex.Message}");
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var file = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath), AppJsonContext.Default.UsageFile);
            if (file?.Entries is null)
            {
                return;
            }

            foreach (var entry in file.Entries)
            {
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.Id) && entry.Count > 0)
                {
                    _entries[entry.Id] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取使用频次失败（视为空）：{ex.Message}");
        }
    }
}