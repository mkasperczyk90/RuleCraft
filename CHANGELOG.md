# Changelog

Maintained automatically by [Release Please](https://github.com/googleapis/release-please) from
[Conventional Commit](https://www.conventionalcommits.org/) messages — new entries are prepended
above on each release; do not edit by hand.

## [1.0.1](https://github.com/mkasperczyk90/RuleCraft/compare/v1.0.0...v1.0.1) (2026-07-18)


### Bug Fixes

* change .js to .mjs ([1e37fdc](https://github.com/mkasperczyk90/RuleCraft/commit/1e37fdc281200d46f099eaecad698f09867aeb58))
* **testing:** treat cancelled test bodies as timeouts, not passes ([f27a6d5](https://github.com/mkasperczyk90/RuleCraft/commit/f27a6d53b0fda016c75963d6992d761fdfd99d6a))
* **testing:** treat cancelled test bodies as timeouts, not passes ([d93b475](https://github.com/mkasperczyk90/RuleCraft/commit/d93b47556ebde25e2f4f8664c932ace227d053d3))

## [1.0.0](https://github.com/mkasperczyk90/RuleCraft/releases/tag/v1.0.0) (2026-07-17)


### Features

* Runtime rule engine for one contract/context pair: describe a rule in natural language, an LLM implements it, it is verified and human-approved, then hot-loaded into the running process with no redeploy.
* Three rule kinds, competing purely by priority: interpreted JSON-DSL rules (a real sandbox), compiled C# rules (Roslyn, in-memory, into a collectible `AssemblyLoadContext`), and static host-code rules.
* Security gate for compiled rules: a minimal whitelisted reference set plus a semantic-model analyzer, resolving most-specific-first (member → type → namespace).
* Approval workflow: rules are parked as `PendingApproval` and nothing runs until approved; `AutoApprove` defaults to off. Reject, disable, enable and quarantine transitions included.
* Durable, crash-safe store: source is the single source of truth, written atomically alongside audit metadata (spec, status, priority, SHA-256, contract/context fingerprint, model id, approver, full validation report).
* DI integration in-box: `services.AddRuleCraft<TContract, TContext>(…)`.
* Vendor-neutral generation through `Microsoft.Extensions.AI.IChatClient` — no model id is hard-coded.
* Ships as a single assembly, `RuleCraft.dll`, with XML docs and a symbol package.
