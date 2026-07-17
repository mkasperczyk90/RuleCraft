using System.Runtime.Loader;
using Microsoft.Extensions.AI;

namespace RuleCraft.Tests;

/// <summary>The sandbox claim, the validation gates and the configuration contract.</summary>
public class JsonRuleGateTests
{
    // ---------------------------------------------------------------- sandbox

    [Fact]
    public async Task Json_rule_needs_no_security_analysis_and_creates_no_load_context()
    {
        var engine = Fixtures.JsonEngine();
        var info = engine.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);
        engine.Approve(info.Id, "reviewer");

        var pendingBefore = engine.GetRules().Single(r => r.Id == info.Id);
        Assert.True(pendingBefore.IsLoaded);

        // A JSON rule is interpreted: there is no assembly, so no load context bears its id.
        // (Filter by id — xunit runs classes in parallel, so a global count would be flaky.)
        Assert.DoesNotContain(AssemblyLoadContext.All, c => c.Name?.Contains(info.Id) == true);
    }

    [Fact]
    public async Task Valid_json_rule_reports_no_diagnostics_and_no_security_findings()
    {
        var engine = Fixtures.JsonEngine();
        var info = engine.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);

        var report = Assert.Single(engine.GetPendingRules()).Report;
        Assert.Empty(report.Diagnostics);
        Assert.Empty(report.SecurityFindings);
        Assert.All(report.TestResults, t => Assert.NotEqual(TestOutcome.Failed, t.Outcome));
        Assert.Equal(RuleStatus.PendingApproval, info.Status);
    }

    // ---------------------------------------------------------------- gates

    [Fact]
    public async Task Document_test_case_that_contradicts_the_predicate_rejects_the_rule()
    {
        // The condition is inverted relative to what the test cases claim — no parser can see
        // that, only running the cases can.
        const string inverted =
            """
            {
              "name": "inverted",
              "when": { "field": "Total", "op": "lte", "value": 100 },
              "then": { "discount": 0.10 },
              "tests": [
                { "name": "big orders should apply", "context": { "Total": 150, "Customer": "a", "ItemCount": 1 }, "applies": true },
                { "name": "small orders should not", "context": { "Total": 50, "Customer": "a", "ItemCount": 1 }, "applies": false }
              ]
            }
            """;

        var engine = Fixtures.JsonEngine();
        var ex = Assert.Throws<RuleValidationException>(() => engine.AddJsonRuleFromSource(inverted));

        var failed = ex.Report.TestResults.Where(t => t.Outcome == TestOutcome.Failed).ToList();
        Assert.Equal(2, failed.Count);
        Assert.Contains(failed, t => t.Name == "big orders should apply");
        Assert.Contains(failed, t => t.Message!.Contains("did not apply"));
    }

    [Fact]
    public async Task Acceptance_test_runs_against_the_factory_built_implementation()
    {
        // The document is internally consistent; only the developer's invariant knows 5.0 is not
        // a legal discount. This is the gate that JSON rules would otherwise lack.
        const string absurdDiscount =
            """
            {
              "name": "absurd",
              "when": { "field": "Total", "op": "gte", "value": 100 },
              "then": { "discount": 5.0 },
              "tests": [
                { "context": { "Total": 150, "Customer": "a", "ItemCount": 1 }, "applies": true },
                { "context": { "Total": 50, "Customer": "a", "ItemCount": 1 }, "applies": false }
              ]
            }
            """;

        var engine = Fixtures.JsonEngine();
        engine.AddAcceptanceTest(new DiscountRangeInvariant());

        var ex = Assert.Throws<RuleValidationException>(() => engine.AddJsonRuleFromSource(absurdDiscount));

        Assert.Contains(ex.Report.TestResults,
            t => t.Outcome == TestOutcome.Failed && t.Name.StartsWith("acceptance:"));
        Assert.Empty(engine.GetPendingRules());
    }

    [Fact]
    public async Task Unknown_property_in_then_is_rejected()
    {
        const string badThen =
            """
            {
              "name": "bad-then",
              "when": { "always": true },
              "then": { "discountPercent": 10 },
              "tests": [ { "context": { "Total": 1, "Customer": "a", "ItemCount": 1 }, "applies": true } ]
            }
            """;

        var engine = Fixtures.JsonEngine();
        var ex = Assert.Throws<RuleValidationException>(() => engine.AddJsonRuleFromSource(badThen));
        Assert.Contains(ex.Report.Diagnostics, d => d.Contains("discountPercent"));
    }

    [Fact]
    public async Task Wrong_type_in_then_names_the_json_path()
    {
        const string badThen =
            """
            {
              "name": "bad-then",
              "when": { "always": true },
              "then": { "discount": "ten percent" },
              "tests": [ { "context": { "Total": 1, "Customer": "a", "ItemCount": 1 }, "applies": true } ]
            }
            """;

        var engine = Fixtures.JsonEngine();
        var ex = Assert.Throws<RuleValidationException>(() => engine.AddJsonRuleFromSource(badThen));
        Assert.Contains(ex.Report.Diagnostics, d => d.Contains("$.discount"));
    }

    [Fact]
    public async Task Factory_rejecting_a_then_surfaces_the_developer_message()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        engine.EnableJsonRules<TestDiscountAction>(then =>
            then.Discount > 0.5m
                ? throw new ArgumentOutOfRangeException(nameof(then), "Discounts above 50% need board approval.")
                : new FlatTestDiscount(then.Discount));

        const string tooGenerous =
            """
            {
              "name": "too-generous",
              "when": { "always": true },
              "then": { "discount": 0.9 },
              "tests": [ { "context": { "Total": 1, "Customer": "a", "ItemCount": 1 }, "applies": true } ]
            }
            """;

        var ex = Assert.Throws<RuleValidationException>(() => engine.AddJsonRuleFromSource(tooGenerous));
        Assert.Contains(ex.Report.Diagnostics, d => d.Contains("board approval"));
    }

    [Fact]
    public async Task Test_context_with_an_unknown_property_fails_that_case()
    {
        const string badContext =
            """
            {
              "name": "bad-context",
              "when": { "always": true },
              "then": { "discount": 0.1 },
              "tests": [ { "name": "typo", "context": { "Totl": 1, "Customer": "a", "ItemCount": 1 }, "applies": true } ]
            }
            """;

        var engine = Fixtures.JsonEngine();
        var ex = Assert.Throws<RuleValidationException>(() => engine.AddJsonRuleFromSource(badContext));
        Assert.Contains(ex.Report.TestResults, t => t.Outcome == TestOutcome.Failed && t.Name == "typo");
    }

    // ---------------------------------------------------------------- configuration

    [Fact]
    public async Task Json_rules_must_be_enabled_before_use()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());

        var ex = Assert.Throws<RuleStateException>(() =>
            engine.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule));
        Assert.Contains("EnableJsonRules", ex.Message);
    }

    [Fact]
    public void Enabling_json_rules_twice_is_rejected()
    {
        var engine = Fixtures.JsonEngine();
        Assert.Throws<InvalidOperationException>(() =>
            engine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount)));
    }

    [Fact]
    public async Task AutoApprove_loads_a_json_rule_immediately()
    {
        var engine = Fixtures.JsonEngine(autoApprove: true);
        var info = engine.AddJsonRuleFromSource(Fixtures.BigOrderJsonRule);

        Assert.Equal(RuleStatus.Approved, info.Status);
        Assert.True(info.IsLoaded);
        Assert.NotNull(engine.Resolve(new TestOrder(150m, "a", 1)));
    }

    // ---------------------------------------------------------------- generation

    [Fact]
    public async Task Llm_generated_json_in_a_fence_is_parsed_and_queued()
    {
        var fake = new FakeChatClient("Here you go:\n```json\n" + Fixtures.BigOrderJsonRule + "\n```");
        var options = Fixtures.Options();
        options.ChatClient = fake;
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);
        engine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));

        var info = await engine.AddJsonRuleAsync("orders of 100 or more get 10% off");

        Assert.Equal(RuleOrigin.Json, info.Origin);
        Assert.Equal(RuleStatus.PendingApproval, info.Status);
        Assert.Equal(1, fake.Calls);

        // The prompt must show the real field names and the `then` shape, or the model is guessing.
        var userPrompt = fake.LastMessages.First(m => m.Role == ChatRole.User).Text!;
        Assert.Contains("Total: decimal", userPrompt);
        Assert.Contains("\"discount\": <decimal>", userPrompt);
    }

    [Fact]
    public async Task Generation_loop_feeds_parse_errors_back_to_the_model()
    {
        const string brokenRule =
            """
            ```json
            { "name": "broken", "when": { "field": "Totl", "op": "gte", "value": 100 },
              "then": { "discount": 0.1 },
              "tests": [ { "context": { "Total": 150, "Customer": "a", "ItemCount": 1 }, "applies": true },
                         { "context": { "Total": 1, "Customer": "a", "ItemCount": 1 }, "applies": false } ] }
            ```
            """;

        var fake = new FakeChatClient(brokenRule, "```json\n" + Fixtures.BigOrderJsonRule + "\n```");
        var options = Fixtures.Options();
        options.ChatClient = fake;
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);
        engine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));

        var info = await engine.AddJsonRuleAsync("orders of 100 or more get 10% off");

        Assert.Equal(RuleStatus.PendingApproval, info.Status);
        Assert.Equal(2, fake.Calls);
        Assert.Contains(fake.LastMessages, m => m.Role == ChatRole.User && (m.Text?.Contains("Totl") ?? false));
    }

    private sealed class DiscountRangeInvariant : IRuleAcceptanceTest<ITestDiscount>
    {
        public string Name => "discount is a fraction in [0, 1]";

        public TestResult Run(ITestDiscount implementation)
        {
            var discount = implementation.GetDiscount(new TestOrder(150m, "a", 1));
            return discount is >= 0m and <= 1m
                ? TestResult.Passed()
                : TestResult.Failed($"Discount {discount} is outside [0, 1].");
        }
    }

    private sealed class FakeChatClient(params string[] responses) : IChatClient
    {
        private int _index;

        public int Calls => _index;

        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToList();
            var text = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
