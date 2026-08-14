# Loaders — the BepInEx & MelonLoader shims

*The only per-loader code in the tree. Read [the system map](../02-system-map.md) first. These two shims
attach the shared engine and drive the one per-frame seam owned by [mod-host (15)](./15-mod-host.md)
(`FrameDriver`); at attach they read the content marker stamped by [interop-patch (11)](./11-interop-patch.md).*

## Job

`Inutil.BepInEx` and `Inutil.MelonLoader` are the entrypoint plugins a loader loads. Each does exactly four
things and nothing else: **derive the loader's plugin base** (`BasePlugin` / `MelonMod`), **`Attach` the
loader-agnostic engine** (`LoaderAdapter`, which lives in `Inutil.dll`), **route a per-frame pump into the
ONE `FrameDriver` seam**, and **start the no-build `.cs` mod loop**. Everything below the shim — the hook
engine, `Hook<T>`, the marshaller, `FrameDriver`, the three mod lifecycles — is shared *verbatim* across both
loaders. The shim is the substrate adapter, not a second copy of the engine.

If you remember one sentence: **the per-loader surface is a plugin base + a per-frame pump; both pumps
one-line into the same `FrameDriver` — so the loaders cannot drift on what a frame does.**

## In the tree

The two shim files are separate per-loader assemblies (`Inutil.BepInEx.dll`, `Inutil.MelonLoader.dll`); the
three files they lean on live in the shared SDK (`Inutil.dll`) and know nothing about either loader.

| File | Holds |
|---|---|
| `managed/src/Inutil.BepInEx/BepInExPlugin.cs` | the `#if BEPINEX` `BasePlugin`; `InstallPump` + the injected `InutilPump` MonoBehaviour; `StartCsModLoop` + its ref dirs |
| `managed/src/Inutil.MelonLoader/MelonLoaderMod.cs` | the `#if MELONLOADER` `MelonMod`; `OnUpdate`/`OnGUI`; `StartCsModLoop` + its ref dirs |
| `managed/src/Inutil/Host/LoaderAdapter.cs` | `Attach`/`Detach` — the loader-**agnostic** engine wiring (`[DllImport]` of `inutil_core.dll`; shared) |
| `managed/src/Inutil/Host/LoaderShim.cs` | `WarnIfInteropUnpatched` / `CheckInterop` — the shared interop-marker safety net (shared) |
| `managed/src/Inutil/Host/FrameDriver.cs` | the ONE per-frame sequence both shims push into ([15](./15-mod-host.md)) |

## Design

**Each shim is a thin adapter over the same engine.** `BepInExPlugin.Load` and
`InutilMelonMod.OnInitializeMelon` do the same four steps in the same order: route the loader's log into
`Inutil.Hooks.Hooks.OnWarning`, `LoaderAdapter.Attach()`, `LoaderShim.WarnIfInteropUnpatched(…)`, then
`StartCsModLoop()`. The *only* structural difference between them is where the per-frame tick comes from.

**BepInEx has no per-frame hook, so the shim injects one.** `BasePlugin` is not a `MonoBehaviour` and BepInEx
exposes no plugin-facing per-frame callback, so `InstallPump` registers and instantiates a resident
`InutilPump` MonoBehaviour (`ClassInjector.RegisterTypeInIl2Cpp<InutilPump>`, a hidden
`HideAndDontSave` `GameObject`, `DontDestroyOnLoad`). `InutilPump.Update()` calls `FrameDriver.Tick()` and
`OnGUI()` calls `FrameDriver.Gui()` — that is the shim's entire per-frame body. The pump roots *only* this
already-permanent plugin context, never a collectible mod ALC.

**MelonLoader gets the tick for free.** MelonLoader's own resident support module raises `OnUpdate` every
frame on the main thread, and `MelonMod` exposes `OnGUI` — no injection needed. `OnUpdate() => FrameDriver.Tick()`
and `OnGUI() => FrameDriver.Gui()`. Same two one-liners as the BepInEx pump, different source of the callback.

**Both one-line into `FrameDriver` — there is no second copy of "drain + tick + gui."** `FrameDriver.Tick()`
is `MainThread.Drain()` (which also steps coroutines) then `Inutil.Mods.Tick()`; `FrameDriver.Gui()` is
`Inutil.Mods.Gui()`. Neither shim spells that sequence; each just *pushes a frame in*. That single seam is the
whole point of the module — see **Why**.

**`LoaderAdapter.Attach/Detach` wires the engine, loader-agnostically.** Because the loader has *already*
`il2cpp_init`'d, embedded CoreCLR, and started Il2CppInterop, `Attach` does **no** interop bring-up — it takes
`&` of the four `[UnmanagedCallersOnly]` dispatchers (we are managed, so this is just `&`), `[DllImport]`s
`inutil_core.dll` (byte-for-byte `native/core/interceptor.c` built SHARED), calls `HostCore.Init(install)`,
and sets the invoker / proceed / vtable-install / vtable-restore callbacks. `Detach` is
`inutil_interceptor_shutdown()`. Every Il2CppInterop-based loader hands a plugin the identical world, which is
why the same adapter serves both — "two loaders, one core, zero core changes."

