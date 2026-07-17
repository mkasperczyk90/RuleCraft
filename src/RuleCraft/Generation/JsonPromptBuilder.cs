using System.Reflection;
using System.Text;

namespace RuleCraft.Generation;

internal static partial class PromptBuilder
{
    /// <summary>Prompts for the JSON-DSL rule kind.</summary>
    public static class Json
    {
        public const string SystemPrompt =
            """
            You are an expert business-rules author writing a rule for the RuleCraft engine.
            The rule is a JSON document that the engine interprets — it is NOT code, and it can
            only express what the grammar below allows. RuleCraft parses and type-checks it,
            runs the test cases you supply, then a human reviews it, so it must be exact.

            Output requirements:
            - Reply with EXACTLY ONE JSON document inside a single ```json fence. No prose.
            - Use only the properties, operators and field names shown below — anything else is rejected.

            If the rule cannot be expressed in this grammar (it needs arithmetic, aggregation,
            the current date, or logic beyond field comparisons), do NOT invent syntax and do NOT
            approximate the rule. Reply with a ```json fence containing only:
            {"error": "why the DSL cannot express this rule"}
            """;

        public static string BuildUserPrompt<TContext, TThen>(string spec, StringComparison stringComparison)
        {
            var contextType = typeof(TContext);
            var builder = new StringBuilder();

            builder.AppendLine("Write one JSON rule document with this shape:");
            builder.AppendLine();
            builder.AppendLine("```json");
            builder.AppendLine("""
                {
                  "name": "short-kebab-case-name",
                  "priority": 0,
                  "when": <condition>,
                  "then": <outcome>,
                  "tests": [
                    { "name": "…", "context": { …a context object… }, "applies": true },
                    { "name": "…", "context": { …a context object… }, "applies": false }
                  ]
                }
                """);
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine("`priority`: higher wins when several rules match one context (default 0; raise it only if the spec implies precedence).");
            builder.AppendLine();
            builder.AppendLine("A <condition> is exactly one of:");
            builder.AppendLine("""
                  { "all": [<condition>, …] }                       all must hold
                  { "any": [<condition>, …] }                       at least one must hold
                  { "not": <condition> }
                  { "always": true }                                the rule always applies
                  { "field": "<field>", "op": "<op>", "value": … }
                """);
            builder.AppendLine();
            builder.AppendLine("Operators:");
            builder.AppendLine("""
                  eq, neq                    any field
                  gt, gte, lt, lte           numeric and date fields ONLY
                  in, notIn                  "value" must be an array
                  contains                   string field (substring) or collection field (membership)
                  startsWith, endsWith       string fields only
                  isNull, notNull            nullable fields only; omit "value"
                """);
            builder.AppendLine();
            builder.AppendLine(stringComparison is StringComparison.OrdinalIgnoreCase or StringComparison.InvariantCultureIgnoreCase or StringComparison.CurrentCultureIgnoreCase
                ? "String comparisons are CASE-INSENSITIVE."
                : "String comparisons are CASE-SENSITIVE — match the exact casing of the data.");
            builder.AppendLine("There is no current date/time and no arithmetic. Nested paths are allowed (e.g. \"Customer.Country\", \"PlacedAt.DayOfWeek\", \"Items.Count\").");
            builder.AppendLine();
            builder.AppendLine($"Fields available on the context ({contextType.FullName}) — use these names exactly:");
            builder.AppendLine("```");
            builder.AppendLine(RenderFields(contextType).TrimEnd());
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine("Test cases: `context` is a JSON object of the same context type. Supply at least one");
            builder.AppendLine("case where the rule applies and one where it must NOT apply (both are required unless");
            builder.AppendLine("the condition is `always`). They are the only thing that catches an inverted condition.");
            builder.AppendLine();
            builder.AppendLine($"The `then` outcome must match this shape ({typeof(TThen).FullName}):");
            builder.AppendLine("```json");
            builder.AppendLine(RenderThenSkeleton(typeof(TThen)).TrimEnd());
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine("Rule specification (natural language, implement it faithfully):");
            builder.AppendLine("\"\"\"");
            builder.AppendLine(spec.Trim());
            builder.AppendLine("\"\"\"");

            return builder.ToString();
        }

        /// <summary>Flat list of usable field paths with their types — what an author may name.</summary>
        private static string RenderFields(Type contextType)
        {
            var builder = new StringBuilder();
            Render(contextType, prefix: string.Empty, depth: 0, new HashSet<Type>());
            return builder.ToString();

            void Render(Type type, string prefix, int depth, HashSet<Type> visited)
            {
                if (depth > 2 || !visited.Add(type))
                    return;

                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                             .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead))
                {
                    var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                    var propertyType = property.PropertyType;
                    builder.AppendLine($"{path}: {TypeShapeRenderer.RenderTypeName(propertyType)}{EnumValues(propertyType)}");

                    var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                    if (IsExpandable(underlying))
                        Render(underlying, path, depth + 1, visited);
                }

                visited.Remove(type);
            }
        }

        private static string EnumValues(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            return underlying.IsEnum ? $"   (one of: {string.Join(", ", Enum.GetNames(underlying))})" : string.Empty;
        }

        private static bool IsExpandable(Type type) =>
            !type.IsPrimitive
            && !type.IsEnum
            && type != typeof(string)
            && type != typeof(decimal)
            && type != typeof(DateTime)
            && type != typeof(DateTimeOffset)
            && type != typeof(DateOnly)
            && type != typeof(TimeSpan)
            && type != typeof(Guid)
            && type.Namespace?.StartsWith("System", StringComparison.Ordinal) != true;

        /// <summary>
        /// Renders the `then` DTO as a JSON skeleton. Generated from the type the factory actually
        /// deserializes, so — unlike a hand-written schema string — it cannot drift out of sync.
        /// </summary>
        private static string RenderThenSkeleton(Type thenType)
        {
            var properties = thenType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                .ToArray();

            if (properties.Length == 0)
                return "{}";

            var builder = new StringBuilder("{\n");
            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                var comma = i == properties.Length - 1 ? string.Empty : ",";
                builder.AppendLine(
                    $"  \"{JsonName(property.Name)}\": <{TypeShapeRenderer.RenderTypeName(property.PropertyType)}>{comma}{EnumValues(property.PropertyType)}");
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static string JsonName(string name) => char.ToLowerInvariant(name[0]) + name[1..];
    }
}
