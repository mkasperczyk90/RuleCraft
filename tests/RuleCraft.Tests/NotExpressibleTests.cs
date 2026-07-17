using Microsoft.Extensions.AI;

namespace RuleCraft.Tests;

/// <summary>
/// The JSON prompt offers the model one way out: reply {"error": "…"} when the grammar cannot
/// express the spec. These pin down that the way out actually leads somewhere — the answer must
/// reach the caller as "escalate to C#", not as a retried parse failure.
/// </summary>
public class NotExpressibleTests
{
    private const string Refusal =
        """
        ```json
        {"error": "The rule needs 1% per 100 above a threshold, and the DSL has no arithmetic."}
        ```
        """;

    private static RuleEngine<ITestDiscount, TestOrder> EngineWith(params string[] responses)
    {
        var options = Fixtures.Options();
        options.ChatClient = new ScriptedChatClient(responses);
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);
        engine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));
        return engine;
    }

    [Fact]
    public async Task Refusal_surfaces_as_RuleNotExpressible_carrying_the_models_reason()
    {
        var engine = EngineWith(Refusal);

        var ex = await Assert.ThrowsAsync<RuleNotExpressibleException>(() =>
            engine.AddJsonRuleAsync("1% per 100 zl above the threshold, capped at 20%"));

        Assert.Contains("no arithmetic", ex.Reason);
        Assert.Contains("AddRuleAsync", ex.Message);
        Assert.Equal("1% per 100 zl above the threshold, capped at 20%", ex.Spec);
    }

    [Fact]
    public async Task Refusal_is_final_and_is_not_retried()
    {
        // Retrying a refusal cannot help: the grammar will not grow between attempts. Burning the
        // attempt budget would also bury the reason under a generic "did not converge" error.
        var client = new ScriptedChatClient(Refusal, "```json\n" + Fixtures.BigOrderJsonRule + "\n```");
        var options = Fixtures.Options();
        options.ChatClient = client;
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);
        engine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));

        await Assert.ThrowsAsync<RuleNotExpressibleException>(() => engine.AddJsonRuleAsync("something impossible"));

        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task Refusal_stores_nothing()
    {
        var storePath = Fixtures.NewStorePath();
        var options = Fixtures.Options(storePath);
        options.ChatClient = new ScriptedChatClient(Refusal);
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);
        engine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));

        await Assert.ThrowsAsync<RuleNotExpressibleException>(() => engine.AddJsonRuleAsync("impossible"));

        Assert.Empty(engine.GetRules());
        Assert.Empty(engine.GetPendingRules());
    }

    [Fact]
    public void A_hand_written_document_named_error_is_a_parse_error_not_a_refusal()
    {
        // The refusal protocol exists only between the engine and the model. A human posting this
        // wrote an invalid rule, and must hear that — not "escalate to C#".
        var engine = Fixtures.JsonEngine();

        var ex = Assert.Throws<RuleValidationException>(() =>
            engine.AddJsonRuleFromSource("""{"error": "I am not a rule"}"""));

        Assert.NotEmpty(ex.Report.Diagnostics);
    }

    [Fact]
    public async Task A_rule_that_merely_mentions_error_is_still_a_rule()
    {
        // Guard against reading any document with an "error" key as a refusal: a real rule carries
        // 'when', and that is what tells the two apart.
        const string ruleWithErrorField =
            """
            ```json
            {
              "name": "big-order-json",
              "priority": 2,
              "when": { "field": "Total", "op": "gte", "value": 100 },
              "then": { "discount": 0.10 },
              "error": "not a refusal — just a stray field",
              "tests": [
                { "context": { "Total": 150, "Customer": "a", "ItemCount": 1 }, "applies": true },
                { "context": { "Total": 50, "Customer": "a", "ItemCount": 1 }, "applies": false }
              ]
            }
            ```
            """;

        var engine = EngineWith(ruleWithErrorField, "```json\n" + Fixtures.BigOrderJsonRule + "\n```");

        // Read as a rule: 'error' is an unmapped member, so it fails validation and gets retried —
        // the ordinary path. What matters is that it is NOT reported as not-expressible.
        var info = await engine.AddJsonRuleAsync("orders of 100 or more get 10% off");

        Assert.Equal(RuleStatus.PendingApproval, info.Status);
    }

    private sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        private int _index;

        public int Calls => _index;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
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
