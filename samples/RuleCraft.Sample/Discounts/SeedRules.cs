namespace RuleCraft.Sample.Discounts;

/// <summary>
/// Rules shipped with the sample so the app demonstrates itself on first run — open the console
/// and there is already one rule of each kind live, no key and no typing required.
///
/// They live in this repository, so — like <see cref="BulkOrderRule"/> — code review is their
/// approval gate, and the seeding below approves them on the app's behalf. Rules that arrive from
/// users at runtime still go through the human approval queue.
///
/// The <c>rules/</c> folder is the engine's store: runtime data, deliberately not checked in.
/// Seeding here is what replaces shipping it.
/// </summary>
public static class SeedRules
{
    public const string PolishOrdersName = "seed-pl-orders";
    public const string VipOrdersName = "seed-vip-orders";

    /// <summary>Orders shipped to Poland of 200 or more get 8% off.</summary>
    private const string PolishOrders =
        """
        {
          "name": "seed-pl-orders",
          "priority": 2,
          "when": {
            "all": [
              { "field": "country", "op": "eq",  "value": "PL" },
              { "field": "total",   "op": "gte", "value": 200 }
            ]
          },
          "then": { "discount": 0.08 },
          "tests": [
            { "name": "PL order of 300 applies",
              "context": { "total": 300, "customerType": "regular", "itemCount": 1, "country": "PL" },
              "applies": true },
            { "name": "German order does not apply",
              "context": { "total": 300, "customerType": "regular", "itemCount": 1, "country": "DE" },
              "applies": false },
            { "name": "small PL order does not apply",
              "context": { "total": 50, "customerType": "regular", "itemCount": 1, "country": "PL" },
              "applies": false }
          ]
        }
        """;

    /// <summary>
    /// The escalation path: a rule written in C#, compiled by Roslyn at runtime and loaded into
    /// its own collectible AssemblyLoadContext — what the LLM produces when a spec cannot be
    /// expressed in the JSON DSL. It ships with its own tests, exactly as a generated rule must.
    /// </summary>
    private const string VipOrders =
        """
        using RuleCraft;
        using RuleCraft.Sample.Discounts;

        namespace RuleCraft.Generated.SeedVip;

        public sealed class VipOrderRule : IRule<IDiscountRule, Order>, IDiscountRule
        {
            public bool AppliesTo(Order context) => context.CustomerType == "vip" && context.Total >= 500m;
            public IDiscountRule Implementation => this;
            public int Priority => 1;
            public decimal GetDiscount(Order order) => 0.15m;
        }

        public sealed class VipMatchTest : IRuleTest
        {
            public string Name => "vip orders of 500+ match, others do not";
            public TestResult Run(TestContext context)
            {
                var rule = new VipOrderRule();
                RuleAssert.True(rule.AppliesTo(new Order(600m, "vip", 2, "DE")));
                RuleAssert.False(rule.AppliesTo(new Order(600m, "regular", 2, "DE")));
                RuleAssert.False(rule.AppliesTo(new Order(100m, "vip", 2, "DE")));
                return TestResult.Passed();
            }
        }

        public sealed class VipDiscountTest : IRuleTest
        {
            public string Name => "vip discount is 15%";
            public TestResult Run(TestContext context)
            {
                RuleAssert.Equal(0.15m, new VipOrderRule().GetDiscount(new Order(600m, "vip", 2, "DE")));
                return TestResult.Passed();
            }
        }
        """;

    /// <summary>
    /// Adds and approves the seed rules the first time the app runs against a store. Each is
    /// skipped once it exists in any state, so rejecting or unloading one in the console sticks.
    /// </summary>
    public static void EnsureSeeded(RuleEngine<IDiscountRule, Order> engine, ILogger logger)
    {
        Seed(engine, logger, PolishOrdersName, "JSON",
            () => engine.AddJsonRuleFromSource(
                PolishOrders, spec: "Orders shipped to Poland of 200 or more get 8% off"));

        Seed(engine, logger, VipOrdersName, "compiled C#",
            () => engine.AddRuleFromSource(
                VipOrders, VipOrdersName, spec: "VIP customers with orders of 500 or more get 15% off"));
    }

    private static void Seed(
        RuleEngine<IDiscountRule, Order> engine, ILogger logger,
        string name, string kind, Func<RuleInfo> add)
    {
        if (engine.GetRules().Any(rule => rule.Name == name))
            return;

        var info = add();
        engine.Approve(info.Id, approvedBy: "seed (shipped with the sample)");
        logger.LogInformation("Seeded {Kind} rule '{Name}' so the sample works on first run.", kind, info.Name);
    }
}
