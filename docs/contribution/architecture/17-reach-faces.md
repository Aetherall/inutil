# Reach faces — Safe, Invoke, Probe, Introspect, Fields

*The fault-safe / by-name substrate. Read [the system map](../02-system-map.md) first; the modder-facing
side of the same tools is [guide/04-escape-hatches.md](../../guide/04-escape-hatches.md). These wrap the
native fault guard from [hook-engine (13)](./13-hook-engine.md) and sit **under** the seamless typed call a
modder writes whenever the type is nameable at author time.*

## Job

Reach a game field or method **by name**, and touch a suspect object **without crashing the process** — for
the cases where a typed face is *structurally* impossible. §5 / this page's reframe name exactly three: an **erased
`Il2CppSystem.Object` handle** (a hook arg or collection element you only hold as a base ref), a **type not
knowable at author time** (DLC, runtime-generated, an assembly you didn't compile against), and the
**REPL** (each line evaluates live — no ahead-of-time pass ran over it). That's the whole warrant. Everywhere
else the type *is* nameable at author time, so a modder writes `player.TakeDamage(5)` directly —
**typed faces first, by-name only when you must.** This page is the floor those faces stand on, exposed raw
only at the edge.

## In the tree

`managed/src/Inutil/{Safe,Invoke,Introspect,Fields}/` — in the SDK (all touch Il2CppInterop + the il2cpp C
ABI), game-agnostic, called on the game (il2cpp-attached) thread.

| File | Holds |
|---|---|
| `Safe/Safe.cs` | **`Safe.TryInvoke`** (Shape A, the substrate) + **`Safe.Run`/`Run<T>`** (Shape B, the scope) over `inutil_core`'s `inutil_guarded_invoke`; the `RunThunk` reverse-P/Invoke trampoline |
| `Safe/InvokeResult.cs` | the verdict struct `InvokeResult { Ok \| Faulted(code,addr) \| Threw }` + `Unbox<T>`/`As<T>`/`AsString`; `SafeResult<T>` (the value-returning twin) |
| `Safe/Probe.cs` | the READ side of the same guard — `IsLiveObject`/`Describe`/`KlassName`/`ProbeResult` over `inutil_guard_read` |
| `Invoke/Invoke.cs` | by-name method reach — `Resolve`/`MethodOf`/`New`/`New<T>`/`TryInvoke`/`Call`/`Unguarded` |
| `Introspect/Introspect.cs` | guarded dumps — `Dump`/`FieldList`/`Methods`/`Props`/`TypeInfo` |
| `Fields/Fields.cs` | by-name field get/set — `GetValue`/`SetValue`/`GetNullable`/`GetObject`/`GetString`/`SetStruct`/… (the silent-probe) |

## Design

**`Safe` — two shapes, ONE guard, deliberately layered** (the P2 "both, gated by a de-risk test" decision).
*Shape A* `TryInvoke(methodInfo, instance, argPtrs)` is the proven substrate: it passes the real
`il2cpp_runtime_invoke` as `invoke_fn`, so the guard's risky window is **pure native** — a caught fault's
`longjmp` unwinds only il2cpp's own native frames, exactly what `guard_smoke` validated. *Shape B*
`Run(() => player.TakeDamage(5))` / `Run<T>` is the **ergonomic face** — fault-safety as a *scope over the
seamless call the modder would normally write*, not a stringly-typed re-spelling. It runs a managed delegate
inside the guard through the `RunThunk` reverse-P/Invoke trampoline (`[UnmanagedCallersOnly]`), so a caught
fault's `longjmp` also unwinds a **CLR reverse-P/Invoke transition frame** — the extra risk v1 never had.
A C# exception the delegate throws is **caught at the trampoline** (it must not unwind across the native
boundary) and surfaced as `ManagedException`/`Threw`; only a hardware fault ever reaches the `longjmp`. The
per-thread `t_body`/`t_result`/`t_exc` are saved/restored across each guard call so a nested `Safe.Run`
(the seamless call re-entering) doesn't clobber the outer frame.

