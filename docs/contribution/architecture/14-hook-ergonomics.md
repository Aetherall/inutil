# Hook ergonomics — Hook<T>, the matcher, and discovery

*The L4 modder surface — where a mod names game methods directly and inutil wires them. Read
[the system map](../02-system-map.md) first. This layer sits ON TOP of the [hook engine (13)](./13-hook-engine.md)
(the one chain it rides) and the [marshaller (12)](./12-marshal.md) (the one engine every slot routes through),
and it is the registration path the [mod host (15)](./15-mod-host.md) drives per generation.*

## Job

Turn `class TallyHook : Hook<Game>` — a class that names game methods as its own methods — into live engine
hooks, so a modder writes plain C# against the game "as if developing the game itself." Three concerns, one
per file group: **`Hook<T>`** gives a body `Self` (the typed receiver) and `Proceed(...)` (run the original);
**`HookMatch`** decides *which* method on the proxy a hook method corresponds to (four tiers, fail-loud when
none bind); **`Mods.Discover`** scans an assembly, installs each match as one engine pre-hook, and tears the
whole set down per collectible ALC. It owns the *ergonomics and wiring* — never a second hook chain and never
a second marshaller.

## In the tree

`managed/src/Inutil/Mods/` (in the SDK, `Inutil.dll`, `namespace Inutil`, so a mod writes `using Inutil;`).
`Mods` is one `partial class` split across two files.

| File | Holds |
|---|---|
| `Hook.cs` | `Hook<T>` (`Self` / `Proceed<R>` / `Proceed`), `HookBinding` (the per-slot frame marshalling), `HookDispatch` (the thread-local around frame) |
| `HookMatch.cs` | the four `Resolve` tiers (exact · widen · interface-impl · generic-inference), `ParamBinds`, and the fail-loud `Diagnose` |
| `Mods.cs` | `ILoad`/`IUnload`/`ITick`/`IGui`, the static lifecycle registry, `Add`, `Tick`/`Gui`, `Safe` isolation |
| `Mods.Discover.cs` | `Discover` (atomic scan + wire), `WireHookClass` (the il2cpp install), `RemoveAll` (ALC teardown), the `_subs` ownership map |

## Design

**`Hook<T>` replaces by default.** A hook body *is* the method: its return value becomes the call's result,
and the original does not auto-run. `Self` is the live receiver typed `T` (null for a static game method);
`Proceed(...)` runs the original un-hooked and returns its typed result. Because replace is the default,
**a body that wants the original to run MUST call `Proceed`** — forgetting it is loud-by-contract
(documented), not a silent mid-frame behaviour change. `Self`/`Proceed` are *static* so they read identically
from instance and static hook bodies — they read a thread-local frame, not `this`.

**`HookDispatch` is that frame — one thread-local `Around` adapter, not a chain.** Discover binds one engine
callback per hook method; on dispatch, `Around` saves the previous frame, populates `_self`/`_ctx`/`_binding`,
reads the args into typed managed values, invokes the body, calls `ctx.Skip()` (the body IS the method — don't
also auto-run the original), writes the return, and writes back any ref/out params. Re-entry is synchronous on
one stack, so a single saved/restored slot handles onion nesting (a hook body that provokes another hooked
call). The whole body is exception-isolated to `Hooks.OnWarning` — one throwing hook never crashes the
dispatch. `Proceed` maps to the engine's `HookContext.Proceed()`: write the args into the current frame, run
the original via the trampoline (no detour re-entry), read its return back.

**Per-slot marshalling — Layer A here, Layer B in the shared engine.** `HookBinding` owns only the frame ABI
(a native slot ⇄ its il2cpp representation); the natural⇄il2cpp *conversion* is handed to `Il2CppMarshal`
([marshal (12)](./12-marshal.md)) — never a second marshaller, the container/Task/Nullable/Tuple logic lives
ONLY in the engine. Each slot dispatches by how the ABI carries the type: a **string** or **blittable value
type** or **reference proxy** is an identity/ABI leaf read straight from the frame; a **by-value value type
whose conversion is element-aware** (`Nullable<T>`, `ValueTuple`) or a **ref-bearing game value struct**
(`Loadout {int; string}`) is boxed at the slot and driven through the engine (`FrameValueToManaged` /
`ManagedToFrameValue` over `ValueTypeBridge`, the §7.5 primitive); a **reference-kind container** (arrays,
`List`/`IReadOnlyList`/`IEnumerable`/`HashSet`, `Dictionary`, `Task`) is recovered as an object pointer and run
through the engine (`PointerToManaged` / `ToIl2Cpp`). A **ref/out** param peels the `ByRef` and reuses those
same kinds, with one difference on write: storing a ref-bearing value into a possibly-live-heap destination
threads the il2cpp GC write barrier (`ctx.ArgWritesBarriered`). Anything the engine can't marshal **fails loud
from the engine** — never a wild pointer read here.

