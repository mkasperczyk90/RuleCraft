using System.Collections.Immutable;
using RuleCraft.Loading;

namespace RuleCraft.Engine;

/// <summary>
/// A rule currently loaded and dispatchable. <see cref="Context"/> is null for static rules
/// registered from host code — there is no generated assembly to unload.
/// </summary>
internal sealed record ActiveRule<TContract, TContext>(
    string Id,
    string Name,
    long Sequence,
    RuleOrigin Origin,
    DateTimeOffset CreatedUtc,
    IRule<TContract, TContext> Rule,
    RuleAssemblyLoadContext? Context)
    where TContract : class
{
    /// <summary>
    /// Both of these run on every <c>Resolve</c>, so a rule that throws for every context throws
    /// once per request. Counters live on the entry, so they are freed with the rule rather than
    /// accumulating in a table keyed by id.
    /// </summary>
    public LogThrottle PredicateFailures { get; } = new();

    /// <inheritdoc cref="PredicateFailures"/>
    public LogThrottle PriorityFailures { get; } = new();
}

/// <summary>
/// Rate-limits log lines for a fault that repeats per request. Logging every occurrence buries the
/// rest of the log; logging only the first hides that it never stopped — so: the first few, then
/// every thousandth, always carrying the running total.
///
/// Deliberately not auto-quarantine: a predicate may throw only for some contexts, and disabling a
/// rule that serves the other 99% of traffic — permanently, on disk — is the more expensive mistake.
/// </summary>
internal sealed class LogThrottle
{
    private long _count;

    public bool ShouldLog(out long count)
    {
        count = Interlocked.Increment(ref _count);
        return count <= 3 || count % 1000 == 0;
    }
}

/// <summary>
/// Lock-free on the read path: <see cref="Snapshot"/> is an immutable array swapped
/// atomically by writers. Predicates (user code) never run under a lock.
/// </summary>
internal sealed class RuleRegistry<TContract, TContext> where TContract : class
{
    private readonly object _writeLock = new();
    private ImmutableArray<ActiveRule<TContract, TContext>> _rules = [];
    private long _sequence;

    public ImmutableArray<ActiveRule<TContract, TContext>> Snapshot => _rules;

    public ActiveRule<TContract, TContext> Add(
        string id,
        string name,
        RuleOrigin origin,
        IRule<TContract, TContext> rule,
        RuleAssemblyLoadContext? context)
    {
        lock (_writeLock)
        {
            if (_rules.Any(r => r.Id == id))
                throw new RuleStateException($"Rule '{id}' is already loaded.");

            var entry = new ActiveRule<TContract, TContext>(
                id, name, ++_sequence, origin, DateTimeOffset.UtcNow, rule, context);
            _rules = _rules.Add(entry);
            return entry;
        }
    }

    public ActiveRule<TContract, TContext>? Remove(string id)
    {
        lock (_writeLock)
        {
            var entry = _rules.FirstOrDefault(r => r.Id == id);
            if (entry is not null)
                _rules = _rules.Remove(entry);
            return entry;
        }
    }

    public bool Contains(string id) => _rules.Any(r => r.Id == id);

    /// <summary>Empties the registry and hands back what was in it, so the caller can unload it.</summary>
    public ImmutableArray<ActiveRule<TContract, TContext>> Clear()
    {
        lock (_writeLock)
        {
            var entries = _rules;
            _rules = [];
            return entries;
        }
    }
}
