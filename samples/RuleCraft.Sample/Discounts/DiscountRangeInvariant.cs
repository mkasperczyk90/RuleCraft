namespace RuleCraft.Sample.Discounts;

/// <summary>
/// An invariant this application enforces on every candidate, whatever its spec said and whoever
/// wrote it. Generated tests check a rule against its own intent; this checks it against the
/// contract — so a rule that "passes its own tests" but returns a 500% discount never loads.
/// </summary>
public sealed class DiscountRangeInvariant : IRuleAcceptanceTest<IDiscountRule>
{
    public string Name => "discount is a fraction in [0, 1]";

    public TestResult Run(IDiscountRule implementation)
    {
        Order[] samples =
        [
            new(0m, "new", 0, "PL"),
            new(49.99m, "regular", 1, "PL"),
            new(150m, "vip", 3, "DE"),
            new(10_000m, "vip", 120, "US"),
        ];

        foreach (var order in samples)
        {
            var discount = implementation.GetDiscount(order);
            if (discount is < 0m or > 1m)
                return TestResult.Failed(
                    $"GetDiscount returned {discount} for total {order.Total} — outside [0, 1].");
        }

        return TestResult.Passed();
    }
}
