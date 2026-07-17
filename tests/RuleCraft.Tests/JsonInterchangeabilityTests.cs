namespace RuleCraft.Tests;

/// <summary>
/// The claim under test: a JSON rule is a rule like any other — same registry, same priority,
/// same evaluation order, same approval queue.
/// </summary>
public class JsonInterchangeabilityTests
{
    private sealed class StaticMatchAll(int priority, decimal discount) : IRule<ITestDiscount, TestOrder>, ITestDiscount
    {
        public bool AppliesTo(TestOrder context) => true;
        public ITestDiscount Implementation => this;
        public int Priority { get; } = priority;
        public decimal GetDiscount(TestOrder order) => discount;
    }

    private const string JsonMatchAllPriority5 =
        """
        {
          "name": "json-top",
          "priority": 5,
          "when": { "always": true },
          "then": { "discount": 0.50 },
          "tests": [ { "name": "always", "context": { "Total": 1, "Customer": "a", "ItemCount": 1 }, "applies": true } ]
        }
        """;

    [Fact]
    public async Task Json_static_and_compiled_rules_compete_by_priority_in_one_order()
    {
        var engine = Fixtures.JsonEngine(autoApprove: true);

        engine.AddJsonRuleFromSource(JsonMatchAllPriority5);      // priority 5
        engine.AddStaticRule(new StaticMatchAll(3, 0.03m), name: "static-mid");
        engine.AddRuleFromSource(Fixtures.BigOrderRule, name: "compiled-low");   // priority 1

        var rules = engine.GetRules();

        Assert.Equal(["json-top", "static-mid", "compiled-low"], rules.Select(r => r.Name));
        Assert.Equal([0, 1, 2], rules.Select(r => r.EvaluationOrder));
        Assert.Equal([RuleOrigin.Json, RuleOrigin.Static, RuleOrigin.Compiled], rules.Select(r => r.Origin));
        Assert.Equal([5, 3, 1], rules.Select(r => r.Priority));

        // Dispatch agrees with the reported order.
        var order = new TestOrder(150m, "alice", 1);
        Assert.Equal(0.50m, engine.Resolve(order)!.GetDiscount(order));

        var all = engine.ResolveAll(order);
        Assert.Equal([0.50m, 0.03m, 0.10m], all.Select(i => i.GetDiscount(order)));
    }

    [Fact]
    public async Task FirstMatch_policy_orders_all_three_kinds_by_registration()
    {
        var options = Fixtures.Options(autoApprove: true);
        options.ResolutionPolicy = ResolutionPolicy.FirstMatch;
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);
        engine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));

        engine.AddStaticRule(new StaticMatchAll(3, 0.03m), name: "first-registered");
        engine.AddJsonRuleFromSource(JsonMatchAllPriority5);   // higher priority, registered later

        var rules = engine.GetRules();
        Assert.Equal(["first-registered", "json-top"], rules.Select(r => r.Name));

        // Registration order wins over priority under FirstMatch, and Resolve agrees.
        var order = new TestOrder(150m, "alice", 1);
        Assert.Equal(0.03m, engine.Resolve(order)!.GetDiscount(order));
    }

    [Fact]
    public async Task Pending_json_rule_is_queued_with_its_document_and_origin()
    {
        var engine = Fixtures.JsonEngine();
        var info = engine.AddJsonRuleFromSource(
            Fixtures.BigOrderJsonRule, name: "big-order-json", spec: "10% off orders of 100+");

        Assert.Equal(RuleOrigin.Json, info.Origin);
        Assert.Equal(RuleStatus.PendingApproval, info.Status);
        Assert.False(info.IsLoaded);
        Assert.Null(info.EvaluationOrder);
        Assert.Equal(2, info.Priority);
        Assert.Null(engine.Resolve(new TestOrder(150m, "alice", 1)));

        var pending = Assert.Single(engine.GetPendingRules());
        Assert.Equal(RuleOrigin.Json, pending.Origin);
        Assert.Equal(Fixtures.BigOrderJsonRule, pending.Source);
        Assert.Equal("10% off orders of 100+", pending.Spec);
        Assert.True(pending.Report.Success);

        engine.Approve(info.Id, "reviewer@example.com");
        Assert.Equal(0.10m, engine.Resolve(new TestOrder(150m, "alice", 1))!.GetDiscount(new TestOrder(150m, "alice", 1)));
    }

    [Fact]
    public async Task Json_rule_predicate_is_respected_and_falls_back()
    {
        var engine = Fixtures.JsonEngine(autoApprove: true);
        engine.SetFallback(new ZeroDiscountFallback());
        engine.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);

        Assert.Equal(0.10m, engine.Resolve(new TestOrder(150m, "a", 1))!.GetDiscount(new TestOrder(150m, "a", 1)));
        Assert.Equal(0m, engine.Resolve(new TestOrder(50m, "a", 1))!.GetDiscount(new TestOrder(50m, "a", 1)));
    }

    [Fact]
    public async Task Removing_a_json_rule_stops_resolution()
    {
        var engine = Fixtures.JsonEngine(autoApprove: true);
        var info = engine.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);

        Assert.NotNull(engine.Resolve(new TestOrder(150m, "a", 1)));

        engine.RemoveRule(info.Id);

        Assert.Null(engine.Resolve(new TestOrder(150m, "a", 1)));
        Assert.Equal(RuleStatus.Disabled, engine.GetRules().Single(r => r.Id == info.Id).Status);
    }
}
