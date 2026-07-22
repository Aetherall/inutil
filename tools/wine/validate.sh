#!/usr/bin/env bash
# inutil v2 validation driver — the structured, machine-checked replacement for v1's removed run-*.sh
# log-scrapers (../../docs/contribution/architecture/20-testing.md). One loop, one verdict:
#
#   1. build + SELF-TEST the aggregator   — prove the tester itself can't lie BEFORE trusting its verdict
#   2. build the loader battery shim       — the thin per-loader plugin over the shared Suite (../managed/test)
#   3. scrub + deploy it                    — clean slate, no stale plugin from a prior run
#   4. launch the real IL2CPP game          — GE-Proton (umu) in a headless gamescope, same recipe as setup-*
#   5. wait for the battery's `done` record — poll the host-visible JSONL sidecar; force-kill is bounded
#   6. aggregate the sidecar                — exit code IS the verdict (0 green / 1 red), nothing is scraped
#
# Usage:  validate.sh <bepinex|melon> [--fail]
#   --fail  arms INUTIL_SELFTEST_FAIL in the game so the canary case fails — proves a real red run turns the
#           whole loop red end-to-end (not just the offline self-test). Expected exit code 1.
#
# Prereqs: setup-bepinex / setup-melon (provisioned loader tree), inside `devenv shell` (dotnet + Proton).
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/env.sh"

TEST_DIR="$REPO_ROOT/managed/test"
CLI_PROJ="$TEST_DIR/Inutil.TestKit.Cli/Inutil.TestKit.Cli.csproj"
CLI_DLL="$TEST_DIR/Inutil.TestKit.Cli/bin/Release/net9.0/inutil-testkit.dll"

# The IL-rewrite seam (§7.2) + its atomic apply CLI, and the runtime SDK (Inutil.dll) whose WrapTask a flipped
# proxy resolves at load — both built + applied here so the Task family runs end-to-end (§7.3).
PATCH_PROJ="$REPO_ROOT/managed/src/Inutil.InteropPatch.Cli/Inutil.InteropPatch.Cli.csproj"
PATCH_DLL="$REPO_ROOT/managed/src/Inutil.InteropPatch.Cli/bin/Release/net9.0/inutil-interoppatch.dll"
SDK_PROJ="$REPO_ROOT/managed/src/Inutil/Inutil.csproj"
SDK_OUT="$REPO_ROOT/managed/src/Inutil/bin/Release"

LOADER="${1:-}"; shift || true
FAIL=0
for a in "$@"; do [ "$a" = "--fail" ] && FAIL=1; done

case "$LOADER" in
  bepinex)
    LOADER_DIR="$INUTIL_UNITY_DIR/loaders/bepinex"
    DEPLOY="$LOADER_DIR/BepInEx/plugins"
    INTEROP="$LOADER_DIR/BepInEx/interop"     # the generated proxies the patcher flips in place
    PROJ="$TEST_DIR/Battery/Inutil.Battery.BepInEx/Inutil.Battery.BepInEx.csproj"
    LOADER_PROP="BepInEx"
    DLL_OVERRIDE="winhttp=n,b" ;;              # BepInEx injects via the winhttp.dll doorstop proxy
  melon|melonloader)
    LOADER_DIR="$INUTIL_UNITY_DIR/loaders/melonloader"
    DEPLOY="$LOADER_DIR/Mods"
    INTEROP="$LOADER_DIR/MelonLoader/Il2CppAssemblies"
    PROJ="$TEST_DIR/Battery/Inutil.Battery.MelonLoader/Inutil.Battery.MelonLoader.csproj"
    LOADER_PROP="MelonLoader"
    DLL_OVERRIDE="version=n,b" ;;              # MelonLoader injects via the version.dll proxy (NOT winhttp)
  *)
    echo "usage: validate.sh <bepinex|melon> [--fail]" >&2; exit 2 ;;
esac

[ -f "$LOADER_DIR/ToyGame.exe" ] || { echo "!! loader not provisioned at $LOADER_DIR — run 'setup-$LOADER' first" >&2; exit 1; }

SIDECAR="$LOADER_DIR/${SIDECAR_NAME:-inutil-results.jsonl}"

# --- 1. build + self-test the aggregator (test the tester before we trust it) ---------------------
echo ">> building the aggregator"
dotnet build "$CLI_PROJ" -c Release -v q --nologo
echo ">> self-testing the aggregator (must catch every failure mode before we trust its verdict)"
dotnet "$CLI_DLL" selftest || { echo "!! aggregator self-test FAILED — refusing to run the battery against a broken judge" >&2; exit 1; }