**`HookMatch.Resolve` — four tiers, each binding UNIQUE-or-null.** Tried in order of specificity; the first
non-null wins:

- **Tier 0 · `MatchExact`** — same name + exact parameter types (reflection returns the most-derived method, so
  a virtual name yields the override the dispatch actually enters). This is the match Discover has always done.
- **Tier 1 · `MatchWidenedContainer`** — a hook may spell a read-only supertype (`IReadOnlyList<T>`/
  `IEnumerable<T>`) of the flipped proxy's concrete `List<T>`; `ParamBinds` binds it only when the element
  types are IDENTICAL, `game` IS-A `hook` (a genuine widening, `IsAssignableFrom`), and **both** spellings are
  containers the correspondence registry marshals (`_reg.ByBclOpenType`).
- **Tier 2 · `MatchInterfaceImpl`** — Il2CppInterop flattens interfaces, so an explicit impl `Ns.IFoo.Bar`
  lands as a mangled public `Ns_IFoo_Bar` with no `: IFoo` and no interface map (verified against the real
  proxy, `ToyGame_ITicker_Tick`). A hook names the plain `Bar`; the tier recognizes the mangling *shape* (ends
  `_Bar`, the preceding `_`-segment is an interface name `I`+Upper) plus an exact signature.
- **Tier 3 · `MatchGenericInference`** — a generic game method `Echo<T>(T)` has no single body to hook; a
  CONCRETE hook `int Echo(int)` names an instantiation. `InferInto` unifies the open signature against the
  concrete one (`T=int`), `MakeGenericMethod` inflates `Echo<int>`, and it re-verifies by exact re-inflated
  signature before binding.

Each fallback tier binds a UNIQUE candidate or returns null: two matches is an ambiguity it must **not**
silently guess through (it returns null, and the diagnostic fires). Tiers 1 and 3 are engine-backed — the
matcher consults the SAME `Families.Default()` registry the marshaller does, so it only ever binds container
spellings the marshaller can actually produce.

**`Diagnose` — the fail-loud seam (P4).** Where Discover once `continue`d silently on a miss, `Diagnose` warns
via `Hooks.OnWarning` — but ONLY when the proxy has a method of that name (an intended hook that mis-typed its
signature), naming the hook method and the proxy's real overloads so the fix reads straight off the warning.
When the name is absent it is a private helper, and it stays silent. It re-checks `Resolve` first, so it never
false-alarms on a hook that DID bind.

**`Discover` is atomic; `WireHookClass` owns the install.** Discover scans the assembly for `Hook<T>`
subclasses and lifecycle implementers; the whole assembly wires or nothing does — every engine hook the call
installed is tracked in an uncommitted `newSubs` list and disposed on any throw, so no partially-wired
generation is ever observable, and `OnLoad` fires only after a successful wire. `WireHookClass` first excludes
methods implementing a lifecycle interface (`ILoad`/`IUnload`/`ITick`/`IGui`) from ALL matching — so a
`Hook<Game>, ITick` never binds its `Tick()` onto a coincidentally-mangled game method — then, per matched
method, builds a `HookBinding`, wraps it in an `Around` closure, and installs it: `Hooks.Pre` for a normal
method, or `Hooks.PreNative` over the native `MethodInfo*` (`NativeMethodInfoOf`) for a Tier-3 closed
instantiation, which the name-based `Pre` can't resolve.

**Ownership + teardown — the mod's un-root.** The engine derives a hook's owner from the callback delegate's
assembly, but the `Around` callback is a closure defined HERE, in the host — so the engine's own
`Hooks.RemoveAll` could never reach it, and that host-static closure is exactly what roots the mod's
collectible ALC. So `Mods` tracks each subscription under the mod's `AssemblyLoadContext` (`_subs`) and
`RemoveAll(owner)` disposes them itself — then drops + `OnUnload`s the lifecycle objects — so one call leaves
no host-side root and the collectible ALC can actually collect.

## Invariants

- **The matcher only binds container spellings the marshaller can produce** — registry-backed via
  `_reg.ByBclOpenType`, the single source of truth for "the engine knows this container" (P1). It never binds
  an unregistered container (`ICollection<T>`) nor a loose covariant match (`IEnumerable<object>` over
  `List<string>` — the same-element-types check rejects it).
