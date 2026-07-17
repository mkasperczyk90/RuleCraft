using System.Reflection;

namespace RuleCraft.Testing;

/// <summary>Shared test-running mechanics: a wall-clock cap and readable exception messages.</summary>
internal static class TestExecution
{
    /// <summary>
    /// Runs arbitrary developer/generated test code with a timeout. In-process execution cannot
    /// stop a runaway CPU loop — a timed-out test is reported as failed and its thread may leak.
    /// </summary>
    public static TestCaseResult RunWithTimeout(string name, TimeSpan timeout, Func<CancellationToken, TestResult> body)
    {
        using var cts = new CancellationTokenSource(timeout);
        var task = Task.Run(() => body(cts.Token), cts.Token);
        try
        {
            if (!task.Wait(timeout))
                return new TestCaseResult(name, TestOutcome.Failed,
                    $"Test timed out after {timeout.TotalSeconds:0.#}s (its thread may still be running).");

            var result = task.Result;
            return new TestCaseResult(name, result.Outcome, result.Message);
        }
        catch (Exception ex)
        {
            return new TestCaseResult(name, TestOutcome.Failed, Unwrap(ex).Message);
        }
    }

    /// <summary>Digs the real exception out of the reflection/task wrappers a test run adds.</summary>
    public static Exception Unwrap(Exception ex)
    {
        while (true)
        {
            switch (ex)
            {
                case AggregateException { InnerException: { } inner }:
                    ex = inner;
                    continue;
                case TargetInvocationException { InnerException: { } inner }:
                    ex = inner;
                    continue;
                default:
                    return ex;
            }
        }
    }
}
