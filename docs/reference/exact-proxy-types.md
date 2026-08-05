# Exact proxy types — make `is` / `switch` tell the truth

**Status:** **built.** Offline: `Inutil.InteropPatch/PoolRetargetRewriter.cs` + `ExactTypeExtract.cs`,
`Inutil.Schema/ExactTypeMap.cs`. Runtime: `Inutil/Marshal/Il2CppObjects.cs`. Proven in-game by the
battery's `exact.*` cases under both loaders.
**Specced:** 2026-08-04 · **Landed:** 2026-08-05
**Grounding:** a read of Il2CppInterop's `Il2CppObjectPool`, `Il2CppObjectBase.InitializerStore<T>`,
`Il2CppClassPointerStore<T>` and `IL2CPP.PointerToValueGeneric` (symbol sources, verified), plus live
measurement in a booted game. Claims below are marked **read** from source, **measured** in-game, or
**asserted** as design.

---

## The one-line problem

> An il2cpp object's **CLR proxy type was a fact about how you reached it**, not about what it is. A
> `Derived` object read through a seam declared `Base` arrived as a `Base`-typed proxy, so `is Derived`
> was **false** — and a C# `switch` over subtypes silently matched nothing.

This was the last big natural-typing leak. Natural typing already makes a proxy's *signatures* speak BCL
(`List<int>`, `Task<T>`, `int?`) instead of `Il2CppSystem.*` ([guide/03](../guide/03-natural-typing.md)). Type
*identity* was still il2cpp-shaped: the modder had to know to write `TryCast<T>()` and had to never write
the C# they'd write against the real game. That is exactly the leak natural typing exists to close.

---

## What used to happen (read)

Generated proxies materialise objects through **two** primitives — and that "two" is the whole story of
this change, so it is stated first:

```
Il2CppObjectPool.Get<T>(ptr)                      <- a CONCRETELY-typed read (a property returning Entity)
  ├─ cls = il2cpp_object_get_class(ptr)      // read — but ONLY to test RuntimeSpecificsStore.IsInjected
  ├─ cache hit on ptr && cachedObject is T   → return it (else evict: pooledPtr = 0)
  └─ InitializerStore<T>.Initializer(ptr)    // constructs T — the DECLARED static type

IL2CPP.PointerToValueGeneric<T>(ptr, …)           <- a read whose element type is a generic PARAMETER
  └─ … box/deref/string rules … → Il2CppObjectPool.Get<T>(ptr)      // the pool, one frame deeper
```

`InitializerStore<T>.Create()` builds its DynamicMethod against
`Il2CppClassPointerStore<T>.CreatedTypeRedirect ?? typeof(T)`. The redirect is **per-`T`**, a static
generic slot — it can express "whenever anyone asks for `Base`, build `X`", but not "build whatever this
pointer actually is". So the real class was read and discarded, and there is **no runtime install point**;
`InitializerStore` is `internal`.

Two consequences worth stating because they are easy to guess wrong:

- **`Cast<T>` / `TryCast<T>` bypass the pool.** `Il2CppObjectBase.Cast<T>` calls
  `InitializerStore<T>.Initializer(Pointer)` directly, so a TryCast neither reads nor populates the
  pointer cache. (Read. This is why casting an object does *not* make later base-typed reads of the same
  pointer come back exact — measured, and initially predicted wrong.)
- **The cache is keyed on the pointer alone**, and satisfies a request when `cachedObject is T`. So
  whichever declared type materialised a pointer *first* was what everyone downstream got.

## What it looked like from a mod (measured, before the fix)

```
seam                          CLR type      il2cpp runtime type
property returning Base       Base          Derived
field/property on an object   Base          Derived
Dictionary<K, Base> value     Base          Derived
```

and on the same object:

```
  is Derived        = False        // CLR test — interrogates the wrapper
  TryCast<Derived>  != null        // il2cpp test — interrogates the object
```

**Why it bit so hard.** The failure was silent and asymmetric. Code that *constructs* its own objects
(`Activator.CreateInstance(derivedType)`) gets an exact proxy, so `is`/`switch` works and looks correct —
it kept working right up until the same code was handed an object that arrived from the game. Then every
`case` fell through, a `switch` with no `default` did nothing, and the caller reported success.

---

## What was built

**1 — the identity map, at patch time** (`ExactTypeExtract` → `ExactTypeMap`, written as
`inutil.typemap` beside the patched proxies). The runtime question is "given this object's native class,
which proxy type *is* it?", and Il2CppInterop can only answer the inverse: `Il2CppClassPointerStore<T>.
NativeClassPtr` is filled by running `T`'s static ctor, so inverting it in-process would mean forcing the
static ctor of every candidate proxy — each of which resolves every field and method pointer of its type.
The patcher does not have to pay that: it is already reading every proxy module with Cecil, and each
proxy's cctor opens by resolving its own class from a literal triple.

```
ToyGame.Player::.cctor    ldstr "Assembly-CSharp.dll"; ldstr "ToyGame"; ldstr "Player"
                          call IL2CPP::GetIl2CppClass; stsfld Il2CppClassPointerStore<Player>::NativeClassPtr
Bootstrap/__c::.cctor     ldsfld Il2CppClassPointerStore<Bootstrap>::NativeClassPtr; ldstr "<>c"
                          call IL2CPP::GetIl2CppNestedType; stsfld …<__c>::NativeClassPtr
