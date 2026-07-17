namespace RuleCraft.Tests;

public class StaticRuleTests
{
    /// <summary>A rule written by hand in the host project — no LLM, no source string, no approval.</summary>
    private sealed class SmallOrderRule : IRule<ITestDiscount, TestOrder>, ITestDiscount
    {
        public bool AppliesTo(TestOrder context) => context.Total < 100m;
        public ITestDiscount Implementation => this;
        public int Priority => 1;
        public decimal GetDiscount(TestOrder order) => 0.02m;
    }

    private sealed class EveryOrderRule(int priority, decimal discount) : IRule<ITestDiscount, TestOrder>, ITestDiscount
    {
        public bool AppliesTo(TestOrder context) => true;
        public ITestDiscount Implementation => this;
        public int Priority { get; } = priority;
        public decimal GetDiscount(TestOrder order) => discount;
    }

    [Fact]
    public void Static_rule_is_live_immediately_without_approval_or_store_entry()
    {
        var storePath = Fixtures.NewStorePath();
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));

        var info = engine.AddStaticRule(new SmallOrderRule());

        Assert.Equal(RuleOrigin.Static, info.Origin);
        Assert.Equal(RuleStatus.Approved, info.Status);
        Assert.True(info.IsLoaded);
        Assert.Equal(0, info.EvaluationOrder);
        Assert.Empty(engine.GetPendingRules());

        var implementation = engine.Resolve(new TestOrder(50m, "alice", 1));
        Assert.NotNull(implementation);
        Assert.Equal(0.02m, implementation!.GetDiscount(new TestOrder(50m, "alice", 1)));

        // Nothing was written to disk: static rules live in code, not in the store.
        Assert.False(Directory.Exists(storePath) && Directory.EnumerateFiles(storePath).Any());
    }

    [Fact]
    public void Static_rule_predicate_is_respected()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        engine.SetFallback(new ZeroDiscountFallback());
        engine.AddStaticRule(new SmallOrderRule());

        Assert.Equal(0.02m, engine.Resolve(new TestOrder(50m, "a", 1))!.GetDiscount(new TestOrder(50m, "a", 1)));
        Assert.Equal(0m, engine.Resolve(new TestOrder(500m, "a", 1))!.GetDiscount(new TestOrder(500m, "a", 1)));
    }

    [Fact]
    public void Static_rule_name_defaults_to_the_type_name_and_can_be_overridden()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());

        Assert.Equal(nameof(SmallOrderRule), engine.AddStaticRule(new SmallOrderRule()).Name);
        Assert.Equal("custom", engine.AddStaticRule(new SmallOrderRule(), name: "custom").Name);
    }

    [Fact]
    public async Task Generated_rule_outranks_a_lower_priority_static_rule()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));
        engine.AddStaticRule(new EveryOrderRule(priority: 1, discount: 0.02m));

        // Fixtures.VipRule has Priority 5 and matches orders of 100+.
        engine.AddRuleFromSource(Fixtures.VipRule);

        var order = new TestOrder(200m, "alice", 1);
        Assert.Equal(0.20m, engine.Resolve(order)!.GetDiscount(order));

        // Below 100 only the static rule matches.
        var small = new TestOrder(50m, "alice", 1);
        Assert.Equal(0.02m, engine.Resolve(small)!.GetDiscount(small));
    }

    [Fact]
    public async Task Static_rule_outranks_a_lower_priority_generated_rule()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));

        // Fixtures.BigOrderRule has Priority 1.
        engine.AddRuleFromSource(Fixtures.BigOrderRule);
        engine.AddStaticRule(new EveryOrderRule(priority: 99, discount: 0.50m));

        var order = new TestOrder(200m, "alice", 1);
        Assert.Equal(0.50m, engine.Resolve(order)!.GetDiscount(order));
    }

    [Fact]
    public async Task Removed_static_rule_stops_resolving_and_needs_no_unload()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var info = engine.AddStaticRule(new SmallOrderRule());

        Assert.NotNull(engine.Resolve(new TestOrder(50m, "a", 1)));

        engine.RemoveRule(info.Id);

        Assert.Null(engine.Resolve(new TestOrder(50m, "a", 1)));
        Assert.Empty(engine.GetRules());
    }

    [Fact]
    public async Task Static_rules_do_not_survive_a_restart()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        engine1.AddStaticRule(new SmallOrderRule());

        // A fresh engine over the same store: static rules live in code and must be re-registered.
        var engine2 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        engine2.ReloadFromStore();

        Assert.Null(engine2.Resolve(new TestOrder(50m, "a", 1)));
        Assert.Empty(engine2.GetRules());

        engine2.AddStaticRule(new SmallOrderRule());
        Assert.NotNull(engine2.Resolve(new TestOrder(50m, "a", 1)));
    }

    [Fact]
    public void Null_rule_is_rejected()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        Assert.Throws<ArgumentNullException>(() => engine.AddStaticRule(null!));
    }
}
