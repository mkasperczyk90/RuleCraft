namespace RuleCraft.Tests;

public class ApprovalWorkflowTests
{
    [Fact]
    public async Task New_rule_is_pending_not_loaded_until_approved()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule, name: "big-order", spec: "10% off 100+");

        Assert.Equal(RuleStatus.PendingApproval, info.Status);
        Assert.False(info.IsLoaded);
        Assert.Null(engine.Resolve(new TestOrder(150m, "alice", 1)));

        var pending = engine.GetPendingRules();
        var candidate = Assert.Single(pending);
        Assert.Equal(info.Id, candidate.Id);
        Assert.Equal("10% off 100+", candidate.Spec);
        Assert.Contains("BigOrderRule", candidate.Source);
        Assert.True(candidate.Report.Success);

        var approved = engine.Approve(info.Id, approvedBy: "reviewer@example.com");
        Assert.Equal(RuleStatus.Approved, approved.Status);
        Assert.True(approved.IsLoaded);
        Assert.Equal("reviewer@example.com", approved.ApprovedBy);
        Assert.NotNull(engine.Resolve(new TestOrder(150m, "alice", 1)));
        Assert.Empty(engine.GetPendingRules());
    }

    [Fact]
    public async Task Rejected_rule_never_loads_and_cannot_be_approved_later()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        var rejected = engine.Reject(info.Id, reason: "not the business logic we want");
        Assert.Equal(RuleStatus.Rejected, rejected.Status);
        Assert.Null(engine.Resolve(new TestOrder(150m, "alice", 1)));

        Assert.Throws<RuleStateException>(() => engine.Approve(info.Id, "reviewer"));
    }

    [Fact]
    public async Task Approving_unknown_rule_throws_RuleNotFound()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        Assert.Throws<RuleNotFoundException>(() => engine.Approve("does-not-exist", "reviewer"));
    }

    [Fact]
    public async Task Tampered_source_on_disk_is_refused_at_approval()
    {
        var storePath = Fixtures.NewStorePath();
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        var sourceFile = Path.Combine(storePath, info.Id + ".cs");
        await File.AppendAllTextAsync(sourceFile, "\n// tampered after validation\n");

        Assert.Throws<RuleStateException>(() => engine.Approve(info.Id, "reviewer"));
    }

    [Fact]
    public async Task Failing_acceptance_test_added_after_queueing_quarantines_at_approval()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        engine.AddAcceptanceTest(new RejectEverythingAcceptanceTest());

        Assert.Throws<RuleValidationException>(() => engine.Approve(info.Id, "reviewer"));

        var rule = engine.GetRules().Single(r => r.Id == info.Id);
        Assert.Equal(RuleStatus.Quarantined, rule.Status);
        Assert.False(rule.IsLoaded);
    }

    [Fact]
    public async Task Acceptance_test_gate_rejects_contract_violations_at_add_time()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        engine.AddAcceptanceTest(new DiscountWithinRangeAcceptanceTest());

        const string brokenContract =
            """
            using RuleCraft;
            using RuleCraft.Tests;

            namespace RuleCraft.Generated.Broken;

            public sealed class Rule : IRule<ITestDiscount, TestOrder>, ITestDiscount
            {
                public bool AppliesTo(TestOrder context) => true;
                public ITestDiscount Implementation => this;
                public decimal GetDiscount(TestOrder order) => 2.50m;
            }

            public sealed class SelfConsistentTest : IRuleTest
            {
                public string Name => "self-consistent but violates the invariant";
                public TestResult Run(TestContext context)
                {
                    RuleAssert.Equal(2.50m, new Rule().GetDiscount(new TestOrder(1m, "x", 1)));
                    return TestResult.Passed();
                }
            }
            """;

        var ex = Assert.Throws<RuleValidationException>(() =>
            engine.AddRuleFromSource(brokenContract));

        Assert.Contains(ex.Report.TestResults,
            t => t.Outcome == TestOutcome.Failed && t.Name.StartsWith("acceptance:"));
    }

    private sealed class RejectEverythingAcceptanceTest : IRuleAcceptanceTest<ITestDiscount>
    {
        public string Name => "reject everything";

        public TestResult Run(ITestDiscount implementation) => TestResult.Failed("nothing passes");
    }

    private sealed class DiscountWithinRangeAcceptanceTest : IRuleAcceptanceTest<ITestDiscount>
    {
        public string Name => "discount within [0,1]";

        public TestResult Run(ITestDiscount implementation)
        {
            var samples = new[]
            {
                new TestOrder(0m, "a", 0),
                new TestOrder(100m, "b", 1),
                new TestOrder(10_000m, "c", 50),
            };

            foreach (var order in samples)
            {
                var discount = implementation.GetDiscount(order);
                if (discount is < 0m or > 1m)
                    return TestResult.Failed($"Discount {discount} for total {order.Total} is outside [0,1].");
            }

            return TestResult.Passed();
        }
    }
}