```

So the map is a **read of the generator's own decision**, never a reconstruction of its naming rules
(`System` → `Il2CppSystem`, `mscorlib` → `Il2Cppmscorlib`, `<>c` → `__c`) — which change with its version
and would be wrong *silently*. The nested arm gets the un-mangled name for free.

**2 — the retarget** (`PoolRetargetRewriter`). Both primitives above are memberref-retargeted to
`Inutil.Marshal.Il2CppObjects`, across **every** module in the interop directory. Mechanically the
simplest pass in the seam: no signature changes, no bodies rebuilt, no slot lockstep — the operand's
declaring type is swapped, and the generic argument, parameters and return type carry over verbatim.

**3 — the runtime** (`Il2CppObjects`). Resolve the object's real class → key → proxy type; use it only if
it is **assignable to the declared type**; otherwise delegate to the original, whose injected-type branch,
null handling and cache stay authoritative. Cost lands on a class it has not seen before: after that it is
one native class read and one `ConcurrentDictionary` hit, alongside the one the pool already pays. The
cache also gets strictly *more* effective — once a pointer is materialised at its exact type, every later
base-typed read satisfies `cachedObject is T` and reuses it, where before a base-typed first read pinned
the base proxy.

**4 — every other materialisation site.** Materialising a proxy from a pointer happens in more places
than the generated proxies: the hook boundary builds `Self`, its reference args and its return from raw
frame pointers; the marshaller recovers an object at a Conv leaf; the reach faces return one. Fixing only
the proxies would have left a mod's own hook receiving a base-typed `this` — the same bug through another
door. All of them route through `Il2CppObjects`, and an offline Cecil check over the built `Inutil.dll`
fails if any code outside it builds a proxy from a pointer (phrased against that fact, so a site added
later is caught without editing the check — it found two on the day it was written).

### The two-primitive lesson

The first in-game run had every property/field/`System.Object`/cross-module case green and both container
cases red. Retargeting the pool alone fixes concretely-typed reads and misses every read whose element
type is a generic parameter — `List<T>::get_Item`, an array indexer — because those call
`PointerToValueGeneric`, which reaches the pool *inside Il2CppInterop.Runtime*, where the patch does not
reach. The container element is also the rung that decides the pass cannot be game-scoped: that read runs
inside `Il2Cppmscorlib`'s own `List` proxy, the module every naturalizing family deliberately skips.

Hence: a **table** of primitives rather than a hard-coded name, and a postcondition (`Remaining`) phrased
over the whole table, so a third primitive surfaces as a residual instead of as a silent half-fix.

---

## Decisions (the spec's open questions, resolved)

- **Populating the index — settled by moving it offline.** No static ctors are forced, at boot or ever;
  the map is extracted with Cecil at patch time and read as a flat text file (~10^5 rows on a real game)
  on first use.
- **The fallback does NOT warn, deliberately.** Every unresolvable case — no map, an unmapped class, a
  generic instantiation, an injected type, a proxy assembly not yet loaded — falls back to exactly the old
  behaviour. There is no mis-marshal to protect against (the fallback *is* the status quo the whole
  codebase was written against), and a warning on the hottest path in interop is an outage, not a
  diagnostic. What *would* be silent-and-wrong is resolving to the **wrong** type, and that cannot happen:
  a resolution is used only when it is assignable to the declared type. `Il2CppObjects.Stats` (exact /
  declared / rejected / map size) and `Rejections` are how the question stays answerable — the battery
  asserts `rejected == 0`, which is the number that means "a map row named a type that is not a subtype
  of the seam it was resolved for".
- **`GetType()` equality flips.** `is` becoming more true is safe; `x.GetType() == typeof(Base)` goes from
  true to false for an object that is really a `Derived`. Rare, real, and in the changelog.
- **`TryCast` stays, as an escape hatch rather than the rule.** Both spellings keep working — exact typing
  only ever hands back a *subtype* of what was asked for — and `exact.trycast.still-works` pins that.

## Limits (stated, not hidden)

- **Generic instantiations are never exact.** `Container<Player>`'s native class presents the same
  (image, namespace, name) as the open `Container\`1`, so a row for it would resolve every instantiation to
  a type that cannot be materialised at all. Generic definitions are excluded from the map at both ends,
  and generic-typed reads fall back.
- **`Cast<T>` / `TryCast<T>` are not retargeted** — they are *explicit*: the caller named the type it
  wants. This pass fixes the implicit materialisations, where nobody chose a type at all.
- **An ambiguous il2cpp identity is dropped**, not guessed: two proxy types claiming one identity would
  make one of them a lie, and the file records the drop so "why is this type never exact?" has an answer
  on disk.
- **The BepInEx preloader's in-memory FALLBACK layer writes no map.** Its normal path patches on disk
  (`PatchDirectory`, which writes the map); if that window is lost to a locked file, the session gets
  natural typing without exact typing. Fail-safe, and the marker still reports the tree's state.

## Acceptance

Per [contribution/01-philosophy](../contribution/01-philosophy.md), proven on **ToyGame under both
loaders**. The fixture the spec called for now exists (`ToyGame.Game::Champion/Warden/Lineup/Warband/
Loot/OpenSessionEx`, rooted in `Bootstrap.Exercise`): a base/derived pair reachable through a
base-declared property, field, plain return, list element, dictionary value, `System.Object` seam, and a
cross-module derived type. Offline coverage is `ExactTypeTests`; in-game is `exact.*`.
