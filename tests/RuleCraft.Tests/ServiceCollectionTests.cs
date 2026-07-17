using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace RuleCraft.Tests;

public class ServiceCollectionTests
{
    [Fact]
    public void AddRuleCraft_registers_a_singleton_and_runs_the_startup_sequence_once()
    {
        var storePath = Fixtures.NewStorePath();
        var configured = 0;

        var services = new ServiceCollection();
        services.AddRuleCraft<ITestDiscount, TestOrder>(
            options => options.StorePath = storePath,
            engine =>
            {
                configured++;
                engine.SetFallback(new ZeroDiscountFallback());
                engine.EnableJsonRules<TestDiscountAction>(then => new FlatTestDiscount(then.Discount));
                engine.ReloadFromStore();
            });

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<RuleEngine<ITestDiscount, TestOrder>>();
        var second = provider.GetRequiredService<RuleEngine<ITestDiscount, TestOrder>>();

        Assert.Same(first, second);
        Assert.Equal(1, configured);
        // The fallback proves configureEngine ran before anyone could resolve it.
        Assert.IsType<ZeroDiscountFallback>(first.Resolve(new TestOrder(1m, "a", 1)));
    }

    [Fact]
    public void The_chat_client_comes_from_the_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new UnusedChatClient());
        services.AddRuleCraft<ITestDiscount, TestOrder>(options => options.StorePath = Fixtures.NewStorePath());

        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<RuleEngine<ITestDiscount, TestOrder>>();

        // Without a client this throws InvalidOperationException before ever calling the LLM, so
        // reaching the fake at all is the proof that the container's client was picked up.
        Assert.IsType<NotSupportedException>(
            Record.Exception(() => engine.AddRuleAsync("whatever").GetAwaiter().GetResult()));
    }

    [Fact]
    public async Task configureOptions_wins_over_the_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IChatClient>(new UnusedChatClient());
        services.AddRuleCraft<ITestDiscount, TestOrder>(options =>
        {
            options.StorePath = Fixtures.NewStorePath();
            options.ChatClient = null;
        });

        using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<RuleEngine<ITestDiscount, TestOrder>>();

        // Nulled out after the container handed one over: the callback has the last word.
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.AddRuleAsync("whatever"));
    }

    [Fact]
    public void The_container_disposes_the_engine_and_unloads_its_rules()
    {
        var services = new ServiceCollection();
        services.AddRuleCraft<ITestDiscount, TestOrder>(options =>
        {
            options.StorePath = Fixtures.NewStorePath();
            options.AutoApprove = true;
        });

        var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<RuleEngine<ITestDiscount, TestOrder>>();
        engine.AddRuleFromSource(Fixtures.BigOrderRule);

        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => engine.Resolve(new TestOrder(150m, "a", 1)));
    }

    private sealed class UnusedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("reached the container's chat client");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
