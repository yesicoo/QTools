using QTools.Host.Bridge;
using QTools.Host.Logging;

namespace QTools.Host.Tools;

/// <summary>已加载的工具：manifest + 目录信息 + 校验状态。</summary>
public sealed class LoadedTool
{
    public required ToolManifest Manifest { get; init; }

    /// <summary>工具目录绝对路径。</summary>
    public required string Directory { get; init; }

    public required DateTime LastModifiedUtc { get; init; }

    /// <summary>校验是否通过；false 时列表仍显示但标注「损坏」与原因。</summary>
    public bool IsValid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ToolPermission> Permissions { get; init; } = Array.Empty<ToolPermission>();

    /// <summary>历史版本列表（新的在前）。首个版本由旧版工具自动迁移生成。</summary>
    public IReadOnlyList<ToolVersion> Versions { get; init; } = Array.Empty<ToolVersion>();

    /// <summary>正在运行的版本号（= manifest.version）。</summary>
    public string ActiveVersion => Manifest.Version;

    public string Id => Manifest.Id;

    public string Name => string.IsNullOrWhiteSpace(Manifest.Name) ? Manifest.Id : Manifest.Name;

    public string Entry => string.IsNullOrWhiteSpace(Manifest.Entry) ? "index.html" : Manifest.Entry;

    public string EntryFullPath => Path.Combine(Directory, Entry);

    public bool HasPermission(ToolPermission permission) => Permissions.Contains(permission);

    /// <summary>入口 URL（相对本地服务根）。</summary>
    public string EntryUrl => $"/tools/{Id}/{Entry}";

    /// <summary>该工具是否已有存储数据（列表展示用）。</summary>
    public bool HasStorageData =>
        System.IO.File.Exists(System.IO.Path.Combine(
            QTools.Host.Configuration.AppPaths.StorageDirectory, Id + ".json"));
}

/// <summary>
/// 工具注册表：扫描 tools 目录、缓存已加载工具、按 ID 查询。线程安全。
/// </summary>
public sealed class ToolRegistry
{
    private readonly string _toolsDirectory;
    private readonly ToolVersionStore _versionStore = new();
    private readonly Dictionary<string, LoadedTool> _tools = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ToolRegistry(string toolsDirectory)
    {
        _toolsDirectory = toolsDirectory;
    }

    public string ToolsDirectory => _toolsDirectory;

    /// <summary>版本存储（版本切换 / 快照 / 删除）。</summary>
    public ToolVersionStore VersionStore => _versionStore;

    /// <summary>切换到指定版本：覆盖根目录文件、更新 manifest.version、重扫。</summary>
    public LoadedTool SwitchVersion(string toolId, string version)
    {
        LoadedTool? tool;
        lock (_gate)
        {
            if (!_tools.TryGetValue(toolId, out tool))
            {
                throw new BridgeException("TOOL_NOT_FOUND", $"未找到工具 '{toolId}'");
            }
        }

        if (!ToolVersionStore.IsValidVersion(version))
        {
            throw new BridgeException("VERSION_INVALID", $"非法版本号 '{version}'");
        }

        var directory = tool.Directory;
        _versionStore.Activate(directory, tool.ActiveVersion, version);

        // 用新版本快照里的 manifest 覆盖根目录 manifest，保证 version 字段同步
        var snapshotManifest = Path.Combine(ToolVersionStore.VersionDirectory(directory, version), "manifest.json");
        if (File.Exists(snapshotManifest))
        {
            var temp = Path.Combine(directory, "manifest.json.switch-tmp");
            File.Copy(snapshotManifest, temp, overwrite: true);
            File.Move(temp, Path.Combine(directory, "manifest.json"), overwrite: true);
        }

        var reloaded = Reload(toolId);
        if (reloaded is null)
        {
            throw new BridgeException("VERSION_SWITCH_FAILED", "切换后工具无法加载，已保留原文件");
        }

        return reloaded;
    }

    /// <summary>删除一个历史版本。</summary>
    public LoadedTool DeleteVersion(string toolId, string version)
    {
        LoadedTool? tool;
        lock (_gate)
        {
            if (!_tools.TryGetValue(toolId, out tool))
            {
                throw new BridgeException("TOOL_NOT_FOUND", $"未找到工具 '{toolId}'");
            }
        }

        _versionStore.Delete(tool.Directory, version, tool.ActiveVersion);
        return Reload(toolId) ?? tool;
    }

