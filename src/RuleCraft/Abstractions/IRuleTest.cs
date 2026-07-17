namespace RuleCraft;

/// <summary>
/// A self-test shipped alongside a generated rule. All tests found in a candidate
/// assembly are executed in a throwaway load context before the rule can be approved.
/// </summary>
public interface IRuleTest
{
    string Name { get; }

    TestResult Run(TestContext context);
}

/// <summary>Execution context passed to rule tests.</summary>
public sealed class TestContext
{
    public TestContext(CancellationToken cancellationToken) => CancellationToken = cancellationToken;

    /// <summary>Signalled when the test exceeds the configured timeout.</summary>
    public CancellationToken CancellationToken { get; }
}
