# Schema — the correspondence registry

*The heart of the system. Read [the system map](../02-system-map.md) first. Consumed by
[interop-patch (11)](./11-interop-patch.md) and [marshal (12)](./12-marshal.md), and referenced by
[hook-ergonomics (14)](./14-hook-ergonomics.md).*

## Job

`Inutil.Schema` is the single object that answers *"how does il2cpp type X correspond to BCL type Y?"* —
and nothing else. It owns the roster of natural-typing families (Task, List, Dictionary, Nullable,
ValueTuple, delegates, …), the structural shape each reduces to, the direction each can be bridged
faithfully, and the recursive element-conversion tree a value marshals through. It owns **no** conversion
*machinery*: no Cecil IL emission, no Il2CppInterop calls. It is pure data + classification, so both seams
build their tables from it and neither can drift from the other.

If you remember one sentence: **a family is *data* here (a shape + a bridge + a direction), not code** —
which is why adding one is a registration, not a new switch arm in two places.

## In the tree

`managed/src/Inutil.Schema/` — deliberately dependency-light (no Il2CppInterop, no Mono.Cecil), which is
what makes it unit-testable in isolation.

| File | Holds |
|---|---|
| `Families.cs` | **`Families.Default()`** — the ONE registration site; every family's facts, in one place |
| `Correspondence.cs` | `TypeCorrespondence` (the per-family record), `IBridge`, `BridgeShape`, `Directionality` |
| `Conv.cs` | `ConvKind`, the recursive `Conv` tree, `ConvShape`, `IConvShapeSource`, `IIl2CppAnchors` |
| `CorrespondenceRegistry.cs` | the registry both seams consume (`Classify` / `ByConvKind` / `ByBclOpenType`) |
| `VirtualSlotPlanner.cs`, `SlotModel.cs`, `SlotPlan.cs` | the shared virtual/vtable-slot planning every family flows through |
| `ContentMarker.cs`, `SchemaMarker.cs` | the content-addressed hash of the registry (the interop / wiremap markers) |
| `WireRegistry.cs`, `WireCorrespondence.cs`, `WireFamilies.cs` | the wire-name side of the same idea (for [metadata (16)](./16-metadata.md)) |

## Design

**One record per family — `TypeCorrespondence`.** It carries the il2cpp anchor name (the open element name,
e.g. `Il2CppSystem…Task\`1`, resolved *reflectively* — never hard-bound at compile time), the open BCL
counterpart (`typeof(Task<>)`), the `ConvKind` (the runtime join key), a `WriteTarget` flag, and an
`IBridge`. Both seams read the same record; neither has its own notion of "family."

**Families are declared once, in `Families.Default()`.** That method builds the registry — one `Register`
call per family. The four hand-kept tables v1 had (the IL flip map, the Task family, the container bridge,
the shape source) collapsed to *lookups* against this registry, so a family's facts exist in exactly one
place. Adding `IList<T>` is one row here + one `IBridge`; both seams pick it up with no other edit. (That
promise is the whole point of the module — see **Why**.)

**Three structural shapes, not per-type code** (`BridgeShape`):

- `ValueLayout` — `Nullable<T>`, `ValueTuple<…>`: a derived generic layout/field copy.
- `ReferenceBespoke` — `Task<T>`, `List<T>`, `Dictionary<K,V>`: a hand-written semantic bridge, bounded in count.
- `ContainerAdapter` — `IEnumerable<T>`, `IReadOnlyList<T>`, arrays: the container bridge ([12](./12-marshal.md)).

The shape tells a seam *which shared machinery to use*. That's the mechanism by which a new family is data:
it names a shape, and the shape already has an implementation.

**Direction is a first-class fact** (`Directionality`): `Il2CppToBcl` (a `Task` result you receive),
`BclToIl2Cpp` (a delegate you pass *into* the game — the one-way family), or `Both` (containers, Nullable,
tuple). A family that reaches a seam in a direction it doesn't support fails loud rather than guessing.

**The `Conv` tree** is the runtime seam's recursive element-converter, and it's here (not in the marshaller)
because its *structure* is shared, unit-testable data. Three properties the shape guarantees that a single
threaded `inner` converter could not:

- **multi-child** — a node holds its own children (1 for Array/List/Task/Nullable, 2 for Dictionary, N for
  ValueTuple); a single `inner` can't express Dict's two or a tuple's N.
- **hook-driven recursion** — the tree is built from the *managed (hook-spelled)* type, because the game's
  il2cpp element is always already native; recursion keys on what the mod spelled (`Task<Player[]>` →
  recurse `Player[]`).
- **no completed/pending drift** — one node serves *both* directions, so there's no second copy of "how do
  I convert this shape" to fall out of sync (the exact v1 `Facade` completed-vs-pending Task bug).

