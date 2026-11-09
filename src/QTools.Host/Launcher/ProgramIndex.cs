using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QTools.Host.Logging;
using QTools.Host.Serialization;

namespace QTools.Host.Launcher;

/// <summary>索引库中的一个本机程序（稳定 id 由目标路径哈希得出，跨次扫描不变）。</summary>
public sealed record IndexEntry(string Id, string Name, string Target, string Source, string? Category);

/// <summary>索引库文件（data/program-index.json）。</summary>
public sealed record ProgramIndexFile(int SchemaVersion, string IndexedAtUtc, IReadOnlyList<IndexEntry> Entries);

/// <summary>
/// 本机程序自动索引：启动后立即加载旧索引供列表使用，同时后台线程重新扫描；
/// 扫描完成后**原子覆盖**索引文件并整体替换内存快照——后台索引期间旧索引始终可用，
/// 绝不会出现「正在索引导致找不到上次已索引程序」的空窗。
/// </summary>
public sealed partial class ProgramIndex
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _filePath;
    private readonly ProgramDiscovery _discovery;
    private readonly string _iconCacheDirectory;
    private readonly object _gate = new();

    private IReadOnlyList<IndexEntry> _entries = Array.Empty<IndexEntry>();
    private string? _lastIndexedUtc;
    private bool _indexing;

    /// <summary>一次后台索引结束（成功或失败）后触发；订阅方据此刷新列表。回调在线程池线程执行。</summary>
    public event Action? Completed;

    public ProgramIndex(string filePath, ProgramDiscovery discovery, string iconCacheDirectory)
    {
        _filePath = filePath;
        _discovery = discovery;
        _iconCacheDirectory = iconCacheDirectory;
        Load();
    }

    public IReadOnlyList<IndexEntry> Entries
    {
        get { lock (_gate) { return _entries; } }
    }

    public bool IsIndexing
    {
        get { lock (_gate) { return _indexing; } }
    }

    public string? LastIndexedUtc
    {
        get { lock (_gate) { return _lastIndexedUtc; } }
    }

    public int Count
    {
        get { lock (_gate) { return _entries.Count; } }
    }

    public IndexEntry? Find(string id)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        }
    }

    /// <summary>启动一次后台索引；已在索引中时返回 false（不重复扫描）。</summary>
    public bool StartBackgroundRefresh()
    {
        lock (_gate)
        {
            if (_indexing)
            {
                return false;
            }

            _indexing = true;
        }

        _ = System.Threading.Tasks.Task.Run(RunRefresh);
        return true;
    }

    private void RunRefresh()
    {
        try
        {
            var discovered = _discovery.Scan();
            var entries = BuildEntries(discovered);
            var timestamp = DateTimeOffset.UtcNow.ToString("o");

            ExtractIcons(entries);
            WriteAtomic(_filePath, new ProgramIndexFile(CurrentSchemaVersion, timestamp, entries));

            lock (_gate)
            {
                _entries = entries;
                _lastIndexedUtc = timestamp;
            }

            Log.Info($"程序索引完成：{entries.Count} 个程序（原子覆盖，旧索引已整体替换）");
        }
        catch (Exception ex)
        {
            // 索引失败保留旧索引：列表依旧可用，只是数据旧一些
            Log.Warn($"后台程序索引失败（保留旧索引）：{ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _indexing = false;
            }

            Completed?.Invoke();
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
                File.ReadAllText(_filePath), AppJsonContext.Default.ProgramIndexFile);
            if (file is null || file.Entries is null)
            {
                return;
            }

            lock (_gate)
            {
                _entries = file.Entries
                    .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.Target))
                    .ToArray();
                _lastIndexedUtc = file.IndexedAtUtc;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取程序索引失败（视为空索引，后台会重建）：{ex.Message}");
        }
    }

    private void ExtractIcons(IReadOnlyList<IndexEntry> entries)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_iconCacheDirectory);
        }
        catch (Exception ex)
        {
            Log.Debug($"创建图标缓存目录失败：{ex.Message}");
            return;
        }

        foreach (var entry in entries)
        {
            // 单个图标失败静默回退字母头像，绝不阻断索引
            WindowsAppIcons.EnsureIconAt(entry.Target, Path.Combine(_iconCacheDirectory, entry.Id + ".png"));
        }
    }

    /// <summary>
    /// 把扫描结果整理成索引条目（纯函数）：过滤垃圾快捷方式、按目标路径去重、生成稳定 id。
    /// </summary>
    public static IReadOnlyList<IndexEntry> BuildEntries(IEnumerable<DiscoveredProgram> programs)
    {
        var entries = new List<IndexEntry>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var program in programs)
        {
            if (string.IsNullOrWhiteSpace(program.Name) || IsJunkName(program.Name))
            {
                continue;
            }

            var target = program.Target.Trim();
            if (target.Length == 0 || !seenTargets.Add(target))
            {
                continue;
            }

            entries.Add(new IndexEntry(BuildId(target), program.Name.Trim(), target, program.Source, program.Category));
        }

        return entries;
    }

    /// <summary>稳定 id：idx- + SHA256(小写目标路径) 前 12 位十六进制。同一程序跨次扫描 id 不变。</summary>
    public static string BuildId(string target)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(target.ToLowerInvariant()));
        return "idx-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>
    /// 垃圾快捷方式名：卸载器 / 安装器 / 帮助文档 / 官网与更新说明等——对启动器没有价值，不进索引。
    /// 中文直接子串匹配；英文用整词匹配，避免误伤 Helper / AppUpdate 这类合法名字。
    /// </summary>
    private static bool IsJunkName(string name)
    {
        return name.Contains("卸载", StringComparison.Ordinal) ||
               name.Contains("安装", StringComparison.Ordinal) ||
               name.Contains("帮助", StringComparison.Ordinal) ||
               name.Contains("手册", StringComparison.Ordinal) ||
               name.Contains("说明", StringComparison.Ordinal) ||
               name.Contains("官网", StringComparison.Ordinal) ||
               name.Contains("网站", StringComparison.Ordinal) ||
               name.Contains("更新日志", StringComparison.Ordinal) ||
               name.Contains("许可", StringComparison.Ordinal) ||
               name.Contains("协议", StringComparison.Ordinal) ||
               name.Contains("隐私", StringComparison.Ordinal) ||
               name.Contains("反馈", StringComparison.Ordinal) ||
               name.Contains("指南", StringComparison.Ordinal) ||
               name.Contains("入门", StringComparison.Ordinal) ||
               JunkEnglishWordPattern().IsMatch(name);
    }

    // 整词匹配：uninstall / unins* / setup / installer / install / readme / help / manuals / changelog / website 等
    [GeneratedRegex(
        @"\b(uninstalls?|unins\w*|setups?|installs?|installers?|readme|helps?|manuals?|changelogs?|licen[cs]es?|websites?|homepages?|documentation|quickstart|tutorial|eula|faq)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JunkEnglishWordPattern();

    /// <summary>原子写：先写同目录临时文件，再 File.Move 覆盖——读者永远看到完整旧文件或完整新文件。</summary>
    public static void WriteAtomic(string filePath, ProgramIndexFile data)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = filePath + ".tmp-" + Path.GetRandomFileName();
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(data, AppJsonContext.Default.ProgramIndexFile));
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (IOException) { /* 临时文件残留不影响正确性 */ }
            }
        }
    }
}