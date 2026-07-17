using RuleCraft.Sample.Discounts;

namespace RuleCraft.Sample.Api;

using Engine = RuleEngine<IDiscountRule, Order>;

/// <summary>Where the rules are actually used — the application side of the engine.</summary>
internal static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        app.MapPost("/orders/evaluate", (Order order, Engine engine) =>
        {
            // The whole integration: ask for the rule that fits this order, then call the contract.
            // Resolve returns the winning rule's implementation, the fallback, or null.
            var rule = engine.Resolve(order) ?? new NoDiscount();
            var discount = rule.GetDiscount(order);

            return Results.Ok(new EvaluationResult(
                order.Total,
                discount,
                FinalTotal: Math.Round(order.Total * (1 - discount), 2),
                MatchedByRule: rule is not NoDiscount));
        });
    }
}
