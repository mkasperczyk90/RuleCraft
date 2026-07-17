using Microsoft.Extensions.AI;

namespace RuleCraft.Tests;

public class GenerationTests
{
    [Fact]
    public async Task Generation_loop_converges_after_compile_error_feedback()
    {
        const string brokenAnswer =
            """
            Here is the rule:
            ```csharp
            using RuleCraft;
            using RuleCraft.Tests;

            namespace RuleCraft.Generated.Gen;

            public sealed class GenRule : IRule<ITestDiscount, TestOrder>, ITestDiscount
            {
                public bool AppliesTo(TestOrder context) => context.Total >= 100m   // missing semicolon
                public ITestDiscount Implementation => this;
                public decimal GetDiscount(TestOrder order) => 0.10m;
            }
            ```
            """;

        var goodAnswer = "```csharp\n" + Fixtures.BigOrderRule + "\n```";

        var fake = new FakeChatClient(brokenAnswer, goodAnswer);
        var options = Fixtures.Options();
        options.ChatClient = fake;
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);

        var info = await engine.AddRuleAsync("orders of 100 or more get 10% off");

        Assert.Equal(RuleStatus.PendingApproval, info.Status);
        Assert.Equal(2, fake.Calls);
        // The second request must have carried the compiler feedback.
        Assert.Contains(fake.LastMessages, m => m.Role == ChatRole.User && (m.Text?.Contains("error CS1002") ?? false));
    }

    [Fact]
    public async Task Generation_gives_up_after_max_attempts_with_full_history()
    {
        var fake = new FakeChatClient("```csharp\nclass Broken {\n```", "```csharp\nclass StillBroken {\n```");
        var options = Fixtures.Options();
        options.ChatClient = fake;
        options.MaxGenerationAttempts = 2;
        var engine = new RuleEngine<ITestDiscount, TestOrder>(options);

        var ex = await Assert.ThrowsAsync<RuleGenerationException>(() => engine.AddRuleAsync("whatever"));

        Assert.Equal(2, ex.Attempts.Count);
        Assert.All(ex.Attempts, attempt => Assert.NotEmpty(attempt.Report.Diagnostics));
    }

    [Fact]
    public async Task AddRuleAsync_without_chat_client_fails_fast()
    {
        var engine = new RuleEngine<ITestDiscount, TestOrder>(Fixtures.Options());
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.AddRuleAsync("spec"));
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
