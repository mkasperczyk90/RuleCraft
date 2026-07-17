namespace RuleCraft.Sample.Discounts;

/// <summary>
/// A rule written by hand and registered with <c>AddStaticRule</c> at startup: it lives in this
/// repository, so code review is its approval gate — no compilation, no store, no queue.
/// It competes with JSON and LLM-generated rules by priority like any other rule.
/// </summary>
public sealed class BulkOrderRule : IRule<IDiscountRule, Order>, IDiscountRule
{
    /// <summary>The predicate: does this rule apply?</summary>
    public bool AppliesTo(Order context) => context.ItemCount >= 50;

    /// <summary>This class is both the rule and its implementation.</summary>
    public IDiscountRule Implementation => this;

    /// <summary>Higher wins when several rules match one order.</summary>
    public int Priority => 1;

    public decimal GetDiscount(Order order) => 0.05m;
}
