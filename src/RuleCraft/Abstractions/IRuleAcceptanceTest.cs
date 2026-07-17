namespace RuleCraft;

/// <summary>
/// A developer-authored invariant executed against every candidate implementation,
/// regardless of which spec produced it (e.g. "a discount never exceeds 100%").
/// This is the non-circular safety net: generated tests verify the spec,
/// acceptance tests verify the contract.
/// </summary>
public interface IRuleAcceptanceTest<in TContract> where TContract : class
{
    string Name { get; }

    TestResult Run(TContract implementation);
}
