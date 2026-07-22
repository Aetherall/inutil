#!/usr/bin/env bash
# Provision the canonical BepInEx 6 (IL2CPP) loader tree that a future v2 validate harness
# runs against — reproducibly, from a fresh ToyGame build, with ZERO manual staging. It lays a
# PINNED BepInEx build over a copy of the real player, then does the one-time FIRST RUN under
# GE-Proton (umu) in a headless gamescope so BepInEx's preloader generates its OWN Il2CppInterop
# assemblies (Cpp2IL -> Il2CppInterop.Generator, against the real GameAssembly.dll) before any
# plugin loads. After this, `bepinex-validate` builds + deploys the inutil plugin (+ companion) and runs the demo.
#
# Idempotent: a no-op if the tree is already provisioned (game + BepInEx core + interop present).
# Pass --force to wipe and reprovision (uses the cached zip if present; DOES regenerate interop —
# several minutes of Cpp2IL). The run prefix (loaders/bepinex.prefix) is left intact either way.
#
# Prereq: `toygame-build` (the real Win64 IL2CPP player at $WINEPREFIX/drive_c/Build). Run inside
# `devenv shell` (provides dotnet/cmake on the build side and INUTIL_PROTON/INUTIL_GAMESCOPE/umu-run
# on the run side).
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/env.sh"

# --- pinned BepInEx: 6.0.0-be.755 IL2CPP win-x64 -------------------------------------------------
# The build our plugin binds to (the inutil host plugin references BepInEx's own be.755 core assemblies) and
# our doorstop_config.ini matches. builds.bepinex.dev names the artifact with the SHORT commit hash.
BEPINEX_BUILD="755"
BEPINEX_HASH="3fab71a"                       # short hash in the artifact name
BEPINEX_VER="6.0.0-be.${BEPINEX_BUILD}"
BEPINEX_ZIP="BepInEx-Unity.IL2CPP-win-x64-${BEPINEX_VER}+${BEPINEX_HASH}.zip"
BEPINEX_URL="https://builds.bepinex.dev/projects/bepinex_be/${BEPINEX_BUILD}/BepInEx-Unity.IL2CPP-win-x64-${BEPINEX_VER}%2B${BEPINEX_HASH}.zip"

LOADER_DIR="$INUTIL_UNITY_DIR/loaders/bepinex"
BEP="$LOADER_DIR/BepInEx"
LOG="$BEP/LogOutput.log"
GAME_SRC="$WINEPREFIX/drive_c/Build"         # toygame-build's output (real Win64 IL2CPP player)
DL_DIR="$INUTIL_UNITY_DIR/dl"

FORCE=0; [ "${1:-}" = "--force" ] && FORCE=1

# --- preconditions --------------------------------------------------------------------------------
[ -f "$GAME_SRC/ToyGame.exe" ] || { echo "!! no built game at $GAME_SRC — run 'toygame-build' first" >&2; exit 1; }

# --- idempotency: already provisioned? -----------------------------------------------------------
if [ "$FORCE" = 0 ] && [ -f "$LOADER_DIR/ToyGame.exe" ] \
   && [ -f "$BEP/core/BepInEx.Unity.IL2CPP.dll" ] && [ -f "$BEP/interop/Assembly-CSharp.dll" ]; then
  echo ">> already provisioned: $LOADER_DIR"
  echo ">>   game + BepInEx $BEPINEX_VER + $(ls "$BEP/interop"/*.dll 2>/dev/null | wc -l) interop assemblies"
  echo ">>   (pass --force to wipe and reprovision; then 'bepinex-validate')"
  exit 0
fi

# --- 1. fresh game copy --------------------------------------------------------------------------
echo ">> provisioning BepInEx $BEPINEX_VER loader tree at $LOADER_DIR"
echo ">>   wiping any prior tree (the run prefix at $LOADER_DIR.prefix is left intact)"
rm -rf "$LOADER_DIR"
mkdir -p "$LOADER_DIR"
echo ">> copying the real ToyGame player from $GAME_SRC ..."
cp -a "$GAME_SRC"/. "$LOADER_DIR"/
# Drop the IL2CPP debug-symbol backup — large, marked "DontShipItWithYourGame", runtime-irrelevant
# (Cpp2IL/interop gen works off GameAssembly.dll + global-metadata.dat, not these symbols).
rm -rf "$LOADER_DIR/ToyGame_BackUpThisFolder_ButDontShipItWithYourGame"

