namespace RuleCraft.Tests;

public class JsonPersistenceTests
{
    [Fact]
    public async Task Json_rule_survives_a_restart()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = Fixtures.JsonEngine(storePath, autoApprove: true);
        engine1.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);

        var engine2 = Fixtures.JsonEngine(storePath);
        engine2.ReloadFromStore();

        var implementation = engine2.Resolve(new TestOrder(150m, "alice", 1));
        Assert.NotNull(implementation);
        Assert.Equal(0.10m, implementation!.GetDiscount(new TestOrder(150m, "alice", 1)));
    }

    [Fact]
    public async Task Reload_without_EnableJsonRules_skips_the_rule_and_leaves_it_approved()
    {
        // The host is misconfigured, the rule is fine. Quarantine is a permanent disk write that
        // fixing the host would not undo, so it must NOT happen here.
        var storePath = Fixtures.NewStorePath();

        var engine1 = Fixtures.JsonEngine(storePath, autoApprove: true);
        var info = engine1.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);

        var misconfigured = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        misconfigured.ReloadFromStore();

        Assert.Null(misconfigured.Resolve(new TestOrder(150m, "alice", 1)));
        Assert.Equal(RuleStatus.Approved, misconfigured.GetRules().Single(r => r.Id == info.Id).Status);

        // Fixing the host recovers the rule with no manual intervention.
        var fixedEngine = Fixtures.JsonEngine(storePath);
        fixedEngine.ReloadFromStore();
        Assert.NotNull(fixedEngine.Resolve(new TestOrder(150m, "alice", 1)));
    }

    [Fact]
    public async Task Approving_a_json_rule_without_EnableJsonRules_throws_and_writes_nothing()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = Fixtures.JsonEngine(storePath);
        var info = engine1.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);

        var misconfigured = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options(storePath));
        Assert.Throws<RuleStateException>(() => misconfigured.Approve(info.Id, "reviewer"));

        Assert.Equal(RuleStatus.PendingApproval, misconfigured.GetRules().Single(r => r.Id == info.Id).Status);
    }

    [Fact]
    public async Task Pending_json_rule_can_be_approved_after_a_restart()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = Fixtures.JsonEngine(storePath);
        var info = engine1.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule, spec: "10% off 100+");

        var engine2 = Fixtures.JsonEngine(storePath);
        engine2.ReloadFromStore();

        var pending = Assert.Single(engine2.GetPendingRules());
        Assert.Equal(info.Id, pending.Id);
        Assert.Equal(RuleOrigin.Json, pending.Origin);

        engine2.Approve(info.Id, "reviewer");
        Assert.NotNull(engine2.Resolve(new TestOrder(150m, "alice", 1)));
    }

    private const string PlCustomersRule =
        """
        {
          "name": "pl-customers",
          "when": { "field": "Customer.Country", "op": "eq", "value": "PL" },
          "then": { "discount": 0.10 },
          "tests": [
            { "context": { "Total": 1, "Customer": { "Name": "a", "Country": "PL", "Tier": "Gold" }, "Tags": [], "PlacedAt": "2026-01-01T00:00:00+00:00" }, "applies": true },
            { "context": { "Total": 1, "Customer": { "Name": "a", "Country": "DE", "Tier": "Gold" }, "Tags": [], "PlacedAt": "2026-01-01T00:00:00+00:00" }, "applies": false }
          ]
        }
        """;

    [Fact]
    public void Contract_change_quarantines_a_json_rule_instead_of_crashing()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = new RuleEngine<ITestDiscount, RichOrder>(Fixtures.Options(storePath, autoApprove: true));
        engine1.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));
        var info = engine1.AddJsonRuleFromSource(PlCustomersRule);

        // A new app version whose context no longer has Customer at all. On disk an upgrade looks
        // like the SAME context type with a different shape, so that is what the stored rule has to
        // claim — a test assembly cannot hold two versions of one type, and leaving it pointing at
        // RichOrder would make this the "another engine's rule" case instead, which is skipped.
        RestampContextType(storePath, info.Id, typeof(TestOrderV2));

        var engine2 = new RuleEngine<ITestDiscount, TestOrderV2>(Fixtures.Options(storePath));
        engine2.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));
        engine2.ReloadFromStore();

        Assert.Null(engine2.Resolve(new TestOrderV2(150m, 1)));
        var rule = engine2.GetRules().Single(r => r.Id == info.Id);
        Assert.Equal(RuleStatus.Quarantined, rule.Status);
        Assert.Contains("changed shape", rule.StatusReason);
    }

    [Fact]
    public void Rules_of_another_engine_sharing_the_store_are_skipped_not_quarantined()
    {
        // StorePath defaults to a folder relative to the process, so two engines in one app land
        // here by simply not setting it. Quarantining is a permanent disk write that fixing the
        // configuration would not undo — and these rules were never this engine's to judge.
        var storePath = Fixtures.NewStorePath();

        var richEngine = new RuleEngine<ITestDiscount, RichOrder>(Fixtures.Options(storePath, autoApprove: true));
        richEngine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));
        var info = richEngine.AddJsonRuleFromSource(PlCustomersRule);

        var otherEngine = new RuleEngine<ITestDiscount, TestOrderV2>(Fixtures.Options(storePath));
        otherEngine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));
        otherEngine.ReloadFromStore();

        // The other engine neither runs it, claims it, nor damages it...
        Assert.Null(otherEngine.Resolve(new TestOrderV2(150m, 1)));
        Assert.DoesNotContain(otherEngine.GetRules(), r => r.Id == info.Id);

        // ...and its owner still finds it exactly as it left it.
        var reopened = new RuleEngine<ITestDiscount, RichOrder>(Fixtures.Options(storePath));
        reopened.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));
        reopened.ReloadFromStore();

        Assert.Equal(RuleStatus.Approved, reopened.GetRules().Single(r => r.Id == info.Id).Status);
        Assert.NotNull(reopened.Resolve(new RichOrder(1m, new TestCustomer("a", "PL", CustomerTier.Gold), [], DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void Approving_another_engines_rule_throws_and_writes_nothing()
    {
        var storePath = Fixtures.NewStorePath();

        var richEngine = new RuleEngine<ITestDiscount, RichOrder>(Fixtures.Options(storePath));
        richEngine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));
        var info = richEngine.AddJsonRuleFromSource(PlCustomersRule);

        var otherEngine = new RuleEngine<ITestDiscount, TestOrderV2>(Fixtures.Options(storePath));
        otherEngine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));

        var ex = Assert.Throws<RuleStateException>(() => otherEngine.Approve(info.Id, "reviewer"));
        Assert.Contains("StorePath", ex.Message);

        Assert.Equal(RuleStatus.PendingApproval, richEngine.GetRules().Single(r => r.Id == info.Id).Status);
    }

    /// <summary>
    /// Rewrites the context type recorded for a stored rule. Only the source is covered by the
    /// tamper hash, so editing metadata is legitimate — this is how a version upgrade presents
    /// itself: same type name, different shape.
    /// </summary>
    private static void RestampContextType(string storePath, string ruleId, Type contextType)
    {
        var path = Path.Combine(storePath, ruleId + ".meta.json");
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        metadata["ContextType"] = contextType.FullName;
        File.WriteAllText(path, metadata.ToJsonString());
    }

    [Fact]
    public async Task Tampered_json_document_is_quarantined_on_reload()
    {
        var storePath = Fixtures.NewStorePath();

        var engine1 = Fixtures.JsonEngine(storePath, autoApprove: true);
        var info = engine1.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);

        await File.AppendAllTextAsync(Path.Combine(storePath, info.Id + ".rule.json"), "\n");

        var engine2 = Fixtures.JsonEngine(storePath);
        engine2.ReloadFromStore();

        Assert.Null(engine2.Resolve(new TestOrder(150m, "alice", 1)));
        Assert.Equal(RuleStatus.Quarantined, engine2.GetRules().Single(r => r.Id == info.Id).Status);
    }

    [Fact]
    public async Task Store_layout_separates_metadata_from_source_by_kind()
    {
        var storePath = Fixtures.NewStorePath();
        var engine = Fixtures.JsonEngine(storePath);

        var json = engine.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);
        var csharp = engine.AddRuleFromSource(Fixtures.BigOrderRule);

        Assert.True(File.Exists(Path.Combine(storePath, json.Id + ".meta.json")));
        Assert.True(File.Exists(Path.Combine(storePath, json.Id + ".rule.json")));
        Assert.True(File.Exists(Path.Combine(storePath, csharp.Id + ".meta.json")));
        Assert.True(File.Exists(Path.Combine(storePath, csharp.Id + ".cs")));
    }

    [Fact]
    public async Task A_stray_json_file_in_the_store_is_ignored_not_fatal()
    {
        var storePath = Fixtures.NewStorePath();
        var engine = Fixtures.JsonEngine(storePath, autoApprove: true);
        engine.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);

        await File.WriteAllTextAsync(Path.Combine(storePath, "notes.json"), """{ "note": "hand-dropped file" }""");
        await File.WriteAllTextAsync(Path.Combine(storePath, "broken.meta.json"), "{ not valid json");

        var engine2 = Fixtures.JsonEngine(storePath);
        engine2.ReloadFromStore();

        Assert.Single(engine2.GetRules());
        Assert.NotNull(engine2.Resolve(new TestOrder(150m, "alice", 1)));
    }
}
