namespace RuleCraft.Tests;

/// <summary>
/// A red-team corpus for the compiled-rule security gate: escapes that <see cref="SecurityAnalyzerTests"/>
/// does not already cover. The gate is a guardrail, not a sandbox (see SECURITY.md), so the invariant
/// is only ever "this never reaches the approval queue" — whether the compile step or the analyzer is
/// what refuses it. Each case that a benign rule must still be allowed is pinned by a positive control
/// at the bottom, so a future tightening cannot quietly ban legitimate arithmetic or string work.
/// </summary>
public class SecurityAnalyzerAdversarialTests
{
    private static string Wrap(string extraMembers) =>
        $$"""
        using RuleCraft;
        using RuleCraft.Tests;

        namespace RuleCraft.Generated.Adversarial;

        public sealed class AdversarialRule : IRule<ITestDiscount, TestOrder>, ITestDiscount
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

    private static RuleValidationException AssertRejected(string extraMembers)
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        return Assert.Throws<RuleValidationException>(() => engine.AddRuleFromSource(Wrap(extraMembers)));
    }

    // -------------------------------------------------------------- reflection via a value, not a name

    [Fact]
    public void Reflection_through_an_instance_GetType_is_rejected()
    {
        // GetType() itself is System.Object.GetType — not banned. The escape is what the returned
        // System.Type gives you; every member on it must be refused, or reflection is one dot away.
        var ex = AssertRejected("public object Reach() => \"x\".GetType().Assembly;");
        Assert.False(ex.Report.Success);
    }

    [Fact]
    public void Reflection_through_typeof_is_rejected()
    {
        var ex = AssertRejected("public object Reach() => typeof(TestOrder).Assembly;");
        Assert.False(ex.Report.Success);
    }

    [Fact]
    public void GetType_module_traversal_is_rejected()
    {
        var ex = AssertRejected("public object Reach() => this.GetType().Module;");
        Assert.False(ex.Report.Success);
    }

    // -------------------------------------------------------------- a banned type worn as a type argument

    [Fact]
    public void A_banned_type_as_a_generic_argument_is_rejected()
    {
        var ex = AssertRejected(
            "public System.Collections.Generic.List<System.Type> Reach() => new();");
        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("System.Type"));
    }

    // -------------------------------------------------------------- banned call hidden inside a lambda

    [Fact]
    public void A_banned_call_inside_a_lambda_body_is_rejected()
    {
        var ex = AssertRejected(
            "public int Reach() { System.Func<int> f = () => { System.IO.File.Delete(\"x\"); return 1; }; return f(); }");
        Assert.False(ex.Report.Success);
    }

    [Fact]
    public void A_banned_call_inside_a_local_function_is_rejected()
    {
        var ex = AssertRejected(
            "public void Reach() { void Inner() => System.GC.Collect(); Inner(); }");
        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("System.GC"));
    }

    // -------------------------------------------------------------- the open root System.* surface

    [Fact]
    public void AppContext_data_access_is_rejected()
    {
        // System.AppContext sits directly under the (unbanned) System root, so nothing but an explicit
        // type ban stops it. GetData can read process-global switches — a data leak the guardrail owes
        // a reviewer, even if it is not remote code execution.
        var ex = AssertRejected("public object? Reach() => System.AppContext.GetData(\"x\");");
        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("System.AppContext"));
    }

    [Fact]
    public void AppContext_base_directory_is_rejected()
    {
        var ex = AssertRejected("public string Reach() => System.AppContext.BaseDirectory;");
        Assert.Contains(ex.Report.SecurityFindings, f => f.Message.Contains("System.AppContext"));
    }

    // -------------------------------------------------------------- positive controls: benign code stays legal

    [Fact]
    public void Arithmetic_string_and_date_work_still_passes_the_gate()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var source = Wrap(
            """
            public decimal Compute(TestOrder o) => System.Math.Round(o.Total * 0.1m, 2);
            public bool Named(TestOrder o) => o.Customer.StartsWith("v", System.StringComparison.OrdinalIgnoreCase);
            public int Day() => System.DateTime.UtcNow.Day;
            public string Id() => System.Guid.NewGuid().ToString("N");
            """);

        var exception = Record.Exception(() => engine.AddRuleFromSource(source));
        Assert.Null(exception);
    }

    [Fact]
    public void Regex_in_a_rule_still_passes_the_gate()
    {
        // Regex is deliberately allowed (business rules need it); ReDoS is documented as out of scope.
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var source = Wrap(
            "public bool Reach(TestOrder o) => System.Text.RegularExpressions.Regex.IsMatch(o.Customer, \"^v\");");

        Assert.Null(Record.Exception(() => engine.AddRuleFromSource(source)));
    }
}
