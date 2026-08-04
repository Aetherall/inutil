# Exact proxy types — make `is` / `switch` tell the truth

**Status:** feature request / design target, **not built.** No in-tree implementation to point at.
**Date:** 2026-08-04
**Grounding:** a read of Il2CppInterop's `Il2CppObjectPool`, `Il2CppObjectBase.InitializerStore<T>` and
`Il2CppClassPointerStore<T>` (symbol sources, verified), plus live measurement in a booted consumer of
proxy CLR types at property / field / collection seams. Every claim below marks whether it is **read**
from source, **measured** in-game, or **asserted** as design.

---

## The one-line problem

> An il2cpp object's **CLR proxy type is a fact about how you reached it**, not about what it is. A
> `Derived` object read through a seam declared `Base` arrives as a `Base`-typed proxy, so `is Derived`
> is **false** — and a C# `switch` over subtypes silently matches nothing.

This is the last big natural-typing leak. Natural typing already makes a proxy's *signatures* speak BCL
(`List<int>`, `Task<T>`, `int?`) instead of `Il2CppSystem.*` ([guide/03](../guide/03-natural-typing.md)).
Type *identity* is still il2cpp-shaped: the modder must know to write `TryCast<T>()` and must never write
the C# they'd write against the real game. That is exactly the leak natural typing exists to close.

---

## What actually happens (read)

Generated proxies materialise objects through **one** primitive:

```
Il2CppObjectPool.Get<T>(ptr)
  ├─ cls = il2cpp_object_get_class(ptr)      // read — but ONLY to test RuntimeSpecificsStore.IsInjected
  ├─ cache hit on ptr && cachedObject is T   → return it (else evict: pooledPtr = 0)
  └─ InitializerStore<T>.Initializer(ptr)    // constructs T — the DECLARED static type
```

`InitializerStore<T>.Create()` builds its DynamicMethod against
`Il2CppClassPointerStore<T>.CreatedTypeRedirect ?? typeof(T)`. The redirect is **per-`T`**, a static
generic slot — it can express "whenever anyone asks for `Base`, build `X`", but not "build whatever this
pointer actually is". So the real class is read and discarded, and there is **no runtime install point**;
`InitializerStore` is `internal`.

Two consequences worth stating because they are easy to guess wrong:

- **`Cast<T>` / `TryCast<T>` bypass the pool.** `Il2CppObjectBase.Cast<T>` calls
  `InitializerStore<T>.Initializer(Pointer)` directly, so a TryCast neither reads nor populates the
  pointer cache. (Read. This is why casting an object does *not* make later base-typed reads of the same
  pointer come back exact — measured, and initially predicted wrong.)
- **The cache is keyed on the pointer alone**, and satisfies a request when `cachedObject is T`. So
  whichever declared type materialises a pointer *first* is what everyone downstream gets.

## What it looks like from a mod (measured)

Observed live in a consumer's game — a type hierarchy where the map's declared value type is the base:

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

Repeat reads of one seam return the **same CLR instance** (`ReferenceEquals = True`), which confirms the
generated getters go through the pooled path above rather than a direct ctor. (Measured.)

**Why it bites so hard.** The failure is silent and asymmetric. Code that *constructs* its own objects
(`Activator.CreateInstance(derivedType)`) gets an exact proxy, so `is`/`switch` works and looks correct —
it keeps working right up until the same code is handed an object that arrived from the game. Then every
`case` falls through, a `switch` with no `default` does nothing, and the caller reports success. The
discovery case cost several generations of "it recompiled and changed nothing".

---

## Proposed change (asserted)

Both game proxies and framework proxies (the il2cpp collections) funnel through `Il2CppObjectPool.Get<T>`,
so this is a single choke point, not a per-seam rewrite:

1. **Patch pass** — retarget the `Il2CppObjectPool::Get<T>` **memberref** across the patched directory to
   inutil's own materialiser. Mechanically a memberref retarget: substantially simpler than the existing
   return families' ret-tail splicing (`ContainerFamily` and friends), and it inherits their idempotency
   and marker story.
2. **Runtime** — inutil's `Get<T>`:
   - preserve the injected-type branch **first**, unchanged;
   - resolve `il2cpp_object_get_class(ptr)` to a generated proxy `Type` via a reverse index;
   - if that type is assignable to `T`, materialise it; **otherwise delegate to the original** `Get<T>`.

The reverse index is buildable from public API: `Il2CppClassPointerStore<T>.NativeClassPtr` is a
`public static IntPtr`, with `Il2CppClassPointerStore.GetNativeClassPointer(Type)` as a reflective
accessor. (Read.)

**Cost is smaller than it looks.** The extra work lands on **cache miss only** — one native class read plus
one index lookup, alongside a `ConcurrentDictionary` hit the pool already pays. It also makes the cache
strictly *more* effective: once a pointer is materialised at its exact type, every later base-typed read
satisfies `cachedObject is T` and reuses it, where today a base-typed first read pins the base proxy.

---

## Open decisions

- **Populating the index.** `NativeClassPtr` is filled by each proxy type's static ctor, so eagerly walking
  every generated type to build the map would force static ctors across the whole surface — expensive, and
  a fresh failure mode. A lazy, populate-on-miss index (native class → proxy type by recovered name) is
  probably right, but the lookup path is the part to design, not hand-wave.
- **The fallback is where the rule still lies.** A class with no generated proxy (stripped, obfuscated, an
  odd generic instantiation) falls back to the declared type. Today's behaviour is uniformly wrong and
  therefore learnable; after this change it is right almost everywhere and wrong in a narrow set nobody can
  predict. Decide whether that case warns — a silent narrow exception may be worse than the status quo.
- **`GetType()` equality flips.** `is` becoming more true is safe. `x.GetType() == typeof(Base)` goes from
  true to false. Rare, but it is a real behaviour change for existing consumers and belongs in the
  changelog, not a footnote.
- **Does `TryCast` stay the documented advice?** If this lands, `TryCast` becomes an escape hatch rather
  than the rule. Both spellings must keep working; the guidance is what changes.

## Acceptance

Per [contribution/01-philosophy](../contribution/01-philosophy.md), this is done when it is proven on
**ToyGame under both loaders** — which requires a fixture the toy game does not have today: a **base/derived
pair reachable through a base-declared seam** (a property returning the base, and a collection whose element
type is the base). That fixture is worth adding regardless of whether this ships: it is the exact shape no
current battery case covers, which is why the leak survived this long.
