# Hook engine — managed detour host + native interceptor & guard

*Where a hook actually fires. Read [the system map](../02-system-map.md) first. This is the boundary
[marshal (12)](./12-marshal.md) converts values across; [interop-patch (11)](./11-interop-patch.md) is why the
proxy signatures the plan reads are natural-typed. The ergonomic `Hook<T>` / `HookMatch` surface that drives it
is [hook-ergonomics (14)](./14-hook-ergonomics.md); the fault-safe `Safe`/`Invoke`/`Probe` faces built on this
page's guard are [reach-faces (17)](./17-reach-faces.md).*

## Job

The hook engine intercepts a live il2cpp method — reads and rewrites its args, replaces or wraps its return,
runs the original on demand — with **no per-method codegen**. It is two halves across one seam. Managed
`Inutil.Hooks` decides *which* method (a typed expression, a proxy `Type`+name, or a raw il2cpp `MethodInfo*`),
computes the Win64/il2cpp **ABI layout plan** for it, and works the intercepted call frame through a typed
`HookContext`. Native `inutil_core` owns the *mechanism*: it detours a method's entry with MinHook + one generic
asm thunk, then reverse-P/Invokes back into managed on every hooked call. The seam is P/Invoke, both ways —
managed `[DllImport]`s the core and hands it four `[UnmanagedCallersOnly]` dispatchers; the core hands back five
install/control callbacks. The same DLL also carries an SEH **fault guard** — a bad pointer becomes a return
value, not a `c0000005` — used by the reach-faces but independent of the hook engine.

## In the tree

| File(s) | Holds |
|---|---|
| `managed/src/Inutil/Hooks/Hooks.cs` | the whole managed half — `CallFrame`/`RetFrame`, `MethodPlan`/`ArgPlan`/`Loc`, `HookContext`, the registration API, the `[UnmanagedCallersOnly]` dispatchers |
| `native/core/interceptor.{c,h}` | the substrate-agnostic engine — `HookCtx`/`VtableCtx`, `dispatch_key`, the install/invoker/vtable primitives, the pre/post dispatchers, the SEH guard |
| `native/core/generic_thunk_post.S` | the x64 asm — `inutil_thunk_post` (call+post), `inutil_invoker_thunk`, `inutil_call_original` (Proceed) |
| `native/CMakeLists.txt`, `native/cmake/toolchain-mingw-w64.cmake` | builds `inutil_core.dll` SHARED via mingw-w64 cross-compile (`--export-all-symbols -static-libgcc`) |
| `native/third_party/minhook/` | vendored MinHook — the x64 inline-detour primitive the methodPointer/vtable paths use |
| `managed/src/Inutil/Host/LoaderAdapter.cs`, `HostCore.cs` | the P/Invoke seam — `Attach()` calls `inutil_interceptor_init` and wires the callbacks into `Inutil.Hooks` |

## Design

**The ABI plan is computed once, at Register.** `MethodPlan.Of(method, mi)` classifies every arg and the return
into a `Loc` — `Gpr` (integer/pointer/object-ref/≤8B struct-by-value), `Xmm` (float/double), `Ind` (a `ref`/`out`
or an odd-sized struct passed by hidden pointer), `Sret` (a return built in a caller buffer) — plus each arg's
positional register-slot index. So the hot path is a table lookup, not a per-call ABI decision, and the typed
surface stays `Arg<T>`/`Return<T>` while the *plan* (not the author) knows the ABI. A twin, `MethodPlan.OfNative(mi)`,
derives the identical plan purely from il2cpp metadata — category for category — so a method Il2CppInterop never
projected (obfuscated/private/base-typed) is still hookable. The two must agree on every projected method (the §4
self-test, below); the native derivation is the one that reaches the long tail.

**Three interception paths, one install pipeline.** `InstallAndAppend` is the single place a detour is wired and a
hook appended. It detours **both** the `methodPointer` and the `virtualMethodPointer` (when distinct) with MinHook —
the common path, which catches interop calls, compiled direct calls, and vtable-virtual calls alike, because
Il2CppInterop drives every proxy call through `il2cpp_runtime_invoke` into the same body. Two shapes need a
different mechanism: a fully-shared `__Canon` generic **method** (all reference-type args fold onto one body il2cpp
runs through `MethodInfo::invoker_method`, offset `0x10`) falls back to overwriting that field (`install_invoker`);
a **shared empty stub** (il2cpp collapses all empty virtual bodies to one address, so detouring it fires for
unrelated methods) is reached by patching one class's `VirtualInvokeData.methodPtr` (`install_vtable`, keyed by the
bound `MethodInfo*` alone). All three dispatch through the same generic thunk and the same managed table.

