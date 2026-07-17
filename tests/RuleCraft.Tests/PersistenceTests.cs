namespace RuleCraft.Tests;

public class PersistenceTests
{
    [Fact]
    public async Task Approved_rules_survive_a_restart_via_ReloadFromStore()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath, autoApprove: true));
        engine1.AddRuleFromSource(Fixtures.BigOrderRule, name: "big-order");

        // "Restart": a fresh engine over the same folder.
        var engine2 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        Assert.Null(engine2.Resolve(new TestOrder(150m, "alice", 1)));

        engine2.ReloadFromStore();

        var implementation = engine2.Resolve(new TestOrder(150m, "alice", 1));
        Assert.NotNull(implementation);
        Assert.Equal(0.10m, implementation!.GetDiscount(new TestOrder(150m, "alice", 1)));
    }

    [Fact]
    public async Task Pending_rules_survive_a_restart_and_can_be_approved_afterwards()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        var info = engine1.AddRuleFromSource(Fixtures.BigOrderRule, spec: "10% off 100+");

        var engine2 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        engine2.ReloadFromStore();

        var pending = Assert.Single(engine2.GetPendingRules());
        Assert.Equal(info.Id, pending.Id);

        engine2.Approve(info.Id, "reviewer");
        Assert.NotNull(engine2.Resolve(new TestOrder(150m, "alice", 1)));
    }

    [Fact]
    public async Task Tampered_source_is_quarantined_on_reload_instead_of_loading()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath, autoApprove: true));
        var info = engine1.AddRuleFromSource(Fixtures.BigOrderRule);

        await File.AppendAllTextAsync(Path.Combine(storePath, info.Id + ".cs"), "\n// tampered\n");

        var engine2 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        engine2.ReloadFromStore();

        Assert.Null(engine2.Resolve(new TestOrder(150m, "alice", 1)));
        var rule = engine2.GetRules().Single(r => r.Id == info.Id);
        Assert.Equal(RuleStatus.Quarantined, rule.Status);
    }

    [Fact]
    public async Task Rule_failing_new_acceptance_test_is_quarantined_on_reload_not_a_crash()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath, autoApprove: true));
        var info = engine1.AddRuleFromSource(Fixtures.BigOrderRule);

        // "New app version" ships a stricter invariant the old rule violates.
        var engine2 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        engine2.AddAcceptanceTest(new NoDiscountAllowedAcceptanceTest());
        engine2.ReloadFromStore();

        Assert.Null(engine2.Resolve(new TestOrder(150m, "alice", 1)));
        var rule = engine2.GetRules().Single(r => r.Id == info.Id);
        Assert.Equal(RuleStatus.Quarantined, rule.Status);
    }

    private sealed class NoDiscountAllowedAcceptanceTest : IRuleAcceptanceTest<ITestDiscount>
    {
        public string Name => "discounts are disabled company-wide";

        public TestResult Run(ITestDiscount implementation)
        {
            var discount = implementation.GetDiscount(new TestOrder(150m, "a", 1));
            return discount == 0m ? TestResult.Passed() : TestResult.Failed($"Discount {discount} != 0.");
        }
    }
}
