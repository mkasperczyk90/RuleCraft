namespace RuleCraft.Store;

/// <summary>Sidecar metadata persisted next to each rule's source file.</summary>
internal sealed class RuleMetadata
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public string? Spec { get; set; }

    /// <summary>
    /// What kind of rule this is; drives the source file extension and which kind validates it.
    /// Initialized (not defaulted by enum order) so metadata written before JSON rules existed
    /// still reads back as Compiled.
    /// </summary>
    public RuleOrigin Origin { get; set; } = RuleOrigin.Compiled;

    public RuleStatus Status { get; set; }

    public int Priority { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public string? SourceSha256 { get; set; }

    /// <summary>
    /// Contract and context types this rule was written against, by full name. Null in metadata
    /// written before these fields existed, and then assumed to be the reading engine's own.
    ///
    /// Load-bearing because <see cref="RuleEngineOptions.StorePath"/> defaults to a folder relative
    /// to the process: two engines over different contracts land in the same one unless told
    /// otherwise, and each would read the other's rules as "no longer valid" and quarantine them —
    /// a permanent disk write that fixing the configuration afterwards would not undo.
    /// </summary>
    public string? ContractType { get; set; }

    /// <inheritdoc cref="ContractType"/>
    public string? ContextType { get; set; }

    /// <summary>
    /// Hash of the contract and context API shape at approval time. Same types, different hash means
    /// the application changed underneath the rule — which is the difference between "this rule is
    /// stale" and "this rule was never mine".
    /// </summary>
    public string? ContractFingerprint { get; set; }

    public string? ModelId { get; set; }

    public string? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAtUtc { get; set; }

    /// <summary>Rejection reason / quarantine cause, when applicable.</summary>
    public string? StatusReason { get; set; }

    public ValidationReport? Report { get; set; }
}
