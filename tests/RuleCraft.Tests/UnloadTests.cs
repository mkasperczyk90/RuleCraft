using System.Runtime.CompilerServices;
using RuleCraft.Compilation;
using RuleCraft.Loading;

namespace RuleCraft.Tests;

public class UnloadTests
{
    [Fact]
    public void Rule_load_context_is_collectible_after_unload()
    {
        var weakContext = LoadRuleAndUnload();

        for (var i = 0; i < 10 && weakContext.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weakContext.IsAlive,
            "The rule AssemblyLoadContext was not collected after Unload() — something is pinning generated types.");
    }

    [Fact]
    public void Generated_type_identity_is_shared_with_the_host()
    {
        var bytes = CompileBigOrderRule();
        var (rule, context) = RuleLoader.Load<ITestDiscount, TestOrder>(bytes, "identity-test");
        try
        {
            // The loaded instance must be castable to the HOST's contract type — this fails
            // with InvalidCastException if the ALC resolved its own copy of the contract assembly.
            Assert.IsAssignableFrom<ITestDiscount>(rule.Implementation);
            Assert.Equal(0.10m, rule.Implementation.GetDiscount(new TestOrder(150m, "a", 1)));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public async Task Test_harness_reports_timeout_as_failure()
    {
        const string spinningTestRule =
            """
            using RuleCraft;
            using RuleCraft.Tests;

            namespace RuleCraft.Generated.Spin;

            public sealed class SpinRule : IRule<ITestDiscount, TestOrder>, ITestDiscount
            {
                public bool AppliesTo(TestOrder context) => true;
                public ITestDiscount Implementation => this;
                public decimal GetDiscount(TestOrder order) => 0m;
            }

            public sealed class SpinningTest : IRuleTest
            {
                public string Name => "spins until cancelled";
                public TestResult Run(TestContext context)
                {
                    while (!context.CancellationToken.IsCancellationRequested)
                    {
                    }
                    return TestResult.Passed();
                }
            }
            """;

        var options = Fixtures.Options();
        options.TestTimeout = TimeSpan.FromMilliseconds(500);
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);

        var ex = Assert.Throws<RuleValidationException>(() => engine.AddRuleFromSource(spinningTestRule));

        Assert.Contains(ex.Report.TestResults,
            t => t.Outcome == TestOutcome.Failed && (t.Message?.Contains("timed out") ?? false));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadRuleAndUnload()
    {
        var bytes = CompileBigOrderRule();
        var (_, context) = RuleLoader.Load<ITestDiscount, TestOrder>(bytes, "unload-test");
        var weak = new WeakReference(context);
        context.Unload();
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static byte[] CompileBigOrderRule()
    {
        var references = ReferenceSetProvider.Build(
        [
            typeof(IRule<,>).Assembly,
            typeof(ITestDiscount).Assembly,
        ]);
        var result = RuleCompiler.Compile("RuleCraft.Generated.UnloadTest", Fixtures.BigOrderRule, references);
        Assert.True(result.Success, string.Join("\n", result.ErrorDiagnostics));
        return result.AssemblyBytes!;
    }
}
