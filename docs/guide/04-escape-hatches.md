# 4. Escape hatches — by-name reach and fault safety

*For the cases the typed path can't reach. Previous: [3. Natural typing](./03-natural-typing.md) · Next: [5. Wire JSON](./05-wire-json.md).*

## When you're here

Chapters 2–3 are the happy path: you have a typed proxy (`Player`, `Game`), so you write
`player.TakeDamage(5)` and hook by name. This chapter is the **escape hatch** — reach for it only when
the typed path is structurally impossible:

- **an erased handle** — you hold something only as `Il2CppSystem.Object` (a hook arg, a collection
  element) and don't have its concrete type;
- **a type you can't name at author time** — DLC, runtime-generated, or content that isn't in the
  proxies you compiled against;
- **a suspect call** — a torn `this`, a possibly-freed object, a handle you're not sure is even live —
  where a normal call would hard-crash the process;
- **the [REPL](./06-repl.md)** — where nothing is known at author time by definition.

If you *can* name the type, prefer the typed face. These are the tools for when you can't.

## `Safe` — call the game without crashing the process

An IL2CPP call that hits bad memory raises a native `0xC0000005` that the CLR **cannot catch** — it takes
the whole game down. `Safe` runs your call inside a native fault guard so a hardware fault becomes a
return value instead:

```csharp
using Inutil;

// Wrap the seamless call you'd normally write:
InvokeResult r = Safe.Run(() => player.TakeDamage(5));
if (r.Faulted) Log($"call faulted: {r}");     // caught a 0xC0000005 instead of dying

// Value-returning form:
SafeResult<int> h = Safe.Run(() => player.GetHealth());
if (h.Ok) Log($"health = {h.Value}");
```

Every result is exactly one of three verdicts:

| Verdict | Means | Read it with |
|---|---|---|
| `Ok` | the call completed | `SafeResult<T>.Value` (or `InvokeResult.Unbox<T>()`/`As<T>()`/`AsString()` on the raw substrate) |
| `Faulted` | a hardware fault was caught — the call did **not** complete | `FaultCode`, `FaultAddr` |
| `Threw` | a managed/il2cpp exception surfaced (a normal, complete call) | `ManagedException` / `RawException` |

**Honest caveat (important):** after a *caught* fault the runtime may be tainted. `Safe` is a seatbelt for
suspect territory and a diagnostic — **report and restart, don't loop-retry** a faulting call. A leaf
fault (a bad `this` deref) is usually benign; a fault deep in allocation/GC may not be.

There's also a lower-level `Safe.TryInvoke(methodInfo, instance, argPtrs)` — the proven substrate that
runs the raw `il2cpp_runtime_invoke` under the guard. `Safe.Run` is the ergonomic face over it; use
`TryInvoke` when you already hold a `MethodInfo*`.

## `Invoke` — call a method by name

