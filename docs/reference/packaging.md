# Packaging — the distributable engine bundle

*Reference & plan. The remaining work to make inutil ship a **versioned, deployable bundle**, so a consumer
(the OpenTarkov engine) stops rebuilding inutil from source. This is the concrete form of the "distributable"
that [`limits.md`](./limits.md) → "Packaging & build" flags as roadmap, and the fix for the coupling described
below. Verify against the tree; this is a plan, not a built feature.*

## Why — the coupling to remove

Today inutil has **no release artifact**. Its only consumer, the OpenTarkov engine, builds inutil *from source*
inside its own `pack.sh`: it names inutil's internal project layout (`Inutil.BepInEx.Patcher`, `managed/refstubs`)
and MSBuild props (`-p:RefStubs`, `-p:InteropProxies=Sdk`, `-p:InutilDll=`, `-p:InutilModsDll=`). That is an
**inversion**: the consumer knows how to build inutil. So every inutil refactor breaks the consumer — the v2
rewrite's first commit (`c3bc2bf`) deleted exactly those projects/props, and the engine can't fast-forward its
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

Laid out as (a consumer copies its loader's tree wholesale):

```
dist/<version>/
  bepinex/BepInEx/plugins/   the shared engine + Inutil.BepInEx.dll   — copy beside a BepInEx install
  melonloader/Mods/          the shared engine + Inutil.MelonLoader.dll — copy into a MelonLoader install
  tools/                     inutil-interoppatch, inutil-metadata-extract (runnable)
  manifest.json  MARKER      machine- + human-readable identity + per-loader file→deploy map
```

`manifest.json`'s per-loader `files` lists are **derived from what pack.sh actually stages**, never hand-kept, so
the manifest cannot drift from the bundle.

**Not in the bundle (deliberately):** no BepInEx preloader `patchers/` DLL. v2 patches interop **offline** (next
section), so there is nothing to inject at boot. Any consumer still copying a patcher into `patchers/` is on the
old model.

## The consumption contract (the anti-coupling guarantee)

A consumer depends on **three stable things**, and nothing about inutil's internal project layout:

1. **The bundle layout** above — which files, and where they deploy relative to a loader.
2. **The patch CLI invocation** — `inutil-interoppatch --game <gameDir>` (auto-detects the loader layout) or
   `inutil-interoppatch <interopDir>`. Idempotent; a no-op on an already-patched folder; exit `0` on success,
   `2` on a usage/path error. This is the whole deployment-time interface.
3. **The mod API** — `Inutil.Hooks`, the `ILoad`/`ITick`/`IGui` lifecycles, `MainThread`, `Hook<Game>`,
   `Inutil.Sugar`, and the escape-hatch faces (`Safe`/`Invoke`/`Probe`/`Introspect`/`Fields`). Consumer mod
   code and any ship-as-source SDK compile against these.

If a change would break any of the three, it is a **contract change** and the consumer must be told — that is the
only coupling that remains.

## How deployment works now (the model the bundle assumes)

inutil v2 patches interop **offline, once**, not via an in-memory preloader:

1. Install the loader; boot the game once so Il2CppInterop generates proxies into `BepInEx/interop/`.
2. Run `inutil-interoppatch --game <gameDir>` — flips proxy signatures to natural types, in place, atomically.
3. If skipped, inutil warns loud at startup (`interop proxies look unpatched…`) — never a silent mismatch
   (the content-addressed marker, [roadmap §1](./roadmap.md), makes this a structural check).

The CLI's `--game` auto-locator exists precisely so a launcher/installer can wrap it. This is a better model than
the old preloader (deterministic, idempotent, inspectable, zero per-boot cost) — the bundle commits to it.

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

- **Refstubs for inutil's *own* CI.** The engine's game-free build (via `managed/refstubs`) is gone. Consumers no
  longer need it — they consume the bundle. But inutil's own CI may still want a game-free build path; decide
  whether to re-add refstubs *internally* (inutil's private concern) or always build against the staged
  `.unity-build` fixture. This does **not** block the bundle.
- ~~**MelonLoader in the bundle**~~ — **resolved:** one bundle carries **both** hosts as **per-loader trees**
  (`bepinex/` + `melonloader/`). The shared engine is byte-identical in each, so the second host costs ~one thin
  DLL and keeps inutil's dual-loader-parity property intact in the artifact.
- **Publish channel** — release asset vs committed `dist/` vs (eventually) NuGet for the managed compile-refs.

## Relationship to the engine

The **OpenTarkov engine** is inutil's first (currently only) consumer and the reason this exists. Its side of the
migration — stop rebuilding inutil, consume this bundle, move the patch step into its launcher — is specified in
that repo's `MIGRATION.md`. **Phase 1 here (the `pack` target + a published bundle) is the prerequisite** for the
engine's Phase 2; the engine can bridge on the last pre-rewrite pin (`480354c`) until it lands.
