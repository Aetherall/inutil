# TODO — code review findings (engine/inutil)

Review pass over the engine core. Ordered by severity; each item names the site, the failure mode,
and the proposed fix. Unchecked = not yet addressed.

**Scope read:** `Hooks.cs`, `native/core/interceptor.c`, `native/core/generic_thunk_post.S`,
`Mods/{Hook,HookMatch,Mods,Mods.Discover}.cs`, `Host/{MainThread,Coroutines,FrameDriver,ModContext,
LoaderShim,LoaderAdapter,HostCore}.cs`, `Inutil.Mods/{CsModHost,CsModCompiler,Repl,ReplServer,
ReplHttpServer,ReplMcpServer,ReplTransport}.cs`, `Marshal/{ContainerBridge,ValueTypeBridge,
Il2CppMarshal,Il2CppConvRuntime}.cs`, `Fields/Fields.cs`, `Safe/Safe.cs`, `Inutil.BepInEx/BepInExPlugin.cs`.

**Not read** (no claims made about these): `Inutil.Schema`, `Inutil.InteropPatch`, `Inutil.Metadata`,
`Inutil.Check.Cli`, `Introspect/Invoke/Wire/Sugar`, `Inutil.MelonLoader`, test bodies, `testgame/`, `tools/`.

---

## High

### [x] H1 — Reverse-P/Invoke dispatchers have no exception isolation — FIXED

**Where:** `managed/src/Inutil/Hooks/Hooks.cs:1168` (`Dispatch`), `:1183` (`PostDispatch`),
`:1197` (`InvokePre`), `:1209` (`InvokePost`).

All four `[UnmanagedCallersOnly]` dispatchers run `pre[i](ctx)` / `post[i](ctx)` in a bare loop.
`HookDispatch.Around` catches everything for the `Hook<T>` tier, but a callback registered through the
raw tier — `Hooks.Pre` / `PreNative` / `PreVtable`, i.e. the REPL and any mod using it — throws straight
out of a managed→native transition, which is a rude process abort, not a caught exception.

Every other seam is exception-isolated (`MainThread.Drain` `:87`, `Coroutines.Step` `:180`,
`Mods.Safe`). The hottest one is not.

**Fix:** per-callback `try/catch → Hooks.OnWarning` inside the four loops. Keep it per-callback (not
per-loop) so one bad hook doesn't suppress its siblings.

**Fixed with two levels, not one.** The per-callback catch is the isolation the finding asks for. A
whole-body catch sits outside it because the *invariant* is "nothing escapes this transition", not "the
raw callback that broke first is guarded": it also covers the table lookup, the `HookContext`
construction, the sret fixup, and a warning sink that itself throws (`WarnSafe` swallows its own
failures — the reporting path must not become the abort it reports). A dispatch-level failure returns
`skip = 0`, so the original runs and the caller gets a real value — the same degrade `Around` makes.

Checked the invariant across the tree rather than the four named sites: `[UnmanagedCallersOnly]` has
exactly five occurrences in `managed/src`, and the fifth (`Safe/Safe.cs` `RunThunk`) was already
isolated with the rule stated in its comment. All five edges now hold it.