- **An ambiguous match falls through to null, never a guess.** Every fallback tier binds unique-or-null; two
  candidates return null and the miss reaches the diagnostic.
- **A mis-spelled but clearly-intended hook warns loud (P4).** Name-on-proxy + signature mismatch ⇒ a precise
  `OnWarning`; name-absent ⇒ silent (a helper). The one seam that used to be silent no longer is.
- **`Discover` is all-or-nothing.** A throw mid-wire disposes every hook the call installed and commits
  nothing; the confirmed CsModHost orphan-class of bug cannot occur.
- **Teardown un-roots the collectible ALC.** `RemoveAll(owner)` disposes the host-owned subscriptions the
  engine's own teardown can't reach, so the mod's context has no host-side root left.
- **Per-slot marshalling routes Layer B through the one engine.** `HookBinding` owns the frame ABI only; the
  conversion is `Il2CppMarshal`'s — there is no second container/Task/Nullable path here.

## Limits, defers & TODOs

- **The ref/out `Proceed` boundary is the one documented ergonomic-tier limit** (`Hook.cs`). A hook body's
  ref/out locals ARE the replaced method's outputs and are written back to the caller. But `Proceed(...)` takes
  args BY VALUE (a params call can't carry `ref`), so it does NOT flow the original's ref/out *mutations* back
  into your locals. A hook that must **replace** an out value does it in the body; one that must **transform**
  the original's ref/out output drops to the raw `HookContext` tier (`Arg<T>`/`SetArg<T>`), where the frame
  slot is addressable directly. This is a semantic boundary, never a silent mis-marshal.
- **GAPS Gap 1 is CLOSED** — the diagnostic plus all four tiers (widen, interface-impl, generic inference for
  value- AND reference-type `__Canon` instantiations) landed, both loaders green. The "raw-proxy" case (naming
  a non-public game method) needed no new matcher — Tier 0 already searches `Public|NonPublic`, so it binds by
  name+signature.
- **The universal inlining boundary applies here too, not just to generics.** A call site the JIT inlined has
  nothing to detour — true of every hook, not a generic-specific silent failure (it's why the ref-type generic
  proof uses a `[NoInlining]` method). Anything a matcher genuinely can't rescue reaches the loud diagnostic,
  never a silent miss.

## Tests

- **Offline** (`managed/src/Inutil.Mods.Tests`, no game boot — the payoff of splitting the DECISION half out of
  the install): every tier is driven over plain stand-in classes — exact, widen (and the rejections:
  `ICollection<T>`, cross-element-type), interface-impl (shape guard + unique-or-null), generic inference
  (per-instantiation, arity, ref-type), raw-proxy non-public bind, and `Diagnose` (warns on collision, silent
  on helper, silent when bound). The same program drives `Discover` end-to-end on a real compiled lifecycle mod
  (compile → collectible-ALC load → Discover → `FrameDriver.Tick` → `RemoveAll` → `Unload`) and confirms the
  no-match branch routes `Diagnose` → `OnWarning` and the lifecycle-exclusion invariant.
- **In-game** (`managed/test/Battery/Cases`, both loaders — a rescued hook must actually *fire*): the
  `ModHostCases` capstone (`modhost.capstone.compiled-hook`) plus one case per matcher and marshalling kind —
  `modhost.hook.container-widen`, `.interface-impl`, `.generic-inference`, `.generic-inference-reftype`,
  `.container-arg`, `.ref-out-replace`, `.ref-bearing-barrier`, `.valuetype-*`. The raw engine tier beneath is
  covered by `HookCases` (`hook.proceed`, `hook.skip`, `hook.canon.spill.route`, …). See [testing (20)](./20-testing.md).

## Why it's shaped this way

Read `Hook.cs`'s header for the real rationale. v1's `Facade` was a god-object that kept its OWN ordered
`Link[]` chain and made `Next.M(args)` a genuine RE-ENTRANT il2cpp call (resume-index dispatch) — a second
hook mechanism running alongside the engine's, with two orderings to keep in sync. v2 deletes that entire
chain: the body simply IS the method (replaces by default), and `Proceed` maps to the finished engine's
`HookContext.Proceed()` (the original via trampoline, no detour re-entry). So there is exactly one chain — the
engine's `Pre[]` — and no second mechanism to drift. `Mods` is the slim mod-facing facade OVER that engine
(lifecycle + the ergonomic discovery layer), not a re-implementation of it. The one thing v2 kept from the
Facade is the *reason* it tracked its links: a host-static closure roots a collectible ALC, so the host that
installs the hook must be the host that disposes it — which is why `RemoveAll` lives here and not in the engine.