**One generic thunk, zero per-method glue** (`generic_thunk_post.S`). It spills the 4 GPR + 4 XMM arg registers and
a pointer to the caller's 5th+ stack args into a `CallFrame`, zeroes the `RetFrame`, calls the pre-dispatcher
(which may rewrite args and returns a SKIP flag), reloads the possibly-rewritten args, `CALL`s the original unless
SKIP, spills the return into the `RetFrame`, calls the post-dispatcher, and reloads. A pre-hook's `Skip()` bypasses
the original and returns whatever the hook left; `Proceed()` runs the original *now* (via `inutil_call_original`,
the MinHook trampoline — no detour re-entry) so a hook can wrap it (`pre → Proceed() → transform → SetReturn → Skip`).

**The dispatch key — §7.7 ABI-completeness.** A `HookCtx` tracks `refs`: a body hooked for a single instantiation
routes unconditionally by the install-bound `MethodInfo*`; only a body shared by `refs>1` reference-type
instantiations needs the **live** trailing `MethodInfo*` the runtime passed, to disambiguate which chain to run.
`dispatch_key` reads it from GPR slot 0-3 *or*, when the arg count spilled it past slot 3, from `CallFrame.stack[miSlot-4]`
— every register/stack shape the ABI can produce, not just the first four slots.

**The patched-`Nullable` ABI carve-out — the tie to schema.** InteropPatch rewrites a `Nullable<T>`-returning proxy
to the *natural* `System.Nullable<U>`, which has no il2cpp class of its own — so deriving its native size/GC
descriptor from the managed type fails and would misclassify the sret boundary. `MethodPlan` maps it back to
`Il2CppSystem.Nullable<U>` (identical native layout) via `BuildNullableHint`/`NativeClassOf`. This is the one
`typeof(Il2CppSystem.*)` outside `Families.cs`, deliberately *not* in [schema (10)](./10-schema.md) because that
module is Il2CppInterop-free — the two are held equal by the self-test, not merged.

**The typed `HookContext` and the GC write barrier.** `Arg<T>`/`SetArg<T>`/`Return<T>`/`SetReturn<T>` (plus
`ArgString`/`ReturnString`, `ArgObject`, `SetReturnTask`) resolve to the right frame address by the plan. A write of
an object-bearing value into a `Loc.Ind` (`ref`/`out`) destination — which points at possibly-live heap — is routed
through `WriteBarriered` (`il2cpp_gc_wbarrier_set_field`) so the incremental collector sees the stored pointer;
everything else is a raw store into the spilled frame (already a GC root). Whether a slot is `RefBearing` is read
from il2cpp's GC descriptor, and the ref-bearing-value-proxy discriminator is `ValueTypeBridge.IsRefBearingValueProxy`
— the single `"Il2CppSystem.ValueType"` spelling ([marshal (12)](./12-marshal.md), C6), called here, not copied.

**Lock-free dispatch, ALC-aware removal.** The table is a `ConcurrentDictionary<nint, Entry>` keyed by native
`MethodInfo*`; each `Entry`'s `Pre`/`Post` are copy-on-write `volatile` arrays, so the dispatchers iterate lock-free
on the game's own thread while a mod adds/removes hooks under `_reg`. A hook's delegate is the only thing rooting its
mod's collectible ALC, so `RemoveAll(AssemblyLoadContext)` un-roots it on unload (and restores any vtable slot whose
entry goes empty, before its closure dangles).

**The SEH fault guard.** `inutil_guard_read` / `inutil_guarded_invoke` run a risky read or an `il2cpp_runtime_invoke`
under a process-wide Vectored Exception Handler + `setjmp`/`longjmp` — mingw C has no `__try/__except`. A hardware
fault (`c0000005`, illegal instruction) becomes a return code instead of killing the process; a managed exception
comes back through il2cpp's own `exc` out-param and is left alone. Self-initializing, idempotent, and independent of
the hook engine — the reach-faces ([17](./17-reach-faces.md)) use it without `inutil_interceptor_init`.

## Invariants

- **One detour per unique `methodPointer`/vtable slot; N hooks append to it (P1).** The core dedups the pointer and
  bumps `refs`; managed appends to the copy-on-write array. One install, many hooks — never a second detour on the
  same body.
- **Distinct generic instantiations are distinct table keys (S5), never special-cased.** The key is always the
  native `MethodInfo*`; a shared `__Canon` body routes each live instantiation to its own key.
