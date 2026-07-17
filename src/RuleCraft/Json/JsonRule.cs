namespace RuleCraft.Json;

/// <summary>
/// A rule expressed as a JSON document: the predicate is an interpreted condition tree, the
/// implementation was built once by the host's factory from the document's <c>then</c> node.
/// Like every other rule kind, the implementation is shared across concurrent resolutions and
/// must therefore be thread-safe.
/// </summary>
internal sealed class JsonRule<TContract, TContext>(
    ConditionNode when,
    TContract implementation,
    int priority) : IRule<TContract, TContext>
    where TContract : class
{
    public bool AppliesTo(TContext context) => context is not null && when.Evaluate(context);

    public TContract Implementation => implementation;

    public int Priority => priority;
}
