# Marshal — the runtime marshaller seam

*The runtime half of natural typing. Read [the system map](../02-system-map.md) and [schema (10)](./10-schema.md)
first. It is the twin of [interop-patch (11)](./11-interop-patch.md): that makes natural types **compile**;
this makes them **work** — at the hook boundary, in a live il2cpp frame.*

## Job

`Inutil.Marshal` converts a value between its natural BCL form and its il2cpp representation, at the moment a
hooked call crosses the boundary (and wherever a mod explicitly asks — `.ToManaged()`/`.ToIl2Cpp()`). It is
where a `List<int>` a mod spelled becomes a real il2cpp list on the way in, and an il2cpp list becomes a real
`List<int>` on the way out. It does this by driving the value through the *schema's* `Conv` tree — it owns the
il2cpp **work**, never the conversion **structure**.

## In the tree

`managed/src/Inutil/Marshal/` (in the SDK, because it touches Il2CppInterop):

| File | Holds |
|---|---|
| `Il2CppMarshal.cs` | the high-level face — build the `Conv` tree for a spelled type once, cache it, drive a value through it |
| `Il2CppConvRuntime.cs` | `IConvRuntime` — the per-kind il2cpp work (the seam half of the `Conv` tree) |
| `Il2CppConvShapeSource.cs` | the shape classifier that adds il2cpp *leaves* to the pure BCL classifier |
| `ContainerBridge.cs` | one materialize/dematerialize per container kind (array/list/dict/set/tuple/nullable/task) |
| `ValueTypeBridge.cs` | the §7.5 value-type bridge — the ONE ref-bearing-value-struct primitive |
| `ReflectionTypeRef.cs` | the reflection `ITypeRef` the classifier reasons over |

## Design

**One entry, one tree per type.** `Il2CppMarshal` builds the `Conv` tree for a spelled managed type once,
caches it, and drives values through it with the real il2cpp runtime — `ToManaged`/`ToIl2Cpp`,
`PointerToManaged`, and the frame-value pair `FrameValueToManaged`/`ManagedToFrameValue` a hook boundary
uses. The `Conv` *composition* is proven offline ([schema (10)](./10-schema.md) tests against a synthetic
runtime); this binds it to il2cpp.

**Structure vs work — the clean split.** `Conv` (schema) owns *which shape, which children, both directions*;
`Il2CppConvRuntime` owns *the per-kind il2cpp operation*. One `Conv` node serves **both** directions, so
there is no second copy of "how do I convert this shape" to fall out of sync — retiring v1's `Facade`
completed-vs-pending Task bug at the structural level.

**The classifier adds il2cpp leaves.** `Il2CppConvShapeSource` extends the pure BCL classifier so recursion
bottoms out correctly: a directly-spelled game reference proxy (`Il2CppObjectBase`-derived) or a ref-bearing
value proxy (`Il2CppSystem.ValueType`-derived) is a **leaf** — passed by identity or bridged whole — never
recursed as a container even if it implements one. A natural BCL delegate classifies as `ConvKind.Delegate`
so it **fails loud** in the runtime rather than silently identity-passing (the delegate family is
`BclToIl2Cpp`-only; there's no reverse bridge).

**`ContainerBridge` — one loop per kind, parameterized by the child `Conv`.** Instead of v1's hand-rolled
per-kind loops (`WrapReferenceArray`/`WrapStructArray`/`WrapStringArray` + four independent enumeration
paths), there's one generic materialization per container kind, and element conversion recurses through
`node.Children[i]`. So a nested spelling — `Player[][]`, a `List<int[]>`, an `int[]` inside a `Task` —
marshals *by construction*, not by a second copy of the loop. Written against the plain sequence surfaces
il2cpp containers expose, so the loop **logic** is exercised offline; only the element types differ in-game.

**`MutableList` — the write-through sequence spelling (the v1 `MutableListM` re-port).** Every family above
materializes a **copy** at the boundary; a hook that must *mutate the game's list* (a pool the callee shrinks
per pick) spells `IList<T>` instead and gets `Il2CppMutableListAdapter<T>` — a live view holding the game's
il2cpp `List<T'>` and forwarding **every op** to it through the proxy's own members, converting the element
per-op through the child `Conv`. Its `ToIl2Cpp` is **identity** (the `IIl2CppListCarrier` seam hands the held
list back), so `Proceed(pool, …)` runs the original on the game's *own* list; a mod-fabricated `IList` (no
carrier) dematerializes into a fresh `List`1` like any sequence. The row shares the `List`1` anchor with the
`List` family (one il2cpp type, two spellings) and registers **after** it, so `Classify` — and therefore the
[interop-patch (11)](./11-interop-patch.md) flip — is untouched; `IList<T>` is a hook-boundary spelling only.
Equality ops (`Remove`/`IndexOf`/`Contains`) forward to the proxy's own methods (the game's comparer decides
membership); a ref-bearing value-type element takes `InvokeUnboxed` on the void writes and **fails loud** on
the equality ops (§7.5 has no faithful unboxed equality boundary).

