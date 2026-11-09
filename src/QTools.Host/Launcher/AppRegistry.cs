using System.Text.Json;
using QTools.Host.Configuration;
using QTools.Host.Logging;

namespace QTools.Host.Launcher;

/// <summary>
/// 本机程序启动项注册表：扫描 apps/ 目录、缓存、按 ID 查询。线程安全。
/// </summary>
public sealed class AppRegistry
{
    private readonly string _appsDirectory;
    private readonly Dictionary<string, LoadedApp> _apps = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public AppRegistry(string appsDirectory)
    {
        _appsDirectory = appsDirectory;
    }

    public string AppsDirectory => _appsDirectory;

    public IReadOnlyList<LoadedApp> GetAll()
    {
        lock (_gate)
        {
            return _apps.Values
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public LoadedApp? Find(string appId)
    {
        lock (_gate)
        {
            return _apps.TryGetValue(appId, out var app) ? app : null;
        }
    }

    public bool Contains(string appId)
    {
        lock (_gate)
        {
            return _apps.ContainsKey(appId);
        }
    }

    /// <summary>重新扫描全部启动项。单个解析失败不影响其他项。</summary>
    public IReadOnlyList<LoadedApp> Rescan()
    {
        var result = new List<LoadedApp>();

        if (!Directory.Exists(_appsDirectory))
        {
            lock (_gate) { _apps.Clear(); }
            return result;
        }

        foreach (var directory in Directory.EnumerateDirectories(_appsDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var directoryName = Path.GetFileName(directory);
            if (directoryName.StartsWith('.') && directoryName.Length > 1)
            {
                continue;
            }

            var manifestPath = Path.Combine(directory, "app.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize(
                    File.ReadAllText(manifestPath),
                    QTools.Host.Serialization.AppJsonContext.Default.AppEntry);

                if (entry is null)
                {
                    Log.Warn($"跳过启动项 {directoryName}：app.json 为空或格式错误");
                    continue;
                }

                var errors = AppEntryValidator.Validate(entry, directoryName);
                var app = new LoadedApp
                {
                    Entry = entry,
                    Directory = Path.GetFullPath(directory),
                    IsValid = errors.Count == 0,
                    Errors = errors,
                };

                result.Add(app);

                if (app.IsValid)
                {
                    Log.Info($"已加载启动项 {app.Id}（{app.Name}）→ {entry.Target}");
                }
                else
                {
                    Log.Warn($"启动项 {directoryName} 校验失败：{string.Join("；", errors)}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"解析启动项 {directoryName} 失败：{ex.Message}");
            }
        }

        lock (_gate)
        {
            _apps.Clear();
            foreach (var app in result)
            {
                _apps[app.Id] = app;
            }
        }

        return result;
    }

    public LoadedApp? Reload(string appId)
    {
        Rescan();
        return Find(appId);
    }

    public void Remove(string appId)
    {
        lock (_gate) { _apps.Remove(appId); }
    }
}
