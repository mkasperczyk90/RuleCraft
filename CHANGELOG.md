# Changelog

All notable changes to RuleCraft are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project aims to follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html) — for a library, the public surface
that governs SemVer is the API *and* the rule-persistence format on disk.

## [Unreleased]

## [1.0.0] — unreleased

First public release. The engine, its three rule kinds and the persistence format are considered
stable from this version.

### Added
- **Runtime rule engine** for one contract/context pair: describe a rule in natural language, an
  LLM implements it, it is verified and human-approved, then hot-loaded into the running process
  with no redeploy.
- **Three rule kinds**, competing purely by priority: interpreted **JSON-DSL** rules (a real
  sandbox), **compiled C#** rules (Roslyn, in-memory, into a collectible `AssemblyLoadContext`),
  and **static** host-code rules.
- **Security gate for compiled rules**: a minimal whitelisted reference set plus a semantic-model
  analyzer, resolving most-specific-first (member → type → namespace).
- **Approval workflow**: rules are parked as `PendingApproval` and nothing runs until approved;
  `AutoApprove` defaults to off. Reject, disable, enable and quarantine transitions included.
- **Durable, crash-safe store**: source is the single source of truth, written atomically
  alongside audit metadata (spec, status, priority, SHA-256, contract/context fingerprint, model
  id, approver, full validation report).
- **DI integration** in-box: `services.AddRuleCraft<TContract, TContext>(…)`.
- **Vendor-neutral generation** through `Microsoft.Extensions.AI.IChatClient` — no model id is
  hard-coded.
- Ships as a single assembly, `RuleCraft.dll`, with XML docs and a symbol package.

[Unreleased]: https://github.com/mkasperczyk90/RuleCraft/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/mkasperczyk90/RuleCraft/releases/tag/v1.0.0
