using System.Text.Json.Serialization;
using Anthropic;
using Microsoft.Extensions.AI;
using RuleCraft;
using RuleCraft.Sample.Api;
using RuleCraft.Sample.Discounts;

// RuleCraft sample: discount rules written in JSON, in C#, or by hand — reviewed and approved
// through HTTP endpoints, then loaded into this running process. Review console: wwwroot/index.html.
//
//   Discounts/  what a rule is in this app (the contract, the context, the `then` vocabulary)
//   Api/        the HTTP surface the console drives
var builder = WebApplication.CreateBuilder(args);
var loggerFactory = LoggerFactory.Create(logging => logging.AddConsole());

// ---------------------------------------------------------------- the LLM (optional)
// The library never picks a vendor — it takes any IChatClient. Get a key at
// https://console.anthropic.com (Settings → API Keys), then either:
//   dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."   (stays out of the repo)
//   set ANTHROPIC_API_KEY=sk-ant-...
// Without a key the app still runs: rules can be posted as JSON or C# source.
var apiKey = builder.Configuration["Anthropic:ApiKey"]
             ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
var hasApiKey = !string.IsNullOrWhiteSpace(apiKey);

// ---------------------------------------------------------------- the engine
var modelId = builder.Configuration["RuleCraft:Model"] ?? "claude-opus-4-8";

if (hasApiKey)
    builder.Services.AddSingleton<IChatClient>(new AnthropicClient { ApiKey = apiKey }.AsIChatClient(modelId));

// AddRuleCraft takes the IChatClient and ILoggerFactory from the container. The second callback is
// the startup sequence, run once when the engine is first resolved — which is where the ordering
// rule lives: stored JSON rules can only load once EnableJsonRules has run.
builder.Services.AddRuleCraft<IDiscountRule, Order>(
    options =>
    {
        options.StorePath = Path.Combine(builder.Environment.ContentRootPath, "rules");
        options.ModelId = modelId;
    },
    engine =>
    {
        engine.SetFallback(new NoDiscount());                           // when no rule matches
        engine.AddAcceptanceTest(new DiscountRangeInvariant());         // enforced on every candidate
        engine.EnableJsonRules<DiscountAction>(DiscountActions.Build);  // the `then` vocabulary for JSON rules
        engine.AddStaticRule(new BulkOrderRule());                      // a rule written in this repo
        engine.ReloadFromStore();                                       // recompiles every stored rule
    });

builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---------------------------------------------------------------- the HTTP surface
var app = builder.Build();

// Resolving it here rather than on the first request: the reload above recompiles every stored rule,
// and that cost belongs at boot, where it is visible.
var engine = app.Services.GetRequiredService<RuleEngine<IDiscountRule, Order>>();
SeedRules.EnsureSeeded(engine, loggerFactory.CreateLogger(nameof(SeedRules)));

app.UseDefaultFiles();   // serves the review console at /
app.UseStaticFiles();

app.MapGet("/status", () => Results.Ok(new { llmConfigured = hasApiKey }));
app.MapRuleEndpoints(hasApiKey);
app.MapOrderEndpoints();

app.Run();
