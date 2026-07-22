# managed/test — the v2 validation harness

The structured, machine-checked replacement for v1's removed log-scrapers (`docs/contribution/architecture/20-testing.md`). Built
**first**, before the engine rewrite, because a hard cutover (§9) has no live fallback — every later "this
family works" claim has to stand on a verdict signal that *cannot be green when it shouldn't be*.

The v1 harness's confirmed HIGH bug was not "a test failed and we missed it" — it was **the tester lied**:
`run-bepinex.sh` exited SUCCESS while ignoring 9 of 12 verdicts it had scraped. This harness is designed so
that failure mode is structurally impossible.

## The three failure modes it structurally excludes

1. A test reports `fail` → the run goes red. (the obvious one v1 still managed to ignore)
2. A test is **declared but never runs** → red. A silently-skipped test is indistinguishable from a passing
   one only if you never wrote down what was *supposed* to run — so every battery declares a **manifest**
   first, and the aggregator reds on any declared id with no result.
3. The game **crashes mid-battery** → red. The battery writes a `done` marker last; its absence means the
   run didn't finish, so a partial sidecar can never be mistaken for a complete green one.

Absence is always red. There is exactly one green path: a manifest, a result for every declared id, all
passing (or explicitly `skip`), and a `done`.

## Protocol

A battery writes **JSONL** to a host-visible sidecar (`inutil-results.jsonl` in the game root, which is
mapped to the host filesystem under wine — the same place `-logFile` writes). One flat JSON object per line,
flushed per record so a force-kill leaves every already-reported result on disk:

```jsonc
{"kind":"manifest","battery":"smoke","ids":["plumbing.alive","il2cpp.runtime","canary.fail"]}  // FIRST
{"kind":"result","battery":"smoke","id":"plumbing.alive","status":"pass","detail":"..."}
{"kind":"result","battery":"smoke","id":"il2cpp.runtime","status":"pass","detail":"resolved ToyGame.Player @ 0x..."}
{"kind":"result","battery":"smoke","id":"canary.fail","status":"pass","detail":"..."}
{"kind":"done","battery":"smoke"}                                                               // LAST
```

`status` is `pass` | `fail` | `skip`. `skip` is an **explicit, reported** non-result (e.g. "not applicable on
this loader") — visible, and it still satisfies "every declared id reported". A test that emits *no* record is
`missing`, which is red. (P4: silence is a design decision, not a default.)

The **record type and its (de)serializer live once**, in `Inutil.TestKit`, referenced by both the emitting
plugin and the reading aggregator — so the two sides cannot drift into disagreement (P1). This is the same
discipline the whole rewrite is about, applied to the harness itself.

## Layout

```
Inutil.TestKit/          the shared protocol: TestRecord + sink + Aggregator (net6.0;net9.0 multi-target)
Inutil.TestKit.Cli/      host CLI (net9): `aggregate <file>` (verdict → exit code) · `selftest` (test the tester)
Battery/
  Cases/                 loader-AGNOSTIC test cases + the Suite runner — one implementation, both loaders
  Inutil.Battery.BepInEx/    ~20-line BasePlugin shim over the shared Suite
  Inutil.Battery.MelonLoader/ ~20-line MelonMod shim over the SAME Suite
```

The two loader shims are thin by design: a case that passes under one loader and fails under the other is a
real loader difference, not a test divergence — the §7.10 lesson (one shared driver, thin per-loader shims)
applied so the MelonLoader-inertness class of bug can't recur here.

## Running

```sh
testkit-selftest        # offline, no game: prove the aggregator catches every failure mode (CI gate)
bepinex-validate        # build+selftest judge → build+deploy shim → launch ToyGame → aggregate (exit = verdict)
melon-validate          # same, under MelonLoader
bepinex-validate --fail # arm the canary so a real run goes RED end-to-end (expected exit 1)
```

Every `*-validate` run **self-tests the aggregator before trusting it** — the judge is proven sound on each
run, not just in CI. Needs a provisioned loader tree (`setup-bepinex` / `setup-melon`) inside `devenv shell`.

## Adding coverage (and, later, a family battery)

The smoke Suite (`Battery/Cases/SmokeCases.cs`) is only the loop's liveness check — three cases that prove
build → deploy → launch → collect → aggregate is real, with no dependency on the not-yet-written engine. As
the v2 engine's families come online, each ships its battery cases into this same Suite:

1. Add cases to a `Register(Suite)` — `suite.Add("family.case", () => { Check.True(...); return "detail"; })`.
   Return = pass (string is the detail); `Check.True(false, …)` = fail; `Check.Skip(…)` = explicit skip; any
   other throw is caught as a fail. No new sink, no new protocol, no new aggregator.
2. Register it from both loader shims (or a shared registration list) so it runs under BepInEx **and**
   MelonLoader.
3. Per §8's mandatory per-family template, a virtual-slot family's cases must cover: a non-virtual member, a
   singleton virtual slot, a multi-member slot with differing per-member closed generics, an open-generic
   member, and a cross-module slot root. The manifest/`missing` check enforces that the declared cases
   actually ran; a PR that declares them but skips one goes red.

Golden-master capture (§9 step 2) is deliberately **out of this harness's scope** — v1 lives on the
`ienumerable-natural-typing` branch and its behavior is captured per-family, from a worktree, when each family
is ported. This harness is the trustworthy *signal*; golden masters are an input it consumes later.