**`LoaderShim.WarnIfInteropUnpatched` reads the page-11 marker at attach.** Right after `Attach`, each shim
passes its loader's proxy dir to the one shared check, which reads the content-addressed marker the interop
patcher stamped (`SchemaMarker.InteropMarkerFileName`) and compares it to *this* build's own registry hash
(`SchemaMarker.Hash(Families.Default())`). `Missing` and `Stale` warn loud and actionably; `Current` is
silent. It is a plain on-disk read (no `Assembly.Load`), so it is correct before the loader lazily loads the
proxies. `CheckInterop` is the single comparison both this warning and the in-game battery gate call.

**How a shim is selected.** Each shim is its own project with its own `<DefineConstants>` (`BEPINEX` /
`MELONLOADER`) guarding its single `#if` file, deploying `Inutil.BepInEx.dll` to `BepInEx/plugins` or
`Inutil.MelonLoader.dll` to the game's `Mods` dir. The `-p:Loader` build property is what the build path
(`validate.sh`, the setup guide) threads to the *shared* SDK and the battery projects so they bind the
loader's proxy tree and namespace. `StartCsModLoop` is `[MethodImpl(NoInlining)]` and called inside a `try`,
so its `Inutil.Mods` (Roslyn-carrying) reference is JIT'd there — an absent mod host is **non-fatal**: the
engine and built-DLL mods stay fully working.

## Invariants

- **One per-frame sequence, pushed IN by both shims (P1).** `FrameDriver.Tick`/`Gui` is the single
  implementation; both pumps call it and neither spells drain/tick/gui. The confirmed HIGH `ITick`/`IGui`-inert
  v1 bug **cannot recur** — there is no second copy to forget half of.
- **The loader-specific facts are each named once.** The proxy namespace prefix (BepInEx strips `Il2Cpp`,
  MelonLoader keeps it), the deploy dir (`plugins` vs `Mods`), the proxy dir (`interop` vs `Il2CppAssemblies`),
  and the interop-core dir (`core` vs `net6`) each appear in exactly one shim's `refDirs` / marker call.
- **Interop-unpatched warns loud at attach (P4).** A proxy tree that was never patched, or patched by a
  different schema, surfaces a precise warning before a mod dies later with a cryptic `MissingMethodException`.
- **The engine wiring is shared, not per-loader.** `Attach`/`Detach`, `WarnIfInteropUnpatched`, and `FrameDriver`
  live in `Inutil.dll`; the shim only supplies the plugin base and the per-frame callback source.

## Limits, defers & TODOs

- **The `Il2Cpp`-prefix divergence a loader-agnostic mod must alias.** BepInEx strips the prefix
  (`ToyGame.*`), MelonLoader keeps it (`Il2CppToyGame.*`), so cross-loader source aliases the game namespace
  (`using Game = ToyGame` / `Il2CppToyGame`). This is a loader fact the *mod author* sees, not a shim bug.
- **The framework/game classifier had to learn the same prefix fact** ([reference/limits.md](../../reference/limits.md) P4, the `IsFrameworkAssembly`
  melon fix). MelonLoader prefixes the game's *own* secondary module (`Il2CppToyGame.Core`), so a blanket
  `Il2Cpp*==framework` rule mis-classed it as framework and the cross-module virtual `Task` root never flipped.
  `CecilProjector.IsFrameworkAssembly` now strips an optional `Il2Cpp` before classifying the remainder.
- **`Detach` is whole-engine shutdown.** A finer-grained collectible-ALC hot-reload will lean on a narrower
  teardown; for now unload tears the whole interceptor down (`LoaderAdapter` note).
- **Everything fails loud.** An absent `Inutil.Mods` warns and continues; a failed interceptor init throws; a
  stale/missing marker warns — no silent degradation.

## Tests

- **In-game — the full battery under BOTH loaders at parity.** `validate.sh bepinex|melon` runs the identical
  suite (BepInEx 77/77, MelonLoader 77/77 per [reference/limits.md](../../reference/limits.md) P4), so the shared `FrameDriver` / `LoaderAdapter` /
  `LoaderShim` are proven live under each loader; the `ModHostCases` capstone specifically proves `FrameDriver`
  drives a compiled `Hook<Game>` mod's `ITick`. See [testing (20)](./20-testing.md).
- **Offline — `FrameworkAssemblyTests` pins the prefix handling.** It asserts `IsFrameworkAssembly` classifies
  by real framework identity, not a bare `Il2Cpp` prefix, against *both* loaders' spellings (`ToyGame.Core`
  and `Il2CppToyGame.Core` are game; every real framework proxy stays framework) — no game boot required.

## Why it's shaped this way

In v1 the MelonLoader host was a **second, incomplete copy** of "drive the per-frame seams." Its `OnUpdate`
did only `MainThread.Drain()` — never `Mods.Tick()` — and there was no `OnGUI` at all, so `ITick`/`IGui` mods
were silently inert under MelonLoader while working under BepInEx (the confirmed HIGH bug). The two hosts
agreed by discipline and drifted. Unifying on one `FrameDriver` that both shims merely *push a frame into*
retires that failure at the structural level: with a single implementation there is no second copy to forget
half of. The native side already proved the same discipline one layer down (`LoaderAdapter` / `inutil_core.dll`
— two loaders, one core, zero core changes); this is that pattern one layer up. Read the header comments in
both shim files and `FrameDriver.cs` before you touch either — they name the exact bug the shape prevents.
