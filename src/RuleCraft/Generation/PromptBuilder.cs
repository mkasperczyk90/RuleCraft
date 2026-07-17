using System.Text;

namespace RuleCraft.Generation;

internal static partial class PromptBuilder
{
    /// <summary>Prompts for the compiled-C# rule kind.</summary>
    public static class CSharp
    {
        public const string SystemPrompt =
            """
            You are an expert C# engineer generating a business rule for the RuleCraft engine.
            RuleCraft compiles your code with Roslyn, runs a security analyzer and executes your
            tests before a human reviews the code, so it must be complete, correct and minimal.

            Output requirements:
            - Reply with EXACTLY ONE complete C# file inside a single ```csharp code fence. No prose.
            - The file must compile standalone against the references provided by the engine.

            Hard constraints (violations are rejected automatically):
            - Allowed namespaces: System, System.Collections.Generic, System.Linq, System.Text,
              System.Globalization, System.Text.RegularExpressions, RuleCraft, and the namespaces
              of the contract/context types shown below.
            - FORBIDDEN: file/network/process access, reflection, Console, Environment, Activator,
              GC, dynamic, unsafe code, preprocessor directives, and re-declaring any type shown
              below (reference them; never copy their definitions into your file).
            - FORBIDDEN: starting or blocking on work of your own — Task.Run, Task.Factory,
              Task.Delay, Task.Wait, ContinueWith, Parallel, Thread. `Task` itself is available
              where the contract's own signatures need it (e.g. `Task.FromResult`).
            - Every class you declare needs a public parameterless constructor.
            - The predicate method AppliesTo must be cheap and side-effect-free.
            """;

        public static string BuildUserPrompt<TContract, TContext>(string ruleId, string spec)
            where TContract : class
        {
            var contractType = typeof(TContract);
            var contextType = typeof(TContext);
            var builder = new StringBuilder();

            builder.AppendLine($"Generate one C# file with namespace `RuleCraft.Generated.Rule_{ruleId}` containing:");
            builder.AppendLine();
            builder.AppendLine($"1. Exactly one public class implementing `RuleCraft.IRule<{contractType.Name}, {contextType.Name}>`:");
            builder.AppendLine($"   - `bool AppliesTo({contextType.Name} context)` — predicate: does this rule apply?");
            builder.AppendLine($"   - `{contractType.Name} Implementation {{ get; }}` — the contract implementation used when it does");
            builder.AppendLine("   - `int Priority { get; }` — higher wins when several rules match (default 0; raise it only if the spec implies precedence)");
            builder.AppendLine($"   The same class may implement both the rule and `{contractType.Name}` (then `Implementation => this;`).");
            builder.AppendLine();
            builder.AppendLine("2. At least two public test classes implementing `RuleCraft.IRuleTest`");
            builder.AppendLine("   (`string Name { get; }` and `TestResult Run(TestContext context)`).");
            builder.AppendLine("   Cover the positive case, the negative case and edge cases of the spec.");
            builder.AppendLine("   Use `RuleCraft.RuleAssert` (True/False/Equal/NotNull) and finish with `return TestResult.Passed();`.");
            builder.AppendLine();
            builder.AppendLine($"Contract to implement ({contractType.FullName}):");
            builder.AppendLine("```");
            builder.AppendLine(TypeShapeRenderer.RenderClosure(contractType).TrimEnd());
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine($"Context passed to AppliesTo ({contextType.FullName}):");
            builder.AppendLine("```");
            builder.AppendLine(TypeShapeRenderer.RenderClosure(contextType).TrimEnd());
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine("Rule specification (natural language, implement it faithfully):");
            builder.AppendLine("\"\"\"");
            builder.AppendLine(spec.Trim());
            builder.AppendLine("\"\"\"");

            return builder.ToString();
        }
    }

    /// <summary>Shared by every kind: the validation report is the feedback, whatever produced it.</summary>
    public static string BuildFeedback(ValidationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("The document was rejected. Fix ALL problems below and return the corrected COMPLETE document in one code fence.");

        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Errors:");
            foreach (var diagnostic in report.Diagnostics.Take(20))
                builder.AppendLine("- " + diagnostic);
        }

        if (report.SecurityFindings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Security policy violations:");
            foreach (var finding in report.SecurityFindings.Take(20))
                builder.AppendLine($"- line {finding.Line}: {finding.Message}");
        }

        var failedTests = report.TestResults.Where(t => t.Outcome == TestOutcome.Failed).ToList();
        if (failedTests.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Failed tests:");
            foreach (var test in failedTests.Take(20))
                builder.AppendLine($"- {test.Name}: {test.Message}");
        }

        return builder.ToString();
    }
}
