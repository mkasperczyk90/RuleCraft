using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RuleCraft.Store;

/// <summary>
/// File-based persistence under the configured root, two files per rule:
/// <c>&lt;id&gt;.meta.json</c> (audit metadata) and the source — <c>&lt;id&gt;.cs</c> for compiled
/// rules or <c>&lt;id&gt;.rule.json</c> for JSON-DSL rules. Sources are the single source of truth:
/// rules are recompiled/reparsed from them on every reload.
/// </summary>
internal sealed class RuleStore
{
    private const string MetadataSuffix = ".meta.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    // No I/O here: constructing an engine should not touch the disk, and a store nobody writes to
    // has no business creating a folder. RuleEngineOptions already rejected a malformed path.
    public RuleStore(string root, ILogger logger)
    {
        _root = root;
        _logger = logger;
    }

    public void Save(RuleMetadata metadata, string source)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_root);

            // Hash exactly the bytes we write — never round-trip the document through a serializer,
            // or the reviewer stops seeing what is actually stored.
            metadata.SourceSha256 = Hash(source);

            // Source before metadata: metadata is the index LoadAll reads, so a crash between the
            // two leaves an orphaned source file (harmless) rather than a rule pointing at nothing.
            WriteAtomic(SourcePath(metadata), source);
            WriteMetadata(metadata);
        }
    }

    public void UpdateMetadata(RuleMetadata metadata)
    {
        lock (_lock)
        {
            WriteMetadata(metadata);
        }
    }

    public RuleMetadata? Find(string id)
    {
        lock (_lock)
        {
            var path = MetadataPath(id);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<RuleMetadata>(File.ReadAllText(path, Encoding.UTF8), JsonOptions);
        }
    }

    public string ReadSource(RuleMetadata metadata)
    {
        lock (_lock)
        {
            return File.ReadAllText(SourcePath(metadata), Encoding.UTF8);
        }
    }

    /// <summary>True when the on-disk source no longer matches the hash recorded at save time.</summary>
    public bool IsSourceTampered(RuleMetadata metadata) =>
        metadata.SourceSha256 is not null && Hash(ReadSource(metadata)) != metadata.SourceSha256;

    /// <summary>
    /// All rules in the store. A file that is not readable metadata is logged and skipped —
    /// a stray or corrupt .json in the folder must not take down the application at startup.
    /// </summary>
    public IReadOnlyList<RuleMetadata> LoadAll()
    {
        lock (_lock)
        {
            // Nothing has been saved yet; the folder appears on the first write.
            if (!Directory.Exists(_root))
                return [];

            var result = new List<RuleMetadata>();

            // Enumerate broadly and filter in code: a glob of "*.meta.json" can also match via
            // Windows 8.3 short names.
            foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
            {
                if (!path.EndsWith(MetadataSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var metadata = JsonSerializer.Deserialize<RuleMetadata>(
                        File.ReadAllText(path, Encoding.UTF8), JsonOptions);
                    if (metadata is not null)
                        result.Add(metadata);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Skipping unreadable rule metadata file {Path}.", path);
                }
            }

            return result.OrderBy(m => m.CreatedUtc).ToList();
        }
    }

    private void WriteMetadata(RuleMetadata metadata) =>
        WriteAtomic(MetadataPath(metadata.Id), JsonSerializer.Serialize(metadata, JsonOptions));

    /// <summary>
    /// Writes via a temp file and a replacing move, so a crash mid-write cannot leave a truncated
    /// file behind. A half-written .meta.json would be skipped by <see cref="LoadAll"/> as corrupt,
    /// silently dropping an approved rule from the application on the next restart — for a file
    /// that doubles as the audit trail, "mostly written" is not good enough.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    private string SourcePath(RuleMetadata metadata) =>
        Path.Combine(_root, metadata.Id + (metadata.Origin == RuleOrigin.Json ? ".rule.json" : ".cs"));

    private string MetadataPath(string id) => Path.Combine(_root, id + MetadataSuffix);

    private static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}
