# Changelog

Maintained automatically by [Release Please](https://github.com/googleapis/release-please) from
[Conventional Commit](https://www.conventionalcommits.org/) messages — new entries are prepended
above on each release; do not edit by hand.

## [1.1.0](https://github.com/mkasperczyk90/RuleCraft/compare/v1.0.1...v1.1.0) (2026-07-25)


### Features

* pluggable rule store, security-analyzer hardening, and CI quali… ([a8ae419](https://github.com/mkasperczyk90/RuleCraft/commit/a8ae4194556cb7fd3c5a64fd9a50c86cf00dcd18))
* pluggable rule store, security-analyzer hardening, and CI quality gates ([a9e807e](https://github.com/mkasperczyk90/RuleCraft/commit/a9e807e3a06364531f7c8d6d04f9bd72cafbec01))


### Bug Fixes

* harden file store paths and log output ([495ae50](https://github.com/mkasperczyk90/RuleCraft/commit/495ae50c331c247b092c4f2e0e078af44fc9cf54))
* resolve commitlint configuration ([471fa44](https://github.com/mkasperczyk90/RuleCraft/commit/471fa44adff136f5f4870e3f2df22f1877cefe63))
* **security:** harden file store paths and log output ([5cfb471](https://github.com/mkasperczyk90/RuleCraft/commit/5cfb47133f3b589495a8bd8f13db5ebc85f68dc7))
* **security:** harden file store paths and log output ([4790af4](https://github.com/mkasperczyk90/RuleCraft/commit/4790af43a14c8da240315fcff49b928701730a52))
* **security:** harden file store paths and log output ([b2dbc06](https://github.com/mkasperczyk90/RuleCraft/commit/b2dbc067087f8d6af9bcdcecdbe332162dd02ab5))
* **security:** harden file store paths and log output ([943ba5c](https://github.com/mkasperczyk90/RuleCraft/commit/943ba5c0df202c069c672c5f687f47b78c16ebca))

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
