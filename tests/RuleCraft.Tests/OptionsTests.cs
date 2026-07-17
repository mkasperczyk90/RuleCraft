namespace RuleCraft.Tests;

/// <summary>The configuration contract: validated once, then frozen.</summary>
public class OptionsTests
{
    [Fact]
    public void Options_are_copied_so_a_later_edit_cannot_move_the_bar_under_a_running_engine()
    {
        var options = Fixtures.Options();
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(options);

        // Flipping this on a live engine would let the next rule skip human approval entirely.
        options.AutoApprove = true;

        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);
        Assert.Equal(RuleStatus.PendingApproval, info.Status);
        Assert.False(info.IsLoaded);
    }

    [Fact]
    public void Security_policy_is_copied_too()
    {
        var options = Fixtures.Options();
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(options);

        // Widening the policy after the fact must not reach rules this engine is already judging.
        options.SecurityPolicy.AllowedNamespaces.Add("System.IO");

        var ex = Assert.Throws<RuleValidationException>(() => engine.AddRuleFromSource(
            """
            using RuleCraft;
            using RuleCraft.Tests;

            namespace RuleCraft.Generated.Late;

            public sealed class Rule : IRule<ITestDiscount, TestOrder>, ITestDiscount
            {
                public bool AppliesTo(TestOrder context) => true;
                public ITestDiscount Implementation => this;
                public decimal GetDiscount(TestOrder order) => 0m;
                public void Sneaky() => System.IO.File.Delete("x");
            }

            public sealed class SmokeTest : IRuleTest
            {
                public string Name => "smoke";
                public TestResult Run(TestContext context) => TestResult.Passed();
            }
            """));

        Assert.False(ex.Report.Success);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_store_path_is_rejected_at_construction(string storePath)
    {
        var options = Fixtures.Options();
        options.StorePath = storePath;

        var ex = Assert.Throws<ArgumentException>(() => new RuleEngine<ITestDiscount, TestOrder>(options));
        Assert.Contains("StorePath", ex.Message);
    }

    [Fact]
    public void Non_positive_test_timeout_is_rejected_at_construction()
    {
        var options = Fixtures.Options();
        options.TestTimeout = TimeSpan.Zero;

        Assert.Throws<ArgumentOutOfRangeException>(() => new RuleEngine<ITestDiscount, TestOrder>(options));
    }

    [Fact]
    public void Zero_generation_attempts_is_rejected_at_construction_not_silently_clamped()
    {
        var options = Fixtures.Options();
        options.MaxGenerationAttempts = 0;

        Assert.Throws<ArgumentOutOfRangeException>(() => new RuleEngine<ITestDiscount, TestOrder>(options));
    }

    [Fact]
    public void Constructing_an_engine_does_not_touch_the_disk()
    {
        // A store nobody has written to has no business creating a folder — and `new RuleEngine()`
        // in a unit test should not leave one behind.
        var storePath = Fixtures.NewStorePath();
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));

        Assert.False(Directory.Exists(storePath));
        Assert.Empty(engine.GetRules());
        Assert.Empty(engine.GetPendingRules());

        engine.AddRuleFromSource(Fixtures.BigOrderRule);
        Assert.True(Directory.Exists(storePath));
    }
}
