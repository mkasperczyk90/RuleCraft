using System.Linq.Expressions;
using System.Reflection;

namespace RuleCraft.Json;

/// <summary>
/// A dotted property path resolved against the context type once, at parse time
/// (<c>Total</c>, <c>Customer.Address.Country</c>, <c>PlacedAt.DayOfWeek</c>, <c>Items.Count</c>).
/// </summary>
internal sealed class ContextField
{
    private const int MaxSegments = 8;
    private const BindingFlags Lookup = BindingFlags.Public | BindingFlags.Instance;

    private readonly PropertyInfo[] _chain;
    private readonly Func<object?, object?>[] _getters;

    private ContextField(string path, PropertyInfo[] chain, bool canBeNull)
    {
        Path = path;
        _chain = chain;
        _getters = Array.ConvertAll(chain, CompileGetter);
        CanBeNull = canBeNull;
    }

    public string Path { get; }

    /// <summary>Declared type of the final segment.</summary>
    public Type Type => _chain[^1].PropertyType;

    /// <summary>
    /// Whether the field can hold null: false for non-nullable value types, and false for reference
    /// types the context declares as non-nullable. Unannotated code reads as "might be null" — an
    /// absent annotation is not a promise.
    /// </summary>
    public bool CanBeNull { get; }

    /// <summary>Returns null when the value is null or any intermediate on the path is null.</summary>
    public object? GetValue(object? root)
    {
        var current = root;
        foreach (var getter in _getters)
        {
            if (current is null)
                return null;
            current = getter(current);
        }

        return current;
    }

    /// <summary>
    /// A compiled getter rather than <see cref="PropertyInfo.GetValue(object)"/>: this runs for
    /// every condition of every JSON rule on every <c>Resolve</c> — the hottest path in the library.
    /// Compiling costs a one-off at parse time.
    /// </summary>
    private static Func<object?, object?> CompileGetter(PropertyInfo property)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var body = Expression.Convert(
            Expression.Property(Expression.Convert(instance, property.DeclaringType!), property),
            typeof(object));

        return Expression.Lambda<Func<object?, object?>>(body, instance).Compile();
    }

    public static bool TryResolve(Type rootType, string path, out ContextField? field, out string? error)
    {
        field = null;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Field path is empty.";
            return false;
        }

        var segments = path.Split('.', StringSplitOptions.TrimEntries);
        if (segments.Length > MaxSegments)
        {
            error = $"Field path '{path}' is too deep (max {MaxSegments} segments).";
            return false;
        }

        var chain = new PropertyInfo[segments.Length];
        var currentType = rootType;

        for (var i = 0; i < segments.Length; i++)
        {
            var property = FindProperty(currentType, segments[i]);
            if (property is null)
            {
                error = $"Unknown field '{path}': type '{currentType.Name}' has no property '{segments[i]}'. " +
                        $"Available: {Available(currentType)}.";
                return false;
            }

            chain[i] = property;
            // Nested lookups continue on the underlying type so `Date?.Year` style paths work.
            currentType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        }

        field = new ContextField(path, chain, CanHoldNull(chain[^1]));
        return true;
    }

    /// <summary>
    /// Reads the declared nullability, so <c>isNull</c> on a non-nullable <c>string</c> is caught at
    /// parse time like the value-type case, instead of quietly never matching.
    /// </summary>
    private static bool CanHoldNull(PropertyInfo property)
    {
        if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
            return true;

        if (property.PropertyType.IsValueType)
            return false;

        // Unknown (the declaring code has nullable annotations disabled) counts as nullable:
        // rejecting a rule on the strength of an annotation nobody wrote would be worse.
        return new NullabilityInfoContext().Create(property).ReadState != NullabilityState.NotNull;
    }

    private static PropertyInfo? FindProperty(Type type, string name) =>
        type.GetProperties(Lookup)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Field list for error messages — this is what lets an LLM fix a typo in one round-trip.</summary>
    private static string Available(Type type)
    {
        var names = type.GetProperties(Lookup)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
            .Select(p => p.Name)
            .ToArray();

        return names.Length == 0 ? "(none)" : string.Join(", ", names);
    }

    public override string ToString() => Path;
}
