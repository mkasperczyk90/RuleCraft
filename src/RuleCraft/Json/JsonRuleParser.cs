using System.Text.Json;

namespace RuleCraft.Json;

/// <summary>
/// Parses a JSON-DSL document and type-checks every condition against the context type.
/// Collects ALL errors rather than stopping at the first, so an LLM can fix everything in one
/// round-trip and a human sees the whole picture at once.
/// </summary>
internal static class JsonRuleParser
{
    // The document depth bounds the condition-tree depth, which bounds Evaluate's recursion.
    // System.Text.Json's reader is not recursive and enforces this by throwing, so this limit is
    // what makes the depth bound real rather than aspirational.
    private const int MaxDepth = 32;
    private const int MaxDocumentBytes = 64 * 1024;
    private const int MaxNodes = 200;
    private const int MaxTests = 50;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = MaxDepth,
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = MaxDepth,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Dictionary<string, ComparisonOp> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = ComparisonOp.Eq,
        ["neq"] = ComparisonOp.Neq,
        ["gt"] = ComparisonOp.Gt,
        ["gte"] = ComparisonOp.Gte,
        ["lt"] = ComparisonOp.Lt,
        ["lte"] = ComparisonOp.Lte,
        ["in"] = ComparisonOp.In,
        ["notIn"] = ComparisonOp.NotIn,
        ["contains"] = ComparisonOp.Contains,
        ["startsWith"] = ComparisonOp.StartsWith,
        ["endsWith"] = ComparisonOp.EndsWith,
        ["isNull"] = ComparisonOp.IsNull,
        ["notNull"] = ComparisonOp.NotNull,
    };

    /// <summary>
    /// Recognizes the refusal document the generation prompt asks for when the DSL genuinely cannot
    /// express a spec: <c>{"error": "…"}</c>. Only the generation loop calls this — a hand-written
    /// document containing "error" is just an invalid rule, and must stay a parse error.
    ///
    /// Without this the model's one correct answer would be read as a malformed rule, retried until
    /// the attempt budget ran out, and finally reported as "invalid document" — burying the single
    /// most useful thing the model can say: escalate this rule to C#.
    /// </summary>
    public static bool TryReadRefusal(string source, out string? reason)
    {
        reason = null;

        if (System.Text.Encoding.UTF8.GetByteCount(source) > MaxDocumentBytes)
            return false;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(source, DocumentOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            // Every real rule carries 'when'; a refusal carries only 'error'. Checking both ways
            // round keeps a rule that happens to have an "error" field from being read as a refusal.
            if (TryGetProperty(root, "when", out _))
                return false;

            if (!TryGetProperty(root, "error", out var error) || error.ValueKind != JsonValueKind.String)
                return false;

            reason = error.GetString();
            return !string.IsNullOrWhiteSpace(reason);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static bool TryParse(
        string source,
        Type contextType,
        StringComparison stringComparison,
        out JsonRuleDefinition? definition,
        out IReadOnlyList<string> errors)
    {
        definition = null;
        var problems = new List<string>();

        if (System.Text.Encoding.UTF8.GetByteCount(source) > MaxDocumentBytes)
        {
            errors = [$"The rule document is larger than {MaxDocumentBytes / 1024} KB."];
            return false;
        }

        JsonRuleDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<JsonRuleDocument>(source, SerializerOptions);
        }
        catch (JsonException ex)
        {
            errors = [ex.Message];
            return false;
        }

        if (document is null)
        {
            errors = ["The rule document is empty."];
            return false;
        }

        if (document.When.ValueKind == JsonValueKind.Undefined)
            problems.Add("Missing required property 'when' (the rule's condition).");

        if (document.Then.ValueKind == JsonValueKind.Undefined)
            problems.Add("Missing required property 'then' (the rule's outcome).");

        var tests = document.Tests ?? [];
        if (tests.Count > MaxTests)
            problems.Add($"Too many test cases ({tests.Count}); the maximum is {MaxTests}.");

        ConditionNode? when = null;
        var nodeCount = 0;
        var isAlways = false;

        if (document.When.ValueKind != JsonValueKind.Undefined)
        {
            using var parsed = Parse(document.When, problems);
            if (parsed is not null)
            {
                when = ParseCondition(parsed.RootElement, contextType, stringComparison, "when", problems, ref nodeCount);
                isAlways = when is AlwaysNode;
            }
        }

        if (when is not null)
            ValidateTestCoverage(tests, isAlways, problems);

        if (problems.Count > 0 || when is null)
        {
            errors = problems.Count > 0 ? problems : ["The rule's 'when' condition could not be parsed."];
            return false;
        }

        definition = new JsonRuleDefinition(document.Name, document.Priority, when, document.Then, tests, isAlways);
        errors = [];
        return true;
    }

    /// <summary>
    /// A rule that cannot demonstrate a case where it does NOT apply usually has an inverted or
    /// vacuous predicate. Enforced here rather than requested in the prompt, because a prompt is
    /// advisory and this gate is the only thing that catches an inverted operator.
    /// </summary>
    private static void ValidateTestCoverage(IReadOnlyList<JsonRuleTestCase> tests, bool isAlways, List<string> problems)
    {
        if (!tests.Any(t => t.Applies))
            problems.Add("At least one test case with \"applies\": true is required.");

        if (!isAlways && !tests.Any(t => !t.Applies))
            problems.Add("At least one test case with \"applies\": false is required (a case the rule must NOT match).");
    }

    private static JsonDocument? Parse(JsonElement element, List<string> problems)
    {
        try
        {
            return JsonDocument.Parse(element.GetRawText(), DocumentOptions);
        }
        catch (JsonException ex)
        {
            problems.Add($"Invalid 'when' condition: {ex.Message}");
            return null;
        }
    }

    private static ConditionNode? ParseCondition(
        JsonElement node, Type contextType, StringComparison comparison, string path,
        List<string> problems, ref int nodeCount)
    {
        if (++nodeCount > MaxNodes)
        {
            problems.Add($"The condition has more than {MaxNodes} nodes.");
            return null;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"'{path}' must be an object, found {node.ValueKind}.");
            return null;
        }

        var forms = new[] { "all", "any", "not", "always", "field" }
            .Where(f => node.TryGetProperty(f, out _))
            .ToArray();

        if (forms.Length == 0)
        {
            problems.Add(
                $"'{path}' is not a condition: expected one of {{\"all\":[…]}}, {{\"any\":[…]}}, " +
                "{\"not\":{…}}, {\"always\":true} or {\"field\":\"…\",\"op\":\"…\",\"value\":…}.");
            return null;
        }

        if (forms.Length > 1)
        {
            problems.Add($"'{path}' mixes condition forms ({string.Join(", ", forms)}); use exactly one.");
            return null;
        }

        return forms[0] switch
        {
            "all" or "any" => ParseGroup(node, forms[0], contextType, comparison, path, problems, ref nodeCount),
            "not" => ParseNot(node, contextType, comparison, path, problems, ref nodeCount),
            "always" => ParseAlways(node, path, problems),
            _ => ParseComparison(node, contextType, comparison, path, problems),
        };
    }

    private static ConditionNode? ParseGroup(
        JsonElement node, string form, Type contextType, StringComparison comparison, string path,
        List<string> problems, ref int nodeCount)
    {
        var array = node.GetProperty(form);
        if (array.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"'{path}.{form}' must be an array of conditions.");
            return null;
        }

        // An empty group is vacuously true (all) or false (any) — it would match everything or
        // nothing with no warning, so it is always a mistake.
        if (array.GetArrayLength() == 0)
        {
            problems.Add($"'{path}.{form}' is empty; that would match {(form == "all" ? "every" : "no")} context. " +
                         "Remove it or give it conditions.");
            return null;
        }

        var children = new List<ConditionNode>();
        var index = 0;
        foreach (var child in array.EnumerateArray())
        {
            var parsed = ParseCondition(child, contextType, comparison, $"{path}.{form}[{index++}]", problems, ref nodeCount);
            if (parsed is not null)
                children.Add(parsed);
        }

        if (children.Count != array.GetArrayLength())
            return null;

        return form == "all" ? new AllNode(children) : new AnyNode(children);
    }

    private static ConditionNode? ParseNot(
        JsonElement node, Type contextType, StringComparison comparison, string path,
        List<string> problems, ref int nodeCount)
    {
        var child = ParseCondition(node.GetProperty("not"), contextType, comparison, $"{path}.not", problems, ref nodeCount);
        return child is null ? null : new NotNode(child);
    }

    private static ConditionNode? ParseAlways(JsonElement node, string path, List<string> problems)
    {
        var value = node.GetProperty("always");
        if (value.ValueKind == JsonValueKind.True)
            return new AlwaysNode();

        problems.Add(value.ValueKind == JsonValueKind.False
            ? $"'{path}.always' is false, so the rule could never apply. Delete the rule instead."
            : $"'{path}.always' must be true.");
        return null;
    }

    private static ConditionNode? ParseComparison(
        JsonElement node, Type contextType, StringComparison comparison, string path, List<string> problems)
    {
        var fieldElement = node.GetProperty("field");
        if (fieldElement.ValueKind != JsonValueKind.String)
        {
            problems.Add($"'{path}.field' must be a string naming a context property.");
            return null;
        }

        if (!node.TryGetProperty("op", out var opElement) || opElement.ValueKind != JsonValueKind.String)
        {
            problems.Add($"'{path}' is missing 'op'. Valid operators: {string.Join(", ", Operators.Keys)}.");
            return null;
        }

        if (!Operators.TryGetValue(opElement.GetString()!, out var op))
        {
            problems.Add($"Unknown operator '{opElement.GetString()}' at '{path}'. " +
                         $"Valid operators: {string.Join(", ", Operators.Keys)}.");
            return null;
        }

        if (!ContextField.TryResolve(contextType, fieldElement.GetString()!, out var field, out var fieldError))
        {
            problems.Add(fieldError!);
            return null;
        }

        var hasValue = node.TryGetProperty("value", out var valueElement);
        return op switch
        {
            ComparisonOp.IsNull or ComparisonOp.NotNull =>
                ParseNullCheck(field!, op, path, problems),
            ComparisonOp.In or ComparisonOp.NotIn =>
                ParseInList(field!, op, hasValue ? valueElement : default, hasValue, comparison, path, problems),
            _ =>
                ParseValueComparison(field!, op, hasValue ? valueElement : default, hasValue, comparison, path, problems),
        };
    }

    private static ConditionNode? ParseNullCheck(ContextField field, ComparisonOp op, string path, List<string> problems)
    {
        // On a field that cannot hold null this is a constant — always false for isNull, always true
        // for notNull. Silent nonsense; reject it.
        if (!field.CanBeNull)
        {
            // For a reference type the field is only non-nullable because the context says so, and
            // that is a one-word fix the author can make — worth naming.
            var advice = field.Type.IsValueType
                ? string.Empty
                : $" If it really can be null, declare it '{ValueCoercion.Describe(field.Type)}?' on the context type.";

            problems.Add($"Field '{field.Path}' is declared {ValueCoercion.Describe(field.Type)} and can never be null, " +
                         $"so '{op.ToString().ToLowerInvariant()}' at '{path}' is always the same answer.{advice}");
            return null;
        }

        return new ComparisonNode(field, op, null, null, StringComparison.Ordinal);
    }

    private static ConditionNode? ParseInList(
        ContextField field, ComparisonOp op, JsonElement value, bool hasValue,
        StringComparison comparison, string path, List<string> problems)
    {
        if (!hasValue || value.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"'{path}.value' must be an array for operator '{op.ToString().ToLowerInvariant()}'.");
            return null;
        }

        if (value.GetArrayLength() == 0)
        {
            problems.Add($"'{path}.value' is an empty array, so the condition can never match.");
            return null;
        }

        var values = new List<object?>();
        var index = 0;
        var failed = false;
        foreach (var item in value.EnumerateArray())
        {
            if (ValueCoercion.TryCoerce(item, ElementType(field), $"{field.Path}[{index}]", out var coerced, out var error))
                values.Add(coerced);
            else
            {
                problems.Add($"{error} (at '{path}.value[{index}]')");
                failed = true;
            }

            index++;
        }

        return failed ? null : new ComparisonNode(field, op, null, values, comparison);
    }

    private static ConditionNode? ParseValueComparison(
        ContextField field, ComparisonOp op, JsonElement value, bool hasValue,
        StringComparison comparison, string path, List<string> problems)
    {
        if (!hasValue)
        {
            problems.Add($"'{path}' is missing 'value' for operator '{op.ToString().ToLowerInvariant()}'.");
            return null;
        }

        if (op is ComparisonOp.Gt or ComparisonOp.Gte or ComparisonOp.Lt or ComparisonOp.Lte
            && !ValueCoercion.IsOrderable(field.Type))
        {
            problems.Add($"Operator '{op.ToString().ToLowerInvariant()}' at '{path}' needs a numeric or date field, " +
                         $"but '{field.Path}' is {ValueCoercion.Describe(field.Type)}.");
            return null;
        }

        if (op is ComparisonOp.StartsWith or ComparisonOp.EndsWith && field.Type != typeof(string))
        {
            problems.Add($"Operator '{op.ToString().ToLowerInvariant()}' at '{path}' needs a string field, " +
                         $"but '{field.Path}' is {ValueCoercion.Describe(field.Type)}.");
            return null;
        }

        if (op == ComparisonOp.Contains && field.Type != typeof(string) && ElementType(field) == field.Type)
        {
            problems.Add($"Operator 'contains' at '{path}' needs a string or collection field, " +
                         $"but '{field.Path}' is {ValueCoercion.Describe(field.Type)}.");
            return null;
        }

        var targetType = op == ComparisonOp.Contains ? ElementType(field) : field.Type;
        if (!ValueCoercion.TryCoerce(value, targetType, field.Path, out var coerced, out var error))
        {
            problems.Add($"{error} (at '{path}.value')");
            return null;
        }

        return new ComparisonNode(field, op, coerced, null, comparison);
    }

    /// <summary>For collection fields, the element type; otherwise the field type itself.</summary>
    private static Type ElementType(ContextField field)
    {
        var type = field.Type;
        if (type == typeof(string))
            return type;

        if (type.IsArray)
            return type.GetElementType()!;

        var enumerable = type.GetInterfaces()
            .Concat(type.IsInterface ? [type] : [])
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerable?.GetGenericArguments()[0] ?? type;
    }
}
