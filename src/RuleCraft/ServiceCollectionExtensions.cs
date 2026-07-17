using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RuleCraft;

/// <summary>Registers a <see cref="RuleEngine{TContract,TContext}"/> in a DI container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine as a singleton, taking <see cref="ILoggerFactory"/> and
    /// <see cref="IChatClient"/> from the container when they are there. The container owns it and
    /// disposes it with the host, unloading its rule assemblies.
    ///
    /// <paramref name="configureEngine"/> is where the startup sequence goes — fallback, acceptance
    /// tests, <c>EnableJsonRules</c>, static rules, <c>ReloadFromStore</c>. It runs once, inside the
    /// singleton factory, which is what keeps <c>EnableJsonRules</c> ahead of <c>ReloadFromStore</c>:
    /// ordering that the raw API leaves to the caller to remember.
    ///
    /// The factory runs on first resolve, so the first request pays for reloading the store. Resolve
    /// the engine once at startup if you would rather pay at boot:
    /// <code>app.Services.GetRequiredService&lt;RuleEngine&lt;IDiscountRule, Order&gt;&gt;();</code>
    /// </summary>
    /// <param name="services">The container to register the engine in.</param>
    /// <param name="configureOptions">Runs before the engine is built; wins over anything taken from the container.</param>
    /// <param name="configureEngine">Runs once on the new engine, before anyone can resolve it.</param>
    public static IServiceCollection AddRuleCraft<TContract, TContext>(
        this IServiceCollection services,
        Action<RuleEngineOptions>? configureOptions = null,
        Action<RuleEngine<TContract, TContext>>? configureEngine = null)
        where TContract : class
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider =>
        {
            var options = new RuleEngineOptions
            {
                LoggerFactory = provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance,
                ChatClient = provider.GetService<IChatClient>(),
            };

            configureOptions?.Invoke(options);

            var engine = new RuleEngine<TContract, TContext>(options);
            configureEngine?.Invoke(engine);
            return engine;
        });

        return services;
    }
}