**`InvokeResult` — the verdict, no allocation.** A `readonly struct` where **exactly one** of `Ok`,
`Faulted` (a hardware fault caught — the call did NOT complete, carries `FaultCode`/`FaultAddr`), and `Threw`
(a managed/il2cpp exception surfaced — a *complete* call) holds. `Unbox<T>` reads a boxed value-type return,
`As<T>` wraps a reference return as its interop proxy, `AsString` marshals a string. `SafeResult<T>` is the
value-returning twin for `Run<T>`: the same status **plus** the delegate's already-materialized managed `T`
(meaningful only when `Ok`).

**`Probe` — the READ side of the same guard.** `IsLiveObject` walks `ptr → its klass slot → the klass`
through `inutil_guard_read` (the *same* VEH + `setjmp` shim as the invoke guard), so a fault at any hop is
just "not live", never a crash. `Describe` is the one-call "what is this?" — valid? class? array (element +
length)? — that ends the pointer archaeology before `Fields`/`Invoke` reach in. Names come back through the
guarded UTF-8 helper (`SafeUtf8`/`ClassName`), so a mapped-but-garbage klass degrades to `""` rather than
faulting on a wild name pointer.

**`Invoke` — name resolution + arg marshalling over Safe's ONE guarded invoke.** `Resolve` walks the runtime
class chain (`il2cpp_class_get_method_from_name` up each parent); `MethodOf` resolves on the object's
*runtime* class (the most-derived type, not the handle's static type). `Call` auto-marshals natural C# args
— a value type as a **pinned** pointer to its bytes, an `Il2CppObjectBase` as its object pointer, a string as
a fresh il2cpp string — then hands off to **`Safe.TryInvoke`**. It adds *only* resolution + marshalling +
allocation on top of that one guarded call; there is **no second guard implementation**. `New`/`New<T>`
allocate zero-init (no constructor). `Unguarded` is the deliberate fast path with **no** guard.

**`Fields` — by-name field access, resolved on the runtime class.** `FindField` walks the runtime class
chain, so it reaches a field a base/obfuscated subclass declares that the handle's static type can't name.
It also handles the value-layout hazards Il2CppInterop's own `proxy.Field` **misreads**: a `Nullable<T>`
field (`GetNullable`/`SetNullable` read/write the inline `{ hasValue, value }`), and a value struct carrying
managed refs (`SetStruct`/`SetNullableStruct` route through `il2cpp_field_set_value`, the runtime's own
GC-aware, write-barriered store, where a raw `*(T*)` write drops the ref). These are the §7.5 hazard a
seamless typed accessor must get right underneath the covers.

**`Introspect` — guarded dumps.** `Dump`/`FieldList`/`Methods`/`Props`/`TypeInfo` walk a live proxy's runtime
class through the il2cpp C ABI, reading every name through `Probe`'s guarded marshal so a partially-torn
object degrades to blanks. It answers the question that precedes the other three — *what names does this
handle actually have?* — the discovery act that makes an unnameable type workable at all.

**All of this is SUBSTRATE.** The preferred path is always the seamless typed call the modder would
normally write (`player.TakeDamage(5)`, `player.Health`) once the type is nameable at author time — these
by-name primitives are the fallback for exactly the three cases named above, never the default.

## Invariants

- **ONE guarded-invoke implementation, not two (P1).** `Invoke.TryInvoke`/`Call` delegate the actual call to
  `Safe.TryInvoke`; the fault guard is spelled once, in `Safe`.
- **A programmer error THROWS; the CALL is guarded.** A null `methodInfo`, an absent method name+arity, a
  null `Invoke` target, or a non-blittable `Call` arg throws (a typo is a bug) — but a *garbage-but-non-null*
  target flows into the guard and comes back `Faulted`, never a crash.
- **Silent-probe is a DELIBERATE design decision (P4), not an accident.** A by-name field read miss returns
  `default`, a write miss returns `false` — because probing-for-maybe-absent *is the job* (a game update
  renames a field out from under you). The loud, assertion-shaped face is the generated typed accessor above
  it; the silent-vs-loud split lives at the seam on purpose.
- **`Probe` proves "plausibly live", not "provably live".** Boehm is non-moving and non-compacting, so a
  *freed* object's memory usually still validates. It catches null / garbage / bad-class handles; it does
  **not** catch use-after-free, and is named accordingly.
- **Exactly one of `{Ok, Faulted, Threw}` holds** on every `InvokeResult` — the three verdicts partition the
  outcome space; there is no fourth, silent state.

## Limits, defers & TODOs

- **The taint-after-fault caveat (report and restart — do NOT loop-retry).** After a *caught* fault the
  runtime is **tainted**. A leaf fault (`runtime_invoke` reading a bogus method struct; a method's first
  `this` deref) holds no il2cpp lock, so the taint is benign — but a fault deep in allocation / class-init /
  GC may not be. `Safe` is a **diagnostic and a seatbelt for suspect territory, not a general retry
  mechanism.** Re-running a faulting call in a loop is the misuse it can't protect you from.
- **`Probe` ≠ use-after-free** (restated because it is the easy mistake): `IsLiveObject` on a dangling
  pointer will usually return `true`. Use it to reject null/garbage, not to prove a handle is safe to keep.
- **`Resolve`/`MethodOf` key on name + arg count only** — `il2cpp_class_get_method_from_name` can't separate
  same-arity overloads. Hold a `MethodInfo*` and call `Safe.TryInvoke` when you need a specific overload.
- **`New`/`New<T>` run NO constructor** (zero-init allocation); call `.ctor` via `Call` if the type needs it.
- **`Introspect` deliberately leaks the `il2cpp_type_get_name` C-heap string** per call — freeing risks a
  crash if a build ever returns a non-heap pointer, and the per-call leak is negligible for a dev tool.
- Everything else **fails loud** — the escape hatch never quietly mis-marshals. The user-facing limit map is
  [reference/limits.md](../../reference/limits.md); the reframe rationale is the **Why** section below
  (superseding the earlier "re-port the three v1 APIs" framing).

## Tests

- **Offline native guard** — `native/tests/guard_smoke.c` (the guarded invoke, Wine-proven) and
  `guard_managed_smoke.c` (the Shape-B extra risk — nested / repeated / interleaved callback faults through
  the reverse-P/Invoke trampoline).
- **In-game** — `managed/test/Battery/Cases/SafeCases.cs`, GREEN under **both loaders**, reflection-only
  (targets named by il2cpp metadata strings, no proxy bound): `safe.tryinvoke.ok` / `safe.run.ok` and the
  caught-fault pair `safe.tryinvoke.faulted-survives` / `safe.run.faulted-survives` — the latter proving the
  **CLR frame chain survives the `longjmp`** (a C test structurally can't reach it: fault, then more managed
  work on the same thread returns `Ok`). The by-name surface: `probe.live-vs-garbage`, `invoke.erased-handle`
  (the §5.1 headline — a `Player` held only as `Il2CppSystem.Object`, reached by name so `Fields.SetInt`/
  `GetInt("Health")` and `Invoke.Call("GetHealth")` **agree**), and `introspect.dump`.

## Why it's shaped this way

v1 shipped `Fields` / `Introspect` / `Invoke` as **modder-facing peer APIs** — a stringly-typed tier you
*dropped to*. That is exactly the friction natural typing exists to delete: reaching for
`Fields.Get<int>(obj, "Health")` when you could write `player.Health`. v2 reframes the trio as **substrate,
not a peer API**: the seamless typed call is the default, fault-safety is a *scope* over it (`Safe.Run`),
and the raw by-name surface survives **only** where §5 makes a typed face structurally impossible — an
erased handle, an unnameable type, the REPL. And the native fault guard it all rests on was, in P2's words,
**"built, proven, and wired to nothing"** — a modder's seamless `player.TakeDamage(5)` over a flipped proxy
could still hardware-crash the process — until `Safe` became its first managed consumer. Re-read this
reframe before reshaping any of this; the split is the whole point.
