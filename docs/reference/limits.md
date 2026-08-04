# Limits & roadmap — what's green, what fails loud, what's not built

*Reference. The honest map of inutil's edges — what's green, what fails loud, what's not built. The
per-component picture is in the [architecture corpus](../contribution/02-system-map.md).
Verify against the tree, which is updated as work lands.*

The governing invariant, first, because it changes how you read everything below:

> **Every limit here fails LOUD.** A shape inutil can't handle raises a precise exception (a
> `NotSupportedException` at the marshalling seam), declines to hook (with a diagnostic), or warns at
> startup. Nothing on this page causes a *silent* wrong value. The one seam where a silent mismatch could
> have slipped through — a mod compiled for patched proxies loaded over unpatched ones — is closed by a
> content-addressed marker that warns loud (Gap 3).

## What's green

- **All three capabilities** — hooking, natural typing, mod hosting — plus the REPL, and the
  escape-hatch faces (`Safe`/`Invoke`/`Probe`/`Introspect`/`Fields`) are built and validated **in-game**.
- **Both loaders at full parity.** As of this writing the in-game battery passes under BepInEx and
  MelonLoader identically — **95/95**. That count grows as cases are added; the battery's own run is ground
  truth, not this number.
- The v1→v2 engine regressions — delegate params, virtual container-param
  flip, the REPL, the `Fields`/`Introspect`/`Invoke` surface, dual-loader validation — are all
  **re-ported and green**. What remains below is coverage holes and un-built ergonomics, not regressions.

## Natural typing — unbridged shapes (all fail loud)

A shape that isn't flipped stays wrapper-typed; your mod either spells the `Il2CppSystem.*` type (compiles
and works) or hits a `NotSupportedException` at the seam.

**"Fail loud" needed a mechanism to stay true.** It held for shapes a pass *considered* and declined — but a
member no pass ever classified produced no flip, no defer, no log line, and simply did not appear. Three
holes lived there (a method-backed `Nullable` accessor, a static container setter, a static `Nullable`
setter); one of them emitted **invalid IL** rather than declining. `ResidualAudit` now runs after every pass
and names every member still wearing a type some family could have flipped, marking it a known deferral or
an **unexplained hole** — see [interop-patch (11)](../contribution/architecture/11-interop-patch.md). The
fixture asserts zero residual after a directory patch. One soft spot remains, recorded there: the audit's
`virtual` reason is coarse now that virtual returns *and* accessors both flip, so a virtual residual is
something to investigate rather than accept.

Open holes (Gap 2):

| Shape | Status | Detail |
|---|---|---|
| value-`Nullable` element **inside** a container (`List<Vec3?>`, `Dictionary<…, Loadout?>`) | **roadmap** | defers the whole container flip (empty il2cpp value-Nullable NREs on unbox) |
| generic-**method** container return/param | **roadmap** | generic-method *Task* returns flip; generic-method *container* return/param does not yet |
| delegate il2cpp→BCL (a method **returning** a delegate) | **by design, roadmap** | only the BCL→il2cpp direction is bridged — you pass a lambda *in*; you don't receive one out |
| delegate arity beyond `Action`≤8 / `Func`≤9 | **bounded** | beyond the bound a param stays wrapper-typed (safe, not silent) |
| managed `Task`/`Task<T>` hook return vs an **unflipped** il2cpp Task proxy | **fails loud at Discover** | used to be the one *deferred* failure (compiled, bound, threw `MissingMethodException` at the first live call); now rejected at mod load with the fix in the message — spell the il2cpp Task, or patch the interop |

## Natural typing — type IDENTITY is not flipped (silent, not loud)

Natural typing flips *signatures*; it does not flip *what a proxy's CLR type is*. An object reached through
a seam declared as a base type — a property/field return, a `Dictionary<K, Base>` value, a collection
element — materialises as a **`Base`-typed proxy even when the object is a `Derived`**, because
`Il2CppObjectPool.Get<T>` constructs the declared static `T`. So:

| Spelling | Interrogates | On a `Derived` behind a `Base` seam |
|---|---|---|
| `is` / `switch` / `as` / cast / `GetType()` | the CLR **wrapper** | **false** / base type |
| `TryCast<T>()` / `Cast<T>()` | the il2cpp **object** | correct |

