# Testing — the two tiers and the pass/fail gate

*Where "validated" stops being a sentence a human typed and becomes the output of an aggregator. Read
[the system map](../02-system-map.md) first. This page is the home of
[philosophy §5](../01-philosophy.md#5-a-validated-claim-is-a-machine-checked-claim) (P5 — *a validated claim
is a machine-checked claim*); every component page's **Tests** section points here.*

## Job

Make **"validated" a machine-checked fact, not a sentence** — "works in-game", "both loaders green", "no
hook threw" mean nothing as prose after watching a log scroll. They mean something only as the exit code of
a checker that fails the build on a red verdict. Testing runs at **two tiers**, and a capability is *done*
only when the tier that can prove it says so:

- **Tier 1 — offline unit tests** for pure logic: the modules deliberately kept dependency-light (Schema,
  the InteropPatch projection, HookMatch, Metadata) proven over synthetic type shapes and real proxies with
  **no game boot**.
- **Tier 2 — the in-game battery** against a *real* IL2CPP build (the ToyGame fixture) under **both loaders**
  (BepInEx, MelonLoader) at parity — one structured JSON record per test, aggregated by a checker that
  itself is tested.

Neither tier scrapes a log line. That is the whole point of the rewrite's first step — see **Why**.

## In the tree

| Path | Holds |
|---|---|
| `managed/src/*.Tests/` | the **offline** unit projects — `Inutil.Schema.Tests`, `Inutil.InteropPatch.Tests`, `Inutil.Mods.Tests`, `Inutil.Metadata.Tests`, `Inutil.Tests`; each a standalone console app, dependency-free assert harness, exit `0` green / `1` red |
| `managed/test/Inutil.TestKit/` | the shared record model — `TestModel.cs` (`RecordKind`/`TestStatus`/`TestRecord` + the ONE JSON (de)serializer), `ResultSink.cs` (`IResultSink`/`FileResultSink`), `Aggregator.cs` (`Verdict`/`Judge`) |
| `managed/test/Inutil.TestKit.Cli/` | the CLI — `Program.cs` (`aggregate <file>` / `selftest`), `SelfTest.cs` (the test-the-tester suite) |
| `managed/test/Battery/Cases/` | the loader-**agnostic** `Suite` + the case groups: `Smoke`, `TaskFlip`, `Container`, `ContainerFlip`, `Hook`, `ValueType`, `ModHost`, `Repl`, `InteropMarker`, `Safe` |
| `managed/test/Battery/Inutil.Battery.{BepInEx,MelonLoader}/` | the thin per-loader plugin shims — build the `Suite`, register the same cases, write the sidecar |
| `tools/wine/` | provisions the ToyGame Win64 IL2CPP build + both loaders; `validate.sh` is the end-to-end gate |

## Design

**Tier 1 — offline, and that's a payoff, not a compromise.** Every module the two seams agree through is
[dependency-light on purpose](./10-schema.md) — no Il2CppInterop, no live domain — so its logic is provable
before a game exists. Each `.Tests` project is a plain console app with a dependency-free `Check` harness
(no NuGet test framework); `Program` accrues failures and exits non-zero if any:

- `Inutil.Schema.Tests` — the planner, classifier, and registry drift guardrails over *synthetic* type
  shapes (detailed in [schema (10)](./10-schema.md)).
- `Inutil.InteropPatch.Tests` — the pure path/projection logic (locator, Cecil member projection) **plus** a
  real Cecil rewrite driven over the *provisioned* proxies, asserted at the plan level and as flip outcomes;
  the Cecil half is **gracefully skipped** when no game is provisioned, the pure suites always run.
- `Inutil.Mods.Tests` — the no-build mod host end-to-end offline: compile a real lifecycle mod from source,
  load it into a collectible ALC, `Discover` it, drive a frame, tear it down — plus HookMatch's decision
  half, which is split out of the install path precisely so it is pure reflection, testable with no il2cpp
  (its `Diagnose` unit + a `Discover → OnWarning` integration; GAPS Gap 1).
