#!/usr/bin/env bash
# Apply the wine-compat fixes that make Unity 2022.3 IL2CPP batchmode builds work
# under wine. Idempotent — safe to re-run. Assumes the editor + msvc-wine are
# already installed (see setup-unity.sh). Run automatically by build-toygame.sh.
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/env.sh"

[ -d "$EDITOR_DIR_UNIX" ] || { echo "!! editor not installed at $EDITOR_DIR_UNIX — run 'unity-setup' first" >&2; exit 1; }
[ -d "$MSVC_DIR" ]        || { echo "!! msvc-wine not installed at $MSVC_DIR — run 'unity-setup' first" >&2; exit 1; }

GCC="$(mingw_gcc)" || { echo "!! x86_64-w64-mingw32-gcc not found (enter devenv shell)" >&2; exit 1; }

echo ">> [1/4] wine localhost resolution (ILPP runner reachability)"
HOSTS="$WINEPREFIX/drive_c/windows/system32/drivers/etc/hosts"
mkdir -p "$(dirname "$HOSTS")"
printf '127.0.0.1\tlocalhost\n::1\t\tlocalhost\n' > "$HOSTS"

echo ">> [2/4] build + install Unity.ILPP.Trigger shim (fixes File.Exists-on-pipe gap)"
"$GCC" -O2 -o "$INUTIL_UNITY_DIR/trigger_shim.exe" "$WINE_FIXES_DIR/trigger_shim.c"
if [ -f "$TRIGGER_EXE" ] && [ ! -f "$TRIGGER_EXE.orig" ]; then cp "$TRIGGER_EXE" "$TRIGGER_EXE.orig"; fi
cp "$INUTIL_UNITY_DIR/trigger_shim.exe" "$TRIGGER_EXE"

echo ">> [3/4] build + install vswhere shim (points Unity's Bee locator at msvc-wine)"
"$GCC" -O2 -o "$INUTIL_UNITY_DIR/vswhere_shim.exe" "$WINE_FIXES_DIR/vswhere_shim.c"
mkdir -p "$VSWHERE_DIR_UNIX"
cp "$INUTIL_UNITY_DIR/vswhere_shim.exe" "$VSWHERE_DIR_UNIX/vswhere.exe"

echo ">> [4/4] Windows 10 SDK registry key (Bee reads InstallationFolder)"
SDK_WIN="$MSVC_WIN\\Windows Kits\\10\\"
for HIVE in \
  'HKLM\SOFTWARE\Wow6432Node\Microsoft\Microsoft SDKs\Windows\v10.0' \
  'HKLM\SOFTWARE\Microsoft\Microsoft SDKs\Windows\v10.0'; do
  wine reg add "$HIVE" /v InstallationFolder /d "$SDK_WIN" /f >/dev/null 2>&1
  wine reg add "$HIVE" /v ProductVersion /d "$SDK_VER_SHORT" /f >/dev/null 2>&1
done

echo ">> prefix prepared."
