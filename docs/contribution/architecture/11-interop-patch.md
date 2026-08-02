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
| `TaskFamily.cs`, `ContainerFamily.cs`, `NullableFamily.cs`, `NullableAccessorFamily.cs`, `ParamFamily.cs`, `DelegateFamily.cs` | the families, each an `IFamilyPass` |
| `ParamFlipResolver.cs` | the ONE param-flip detection (container / value-Nullable / delegate) |
| `ParamFlip.cs`, `ContainerFlip.cs`, `WrapHelpers.cs`, `ReturnTail.cs` | the splice mechanisms — incl. `ParamFlip.LoadsParam`, the ONE "which `ldarg` is this param" rule |
| `*Rewriter.cs` (`TaskProxyRewriter`, `ContainerReturnRewriter`, `NullableReturnRewriter`, `NullableFieldRewriter`, `ContainerFieldRewriter`, `ArrayRewriter`, `ParamRewriter`) | the per-pass IL emission |
| `ResidualAudit.cs` | the post-patch audit — what is STILL wearing a naturalizable il2cpp type, and whether there's a known reason |
| `WireAttributeRewriter.cs` | re-attaches recovered wire names as `[JsonPropertyName]` ([metadata (16)](./16-metadata.md)) |
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

## The accessor pass

`NullableFieldRewriter` owns **every** `Nullable<T>` property, because the return/param families exclude
accessors by design — so it is the only thing standing between a Nullable member and a silently-unflipped
proxy. The boundary it dispatches on is the accessor's **body shape**, not "field vs property":

| Backing | Getter | Setter |
|---|---|---|
| **field-backed** (`<X>k__BackingField`; body loads `NativeFieldInfoPtr`) | tail-swap the broken `newobj Nullable<T>(ptr)` for the null-aware read | rebuild the body around `WriteNullableField`/`…RefField` (instance) or `WriteNullable[Ref]StaticField` (static) |
| **method-backed** (a real property; body loads `NativeMethodInfoPtr` + `il2cpp_runtime_invoke`) | the SAME tail-swap — `runtime_invoke` boxes a Nullable return exactly as the field box does | the ordinary **param flip** (`ParamFlipResolver` + `ParamFlip.Splice`) |

An **auto-property yields BOTH**: a field-backed pair over the backing field and a method-backed pair for the
property itself, one storage, two proxy members. Missing that duality is what left `ItemTemplate::ParentId`
unflipped in a real game while its backing field flipped, so the patch log looked busy and correct.

**Virtual** accessors (always method-backed — interop's field wrappers are never virtual) go through the
shared `VirtualSlotPlanner`, plus a coupling step: a property is *three* things that must agree (`get_`,
`set_`, the property's own type) across every type in the override graph, and `get_X`/`set_X` are **separate
slots**. So the property flips only if **both** its accessor slots flip. Flipping one alone yields a property
whose accessors disagree — which loads fine and misbehaves only when touched.

**Static** members differ in *both* operands: no object to write through, and the value is `ldarg.0` (the slot
holding `this` on an instance method). Getting that wrong is not a mis-optimisation but invalid IL — it
shipped that way once, leaving three static setters in a real game's proxies that would throw
`InvalidProgramException` on first call. `ParamFlip.LoadsParam` is now the one arg-index rule the container
pass shares, which is what closed the same blind spot there (529 static container properties, deferred
silently).

## Invariants

- **Nothing is left naturalizable-but-unflipped without being named.** `ResidualAudit` runs after every pass
  and reports each member still wearing a Nullable/container type an family *could* have flipped, attributed
  to a known deferral or flagged **unexplained** — a hole. Phrased against the fact, not against any pass's
  internals, so a new family/backing/renderer that slips through is caught without editing the check. It is a
  *report*, not a gate; `Unexplained` is the number to act on. Every hole in this pass's history (method-backed
  accessor, static container setter, static Nullable setter) was invisible precisely because a pass can only
  report on what it *looked at*, and the fixture asserts zero residual after a directory patch.
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
- **`ResidualAudit`'s `virtual` reason is COARSE.** Virtual Nullable returns *and* virtual accessors both flip
  now, so "virtual" no longer explains a residual by itself — the only legitimate cases left are the planner's
  slot gates (external / framework root). Tightening it means resolving slot roots inside the audit (the
  planner's job). Until then a virtual residual count is something to *investigate*, not accept — the one
  place a real hole could still read as expected. *Roadmap.*

## Tests

- **Offline** (`managed/src/Inutil.InteropPatch.Tests/`): `GameLocatorTests` (synthetic dir trees),
  `CecilProjectionTests` (the projection + planner over real Cecil shapes), `FrameworkAssemblyTests` (the
  framework discriminator, both loader spellings). The CLI runs offline GREEN and the marker round-trips in
  `MarkerTests` ([schema (10)](./10-schema.md)).
- **The source proxies must be PRISTINE**, asserted via the schema marker — on already-flipped input the
  idempotency cases pass *vacuously* and negative assertions invert, so a patched source is a hard setup
  error (exit 2), never a red that reads like a code regression. `setup-bepinex` snapshots
  `interop.pristine` at generation time (the first `bepinex-validate` patches `interop/` in place), and both
  the suite and the `check` gate prefer that snapshot.
- **The residual audit is asserted in BOTH directions** — it must *see* the unflipped members before the
  pass runs and report none after. A check that only ever runs against the fixed state cannot tell "covered"
  from "never looked", which is the exact failure it exists to catch.
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
