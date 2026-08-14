# Mod host — the three lifecycles, the compiler, and scheduling

*The mod-execution layer — where a `.cs` file on disk becomes a live hook in a running frame. Read [the system
map](../02-system-map.md) first. The ergonomic `Hook<T>` / `HookMatch` discovery this host drives is
[hook-ergonomics (14)](./14-hook-ergonomics.md); the in-game REPL built on the same pump is [repl (18)](./18-repl.md);
the per-loader shims that push the frame tick in are [loaders (19)](./19-loaders.md).*

## Job

This layer answers *"how does a plain `.cs` file the modder dropped on disk become a running mod — compiled,
loaded, hooked, ticked every frame, and cleanly torn down when it changes?"* It owns three things: the **three
`.cs` lifecycles** (a shared-source library, a load-once coremod, a hot-reload mod), the **in-process compiler**
that turns their sources into a real assembly, and the **scheduling substrate** — the one per-frame seam, the
main-thread pump, the coroutine runner — that every loaded mod (and the REPL) runs on. It owns no hook *engine*
and no *marshalling*; it drives the discovery/install path ([14](./14-hook-ergonomics.md)) and the boundary
conversion ([12](./12-marshal.md)) rather than reimplementing them.

## In the tree

`managed/src/Inutil.Mods/` — the Roslyn-carrying host, kept **out** of the SDK (`Inutil.dll` stays lean; a game
without `.cs` mods ships no compiler). `managed/src/Inutil/Host/` — the substrate-agnostic scheduling primitives,
in `Inutil.dll` so any consumer (mods, REPL, both loaders) reuses one copy.

| File | Holds |
|---|---|
| `Inutil.Mods/CsModHost.cs` | the **hot-reload** lifecycle — poll+debounce, `ModSlot` generation currency, atomic load/unwind, main-thread dispatch |
| `Inutil.Mods/Coremods.cs` | the **load-once, permanent** lifecycle — `LoadSync`, synchronous and pre-menu, never torn down |
| `Inutil.Mods/ModLibs.cs` | the **shared-source library** lifecycle — emit-to-disk (`<name>.dll`) + host-context load so consumers bind one identity |
| `Inutil.Mods/CsModCompiler.cs` | the in-process Roslyn compiler — ref-dir scan, `IsManaged` native-PE skip, PE+PDB emit, cached reference set |
| `Inutil/Host/ModContext.cs` | the collectible `AssemblyLoadContext` — one mod assembly, the host's shared copies |
| `Inutil/Host/FrameDriver.cs` | the ONE per-frame seam both loaders push into — `Tick()` / `Gui()` |
| `Inutil/Host/MainThread.cs` | the main-thread pump — `Post`/`Drain`/`NextFrame`/`Until`/`Pin`, the pre-pump guard |
| `Inutil/Host/Coroutines.cs` | the per-frame coroutine runner — the `Wait` vocabulary, error-isolated stepping |
| `Inutil/Mods/Mods.cs`, `Mods.Discover.cs` | the lifecycle registry + `Discover`/`RemoveAll` the host drives (the ergonomic tier is [14](./14-hook-ergonomics.md)) |
| `Inutil/Host/{HostCore,LoaderAdapter}.cs` | the native engine-attach seam — referenced, owned by [loaders (19)](./19-loaders.md) |

## Design