When you don't have a typed proxy method, resolve and call by name on the object's **runtime** class. The
call is fault-guarded (it's `Safe` underneath — one guard implementation, not two):

```csharp
// Natural args auto-marshalled — value types, proxies, and strings, no unsafe pointers:
InvokeResult r = Invoke.Call(target, "TakeDamage", 5);
InvokeResult n = Invoke.Call(target, "SetName", "hero");

// Allocate an instance (no constructor runs) then run .ctor explicitly:
var p = Invoke.New<Player>();
Invoke.Call(p!, ".ctor");
```

A missing name/arity **throws** (a typo is a programmer error) — but the *call itself* is guarded, so a
bad target yields `Faulted`, not a crash. For `ref`/`out` params, non-blittable structs, or hot paths,
use `Invoke.TryInvoke` with your own arg pointers; `Invoke.Unguarded` skips the guard entirely when you
*know* the target is good and want no overhead.

## `Probe` — is this pointer even a live object?

Before reaching into an erased or suspect handle, ask what it is — fault-safely:

```csharp
if (Probe.IsLiveObject(handle))                 // null / garbage / bad-class → false, never a crash
{
    ProbeResult d = Probe.Describe(handle);      // "ToyGame.Player klass=0x…", or arrays: "Int32[7] (elem Int32)"
    Log(d.ToString());
}
```

**Honest limitation:** this proves *plausibly* live, not *provably* live. The GC is non-moving, so a
freed object's memory usually still validates — `Probe` catches null/garbage/bad-class pointers, **not**
use-after-free.

## `Introspect` — dump an object

For interactive discovery (especially in the REPL), print what an object has:

```csharp
Log(Introspect.Dump(obj));        // fields + values
Log(Introspect.Methods(obj));     // method signatures
Log(Introspect.FieldList(obj));   // field names + types
Log(Introspect.Props(obj));       // properties
Log(Introspect.TypeInfo(obj));    // the type itself
```

All of it goes through the same guarded reads as `Probe`, so dumping a suspect object degrades to blanks
rather than faulting.

## `Fields` — get/set a field by name

The field-side twin of `Invoke`. Note the deliberate **silent-probe** convention here: a missing field
returns `false`/`default` rather than throwing (this is discovery code — you're often probing for a field
that may not exist):

```csharp
int hp     = Fields.GetInt(obj, "_health");           // miss → default(int)
bool ok    = Fields.SetInt(obj, "_health", 100);      // miss → false (didn't throw)
string? nm = Fields.GetString(obj, "_name");
var gear   = Fields.GetObject(obj, "_gear");          // a nested proxy
int? level = Fields.GetNullable<int>(obj, "_level");  // reads ref-bearing/Nullable value fields correctly
```

`Fields` handles the value-layout hazards (ref-bearing value fields, `Nullable`) that Il2CppInterop's own
`proxy.Field` misreads.

**A miss is silent, so check the result when you're not probing.** `Set*` returns `false` when the field
isn't there. Discarding that is how a renamed field on the next game build becomes a mod that quietly does
nothing:

```csharp
if (!Fields.SetString(session, "_locationTime", wt.Time))
    Log.LogError("session._locationTime not found — the field was renamed; the guard below will still throw");
```

## Getter-only property? Look for its backing field first

The single most common reason to reach for `Fields` is a property the game exposes **read-only** —
`public string LocationTime { get; }`. There is usually a typed way in, and it is easy to miss.

An auto-property is backed by a compiler-generated field, `<LocationTime>k__BackingField`. Il2CppInterop
renders that field as **its own property, with both accessors** — so one storage surfaces as *two* proxy
members: the read-only property, and a settable twin whose name is the backing field with the angle brackets
sanitized away:

```csharp
// instead of — stringly-typed, silent on a miss, angle brackets and all:
Fields.SetString(session, "<LocationTime>k__BackingField", wt.Time);

// prefer — typed, compile-checked, breaks loudly if the game renames the property:
session._LocationTime_k__BackingField = wt.Time;
```

**Confirm the spelling before relying on it.** The `<X>k__BackingField` → `_X_k__BackingField` sanitization
is a convention these proxies follow consistently, not a guarantee — check the type's real surface:

```sh
surface-query --methods <Type>     # lists get_/set_ accessors under their actual names
```

Two caveats worth holding:

- **It is still a read-only member.** The game declared it not-writable; code elsewhere may assume nothing
  writes it. A typed backing-field write is *safer to author* than a by-name one, not more *legitimate*. When
  the type has a real setter, constructor, or wire form, use that instead.
- **If the type is a wire DTO** — one the game only ever deserializes — you are probably hand-minting an
  object that should be built from JSON. See [5. Wire JSON](./05-wire-json.md); that chapter's rule is: if
  you find yourself writing read-only members by name on a type the game only deserializes, build it on the
  wire side instead. Backing-field writes are for *live* objects with no wire form.

## Prefer the typed face when you can

Raw `Fields`/`Invoke` are the *substrate*. When the type **is** known at author time, write the seamless
typed call (`player.TakeDamage(5)`, `view.Gear`) instead — checked at compile time, no stringly-typed
silent-probe. Reach for raw `Fields`/`Invoke` only for the irreducible cases at the top of this page.

## Checkpoint

- ✅ you know these are escape hatches — typed faces first, by-name only when you must
- ✅ for a getter-only property you check for the settable `_X_k__BackingField` twin before reaching for `Fields`
- ✅ `Safe.Run` turns a would-be crash into an `Ok`/`Faulted`/`Threw` verdict (and its taint caveat)
- ✅ `Invoke`/`Fields` reach methods/fields by name; `Probe`/`Introspect` inspect suspect handles
- ✅ you know the honest limits: "plausibly live" ≠ use-after-free; `Fields` misses are silent by design

**Next → [5. Wire JSON](./05-wire-json.md)** — the typed alternative to by-name writes: building game objects
the way the game builds them, from checked object literals.
