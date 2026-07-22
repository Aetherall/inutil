# InteropPatch — the IL-rewrite seam

*The offline half of natural typing. Read [the system map](../02-system-map.md) and [schema (10)](./10-schema.md)
first. It is the twin of [marshal (12)](./12-marshal.md): this makes natural types **compile**; that makes
them **work** at runtime. Both consume [schema (10)](./10-schema.md).*

## Job

`Inutil.InteropPatch` rewrites the Il2CppInterop proxy DLLs *on disk*, in place, so their method signatures
speak natural BCL types (`Task<T>`, `int?`, `List<T>`, `Action`) where the raw proxy spelled
`Il2CppSystem.*`. It runs **offline** — one CLI pass (`inutil-interoppatch`) after install and after each
game update — and consumes the [schema (10)](./10-schema.md) registry so it flips exactly the families the
runtime marshaller can produce, and no others.

It is the reason a mod *compiles* against `List<int>`. Turning that compiled call into a working one at
runtime is [marshal (12)](./12-marshal.md)'s job; the two are twins over one registry.

## In the tree

`managed/src/Inutil.InteropPatch/` (the engine, Mono.Cecil-based) + `managed/src/Inutil.InteropPatch.Cli/`
(the `inutil-interoppatch` executable).

| File(s) | Holds |
|---|---|
| `PatchDirectory.cs` | **`InteropPatcher.PatchDirectory`** — the directory-level apply driver + `DirectoryPatchResult` |
| `CecilProjection.cs`, `CecilTypeRef.cs`, `CecilMemberRef.cs` | project Mono.Cecil defs onto the pure schema `ISlotType`/`ISlotMethod` |
| `TaskFamily.cs`, `ContainerFamily.cs`, `NullableFamily.cs`, `ParamFamily.cs`, `DelegateFamily.cs` | the families, each an `IFamilyPass` |
| `ParamFlipResolver.cs` | the ONE param-flip detection (container / value-Nullable / delegate) |
| `ParamFlip.cs`, `ContainerFlip.cs`, `WrapHelpers.cs`, `ReturnTail.cs` | the splice mechanisms |
| `*Rewriter.cs` (`TaskProxyRewriter`, `ContainerReturnRewriter`, `NullableReturnRewriter`, `ParamRewriter`) | the per-pass IL emission |
| `GameLayout.cs` | **`GameLocator.Locate`** — the `--game` loader-layout locator |

## Design

