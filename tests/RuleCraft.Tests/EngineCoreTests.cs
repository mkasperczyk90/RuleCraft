namespace RuleCraft.Tests;

public class EngineCoreTests
{
    [Fact]
    public async Task Rule_from_source_is_compiled_loaded_and_dispatched_through_the_contract()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));

        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule, name: "big-order");

        Assert.Equal(RuleStatus.Approved, info.Status);
        Assert.True(info.IsLoaded);

        var implementation = engine.Resolve(new TestOrder(150m, "alice", 2));
        Assert.NotNull(implementation);
        Assert.Equal(0.10m, implementation!.GetDiscount(new TestOrder(150m, "alice", 2)));
    }

    [Fact]
    public async Task Fallback_is_returned_when_no_rule_matches()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));
        var fallback = new ZeroDiscountFallback();
        engine.SetFallback(fallback);

        engine.AddRuleFromSource(Fixtures.BigOrderRule);

        var resolved = engine.Resolve(new TestOrder(10m, "bob", 1));
        Assert.Same(fallback, resolved);
    }

    [Fact]
    public void Resolve_without_rules_and_fallback_returns_null()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        Assert.Null(engine.Resolve(new TestOrder(10m, "bob", 1)));
    }

    [Fact]
    public async Task Highest_priority_rule_wins_and_ResolveAll_orders_by_priority()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));
        engine.AddRuleFromSource(Fixtures.BigOrderRule);
        engine.AddRuleFromSource(Fixtures.VipRule);

        var order = new TestOrder(200m, "carol", 3);

        var winner = engine.Resolve(order);
        Assert.Equal(0.20m, winner!.GetDiscount(order));

        var all = engine.ResolveAll(order);
        Assert.Equal(2, all.Count);
        Assert.Equal(0.20m, all[0].GetDiscount(order));
        Assert.Equal(0.10m, all[1].GetDiscount(order));
    }

    [Fact]
    public async Task ThrowOnAmbiguity_policy_throws_when_two_rules_match()
    {
        var options = Fixtures.Options(autoApprove: true);
        options.ResolutionPolicy = ResolutionPolicy.ThrowOnAmbiguity;
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);
        engine.AddRuleFromSource(Fixtures.BigOrderRule);
        engine.AddRuleFromSource(Fixtures.VipRule);

        Assert.Throws<AmbiguousRuleMatchException>(() => engine.Resolve(new TestOrder(200m, "carol", 3)));
    }

    [Fact]
    public async Task Removed_rule_no_longer_resolves()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        Assert.NotNull(engine.Resolve(new TestOrder(150m, "dave", 1)));

        engine.RemoveRule(info.Id);
        Assert.Null(engine.Resolve(new TestOrder(150m, "dave", 1)));
    }

    [Fact]
    public async Task Rule_with_throwing_predicate_is_skipped_not_fatal()
    {
        const string throwingRule =
            """
            using System;
            using RuleCraft;
            using RuleCraft.Tests;

            namespace RuleCraft.Generated.Throwing;

            public sealed class ThrowingRule : IRule<ITestDiscount, TestOrder>, ITestDiscount
            {
                public bool AppliesTo(TestOrder context) => throw new InvalidOperationException("boom");
                public ITestDiscount Implementation => this;
                public decimal GetDiscount(TestOrder order) => 0.99m;
            }

            public sealed class SmokeTest : IRuleTest
            {
                public string Name => "instantiates";
                public TestResult Run(TestContext context)
                {
                    RuleAssert.NotNull(new ThrowingRule());
                    return TestResult.Passed();
                }
            }
            """;

        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));
        engine.AddRuleFromSource(throwingRule);

        Assert.Null(engine.Resolve(new TestOrder(150m, "eve", 1)));
    }

    [Fact]
    public async Task Invalid_source_throws_RuleValidationException_with_diagnostics()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());

        var ex = Assert.Throws<RuleValidationException>(() =>
            engine.AddRuleFromSource("public class Nope {"));

        Assert.NotEmpty(ex.Report.Diagnostics);
    }

    [Fact]
    public async Task Failing_generated_test_rejects_the_candidate()
    {
        const string failingTestRule =
            """
            using RuleCraft;
            using RuleCraft.Tests;

            namespace RuleCraft.Generated.Failing;

            public sealed class SomeRule : IRule<ITestDiscount, TestOrder>, ITestDiscount
            {
                public bool AppliesTo(TestOrder context) => true;
                public ITestDiscount Implementation => this;
                public decimal GetDiscount(TestOrder order) => 0.10m;
            }

            public sealed class WrongExpectationTest : IRuleTest
            {
                public string Name => "wrong expectation";
                public TestResult Run(TestContext context)
                {
                    RuleAssert.Equal(0.50m, new SomeRule().GetDiscount(new TestOrder(1m, "x", 1)));
                    return TestResult.Passed();
                }
            }
            """;

        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());

        var ex = Assert.Throws<RuleValidationException>(() =>
            engine.AddRuleFromSource(failingTestRule));

        Assert.Contains(ex.Report.TestResults, t => t.Outcome == TestOutcome.Failed);
    }
}
