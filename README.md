# RuleCraft

<!-- The NuGet badge stays commented until 1.0.0 is actually published — until then it renders as
     "invalid", which looks worse than no badge at all. Uncomment it with the first release:
     [![NuGet](https://img.shields.io/nuget/v/RuleCraft.svg)](https://www.nuget.org/packages/RuleCraft)

     Every link in this file is absolute on purpose: it is also the package page on nuget.org, which
     does not resolve relative links. Keep it that way when adding one. -->
[![Build](https://github.com/mkasperczyk90/RuleCraft/actions/workflows/ci.yml/badge.svg)](https://github.com/mkasperczyk90/RuleCraft/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/mkasperczyk90/RuleCraft/blob/main/LICENSE)

**You define an interface. RuleCraft has an LLM implement it** — from a plain-language spec, at
runtime — verifies the result, waits for a human to approve it, and hot-loads it into the running
application. No redeploy.

It exists for the logic that changes faster than you want to ship: discounts, eligibility, limits,
routing. The people who know what a rule should say are usually not the people who can deploy it —
RuleCraft closes that gap without giving up a developer-grade safety net.

Concretely: your code owns the contract and the data it decides on, as ordinary types —

```csharp
public interface IDiscountRule { decimal GetDiscount(Order order); }
public sealed record Order(decimal Total, string CustomerType);
```

— and, given an engine for that pair (constructed once at startup, see
[Wiring rules into your code](#wiring-rules-into-your-code)), a rule starts as a sentence:

```csharp
var rule = await engine.AddRuleAsync("VIP customers get 15% off orders of 200 or more");

// By now an LLM has written a C# class implementing IDiscountRule, and RuleCraft has compiled it
// in memory, run a security analyzer over it and executed its tests. It is NOT live yet:
engine.Approve(rule.Id, approvedBy: "you@corp.com");   // a human decides what runs

// From here the rule is part of the running process — no build, no restart:
var order = new Order(250m, "vip");
decimal discount = engine.Resolve(order)!.GetDiscount(order);   // 0.15m
```

(This snippet is [kept executable as a test](https://github.com/mkasperczyk90/RuleCraft/blob/main/tests/RuleCraft.Tests/ReadmeIntroTests.cs),
so it works.)

The implementation the LLM writes takes one of two forms, chosen per rule:

- **A JSON rule** — a document in a deliberately tiny rule language that RuleCraft *interprets*,
  never compiles. It can test fields of your context and fill in an action type you define, and
  nothing else — the grammar has no words for I/O, loops or reflection. A real sandbox; it covers
  most business rules, and it is the form to prefer.
- **A compiled C# class** — full expressive power for the rules JSON cannot say, behind a heavier
  gate: a whitelisted reference set, a security analyzer, generated tests, your acceptance tests,
  and the same human approval.

The LLM is optional: hand-written JSON or C# enters the same verification and approval loop
(`AddJsonRuleFromSource` / `AddRuleFromSource`), and an engine only ever fed rules you wrote
needs no LLM at all.

Under the hood, when a spec arrives, RuleCraft:

1. asks an LLM to generate a C# class implementing the contract, a predicate
   (`AppliesTo(context)` — "does this rule apply?") and self-tests,
2. compiles it in-memory with Roslyn (against a minimal, whitelisted reference set),
3. runs a **semantic-model security analyzer** over the code,
4. executes the **generated tests** plus the developer's **acceptance tests** in a throwaway
   `AssemblyLoadContext`,
5. persists the source + audit metadata to disk and parks the rule as **PendingApproval**,
6. after a human approves it (via your own HTTP endpoint), loads it into a **collectible
   `AssemblyLoadContext`** in the live process,
7. at runtime, dispatches calls to the highest-priority rule whose predicate matches.

A JSON rule walks the same path, with parsing where compilation and analysis sit.

The whole library is a single assembly: `RuleCraft.dll`.

## Contents

- [Install](#install)
- [Quickstart](#quickstart)
- [Wiring rules into your code](#wiring-rules-into-your-code) — contract → engine → rules → approval → dispatch
- [JSON rules: a real sandbox for the 90% case](#json-rules-a-real-sandbox-for-the-90-case)
  - [Grammar](#grammar)
  - [The expressiveness ceiling — the honest part](#the-expressiveness-ceiling--the-honest-part)
- [Ordering: which rule wins](#ordering-which-rule-wins)
- [Where the code lives: disk vs memory](#where-the-code-lives-disk-vs-memory)
- [Sample app](#sample-app)
- [How the risky parts are handled](#how-the-risky-parts-are-handled)
- [Threat model — read this before production](#threat-model--read-this-before-production)
- [Constraints (v1)](#constraints-v1)
- [Solution layout](#solution-layout)
- [License](#license)

## Install

```bash
dotnet add package RuleCraft
```

- **Target framework:** `net8.0` or newer.
- **Dependencies:** `Microsoft.CodeAnalysis.CSharp` (Roslyn — RuleCraft compiles rules at runtime,
  so this is not a small dependency), plus the `Microsoft.Extensions.*` abstractions for AI,
  logging and dependency injection. Roslyn must be **5.6.0 or newer**; see
  [Constraints](#constraints-v1).
- **No LLM vendor is baked in.** Generation talks to `Microsoft.Extensions.AI.IChatClient`, so any
  provider works — the sample uses the official
  [`Anthropic`](https://www.nuget.org/packages/Anthropic) SDK. The library never hard-codes a model
  id, and an engine that is only ever fed rules you wrote needs no LLM at all.

## Quickstart

Hand-written rule, no LLM, no container — the shortest path from nothing to a rule dispatching.
(This snippet is [kept executable as a test](https://github.com/mkasperczyk90/RuleCraft/blob/main/tests/RuleCraft.Tests/QuickstartTests.cs),
so it works.)

```csharp
// Your contract, and the context its rules decide on. Ordinary types: no base class, no attributes.
public interface IDiscountRule { decimal GetDiscount(Order order); }
public sealed record Order(decimal Total, string CustomerType);

// The vocabulary a JSON rule may fill in, and what you build from it.
public sealed record DiscountAction(decimal Discount);
public sealed class FlatDiscount(decimal d) : IDiscountRule { public decimal GetDiscount(Order o) => d; }
```

```csharp
var engine = new RuleEngine<IDiscountRule, Order>(new RuleEngineOptions { StorePath = "rules" });
engine.EnableJsonRules<DiscountAction>(then => new FlatDiscount(then.Discount));

// An LLM writes these from a spec — here it is written by hand, which needs no API key.
var rule = engine.AddJsonRuleFromSource(
    """
    {
      "when":  { "field": "Total", "op": "gte", "value": 100 },
      "then":  { "discount": 0.10 },
      "tests": [
        { "context": { "total": 150, "customerType": "vip" }, "applies": true  },
        { "context": { "total": 50,  "customerType": "vip" }, "applies": false }
      ]
    }
    """);

// Nothing runs until a human approves it. That is the whole point.
engine.Approve(rule.Id, approvedBy: "you@corp.com");

var order = new Order(150m, "vip");
decimal discount = engine.Resolve(order)!.GetDiscount(order);   // 0.10
```

Swap `AddJsonRuleFromSource(document)` for `AddJsonRuleAsync("orders of 100 or more get 10% off")`
and the LLM writes the document instead — the rest of the loop is identical.

## Wiring rules into your code

### 1. Define the contract and the context

The contract is what rules implement; the context is what predicates decide on. Both are
ordinary types in your project — no base class, no attributes.

```csharp
public interface IDiscountRule { decimal GetDiscount(Order order); }
public sealed record Order(decimal Total, string CustomerType, int ItemCount, string Country);
```

### 2. Create the engine once, at startup

```csharp
builder.Services.AddSingleton<IChatClient>(
    new AnthropicClient().AsIChatClient("claude-opus-4-8"));   // any IChatClient; optional

builder.Services.AddRuleCraft<IDiscountRule, Order>(
    options =>
    {
        options.StorePath = "rules";                           // give each engine its own folder
        options.ModelId   = "claude-opus-4-8";                  // never hard-coded by the library
    },
    engine =>
    {
        engine.SetFallback(new NoDiscount());                   // used when nothing matches
        engine.AddAcceptanceTest(new DiscountRangeInvariant()); // invariant enforced on EVERY candidate
        engine.EnableJsonRules<DiscountAction>(DiscountActions.Build);  // the `then` vocabulary
        engine.AddStaticRule(new BulkOrderRule());              // hand-written rule from your repo
        engine.ReloadFromStore();                               // approved rules — AFTER EnableJsonRules
    });
```

`AddRuleCraft` takes `IChatClient` and `ILoggerFactory` from the container, registers the engine as
a singleton, and disposes it with the host. The second callback is your startup sequence: it runs
once, inside the singleton factory, which is what keeps `EnableJsonRules` ahead of `ReloadFromStore`
instead of leaving that ordering to memory. It runs on first resolve, so resolve the engine once at
startup if you would rather pay for recompiling the store at boot than on the first request:

```csharp
app.Services.GetRequiredService<RuleEngine<IDiscountRule, Order>>();
```

Constructing one by hand works exactly the same — `new RuleEngine<IDiscountRule, Order>(options)` —
and no part of the library requires a container.

Options are validated and **copied** into the engine: the instance you pass in stays yours, and
nothing you do to it afterwards can turn `AutoApprove` on or widen `SecurityPolicy` under rules
already in flight. A bad `StorePath`, a non-positive `TestTimeout` or zero generation attempts throw
from the constructor, at the composition root, rather than failing later inside a request.

The engine is thread-safe and meant to be a singleton: resolution is lock-free, adding or
removing rules swaps an immutable snapshot, and mutations of one rule are serialized — an
`Approve` racing a `Reject` cannot leave a rule live but recorded as rejected.

**Only the two generation methods are `async`**, because only they do I/O (the call to the LLM).
Compiling, parsing, testing and approving are CPU-bound and run on the calling thread: an `Approve`
costs a Roslyn compile, and the API says so rather than hiding it behind a `Task` that was never
asynchronous. Wrap those calls in `Task.Run` if a request thread must not block.

`Dispose()` unloads every rule assembly the engine loaded — worth doing if you build engines per
scope (a test suite, say); a singleton normally lives as long as the process.

### 3. Add rules — three kinds, one dispatcher

| Kind | Add it with | How it runs | Stored? | Needs approval? | Survives restart? |
|---|---|---|---|---|---|
| **Json** — a JSON-DSL document | `AddJsonRuleAsync(spec)` / `AddJsonRuleFromSource(doc)` | interpreted — no code | yes | yes | yes, reparsed |
| **Compiled** — C# source | `AddRuleAsync(spec)` / `AddRuleFromSource(src)` | Roslyn → collectible ALC | yes | yes | yes, recompiled |
| **Static** — a class in your repo | `AddStaticRule(new BulkOrderRule())` | already compiled | no | no — git is the gate | re-register at startup |

All three land in the same registry and compete purely by priority — the dispatcher cannot tell
them apart, and `GetRules()` lists them side by side.

A **static** rule is just a class implementing `IRule<TContract, TContext>`:

```csharp
public sealed class BulkOrderRule : IRule<IDiscountRule, Order>, IDiscountRule
{
    public bool AppliesTo(Order context) => context.ItemCount >= 50;  // the predicate
    public IDiscountRule Implementation => this;                       // may implement both
    public int Priority => 1;                                          // higher wins
    public decimal GetDiscount(Order order) => 0.05m;
}
```

Static rules skip compilation, the store and the approval queue, because the code already went
through your normal review; they exist only for the lifetime of the process, so register them on
every boot.

### 4. Approve what the LLM produced

`AddRuleAsync` never goes live by itself — it parks the candidate as `PendingApproval`. Map
these three calls to your own endpoints (the sample does exactly this):

```csharp
var pending = engine.GetPendingRules();  // id, spec, source, security report, test results
engine.Approve(id, approvedBy: "reviewer@corp.com");  // compiles again, then loads
engine.Reject(id, "not the logic we want");           // kept on disk for audit
```

Nothing here is a one-way door except rejection. `RemoveRule(id)` unloads a live rule and
`Enable(id, by)` brings it back — and brings back a rule quarantined by a contract change, once the
contract is restored. `Enable` revalidates from scratch, so it is exactly as safe as the original
approval; a rule that is still wrong is refused and stays quarantined. A **rejected** rule stays
rejected: reversing that decision means submitting a new candidate through review, not editing a
status.

| Status | Meaning | Way out |
|---|---|---|
| `PendingApproval` | validated, waiting for a human | `Approve` / `Reject` |
| `Approved` | live and dispatchable | `RemoveRule` → `Disabled` |
| `Disabled` | unloaded by an operator; source kept | `Enable` |
| `Quarantined` | failed revalidation (usually a contract change) | `Enable`, once it validates again |
| `Rejected` | a decision on the record | none — submit a new rule |

### 5. Dispatch

```csharp
var impl = engine.Resolve(order);         // winning rule's implementation, fallback, or null
var discount = impl.GetDiscount(order);

var all = engine.ResolveAll(order);       // every matching implementation, in evaluation order
```

Do not cache what `Resolve` returns beyond the current operation — a cached implementation
pins the rule's assembly and prevents unload.

## JSON rules: a real sandbox for the 90% case

Most business rules are "conditions over context fields → a parameterized outcome". For those,
the JSON DSL is strictly better than generated code: it is a **genuine sandbox** (no Roslyn, no
assembly, no load context, nothing for a security analyzer to miss) and it is **reviewable by the
people who own the rule** rather than only by programmers.

```json
{
  "name": "vip-big-orders",
  "priority": 5,
  "when": { "all": [
    { "field": "CustomerType", "op": "eq",  "value": "vip" },
    { "field": "Total",        "op": "gte", "value": 500 }
  ]},
  "then": { "discount": 0.15 },
  "tests": [
    { "name": "vip 600 applies", "context": { "total": 600, "customerType": "vip", "itemCount": 2, "country": "PL" }, "applies": true },
    { "name": "regular does not", "context": { "total": 600, "customerType": "regular", "itemCount": 2, "country": "PL" }, "applies": false }
  ]
}
```

### The `then` vocabulary is yours

A document cannot invent behaviour — it can only fill in a DTO your code defines:

```csharp
public sealed record DiscountAction(decimal Discount);            // the entire vocabulary
engine.EnableJsonRules<DiscountAction>(a => new FlatDiscount(a.Discount));
```

That one line is what makes the sandbox real: a JSON rule can express nothing your `TThen` type
cannot carry and your factory cannot build. The type also does triple duty — System.Text.Json
validates against it, the LLM prompt is *rendered from it* (so the vocabulary can never drift out
of sync), and changing the record breaks the factory at compile time. Rules need richer outcomes?
Widen the vocabulary in C#, with tests, deliberately.

Call `EnableJsonRules` **before** `ReloadFromStore`.

### Grammar

Conditions: `{"all":[…]}`, `{"any":[…]}`, `{"not":{…}}`, `{"always":true}`,
`{"field":"Path.To.Prop","op":"…","value":…}`.

| Operator | Applies to |
|---|---|
| `eq`, `neq` | any field |
| `gt`, `gte`, `lt`, `lte` | numeric and date fields only |
| `in`, `notIn` | `value` must be an array |
| `contains` | string field (substring) or collection field (membership) |
| `startsWith`, `endsWith` | string fields |
| `isNull`, `notNull` | nullable fields — including reference types your context declares as `string?` rather than `string` |

Field paths resolve over any public property, so `Customer.Country`, `PlacedAt.DayOfWeek` and
`Items.Count` work for free. String comparisons are **case-insensitive by default** (rule authors
write "VIP" where the data says "vip"); pass `stringComparison:` to `EnableJsonRules` to change it.

### The expressiveness ceiling — the honest part

The DSL deliberately has no arithmetic, no aggregation and no current date. "1% per 100 zł above
the threshold, capped at 20%, computed on the basket excluding promo items" **cannot** be written
as JSON here, and that is by design: adding those features would mean building a programming
language inside JSON — worse than C#, without types, a debugger or an IDE.

When a rule doesn't fit, escalate to `AddRuleAsync` (compiled C#) and accept the security
trade-off for that one rule. The LLM is instructed to say so rather than approximate the spec, and
saying so is a first-class outcome, not a failure: `AddJsonRuleAsync` throws
**`RuleNotExpressibleException`** carrying the model's own explanation of what the grammar lacks.
It is thrown on the spot — retrying cannot grow the grammar — and nothing is written to the store.

```csharp
try { await engine.AddJsonRuleAsync(spec); }
catch (RuleNotExpressibleException ex)
{
    // ex.Reason: "…needs arithmetic, and the DSL has none."
    await engine.AddRuleAsync(spec);   // the deliberate escalation
}
```

### What the DSL does and does not save you from

| | |
|---|---|
| Typo'd field name, wrong value type, undeclared enum, empty `all` | **Parse error**, listing the available fields — same loudness as a C# compile error |
| `File.Delete`, reflection, an infinite loop | **Impossible to express** — this is the real win over generated code |
| An inverted operator or a wrong threshold | **Silent in JSON and in C# alike.** Only the `tests` array catches it — which is why at least one `applies: true` and one `applies: false` case are *required*, not suggested |

## Ordering: which rule wins

Several predicates may match one context, so the order is explicit and inspectable.
`GetRules()` returns every rule — static and stored, loaded and not — with the exact position
its predicate is consulted at:

```csharp
foreach (var rule in engine.GetRules())
    Console.WriteLine($"{rule.EvaluationOrder}. {rule.Name} ({rule.Origin}, priority {rule.Priority}, {rule.Status})");

// 0. vip-big-orders (Json, priority 5, Approved)
// 1. BulkOrderRule  (Static, priority 1, Approved)
// 2. legacy-rule    (Compiled, priority 1, Approved)
//    pending-rule   (Json, priority 0, PendingApproval)   ← EvaluationOrder = null, not loaded
```

The order follows `RuleEngineOptions.ResolutionPolicy` and is the same order `Resolve` and
`ResolveAll` actually use — one code path, so the report cannot drift from the behaviour:

| Policy | Evaluation order | `Resolve` returns |
|---|---|---|
| `HighestPriority` (default) | priority descending, **newest wins ties** | the first match |
| `FirstMatch` | registration order, oldest first | the first match |
| `ThrowOnAmbiguity` | registration order | throws `AmbiguousRuleMatchException` if 2+ match |

Rules that are not loaded (pending, rejected, quarantined, disabled) have
`EvaluationOrder == null` and are listed last.

## Where the code lives: disk vs memory

- **Source** (`rules/<id>.cs` for compiled rules, `rules/<id>.rule.json` for JSON ones) is written
  to disk, alongside `rules/<id>.meta.json` with the spec, status, priority, SHA-256 of the source,
  the contract and context types it was written against and their fingerprint, model id, approver
  and the full validation report. Source is the single source of truth.
- **That folder is runtime data, not source.** If it sits inside a project directory, exclude it
  from compilation (`<Compile Remove="rules/**" />`): its `.cs` files are meant for Roslyn at
  runtime, and a project that compiles them too would bake a stored rule into your own assembly —
  where a bad one breaks your build instead of being quarantined.
- **The compiled assembly is never written to disk.** Roslyn emits to a `MemoryStream` and it
  is loaded via `LoadFromStream` into a collectible `AssemblyLoadContext` — nothing to lock,
  nothing to clean up, and a rule is gone from the process once unloaded.
- On restart, every approved rule is **recompiled from its source** and revalidated. That is
  why a contract change surfaces immediately as a quarantined rule instead of a stale DLL that
  pretends to still fit.
- Static rules are in your own assembly and touch none of this.

## Sample app

[`samples/RuleCraft.Sample`](https://github.com/mkasperczyk90/RuleCraft/tree/main/samples/RuleCraft.Sample)
is an ASP.NET Core minimal API demonstrating the full loop, with a small review console: write a
spec (as a JSON rule or as C#), watch the candidate appear in the approval queue with its document,
report and test results, approve it, and see the discount change on the next order — without
restarting the process. It seeds one rule of each kind at startup, so it runs on a fresh clone with
no API key; a key is only needed to generate a rule from a spec.

```bash
git clone https://github.com/mkasperczyk90/RuleCraft.git
cd RuleCraft/samples/RuleCraft.Sample
dotnet run                          # then open http://localhost:5199/
```

Its endpoints, configuration and layout are documented in the
[sample's own README](https://github.com/mkasperczyk90/RuleCraft/blob/main/samples/RuleCraft.Sample/README.md).

## How the risky parts are handled

- **Type identity.** The generated assembly must share the host's contract type. The rule
  `AssemblyLoadContext.Load()` returns `null` for every dependency (everything resolves to the
  default context), and the analyzer rejects code that re-declares contract/context types.
  Otherwise you get the infamous `InvalidCastException: cannot cast X to X`.
- **Unload is cooperative.** `RemoveRule` atomically unregisters the rule and calls
  `Unload()`; the assembly disappears when nothing references it. `Dispose()` does the same for
  every rule at once. Never cache resolved implementations long-term.
- **The store survives a crash mid-write.** Metadata and source are written to a temp file and
  moved into place, because a half-written `.meta.json` reads as corrupt — and a corrupt one is
  skipped at startup, silently dropping an approved rule from the application.
- **Circular tests.** LLM-generated tests mostly verify the LLM against itself. Register
  `IRuleAcceptanceTest<TContract>` invariants — they run against every candidate and again on
  every reload.
- **Generation loop.** Compile errors, security findings and test failures are fed back to the
  model (up to `MaxGenerationAttempts`); failures surface as `RuleGenerationException` with the
  full attempt history.

## Threat model — read this before production

**It depends on the kind of rule, and the difference is not a detail.**

**JSON rules are a real sandbox.** A document is interpreted, never compiled. It can express
conditions over your context fields and fill in your `TThen` DTO — nothing else. There is no
`System.IO` to reach for, no reflection to smuggle in, no loop to spin: the grammar has no such
concepts. Depth, size and node count are bounded, so a rule cannot burn CPU on every `Resolve`.

**Compiled C# rules are not.** .NET has **no in-process sandbox**: approved rule code runs with
the full permissions of your process. The security gate (reference whitelist + semantic-model
analyzer banning `System.IO`, `System.Net`, `System.Reflection`, `System.Diagnostics`, interop,
threading, `Activator`, `Environment`, `unsafe`, `dynamic`, preprocessor directives, …) is a
**guardrail and review aid, not a sandbox**.

The policy resolves most-specific-first — member, then type, then namespace — so it can hand out
`System.Threading.Tasks` (an async contract cannot be implemented without naming `Task`) while
still refusing `Task.Run`, `Task.Factory`, `Parallel` and friends. That distinction is load-bearing:
work a rule starts for itself outlives the test harness's timeout, which is the only thing standing
between a runaway rule and your process.

### What the analyzer lets through, on purpose

Three things a compiled rule can still do. None is a reason not to use compiled rules; all three
are reasons the human approval step is not decoration.

- **Burn CPU on every `Resolve`.** `System.Text.RegularExpressions` is allowed — business rules
  need it — so a catastrophically backtracking pattern in `AppliesTo` is a ReDoS on your hot path,
  triggered by whatever data hits it. The test harness times out a candidate that hangs *during
  validation*; it cannot help once the rule is live. Review regexes in rule code the way you would
  in your own.
- **Kill the process by recursing.** `StackOverflowException` cannot be caught in .NET. The
  `try`/`catch` around every predicate makes a *throwing* rule harmless; a rule that recurses
  without a base case takes the process down regardless, and no in-process gate can change that.
- **Be argued into existence by the spec.** The spec text goes into the prompt verbatim: whoever
  writes specs can try to talk the model into whatever code they like. That is not a hole in the
  analyzer, it is the reason `AutoApprove` defaults to off — approval is what separates "a user
  suggested this code" from "this code runs in production". JSON rules are unaffected in kind: the
  grammar bounds the outcome no matter what the prompt says.

| Scenario | JSON rules | Compiled C# rules |
|---|---|---|
| LLM accidentally emits harmful code (`File.Delete`, `Process.Start`, reflection) | **Impossible to express** | **Yes** — analyzer catches it reliably |
| Semi-trusted internal users writing specs, human approval enabled | **Yes** | **Yes** — approval + analyzer + acceptance tests is a reasonable bar |
| Hostile users submitting specs with no human review | **Plausible** — but you own the residual risk: a bad rule is still a *wrong business decision*, and `TThen` is exactly as dangerous as your factory makes it | **No.** Do not do this. You would need OS-level isolation (separate process/container) |

The practical consequence: **prefer JSON, escalate to C# deliberately**. `AutoApprove = true`
disables the human gate for both kinds — leave it off for compiled rules unless every spec author
could equally well deploy code to the box.

## Constraints (v1)

- **`Microsoft.CodeAnalysis.CSharp` 5.6.0 or newer** is required. A host that pins an older
  version fails the restore with `NU1605` (package downgrade) — raise the pin or drop it and let
  NuGet resolve. 5.6.0 is the newest published, so there is no "host is ahead of us" case yet.
- Single-file publish and trimming of the **host** are not supported: Roslyn needs the contract
  assemblies loadable from disk, and a clear exception is thrown when a compiled rule is validated.
  The reference set is built lazily, on the first compiled rule, so an engine that only ever sees
  JSON rules never reaches that code — untested territory rather than a promise.
- Generated C# tests run in-process with a timeout; a pure-CPU infinite loop is reported as a
  failed test but its thread may leak until process exit. JSON rules cannot loop.
- **One `StorePath` per engine.** The default is a `rules/` folder relative to the process, so two
  engines over different contracts land in the same one. Each records the contract its rules were
  written against, ignores the other's, and logs an error at reload rather than quarantining what
  is not its own — but the folder is still shared, and you should not rely on that politeness.
- `Expression.Compile` is used for JSON field access, so a JSON-rule engine will not survive full
  AOT either.
- Source files on disk are hashed; a tampered file is refused at approval and quarantined on
  reload. This is an integrity signal, not a security boundary — anyone who can write to
  `rules/` already owns the process. It also means hand-editing `<id>.rule.json` quarantines the
  rule: go through the engine, not the disk.
- The JSON DSL has no arithmetic, aggregation or current date, by design — see the ceiling above.

## Solution layout

```
src/RuleCraft/            the library (single DLL): engine, JSON-DSL parser/interpreter,
                          Roslyn compiler, ALC loading, security analyzer, test harness,
                          store, LLM generation
tests/RuleCraft.Tests/    xunit suite (147 tests, no network needed)
samples/RuleCraft.Sample/ ASP.NET Core minimal API demo + review console
```

## License

[MIT](https://github.com/mkasperczyk90/RuleCraft/blob/main/LICENSE) — © 2026 mkasperczyk90@gmail.com