**Coverage:** `hook.raw.throw.isolated` — a throwing raw callback registered BEFORE a sibling; asserts
the process survives, the sibling still fires, and the result is unchanged. Registering the thrower
first is deliberate: a per-*loop* catch would swallow the rest of the chain, and this case fails on that
too. Landing it also unblocked `hook.proceed.original.throws` (H2's Proceed-path coverage), which needs
this isolation to exist before a throw out of `Proceed` can be observed rather than abort.

### [x] H2 — Hand-written thunks carry no SEH unwind data — FIXED

**Where:** `native/core/generic_thunk_post.S` — zero `.seh_*` directives in the file
(`grep -c seh_` → 0).

`inutil_thunk_post` establishes an `rbp` frame + `sub rsp,0x90` and **CALLs** the original;
`inutil_call_original` pushes `rbx/rsi/rdi` + `sub rsp,0x88` and CALLs. With no `.pdata`/`.xdata`
entry, `RtlLookupFunctionEntry` fails for these addresses and the x64 unwinder falls back to the leaf
assumption (return address at `[rsp]`) — wrong by 0x90/0x88 bytes. IL2CPP propagates managed exceptions
as C++ EH on Windows, so a hooked method whose original throws unwinds through our frame into a garbage
return address.

**Fix:** annotate both procs with `.seh_proc / .seh_pushreg / .seh_stackalloc / .seh_endprologue /
.seh_endproc` (mingw gas supports them; emits the .pdata/.xdata entries). The `make_closure` stubs are
leaf `jmp`s and need nothing.

**Validation:** add a ToyGame battery case with a hooked method whose original deliberately throws a
managed exception caught by an outer managed frame — must survive on both loaders.

**Fixed as diagnosed, and confirmed on both sides of the boundary before changing anything:**
- The three procs really had no `.pdata` entry in the linked `inutil_core.dll` (the C functions beside
  them did) — checked by parsing the PE, not by grepping the source.
- EFT's own `GameAssembly.dll` carries the `.?AUIl2CppExceptionWrapper@@` RTTI descriptor and the
  `0x19930520` `ThrowInfo` magic, so il2cpp really does raise managed exceptions as MSVC C++ EH —
  which unwinds exclusively through `.pdata`/`.xdata`.

Two things worth keeping in mind for anyone touching this file again, both verified in the emitted
unwind codes (`objdump` + a decode of the `.xdata`):
- `inutil_thunk_post` **must** use `.seh_setframe rbp, 0`, not a bare `.seh_stackalloc`. Its rsp is
  *dynamic* — `sub rsp,0x80` around the CALL to the original — so a static alloc code unwinds that call
  site 0x80 short. With a frame register the unwinder recovers rsp from rbp and the adjustment is
  transparent.
- `inutil_call_original` must **not** use `.seh_setframe`, even though it has an rbp frame. Its
  `mov rbp, rsp` precedes the `rbx/rsi/rdi` pushes, and `UWOP_SET_FPREG` makes the unwinder skip every
  code before it in the array — silently leaving three callee-saved registers clobbered on an unwind.
  Its rsp is static, so plain `pushreg`×4 + `stackalloc` describes the frame completely.

**Regression gate:** `native/cmake/check-unwind.py`, wired as a POST_BUILD step on `inutil_core`. It
reads the proc list out of the `.S` (`.globl` + matching label) and requires each to be covered in the
linked PE's `.pdata`, so a *newly added* thunk that forgets its annotations fails on the build that adds
it — rather than a check naming the three symbols that happened to be broken this time. Verified
discriminating: stripping one proc's directives fails the build naming that proc.

**Behavioural coverage:** `hook.original.throws.unwinds` (HookCases) — a hooked `ThrowingTally` whose
original throws, called from ToyGame's own `CatchThrowingTally` try/catch. Covers the auto-run path
(`inutil_thunk_post`). The Proceed path (`inutil_call_original`) is annotated to the same standard but
has **no behavioural coverage**: an exception raised inside `c.Proceed()` escapes through the raw-tier
callback, which has no isolation yet — see H1, which must land before that case can be written.

### [ ] H3 — REPL transports are CSRF-open; loopback binding is not a defence against a browser

**Where:** `Inutil.Mods/ReplTransport.cs` (`MiniHttp.WriteStatus`), `ReplHttpServer.cs:124`,
`ReplMcpServer.cs:120-126`, session ids at `ReplMcpServer.cs:115`.

`WriteStatus` emits `Access-Control-Allow-Origin: *`, `Access-Control-Allow-Methods: GET, POST, OPTIONS`,
`Access-Control-Allow-Headers: *` on **every** response including the 204 preflight; the SSE and JSON
responses repeat `ACAO: *`. There is no `Origin`/`Host` validation, no token, and MCP session ids are
sequential (`"s" + Interlocked.Increment` → `s1`, `s2`, …).

Consequence: any web page open while the game runs can preflight-then-POST arbitrary C# to `/eval` or
`/messages?sessionId=s1` and read the answer off the `*`-permitted stream. That is in-process RCE in the
game from a visited website. The file headers' "LOOPBACK ONLY — never reachable off the machine"
reasoning holds for the network and not for the browser.

**Fix (keeps the `curl` / `claude mcp add` ergonomics):**
- drop the wildcard CORS headers entirely;
- reject any request that carries an `Origin` header;
- require `Host: 127.0.0.1:<port>`;
- put a per-boot random token in the URL path, printed in the same log line the tooling already scrapes.

---

## Medium

### [ ] M1 — `refs > 1` conflates "shared `__Canon` body" with "shared empty stub", silently killing both hooks

**Where:** `native/core/interceptor.c:146` (`inutil_install` bumps `refs`), `:87` (`dispatch_key`).

`inutil_install` bumps `refs` whenever a *different* `MethodInfo*` installs at an already-detoured
`methodPointer`; `dispatch_key` then routes by the live trailing `MethodInfo*` read from
`gpr[miSlot]` / `stack[miSlot-4]`. Correct for a fully-shared generic. But il2cpp also folds *all empty
virtual bodies* onto one address — the exact case `PreVtable`'s header calls out (`Hooks.cs:739`). Two
non-generic methods landing there make `refs == 2`, and a non-generic call passes no trailing
`MethodInfo*`, so the read is whatever junk is in RDX/R8 → non-zero → `Table` miss → **both** hooks stop
firing, with no warning.

**Fix:** managed already computes `canon` (`Hooks.cs:628`, `:735`). Thread it into `install` and enable
live-mi routing only when the registrants are canon; otherwise warn loudly that a non-canon
`methodPointer` is already bound to a different `mi` (and point at the vtable tier).

### [ ] M2 — Partial detour install silently accepted

**Where:** `Hooks.cs:681-682`.

```csharp
bool ok = mp != 0 && _install(mp, mi, plan.MiSlot) != 0;
if (vmp != 0 && vmp != mp) ok |= _install(vmp, mi, plan.MiSlot) != 0;
```

If the `methodPointer` detour takes and `virtualMethodPointer` doesn't, `ok` stays true and every call
site entering through the virtual pointer misses the hook. The C side is scrupulous about
no-partial-commit (`interceptor.c:158-164`, `:253-259`); this `|=` undoes that discipline at the managed
boundary.

**Fix:** track the two installs separately; a failed second install either unwinds the first or fails
loud with which entry didn't take.

### [ ] M3 — Fabricated il2cpp Tasks: a faulted managed task hangs the game; completion can run off-thread

**Where:** `Marshal/ContainerBridge.cs:98-124` (`DematerializeTask`), `:129` (`TaskResult`).

Two distinct problems:

1. **Faulted/cancelled task → permanent hang.** `TaskResult` reads `.Result` by reflection. For a faulted
   or cancelled managed task that throws. On the pending path it throws *inside* the `ContinueWith`, so
   the continuation faults unobserved and the il2cpp promise handed to the game **never completes** — the
   game's `await` hangs forever with no log line. On the already-completed path it surfaces only as a
   generic hook-dispatch warning. There is no `TrySetException` path at all.
2. **Off-thread il2cpp touch.** The continuation is `ExecuteSynchronously`, so `Complete()` — which calls
   `TrySetResult` on an il2cpp proxy, or `ValueTypeBridge.InvokeUnboxed` — runs on whichever thread
   completed the managed task. The idiomatic mod hook (`async Task<X>` doing I/O) completes on the pool,
   which is exactly the off-thread il2cpp touch `MainThread` exists to prevent. The header concedes this
   but it sits under the most natural mod shape.

**Fix:** route the completion through `MainThread.Post` when `!MainThread.OnMainThread`; map a
faulted/cancelled managed task onto the il2cpp task's exception path (or, if the il2cpp `Task`1` surface
can't express it, complete with default **and** emit a loud warning — never leave the promise unset).

### [ ] M4 — Nullable field layout has two implementations, one an unchecked assumption

**Where:** `Fields/Fields.cs:93` (`SetNullable<UT>`), `Marshal/ValueTypeBridge.cs:158`
(`WriteNullableField<T>`) vs. their twins `Fields.cs:156` (`SetNullableStruct`),
`ValueTypeBridge.cs:176` (`WriteNullableRefField`), `:119` (`RefToNullable`).

The two value-typed setters compute `vo = SizeOf<T?>() - SizeOf<T>()` and assume `hasValue` at byte 0 —
the *CoreCLR* layout. The ref-bearing twins resolve `"value"`/`"hasValue"` **by name** through
`il2cpp_class_get_field_from_name`, and the getter (`GetNullable`, `Fields.cs:83`) goes through boxing and
touches no layout at all. So a get/set round-trip on one field straddles two different notions of where
the bytes live, and the assumption is not derived from the same source as everything around it.

No coverage: nothing in `managed/test/Battery/Cases/` references either method.

**Fix:** derive both offsets from `il2cpp_field_get_offset` on the Nullable class's own fields — one
implementation, same source as the twins. This is the "enforce the invariant, not the instance" shape:
it survives an il2cpp/Unity layout change instead of silently corrupting on one.

### [x] M5 — Return-marshal failure silently replaces the method with `null`/`0` — FIXED (but see reachability)

**Where:** `Mods/Hook.cs:238-240` (`HookDispatch.Around`).

`ctx.Skip()` fires *before* `WriteReturn` / `WriteRefOutArgs`. `Il2CppMarshal.ToIl2Cpp` fails loud in
several documented cases (correspondence mismatch, unresolvable proxy, foreign CLR object), so a throwing
`WriteReturn` leaves the method skipped with the return slot at the thunk's zeroed `RetFrame`
(`generic_thunk_post.S:60` — deterministic zero, not garbage, so this is semantic, not memory-safety).
The game sees a successful call returning null.

**Fix:** marshal into a temp *before* `Skip()`, or clear the skip cell in the catch — so the degraded
outcome is "the original ran" rather than "silently returned null".

**Fixed by ordering, needing neither a temp nor an un-skip:** stage the frame first
(`WriteReturn` / `WriteRefOutArgs`), commit `ctx.Skip()` last. `Skip` is idempotent and `Proceed` sets
its own, so a body that already ran the original keeps its skip and the double-run seam stays closed —
the two outcomes the alternatives had to special-case fall out of the order.

**Reachability — the finding overstates it, and this is worth recording.** An attempt to write a
battery case for the degraded path failed to produce one, because *every* arm of `WriteReturnSlot` is
either non-throwing or throws only on an internal engine fault, and the hook's declared return type is
bound EXACTLY to the (interop-patched, already-flipped) proxy signature at every `HookMatch` tier —
Tier 1 widens params only, "return exact". So the spelling corresponds by construction:
- proxy returns take `((Il2CppObjectBase)v).Pointer`, which cannot throw;
- container returns marshal between corresponding types;
- the "foreign CLR object" route is closed at the mod's COMPILE step — Il2CppInterop renders an il2cpp
  interface as a *class* with an `(IntPtr)` ctor (`IDamageable.IDamageable(IntPtr)`), so a mod cannot
  hand back its own pure-CLR implementation of a game interface.

So the reorder is a correct, zero-cost defensive fix that closes the failure mode if any of those
invariants ever slips (a new marshal path, a flip bug, a hand-built `HookBinding`) — but no mod a user
can write today reaches it. It is therefore **deliberately untested**, not untested by omission: the
alternative was an artificial ToyGame shape existing only to be marshalled wrongly, which would assert
that the test fixture is broken rather than that the engine is right.

---

## Low / nits

### [ ] L1 — `make_closure` address-space cost + leak on failure
`native/core/interceptor.c:111`, `:196`. One `VirtualAlloc` per hook = 64 KB reserved (allocation
granularity) + a committed page for a 21-byte stub. At the "thousands of methods" the pool comment
anticipates, that's tens of MB reserved for tens of KB of code. Use a bump allocator over one RWX page
(~190 stubs/page). Also: the closure page leaks when `MH_CreateHook` subsequently fails (`:158`).

### [ ] L2 — `MiniHttp` unbounded allocations
`Inutil.Mods/ReplTransport.cs`. `new byte[contentLength]` straight from the header, and the header
accumulator itself has no cap. Loopback-only, but a malformed client shouldn't be able to request a 2 GB
array. Cap both (e.g. 8 MB body, 64 KB headers) and 400 past the limit.

### [ ] L3 — Stale reference cache in `CsModCompiler`
`Inutil.Mods/CsModCompiler.cs:101`. Keyed on each ref dir's `LastWriteTimeUtc`. On Linux, overwriting a
DLL in place does not bump the directory mtime, so a rebuilt `Inutil.dll` / SDK can serve stale metadata
to every subsequent hot-reload compile for the process lifetime. Key on `max(file mtime)` or the file set.

### [ ] L4 — `SigOf` comment/code divergence
`Inutil.Mods/CsModHost.cs:105`. Comment says "relpath"; code appends `Path.GetFileName(f)`. A `.cs` moved
between subdirs with the same name/size/mtime is invisible to the poll. One-word fix (use the path
relative to the mod dir).

### [ ] L5 — Latent lock inversion between `Mods._gate` and `Coroutines._active`
`Mods/Mods.cs` (`RemoveLifecycle` runs user `OnUnload` while holding `_gate`) vs.
`Host/Coroutines.cs:159` (`Tick` runs user `MoveNext`/predicates while holding `_active`) and `:143`
(`StopAll` takes `_active` from the unload path). Today every caller funnels through the main thread so
it cannot bite, but `StopAll`'s own comment invites the inversion ("whatever thread a mod unload runs
on"). Either narrow the locks to not span user code, or document that unload is main-thread-only and
assert it.

### [ ] L6 — `NativeLibrary.TryLoad` without `Free`
`Safe/Safe.cs:48`. Refcount leak on a module that never unloads anyway — cosmetic.

---

## Notes (no action)

Things checked and found correct, recorded so a later pass doesn't re-derive them:

- **Thunk stack alignment.** Every `call` site in `generic_thunk_post.S` lands at 16-byte alignment:
  `inutil_thunk_post` (`push rbp` → 0 mod 16; `sub 0x90` → 0; `sub 0x80` before the original → 0) and
  `inutil_call_original` (`push rbp/rbx/rsi/rdi` → 8; `sub 0x88` → 0). Stack-arg re-staging reads
  `[rbp+0x30..0x68]` — the caller's 5th+ arg slots — correctly.
- **RetFrame zeroing** (`:60`) makes a `Skip()` with no `SetReturn` deterministic rather than garbage.
- **Copy-on-write hook arrays** (`Hooks.cs:479-480`) with a single `_reg` writer lock: dispatch reads the
  volatile reference once and iterates lock-free — correct, no torn dispatch.
- **`Of` / `OfNative` twin plans** + `SelfTestNativePlan` (`Hooks.cs:1134`) are a genuine machine-checked
  invariant, not a smoke test.
- **`CsModHost`** generation counter (stale-write-wins) and atomic ALC unwind on a failed load are right.
- **`Mods.Discover`** atomic wire/unwind (`Mods.Discover.cs:59-63`) is right.