**This is the one unbridged shape that fails SILENTLY** rather than loud — a `switch` over subtypes matches
no case and, without a `default`, does nothing at all. It is also asymmetric: code that constructs its own
objects gets exact proxies, so `is` works until the same code is handed an object that came from the game.

Today's rule is therefore uniform and total: **never trust `is` on an il2cpp proxy — always `TryCast`.**
Closing it is specced in [exact-proxy-types.md](./exact-proxy-types.md) (not built); no battery case covers
the shape, which is why it went unrecorded this long.

The ergonomic `Proceed` call is contract-checked at dispatch (all directed errors, never a frame
corruption): arity is none-or-all (a partial arg list no longer silently mixes new and stale slots),
value-typed args must be the exact type (no raw bit-reinterpretation; enums accept their underlying
primitive), and `Proceed<R>`'s `R` must be the declared or the game return type (or a proxy base). Once a
body has called `Proceed`, a later throw can no longer double-run the original — the caller gets the
original's result and the warning log gets the exception.

## Metadata — wire-name recovery

- **Depends on the game not stripping attribute metadata.** Wire-name recovery reads the game's metadata
  offline; if a shipped game strips it, member-name remapping **degrades to member-name keys** (no wire
  remap) — a graceful fallback, never a hard failure, but outside anyone's control once a game ships.
- **Checked wire shapes inherit that dependency.** `Json.ToNode`/`To<T>(shape)`
  ([guide 5](../guide/05-wire-json.md)) validates object-literal keys against the *recovered* wire members,
  so a type absent from the wiremap has nothing to check — it says so loudly rather than rejecting every key
  as a typo, and such a type is built from a JSON string instead.
- **The key check proves existence, not completeness.** It catches a member you *misspelled*; it cannot
  catch one you *omitted*, which stays at its default. A round-trip against the game's own serializer is
  still the way to verify a minted object's shape.

## Escape hatches

- **`Probe` proves *plausibly* live, not *provably* live.** It catches null / garbage / bad-class
  pointers; it does **not** catch use-after-free (the GC is non-moving, so freed memory often still
  validates).
- **`Safe` tolerates a caught fault, but the runtime may be tainted afterward.** It's a seatbelt for
  suspect territory and a diagnostic — report and restart, don't loop-retry a faulting call.
- **`Fields` misses are silent by design** (`false`/`default`, not an exception) — it's discovery code.
  Prefer the seamless typed member (`player.Health`) when the type is nameable at author time.

## The REPL

- **Sessions are not collectible.** Roslyn submission assemblies persist for the session's life (the hooks
  a session registers *are* removable). A `:reset` that reclaims memory is roadmap.
- **A line can't `await` a main-thread primitive** — it fails loud rather than deadlocking. Do async /
  per-frame work from a hook or coroutine.
- **Opt-in.** The loader doesn't open a REPL port; you start `ReplServer` from a mod/coremod. Loopback
  only.

## Packaging & build

- **A `pack` target exists; no *published* release yet.** `tools/pack.sh` produces a versioned bundle in
  `dist/<version>/` (both loader trees + the runnable patch CLI); what's left before a published release —
  the schema-marker version tie and a publish channel — is tracked in [packaging](./packaging.md). For now you
  run `pack.sh` (or build in place and deploy the DLLs beside your loader, [1. Setup](../guide/01-setup-first-mod.md)).
- **The native engine (`inutil_core.dll`) is cross-compiled** with the vendored mingw toolchain
  (`ninja -C native/build inutil_core`); see `native/` and `tools/wine/validate.sh` for the reference
  build. A prebuilt native binary distribution is roadmap.

## Where these get closed

Each hole above is a well-scoped contribution. A natural-typing family is one registration in
`managed/src/Inutil.Schema/Families.cs`; the flip-scope holes are extensions of the existing
`ParamFamily`/`ContainerFlip` machinery. See [the contribution workflow](../contribution/01-philosophy.md)
and [the roadmap](./roadmap.md) for the current next-step candidates.
