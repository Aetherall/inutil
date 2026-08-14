# Packaging — the distributable engine bundle

*Reference & plan. The remaining work to make inutil ship a **versioned, deployable bundle**, so a consumer
(the OpenTarkov engine) stops rebuilding inutil from source. This is the concrete form of the "distributable"
that [`limits.md`](./limits.md) → "Packaging & build" flags as roadmap, and the fix for the coupling described
below. Parts of this page are BUILT (the bundle, both CLIs, the game-free mode) and parts are still plan
(version tie, publish channel) — each section says which. Verify against the tree.*

## Why — the coupling to remove

> **Status:** largely discharged. `tools/pack.sh` exists and produces the bundle, including a **game-free**
> mode (`PACK_BEPINEX_DIR` + the committed refstubs) that needs no provisioned game — and per its own docs the
> OpenTarkov consumer now ships the engine via that bundle rather than by rebuilding inutil's projects. What
> remains is the schema-marker version tie and a publish channel ("Remaining work" 4–5). The history below is
> kept because it explains the shape of the contract.

Historically inutil had **no release artifact**. Its only consumer, the OpenTarkov engine, built inutil *from source*
inside its own `pack.sh`: it named inutil's internal project layout (`Inutil.BepInEx.Patcher`, `managed/refstubs`)
and MSBuild props (`-p:RefStubs`, `-p:InteropProxies=Sdk`, `-p:InutilDll=`, `-p:InutilModsDll=`). That was an
**inversion**: the consumer knew how to build inutil. So every inutil refactor broke the consumer — the v2
rewrite's first commit (`c3bc2bf`) deleted exactly those projects/props, and the engine could not fast-forward its
submodule past it even though v2 is a strict descendant of the engine's pin.

The fix: **inutil owns producing its own bundle; the consumer consumes the artifact.** After that, inutil can
refactor its internals freely — the consumer depends only on the bundle's stable shape and the mod API (which
the rewrite preserved: `Hooks`, `ILoad`/`ITick`/`IGui`, `MainThread`, `Hook<>`, `Inutil.Sugar`).

## What ships — the bundle manifest

The bundle is the game-agnostic engine, laid out per loader. It is produced by [`tools/pack.sh`](../../tools/pack.sh)
— the staging that used to live in the engine's `pack.sh` (steps 4–5b), re-homed here and generalized off any one
consumer. The **shared engine** (everything marked "both trees") is loader-invariant — built **once** and copied
byte-identically into each tree; only the ~one thin host DLL differs per loader.

| Artifact | Source project | In the bundle | Notes |
|---|---|---|---|
| `Inutil.dll` | `Inutil` | both trees | the SDK — game-agnostic (names only Il2CppInterop + `Il2Cppmscorlib` + BCL) |
| `Inutil.Schema.dll` | `Inutil.Schema` | both trees | the pure schema engine (Conv tree / correspondence / planner) `Inutil.dll` depends on |
| `Inutil.Mods.dll` | `Inutil.Mods` | both trees | the no-build `.cs` mod host + REPL (drags the Roslyn closure) |
| `Microsoft.CodeAnalysis*.dll` | Roslyn closure of `Inutil.Mods` | both trees | the 4-DLL scripting closure the mod host + REPL need |
| `inutil_core.dll` | `native/` (mingw + ninja) | both trees | the MinHook interceptor + SEH fault guard; game-agnostic |
| `Inutil.BepInEx.dll` | `Inutil.BepInEx` | `bepinex/` only | the thin `BasePlugin` host |
| `Inutil.MelonLoader.dll` | `Inutil.MelonLoader` | `melonloader/` only | the thin `MelonMod` twin |
| `inutil-interoppatch` | `Inutil.InteropPatch.Cli` | `tools/` (runnable) | **the offline patcher** — the whole deployment-time interface (see below) |
| `inutil-metadata-extract` | `Inutil.Metadata.Cli` | `tools/` (runnable, optional) | recovers wire names for `inutil-interoppatch` to re-attach ([architecture 16](../contribution/architecture/16-metadata.md)); carries a Cpp2IL closure |
| `inutil-check` | `Inutil.Check.Cli` | `tools/` (runnable, optional) | the offline dev-check CLI — compiles a mod through the TARGET tree's own `CsModCompiler` (so an offline check == a hot-reload, structurally), plus Cecil `query`/`methods`/`dump` over its proxies |
| `Inutil.BepInEx.Patcher.dll` | `Inutil.BepInEx.Patcher` | `bepinex/BepInEx/patchers/` | the preloader patcher — applies the SAME `PatchModule` in memory, so a boot patches before any plugin resolves a game type |

