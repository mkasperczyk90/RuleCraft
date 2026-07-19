using RuleCraft;

namespace RuleCraft.Tests;

/// <summary>
/// A store nobody backs with a disk: proves the <see cref="IRuleStore"/> seam is real, and that two
/// engines sharing one store see each other's approved rules — the multi-instance story a single
/// process's file store cannot tell.
/// </summary>
public sealed class InMemoryRuleStore : IRuleStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, StoredRule> _rules = new(StringComparer.Ordinal);

    public void Save(StoredRule rule)
    {
        lock (_lock) { _rules[rule.Record.Id] = rule; }
    }

    public void Update(RuleRecord record)
    {
        // Update only ever follows a Save, so the source is already there — keep it, swap the record.
        lock (_lock) { _rules[record.Id] = _rules[record.Id] with { Record = record }; }
    }

    public RuleRecord? Find(string id)
    {
        lock (_lock) { return _rules.TryGetValue(id, out var stored) ? stored.Record : null; }
    }

    public string ReadSource(RuleRecord record)
    {
        lock (_lock) { return _rules[record.Id].Source; }
    }

    public IReadOnlyList<RuleRecord> LoadAll()
    {
        lock (_lock) { return _rules.Values.Select(s => s.Record).OrderBy(r => r.CreatedUtc).ToList(); }
    }
}

public class CustomRuleStoreTests
{
    private static RuleEngineOptions Options(IRuleStore store) => new()
    {
        Store = store,
        TestTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public void A_custom_store_carries_a_rule_through_add_approve_and_resolve()
    {
        var store = new InMemoryRuleStore();
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Options(store));

        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);
        Assert.Equal(RuleStatus.PendingApproval, info.Status);

        engine.Approve(info.Id, "reviewer");

        var resolved = engine.Resolve(new TestOrder(150m, "alice", 1));
        Assert.NotNull(resolved);
        Assert.Equal(0.10m, resolved!.GetDiscount(new TestOrder(150m, "alice", 1)));
    }

    [Fact]
    public void Two_engines_sharing_one_store_see_each_others_approved_rules()
    {
        var store = new InMemoryRuleStore();

        using var writer = new RuleEngine<ITestDiscount, TestOrder>(Options(store));
        var info = writer.AddRuleFromSource(Fixtures.BigOrderRule);
        writer.Approve(info.Id, "reviewer");

        // A second instance over the same store starts blank, then reloads — the cross-instance path
        // a shared backend exists to serve.
        using var reader = new RuleEngine<ITestDiscount, TestOrder>(Options(store));
        Assert.Null(reader.Resolve(new TestOrder(150m, "alice", 1)));

        reader.ReloadFromStore();

        var resolved = reader.Resolve(new TestOrder(150m, "alice", 1));
        Assert.NotNull(resolved);
        Assert.Equal(0.10m, resolved!.GetDiscount(new TestOrder(150m, "alice", 1)));
    }

    [Fact]
    public void A_custom_store_still_gets_tamper_protection_from_the_engine()
    {
        var store = new InMemoryRuleStore();
        using var engine = new RuleEngine<ITestDiscount, TestOrder>(Options(store));
        var info = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        // Reach into the backing store and corrupt the source behind the engine's back, leaving the
        // record (and its recorded hash) intact — exactly what the tamper check must catch.
        var record = store.Find(info.Id)!;
        store.Save(new StoredRule(record, "// tampered\n" + store.ReadSource(record)));

        Assert.Throws<RuleStateException>(() => engine.Approve(info.Id, "reviewer"));
    }
}