- **`Of` == `OfNative` on every projected method.** The `SelfTestNativePlan` cross-check (`MethodPlan.Diff`) proves
  the proxy-derived plan equals the pure-metadata plan — including the patched-`Nullable` mapping, which is how the
  schema tie holds without a shared `typeof`.
- **Every failure path fails loud (P4).** No entry pointer, a native install that didn't take, an open generic
  method definition, an unresolved name — each throws a directed exception. A key miss in the dispatcher returns 0
  (fall through to the original), never a wild read.
- **No partial commit on install.** If `MH_EnableHook` (or a vtable `VirtualProtect`) fails, the created hook is
  undone and the pool entry popped — so the dedup can never resurrect a failed install as a live shared body.
- **`"Il2CppSystem.ValueType"` is spelled once** (`ValueTypeBridge`, C6); the plan calls the discriminator.

## Limits, defers & TODOs

- **The inlining boundary is fundamental.** A call site il2cpp (or the C++ compiler) folded into its caller has no
  `call` instruction to detour, and nothing recovers it at runtime — verified against the metadata + disassembly.
  Trivial leaves (field getters, tiny arithmetic) are the prone case. The engine can't intercept the inlined site
  but flags it at registration: `LooksInlineProne` (a cheap byte heuristic, not a disassembler) warns via
  `OnWarning`, routable to the loader's logger. This is a loud advisory, not a silent miss.
- **`__Canon` body detour, invoker fallback.** A shared-generic body is detoured directly (subsuming the invoke
  path); overwriting `MethodInfo::invoker_method` is the fallback only when the body pointer won't take — strictly
  worse (reflection-invoked calls only), and a non-generic method has no such fallback (it throws).
- **Open generic method definitions can't be hooked directly** — they have no body the runtime enters.
  `RegisterNative` throws and directs you to `InflateNative(mi, typeArgClasses)` to reach a closed instantiation.
- **After a caught fault the runtime is tainted.** `inutil_guarded_invoke` unwinds past il2cpp frames; the contract
  is report-and-restart, not keep-going. The guard does *not* catch stack overflow (the guard page is already tripped).
- **win-x64 only.** The thunk, the ABI plan, and the build are Win64/mingw-w64 specific.

## Tests

- **Offline (native, under Wine).** `native/tests/guard_smoke.c` + `guard_managed_smoke.c`, driven by
  `tools/wine/guard-smoke.sh`, build the real `inutil_core.dll` and prove the SEH guard catches a bad read and a
  garbage-class invoke — single-frame, nested multi-frame, 64 repeated faults, and a clean call after a fault —
  with the process surviving. A pure-C harness has no CLR, so it *cannot* prove the reverse-P/Invoke frame survives;
  that is deliberately called out and left to the in-game case.
- **In-game (the battery).** `managed/test/Battery/Cases/HookCases.cs`, under both loaders: `hook.engine.attach`
  (the P/Invoke seam comes up), `hook.pre.observe`/`hook.arg.rewrite`/`hook.return.rewrite` (dispatch + the ABI
  plan read/write the right slots), `hook.skip`/`hook.proceed` (the SKIP and around branches),
  `hook.delegate.dispatch` (the detour is on the real body, not a proxy shim), `hook.canon.spill.route` (the §7.7
  headline — a shared `__Canon` whose `MethodInfo*` spills to `Stack[1]` routes each instantiation correctly), and
  `hook.task.fabricate.return` (`SetReturnTask` at the boundary). The guard's CLR-frame survival is
  `safe.run.faulted-survives`. The §4 plan agreement is `SelfTestNativePlan`.

## Why it's shaped this way

The v1 bug-class this shape retires is the **shared-`__Canon` mis-route** (spec §7.7). When il2cpp folds every
reference-type instantiation of a generic method onto one native body, one detour serves many instantiations — and
v1 capped the trailing-`MethodInfo*` slot at GPR 3, so any method whose arg count spilled that hidden pointer onto
the stack fell through to a `-1` "unknown" and mis-routed *every* such call to the first-registered chain (calling
`EchoWide<Boss>` fired the `Player` hook). The fix is not a special case: `dispatch_key` reads the live pointer from
`CallFrame.stack[miSlot-4]` — reusing machinery the thunk already exposes — so every register/stack shape the ABI
can produce is handled. Paired with it are the two **no-partial-commit** install fixes (a failed `MH_EnableHook` or
vtable patch must not leave a zombie the dedup resurrects) and the **unified dedup key** across install / dispatch /
restore. And the whole thing is **codegen-free**: one asm thunk and one ABI plan mean there is no per-method glue to
drift out of agreement with a method's real signature — the plan is derived from the signature (or the metadata),
proven equal by the self-test, and read on the hot path as data.
