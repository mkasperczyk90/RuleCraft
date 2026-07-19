using RuleCraft;

namespace RuleCraft.Tests;

/// <summary>The low-ceremony conveniences: async offload wrappers and single-rule lookup.</summary>
public class EngineConvenienceTests
{
    private sealed class FlatStaticRule(decimal discount) : IRule<ITestDiscount, TestOrder>, ITestDiscount
    {
        public bool AppliesTo(TestOrder context) => true;
        public ITestDiscount Implementation => this;
        public decimal GetDiscount(TestOrder order) => discount;
    }

    [Fact]
    public async Task AddRuleFromSourceAsync_then_ApproveAsync_loads_the_rule()
    {
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());

        var info = await engine.AddRuleFromSourceAsync(Fixtures.BigOrderRule);
        Assert.Equal(RuleStatus.PendingApproval, info.Status);

        var approved = await engine.ApproveAsync(info.Id, "reviewer");
        Assert.Equal(RuleStatus.Approved, approved.Status);

        var resolved = engine.Resolve(new TestOrder(150m, "alice", 1));
        Assert.NotNull(resolved);
    }

    [Fact]
    public async Task An_already_cancelled_token_stops_the_async_add_before_it_runs()
    {
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.AddRuleFromSourceAsync(Fixtures.BigOrderRule, cancellationToken: cts.Token));
    }

    [Fact]
    public void GetRule_returns_a_stored_rule_by_id()
    {
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        var found = engine.GetRule(info.Id);

        Assert.NotNull(found);
        Assert.Equal(info.Id, found!.Id);
        Assert.Equal(RuleStatus.PendingApproval, found.Status);
    }

    [Fact]
    public void GetRule_returns_a_static_rule_by_id()
    {
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var info = engine.AddStaticRule(new FlatStaticRule(0.05m), "flat");

        var found = engine.GetRule(info.Id);

        Assert.NotNull(found);
        Assert.Equal(RuleOrigin.Static, found!.Origin);
        Assert.True(found.IsLoaded);
    }

    [Fact]
    public void GetRule_returns_null_for_an_unknown_id()
    {
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        Assert.Null(engine.GetRule("does-not-exist"));
    }
}
