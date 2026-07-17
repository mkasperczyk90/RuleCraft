# Contributing to RuleCraft

Thanks for looking. RuleCraft is a library that compiles and runs LLM-generated code inside the
host process, so a few of its constraints are load-bearing rather than incidental — this file
points them out so a PR doesn't run aground on one.

## Before a big change, open an issue

For a bug fix or a small improvement, a PR is welcome directly. For anything that changes the
public API or the on-disk rule format, open an issue first: both are covered by SemVer here, and a
format change has to consider rules already sitting in someone's store.

## Building and testing

```bash
dotnet build RuleCraft.slnx --configuration Release
dotnet test tests/RuleCraft.Tests/RuleCraft.Tests.csproj --configuration Release
```

- **The SDK is pinned** in `global.json`. Use that version (or a matching patch) so your build
  matches CI.
- **Tests target `net8.0` and `net10.0` both.** `net8.0` is the package's promise; the suite must
  pass on both runtimes and on both Linux and Windows — CI runs all four legs. If you only have one
  runtime installed, that leg is skipped locally but still runs in CI.
- **The build is warning-free**, and CI keeps it that way. A new warning is a regression.
- A couple of tests (`UnloadTests`, `LifecycleTests`) exercise GC and collectible load contexts.
  If one flakes on a loaded machine, diagnose it (`--blame-hang`) rather than re-running blindly —
  it may be telling you something.

Conventions live in [`.editorconfig`](.editorconfig) and, for coding agents, in
[`AGENTS.md`](AGENTS.md). `dotnet format` follows the editorconfig; note that it will want to
reindent one deliberate stacked-`foreach` in `TypeShapeRenderer` — leave it.

## Things that are the way they are on purpose

Please don't "fix" these in a PR without discussion — each is a documented decision, most of them
in the README's [threat model](README.md#threat-model--read-this-before-production) and
[constraints](README.md#constraints-v1):

- **One assembly.** Everything ships in `RuleCraft.dll`; the DI helpers live in core rather than a
  separate package on purpose. Don't split the library.
- **Compiled rules are not sandboxed**, and the analyzer is a guardrail, not a boundary. Allowing
  `Regex` (ReDoS), an uncatchable stack overflow, and prompt injection through a spec are all
  deliberate — the human approval gate is the answer to them.
- **The JSON DSL has no arithmetic, aggregation or current-date**, by design. Widening the grammar
  is a real design change, not a quick add.
- **The store hashes exact bytes**, `Load` returns `null` in the rule load context (type identity),
  and JSON values are coerced at parse time. See the README's list of deliberate trade-offs.

## Reporting a security issue

Do not open a public issue for something exploitable. See [SECURITY.md](SECURITY.md) — and note
that most "the analyzer allowed X" cases are documented as intentional, while a genuine analyzer
bypass or JSON-sandbox escape is exactly what the private channel is for.

## Licensing

By contributing, you agree your contributions are licensed under the project's
[MIT license](LICENSE).
