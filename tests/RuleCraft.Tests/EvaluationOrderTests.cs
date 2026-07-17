namespace RuleCraft.Tests;

public class EvaluationOrderTests
{
    private sealed class MatchAllRule(int priority) : IRule<ITestDiscount, TestOrder>, ITestDiscount
    {
        public bool AppliesTo(TestOrder context) => true;
        public ITestDiscount Implementation => this;
        public int Priority { get; } = priority;
        public decimal GetDiscount(TestOrder order) => Priority / 100m;
    }

    [Fact]
    public void GetRules_reports_evaluation_order_highest_priority_first()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        engine.AddStaticRule(new MatchAllRule(1), name: "low");
        engine.AddStaticRule(new MatchAllRule(50), name: "high");
        engine.AddStaticRule(new MatchAllRule(10), name: "mid");

        var rules = engine.GetRules();

        Assert.Equal(["high", "mid", "low"], rules.Select(r => r.Name));
        Assert.Equal([0, 1, 2], rules.Select(r => r.EvaluationOrder));
    }

    [Fact]
    public void Newest_rule_wins_ties_under_HighestPriority()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        engine.AddStaticRule(new MatchAllRule(5), name: "older");
        engine.AddStaticRule(new MatchAllRule(5), name: "newer");

        var rules = engine.GetRules();
        Assert.Equal(["newer", "older"], rules.Select(r => r.Name));

        // Resolve must agree with the order GetRules reports.
        Assert.Same(engine.ResolveAll(new TestOrder(1m, "a", 1))[0], engine.Resolve(new TestOrder(1m, "a", 1)));
    }

    [Fact]
    public void FirstMatch_policy_evaluates_in_registration_order_not_priority_order()
    {
        var options = Fixtures.Options();
        options.ResolutionPolicy = ResolutionPolicy.FirstMatch;
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);

        engine.AddStaticRule(new MatchAllRule(1), name: "registered-first");
        engine.AddStaticRule(new MatchAllRule(99), name: "registered-second");

        var rules = engine.GetRules();
        Assert.Equal(["registered-first", "registered-second"], rules.Select(r => r.Name));

        // The reported order is the order actually used: the older rule wins despite lower priority.
        var order = new TestOrder(1m, "a", 1);
        Assert.Equal(0.01m, engine.Resolve(order)!.GetDiscount(order));
        Assert.Equal(0.01m, engine.ResolveAll(order)[0].GetDiscount(order));
    }

    [Fact]
    public async Task Not_loaded_rules_have_no_evaluation_order_and_come_last()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());

        // Pending: validated but not approved, so not loaded.
        var pending = engine.AddRuleFromSource(Fixtures.BigOrderRule, name: "pending-rule");
        engine.AddStaticRule(new MatchAllRule(1), name: "live-rule");

        var rules = engine.GetRules();

        Assert.Equal(["live-rule", "pending-rule"], rules.Select(r => r.Name));
        Assert.Equal(0, rules[0].EvaluationOrder);
        Assert.Null(rules[1].EvaluationOrder);
        Assert.False(rules[1].IsLoaded);
        Assert.Equal(RuleStatus.PendingApproval, rules[1].Status);

        // Approving it gives it a place in the order.
        engine.Approve(pending.Id, "reviewer");
        Assert.All(engine.GetRules(), rule => Assert.NotNull(rule.EvaluationOrder));
    }

    [Fact]
    public async Task GetRules_mixes_static_and_stored_rules_in_one_ordered_list()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));

        engine.AddStaticRule(new MatchAllRule(3), name: "static-mid");
        engine.AddRuleFromSource(Fixtures.VipRule, name: "generated-top");   // Priority 5
        engine.AddStaticRule(new MatchAllRule(1), name: "static-low");

        var rules = engine.GetRules();

        Assert.Equal(["generated-top", "static-mid", "static-low"], rules.Select(r => r.Name));
        Assert.Equal(
            [RuleOrigin.Compiled, RuleOrigin.Static, RuleOrigin.Static],
            rules.Select(r => r.Origin));
        Assert.Equal([5, 3, 1], rules.Select(r => r.Priority));
    }
}