**`ValueTypeBridge` — the §7.5 primitive.** A ref-bearing il2cpp value type (a struct with a reference field,
`Loadout { int Gold; string Owner }`) crossing into a container field slot must have its **unboxed** bytes
written through the il2cpp GC **write barrier** — not the boxed-proxy pointer Il2CppInterop's generic
`ctor`/`set_Item`/`Add` hands over (the naive path drops the string and reads a garbage int, verified
in-game). This is centralized in one primitive (`BoxFrameValue`/`UnboxToFrame`/`WriteField`/`InvokeUnboxed`),
and its discriminator `IsRefBearingValueProxy` is **the single spelling** of `"Il2CppSystem.ValueType"` in the
tree — the classifier here and the [hook engine (13)](./13-hook-engine.md)'s ABI sizing both call it rather
than re-spelling it (C6).

## Invariants

- **Consumes the schema `Conv` tree; owns no conversion structure (P1).** There is no second "how to convert
  this shape" — the tree is the one definition, both directions.
- **Every value-type write site routes through `ValueTypeBridge` or fails loud (P3 / C3 / C5).** The
  invariant v1 re-learned three times (TaskM, Sugar's dict helpers, Mirror) has exactly one implementation;
  an unwired site throws rather than silently corrupting.
- **`"Il2CppSystem.ValueType"` appears in exactly one file** (`ValueTypeBridge`) — every other site calls the
  shared discriminator (C6; enforced by `RegistryDriftTest`).
- **Anything the engine can't marshal fails loud from the engine** (`Conv.Build` / the correspondence), never
  a wild pointer read at the boundary — so an unknown or unbridged shape surfaces a clear error.
- **A delegate reaching the runtime fails loud.** The family is one-way; a hook spelling `System.Action` for
  a flipped param that somehow reaches the runtime gets a precise error, never a mis-marshal.

## Limits, defers & TODOs

The read+write matrix for the shipped families is complete and GREEN under both loaders. The edges:

- **The delegate direction is one-way** — `BclToIl2Cpp` only (you pass a lambda *into* the game). A method
  *returning* a delegate stays wrapper-typed; the runtime `Delegate` node fails loud rather than inventing a
  reverse bridge. *By design; see [reference/limits.md](../../reference/limits.md).*
- **`Nullable<ref-bearing-value-struct>` is unreachable by construction, not a gap.** CLR `Nullable<T>`
  requires `T : struct`, and a ref-bearing il2cpp value type renders as a *class* proxy — so the shape can be
  neither spelled nor type-loaded. `DematerializeNullable`'s ref-bearing branch is a deliberate backstop for a
  shape the type system forbids (verified in [reference/limits.md](../../reference/limits.md)), not unfinished work. The tuple/dict/list ref-bearing
  *element* writes are real and done (they route through `ValueTypeBridge.WriteField`).

## Tests

- **Offline** ([schema (10)](./10-schema.md) tests): the `Conv` composition — multi-child, both directions —
  is proven against a synthetic runtime, so the *structure* is verified before this seam runs in a game.
- **In-game**: the il2cpp binding is proven by the battery under both loaders — the Task / container /
  value-type / Nullable cases, and `ValueTypeBridge` specifically by
  `valuetype.{tuple-element,dict-value,list-element}.write`. See [testing (20)](./20-testing.md).

## Why it's shaped this way

v1's `Facade` carried three duplications that each shipped a bug: two copies of "how to convert a Task"
(the completed-vs-pending drift), hand-rolled per-kind container loops, and the value-type write-barrier
invariant re-derived at three sites. This seam retires all three by *not owning structure*: one `Conv` tree
(both directions, one node) from [schema (10)](./10-schema.md), one `ContainerBridge` loop per kind (recursing
through children), one `ValueTypeBridge`. It is the runtime twin of [interop-patch (11)](./11-interop-patch.md)
— they consume the same registry, so what compiles is exactly what marshals, and neither can drift from the
other.
