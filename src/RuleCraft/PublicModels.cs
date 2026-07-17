namespace RuleCraft;

public enum RuleStatus
{
    PendingApproval,
    Approved,
    Rejected,
    Quarantined,
    Disabled,
}

/// <summary>What a rule actually is — how it is expressed and executed.</summary>
public enum RuleOrigin
{
    /// <summary>C# source in the store, compiled at runtime: LLM-generated or hand-written.</summary>
    Compiled,

    /// <summary>Registered from host code via <c>AddStaticRule</c>: never compiled, stored or queued for approval.</summary>
    Static,

    /// <summary>A JSON-DSL document in the store, interpreted by the engine. No compilation, no assembly.</summary>
    Json,
}

public enum ResolutionPolicy
{
    /// <summary>Among matching rules pick the highest <see cref="IRule{TContract,TContext}.Priority"/>; newest wins ties. Default.</summary>
    HighestPriority,

    /// <summary>Pick the first matching rule in registration order.</summary>
    FirstMatch,

    /// <summary>Throw <see cref="AmbiguousRuleMatchException"/> when more than one rule matches.</summary>
    ThrowOnAmbiguity,
}

public sealed record SecurityFinding(string Message, int Line);

public sealed record TestCaseResult(string Name, TestOutcome Outcome, string? Message);

/// <summary>
/// Everything a reviewer needs to see about a candidate.
/// <paramref name="Diagnostics"/> are compiler errors for C# rules and parse/validation errors for
/// JSON rules; <paramref name="SecurityFindings"/> are always empty for JSON rules, which cannot
/// express unsafe code in the first place.
/// </summary>
public sealed record ValidationReport(
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<SecurityFinding> SecurityFindings,
    IReadOnlyList<TestCaseResult> TestResults)
{
    public bool Success =>
        Diagnostics.Count == 0
        && SecurityFindings.Count == 0
        && TestResults.All(t => t.Outcome != TestOutcome.Failed);
}

/// <summary>
/// A rule as the engine sees it, whatever its kind or status.
/// <paramref name="EvaluationOrder"/> is the 0-based position in the order predicates are consulted
/// by <c>Resolve</c> under the configured <see cref="ResolutionPolicy"/>; null when the rule is not
/// loaded.
/// </summary>
public sealed record RuleInfo(
    string Id,
    string Name,
    RuleStatus Status,
    RuleOrigin Origin,
    int Priority,
    string? Spec,
    DateTimeOffset CreatedUtc,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAtUtc,
    string? StatusReason,
    bool IsLoaded,
    int? EvaluationOrder);

/// <summary>
/// A validated candidate awaiting human approval. Expose this from your own review endpoint.
/// <paramref name="Source"/> is the C# source or JSON document exactly as stored — pick a viewer
/// by <paramref name="Origin"/>.
/// </summary>
public sealed record PendingRule(
    string Id,
    string Name,
    RuleOrigin Origin,
    string? Spec,
    string Source,
    ValidationReport Report,
    DateTimeOffset CreatedUtc);
