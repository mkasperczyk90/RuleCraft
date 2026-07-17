namespace RuleCraft.Tests;

/// <summary>The way back: a removed or quarantined rule must not be a one-way door.</summary>
public class EnableTests
{
    [Fact]
    public void Removed_rule_can_be_enabled_again_and_dispatches()
    {
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        engine.RemoveRule(info.Id);
        Assert.Null(engine.Resolve(new TestOrder(150m, "a", 1)));

        var enabled = engine.Enable(info.Id, "operator@example.com");

        Assert.Equal(RuleStatus.Approved, enabled.Status);
        Assert.True(enabled.IsLoaded);
        Assert.Equal(0.10m, engine.Resolve(new TestOrder(150m, "a", 1))!.GetDiscount(new TestOrder(150m, "a", 1)));
    }

    [Fact]
    public void Enabling_revalidates_so_a_rule_that_is_still_wrong_stays_out()
    {
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);
        engine.RemoveRule(info.Id);

        // The invariant the rule violates arrives while it is disabled: enabling is an approval and
        // must be judged as one, not a status edit.
        engine.AddAcceptanceTest(new NoDiscountsAllowed());

        Assert.Throws<RuleValidationException>(() => engine.Enable(info.Id, "operator"));
        Assert.Equal(RuleStatus.Quarantined, engine.GetRules().Single(r => r.Id == info.Id).Status);
        Assert.Null(engine.Resolve(new TestOrder(150m, "a", 1)));
    }

    [Fact]
    public void A_quarantined_rule_recovers_once_the_reason_is_gone()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath, autoApprove: true));
        var info = engine1.AddRuleFromSource(Fixtures.BigOrderRule);

        // A stricter invariant ships and quarantines the rule on reload...
        var strict = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        strict.AddAcceptanceTest(new NoDiscountsAllowed());
        strict.ReloadFromStore();
        Assert.Equal(RuleStatus.Quarantined, strict.GetRules().Single(r => r.Id == info.Id).Status);

        // ...and is reverted. Without Enable the source sits in the store, valid and unreachable.
        using var reverted = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        var enabled = reverted.Enable(info.Id, "operator");

        Assert.Equal(RuleStatus.Approved, enabled.Status);
        Assert.Null(enabled.StatusReason);
        Assert.NotNull(reverted.Resolve(new TestOrder(150m, "a", 1)));
    }

    [Fact]
    public void A_rejected_rule_stays_rejected()
    {
        // Rejection is a decision on the record. Undoing it should mean a new candidate through
        // review, not an operator quietly turning it back on.
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);
        engine.Reject(info.Id, "not the logic we want");

        var ex = Assert.Throws<RuleStateException>(() => engine.Enable(info.Id, "operator"));
        Assert.Contains("rejected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(engine.Resolve(new TestOrder(150m, "a", 1)));
    }

    [Fact]
    public void Enabling_a_live_rule_is_rejected()
    {
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        Assert.Throws<RuleStateException>(() => engine.Enable(info.Id, "operator"));
    }

    [Fact]
    public void Enabling_an_unknown_rule_throws_RuleNotFound()
    {
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        Assert.Throws<RuleNotFoundException>(() => engine.Enable("does-not-exist", "operator"));
    }

    private sealed class NoDiscountsAllowed : IRuleAcceptanceTest<ITestDiscount>
    {
        public string Name => "discounts are disabled company-wide";

        public TestResult Run(ITestDiscount implementation)
        {
            var discount = implementation.GetDiscount(new TestOrder(150m, "a", 1));
            return discount == 0m ? TestResult.Passed() : TestResult.Failed($"Discount {discount} != 0.");
        }
    }
}
