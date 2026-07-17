using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RuleCraft.Security;

/// <summary>
/// Walks the bound semantic model of a candidate compilation and reports banned API usage.
/// Symbol-based (not textual), so aliasing (<c>using F = System.IO.File;</c>) and fully
/// qualified spellings are caught the same way.
/// </summary>
internal static class SecurityAnalyzer
{
    public static IReadOnlyList<SecurityFinding> Analyze(
        CSharpCompilation compilation,
        SyntaxTree tree,
        SecurityPolicy policy,
        IReadOnlyCollection<string> reservedTypeNames)
    {
        var findings = new List<SecurityFinding>();
        var seen = new HashSet<(int Line, string Message)>();
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        void Report(SyntaxNode node, string message)
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (seen.Add((line, message)))
                findings.Add(new SecurityFinding(message, line));
        }

        if (root.ContainsDirectives)
        {
            foreach (var directive in root.DescendantTrivia().Where(t => t.IsDirective))
            {
                var line = directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (seen.Add((line, "Preprocessor directives are not allowed.")))
                    findings.Add(new SecurityFinding("Preprocessor directives are not allowed.", line));
            }
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case TypeDeclarationSyntax typeDecl:
                    if (reservedTypeNames.Contains(typeDecl.Identifier.Text))
                        Report(typeDecl,
                            $"Type '{typeDecl.Identifier.Text}' redeclares a host contract/context type. " +
                            "Reference the existing type instead (redeclaring it breaks type identity across load contexts).");
                    if (typeDecl.Modifiers.Any(SyntaxKind.UnsafeKeyword))
                        Report(typeDecl, "Unsafe code is not allowed.");
                    break;

                case UnsafeStatementSyntax:
                    Report(node, "Unsafe code is not allowed.");
                    break;

                case PointerTypeSyntax or FunctionPointerTypeSyntax:
                    Report(node, "Pointer types are not allowed.");
                    break;

                case StackAllocArrayCreationExpressionSyntax:
                    Report(node, "stackalloc is not allowed.");
                    break;

                case MethodDeclarationSyntax method when method.Modifiers.Any(SyntaxKind.ExternKeyword):
                    Report(node, "extern methods are not allowed.");
                    break;

                case IdentifierNameSyntax identifier when identifier.Identifier.Text == "dynamic"
                    && model.GetTypeInfo(identifier).Type?.TypeKind == TypeKind.Dynamic:
                    Report(node, "dynamic is not allowed.");
                    break;
            }

            if (node is SimpleNameSyntax simpleName)
            {
                var symbol = model.GetSymbolInfo(simpleName).Symbol;
                if (symbol is not null)
                    CheckSymbol(simpleName, symbol, policy, Report);
            }
        }

        return findings;
    }

    /// <summary>
    /// The most specific rule wins: member, then type, then namespace. That ordering is what lets a
    /// policy hand out a namespace and still refuse things inside it — the default one must allow
    /// System.Threading.Tasks, because an async contract cannot be implemented without naming Task,
    /// while still refusing Task.Run and Parallel.
    /// </summary>
    private static void CheckSymbol(
        SyntaxNode node, ISymbol symbol, SecurityPolicy policy, Action<SyntaxNode, string> report)
    {
        if (symbol is INamespaceSymbol ns)
        {
            var name = ns.ToDisplayString();
            if (!IsAllowedNamespace(name, policy)
                && !IsPathToAllowedNamespace(name, policy)
                && IsBannedNamespace(name, policy))
                report(node, $"Namespace '{name}' is banned by the security policy.");
            return;
        }

        var containingType = symbol as ITypeSymbol ?? symbol.ContainingType;
        if (containingType is null)
            return;

        var typeName = containingType.OriginalDefinition.ToDisplayString();
        var namespaceName = containingType.ContainingNamespace?.ToDisplayString() ?? string.Empty;

        if (symbol is not ITypeSymbol && policy.BannedMembers.Contains($"{typeName}.{symbol.Name}"))
        {
            report(node, $"Use of '{typeName}.{symbol.Name}' is banned by the security policy.");
            return;
        }

        if (policy.AllowedTypes.Contains(typeName))
            return;

        // Before the namespace allow-list, not after: banning a type is the more specific statement.
        if (policy.BannedTypes.Contains(typeName))
        {
            report(node, $"Use of '{typeName}' is banned by the security policy.");
            return;
        }

        if (IsAllowedNamespace(namespaceName, policy))
            return;

        if (IsBannedNamespace(namespaceName, policy))
            report(node, $"Use of '{typeName}' (namespace '{namespaceName}') is banned by the security policy.");
    }

    private static bool IsAllowedNamespace(string ns, SecurityPolicy policy) =>
        policy.AllowedNamespaces.Any(prefix => Matches(ns, prefix));

    /// <summary>
    /// True when this namespace is merely a segment on the way to an allowed one — <c>System.Threading</c>
    /// is banned, yet you cannot write <c>System.Threading.Tasks.Task</c> without naming it.
    ///
    /// Banning a namespace means "no types from here", not "never utter these words": every type is
    /// checked on its own below, so a path segment carries no risk. Without this, allowing a
    /// namespace nested under a banned one is silently impossible.
    /// </summary>
    private static bool IsPathToAllowedNamespace(string ns, SecurityPolicy policy) =>
        policy.AllowedNamespaces.Any(allowed => allowed.StartsWith(ns + ".", StringComparison.Ordinal));

    private static bool IsBannedNamespace(string ns, SecurityPolicy policy) =>
        policy.BannedNamespaces.Any(prefix => Matches(ns, prefix));

    private static bool Matches(string ns, string prefix) =>
        ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal);
}
