# RuleCraft sample — discounts

An ASP.NET Core minimal API demonstrating the full RuleCraft loop, with a small review console at
`http://localhost:5199/` (`wwwroot/index.html`): write a spec (as a JSON rule or as C#), watch the
candidate appear in the approval queue with its document, report and test results, approve it, and
see the discount change on the next order — without restarting the process.

For what the library itself does and why, see the [main README](../../README.md).

```bash
dotnet run                          # then open http://localhost:5199/
```

**It works on first run with no key.** `SeedRules` adds one rule of each kind at startup — a JSON
rule, a compiled C# rule, and the static `BulkOrderRule` — so `GET /rules` shows all three
competing by priority before you type anything. Seeding is skipped once a rule exists, so
rejecting or unloading one in the console sticks.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `POST /rules` `{spec, name?, format?}` | generate a rule from natural language — `format` is `json` (default) or `csharp`; needs `ANTHROPIC_API_KEY` |
| `POST /rules/from-json` `{source, name?}` | submit a hand-written JSON rule document (no LLM needed) |
| `POST /rules/from-source` `{source, name?}` | submit hand-written C# rule source (no LLM needed) |
| `GET /rules` | all rules — JSON, compiled and static — with status and evaluation order |
| `GET /rules/pending` | review queue: document/source, report, test results |
| `POST /rules/{id}/approve` `{approvedBy}` | load the rule into the running app |
| `POST /rules/{id}/reject` `{reason}` | reject; kept on disk for audit |
| `DELETE /rules/{id}` | unregister + unload (reversible) |
| `POST /rules/{id}/enable` `{approvedBy}` | revalidate and reload a disabled or quarantined rule |
| `POST /orders/evaluate` | run an order through the currently loaded rules |

## Adding the API key (only needed to generate rules from a spec)

Get a key at **[console.anthropic.com](https://console.anthropic.com)** → Settings → API Keys —
that's the developer platform, a separate paid product from a Claude.ai subscription. Then either:

```bash
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."   # stays out of the repo — preferred
set ANTHROPIC_API_KEY=sk-ant-...                          # or the environment variable
```

The model defaults to `claude-opus-4-8` (Claude Opus 4.8); override with `RuleCraft:Model`.

## Layout

```
Program.cs                        startup: key → engine → seed → endpoints
Discounts/  IDiscountRule.cs      the contract + the Order context
            DiscountAction.cs     the `then` vocabulary JSON rules may use, and its factory
            BulkOrderRule.cs      a static rule written in this repo
            DiscountRangeInvariant.cs  the acceptance test enforced on every candidate
            SeedRules.cs          the rules seeded on first run
Api/        RuleEndpoints.cs      propose → review → approve
            OrderEndpoints.cs     where rules are actually used
            RuleCraftProblems.cs  RuleCraft exceptions → HTTP responses
wwwroot/index.html                the review console
```

Rules persist under `rules/`: `<id>.meta.json` (audit metadata) plus `<id>.rule.json` or `<id>.cs`
(the source). That folder is runtime data, not source — the csproj excludes it from compilation,
and it is gitignored. Rules are reparsed/recompiled and re-loaded on restart; one that no longer
validates after a contract change is **quarantined**, not fatal.
