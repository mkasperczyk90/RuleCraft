namespace RuleCraft.Tests;

public interface ITestDiscount
{
    decimal GetDiscount(TestOrder order);
}

public sealed record TestOrder(decimal Total, string Customer, int ItemCount);

/// <summary>The `then` vocabulary for JSON rules in tests: a flat discount.</summary>
public sealed record TestDiscountAction(decimal Discount);

public sealed class FlatTestDiscount(decimal discount) : ITestDiscount
{
    public decimal GetDiscount(TestOrder order) => discount;
}

public enum CustomerTier
{
    Bronze,
    Silver,
    Gold,
}

public sealed record TestCustomer(string Name, string? Country, CustomerTier Tier);

/// <summary>Richer context for path/enum/collection coverage; `Customer` may be null.</summary>
public sealed record RichOrder(
    decimal Total,
    TestCustomer? Customer,
    string[] Tags,
    DateTimeOffset PlacedAt);

/// <summary>A contract change: same contract, context without `Customer`.</summary>
public sealed record TestOrderV2(decimal Total, int ItemCount);

public sealed class ZeroDiscountFallback : ITestDiscount
{
    public decimal GetDiscount(TestOrder order) => 0m;
}

/// <summary>Shared rule-source snippets and a per-test store directory.</summary>
public static class Fixtures
{
    public static string NewStorePath() =>
        Path.Combine(Path.GetTempPath(), "ruleforge-tests", Guid.NewGuid().ToString("N"));

    public static RuleEngineOptions Options(string? storePath = null, bool autoApprove = false)
    {
        var options = new RuleEngineOptions
        {
            StorePath = storePath ?? NewStorePath(),
            AutoApprove = autoApprove,
            TestTimeout = TimeSpan.FromSeconds(5),
        };
        return options;
    }

    /// <summary>An engine with JSON rules enabled over the standard discount fixtures.</summary>
    public static RuleEngine<ITestDiscount, TestOrder> JsonEngine(
        string? storePath = null,
        bool autoApprove = false,
        StringComparison stringComparison = StringComparison.OrdinalIgnoreCase)
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Options(storePath, autoApprove));
        engine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount), stringComparison);
        return engine;
    }

    /// <summary>A valid JSON rule: 10% for orders of 100+, priority 2.</summary>
    public const string BigOrderJsonRule =
        """
        {
          "name": "big-order-json",
          "priority": 2,
          "when": { "field": "Total", "op": "gte", "value": 100 },
          "then": { "discount": 0.10 },
          "tests": [
            { "name": "150 applies", "context": { "Total": 150, "Customer": "alice", "ItemCount": 1 }, "applies": true },
            { "name": "50 does not", "context": { "Total": 50, "Customer": "alice", "ItemCount": 1 }, "applies": false }
          ]
        }
        """;

    /// <summary>A valid rule: 10% discount for orders of 100+, priority 1.</summary>
    public const string BigOrderRule =
        """
        using RuleCraft;
        using RuleCraft.Tests;

        namespace RuleCraft.Generated.BigOrder;

        public sealed class BigOrderRule : IRule<ITestDiscount, TestOrder>, ITestDiscount
        {
            public bool AppliesTo(TestOrder context) => context.Total >= 100m;
            public ITestDiscount Implementation => this;
            public int Priority => 1;
            public decimal GetDiscount(TestOrder order) => 0.10m;
        }

        public sealed class PositiveTest : IRuleTest
        {
            public string Name => "big order matches and gets 10%";
            public TestResult Run(TestContext context)
            {
                var rule = new BigOrderRule();
                RuleAssert.True(rule.AppliesTo(new TestOrder(150m, "a", 1)));
                RuleAssert.Equal(0.10m, rule.GetDiscount(new TestOrder(150m, "a", 1)));
                return TestResult.Passed();
            }
        }

        public sealed class NegativeTest : IRuleTest
        {
            public string Name => "small order does not match";
            public TestResult Run(TestContext context)
            {
                RuleAssert.False(new BigOrderRule().AppliesTo(new TestOrder(50m, "a", 1)));
                return TestResult.Passed();
            }
        }
        """;

    /// <summary>A valid rule matching the same orders as BigOrderRule but with priority 5.</summary>
    public const string VipRule =
        """
        using RuleCraft;
        using RuleCraft.Tests;

        namespace RuleCraft.Generated.Vip;

        public sealed class VipRule : IRule<ITestDiscount, TestOrder>, ITestDiscount
        {
            public bool AppliesTo(TestOrder context) => context.Total >= 100m;
            public ITestDiscount Implementation => this;
            public int Priority => 5;
            public decimal GetDiscount(TestOrder order) => 0.20m;
        }

        public sealed class VipTest : IRuleTest
        {
            public string Name => "vip discount is 20%";
            public TestResult Run(TestContext context)
            {
                RuleAssert.Equal(0.20m, new VipRule().GetDiscount(new TestOrder(150m, "a", 1)));
                return TestResult.Passed();
            }
        }
        """;
}
