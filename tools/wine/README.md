# tools/wine — building a real IL2CPP game under wine, on Linux

This directory builds **`testgame/ToyGame`** into a real **Win64 IL2CPP** player —
`GameAssembly.dll` + `global-metadata.dat` — entirely under **wine on Linux**, with
no Windows machine. That artifact is the real `il2cpp` runtime the interceptor is
validated against (a game-agnostic stand-in for any IL2CPP Unity title).

Getting Unity's 2022.3 editor to do a headless IL2CPP build under wine needs three
fixes that aren't obvious; they're the reason this directory exists.

## Workflow (devenv scripts)

```sh
unity-setup     # one-time: wine prefix + msvc-wine toolchain + Unity editor + fixes
unity-license   # activate Unity Personal for this prefix (needs your own .env)
toygame-build   # build testgame/ToyGame -> Win64 IL2CPP artifacts
```

Output lands in `$INUTIL_UNITY_DIR/prefix/drive_c/Build/`
(`INUTIL_UNITY_DIR` defaults to `<repo>/.unity-build`, gitignored, multi-GB —
override it, e.g. in `devenv.local.nix`, to reuse an install elsewhere).

## Provisioning a real loader (BepInEx / MelonLoader)

Once `toygame-build` has produced a player, these lay a real loader over a copy of it and let it
generate its own first-run Il2CppInterop proxies (Cpp2IL) — the same proxies `InteropPatch --game`
will later target:

```sh
setup-bepinex     # lay pinned BepInEx 6 (IL2CPP) over a ToyGame copy + first-run interop; idempotent, --force to redo
setup-melon       # lay pinned MelonLoader v0.7.3 over a ToyGame copy + first-run interop; idempotent, --force to redo
```

Both launch the game once under **GE-Proton (umu)** in a **headless gamescope** to let the loader's
preloader run — bare wine can't load `winex11.drv` here, so pressure-vessel supplies the GPU driver,
and the raw (no-setcap) gamescope avoids a `bwrap` capability abort. The Proton runner + raw gamescope
are provided by `devenv.nix` (`INUTIL_PROTON` / `INUTIL_GAMESCOPE`).

**v1's loader-validation harness (build + deploy inutil, launch, scrape PASS/FAIL from the log) was
removed as part of the ground-up rewrite** — see `../../docs/contribution/02-system-map.md`, and specifically
the v2 validation design, which found the old harness's verdict-aggregation itself was broken (it ignored most of the results
it scraped) and calls for a structured, machine-checked replacement rather than a port of the old
scripts. Build the v2 equivalent against `setup-bepinex`/`setup-melon`'s output once v2's artifacts
exist.

## Licensing

`unity-license` reads **your own** Unity credentials from `<repo>/.env`
(`USERNAME=` / `PASSWORD=`, see `.env.example`). A **free Unity Personal** account
is sufficient. `.env` is gitignored; credentials never enter the repo or the
conversation. Two non-obvious gotchas are handled in `activate-license.sh`:

- Creds are extracted with `sed`, **not `source`** — `source` silently truncates a
  value containing shell-special characters, yielding a `401 Input Error`.
- The activated entitlement is written to two locations; if they differ, Unity's
  resolver rejects **both** ("duplicated entitlement group ids"). We reconcile them.

## The three wine-compat fixes (`prepare-prefix.sh`)

| Fix | Why |
|-----|-----|
| **`trigger_shim.c`** replaces `Unity.ILPP.Trigger.exe` | The editor's IL-Post-Processor connectivity check calls `File.Exists(\\.\pipe\…)`. Under wine `GetFileAttributes`/`FindFirstFile` on the `\\.\pipe\` namespace returns `ERROR_BAD_DEV_TYPE`, so `File.Exists` is false even though the pipe is fully connectable. The real Trigger throws "Can't find file" and the build hangs forever. The shim checks via `WaitNamedPipe`/`CreateFile` (which work) and exits 0. |
| **`vswhere_shim.c`** at the standard VS Installer path | Unity's IL2CPP Bee toolchain locator runs `vswhere -format xml` to find a VS C++ toolchain. msvc-wine has the full VS layout but isn't registered, so real vswhere reports nothing. The shim reports msvc-wine as VS 2022. |
| **Windows 10 SDK registry key** | Bee reads `HKLM\SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows\v10.0\InstallationFolder`. We point it at msvc-wine's `Windows Kits\10`. |

Plus a wine `hosts` file with `localhost` (the ILPP runner is reached over a named
pipe, but `localhost` must resolve for the editor↔runner handshake).

## Files

- `env.sh` — shared config; all paths derive from `INUTIL_UNITY_DIR` (+ `scrub_inutil_deploy`, the hermetic-deploy scrub).
- `setup-unity.sh` — installs prefix + msvc-wine + editor + module, applies fixes.
- `activate-license.sh` — Unity Personal activation from `.env`.
- `prepare-prefix.sh` — (re)applies the three fixes; idempotent.
- `build-toygame.sh` — stages the project and runs the batchmode IL2CPP build.
- `setup-bepinex.sh` / `setup-melon.sh` — provision a loader + first-run interop over a ToyGame copy.
- `trigger_shim.c`, `vswhere_shim.c` — the two shims (built with mingw on demand).

v1's `lib.sh` (shared build/deploy/launch mechanism for the removed `run-*.sh` validators) and the
validators themselves were removed with the rest of the old implementation — see the note above.
