using System.Reflection;
using System.Text;

namespace RuleCraft.Generation;

/// <summary>
/// Renders the public shape of the contract and context types as pseudo-C# so the LLM
/// writes against real, compile-checked members instead of guessing property names.
/// </summary>
internal static class TypeShapeRenderer
{
    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void",
        [typeof(object)] = "object",
        [typeof(string)] = "string",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(char)] = "char",
    };

    public static string RenderTypeName(Type type)
    {
        if (Keywords.TryGetValue(type, out var keyword))
            return keyword;

        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return RenderTypeName(underlying) + "?";

        if (type.IsArray)
            return RenderTypeName(type.GetElementType()!) + "[]";

        if (type.IsGenericType)
        {
            var name = type.Name[..type.Name.IndexOf('`')];
            var args = string.Join(", ", type.GetGenericArguments().Select(RenderTypeName));
            return $"{name}<{args}>";
        }

        return type.Name;
    }

    /// <summary>Renders <paramref name="root"/> and, recursively, user-defined types reachable from its public surface.</summary>
    public static string RenderClosure(Type root, int maxDepth = 3)
    {
        var builder = new StringBuilder();
        var visited = new HashSet<Type>();
        Render(root, 0);
        return builder.ToString();

        void Render(Type type, int depth)
        {
            if (depth > maxDepth || !visited.Add(type))
                return;

            builder.AppendLine(RenderSingle(type));

            foreach (var referenced in ReferencedUserTypes(type))
                Render(referenced, depth + 1);
        }
    }

    private static string RenderSingle(Type type)
    {
        var builder = new StringBuilder();

        if (type.IsEnum)
        {
            builder.AppendLine($"public enum {type.Name}");
            builder.AppendLine("{");
            foreach (var name in Enum.GetNames(type))
                builder.AppendLine($"    {name},");
            builder.AppendLine("}");
            return builder.ToString();
        }

        var kind = type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";
        builder.AppendLine($"public {kind} {type.Name}   // namespace {type.Namespace}");
        builder.AppendLine("{");

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var setter = property.CanWrite ? " set;" : string.Empty;
            builder.AppendLine($"    {RenderTypeName(property.PropertyType)} {property.Name} {{ get;{setter} }}");
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName)
                continue;
            var parameters = string.Join(", ",
                method.GetParameters().Select(p => $"{RenderTypeName(p.ParameterType)} {p.Name}"));
            builder.AppendLine($"    {RenderTypeName(method.ReturnType)} {method.Name}({parameters});");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static IEnumerable<Type> ReferencedUserTypes(Type type)
    {
        var candidates = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType)
            .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));

        foreach (var candidate in candidates.SelectMany(Unwrap))
        {
            if (IsUserType(candidate))
                yield return candidate;
        }
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.IsArray)
        {
            foreach (var inner in Unwrap(type.GetElementType()!))
                yield return inner;
            yield break;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            foreach (var inner in Unwrap(argument))
                yield return inner;
            yield break;
        }

        yield return type;
    }

    private static bool IsUserType(Type type) =>
        !type.IsPrimitive
        && type != typeof(string)
        && type != typeof(decimal)
        && type.Namespace is { } ns
        && !ns.StartsWith("System", StringComparison.Ordinal)
        && !ns.StartsWith("Microsoft", StringComparison.Ordinal)
        && ns != "RuleCraft";
}