# --- 1b. patch the interop proxies (§7.2) + build the SDK the flip resolves to (§7.3) --------------
# The flip must be present in the DEPLOYED proxy before launch (so Il2CppInterop loads the natural return
# type), and Inutil.dll must be beside the loader (so the spliced `call WrapTaskT` resolves at JIT). Both are
# idempotent: re-patching an already-flipped tree writes nothing.
[ -f "$INTEROP/Assembly-CSharp.dll" ] || { echo "!! interop proxies not found at $INTEROP — run 'setup-$LOADER' first" >&2; exit 1; }
echo ">> building the interop patcher + SDK"
dotnet build "$PATCH_PROJ" -c Release -v q --nologo
dotnet build "$SDK_PROJ"   -c Release -v q --nologo -p:Loader="$LOADER_PROP"
# The no-build mod host (Inutil.Mods) + its Roslyn closure — deployed so the §7.9/§7.10 capstone case can
# COMPILE a Hook<Game> mod from source in-game. Absent ⇒ that one case SKIPs; every other case is unaffected.
echo ">> building the no-build mod host (Inutil.Mods + Roslyn) for the §7.9 capstone"
MODS_PROJ="$REPO_ROOT/managed/src/Inutil.Mods/Inutil.Mods.csproj"
dotnet build "$MODS_PROJ" -c Release -v q --nologo -p:Loader="$LOADER_PROP"
MODS_OUT="$(dirname "$MODS_PROJ")/bin/Release"
# Drive the patcher through the v2 --game locator (../../docs/contribution/architecture/16-metadata.md): it re-detects the loader layout
# under $LOADER_DIR and resolves the SAME proxy dir the shell computed as $INTEROP above — dogfooding the locator
# the pillar's multi-stage pass hangs off, instead of passing the dir the shell already knows.
echo ">> patching the interop proxies under $LOADER_DIR (§7.2 atomic in-place, via --game locator)"
dotnet "$PATCH_DLL" --game "$LOADER_DIR" || { echo "!! interop patch failed" >&2; exit 1; }

# --- 1c. build the native hook engine (§7.7) so the SDK's LoaderAdapter can [DllImport] it -----------
# inutil_core.dll (core/interceptor.c + generic_thunk_post.S, built SHARED) is the substrate-agnostic hook
# engine. Cross-compiled with the vendored mingw toolchain; deployed beside Inutil.dll so the plugin's
# [DllImport("inutil_core")] resolves. Idempotent (Ninja no-ops an unchanged tree).
echo ">> building the native hook engine (inutil_core.dll)"
cmake -S "$REPO_ROOT/native" -B "$REPO_ROOT/native/build" -G Ninja -Wno-dev \
      -DCMAKE_TOOLCHAIN_FILE="$REPO_ROOT/native/cmake/toolchain-mingw-w64.cmake" >/dev/null \
  || { echo "!! native core cmake configure failed" >&2; exit 1; }
ninja -C "$REPO_ROOT/native/build" inutil_core >/dev/null || { echo "!! native hook engine build failed" >&2; exit 1; }
CORE_DLL="$REPO_ROOT/native/build/inutil_core.dll"
[ -f "$CORE_DLL" ] || { echo "!! inutil_core.dll not produced at $CORE_DLL" >&2; exit 1; }

# --- 1d. extract the metadata wire map (§7.13 / ../../docs/contribution/architecture/16-metadata.md) — the pillar's author-time pass ----------
# Recover the authored JSON wire names the IL2CPP proxy STRIPS (Cpp2IL over GameAssembly.dll + global-metadata.dat,
# via the same --game locator) into the interop dir's inutil.wiremap.json, beside the proxies — the same sidecar
# InteropPatch/WireMap.cs reads at patch time. Run for BOTH loaders: the wire-map keys REAL il2cpp class names, and
# WireMap.ForType's Il2Cpp-prefix bridge lets the same recovered map drive BepInEx (ToyGame.*) and MelonLoader
# (Il2CppToyGame.*) alike.
echo ">> extracting the wire map (inutil.wiremap.json) for the interop patch (§7.13 / metadata pillar)"
META_PROJ="$REPO_ROOT/managed/src/Inutil.Metadata.Cli/Inutil.Metadata.Cli.csproj"
META_DLL="$REPO_ROOT/managed/src/Inutil.Metadata.Cli/bin/Release/net9.0/inutil-metadata-extract.dll"
dotnet build "$META_PROJ" -c Release -v q --nologo
dotnet "$META_DLL" --game "$LOADER_DIR" || { echo "!! metadata extract failed" >&2; exit 1; }

# --- 2. build the loader battery shim -------------------------------------------------------------
echo ">> building the $LOADER battery shim"
dotnet build "$PROJ" -c Release -v q --nologo -p:Loader="$LOADER_PROP"
SHIM_OUT="$(dirname "$PROJ")/bin/Release"