Laid out as (a consumer copies its loader's tree wholesale):

```
dist/<version>/
  bepinex/BepInEx/plugins/   the shared engine + Inutil.BepInEx.dll   — copy beside a BepInEx install
  melonloader/Mods/          the shared engine + Inutil.MelonLoader.dll — copy into a MelonLoader install
  tools/                     inutil-interoppatch, inutil-metadata-extract, inutil-check (runnable)
  manifest.json  MARKER      machine- + human-readable identity + per-loader file→deploy map
```

`manifest.json`'s per-loader `files` lists are **derived from what pack.sh actually stages**, never hand-kept, so
the manifest cannot drift from the bundle.

**The preloader patcher IS in the bundle.** An earlier revision of this page said it was deliberately absent
because v2 patches offline. That is wrong on the artifact: `tools/pack.sh` stages `Inutil.BepInEx.Patcher.dll`
into `bepinex/BepInEx/patchers/`, and the host plugin also patches at boot. Both drive the SAME
`InteropPatcher.PatchModule` — one rewrite implementation, applied either on disk (the CLI, offline) or in
memory (the preloader, before any plugin resolves a game type). The offline CLI remains the *deployment*
contract below; the boot-time path is what keeps a freshly-generated or regenerated interop dir correct without
a manual step.

## The consumption contract (the anti-coupling guarantee)

A consumer depends on **four stable things**, and nothing about inutil's internal project layout:

1. **The bundle layout** above — which files, and where they deploy relative to a loader.
2. **The patch CLI invocation** — `inutil-interoppatch --game <gameDir>` (auto-detects the loader layout) or
   `inutil-interoppatch <interopDir>`. Idempotent; a no-op on an already-patched folder; exit `0` on success,
   `2` on a usage/path error. This is the whole deployment-time interface.
3. **The check CLI invocation** — `inutil-check check <bepDir> <modDir>` (offline type-check of a mod against
   that tree's own proxies + engine), plus `query`/`methods`/`dump` for proxy discovery. Exit `0` clean, `1`
   diagnostics, `2` setup. It late-binds the TARGET tree's `Inutil.Mods.dll`, so the offline check and the
   in-process hot-reload are the same compiler — there is no second implementation to drift.
4. **The mod API** — `Inutil.Hooks`, the `ILoad`/`ITick`/`IGui` lifecycles, `MainThread`, `Hook<Game>`,
   `Inutil.Sugar`, the escape-hatch faces (`Safe`/`Invoke`/`Probe`/`Introspect`/`Fields`), and the wire seam
   (`Inutil.Json`/`Inutil.Wire` — see [guide 5](../guide/05-wire-json.md)). Consumer mod code and any
   ship-as-source SDK compile against these.

If a change would break any of the four, it is a **contract change** and the consumer must be told — that is the
only coupling that remains.

## How deployment works now (the model the bundle assumes)

The **offline CLI is the deployment contract** — the step an installer runs and a consumer can inspect:

1. Install the loader; boot the game once so Il2CppInterop generates proxies into `BepInEx/interop/`.
2. Run `inutil-interoppatch --game <gameDir>` — flips proxy signatures to natural types, in place, atomically.
3. If skipped, inutil warns loud at startup (`interop proxies look unpatched…`) — never a silent mismatch
   (the content-addressed marker, [roadmap §1](./roadmap.md), makes this a structural check).

The CLI's `--game` auto-locator exists precisely so a launcher/installer can wrap it. Offline is the better
*deployment* model (deterministic, idempotent, inspectable, zero per-boot cost) — that is what the bundle
commits to.

The boot-time patcher is not an alternative to that model but a **backstop for a regenerated interop dir**: a
game update, a file verify, or a wiped profile rewrites `interop/` from scratch, and the proxies are unpatched
again until something patches them. Both paths call the same `InteropPatcher.PatchModule` over the same schema
registry, so "patched on disk" and "patched in memory" cannot diverge — and both stamp/read the same marker, so
whichever ran, the state is inspectable afterwards.

## Remaining work (ordered)

1. ✅ **Freeze the manifest** — done. The table above is the frozen set (both loaders in one bundle, per-loader
   trees); `tools/pack.sh` derives `manifest.json`'s file lists from what it stages, so it can't drift.
2. ✅ **A `pack` target** — done: [`tools/pack.sh`](../../tools/pack.sh). Builds Release, builds the native core
   (mingw + ninja), stages both loader trees + the Roslyn closure into `dist/<version>/`. Runs in ~6 s off the
   provisioned `.unity-build` refs; requires no booted game (it builds the game-agnostic engine).
3. ✅ **Ship `inutil-interoppatch` as a runnable** — done: `pack.sh` `dotnet publish`es both CLIs into `tools/`
   (framework-dependent by default; `PACK_RID` + `PACK_SELF_CONTAINED=1` produce a self-contained Windows tool
   for a launcher host without a .NET runtime). Verified standalone: no-args → usage/exit 2, `--game <bad>` →
   loud error/exit 2.
4. ⏳ **Version the bundle** — partial. `pack.sh` stamps git-describe identity into `manifest.json` + `MARKER`.
   The remaining piece is the **schema content-marker tie**: fill `manifest.json`'s `schemaMarker` (currently
   `null`) from `SchemaMarker.Hash(Families.Default())`, so a consumer can detect a bundle-vs-patched-proxy drift
   with the same fail-loud check the loader does at Attach. Needs a `marker` verb on the patch CLI (it already
   references `SchemaMarker`) for `pack.sh` to shell out to.
5. ⬜ **Publish** — open. `dist/` is gitignored (built, not committed). Decide where a released bundle lives: a
   GitHub release asset, or a committed `dist/` to bootstrap.
6. ⬜ **Lock the contract** — this page now matches what `pack.sh` produces; lock it as the consumer-facing
   interface once publish (5) lands.

## Open decisions

- ~~**Refstubs for inutil's *own* CI**~~ — **resolved:** the committed refstubs (`managed/refstubs`) are back and
  are a first-class `pack.sh` mode. `PACK_BEPINEX_DIR` selects the GAME-FREE build: the refstubs stand in for the
  fixture's generated interop proxies and the pinned BepInEx zip supplies the loader core, so the bundle builds
  with no provisioned game at all (`-p:RefStubs`). That is also how a consumer's CI builds the engine.
- ~~**MelonLoader in the bundle**~~ — **resolved:** one bundle carries **both** hosts as **per-loader trees**
  (`bepinex/` + `melonloader/`). The shared engine is byte-identical in each, so the second host costs ~one thin
  DLL and keeps inutil's dual-loader-parity property intact in the artifact.
- **Publish channel** — release asset vs committed `dist/` vs (eventually) NuGet for the managed compile-refs.

## Relationship to the engine

The **OpenTarkov engine** is inutil's first (currently only) consumer and the reason this exists. Its side of the
migration — stop rebuilding inutil, consume this bundle, move the patch step into its launcher — is specified in
that repo's `MIGRATION.md`. **Phase 1 here (the `pack` target + a published bundle) is the prerequisite** for the
engine's Phase 2; the engine can bridge on the last pre-rewrite pin (`480354c`) until it lands.
