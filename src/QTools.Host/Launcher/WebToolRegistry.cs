using System.Text.Json;
using QTools.Host.Configuration;
using QTools.Host.Logging;

namespace QTools.Host.Launcher;

/// <summary>
/// 在线工具注册表：扫描 webtools/ 目录、缓存、按 ID 查询。线程安全。
/// </summary>
public sealed class WebToolRegistry
{
    private readonly string _webToolsDirectory;
    private readonly Dictionary<string, LoadedWebTool> _tools = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public WebToolRegistry(string webToolsDirectory)
    {
        _webToolsDirectory = webToolsDirectory;
    }

    public string WebToolsDirectory => _webToolsDirectory;

    public IReadOnlyList<LoadedWebTool> GetAll()
    {
        lock (_gate)
        {
            return _tools.Values
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public LoadedWebTool? Find(string webId)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(webId, out var tool) ? tool : null;
        }
    }

    public bool Contains(string webId)
    {
        lock (_gate)
        {
            return _tools.ContainsKey(webId);
        }
    }

    /// <summary>重新扫描全部在线工具。单个解析失败不影响其他项。</summary>
    public IReadOnlyList<LoadedWebTool> Rescan()
    {
        var result = new List<LoadedWebTool>();

        if (!Directory.Exists(_webToolsDirectory))
        {
            lock (_gate) { _tools.Clear(); }
            return result;
        }

        foreach (var directory in Directory.EnumerateDirectories(_webToolsDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var directoryName = Path.GetFileName(directory);
            if (directoryName.StartsWith('.') && directoryName.Length > 1)
            {
                continue;
            }

            var manifestPath = Path.Combine(directory, "web.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize(
                    File.ReadAllText(manifestPath),
                    QTools.Host.Serialization.AppJsonContext.Default.WebToolEntry);

                if (entry is null)
                {
                    Log.Warn($"跳过在线工具 {directoryName}：web.json 为空或格式错误");
                    continue;
                }

                var errors = WebToolValidator.Validate(entry, directoryName);
                var tool = new LoadedWebTool
                {
                    Entry = entry,
                    Directory = Path.GetFullPath(directory),
                    IsValid = errors.Count == 0,
                    Errors = errors,
                };

                result.Add(tool);

                if (tool.IsValid)
                {
                    Log.Info($"已加载在线工具 {tool.Id}（{tool.Name}）→ {entry.Url}");
                }
                else
                {
                    Log.Warn($"在线工具 {directoryName} 校验失败：{string.Join("；", errors)}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"解析在线工具 {directoryName} 失败：{ex.Message}");
            }
        }

        lock (_gate)
        {
            _tools.Clear();
            foreach (var tool in result)
            {
                _tools[tool.Id] = tool;
            }
        }

        return result;
    }

    public LoadedWebTool? Reload(string webId)
    {
        Rescan();
        return Find(webId);
    }

    public void Remove(string webId)
    {
        lock (_gate) { _tools.Remove(webId); }
    }
}