- `Inutil.Metadata.Tests` — the wire-map emit shape over a synthetic attributed module (integration Cpp2IL
  pass skipped without a game).
- `Inutil.Tests` — the SDK runtime seam's game-independent surface (the `Il2CppSugar` Task-carrier logic).

**Tier 2 — the battery is a loader plugin that reports structured records.** The BepInEx/MelonLoader shims
each derive the loader's plugin base, build a `Suite("smoke")`, register the *same* case groups, and call
`RunAll`. There is exactly **one** driver (`Battery/Cases/Suite.cs`) — declare the manifest, run each case,
isolate its failure, report done — so the two loaders **cannot drift** (the §7.10 lesson: one shared driver,
thin per-loader shims). Every test emits **one flat JSON record per line** (JSONL) over three kinds
(`TestModel.cs`):

- **manifest** — written *first*, the FULL set of ids this battery intends to run. It is what lets the
  checker tell a test that ran-and-failed from one that never ran.
- **result** — one per case actually run: `pass` / `fail` / `skip` + a human `detail`.
- **done** — written *last*; its *absence* means the run crashed or was killed mid-run, so a partial file can
  never be read as a complete green one.

A case signals by control flow: return = pass (the string is the pass detail), `Check.True(false, …)` = fail,
`Check.Skip(…)` = an *explicit, reported* skip; any other exception is caught as a fail, so a throwing case
can never take the game down or silently vanish. `FileResultSink` **flushes every record** (append-and-close
per line) to a host-visible sidecar (`inutil-results.jsonl` in the game root, mapped to the host under wine),
because the game runs headless under Proton and is force-killed a few seconds in — robustness-under-kill is
the design constraint.

**The aggregator is the verdict.** `Aggregator.Judge` reads the sidecar and returns `Ok` **only when every
reason list is empty** — there is deliberately no path that returns green by absence of evidence. Each way a
battery can fail *or hide a failure* is an explicit red branch: missing/empty file, no manifest, no done, a
manifest id with no result (a *silently skipped* test), a result id not in the manifest, a duplicate, a
malformed line, and the obvious `status=fail`. The CLI's exit code (`Program.cs`) **is** the verdict —
`0` green, `1` red.

**`validate.sh` orchestrates the loop under one chosen loader** (`validate.sh <bepinex|melon> [--fail]`):
build + **self-test the aggregator** → build the interop patcher + SDK and patch the deployed proxies in
place → build the native hook engine and extract the wire map → build the battery shim → scrub + deploy →
launch the real IL2CPP game (GE-Proton via `umu` in a headless gamescope) → poll for the `done` record →
**aggregate; the exit code is the verdict**. The [wine harness](../../../tools/wine/README.md) (`setup-unity`,
`toygame-build`, `setup-bepinex`, `setup-melon`) provisions the ToyGame IL2CPP player + both loaders that
this gate runs against.

## Invariants

- **No scraped logs — structured records + a count check (P5).** A result is never inferred from a substring
  match on a log line; it is a typed record, and the emitter and aggregator share the *one* serializer in
  `TestModel.cs` so neither can hand-roll a shape the other misreads.
- **Every registered test MUST report.** The manifest ↔ results reconciliation makes a declared-but-never-
  reported id red (a silently skipped test — the original blind spot), as it does a rogue undeclared id, a
  duplicate, a missing `done`, and a malformed line. Because the manifest is derived from the ids a case
  group `Add`s, adding a case automatically enrolls it in the count check.
- **Test the tester before trusting it.** `inutil-testkit selftest` writes crafted sidecars and asserts the
  aggregator goes RED on every failure mode and GREEN *only* on a complete all-pass; `validate.sh` runs it
  **before** the battery and refuses to run "against a broken judge". The `--fail` flag arms a live in-game
  canary (`canary.fail`) so a real red run is proven end-to-end, not just offline.
