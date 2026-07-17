namespace RuleCraft.Sample.Discounts;

/// <summary>
/// The contract every rule implements — the one method this application calls once a rule wins.
/// Keep it small: this is the entire surface an LLM-generated rule can act through.
/// </summary>
public interface IDiscountRule
{
    /// <summary>The discount as a fraction in [0, 1] — 0.10m means 10% off.</summary>
    decimal GetDiscount(Order order);
}

/// <summary>
/// The context every rule's predicate decides on. Rules can only test what is on this record,
/// so it doubles as the vocabulary of "when" — a JSON rule may name `Total`, `CustomerType`,
/// `ItemCount` or `Country`, and nothing else.
/// </summary>
public sealed record Order(decimal Total, string CustomerType, int ItemCount, string Country);
