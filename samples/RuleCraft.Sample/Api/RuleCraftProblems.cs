using RuleCraft;

namespace RuleCraft.Sample.Api;

/// <summary>
/// Turns RuleCraft's exceptions into HTTP responses, so a rejected rule tells the reviewer what
/// was wrong with it instead of returning a bare 500. The validation report is the useful part —
/// it carries the compiler/parser errors, security findings and test results.
///
/// Two entry points over one mapping table: the generation endpoints are genuinely asynchronous
/// (they call the LLM), the rest of the engine's API is synchronous.
/// </summary>
internal static class RuleCraftProblems
{
    public static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (Map(exception) is { } problem)
        {
            return problem;
        }
    }

    public static IResult Handle(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (Map(exception) is { } problem)
        {
            return problem;
        }
    }

    /// <summary>Null for anything RuleCraft did not raise — those still surface as a 500.</summary>
    private static IResult? Map(Exception exception) => exception switch
    {
        RuleNotFoundException ex =>
            Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound),

        // e.g. approving an already-rejected rule, or a JSON rule with JSON support disabled.
        RuleStateException ex =>
            Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict),

        RuleValidationException ex =>
            Results.Json(
                new { error = ex.Message, report = ex.Report },
                statusCode: StatusCodes.Status422UnprocessableEntity),

        // The DSL cannot express this spec and the model said so instead of approximating it.
        // Not a failure: the answer is "ask for it in C#", and the console offers exactly that.
        RuleNotExpressibleException ex =>
            Results.Json(
                new { error = ex.Message, reason = ex.Reason, escalateTo = "csharp" },
                statusCode: StatusCodes.Status422UnprocessableEntity),

        // The LLM never converged: return every attempt so the spec can be fixed.
        RuleGenerationException ex =>
            Results.Json(
                new { error = ex.Message, attempts = ex.Attempts.Select(a => new { a.Report, a.Source }) },
                statusCode: StatusCodes.Status422UnprocessableEntity),

        _ => null,
    };
}
