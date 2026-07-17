using System.Reflection;
using Microsoft.CodeAnalysis;
using RuleCraft.Compilation;
using RuleCraft.Loading;
using RuleCraft.Security;
using RuleCraft.Testing;

namespace RuleCraft.Engine;

/// <summary>Rules written in C#: Roslyn compilation → security analysis → generated + acceptance tests.</summary>
internal sealed class CSharpRuleKind<TContract, TContext> : IRuleKind<TContract, TContext> where TContract : class
{
    private static readonly string[] ReservedTypeNames =
    [
        typeof(TContract).Name,
        typeof(TContext).Name,
        nameof(IRuleTest),
        "IRule",
    ];

    private readonly RuleEngineOptions _options;
    private readonly Func<IReadOnlyList<IRuleAcceptanceTest<TContract>>> _acceptanceTests;

    /// <summary>
    /// Built once per engine, not once per candidate: reading assembly metadata off disk is not
    /// free, and the set cannot change — the engine froze its options, so
    /// <see cref="RuleEngineOptions.AdditionalReferenceAssemblies"/> is fixed. Lazy, so an engine
    /// that only ever sees JSON rules never touches Roslyn at all.
    /// </summary>
    private readonly Lazy<IReadOnlyList<MetadataReference>> _references;

    public CSharpRuleKind(RuleEngineOptions options, Func<IReadOnlyList<IRuleAcceptanceTest<TContract>>> acceptanceTests)
    {
        _options = options;
        _acceptanceTests = acceptanceTests;
        _references = new Lazy<IReadOnlyList<MetadataReference>>(() => ReferenceSetProvider.Build(HostAssemblies()));
    }

    public RuleOrigin Origin => RuleOrigin.Compiled;

    public PipelineOutcome<TContract, TContext> Validate(string ruleId, string source)
    {
        var compiled = RuleCompiler.Compile($"RuleCraft.Generated.{ruleId}", source, _references.Value);

        if (!compiled.Success)
            return PipelineOutcome<TContract, TContext>.Failed(new ValidationReport(compiled.ErrorDiagnostics, [], []));

        var findings = SecurityAnalyzer.Analyze(
            compiled.Compilation, compiled.SyntaxTree, _options.SecurityPolicy, ReservedTypeNames);

        if (findings.Count > 0)
            return PipelineOutcome<TContract, TContext>.Failed(new ValidationReport([], findings, []));

        var (testResults, priority) = RuleTestHarness.Run<TContract, TContext>(
            compiled.AssemblyBytes!, ruleId, _acceptanceTests(), _options.TestTimeout);

        var report = new ValidationReport([], [], testResults);
        if (!report.Success)
            return PipelineOutcome<TContract, TContext>.Failed(report);

        var assemblyBytes = compiled.AssemblyBytes!;
        return new PipelineOutcome<TContract, TContext>(report, priority, () =>
        {
            var (rule, context) = RuleLoader.Load<TContract, TContext>(assemblyBytes, ruleId);
            return new LoadedRule<TContract, TContext>(rule, context);
        });
    }

    private IEnumerable<Assembly> HostAssemblies()
    {
        yield return typeof(IRule<,>).Assembly;
        yield return typeof(TContract).Assembly;
        yield return typeof(TContext).Assembly;
        foreach (var assembly in _options.AdditionalReferenceAssemblies)
            yield return assembly;
    }
}
