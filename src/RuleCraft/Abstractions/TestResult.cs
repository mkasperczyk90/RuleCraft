namespace RuleCraft;

public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>Outcome of a single <see cref="IRuleTest"/> or <see cref="IRuleAcceptanceTest{TContract}"/>.</summary>
public sealed record TestResult(TestOutcome Outcome, string? Message = null)
{
    public static TestResult Passed() => new(TestOutcome.Passed);

    public static TestResult Failed(string message) => new(TestOutcome.Failed, message);

    public static TestResult Skipped(string? reason = null) => new(TestOutcome.Skipped, reason);
}