    public IReadOnlyList<LoadedTool> GetAll()
    {
        lock (_gate)
        {
            return _tools.Values
                .OrderBy(t => t.Id, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public bool TryGet(string toolId, out LoadedTool? tool)
    {
        lock (_gate)
        {
            return _tools.TryGetValue(toolId, out tool);
        }
    }

    public LoadedTool? Find(string toolId)
    {
        TryGet(toolId, out var tool);
        return tool;
    }

    /// <summary>注册表中是否已存在该 ID。</summary>
    public bool Contains(string toolId)
    {
        lock (_gate)
        {
            return _tools.ContainsKey(toolId);
        }
    }

    /// <summary>重新扫描全部工具，并返回指定 ID 的新加载结果（不存在则返回 null）。</summary>
    public LoadedTool? Reload(string toolId)
    {
        Rescan();
        return Find(toolId);
    }

    /// <summary>重新扫描全部工具。单个工具解析失败不影响其他工具。</summary>
    public IReadOnlyList<LoadedTool> Rescan()
    {
        var result = new List<LoadedTool>();

        if (!Directory.Exists(_toolsDirectory))
        {
            Log.Warn($"工具目录不存在：{_toolsDirectory}");
            lock (_gate)
            {
                _tools.Clear();
            }

            return result;
        }

        foreach (var directory in Directory.EnumerateDirectories(_toolsDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var directoryName = Path.GetFileName(directory);

            // 跳过宿主内部目录（.versions 等）：工具必须是 tools/{id}/ 这一层
            if (directoryName.StartsWith('.') && directoryName.Length > 1)
            {
                continue;
            }

            var manifestPath = Path.Combine(directory, "manifest.json");

            if (!File.Exists(manifestPath))
            {
                Log.Warn($"跳过 {directoryName}：缺少 manifest.json");
                continue;
            }

            try
            {
                var manifest = QTools.Host.Serialization.AppJsonContext.Default.ToolManifest is { }
                    ? System.Text.Json.JsonSerializer.Deserialize(
                        File.ReadAllText(manifestPath),
                        QTools.Host.Serialization.AppJsonContext.Default.ToolManifest)
                    : null;

                if (manifest is null)
                {
                    Log.Warn($"跳过 {directoryName}：manifest.json 为空或格式错误");
                    continue;
                }

                var errors = ToolManifestValidator.Validate(manifest, directoryName);
                var lastModified = File.GetLastWriteTimeUtc(manifestPath);

                // 版本迁移：旧版工具没有 .versions/ 快照，把当前文件存成第一个版本
                var versions = _versionStore.List(directory, manifest.Version);
                if (versions.Count == 0 && ToolVersionStore.IsValidVersion(manifest.Version))
                {
                    _versionStore.Snapshot(directory, manifest.Version, note: "初始版本（自动迁移）");
                    versions = _versionStore.List(directory, manifest.Version);
                }

                var tool = new LoadedTool
                {
                    Manifest = manifest,
                    Directory = Path.GetFullPath(directory),
                    LastModifiedUtc = lastModified,
                    IsValid = errors.Count == 0,
                    Errors = errors,
                    Permissions = ToolManifestValidator.ParsePermissions(manifest.Permissions),
                    Versions = versions,
                };

                result.Add(tool);

                if (tool.IsValid)
                {
                    Log.Info($"已加载工具 {tool.Id} v{tool.Manifest.Version}（权限：{(tool.Permissions.Count == 0 ? "无" : string.Join(",", tool.Permissions))}）");
                }
                else
                {
                    Log.Warn($"工具 {directoryName} 的 manifest 校验失败：{string.Join("；", errors)}");
                }
            }
            catch (Exception ex)
            {
                // 单个工具解析失败绝不影响宿主与其他工具
                Log.Warn($"解析工具 {directoryName} 失败：{ex.Message}");
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

    /// <summary>登记运行时新建的工具（不触发全量扫描）。</summary>
    public void Add(LoadedTool tool)
    {
        lock (_gate)
        {
            _tools[tool.Id] = tool;
        }
    }

    public void Remove(string toolId)
    {
        lock (_gate)
        {
            _tools.Remove(toolId);
        }
    }
}