**A directory-level driver, not per-DLL — by design.** `InteropPatcher.PatchDirectory` patches *every*
proxy module in the interop dir in one pass. That's not a convenience; it's what makes the planner's
cross-module reasoning *sound*. A cross-module virtual Task slot must flip in **lockstep**: flipping
`Assembly-CSharp`'s `Backend<T>::OpenSession` override requires `ToyGame.Core`'s `BackendBase<T>::OpenSession`
root to flip too, or the half-flipped proxy fails to load (v1's exact frozen-on-disk bug). The planner's
cross-module gate *trusts* the sibling module will be patched; the directory driver keeps that promise.

**Atomic apply.** Each changed module is written to a temp file in the same directory and `rename(2)`'d over
the original — a mid-write failure never leaves a torn proxy (retires v1's in-place truncate-and-rewrite).

**The pure planner does the reasoning; families supply judgment.** The shared `VirtualSlotPlanner` (in
[schema (10)](./10-schema.md)) owns slot grouping, the framework-root gate, and all-or-nothing consistency.
Each family is an `IFamilyPass` that supplies only two things: `PlanMember` (*what does this member flip to,
and can its body take the flip*) and `Apply` (splice it). `CecilProjector` adapts real Cecil methods to the
planner's `ISlotType`/`ISlotMethod` interface — so the planner reasons about a real proxy through the *same*
interface it reasons about synthetic test shapes, and the rules live in the pure engine, not the seam.

**One param engine, not three.** `ParamFamily` + `ParamFlipResolver` + `ParamFlip.Splice` are a single
param-flip mechanism spanning all three param families (container, value-Nullable, delegate), driven through
the *same* `VirtualSlotPlanner` as the return families. `ParamFlipResolver` is the one place that answers "is
this param flippable, and by which converter" — adding a param family is one arm there, never a fourth
rewriter.

**The framework-root gate.** An override of a `UnityEngine` / `Il2Cppmscorlib` virtual must **not** flip —
we can't flip the framework base to match — so `ResolveSlot` roots it to the framework decl and defers the
whole slot. `CecilProjector.IsFrameworkAssembly` is the discriminator (it strips an optional `Il2Cpp` prefix,
so MelonLoader's `Il2CppToyGame.Core` game module isn't mistaken for framework — a fix pinned by
`FrameworkAssemblyTests`).

**Idempotent.** A flipped param stops being a candidate (`ParamFlipResolver` returns null for a `System.*`
type), so re-running the patch is a clean no-op — no "already flipped" bookkeeping needed on the param path.

**The marker.** After patching, the driver stamps `SchemaMarker.Hash(Families.Default())` into the dir
(`DirectoryPatchResult.SchemaHash`). Because every rewriter builds from that one registry, the hash is the
honest content-address of the patch — which is how the runtime detects proxies patched by a *different*
schema (see Invariants).

**The `--game` locator.** `GameLocator.Locate` is pure path logic (no Cecil, no game) that detects the
loader layout and locates the shared inputs (`InteropDir`, and `GameAssembly.dll` / `global-metadata.dat`
for [metadata (16)](./16-metadata.md)'s extract stage, which hangs off the *same* located inputs).

## Invariants

- **Consumes the schema registry; owns no family knowledge (P1).** The family name pairs come from
  `Families.Default()` via `ByConvKind`, not inline constants (C1) — so the flip roster can't drift from
  what the runtime marshaller produces.
- **Cross-module slots flip all-or-nothing.** The directory driver + the planner's gate guarantee a slot
  never half-flips across modules — the load-time failure that guarantee prevents is the frozen-on-disk bug.
- **Never a torn proxy.** Atomic temp-write + rename; a failed patch leaves the original intact.
- **Framework proxies never flip.** The framework-root gate defers rather than flip a base it can't rewrite.
- **The stamp is the patch's content-address.** `LoaderShim.WarnIfInteropUnpatched` reads it at startup and
  warns loud on `Missing`/`Stale` — closing the one seam where a mod compiled for flipped proxies could load
  over unpatched ones silently ([schema (10)](./10-schema.md) markers; the runtime read is in
  [loaders (19)](./19-loaders.md)).

## Limits, defers & TODOs

All fail loud — an unflipped proxy stays wrapper-typed, so a mod either spells the `Il2CppSystem.*` type
(compiles) or hits a `NotSupportedException` at the runtime seam. Open holes ([Gap 2](../../reference/limits.md)):

- **A value-`Nullable` element inside a container** (`List<Vec3?>`, `Dictionary<…, Loadout?>`) defers the
  *whole* container flip (`ContainerFlip.cs`) — an empty il2cpp value-Nullable would NRE on unbox. *Roadmap.*
- **Generic-method container returns/params** are the deferred "v17" case (`ContainerFamily.cs`, `TaskFamily.cs`)
  — the generic-method *Task* return flips; the generic-method *container* return/param does not yet. *Roadmap.*
- **Deferred slots are correct, not incomplete** — a framework root or an unprovable cross-module slot is
  *fail-safe*. Don't try to force a flip the planner declined.

## Tests

- **Offline** (`managed/src/Inutil.InteropPatch.Tests/`): `GameLocatorTests` (synthetic dir trees),
  `CecilProjectionTests` (the projection + planner over real Cecil shapes), `FrameworkAssemblyTests` (the
  framework discriminator, both loader spellings). The CLI runs offline GREEN and the marker round-trips in
  `MarkerTests` ([schema (10)](./10-schema.md)).
- **In-game**: the actual flips are proven by the battery's Task / container / Nullable / param / delegate
  cases under both loaders (e.g. `param.container.virtual.flip.runs` — an override dispatched through the
  flipped vtable slot). See [testing (20)](./20-testing.md).

## Why it's shaped this way

v1 flipped proxies through per-family `PatchXXX` passes with an in-place truncate-and-rewrite that could
leave a torn proxy on failure, and its cross-module virtual-slot handling (the v15 evolution) was the source
of the frozen-on-disk half-flip. Three shapes here retire that class of bug at once: the **directory-level
atomic driver** (lockstep + never torn), the **shared `VirtualSlotPlanner` via `IFamilyPass`** (one slot-walk
for every family, virtual and non-virtual), and the **unified `ParamFamily`** (one param engine, not three
rewriters to keep in sync). And because the family facts come from [schema (10)](./10-schema.md), this seam
*cannot* flip something the runtime marshaller can't materialize — the twin property that makes "two seams,
one registry" real instead of aspirational.
