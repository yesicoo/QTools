using System.Text;
using System.Text.Json;
using QTools.Host.Logging;
using QTools.Host.Serialization;

namespace QTools.Host.Launcher;

/// <summary>pinned-items.json 的落盘形态（键为 "kind:id"）。</summary>
public sealed record PinnedItemFile(IReadOnlyList<string> Keys);

/// <summary>
/// 列表置顶记录：用户右键「置顶」后的持久化顺序偏好。
/// 只影响统一列表排序（置顶条目始终排在最前），与窗口级「置顶」（Topmost）无关；
/// 键含 kind，避免小工具与启动项同名 id 互相串台。
/// </summary>
public sealed class PinnedItemStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private HashSet<string> _keys = new(StringComparer.Ordinal);

    public PinnedItemStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public static string BuildKey(string kind, string id) => kind + ":" + id;

    public bool IsPinned(string kind, string id)
    {
        lock (_gate) { return _keys.Contains(BuildKey(kind, id)); }
    }

    public int Count
    {
        get { lock (_gate) { return _keys.Count; } }
    }

    /// <summary>设置置顶状态；返回是否确实发生了变化（重复设置返回 false）。</summary>
    public bool SetPinned(string kind, string id, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(id)) { return false; }

        var key = BuildKey(kind, id);
        lock (_gate)
        {
            var changed = pinned ? _keys.Add(key) : _keys.Remove(key);
            if (!changed) { return false; }
            SaveLocked();
        }

        Log.Info($"已{(pinned ? "置顶" : "取消置顶")}列表条目 {key}");
        return true;
    }

    /// <summary>条目被删除时顺带清理置顶记录（避免残留键无限增长）。</summary>
    public void Forget(string kind, string id)
    {
        var key = BuildKey(kind, id);
        lock (_gate)
        {
            if (!_keys.Remove(key)) { return; }
            SaveLocked();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) { return; }

            var file = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath), AppJsonContext.Default.PinnedItemFile);
            if (file?.Keys is { Count: > 0 })
            {
                _keys = new HashSet<string>(file.Keys, StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取置顶列表失败：{ex.Message}");
        }
    }

    private void SaveLocked()
    {
        // 临时文件 + 原子覆盖，与程序索引/隐藏列表同一写入纪律
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

        var file = new PinnedItemFile(_keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        var temporary = _filePath + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(file, AppJsonContext.Default.PinnedItemFile),
            new UTF8Encoding(false));
        File.Move(temporary, _filePath, overwrite: true);
    }
}