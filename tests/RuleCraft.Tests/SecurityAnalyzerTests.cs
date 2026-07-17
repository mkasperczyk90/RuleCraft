namespace RuleCraft.Tests;

public class SecurityAnalyzerTests
{
    private static string Wrap(string extraMembers) =>
        $$"""
        using RuleCraft;
        using RuleCraft.Tests;

        namespace RuleCraft.Generated.Hostile;

        public sealed class HostileRule : IRule<ITestDiscount, TestOrder>, ITestDiscount
        {
            public bool AppliesTo(TestOrder context) => true;
            public ITestDiscount Implementation => this;
            public decimal GetDiscount(TestOrder order) => 0m;

            {{extraMembers}}
        }

        public sealed class SmokeTest : IRuleTest
        {
            public string Name => "smoke";
            public TestResult Run(TestContext context) => TestResult.Passed();
        }
        """;

    private static RuleValidationException AssertRejected(string source)
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        return Assert.Throws<RuleValidationException>(() => engine.AddRuleFromSource(source));
    }

    [Fact]
    public void Reflection_usage_is_rejected_with_a_named_finding()
    {
        var ex = AssertRejected(Wrap(
            "public object Sneaky() => System.Reflection.Assembly.GetExecutingAssembly();"));

        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("System.Reflection"));
    }

    [Fact]
    public void Activator_is_rejected()
    {
        var ex = AssertRejected(Wrap(
            "public object Sneaky() => System.Activator.CreateInstance(typeof(TestOrder), 1m, \"x\", 1)!;"));

        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("System.Activator"));
    }

    [Fact]
    public void Environment_and_GC_are_rejected()
    {
        var ex = AssertRejected(Wrap(
            "public string Sneaky() { System.GC.Collect(); return System.Environment.MachineName; }"));

        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("System.GC"));
        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("System.Environment"));
    }

    [Fact]
    public void Type_GetType_lookup_is_rejected()
    {
        var ex = AssertRejected(Wrap(
            "public object? Sneaky() => System.Type.GetType(\"System.IO.File\");"));

        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("System.Type"));
    }

    [Fact]
    public void Namespace_alias_does_not_evade_the_ban()
    {
        var source = "using T = System.Threading.Thread;\n" + Wrap(
            "public void Sneaky() => T.Sleep(1);");

        var ex = AssertRejected(source);
        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("System.Threading"));
    }

    [Fact]
    public void Redeclaring_the_contract_type_is_rejected()
    {
        const string redeclaration =
            """
            using RuleCraft;
            using RuleCraft.Tests;

            namespace RuleCraft.Generated.Evil;

            public interface ITestDiscount
            {
                decimal GetDiscount(TestOrder order);
            }

            public sealed class Rule : IRule<RuleCraft.Tests.ITestDiscount, TestOrder>, RuleCraft.Tests.ITestDiscount
            {
                public bool AppliesTo(TestOrder context) => true;
                public RuleCraft.Tests.ITestDiscount Implementation => this;
                public decimal GetDiscount(TestOrder order) => 0m;
            }

            public sealed class SmokeTest : IRuleTest
            {
                public string Name => "smoke";
                public TestResult Run(TestContext context) => TestResult.Passed();
            }
            """;

        var ex = AssertRejected(redeclaration);
        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("redeclares"));
    }

    [Fact]
    public void Preprocessor_directives_are_rejected()
    {
        var source = "#define SNEAKY\n" + Wrap("");
        var ex = AssertRejected(source);
        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("Preprocessor"));
    }

    [Fact]
    public void Hostile_constructs_fail_validation_one_way_or_another()
    {
        // These may die in compilation (missing references) or in analysis — either gate is fine,
        // the invariant is that they never reach the approval queue.
        string[] snippets =
        [
            "public void Sneaky() { System.IO.File.Delete(\"x\"); }",
            "public void Sneaky() { System.Diagnostics.Process.Start(\"cmd\"); }",
            "[System.Runtime.InteropServices.DllImport(\"kernel32\")] public static extern void Beep();",
            "public unsafe void Sneaky(int* p) { }",
            "public void Sneaky() { dynamic d = 1; d.Boom(); }",
        ];

        foreach (var snippet in snippets)
        {
            var ex = AssertRejected(Wrap(snippet));
            Assert.False(ex.Report.Success);
        }
    }

    [Fact]
    public void Starting_work_of_its_own_is_rejected()
    {
        // System.Threading.Tasks cannot be allow-listed wholesale: a rule that spawns work escapes
        // the harness's timeout and drives a coach and horses through the ban on threading.
        var ex = AssertRejected(Wrap(
            "public void Sneaky() { System.Threading.Tasks.Task.Run(() => { while (true) { } }); }"));

        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("Task.Run"));
    }

    [Theory]
    [InlineData("public void Sneaky() { System.Threading.Tasks.Task.Factory.StartNew(() => 1); }", "Task.Factory")]
    [InlineData("public void Sneaky() { System.Threading.Tasks.Task.Delay(10000).Wait(); }", "Task.Delay")]
    [InlineData("public void Sneaky() { System.Threading.Tasks.Task.Run(() => 1).ContinueWith(t => 2); }", "Task.Run")]
    public void Ways_of_spawning_or_blocking_are_all_rejected(string snippet, string expected)
    {
        var ex = AssertRejected(Wrap(snippet));
        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains(expected));
    }

    [Fact]
    public void Parallel_is_rejected_even_though_its_namespace_is_allowed()
    {
        // Two gates stand in the way and the test should not care which fires: Parallel lives in an
        // assembly the reference set never offers (a compile error), and the policy bans the type
        // outright in case a host widens that set via AdditionalReferenceAssemblies.
        var ex = AssertRejected(Wrap("public void Sneaky() { System.Threading.Tasks.Parallel.For(0, 10, i => { }); }"));
        Assert.False(ex.Report.Success);
    }

    [Fact]
    public void Task_itself_stays_usable_so_async_contracts_still_compile()
    {
        // The ban is on starting work, not on the type: a contract may be async, and then the rule
        // has to name Task to implement it.
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var source = Wrap(
            "public System.Threading.Tasks.Task<decimal> Later() => System.Threading.Tasks.Task.FromResult(0m);");

        var exception = Record.Exception(() => engine.AddRuleFromSource(source));

        Assert.True(exception is null, Describe(exception));
    }

    /// <summary>Says which gate rejected a rule — "failed validation" alone sends you back to the debugger.</summary>
    private static string? Describe(Exception? exception) => exception switch
    {
        null => null,
        RuleValidationException ex =>
            $"diagnostics: [{string.Join(" | ", ex.Report.Diagnostics)}] " +
            $"security: [{string.Join(" | ", ex.Report.SecurityFindings.Select(f => $"line {f.Line}: {f.Message}"))}] " +
            $"failed tests: [{string.Join(" | ", ex.Report.TestResults.Where(t => t.Outcome == TestOutcome.Failed).Select(t => $"{t.Name}: {t.Message}"))}]",
        _ => exception.ToString(),
    };

    [Fact]
    public void Benign_rule_passes_the_gate()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);
        Assert.Equal(RuleStatus.PendingApproval, info.Status);
    }
}
