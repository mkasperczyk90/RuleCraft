# AGENTS.md

Guidance for coding agents working in this repository. Human-facing docs live in `README.md`
(the design, the threat model, the constraints — read it before changing behaviour) and
`todo.md` (open work, and the reasoning behind decisions already made, in Polish).

## What this is

RuleCraft is a .NET library where business rules are described in natural language, implemented
by an LLM, verified automatically, approved by a human, and hot-loaded into a running process.
The library compiles and executes generated code, so the security gate, the approval step and
the load-context handling are the design — not incidental plumbing.

```
src/RuleCraft/            the library, shipped as a single DLL
tests/RuleCraft.Tests/    xunit suite (151 tests, no network, no API key)
samples/RuleCraft.Sample/ ASP.NET Core minimal API + review console
```

## Commands

```bash
dotnet build RuleCraft.slnx
dotnet test tests/RuleCraft.Tests/RuleCraft.Tests.csproj
dotnet pack src/RuleCraft/RuleCraft.csproj -c Release
dotnet run --project samples/RuleCraft.Sample   # http://localhost:5199, works with no API key
```

The build is expected to be warning-free — library, sample and `pack` alike. A new warning is a
regression, not noise.

## Non-obvious things worth knowing before you change something

- **One assembly, no satellite packages.** `RuleCraft.dll` is a hard project constraint. DI
  integration lives in core rather than in a `RuleCraft.DependencyInjection` package for exactly
  this reason. Taking a NuGet dependency on an abstractions package is fine; splitting the
  library is not.
- **Framework targets are deliberately mixed.** The library targets `net8.0` (that is the
  package's promise); tests and sample target `net10.0`. The solution is `.slnx`, which needs a
  recent SDK (10.0.201 locally).
- **`samples/RuleCraft.Sample/rules/` is runtime data, not source.** The csproj removes it from
  compilation because Roslyn compiles those `.cs` files at runtime; if the project compiled them
  too, a stored rule would land in the app's assembly and a bad rule would break the build. It is
  gitignored for the same reason.
- **Tests need no secrets and no network.** Generation goes through `FakeChatClient` /
  `ScriptedChatClient`. Do not wire an API key into tests or CI for the suite — only publishing
  needs one.
- **`UnloadTests` and `LifecycleTests` lean on GC and collectible `AssemblyLoadContext`s** (a
  `GC.Collect()` loop). If they flake on a loaded machine, diagnose with `--blame-hang`; do not
  paper over it with a retry.
- **`RuleForge` appears in `todo.md` on purpose.** It is the old name and now refers to somebody
  else's package on nuget.org. Do not "fix" those mentions. Everywhere else the name is
  `RuleCraft` — including LLM prompts, ALC names, generated assembly names and the logger
  category, where it is easy to miss.
- **The security policy resolves most-specific-first**: member → type → namespace. That is what
  lets `System.Threading.Tasks` through while still banning `Task.Run`. Widening the allow-list at
  namespace level silently undoes bans below it.

## Conventions

- Comments explain *why*, and only where the code cannot say it itself. The existing ones state
  constraints and trade-offs; match that bar rather than narrating the next line.
- Public API carries XML docs — the package ships them and consumers read the API through
  IntelliSense. On records, document positional parameters with `<paramref>` inside `<summary>`;
  `///` on a positional parameter is dropped by the compiler and never reaches IntelliSense.
- `Async` suffix only where there is real I/O. CPU-bound work stays synchronous and does not get
  wrapped in `Task.Run` on the consumer's behalf.
- Tests are xunit, named as sentences (`The_readme_quickstart_works`). The README's code is kept
  executable: the intro example in `ReadmeIntroTests.cs`, the quickstart in `QuickstartTests.cs` —
  change one side, change the other.

## Before finishing

Run the full suite. If you touched anything the README describes — the API surface, the threat
model, the constraints, the layout — update the README in the same change.
