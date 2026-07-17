using System.Text.Json;

namespace RuleCraft.Json;

/// <summary>
/// Converts a JSON literal into the field's exact CLR type — at PARSE time, once.
///
/// This is load-bearing, not an optimisation: comparing boxed values of different types silently
/// fails (<c>((object)500L).Equals((object)500m)</c> is false), so a rule written as
/// <c>{"field":"Total","op":"eq","value":500}</c> against a <c>decimal</c> field would simply
/// never match — no error, no diagnostic. Coercing up front turns that into either a correct
/// comparison or a loud error.
/// </summary>
internal static class ValueCoercion
{
    public static bool TryCoerce(JsonElement value, Type target, string fieldPath, out object? result, out string? error)
    {
        result = null;
        error = null;

        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (underlying != target || !target.IsValueType)
                return true; // null is legal for nullable and reference types

            error = $"Field '{fieldPath}' is {Describe(target)} and cannot be compared to null.";
            return false;
        }

        if (underlying.IsEnum)
            return TryCoerceEnum(value, underlying, fieldPath, out result, out error);

        if (underlying == typeof(string))
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                error = Mismatch(fieldPath, target, value);
                return false;
            }

            result = value.GetString();
            return true;
        }

        if (underlying == typeof(bool))
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = Mismatch(fieldPath, target, value);
                return false;
            }

            result = value.GetBoolean();
            return true;
        }

        if (underlying == typeof(Guid))
            return TryRead(value, fieldPath, target, out result, out error, e => e.TryGetGuid(out var v) ? v : null);

        // ISO 8601 only, via System.Text.Json — never DateTime.TryParse, whose culture rules would
        // make "17.07.2026" mean different days on different machines.
        if (underlying == typeof(DateTime))
            return TryRead(value, fieldPath, target, out result, out error, e => e.TryGetDateTime(out var v) ? v : null);

        if (underlying == typeof(DateTimeOffset))
            return TryRead(value, fieldPath, target, out result, out error, e => e.TryGetDateTimeOffset(out var v) ? v : null);

        if (underlying == typeof(DateOnly))
            return TryRead(value, fieldPath, target, out result, out error,
                e => e.TryGetDateTime(out var v) ? DateOnly.FromDateTime(v) : null);

        if (underlying == typeof(TimeSpan))
            return TryRead(value, fieldPath, target, out result, out error,
                e => e.ValueKind == JsonValueKind.String && TimeSpan.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null);

        if (IsNumeric(underlying))
            return TryCoerceNumber(value, underlying, fieldPath, target, out result, out error);

        error = $"Field '{fieldPath}' has type {Describe(target)}, which the JSON rule DSL cannot compare. " +
                "Use a field of a primitive, string, enum or date type, or write this rule in C#.";
        return false;
    }

    private static bool TryCoerceEnum(JsonElement value, Type enumType, string fieldPath, out object? result, out string? error)
    {
        result = null;
        error = null;

        if (value.ValueKind != JsonValueKind.String)
        {
            error = $"Field '{fieldPath}' is enum {enumType.Name}; use one of its names as a string. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames(enumType))}.";
            return false;
        }

        var text = value.GetString()!;

        // Enum.TryParse happily accepts "5" and comma-separated lists, producing values that were
        // never declared — IsDefined is what makes an unknown value loud instead of silent.
        if (!Enum.TryParse(enumType, text, ignoreCase: true, out var parsed)
            || parsed is null
            || !Enum.IsDefined(enumType, parsed))
        {
            error = $"'{text}' is not a valid {enumType.Name} for field '{fieldPath}'. " +
                    $"Valid values: {string.Join(", ", Enum.GetNames(enumType))}.";
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryCoerceNumber(
        JsonElement value, Type underlying, string fieldPath, Type target, out object? result, out string? error)
    {
        result = null;
        error = null;

        if (value.ValueKind != JsonValueKind.Number)
        {
            error = Mismatch(fieldPath, target, value);
            return false;
        }

        object? read =
            underlying == typeof(int) ? (value.TryGetInt32(out var i) ? i : null) :
            underlying == typeof(long) ? (value.TryGetInt64(out var l) ? l : null) :
            underlying == typeof(short) ? (value.TryGetInt16(out var s) ? s : null) :
            underlying == typeof(byte) ? (value.TryGetByte(out var b) ? b : null) :
            underlying == typeof(sbyte) ? (value.TryGetSByte(out var sb) ? sb : null) :
            underlying == typeof(uint) ? (value.TryGetUInt32(out var ui) ? ui : null) :
            underlying == typeof(ulong) ? (value.TryGetUInt64(out var ul) ? ul : null) :
            underlying == typeof(ushort) ? (value.TryGetUInt16(out var us) ? us : null) :
            underlying == typeof(decimal) ? (value.TryGetDecimal(out var m) ? m : null) :
            underlying == typeof(double) ? (value.TryGetDouble(out var d) ? d : null) :
            underlying == typeof(float) ? (value.TryGetSingle(out var f) ? f : null) :
            null;

        if (read is null)
        {
            error = $"Value {value.GetRawText()} does not fit field '{fieldPath}' of type {Describe(target)}.";
            return false;
        }

        result = read;
        return true;
    }

    private static bool TryRead(
        JsonElement value, string fieldPath, Type target, out object? result, out string? error,
        Func<JsonElement, object?> read)
    {
        result = read(value);
        error = result is null ? Mismatch(fieldPath, target, value) : null;
        return result is not null;
    }

    private static bool IsNumeric(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
        || type == typeof(sbyte) || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort)
        || type == typeof(decimal) || type == typeof(double) || type == typeof(float);

    /// <summary>True for types that have a meaningful order (gt/gte/lt/lte).</summary>
    public static bool IsOrderable(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return IsNumeric(underlying)
            || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateOnly) || underlying == typeof(TimeSpan);
    }

    public static string Describe(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        return underlying is not null ? $"{underlying.Name}?" : type.Name;
    }

    private static string Mismatch(string fieldPath, Type target, JsonElement value) =>
        $"Value {value.GetRawText()} cannot be read as {Describe(target)} for field '{fieldPath}'.";
}
