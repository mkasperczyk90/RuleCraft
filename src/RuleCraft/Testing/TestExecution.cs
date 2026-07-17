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
        // The token is deliberately NOT wired to a timer: we cancel only after the wait has already
        // failed. That is what makes the verdict unambiguous. Wire it to fire at `timeout` and the
        // two clocks race at the deadline — a body finishing a hair early looks the same as one that
        // only returned because it was cancelled, so you must either trust a cancelled result
        // (fail-open) or reject a legitimately-slow pass (fail-closed). Cancelling after the fact
        // avoids the dilemma outright.
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => body(cts.Token), cts.Token);

        TestCaseResult TimedOut() => new(name, TestOutcome.Failed,
            $"Test timed out after {timeout.TotalSeconds:0.#}s (its thread may still be running).");

        try
        {
            // Wait returns true only if the body completed strictly within the budget, of its own
            // accord — it never saw cancellation, so its result is trustworthy even at the boundary.
            if (!task.Wait(timeout))
            {
                // Budget spent. Signal the token so a cooperative body stops and its thread is freed
                // promptly; one that ignores the token leaks until it returns (see summary).
                cts.Cancel();
                return TimedOut();
            }

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
