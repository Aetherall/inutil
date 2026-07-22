#!/usr/bin/env bash
# Build the ToyGame as a Win64 IL2CPP player under wine, producing the real
# il2cpp surface the interceptor validates against:
#   <state>/prefix/drive_c/Build/{ToyGame.exe, GameAssembly.dll}
#   .../ToyGame_Data/il2cpp_data/Metadata/global-metadata.dat
#
# Prereqs: `unity-setup` (editor + msvc-wine) and `unity-license` (Personal seat).
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/env.sh"

[ -f "$EDITOR_DIR_UNIX/Unity.exe" ] || { echo "!! editor missing — run 'unity-setup'" >&2; exit 1; }

# Ensure the wine-compat fixes are in place (idempotent).
bash "$WINE_FIXES_DIR/prepare-prefix.sh"

# Stage the toy-game source into the prefix C: drive (keep the Library cache for
# fast rebuilds; only the source dirs are refreshed from the repo).
STAGED="$WINEPREFIX/drive_c/ToyGame"
mkdir -p "$STAGED"
for d in Assets ProjectSettings Packages; do
  [ -d "$TOYGAME_SRC/$d" ] || continue
  rm -rf "$STAGED/$d"; cp -r "$TOYGAME_SRC/$d" "$STAGED/$d"
done
rm -rf "$STAGED/Temp"          # clear any stale UnityLockfile from a killed run

# --- MSVC developer environment ---
# Bee detects the toolchain via the vswhere shim + SDK registry (prepare-prefix),
# then drives cl.exe itself; these vars mirror a VS dev prompt as belt-and-suspenders.
VCT="$MSVC_WIN\\VC\\Tools\\MSVC\\$MSVC_VER"
WK="$MSVC_WIN\\Windows Kits\\10"
export VCINSTALLDIR="$MSVC_WIN\\VC\\"
export VCToolsInstallDir="$VCT\\"
export VCToolsVersion="$MSVC_VER"
export WindowsSdkDir="$WK\\"
export WindowsSDKVersion="$SDK_VER\\"
export INCLUDE="$VCT\\include;$WK\\Include\\$SDK_VER\\ucrt;$WK\\Include\\$SDK_VER\\shared;$WK\\Include\\$SDK_VER\\um;$WK\\Include\\$SDK_VER\\winrt;$WK\\Include\\$SDK_VER\\cppwinrt"
export LIB="$VCT\\lib\\x64;$WK\\Lib\\$SDK_VER\\ucrt\\x64;$WK\\Lib\\$SDK_VER\\um\\x64"
export WINEPATH="$VCT\\bin\\Hostx64\\x64;$WK\\bin\\$SDK_VER\\x64"
# ILPP runner Kestrel host off the default :80; editor reaches the runner via named pipe.
export ASPNETCORE_URLS='http://127.0.0.1:0'
# Read by the vswhere shim + Bee toolchain detection.
export INUTIL_MSVC_WIN="$MSVC_WIN"
export VS170COMNTOOLS="$MSVC_WIN\\Common7\\Tools\\"
export VS160COMNTOOLS="$MSVC_WIN\\Common7\\Tools\\"

LOG="$WINEPREFIX/drive_c/build.log"
rm -f "$LOG"
echo ">> building ToyGame (Win64 IL2CPP) under wine — a few minutes..."
wine "$EDITOR_WIN" -batchmode -nographics -quit \
  -projectPath 'C:\ToyGame' \
  -executeMethod Builder.BuildWin64IL2CPP \
  -out 'C:\Build' \
  -logFile 'C:\build.log' &
WINE_PID=$!

# Unity's mono runtime often can't abort its threads on exit under wine
# ("abort_threads: Failed aborting"), so the editor process hangs AFTER a
# successful build. Watch the log for the result marker rather than waiting on
# the process, then reap the straggler editor + licensing daemon.
result=""
for _ in $(seq 1 200); do            # up to ~20 min
  sleep 6
  if grep -aq 'Build Finished, Result:' "$LOG" 2>/dev/null; then
    result="$(tr -d '\r' < "$LOG" | grep -ao 'Build Finished, Result: [A-Za-z]*' | tail -1)"
    break
  fi
  kill -0 "$WINE_PID" 2>/dev/null || break   # editor exited without a result
done
pkill -9 -f '[U]nity.exe' 2>/dev/null || true
pkill -9 -f '[U]nity.Licensing' 2>/dev/null || true
wait "$WINE_PID" 2>/dev/null || true

echo ">> ${result:-build did not reach a result marker}"
echo ">> artifacts:"
ok=1
for a in "Build/ToyGame.exe" "Build/GameAssembly.dll" "Build/ToyGame_Data/il2cpp_data/Metadata/global-metadata.dat"; do
  f="$WINEPREFIX/drive_c/$a"
  if [ -f "$f" ]; then printf '   \342\234\223 %s  %s\n' "$(du -h "$f" | cut -f1)" "$a"; else echo "   ✗ MISSING: $a"; ok=0; fi
done
[ "$ok" = 1 ] && echo ">> SUCCESS — GameAssembly.dll + global-metadata.dat ready at $WINEPREFIX/drive_c/Build" || { echo ">> build did not produce all artifacts; see $WINEPREFIX/drive_c/build.log" >&2; exit 1; }
