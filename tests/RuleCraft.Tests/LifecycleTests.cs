using System.Runtime.CompilerServices;

namespace RuleCraft.Tests;

/// <summary>Disposal and the concurrency of the approval workflow.</summary>
public class LifecycleTests
{
    [Fact]
    public void Dispose_unloads_the_rule_assemblies_the_engine_owns()
    {
        var weak = LoadRuleInDisposedEngine();

        for (var i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive,
            "Disposing the engine must unload its rule load contexts — otherwise an engine per test leaks them.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadRuleInDisposedEngine()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        // The loader names each rule's context after its id, so this finds exactly ours — the test
        // classes run in parallel and other engines have contexts of their own live at this moment.
        var context = System.Runtime.Loader.AssemblyLoadContext.All.Single(c => c.Name == $"RuleCraft:{info.Id}");
        var weak = new WeakReference(context);

        engine.Dispose();
        return weak;
    }

    [Fact]
    public void Disposed_engine_refuses_further_use()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(autoApprove: true));
        engine.AddRuleFromSource(Fixtures.BigOrderRule);
        engine.Dispose();

        // Silently resolving nothing would look like "no rule matched" — a wrong business answer.
        Assert.Throws<ObjectDisposedException>(() => engine.Resolve(new TestOrder(150m, "a", 1)));
        Assert.Throws<ObjectDisposedException>(() => engine.AddRuleFromSource(Fixtures.VipRule));
        Assert.Throws<ObjectDisposedException>(() => engine.Approve("whatever", "reviewer"));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        engine.Dispose();
        engine.Dispose();
    }

    [Fact]
    public void Concurrent_approve_and_reject_of_one_rule_cannot_disagree_about_the_outcome()
    {
        // Unserialized, both calls pass the PendingApproval check, and the rule can end up loaded
        // and serving traffic while the store records it as rejected.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
            var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

            var results = new RuleInfo?[2];
            var barrier = new Barrier(2);

            Parallel.For(0, 2, i =>
            {
                barrier.SignalAndWait();
                try
                {
                    results[i] = i == 0
                        ? engine.Approve(info.Id, "reviewer")
                        : engine.Reject(info.Id, "no thanks");
                }
                catch (RuleStateException)
                {
                    // Exactly one of the two must lose this race.
                }
            });

            var winners = results.Where(r => r is not null).ToList();
            Assert.Single(winners);

            var stored = engine.GetRules().Single(r => r.Id == info.Id);
            Assert.Equal(winners[0]!.Status, stored.Status);
            Assert.Equal(stored.Status == RuleStatus.Approved, stored.IsLoaded);
        }
    }

    [Fact]
    public void Concurrent_approvals_of_one_rule_load_it_exactly_once()
    {
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        var succeeded = 0;
        Parallel.For(0, 4, _ =>
        {
            try
            {
                engine.Approve(info.Id, "reviewer");
                Interlocked.Increment(ref succeeded);
            }
            catch (RuleStateException)
            {
            }
        });

        Assert.Equal(1, succeeded);
        Assert.Single(engine.GetRules(), r => r.Id == info.Id && r.IsLoaded);
    }
}
