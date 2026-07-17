namespace RuleCraft.Engine;

/// <summary>
/// One lock per rule id, striped over a fixed table so it cannot grow with the store.
///
/// The approval workflow is a read-decide-write sequence over a file on disk and a registry in
/// memory, and without this it races: two threads approving the same id both pass the status
/// check and both compile, the loser failing only at registry insert — after doing the work. Worse,
/// an Approve interleaved with a Reject can leave a rule loaded and serving traffic while the store
/// records it as rejected.
///
/// Collisions between two different ids are possible and harmless: they only serialize two
/// operations that had no reason to overlap. <c>Resolve</c> never takes these locks.
/// </summary>
internal sealed class RuleLocks
{
    private readonly object[] _stripes;

    public RuleLocks(int stripes = 16)
    {
        _stripes = new object[stripes];
        for (var i = 0; i < stripes; i++)
            _stripes[i] = new object();
    }

    public object For(string ruleId) =>
        _stripes[(uint)StringComparer.Ordinal.GetHashCode(ruleId) % _stripes.Length];
}
