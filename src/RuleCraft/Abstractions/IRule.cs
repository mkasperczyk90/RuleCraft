namespace RuleCraft;

/// <summary>
/// A single rule: a predicate deciding whether the rule applies to a given context,
/// plus the implementation of the application contract to use when it does.
/// Generated rule classes implement this interface.
/// </summary>
public interface IRule<TContract, TContext> where TContract : class
{
    /// <summary>Cheap, side-effect-free predicate evaluated on every resolution.</summary>
    bool AppliesTo(TContext context);

    /// <summary>The contract implementation used when <see cref="AppliesTo"/> returns true.</summary>
    TContract Implementation { get; }

    /// <summary>Higher priority wins when multiple rules apply (see <see cref="ResolutionPolicy"/>).</summary>
    int Priority => 0;
}
