using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RuleCraft.Compilation;

internal sealed record RuleCompilationResult(
    bool Success,
    byte[]? AssemblyBytes,
    IReadOnlyList<string> ErrorDiagnostics,
    CSharpCompilation Compilation,
    SyntaxTree SyntaxTree);

internal static class RuleCompiler
{
    // Pinned so LLM output is judged against a stable language surface.
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);

    private static readonly CSharpCompilationOptions CompilationOptions = new(
        OutputKind.DynamicallyLinkedLibrary,
        optimizationLevel: OptimizationLevel.Release,
        deterministic: true,
        allowUnsafe: false,
        nullableContextOptions: NullableContextOptions.Enable);

    public static RuleCompilationResult Compile(string assemblyName, string source, IReadOnlyList<MetadataReference> references)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        var compilation = CSharpCompilation.Create(assemblyName, [tree], references, CompilationOptions);

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);

        var errors = emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();

        return new RuleCompilationResult(
            emit.Success,
            emit.Success ? stream.ToArray() : null,
            errors,
            compilation,
            tree);
    }
}
