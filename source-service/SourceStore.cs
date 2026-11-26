using System.Text.Json;

namespace QTools.Source;

public sealed class SourceStore
{
    private readonly object _sync = new();
    private readonly string _dataDirectory;
    private readonly string _statePath;
    private StoreState _state;

    public SourceStore(string dataDirectory)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _statePath = Path.Combine(_dataDirectory, "state.json");
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "packages"));
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "extracted"));
        _state = new StoreState();

        if (File.Exists(_statePath))
        {
            var data = File.ReadAllBytes(_statePath);
            _state = JsonHelper.Deserialize<StoreState>(data)
                ?? throw new InvalidDataException("状态文件为空");
            if (_state.SchemaVersion != 1)
            {
                throw new InvalidDataException("状态文件版本不受支持");
            }

            _state.Submissions ??= new(StringComparer.Ordinal);
            _state.Catalog ??= new(StringComparer.Ordinal);
            foreach (var id in _state.Submissions.Keys.ToArray())
            {
                _state.Submissions[id] = _state.Submissions[id] with
                {
                    PackagePath = PackagePath(id),
                    ExtractPath = ExtractPath(id),
                };
            }
        }
    }

    public string DataDirectory => _dataDirectory;
    public string PackagePath(string id) => Path.Combine(_dataDirectory, "packages", id + ".qtools");
    public string ExtractPath(string id) => Path.Combine(_dataDirectory, "extracted", id);

    public Submission GetSubmission(string id)
    {
        lock (_sync)
        {
            return _state.Submissions.TryGetValue(id, out var submission)
                ? submission
                : throw new KeyNotFoundException("提交记录不存在");
        }
    }

    public bool AddSubmission(Submission submission)
    {
        lock (_sync)
        {
            if (_state.Submissions.ContainsKey(submission.Id)) { return false; }
            var next = CopyStateLocked();
            next.Submissions[submission.Id] = submission;
            CommitLocked(next);
            return true;
        }
    }

    public CatalogTool Approve(string id, string? note)
    {
        lock (_sync)
        {
            if (!_state.Submissions.TryGetValue(id, out var submission))
            {
                throw new KeyNotFoundException("提交记录不存在");
            }
            if (submission.Status != "pending")
            {
                throw new InvalidOperationException("只有待审核提交可以通过");
            }

            var kind = string.IsNullOrEmpty(submission.Kind) ? "tool" : submission.Kind;
            _state.Catalog.TryGetValue(submission.Manifest.Id, out var tool);
            if (tool is not null)
            {
                if (!string.Equals(tool.Kind, kind, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("该 ID 已被其他类型的条目占用");
                }
                if (kind == "tool")
                {
                    if (tool.Versions.Any(version => version.Version == submission.Manifest.Version))
                    {
                        throw new InvalidOperationException($"版本 {submission.Manifest.Version} 已存在");
                    }
                    var current = CurrentRevision(tool);
                    if (SemanticVersion.Compare(current.Version, submission.Manifest.Version) >= 0)
                    {
                        throw new InvalidOperationException($"新版本必须高于当前 {current.Version}");
                    }
                }
                else
                {
                    var current = CurrentRevision(tool);
                    if (SemanticVersion.Compare(current.Version, submission.Manifest.Version) > 0)
                    {
                        throw new InvalidOperationException($"新版本不能低于当前 {current.Version}");
                    }
                }
            }

            var now = DateTimeOffset.UtcNow;
            var revision = new Revision
            {
                SubmissionId = submission.Id,
                Version = submission.Manifest.Version,
                Sha256 = submission.Sha256,
                SizeBytes = submission.SizeBytes,
                SubmittedAtUtc = submission.CreatedAtUtc,
                ApprovedAtUtc = now,
                Manifest = submission.Manifest,
                Kind = kind,
                WebUrl = submission.WebUrl,
            };
            submission = submission with
            {
                Status = "approved",
                ReviewedAtUtc = now,
                ReviewNote = note?.Trim(),
            };
            tool ??= new CatalogTool { Id = submission.Manifest.Id, Kind = kind };
            var previousVersions = kind == "web"
                ? tool.Versions.Where(item => item.Version != revision.Version)
                : tool.Versions;
            tool = tool with
            {
                Name = submission.Manifest.Name,
                Description = tool.DescriptionCustomized ? tool.Description : submission.Manifest.Description,
                Category = tool.CategoryCustomized ? tool.Category : submission.Manifest.Category,
                Glyph = submission.Manifest.Glyph,
                Kind = kind,
                WebUrl = submission.WebUrl,
                Permissions = (submission.Manifest.Permissions ?? []).ToArray(),
                CurrentSubmissionId = submission.Id,
                Versions = [.. previousVersions, revision],
                Published = true,
                UpdatedAtUtc = now,
            };
            var versions = tool.Versions.OrderBy(item => item, VersionComparer.Instance).ToArray();
            tool = tool with { Versions = versions };
            var next = CopyStateLocked();
            next.Submissions[id] = submission;
            next.Catalog[tool.Id] = tool;
            CommitLocked(next);
            return tool;
        }
    }

    public void Reject(string id, string? note)
    {
        lock (_sync)
        {
            if (!_state.Submissions.TryGetValue(id, out var submission))
            {
                throw new KeyNotFoundException("提交记录不存在");
            }
            if (submission.Status != "pending")
            {
                throw new InvalidOperationException("只有待审核提交可以拒绝");
            }

            var next = CopyStateLocked();
            next.Submissions[id] = submission with
            {
                Status = "rejected",
                ReviewedAtUtc = DateTimeOffset.UtcNow,
                ReviewNote = note?.Trim(),
            };
            CommitLocked(next);
        }
    }

    public void SetPublished(string id, bool published)
    {
        lock (_sync)
        {
            if (!_state.Catalog.TryGetValue(id, out var tool) || tool.Versions.Count == 0)
            {
                throw new KeyNotFoundException("工具不存在");
            }
            var next = CopyStateLocked();
            next.Catalog[id] = tool with { Published = published, UpdatedAtUtc = DateTimeOffset.UtcNow };
            CommitLocked(next);
        }
    }

    public IReadOnlyList<CatalogTool> Catalog(bool includeUnpublished)
    {
        lock (_sync)
        {
            return _state.Catalog.Values
                .Where(tool => includeUnpublished || tool.Published)
                .OrderByDescending(tool => tool.Pinned)
                .ThenByDescending(tool => tool.Downloads)
                .ThenByDescending(tool => tool.UpdatedAtUtc)
                .ThenBy(tool => tool.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public CatalogTool UpdateMetadata(string id, string? description, string? category, bool? pinned)
    {
        lock (_sync)
        {
            if (!_state.Catalog.TryGetValue(id, out var tool))
            {
                throw new KeyNotFoundException("工具不存在");
            }

            var next = tool;
            if (description is not null)
            {
                next = next with { Description = NormalizeMetadata(description), DescriptionCustomized = true };
            }
            if (category is not null)
            {
                next = next with { Category = NormalizeMetadata(category), CategoryCustomized = true };
            }
            if (pinned is not null)
            {
                next = next with { Pinned = pinned.Value };
            }
            var state = CopyStateLocked();
            state.Catalog[id] = next;
            CommitLocked(state);
            return next;
        }
    }

    public void RecordDownload(string id)
    {
        lock (_sync)
        {
            if (!_state.Catalog.TryGetValue(id, out var tool)) { return; }
            var next = CopyStateLocked();
            next.Catalog[id] = tool with { Downloads = tool.Downloads + 1 };
            CommitLocked(next);
        }
    }

    private static string? NormalizeMetadata(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public CatalogTool GetTool(string id, bool includeUnpublished = false)
    {
        lock (_sync)
        {
            if (!_state.Catalog.TryGetValue(id, out var tool) || (!includeUnpublished && !tool.Published))
            {
                throw new KeyNotFoundException("工具不存在或未公开");
            }
            return tool;
        }
    }

    public IReadOnlyList<Submission> Submissions(string? status)
    {
        lock (_sync)
        {
            return _state.Submissions.Values
                .Where(item => string.IsNullOrEmpty(status) || status == "all" || item.Status == status)
                .OrderByDescending(item => item.CreatedAtUtc)
                .ToArray();
        }
    }

    public AdminStats Stats()
    {
        lock (_sync)
        {
            return new AdminStats
            {
                Tools = _state.Catalog.Count,
                Downloads = _state.Catalog.Values.Sum(tool => tool.Downloads),
                Pending = _state.Submissions.Values.Count(item => item.Status == "pending"),
                Approved = _state.Submissions.Values.Count(item => item.Status == "approved"),
                Rejected = _state.Submissions.Values.Count(item => item.Status == "rejected"),
            };
        }
    }

    public static Revision CurrentRevision(CatalogTool tool) =>
        tool.Versions.FirstOrDefault(version => version.SubmissionId == tool.CurrentSubmissionId)
        ?? tool.Versions.FirstOrDefault()
        ?? throw new InvalidOperationException("工具没有可用版本");

    public static Revision? FindRevision(CatalogTool tool, string? version) =>
        string.IsNullOrEmpty(version)
            ? tool.Versions.FirstOrDefault()
            : tool.Versions.FirstOrDefault(item => item.Version == version);

    private StoreState CopyStateLocked() => new()
    {
        SchemaVersion = _state.SchemaVersion,
        Submissions = new Dictionary<string, Submission>(_state.Submissions, StringComparer.Ordinal),
        Catalog = new Dictionary<string, CatalogTool>(_state.Catalog, StringComparer.Ordinal),
    };

    private void CommitLocked(StoreState next)
    {
        AtomicFile.Write(_statePath, JsonHelper.Serialize(next), privateFile: true);
        _state = next;
    }

    private sealed class VersionComparer : IComparer<Revision>
    {
        public static readonly VersionComparer Instance = new();
        public int Compare(Revision? x, Revision? y) =>
            x is null || y is null ? 0 : SemanticVersion.Compare(y.Version, x.Version);
    }
}
