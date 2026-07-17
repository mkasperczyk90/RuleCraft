using System.Diagnostics;
using RuleCraft.Testing;

namespace RuleCraft.Tests;

/// <summary>
/// Guards <see cref="TestExecution.RunWithTimeout"/> directly, at unit speed. The full-pipeline
/// version of the first case is <c>UnloadTests.Test_harness_reports_timeout_as_failure</c>, which
/// flaked on CI: two independent timeouts raced, and when the cancelled body returned before the
/// wait's own deadline the harness reported the runaway test as <b>passed</b> (fail-open).
/// </summary>
public class TestExecutionTests
{
    [Fact]
    public void A_body_that_returns_only_after_cancellation_is_a_timeout()
    {
        var result = TestExecution.RunWithTimeout("spin", TimeSpan.FromMilliseconds(50), ct =>
        {
            // Cooperative: spins until cancelled, then "passes" — passing only because it was
            // cancelled is exactly the case that must not be trusted. The wall-clock bound keeps a
            // regressed harness from hanging the whole suite instead of failing this one test.
            var safety = Stopwatch.StartNew();
            while (!ct.IsCancellationRequested && safety.Elapsed < TimeSpan.FromSeconds(5))
            {
            }

            return TestResult.Passed();
        });

        Assert.Equal(TestOutcome.Failed, result.Outcome);
        Assert.Contains("timed out", result.Message);
    }

    [Fact]
    public void A_body_that_throws_on_cancellation_is_also_a_timeout()
    {
        var result = TestExecution.RunWithTimeout("throw-on-cancel", TimeSpan.FromMilliseconds(50), ct =>
        {
            var safety = Stopwatch.StartNew();
            while (safety.Elapsed < TimeSpan.FromSeconds(5))
                ct.ThrowIfCancellationRequested();

            return TestResult.Passed();
        });

        Assert.Equal(TestOutcome.Failed, result.Outcome);
        Assert.Contains("timed out", result.Message);
    }

    [Fact]
    public void A_fast_passing_test_stays_passed()
    {
        var result = TestExecution.RunWithTimeout("quick", TimeSpan.FromSeconds(30), _ => TestResult.Passed());

        Assert.Equal(TestOutcome.Passed, result.Outcome);
    }

    [Fact]
    public void A_genuine_test_failure_keeps_its_own_message()
    {
        var result = TestExecution.RunWithTimeout("fails", TimeSpan.FromSeconds(30),
            _ => TestResult.Failed("expected 3, got 4"));

        Assert.Equal(TestOutcome.Failed, result.Outcome);
        Assert.Equal("expected 3, got 4", result.Message);
    }
}
