using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RuleCraft;

/// <summary>
/// The default <see cref="IRuleStore"/>: file-based persistence under one folder, two files per rule —
/// <c>&lt;id&gt;.meta.json</c> (the record) and the source, <c>&lt;id&gt;.cs</c> for compiled rules or
/// <c>&lt;id&gt;.rule.json</c> for JSON-DSL rules. Writes go through a temp file and a replacing move,
/// so a crash mid-write cannot leave a truncated file; source is written before the record, so a crash
/// between the two leaves an orphaned source (harmless) rather than a record pointing at nothing.
/// </summary>
public sealed class FileRuleStore : IRuleStore
{
    private const string MetadataSuffix = ".meta.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    // No I/O here: constructing a store should not touch the disk, and one nobody writes to has no
    // business creating a folder. The folder appears on the first Save.
    public FileRuleStore(string root, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("The store root must name a folder.", nameof(root));
        _root = root;
        _logger = logger ?? NullLogger.Instance;
    }

    public void Save(StoredRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_lock)
        {
            Directory.CreateDirectory(_root);
            WriteAtomic(SourcePath(rule.Record), rule.Source);
            WriteMetadata(rule.Record);
        }
    }

    public void Update(RuleRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            WriteMetadata(record);
        }
    }

    public RuleRecord? Find(string id)
    {
        lock (_lock)
        {
            var path = MetadataPath(id);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<RuleRecord>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
        }
    }

    public string ReadSource(RuleRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            return File.ReadAllText(SourcePath(record), Encoding.UTF8);
        }
    }

    /// <summary>
    /// Every rule's record. A file that is not readable metadata is logged and skipped — a stray or
    /// corrupt .json in the folder must not take down the application at startup.
    /// </summary>
    public IReadOnlyList<RuleRecord> LoadAll()
    {
        lock (_lock)
        {
            if (!Directory.Exists(_root))
                return [];

            var result = new List<RuleRecord>();

            // Enumerate broadly and filter in code: a glob of "*.meta.json" can also match via
            // Windows 8.3 short names.
            foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
            {
                if (!path.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var record = JsonSerializer.Deserialize<RuleRecord>(
                        File.ReadAllText(path, Encoding.UTF8), JsonOptions);
                    if (record is not null)
                        result.Add(record);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Skipping unreadable rule metadata file {Path}.", path);
                }
            }

            return result.OrderBy(m => m.CreatedUtc).ToList();
        }
    }

    private void WriteMetadata(RuleRecord record) =>
        WriteAtomic(MetadataPath(record.Id), JsonSerializer.Serialize(record, JsonOptions));

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    private string SourcePath(RuleRecord record) =>
        Path.Combine(_root, record.Id + (record.Origin == RuleOrigin.Json ? ".rule.json" : ".cs"));

    private string MetadataPath(string id) => Path.Combine(_root, id + MetadataSuffix);
}
