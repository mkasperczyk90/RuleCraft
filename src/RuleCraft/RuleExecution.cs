using RuleCraft.Testing;

namespace RuleCraft;

/// <summary>
/// Opt-in bounded invocation for a resolved rule's implementation. <see cref="RuleEngine{TContract,TContext}.Resolve"/>
/// hands back an implementation whose methods run unguarded on your thread; wrap the call here when a
/// request thread must never block on a pathological (LLM-generated) rule.
///
/// The same honest caveat as the validation harness applies: in-process code cannot be forcibly
/// stopped. On timeout this returns <c>false</c> promptly, but the runaway thread keeps running until
/// it returns on its own — a leak, not a kill. This is a backstop for latency, not a security
/// boundary; the security boundary is the acceptance tests and the human approval gate.
/// </summary>
public static class RuleExecution
{
    /// <summary>
    /// Runs <paramref name="invocation"/> and waits up to <paramref name="timeout"/>. Returns true
    /// with <paramref name="result"/> when it completed in time; false with <paramref name="error"/>
    /// set to the unwrapped exception it threw, or a <see cref="TimeoutException"/> when it ran long.
    /// </summary>
    public static bool TryInvoke<T>(Func<T> invocation, TimeSpan timeout, out T? result, out Exception? error)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be positive.");

        result = default;
        error = null;

        var task = Task.Run(invocation);
        try
        {
            // Wait returns true only if the body finished within the budget of its own accord — the
            // same reasoning as TestExecution.RunWithTimeout: never trust a result produced after the
            // deadline, and never fail a legitimately-slow one at the boundary.
            if (!task.Wait(timeout))
            {
                error = new TimeoutException(
                    $"Rule invocation exceeded {timeout.TotalSeconds:0.#}s (its thread may still be running).");
                return false;
            }

            result = task.Result;
            return true;
        }
        catch (Exception ex)
        {
            error = TestExecution.Unwrap(ex);
            return false;
        }
    }
}
