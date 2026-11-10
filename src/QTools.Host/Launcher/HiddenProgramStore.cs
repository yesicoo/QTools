using System.Text;
using System.Text.Json;
using QTools.Host.Logging;
using QTools.Host.Serialization;

namespace QTools.Host.Launcher;

/// <summary>hidden-programs.json 的落盘形态。</summary>
public sealed record HiddenProgramFile(IReadOnlyList<string> Ids);

/// <summary>
/// 索引程序隐藏列表：用户右键「从列表隐藏」后的持久化记录。
/// 索引 id 稳定（目标路径哈希），隐藏记录跨重新索引保留；
/// 只影响统一列表展示，不改动程序本身，已隐藏的程序仍可手动添加为启动项。
/// </summary>
public sealed class HiddenProgramStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private HashSet<string> _ids = new(StringComparer.Ordinal);

    public HiddenProgramStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public bool IsHidden(string programId)
    {
        lock (_gate) { return _ids.Contains(programId); }
    }

    public int Count
    {
        get { lock (_gate) { return _ids.Count; } }
    }

    /// <summary>隐藏一个索引程序；返回是否为新增（重复隐藏返回 false）。</summary>
    public bool Hide(string programId)
    {
        if (string.IsNullOrWhiteSpace(programId)) { return false; }

        lock (_gate)
        {
            if (!_ids.Add(programId)) { return false; }
            SaveLocked();
        }

        Log.Info($"已隐藏索引程序 {programId}");
        return true;
    }

    /// <summary>恢复显示一个索引程序；返回是否确实移除了一条记录。</summary>
    public bool Unhide(string programId)
    {
        lock (_gate)
        {
            if (!_ids.Remove(programId)) { return false; }
            SaveLocked();
        }

        Log.Info($"已恢复显示索引程序 {programId}");
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) { return; }

            var file = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath), AppJsonContext.Default.HiddenProgramFile);
            if (file?.Ids is { Count: > 0 })
            {
                _ids = new HashSet<string>(file.Ids, StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取隐藏程序列表失败：{ex.Message}");
        }
    }

    private void SaveLocked()
    {
        // 临时文件 + 原子覆盖，与程序索引同一写入纪律
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

        var file = new HiddenProgramFile(_ids.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        var temporary = _filePath + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(file, AppJsonContext.Default.HiddenProgramFile),
            new UTF8Encoding(false));
        File.Move(temporary, _filePath, overwrite: true);
    }
}
