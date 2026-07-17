using RuleCraft.Sample.Discounts;
using static RuleCraft.Sample.Api.RuleCraftProblems;

namespace RuleCraft.Sample.Api;

using Engine = RuleEngine<IDiscountRule, Order>;

/// <summary>
/// The rule lifecycle over HTTP: propose → review → approve. Nothing a user submits goes live
/// until someone calls approve, which is the whole point of the queue.
///
/// Only the LLM endpoint is asynchronous. Submitting and approving a rule compiles it, which is
/// CPU-bound work the engine runs on the calling thread — fine for a review console handling one
/// click at a time; a busy host would wrap those calls in <c>Task.Run</c> to keep request threads
/// free.
/// </summary>
internal static class RuleEndpoints
{
    public static void MapRuleEndpoints(this WebApplication app, bool hasApiKey)
    {
        // --- propose -------------------------------------------------------

        // Describe a rule in plain language; the LLM writes it. The one endpoint that does I/O.
        app.MapPost("/rules", (AddRuleRequest request, Engine engine) => Handle(async () =>
        {
            if (!hasApiKey)
                return Results.Problem(
                    "No Anthropic API key. Set ANTHROPIC_API_KEY, or run: " +
                    "dotnet user-secrets set \"Anthropic:ApiKey\" \"sk-ant-...\" — get one at " +
                    "https://console.anthropic.com (Settings → API Keys). Without a key you can " +
                    "still post rules to /rules/from-json or /rules/from-source.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            // JSON by default: sandboxed and reviewable by non-programmers.
            // C# is the escalation path for rules the DSL cannot express — and when the model
            // reports that a spec needs it, RuleNotExpressibleException says so in as many words.
            var info = string.Equals(request.Format, "csharp", StringComparison.OrdinalIgnoreCase)
                ? await engine.AddRuleAsync(request.Spec, request.Name)
                : await engine.AddJsonRuleAsync(request.Spec, request.Name);

            return Results.Created($"/rules/{info.Id}", info);
        }));

        // Submit a rule you wrote yourself — no LLM, no API key.
        app.MapPost("/rules/from-json", (AddRuleFromSourceRequest request, Engine engine) => Handle(() =>
        {
            var info = engine.AddJsonRuleFromSource(request.Source, request.Name, request.Spec);
            return Results.Created($"/rules/{info.Id}", info);
        }));

        app.MapPost("/rules/from-source", (AddRuleFromSourceRequest request, Engine engine) => Handle(() =>
        {
            var info = engine.AddRuleFromSource(request.Source, request.Name, request.Spec);
            return Results.Created($"/rules/{info.Id}", info);
        }));

        // --- review --------------------------------------------------------

        /// Every rule with its status and the position its predicate is consulted at.
        app.MapGet("/rules", (Engine engine) => Results.Ok(engine.GetRules()));

        /// Candidates awaiting a decision — carries the source, report and test results.
        app.MapGet("/rules/pending", (Engine engine) => Results.Ok(engine.GetPendingRules()));

        // --- decide --------------------------------------------------------

        // Approve is what loads the rule into this running process.
        app.MapPost("/rules/{id}/approve", (string id, ApproveRequest request, Engine engine) =>
            Handle(() => Results.Ok(engine.Approve(id, request.ApprovedBy))));

        // Rejected rules stay on disk for audit but never load.
        app.MapPost("/rules/{id}/reject", (string id, RejectRequest request, Engine engine) =>
            Handle(() => Results.Ok(engine.Reject(id, request.Reason))));

        // Unregisters a live rule; the next resolution no longer sees it. Reversible — see below.
        app.MapDelete("/rules/{id}", (string id, Engine engine) => Handle(() =>
        {
            engine.RemoveRule(id);
            return Results.NoContent();
        }));

        // The way back from DELETE, and from a quarantine once the contract that caused it is fixed.
        // Revalidates from scratch, so an enable is as safe as the original approval.
        app.MapPost("/rules/{id}/enable", (string id, ApproveRequest request, Engine engine) =>
            Handle(() => Results.Ok(engine.Enable(id, request.ApprovedBy))));
    }
}
