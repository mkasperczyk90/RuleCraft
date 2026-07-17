using System.Collections;

namespace RuleCraft.Json;

internal enum ComparisonOp
{
    Eq,
    Neq,
    Gt,
    Gte,
    Lt,
    Lte,
    In,
    NotIn,
    Contains,
    StartsWith,
    EndsWith,
    IsNull,
    NotNull,
}

/// <summary>A parsed, type-checked predicate node. Evaluation does no parsing and no conversion.</summary>
internal abstract class ConditionNode
{
    public abstract bool Evaluate(object context);
}

internal sealed class AllNode(IReadOnlyList<ConditionNode> children) : ConditionNode
{
    public override bool Evaluate(object context) => children.All(c => c.Evaluate(context));
}

internal sealed class AnyNode(IReadOnlyList<ConditionNode> children) : ConditionNode
{
    public override bool Evaluate(object context) => children.Any(c => c.Evaluate(context));
}

internal sealed class NotNode(ConditionNode child) : ConditionNode
{
    public override bool Evaluate(object context) => !child.Evaluate(context);
}

internal sealed class AlwaysNode : ConditionNode
{
    public override bool Evaluate(object context) => true;
}

/// <summary>
/// One field comparison. The constant was coerced to the field's exact CLR type at parse time,
/// so ordering can go straight through <see cref="IComparable"/> and equality through
/// <see cref="object.Equals(object)"/> without surprises.
/// </summary>
internal sealed class ComparisonNode(
    ContextField field,
    ComparisonOp op,
    object? value,
    IReadOnlyList<object?>? values,
    StringComparison stringComparison) : ConditionNode
{
    public override bool Evaluate(object context)
    {
        var actual = field.GetValue(context);

        return op switch
        {
            ComparisonOp.IsNull => actual is null,
            ComparisonOp.NotNull => actual is not null,
            ComparisonOp.Eq => AreEqual(actual, value),
            ComparisonOp.Neq => !AreEqual(actual, value),
            ComparisonOp.In => values!.Any(v => AreEqual(actual, v)),
            ComparisonOp.NotIn => !values!.Any(v => AreEqual(actual, v)),
            ComparisonOp.Contains => Contains(actual),
            ComparisonOp.StartsWith => actual is string s && value is string prefix && s.StartsWith(prefix, stringComparison),
            ComparisonOp.EndsWith => actual is string s2 && value is string suffix && s2.EndsWith(suffix, stringComparison),
            _ => CompareOrdered(actual),
        };
    }

    private bool AreEqual(object? actual, object? expected)
    {
        if (actual is null || expected is null)
            return actual is null && expected is null;

        if (actual is string left && expected is string right)
            return string.Equals(left, right, stringComparison);

        return actual.Equals(expected);
    }

    private bool Contains(object? actual)
    {
        // String before IEnumerable: a string is also IEnumerable<char>, and substring is what a
        // business rule means by "contains".
        if (actual is string text)
            return value is string needle && text.Contains(needle, stringComparison);

        if (actual is IEnumerable items)
            return items.Cast<object?>().Any(item => AreEqual(item, value));

        return false;
    }

    /// <summary>Ordering with a null on either side is false — never an exception.</summary>
    private bool CompareOrdered(object? actual)
    {
        if (actual is null || value is null || actual is not IComparable comparable)
            return false;

        var result = comparable.CompareTo(value);
        return op switch
        {
            ComparisonOp.Gt => result > 0,
            ComparisonOp.Gte => result >= 0,
            ComparisonOp.Lt => result < 0,
            ComparisonOp.Lte => result <= 0,
            _ => false,
        };
    }
}
