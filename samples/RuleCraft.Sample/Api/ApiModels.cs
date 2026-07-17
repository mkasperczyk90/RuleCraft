namespace RuleCraft.Sample.Api;

/// <param name="Format">"json" (default — sandboxed) or "csharp" (the escalation path).</param>
public sealed record AddRuleRequest(string Spec, string? Name, string? Format);

/// <summary>A rule document or C# file written by hand, submitted without the LLM.</summary>
public sealed record AddRuleFromSourceRequest(string Source, string? Name, string? Spec);

public sealed record ApproveRequest(string ApprovedBy);

public sealed record RejectRequest(string Reason);

public sealed record EvaluationResult(decimal Total, decimal Discount, decimal FinalTotal, bool MatchedByRule);