**`CanBind` is always a probe, never a hard bind.** A family inspects the *runtime/rewrite-time* closed
type and decides whether it binds — it never asserts a compile-time il2cpp type name against a game. This is
the honest response to Il2CppInterop stripping/version drift: the probe is the mitigation.

**The `VirtualSlotPlanner`** is the one place virtual/vtable/interface slot planning lives; every family that
touches virtual slots flows through it rather than re-deriving the walk. A slot it cannot prove safe to flip
is *deferred* (`IsDeferred`/`DeferReason`) — a fail-safe, not an unfinished path.

**The markers** (`SchemaMarker`, `ContentMarker`) hash the registry into a content address stamped onto the
patched proxy dir (and the wiremap sidecar). Because every rewriter builds from this one registry, hashing
it is the honest content-address of the patch — which is how the runtime detects "compiled for a different
schema than these proxies were patched with" ([interop-patch (11)](./11-interop-patch.md)).

## Invariants

- **Single source of truth (philosophy §1 / spec P1).** Every family fact lives in `Families.cs` and
  nowhere else; both seams reach it by lookup. This is the invariant the whole module exists to hold.
- **`Families.cs` is the only file that may spell an `Il2CppSystem.*` family name** (the C4 guardrail).
  Enforced by `RegistryDriftTest` / `WireRegistryDriftTest`, which fail if the string appears elsewhere.
- **The registry drives *both* the read (BCL→ConvKind, shape) and the write (ConvKind→il2cpp name,
  container bridge) direction** — `ByConvKind` returns the write-target row, so a read-only spelling
  (`IReadOnlyList`) classifies to a sequence yet materializes into the concrete `List`. One column
  (`WriteTarget`) keeps that exact.
- **Purity is load-bearing, not incidental.** Keeping Il2CppInterop and Cecil *out* is what lets the whole
  planning/classification layer be proven against synthetic type shapes before any real proxy exists.

## Limits, defers & TODOs

- **A family must reduce to a shape + bridge + direction.** If a proposed family needs genuinely per-type
  conversion code, it doesn't belong here as-is — that's the signal to either find its shape or extend a
  `BridgeShape`, not to special-case it. (This is a constraint, not a bug.)
- **The ABI-sizing carve-out is deliberately *not* here.** Mapping a patch-erased `Nullable` back to its
  il2cpp class to size the Win64 boundary is an ABI concern that lives in the hook engine; `Inutil.Schema`
  is Il2CppInterop-free and structurally cannot hold a `typeof(Il2CppSystem.*)`. The two are *tied by a
  cross-check test* rather than merged (the guardrail can't scan a `typeof`) — see
  [hook-engine (13)](./13-hook-engine.md).
- **Deferred slots are correct, not incomplete.** `IsDeferred`/`DeferReason` means the planner refused an
  unsafe flip; tested per-reason in `PlannerTests`. Don't mistake a defer for a gap.
- **Open-generic-parameter members** are never hard-bound (`CanBind` returns false when an argument is an
  open parameter) — that case is the slot planner's concern, handled at the seam.
- Cross-family limits that surface *through* the schema (e.g. value-`Nullable` inside a container, the
  generic-method container flip) are tracked at the seam that defers them — see
  [reference/limits.md](../../reference/limits.md).

## Tests

`managed/src/Inutil.Schema.Tests/` — all offline, no game boot (that's the payoff of the module's purity):

- `FamiliesTests`, `ClassifyTests`, `WireClassifyTests` — the registry binds the right shapes.
- `ConvTests` — the recursive tree over synthetic shapes (multi-child, both directions).
- `PlannerTests` — `VirtualSlotPlanner`, including each defer reason.
- `CorrespondenceValidatorTests` — the runtime-boundary validator.
- `RegistryDriftTest`, `WireRegistryDriftTest` — the C4 guardrail: no family string outside `Families.cs`.
- `MarkerTests` — hash determinism / order-independence / Current-Stale-Missing.
- `RuntimeIsolationTest` — proves the module stays free of the heavy deps.

## Why it's shaped this way

In v1 the notion of "family" was **spread across two implementations that agreed by discipline**: the
runtime marshaller's `Kind` enum and the IL rewriter's per-family `PatchXXX` passes. They drifted — the
confirmed Nullable/Enumerable virtual-slot bugs were exactly a case where one side knew something the other
didn't. `Inutil.Schema` exists to make that drift *structurally impossible*: put the shared fact in one
object both sides consume, and there's no second copy to fall out of sync.

The discipline was hard to land and nearly slipped back (the C1–C7 course-correction): building the shared
registry is not the same as *routing every consumer through it*, and a guardrail that greps the symptom is
blind to the same fact in a new seam. That lesson is [philosophy §6](../01-philosophy.md) — and this module
is where it's most concentrated, because this is the object every other component agrees through. Change how
a family is declared here and you are touching the one load-bearing convergence in the system; do it with
the guardrail tests, not by hand.
