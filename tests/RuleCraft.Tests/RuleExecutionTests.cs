using System.Diagnostics;
using RuleCraft;

namespace RuleCraft.Tests;

public class RuleExecutionTests
{
    [Fact]
    public void A_fast_invocation_returns_its_result()
    {
        var ok = RuleExecution.TryInvoke(() => 41 + 1, TimeSpan.FromSeconds(30), out var result, out var error);

        Assert.True(ok);
        Assert.Equal(42, result);
        Assert.Null(error);
    }

    [Fact]
    public void A_throwing_invocation_is_isolated_and_the_exception_is_unwrapped()
    {
        var ok = RuleExecution.TryInvoke<int>(
            () => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(30), out _, out var error);

        Assert.False(ok);
        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("boom", error!.Message);
    }

    [Fact]
    public void A_runaway_invocation_times_out_instead_of_blocking_forever()
    {
        var ok = RuleExecution.TryInvoke(
            () =>
            {
                var safety = Stopwatch.StartNew();
                while (safety.Elapsed < TimeSpan.FromSeconds(5)) { }
                return 0;
            },
            TimeSpan.FromMilliseconds(50), out _, out var error);

        Assert.False(ok);
        Assert.IsType<TimeoutException>(error);
    }

    [Fact]
    public void A_nonpositive_timeout_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RuleExecution.TryInvoke(() => 1, TimeSpan.Zero, out _, out _));
    }
}
