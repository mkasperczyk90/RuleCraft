using System.Security.Cryptography;
using System.Text;

namespace RuleCraft;

/// <summary>
/// A rule's persisted audit record — everything about a rule except its source text. Immutable: a
/// status change produces a new record via <c>with</c>, so a store never sees a half-mutated one.
/// The field names are the on-disk JSON contract for <see cref="FileRuleStore"/>; a custom store may
/// map them to columns however it likes, but must round-trip every one — the engine relies on
/// <see cref="SourceSha256"/>, <see cref="ContractType"/>/<see cref="ContextType"/> and
/// <see cref="ContractFingerprint"/> for integrity and contract-change detection.
/// </summary>
public sealed record RuleRecord
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Spec { get; init; }

    /// <summary>What kind of rule this is; drives the source extension and which kind validates it.</summary>
    public RuleOrigin Origin { get; init; } = RuleOrigin.Compiled;

    public RuleStatus Status { get; init; }

    public int Priority { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>SHA-256 of the exact source bytes, set by the engine at save time; the tamper signal.</summary>
    public string? SourceSha256 { get; init; }

    /// <summary>Contract/context type this rule was written against, by full name. Null in pre-versioned metadata.</summary>
    public string? ContractType { get; init; }

    /// <inheritdoc cref="ContractType"/>
    public string? ContextType { get; init; }

    /// <summary>Hash of the contract/context API shape at approval time; distinguishes "stale" from "not mine".</summary>
    public string? ContractFingerprint { get; init; }

    public string? ModelId { get; init; }

    public string? ApprovedBy { get; init; }

    public DateTimeOffset? ApprovedAtUtc { get; init; }

    /// <summary>Rejection reason / quarantine cause, when applicable.</summary>
    public string? StatusReason { get; init; }

    public ValidationReport? Report { get; init; }
}

/// <summary>A rule's record together with its source text — the unit a store persists on <see cref="IRuleStore.Save"/>.</summary>
public sealed record StoredRule(RuleRecord Record, string Source);

/// <summary>
/// Where an engine persists rules. The default is <see cref="FileRuleStore"/> (one folder, two files
/// per rule); implement this to back rules with a shared database or object store so several engine
/// instances can see the same rules — set it on <see cref="RuleEngineOptions.Store"/>.
///
/// Implementations must be thread-safe: an engine calls them from concurrent approval operations
/// (serialized per rule id, but not across ids) and from a lock-free read path is NOT one of them —
/// the store is never on <c>Resolve</c>. The engine owns integrity (hashing, tamper detection) and
/// contract checks; a store is pure persistence and must round-trip every <see cref="RuleRecord"/>
/// field and the source verbatim.
/// </summary>
public interface IRuleStore
{
    /// <summary>Creates or replaces a rule's source and record. Source is written before the record.</summary>
    void Save(StoredRule rule);

    /// <summary>Replaces a rule's record only, leaving its source untouched — used for status changes.</summary>
    void Update(RuleRecord record);

    /// <summary>The record for <paramref name="id"/>, or null if the store has no such rule.</summary>
    RuleRecord? Find(string id);

    /// <summary>The source text for a rule. The record carries the origin a file store needs to find it.</summary>
    string ReadSource(RuleRecord record);

    /// <summary>Every rule's record. Sources are read lazily via <see cref="ReadSource"/>, not here.</summary>
    IReadOnlyList<RuleRecord> LoadAll();
}

/// <summary>The one hash both the engine (when saving/verifying) and a store agree on.</summary>
internal static class RuleHash
{
    public static string Sha256Hex(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}
