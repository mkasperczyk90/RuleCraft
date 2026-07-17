namespace RuleCraft.Sample.Discounts;

/// <summary>
/// The vocabulary a JSON rule's `then` may use — and the reason JSON rules are a real sandbox.
/// A rule document cannot invent behaviour; it can only fill in this record, which
/// <see cref="DiscountActions.Build"/> is the only code that reads.
///
/// It also does triple duty: System.Text.Json validates against it, the LLM prompt is rendered
/// from it (so the vocabulary can't drift), and changing it breaks the factory at compile time.
/// Rules need richer outcomes? Widen this deliberately, in C#, with tests.
/// </summary>
public sealed record DiscountAction(decimal Discount);

public static class DiscountActions
{
    /// <summary>Turns a validated `then` node into the implementation the engine will dispatch to.</summary>
    public static IDiscountRule Build(DiscountAction action) => new FlatDiscount(action.Discount);
}

/// <summary>A fixed discount, whatever the order.</summary>
public sealed class FlatDiscount(decimal discount) : IDiscountRule
{
    public decimal GetDiscount(Order order) => discount;
}

/// <summary>Used when no rule matches — the engine's fallback.</summary>
public sealed class NoDiscount : IDiscountRule
{
    public decimal GetDiscount(Order order) => 0m;
}