# --- 2. lay down pinned BepInEx (doorstop winhttp proxy + core + dotnet CoreCLR) ------------------
mkdir -p "$DL_DIR"
ZIP="$DL_DIR/$BEPINEX_ZIP"
if [ ! -s "$ZIP" ]; then
  echo ">> downloading $BEPINEX_ZIP ..."
  curl -fSL --retry 3 -o "$ZIP" "$BEPINEX_URL" || { echo "!! download failed: $BEPINEX_URL" >&2; exit 1; }
fi
echo ">> unpacking BepInEx over the game dir ..."
python3 -m zipfile -e "$ZIP" "$LOADER_DIR"   # IL2CPP zips unpack to the root (no wrapper dir)
[ -f "$BEP/core/BepInEx.Unity.IL2CPP.dll" ]                              || { echo "!! BepInEx unpack incomplete ($BEP/core missing)" >&2; exit 1; }
[ -f "$LOADER_DIR/winhttp.dll" ] && [ -f "$LOADER_DIR/doorstop_config.ini" ] || { echo "!! doorstop (winhttp.dll/doorstop_config.ini) missing after unpack" >&2; exit 1; }

# --- 3. first run: BepInEx generates its OWN Il2CppInterop assemblies (NO plugin yet) -------------
# Same launch recipe a v2 validate harness will use (GE-Proton via umu inside a headless gamescope; WINEDLLOVERRIDES
# loads the LOCAL winhttp.dll doorstop proxy). With zero plugins, BepInEx's preloader runs Cpp2IL +
# Il2CppInterop.Generator against the real GameAssembly.dll, writes BepInEx/interop/*.dll, then the
# chainloader starts. We tear the tree down the instant "Chainloader startup complete" lands (interop
# is fully written by then); a generous timeout backstops the one-time Cpp2IL pass + first prefix init.
PROTON="${INUTIL_PROTON:?INUTIL_PROTON unset — enter 'devenv shell' (points at a GE-Proton steamcompattool dir)}"
GS="${INUTIL_GAMESCOPE:-gamescope}"
[ -d "$PROTON/files" ]        || { echo "!! GE-Proton not found at $PROTON (set INUTIL_PROTON)" >&2; exit 1; }
command -v umu-run >/dev/null || { echo "!! umu-run not on PATH — enter 'devenv shell' (provides pkgs.umu-launcher)" >&2; exit 1; }

RUN_PREFIX="$LOADER_DIR.prefix"
mkdir -p "$RUN_PREFIX"
rm -f "$LOG"
cd "$LOADER_DIR"
echo ">> first run under GE-Proton (umu) in headless gamescope — generating Il2CppInterop assemblies"
echo ">>   (Cpp2IL over the real GameAssembly.dll; this is the slow one-time step)"
set +e
setsid env GAMEID="umu-0" STORE="none" PROTONPATH="$PROTON" WINEPREFIX="$RUN_PREFIX" \
       WINEDLLOVERRIDES="winhttp=n,b" \
  "$GS" --backend headless -W 1280 -H 720 -- \
      umu-run ./ToyGame.exe -logFile "$LOADER_DIR/player.log" >"$LOADER_DIR/run.log" 2>&1 &
GS_PID=$!

# Trigger-based teardown: kill the whole tree the instant BepInEx finishes coming up (interop done).
( tail -n +1 -F --pid="$GS_PID" "$LOG" 2>/dev/null | while IFS= read -r l; do
    case "$l" in
      *"Chainloader startup complete"*)
        kill -TERM "$GS_PID" 2>/dev/null; kill -TERM -- "-$GS_PID" 2>/dev/null; break ;;
    esac
  done ) & WATCH_PID=$!

timeout "${INUTIL_SETUP_TIMEOUT:-600}" tail --pid="$GS_PID" -f /dev/null 2>/dev/null
rc=$?
[ "$rc" = 124 ] && echo ">> (interop gen did not finish within ${INUTIL_SETUP_TIMEOUT:-600}s — tearing down; see run.log/player.log)"
kill -TERM "$GS_PID" 2>/dev/null; kill -TERM -- "-$GS_PID" 2>/dev/null
wait "$GS_PID"   2>/dev/null
wait "$WATCH_PID" 2>/dev/null
set -e

# --- 4. verify interop generated -----------------------------------------------------------------
n=$(ls "$BEP/interop"/*.dll 2>/dev/null | wc -l)
[ -f "$BEP/interop/Assembly-CSharp.dll" ] || { echo "!! first run did not generate interop ($BEP/interop empty); see run.log/player.log/$LOG" >&2; exit 1; }
echo ">> SUCCESS — provisioned $LOADER_DIR"
echo ">>   game + BepInEx $BEPINEX_VER + $n interop assemblies"
echo ">> next: 'bepinex-validate'"
