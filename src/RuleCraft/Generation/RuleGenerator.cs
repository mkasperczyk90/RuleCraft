using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RuleCraft.Engine;

namespace RuleCraft.Generation;

/// <summary>
/// The prompts for one rule kind: a system prompt, a spec → user-prompt builder, and — for kinds
/// whose prompt offers the model a way to say "this grammar cannot express that" — a reader that
/// recognizes such an answer. <see cref="TryReadRefusal"/> returns the model's reason, or null when
/// the document is an ordinary rule attempt.
/// </summary>
internal sealed record RulePrompts(
    string System,
    Func<string, string> BuildUser,
    Func<string, string?>? TryReadRefusal = null);

/// <summary>
/// Drives the LLM codegen loop: prompt → extract the fenced document → validate (compile/parse,
/// security, tests) → on failure feed the full report back to the model and retry.
/// Kind-agnostic: it neither knows nor cares whether the document is C# or JSON.
/// </summary>
internal sealed class RuleGenerator(IChatClient chatClient, string? modelId, int maxAttempts, ILogger logger)
{
    private readonly int _maxAttempts = Math.Max(1, maxAttempts);

    public async Task<(string Source, PipelineOutcome<TContract, TContext> Outcome)> GenerateAsync<TContract, TContext>(
        string ruleId,
        string spec,
        RulePrompts prompts,
        Func<string, PipelineOutcome<TContract, TContext>> validate,
        CancellationToken cancellationToken)
        where TContract : class
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, prompts.System),
            new(ChatRole.User, prompts.BuildUser(spec)),
        };

        var chatOptions = new ChatOptions { ModelId = modelId };
        var attempts = new List<GenerationAttempt>();

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken)
                .ConfigureAwait(false);
            var source = CodeExtractor.Extract(response.Text);

            // A refusal is a final answer, not a failed attempt: the grammar is missing something the
            // spec needs, and no amount of retrying will grow it. Surface it instead of retrying.
            if (prompts.TryReadRefusal?.Invoke(source) is { } refusal)
            {
                logger.LogInformation(
                    "Rule {RuleId} cannot be expressed in this rule kind's grammar: {Reason}", ruleId, refusal);
                throw new RuleNotExpressibleException(spec, refusal);
            }

            var outcome = validate(source);
            attempts.Add(new GenerationAttempt(source, outcome.Report));

            if (outcome.Success)
            {
                logger.LogInformation("Rule {RuleId} generated successfully on attempt {Attempt}.", ruleId, attempt);
                return (source, outcome);
            }

            logger.LogWarning(
                "Rule {RuleId} attempt {Attempt}/{Max} rejected: {Errors} error(s), {Findings} security finding(s), {Failed} failed test(s).",
                ruleId, attempt, _maxAttempts,
                outcome.Report.Diagnostics.Count,
                outcome.Report.SecurityFindings.Count,
                outcome.Report.TestResults.Count(t => t.Outcome == TestOutcome.Failed));

            messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
            messages.Add(new ChatMessage(ChatRole.User, PromptBuilder.BuildFeedback(outcome.Report)));
        }

        throw new RuleGenerationException(
            $"The LLM did not produce a valid rule within {_maxAttempts} attempt(s).", attempts);
    }
}
