using System.Text.Json;
using System.Text.Json.Serialization;
using RuleCraft.Engine;
using RuleCraft.Testing;

namespace RuleCraft.Json;

/// <summary>
/// Rules expressed as a JSON-DSL document: parse + type-check → build the outcome with the host's
/// factory → run the document's own test cases → run the developer's acceptance tests.
///
/// No Roslyn, no assembly, no load context — the document cannot express anything the factory does
/// not understand, so <see cref="ValidationReport.SecurityFindings"/> is genuinely always empty
/// rather than merely unchecked.
/// </summary>
internal sealed class JsonRuleKind<TContract, TContext, TThen> : IRuleKind<TContract, TContext>
    where TContract : class
{
    private static readonly JsonSerializerOptions ThenOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // Turns a typo in `then` into a named error instead of a silently defaulted field.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // Enums are written by name in rule documents, exactly as conditions compare them.
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions ContextOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Func<TThen, TContract> _build;
    private readonly StringComparison _stringComparison;
    private readonly RuleEngineOptions _options;
    private readonly Func<IReadOnlyList<IRuleAcceptanceTest<TContract>>> _acceptanceTests;

    public JsonRuleKind(
        Func<TThen, TContract> build,
        StringComparison stringComparison,
        RuleEngineOptions options,
        Func<IReadOnlyList<IRuleAcceptanceTest<TContract>>> acceptanceTests)
    {
        _build = build;
        _stringComparison = stringComparison;
        _options = options;
        _acceptanceTests = acceptanceTests;
    }

    public RuleOrigin Origin => RuleOrigin.Json;

    public PipelineOutcome<TContract, TContext> Validate(string ruleId, string source)
    {
        if (!JsonRuleParser.TryParse(source, typeof(TContext), _stringComparison, out var definition, out var errors))
            return Fail([.. errors]);

        if (!TryBuildImplementation(definition!.Then, out var implementation, out var buildErrors))
            return Fail(buildErrors);

        var results = new List<TestCaseResult>();
        results.AddRange(RunDocumentTests(definition));
        results.AddRange(RunAcceptanceTests(implementation!));

        var report = new ValidationReport([], [], results);
        if (!report.Success)
            return PipelineOutcome<TContract, TContext>.Failed(report);

        return new PipelineOutcome<TContract, TContext>(
            report,
            definition.Priority,
            () => new LoadedRule<TContract, TContext>(
                new JsonRule<TContract, TContext>(definition.When, implementation!, definition.Priority),
                Context: null),
            SuggestedName: definition.Name);
    }

    private bool TryBuildImplementation(JsonElement then, out TContract? implementation, out IReadOnlyList<string> errors)
    {
        implementation = null;

        TThen? action;
        try
        {
            action = JsonSerializer.Deserialize<TThen>(then.GetRawText(), ThenOptions);
        }
        catch (JsonException ex)
        {
            errors = [$"Invalid 'then': {ex.Message}"];
            return false;
        }

        if (action is null)
        {
            errors = ["'then' must describe the rule's outcome; it deserialized to null."];
            return false;
        }

        try
        {
            implementation = _build(action);
        }
        catch (Exception ex)
        {
            errors = [$"The application rejected this 'then': {TestExecution.Unwrap(ex).Message}"];
            return false;
        }

        if (implementation is null)
        {
            errors = ["The application's JSON rule factory returned null."];
            return false;
        }

        errors = [];
        return true;
    }

    /// <summary>
    /// Runs the document's own test cases. These are plain data evaluated against an interpreted
    /// predicate — bounded work with no way to loop — so they run inline, without the thread-pool
    /// churn of a timeout per case.
    /// </summary>
    private List<TestCaseResult> RunDocumentTests(JsonRuleDefinition definition)
    {
        var results = new List<TestCaseResult>();
        var index = 0;

        foreach (var test in definition.Tests)
        {
            var name = string.IsNullOrWhiteSpace(test.Name) ? $"case[{index}]" : test.Name!;
            index++;
            results.Add(RunDocumentTest(definition, test, name));
        }

        return results;
    }

    private TestCaseResult RunDocumentTest(JsonRuleDefinition definition, JsonRuleTestCase test, string name)
    {
        TContext? context;
        try
        {
            context = JsonSerializer.Deserialize<TContext>(test.Context.GetRawText(), ContextOptions);
        }
        catch (JsonException ex)
        {
            return new TestCaseResult(name, TestOutcome.Failed, $"Invalid test context: {ex.Message}");
        }

        if (context is null)
            return new TestCaseResult(name, TestOutcome.Failed, "Test context deserialized to null.");

        bool applies;
        try
        {
            applies = definition.When.Evaluate(context);
        }
        catch (Exception ex)
        {
            return new TestCaseResult(name, TestOutcome.Failed,
                $"Evaluating the condition threw: {TestExecution.Unwrap(ex).Message}");
        }

        return applies == test.Applies
            ? new TestCaseResult(name, TestOutcome.Passed, null)
            : new TestCaseResult(name, TestOutcome.Failed,
                $"Expected the rule to {(test.Applies ? "apply" : "not apply")} to this context, but it did {(applies ? "" : "not ")}apply.");
    }

    /// <summary>Developer invariants are arbitrary code, so they keep the timeout.</summary>
    private IEnumerable<TestCaseResult> RunAcceptanceTests(TContract implementation) =>
        _acceptanceTests().Select(test =>
            TestExecution.RunWithTimeout($"acceptance:{test.Name}", _options.TestTimeout, _ => test.Run(implementation)));

    private static PipelineOutcome<TContract, TContext> Fail(IReadOnlyList<string> errors) =>
        PipelineOutcome<TContract, TContext>.Failed(new ValidationReport(errors, [], []));
}
