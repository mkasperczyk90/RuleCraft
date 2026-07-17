namespace RuleCraft.Tests;

/// <summary>
/// The README's quickstart, kept executable. It is the first code anyone reads and the one snippet
/// nobody can afford to have rot — so it lives here rather than only in prose. Keep the two in step.
/// </summary>
public class QuickstartTests
{
    public interface IDiscountRule
    {
        decimal GetDiscount(QuickstartOrder order);
    }

    public sealed record QuickstartOrder(decimal Total, string CustomerType);

    /// <summary>The entire vocabulary a JSON rule may fill in.</summary>
    public sealed record DiscountAction(decimal Discount);

    public sealed class FlatDiscount(decimal discount) : IDiscountRule
    {
        public decimal GetDiscount(QuickstartOrder order) => discount;
    }

    [Fact]
    public void The_readme_quickstart_works()
    {
        using var engine = new RuleEngine<IDiscountRule, QuickstartOrder>(
            new RuleEngineOptions { StorePath = Fixtures.NewStorePath() });

        engine.EnableJsonRules<DiscountAction>(then => new FlatDiscount(then.Discount));

        var rule = engine.AddJsonRuleFromSource(
            """
            {
              "when":  { "field": "Total", "op": "gte", "value": 100 },
              "then":  { "discount": 0.10 },
              "tests": [
                { "context": { "total": 150, "customerType": "vip" }, "applies": true  },
                { "context": { "total": 50,  "customerType": "vip" }, "applies": false }
              ]
            }
            """);

        Assert.Equal(RuleStatus.PendingApproval, rule.Status);

        engine.Approve(rule.Id, approvedBy: "you@corp.com");

        var order = new QuickstartOrder(150m, "vip");
        Assert.Equal(0.10m, engine.Resolve(order)!.GetDiscount(order));

        // …and an order the rule does not match resolves to nothing (no fallback was set).
        Assert.Null(engine.Resolve(new QuickstartOrder(50m, "vip")));
    }
}