- **Both loaders must be green at parity before "done".** One loader-agnostic `Suite`, two ~20-line shims;
  parity is a run of `validate.sh` per loader, and a family is not done until both are green.
- **The mandatory per-family test template** (spec §8 / §10): a new (or ported) virtual-slot family ships
  battery coverage for **non-virtual member**, **singleton virtual slot**, **multi-member virtual slot**
  (differing per-member closed generic args), an **open-generic-parameter member**, and a **cross-module
  slot root**. This is the checklist that would have caught the Nullable/Enumerable bugs at ship time — **a
  PR without it does not merge.**

## Limits, defers & TODOs

- **Environmental `Check.Skip` guards are honest harness guards, not gaps** ([reference/limits.md](../../reference/limits.md) preamble). A skip like
  "il2cpp proxy construction not reachable" or "Roslyn not deployed" is an *explicit, reported* non-result;
  **none fire in the real in-game run**. Likewise `IsDeferred`/`DeferReason` in the schema/patch is a
  fail-safe *feature*, tested per-reason — not unfinished work. Don't read either as a coverage hole.
- **`validate.sh` targets one loader per invocation.** Parity is enforced by running it for *each* loader;
  there is no single command that runs both, by design (each needs its own provisioned tree + injector
  override).
- **CI is not yet wired.** There is no pipeline config in the tree; `tools/wine/validate.sh` and `devenv.nix`
  are the ground truth for how the project builds and validates.

## Tests

This *is* the testing page, so the useful thing to describe is **how to add one**:

- **An offline case** goes in the matching `.Tests` project — a `Conv`/planner shape in
  `Inutil.Schema.Tests`, an IL projection or flip in `Inutil.InteropPatch.Tests`, a mod-host lifecycle or
  HookMatch decision in `Inutil.Mods.Tests`. Add an assertion through that project's dependency-free `Check`
  harness; the process exit code carries the verdict, so wiring is nothing more than a new `Check(...)` line.
  Reach for this tier whenever the logic is pure (`System.Type`, Cecil, a synthetic shape) — no game needed.
- **An in-game case** goes in `managed/test/Battery/Cases/`: add a `suite.Add("family.aspect.…", () => …)`
  to the right case group (or a new `XxxCases` group with a `Register(Suite)`), then register it in **both**
  loader plugins' `Load()` — the `SmokeCases.Register(suite)` pattern in `Inutil.Battery.BepInEx/Plugin.cs`
  and its MelonLoader twin. Signal outcome by control flow (return / `Check.True` / `Check.Skip`); the
  manifest and the count check pick the id up automatically. Reach for this tier whenever the seam needs a
  live il2cpp frame — a real flip, a hook boundary, a value-type write.

## Why it's shaped this way

The confirmed HIGH finding that motivated all of this: **v1's validation harness exited SUCCESS while
ignoring 9 of 12 battery verdicts** — it checked a subset of expected PASS lines scraped from a log and
declared success regardless of the rest. That is *why* 49 other bugs went unnoticed until an external
154-agent adversarial review found them: the harness that was supposed to catch them was structurally blind
to most of what it "ran". So **fixing the harness was step 1 of the rewrite — before porting a single
family**: every step of a hard cutover depends on a trustworthy pass/fail signal,
and building on a scraped-log harness would mean discovering its blind spots only *after* cutover.

The shape here makes the old failure mode impossible rather than merely fixed. There is no "rest" to
ignore, because there is no subset to check — the manifest declares every id, and a missing report is red.
The judge can't quietly pass, because it is self-tested to go red on each way a battery can hide a failure.
And "green" can't drift between loaders, because both run the one driver. Change any of that — infer a
result from a log again, let an unreported test slide, split the driver in two — and you re-open the exact
hole the rewrite was built to close. Internalize that before you touch it.