**Three lifecycles, loaded in one order: libs → coremods → hot-reload mods.** Both loader shims call the same
three-step sequence — `ModLibs.LoadSync` → `Coremods.LoadSync` → `CsModHost.Start` — because each later stage may
reference the earlier. They share one `CsModCompiler` and one `Mods.Discover`, yet are **three named types, not one
with flags** (philosophy §3, *name, don't merge*): *timing and lifetime are different contracts.*

- **`ModLibs`** — the code-sharing primitive. Compiles each `<libsDir>/<name>/*.cs` into a **stable, named DLL
  emitted to disk** at the libsDir root (rebuilt only when a source is newer — `IsStale`), then loads it into the
  **host's** context (`AssemblyLoadContext.GetLoadContext(typeof(ModLibs).Assembly)`), so a consumer mod compiled
  against that on-disk DLL binds to the **same loaded instance** — one type identity, not a private copy. libsDir
  is on the ref-dir list, so coremods and mods see it by name.
- **`Coremods`** — the load-once, permanent lifecycle. Compiles + loads + `Discover`s each `<coreDir>/<name>/*.cs`
  **synchronously on the calling (chainload) thread, before the menu**, so a boot-time fix's detour is live before
  the game loads. A coremod loads into its own collectible ALC but is **rooted forever** (`_rooted`) — it never
  hot-reloads and is never torn down.
- **`CsModHost`** — the hot-reload lifecycle. Watches `<modsRoot>/<name>/*.cs` and, on each change, recompiles and
  swaps the running generation through the collectible-ALC primitives. This is the only one of the three that
  unloads.

**The collectible `ModContext`.** One mod assembly per ALC, loaded via `LoadFromStream`/`LoadFromAssemblyPath`,
**sharing** the host's already-loaded assemblies (`Inutil.dll`, the interop proxies, `Il2CppInterop`) so the mod's
hook lands in the host's one true table and type identity matches. The subtle rule: it defers shared references to
the context that loaded `ModContext`'s **own** assembly — the host plugin's context — **not** `AssemblyLoadContext.Default`.
A plain null-return would fall back to Default and spawn *fresh* copies from the app base (two `Il2CppInterop`
runtimes, one initialized — broken identity). The mod being the **sole host-side root** of its ALC is what lets a
full teardown drop it to collection.

**`CsModCompiler` — mirror the live runtime's reference set exactly.** It references every managed DLL found across
the same on-disk dirs the loader itself loaded (the BCL `dotnet/`, the interop proxies, `Il2CppInterop` + the
loader core, the SDK `Inutil.dll`), de-duped by simple name (first dir wins), native PEs skipped (`IsManaged` —
`PEReader.HasMetadata`), the runtime BCL appended last as a guaranteed fallback. So a clean compile equals a clean
load. It emits `Debug` (faithful hot-reload stack traces + a PDB) and suppresses the three ref-version diagnostics
(`CS1701`/`CS1702`/`CS1705`) the proxies' newer `System.Private.CoreLib` would otherwise trip — runtime binding is
correct; this is the standard config for compiling against il2cpp proxies. The MetadataReferences are cached on the
dir list + each dir's mtime, so a steady-state recompile reuses them.

**One per-frame seam: `FrameDriver`.** `Tick()` is `MainThread.Drain()` then `Inutil.Mods.Tick()` — drain the
posted-action queue, step coroutines (inside `Drain`), *then* drive every mod's `ITick`; `Gui()` drives every mod's
`IGui` from the loader's own `OnGUI` cadence. Both shims — BepInEx's injected `InutilPump.Update` and MelonLoader's
`OnUpdate`/`OnGUI` — call **this**; neither hand-rolls the sequence. That is the whole point (see **Why**): with one
`FrameDriver` there is no second copy of "drive the per-frame seams" to forget half of.

**`MainThread` + `Coroutines` — the async substrate.** Most il2cpp/Unity APIs must run on the main thread, and a
hook's `HookContext` frame is valid only for the synchronous call — so the sound pattern is *capture, then `Post` a
closure that does the il2cpp work on the next tick*. `MainThread` is a `ConcurrentQueue<Action>` drained in a
**snapshot budget** (an action that itself posts runs next tick, never starving the frame), each action
exception-isolated to `Hooks.OnWarning`. `Post<T>` (result-bearing, awaitable off-thread) carries the one v2
**addition**: a pre-pump guard that *fails loud* rather than enqueueing into a queue nothing can drain yet (P4 —
fail loud beats hang forever). `Coroutines` is a tiny **owned**, pure-managed scheduler (its own `Wait` vocabulary —
`Frames`/`Seconds`/`Until`/`While`/nested), ticked once per frame by `Drain` *after* the action queue, one
`MoveNext` per coroutine per tick, faults isolated. Both were **ported essentially verbatim from v1** (the review
found no confirmed bugs here); the only change is the guard.

**`CsModHost` closes two structural races.** Change detection is a polled **signature** (each `.cs`'s relpath + size
+ mtime), a burst of saves **debounced** into one reload ~250ms after the last. The reload compiles on a **worker
thread** (pure CPU) and posts only the il2cpp-touching load/teardown to the main thread. Two races the v2 rewrite
fixes:

1. **Generation currency.** `ModSlot.Gen` is a monotonic request counter. A completed compile applies **only if it
   is still the slot's latest generation** — a slow compile of an *older* edit that finishes late is discarded, never
   overwriting a newer applied result.
2. **Atomic load.** `LoadOnMain` tears down the previous generation, loads the fresh bytes into a new `ModContext`,
   and `Discover`s — and on **any** failure unwinds the ALC it created (`Mods.RemoveAll` + `Hooks.RemoveAll` +
   `Unload`). No half-loaded generation, no orphaned collectible context. `Mods.Discover` is itself atomic (it
   disposes every hook it installed if any method's wiring throws), so the two compose.

## Invariants

- **One per-frame sequence, one definition (P1).** Every frame flows through the single `FrameDriver.Tick`/`Gui`;
  there is no per-loader copy. This is the invariant that retires the `ITick`/`IGui`-inert bug (see **Why**).
- **Every il2cpp-touching step runs on the main thread.** Load / `Discover` / teardown are posted via
  `MainThread.Post` (or, for a coremod, called synchronously *on* the chainload thread); the Roslyn compile stays
  **off** it. A hook body that needs a later frame `Post`s or `Start`s a coroutine and resumes on the main thread.
- **A compile error keeps the previous generation.** `ReloadOnBg` logs the diagnostics and returns; the running mod
  keeps running. A compile is never a teardown.
- **A failed load unwinds its ALC — no orphan.** The freshly-created `ModContext` must not outlive a throwing
  load/`Discover`; it is `RemoveAll`'d and `Unload`ed before the log line.
- **Every per-frame / lifecycle / teardown callback is exception-isolated (P4).** A throw in a mod's `Tick`,
  `OnGui`, `OnLoad`, `OnUnload`, a posted action, or a coroutine step routes to `Hooks.OnWarning` and never stalls
  the frame or a sibling mod.
- **Full teardown un-roots the collectible ALC.** One `Mods.RemoveAll` disposes the mod's ergonomic hook
  subscriptions (host-static closures the engine's own `RemoveAll` can't reach) **and** its raw engine hooks **and**
  fires `OnUnload`; then `Unload` — the mod was the sole host-side root, so it collects. The installed detours stay
  put (they point at the host's stable `Dispatch`); only the per-mod hook arrays shrink.

## Limits, defers & TODOs

- **Polling, not `FileSystemWatcher` — deliberate.** Under Wine/Proton a Windows-.NET `FileSystemWatcher` does not
  reliably see host-side writes through the pressure-vessel bind mount, so a polled signature (1s timer) is the
  portable choice. Not a stopgap.
- **Everything fails loud (P4).** A compile error, a load failure, a compiler that throws, an emit that can't write
  a lib DLL — each surfaces a precise log line or `OnWarning`; none half-applies silently. A modder always sees why
  a mod didn't go live.
- **The coroutine runner is not a Unity-coroutine reimplementation.** It deliberately does **not** interpret
  `UnityEngine.YieldInstruction` types, so it stays loader- and game-agnostic and validates identically on both
  loaders; `Wait.Seconds` is real wall time (it ignores `Time.timeScale`). A consumer needing true Unity fidelity
  (`WaitForSeconds`, `AsyncOperation`) calls their loader's own facility — this is the marshal-and-step primitive.
- **`LoaderAdapter.Detach` is whole-engine shutdown.** Per-mod hot-reload teardown is already fine-grained
  (`RemoveAll` + `Unload`); the *engine*-level detach stays coarse — noted at the seam ([19](./19-loaders.md)).

## Tests

- **Offline** (`managed/src/Inutil.Mods.Tests/Program.cs`): the end-to-end lifecycle on a **real compiled mod** —
  `CsModCompiler` compiles a `ILoad`/`ITick`/`IUnload` mod from source → it loads into a collectible `ModContext` →
  `Mods.Discover` fires `OnLoad` → `FrameDriver.Tick` drives its `ITick` → `Mods.RemoveAll` fires `OnUnload` and the
  ALC `Unload`s — each seam asserted via a marker file (the mod lives in its own ALC). A lifecycle mod touches no
  il2cpp, so the whole path runs in the offline gate. It also asserts the three lifecycle types are **distinct**
  (`Coremods`/`ModLibs`/`CsModHost`), and covers the REPL + `HookMatch` halves in the same gate
  ([18](./18-repl.md) / [14](./14-hook-ergonomics.md)).
- **In-game** (`managed/test/Battery`, `ModHostCases`): the **capstone** `modhost.capstone.compiled-hook` — a mod
  compiled from source whose ergonomic `Hook<Game>` fires against the real il2cpp method (arg doubled via
  `Proceed`), whose `ITick` `FrameDriver` drives, and whose teardown removes the hook (the delta returns to
  baseline). Plus the container-arg, container-widen, interface-impl, generic-inference, and value-type cases that
  drive the ergonomic tier + shared engine end-to-end under **both** loaders. See [testing (20)](./20-testing.md).

## Why it's shaped this way

v1's mod loop shipped three bugs this shape retires **structurally**:

1. **The un-Unloaded orphan ALC.** A load/`Discover` that threw left its collectible context alive forever — a
   permanent orphan (v1's `CsModHost.cs:308`). `LoadOnMain` now unwinds the ALC it created on any failure, and
   `Mods.Discover` disposes every hook it installed if wiring throws — so no half-loaded generation is observable.
2. **The stale-write-wins race.** A slow compile of an *old* edit could finish after a newer edit's applied result
   and overwrite it. `ModSlot.Gen` is the currency token; a completed compile applies only if still latest.
3. **The per-frame divergence between loaders.** MelonLoader mods with `ITick`/`IGui` were **silently inert** (v1's
   `MelonLoaderMod.cs:61`) — a *second, incomplete copy* of "drive the per-frame seams". `FrameDriver` is the one
   copy both shims push into, so there is no second implementation to forget half of; the bug becomes structurally
   impossible.

The through-line is the discipline the native side already proved — one `inutil_core`, two loaders, zero core
changes ([19](./19-loaders.md)) — applied one layer up: **distinct where the contracts genuinely differ** (three
lifecycles, three names) and **single where a fact must agree** (one frame seam, one compiler, one collectible-ALC
primitive). Merge the three lifecycles to save lines and you couple timing/lifetime contracts that need to diverge;
fork the frame seam per loader and you re-open bug 3 the day someone edits one copy. Change either and read this
section first.
