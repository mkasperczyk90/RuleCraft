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

        TestCaseResult TimedOut() => new(name, TestOutcome.Failed,
            $"Test timed out after {timeout.TotalSeconds:0.#}s (its thread may still be running).");

        try
        {
            // Two conditions, because the timeout can win either way. task.Wait times out when the
            // body ignores the token and never returns (its thread then leaks — see summary). But a
            // body that DOES observe the token races the wall clock: it can notice cancellation,
            // return, and complete the task inside Wait's own window. Reporting task.Result then
            // would pass a test that only returned because we cancelled it — the fail-open bug this
            // guards against. If cancellation fired at all, it timed out, whatever the body returned.
            if (!task.Wait(timeout) || cts.IsCancellationRequested)
                return TimedOut();

            var result = task.Result;
            return new TestCaseResult(name, result.Outcome, result.Message);
        }
        catch (Exception ex)
        {
            // A cooperative body may react to the token by throwing rather than returning; that is
            // the same timeout, not a test failure with a "task was canceled" message.
            if (cts.IsCancellationRequested && Unwrap(ex) is OperationCanceledException)
                return TimedOut();

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
