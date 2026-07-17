namespace RuleCraft;

/// <summary>Thrown by <see cref="RuleAssert"/>; converted to a failed <see cref="TestResult"/> by the test harness.</summary>
public sealed class RuleAssertException : Exception
{
    public RuleAssertException(string message) : base(message)
    {
    }
}

/// <summary>Minimal assertion helpers for generated rule tests (no external test framework needed).</summary>
public static class RuleAssert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
            throw new RuleAssertException(message ?? "Expected condition to be true.");
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition)
            throw new RuleAssertException(message ?? "Expected condition to be false.");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new RuleAssertException(message ?? $"Expected: {expected}, actual: {actual}.");
    }

    public static void NotNull(object? value, string? message = null)
    {
        if (value is null)
            throw new RuleAssertException(message ?? "Expected value to be non-null.");
    }
}
