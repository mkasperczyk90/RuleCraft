using RuleCraft.Loading;

namespace RuleCraft.Engine;

/// <summary>
/// A rule ready to enter the registry. <see cref="Context"/> is null for kinds that produce no
/// assembly (static and JSON rules) — there is nothing to unload.
/// </summary>
internal sealed record LoadedRule<TContract, TContext>(
    IRule<TContract, TContext> Rule,
    RuleAssemblyLoadContext? Context)
    where TContract : class;

/// <summary>
/// Result of validating a candidate. <see cref="Load"/> is non-null if and only if validation
/// passed — the nullability *is* the invariant, so there is no separate success flag and no
/// untyped artifact to cast. Validation already knows how to materialize the rule, so it hands
/// back a closure instead of raw bytes.
///
/// <paramref name="SuggestedName"/> is the name the document gave itself, if any; the caller's
/// explicit name still wins.
/// </summary>
internal sealed record PipelineOutcome<TContract, TContext>(
    ValidationReport Report,
    int Priority,
    Func<LoadedRule<TContract, TContext>>? Load,
    string? SuggestedName = null)
    where TContract : class
{
    public bool Success => Load is not null;

    public static PipelineOutcome<TContract, TContext> Failed(ValidationReport report) => new(report, 0, null);
}

/// <summary>
/// One way of expressing a rule (compiled C# or JSON-DSL). The engine drives every kind through
/// the same validate → queue → approve → load pipeline and stays kind-blind otherwise.
/// </summary>
internal interface IRuleKind<TContract, TContext> where TContract : class
{
    RuleOrigin Origin { get; }

    PipelineOutcome<TContract, TContext> Validate(string ruleId, string source);
}
