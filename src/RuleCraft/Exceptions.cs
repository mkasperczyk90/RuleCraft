namespace RuleCraft;

public class RuleCraftException : Exception
{
    public RuleCraftException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>A candidate failed compilation, security analysis or its tests. Inspect <see cref="Report"/>.</summary>
public sealed class RuleValidationException : RuleCraftException
{
    public RuleValidationException(string message, ValidationReport report) : base(message) => Report = report;

    public ValidationReport Report { get; }
}

public sealed record GenerationAttempt(string Source, ValidationReport Report);

/// <summary>The LLM failed to produce a valid rule within the configured number of attempts.</summary>
public sealed class RuleGenerationException : RuleCraftException
{
    public RuleGenerationException(string message, IReadOnlyList<GenerationAttempt> attempts) : base(message)
        => Attempts = attempts;

    public IReadOnlyList<GenerationAttempt> Attempts { get; }
}

/// <summary>
/// The model reported that the spec cannot be expressed in the target rule kind's grammar, rather
/// than approximating it. Today only the JSON DSL can say this — it deliberately has no arithmetic,
/// aggregation or current date. Escalate the spec to <c>AddRuleAsync</c> (compiled C#), accepting
/// the security trade-off, or narrow it until the DSL fits.
/// </summary>
public sealed class RuleNotExpressibleException : RuleCraftException
{
    public RuleNotExpressibleException(string spec, string reason)
        : base($"This rule cannot be expressed as a JSON rule: {reason} " +
               "Escalate it to AddRuleAsync (compiled C#) or narrow the spec.")
    {
        Spec = spec;
        Reason = reason;
    }

    /// <summary>The spec that was asked for.</summary>
    public string Spec { get; }

    /// <summary>The model's own explanation of what the grammar is missing.</summary>
    public string Reason { get; }
}

public sealed class AmbiguousRuleMatchException : RuleCraftException
{
    public AmbiguousRuleMatchException(IReadOnlyList<string> ruleIds)
        : base($"Multiple rules matched the context: {string.Join(", ", ruleIds)}.")
        => RuleIds = ruleIds;

    public IReadOnlyList<string> RuleIds { get; }
}

public sealed class RuleNotFoundException : RuleCraftException
{
    public RuleNotFoundException(string ruleId) : base($"Rule '{ruleId}' was not found.")
    {
    }
}

/// <summary>Invalid status transition, e.g. approving an already rejected rule.</summary>
public sealed class RuleStateException : RuleCraftException
{
    public RuleStateException(string message) : base(message)
    {
    }
}
