#!/usr/bin/env bash
# Provision the canonical MelonLoader (IL2CPP) loader tree that a future v2 validate harness runs
# against — reproducibly, from a fresh ToyGame build, with ZERO manual staging. The MelonLoader analog
# of setup-bepinex.sh: lay a PINNED MelonLoader build over a copy of the real player, then do the
# one-time FIRST RUN under GE-Proton (umu) in a headless gamescope so MelonLoader's preloader generates
# its OWN Il2CppInterop assemblies (Cpp2IL -> Il2CppInterop.Generator, against the real GameAssembly.dll,
# into MelonLoader/Il2CppAssemblies/) before any mod loads. After this, `melon-validate` builds + deploys
# the inutil plugin (+ SampleMod) and runs the Demo + M-live proof.
#
# MelonLoader injects via a `version.dll` proxy (where BepInEx uses winhttp.dll) + a MelonLoader/ dir; the
# x64 release zip drops exactly those over the game root. First run writes ~70 Il2CppAssemblies. The
# RemoteAPI 50x errors in the log are MelonLoader's obfuscation-map lookup failing gracefully (our game
# isn't obfuscated) — harmless; generation proceeds offline from the local GameAssembly.dll.
#
# Idempotent: a no-op if the tree is already provisioned (game + MelonLoader core + interop present).
# Pass --force to wipe and reprovision (uses the cached zip if present; DOES regenerate interop —
# minutes of Cpp2IL). The run prefix (loaders/melonloader.prefix) is left intact either way.
#
# Prereq: `toygame-build` (the real Win64 IL2CPP player at $WINEPREFIX/drive_c/Build). Run inside
# `devenv shell` (provides dotnet/cmake on the build side and INUTIL_PROTON/INUTIL_GAMESCOPE/umu-run
# on the run side).
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/env.sh"

# --- pinned MelonLoader: v0.7.3 Open-Beta, x64 (net6 / Il2CppInterop backend) ---------------------
# The build our plugin binds to (the inutil host plugin references MelonLoader's own net6 core + Il2CppInterop
# 1.5.1). LavaGang ships the Windows x64 build as MelonLoader.x64.zip (Linux/macOS variants are for
# native Linux/Mac games — we run the Windows player under Proton, so x64 is correct).
MELON_VER="0.7.3"
MELON_ZIP="MelonLoader.x64.zip"
MELON_URL="https://github.com/LavaGang/MelonLoader/releases/download/v${MELON_VER}/${MELON_ZIP}"

LOADER_DIR="$INUTIL_UNITY_DIR/loaders/melonloader"
ML="$LOADER_DIR/MelonLoader"
LOG="$ML/Latest.log"
GAME_SRC="$WINEPREFIX/drive_c/Build"         # toygame-build's output (real Win64 IL2CPP player)
DL_DIR="$INUTIL_UNITY_DIR/dl"

FORCE=0; [ "${1:-}" = "--force" ] && FORCE=1

# --- preconditions --------------------------------------------------------------------------------
[ -f "$GAME_SRC/ToyGame.exe" ] || { echo "!! no built game at $GAME_SRC — run 'toygame-build' first" >&2; exit 1; }

# --- idempotency: already provisioned? -----------------------------------------------------------
if [ "$FORCE" = 0 ] && [ -f "$LOADER_DIR/ToyGame.exe" ] && [ -f "$LOADER_DIR/version.dll" ] \
   && [ -f "$ML/net6/MelonLoader.dll" ] && [ -f "$ML/Il2CppAssemblies/Assembly-CSharp.dll" ]; then
  echo ">> already provisioned: $LOADER_DIR"
  echo ">>   game + MelonLoader v$MELON_VER + $(ls "$ML/Il2CppAssemblies"/*.dll 2>/dev/null | wc -l) interop assemblies"
  echo ">>   (pass --force to wipe and reprovision; then 'melon-validate')"
  exit 0
fi

# --- 1. fresh game copy --------------------------------------------------------------------------
echo ">> provisioning MelonLoader v$MELON_VER loader tree at $LOADER_DIR"
echo ">>   wiping any prior tree (the run prefix at $LOADER_DIR.prefix is left intact)"
rm -rf "$LOADER_DIR"
mkdir -p "$LOADER_DIR"
echo ">> copying the real ToyGame player from $GAME_SRC ..."
cp -a "$GAME_SRC"/. "$LOADER_DIR"/
# Drop the IL2CPP debug-symbol backup — large, marked "DontShipItWithYourGame", runtime-irrelevant.
rm -rf "$LOADER_DIR/ToyGame_BackUpThisFolder_ButDontShipItWithYourGame"

# --- 2. lay down pinned MelonLoader (version.dll proxy + MelonLoader/ runtime) --------------------
mkdir -p "$DL_DIR"
ZIP="$DL_DIR/$MELON_ZIP"
if [ ! -s "$ZIP" ]; then
  echo ">> downloading $MELON_ZIP ..."
  curl -fSL --retry 3 -o "$ZIP" "$MELON_URL" || { echo "!! download failed: $MELON_URL" >&2; exit 1; }
fi
echo ">> unpacking MelonLoader over the game dir ..."
python3 -m zipfile -e "$ZIP" "$LOADER_DIR"   # the x64 zip unpacks to the root: version.dll + MelonLoader/
[ -f "$ML/net6/MelonLoader.dll" ]                                       || { echo "!! MelonLoader unpack incomplete ($ML/net6 missing)" >&2; exit 1; }
[ -f "$LOADER_DIR/version.dll" ]                                        || { echo "!! version.dll proxy missing after unpack" >&2; exit 1; }

# --- 3. first run: MelonLoader generates its OWN Il2CppInterop assemblies (NO mod yet) ------------
# Same launch recipe a v2 validate harness will use (GE-Proton via umu inside a headless gamescope; WINEDLLOVERRIDES
# loads the LOCAL version.dll proxy). With zero mods, MelonLoader's preloader runs Cpp2IL +
# Il2CppInterop.Generator against the real GameAssembly.dll, writes MelonLoader/Il2CppAssemblies/*.dll,
# then loads mods (zero) and starts. We tear the tree down the instant "Mods loaded." lands (interop is
# fully written by then); a generous timeout backstops the one-time Cpp2IL pass + first prefix init.
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
       WINEDLLOVERRIDES="version=n,b" \
  "$GS" --backend headless -W 1280 -H 720 -- \
      umu-run ./ToyGame.exe -logFile "$LOADER_DIR/player.log" >"$LOADER_DIR/run.log" 2>&1 &
GS_PID=$!

# Trigger-based teardown: kill the whole tree the instant MelonLoader finishes loading mods (interop
# is fully written before "Loading Mods..."; the count line "N Mod(s) loaded." prints just after).
( tail -n +1 -F --pid="$GS_PID" "$LOG" 2>/dev/null | while IFS= read -r l; do
    case "$l" in
      *"Mods loaded."*|*"Mod loaded."*)
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
n=$(ls "$ML/Il2CppAssemblies"/*.dll 2>/dev/null | wc -l)
[ -f "$ML/Il2CppAssemblies/Assembly-CSharp.dll" ] || { echo "!! first run did not generate interop ($ML/Il2CppAssemblies empty); see run.log/player.log/$LOG" >&2; exit 1; }
echo ">> SUCCESS — provisioned $LOADER_DIR"
echo ">>   game + MelonLoader v$MELON_VER + $n interop assemblies"
echo ">> next: 'melon-validate'"
