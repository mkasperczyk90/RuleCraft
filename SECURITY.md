# Security policy

RuleCraft compiles and executes code that an LLM wrote, inside your process. Its
[threat model](README.md#threat-model--read-this-before-production) is part of the README rather
than a footnote, and this file assumes you have read it.

## Reporting a vulnerability

Report privately through GitHub: **Security → Report a vulnerability** on this repository. That
opens a private advisory only the maintainer can see. Please do not open a public issue for
anything you believe is exploitable.

Expect an acknowledgement within a few days. If a report is valid, you will get a fix or a clear
explanation of why the behaviour is intentional — see the scope below, because that line matters
more here than in most libraries.

## Supported versions

The latest released version. Nothing is published on nuget.org yet; once 1.0.0 ships, fixes will
land in the newest release rather than being backported.

## Scope: what counts

Three of the four bullets below are already documented in the README as deliberate. That is not a
brush-off — it is the whole design, and the human approval gate is the answer to all three.

**In scope — please report these privately:**

- **Anything a JSON rule can do beyond its grammar.** JSON rules are a real sandbox: a document is
  interpreted, never compiled, and can only express conditions over your context fields and fill in
  your `TThen` DTO. Reaching anything else — I/O, reflection, unbounded CPU on `Resolve`, escaping
  the depth/size/node bounds — is a genuine escape and the highest-severity report here.
- **A bypass of the security analyzer for compiled rules.** Source that the analyzer accepts but
  that still reaches `System.IO`, `System.Net`, `System.Reflection`, `System.Diagnostics`, interop,
  thread/work creation, `Activator`, `Environment`, `unsafe` or `dynamic`. The analyzer is
  explicitly *a guardrail and a review aid, not a sandbox* — but people rely on it while reviewing,
  so a way around it is worth a private report even though it is not, strictly, a broken boundary.
- **A rule that runs without having been approved**, or an approval/quarantine decision that can be
  forced from outside the engine's API.
- **Type-identity or load-context escapes** — a rule assembly reaching into the host beyond its
  contract.

**Out of scope — documented, on purpose:**

- **Compiled C# rules are not sandboxed.** .NET has no in-process sandbox; an approved rule runs
  with your process's full permissions. This is stated plainly in the README. "An approved compiled
  rule can do X" is not a vulnerability report.
- **ReDoS via a regex in `AppliesTo`, and `StackOverflowException` from unbounded recursion.** Both
  are listed under *What the analyzer lets through, on purpose*. `Regex` is allowed because business
  rules need it, and a stack overflow cannot be caught in .NET by anyone.
- **Prompt injection through a rule's spec.** The spec goes into the prompt verbatim; whoever writes
  specs can try to talk the model into anything. That is why `AutoApprove` defaults to off. A report
  that a spec produced hostile *source* is interesting only if that source also **passed** the
  analyzer and the tests — at which point it is an analyzer bypass, and in scope.
- **Tampering with files under the store path.** The SHA-256 of each source is an integrity signal,
  not a security boundary: anyone who can write to `rules/` already owns the process.

## If you run RuleCraft in production

The short version of the README's advice: **prefer JSON rules, escalate to compiled C# deliberately,
and leave `AutoApprove` off.** Do not accept specs from hostile users with no human review — for
compiled rules that needs OS-level isolation, which this library does not provide and does not claim to.