# --- 3. scrub + deploy ----------------------------------------------------------------------------
mkdir -p "$DEPLOY"
scrub_inutil_deploy "$DEPLOY"                       # remove any Inutil*.dll a prior run left here
cp "$SHIM_OUT"/Inutil.Battery.*.dll "$SHIM_OUT"/Inutil.TestKit.dll "$DEPLOY"/
cp "$MODS_OUT"/*.dll "$DEPLOY"/                     # Inutil.Mods.dll + the Roslyn closure (the §7.9 capstone compiler)
cp "$SDK_OUT"/Inutil.dll "$SDK_OUT"/Inutil.Schema.dll "$DEPLOY"/   # the runtime SDK — overwrites Mods's copy with the canonical -p:Loader build
cp "$CORE_DLL" "$DEPLOY"/                           # the native hook engine the SDK's LoaderAdapter [DllImport]s (§7.7)
# NB The in-game .cs mod compile (§7.9 capstone) + the REPL both reference the interop proxies. The interop patch
# now normalizes each proxy's System.Private.CoreLib reference to the game's runtime BCL version
# (PatchDirectory.NormalizeCoreLibRef), so the compile is BCL-consistent with the net6 runtime — no staged net9
# shared framework needed (this replaced the former dotnet9/ CS1705 workaround).
rm -f "$SIDECAR"                                    # never read a prior run's results
echo ">> deployed $(ls "$DEPLOY"/Inutil.Battery.*.dll | xargs -n1 basename) + Inutil.TestKit.dll + Inutil.dll + Inutil.Mods.dll + inutil_core.dll -> $DEPLOY"

# --- 4. launch the real game (setup-*.sh recipe: GE-Proton via umu in a headless gamescope) -------
PROTON="${INUTIL_PROTON:?INUTIL_PROTON unset — enter 'devenv shell'}"
GS="${INUTIL_GAMESCOPE:-gamescope}"
[ -d "$PROTON/files" ]        || { echo "!! GE-Proton not found at $PROTON" >&2; exit 1; }
command -v umu-run >/dev/null || { echo "!! umu-run not on PATH — enter 'devenv shell'" >&2; exit 1; }

RUN_PREFIX="$LOADER_DIR.prefix"; mkdir -p "$RUN_PREFIX"
FAIL_ENV=(); [ "$FAIL" = 1 ] && { FAIL_ENV=(INUTIL_SELFTEST_FAIL=1); echo ">> canary armed: INUTIL_SELFTEST_FAIL=1 (expecting a RED run)"; }

cd "$LOADER_DIR"
echo ">> launching $LOADER over ToyGame under GE-Proton (headless gamescope)"
set +e
setsid env GAMEID="umu-0" STORE="none" PROTONPATH="$PROTON" WINEPREFIX="$RUN_PREFIX" \
       WINEDLLOVERRIDES="$DLL_OVERRIDE" "${FAIL_ENV[@]}" \
  "$GS" --backend headless -W 1280 -H 720 -- \
      umu-run ./ToyGame.exe -logFile "$LOADER_DIR/player.log" >"$LOADER_DIR/run.log" 2>&1 &
GS_PID=$!

# --- 5. wait for the battery's `done` record, then tear down --------------------------------------
# The battery writes its sidecar at chainload (before the fixture's own 3s scene watchdog), so `done`
# normally lands within the launch window. Poll for it; a generous timeout backstops a wedged guest.
DEADLINE=$(( SECONDS + ${INUTIL_VALIDATE_TIMEOUT:-240} ))
done_seen=0
while kill -0 "$GS_PID" 2>/dev/null; do
  if [ -f "$SIDECAR" ] && grep -q '"kind":"done"' "$SIDECAR" 2>/dev/null; then done_seen=1; break; fi
  [ "$SECONDS" -ge "$DEADLINE" ] && { echo ">> (timed out after ${INUTIL_VALIDATE_TIMEOUT:-240}s waiting for the battery to finish)"; break; }
  sleep 2
done
kill -TERM "$GS_PID" 2>/dev/null; kill -TERM -- "-$GS_PID" 2>/dev/null
wait "$GS_PID" 2>/dev/null
set -e
[ "$done_seen" = 1 ] && echo ">> battery reported done; tearing down" \
                     || echo ">> battery did NOT report done — the aggregator will judge the partial/absent sidecar as red"

# --- 6. aggregate: the exit code IS the verdict ---------------------------------------------------
echo ">> ── verdict ──────────────────────────────────────────────"
dotnet "$CLI_DLL" aggregate "$SIDECAR"
