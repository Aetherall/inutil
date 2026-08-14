#!/usr/bin/env bash
# One-time (idempotent) setup of the wine-hosted Unity IL2CPP build environment:
# wine prefix + msvc-wine C++ toolchain + Unity editor & Win64 IL2CPP module +
# the wine-compat fixes. Large downloads (~5GB MSVC, ~3GB editor) land in
# INUTIL_UNITY_DIR (default <repo>/.unity-build, gitignored).
#
# After this, run `unity-license` (needs your .env), then `toygame-build`.
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/env.sh"

mkdir -p "$INUTIL_UNITY_DIR" "$DL_DIR"

echo ">> [1/4] wine prefix"
if [ ! -d "$WINEPREFIX/drive_c" ]; then
  wineboot -i >/dev/null 2>&1 || true
fi
mkdir -p "$WINEPREFIX/drive_c/users/$(whoami)/AppData/Local/Unity/licenses" \
         "$WINEPREFIX/drive_c/users/$(whoami)/AppData/Local/Unity/config" \
         "$WINEPREFIX/drive_c/ProgramData/Unity/licenses"

echo ">> [2/4] MSVC toolchain (msvc-wine)"
if [ ! -e "$MSVC_DIR/bin/x64/cl" ]; then
  echo "   downloading MSVC $MSVC_VER + Windows SDK (~5GB, one time)..."
  [ -d "$MSVC_WINE_DIR/.git" ] || git clone --depth 1 https://github.com/mstorsjo/msvc-wine "$MSVC_WINE_DIR"
  python3 "$MSVC_WINE_DIR/vsdownload.py" --accept-license --dest "$MSVC_DIR"
  "$MSVC_WINE_DIR/install.sh" "$MSVC_DIR"
else
  echo "   already present at $MSVC_DIR"
fi

echo ">> [3/4] Unity $UNITY_VERSION editor + Win64 IL2CPP module"
if [ ! -f "$EDITOR_DIR_UNIX/Unity.exe" ]; then
  base="https://download.unity3d.com/download_unity/$UNITY_CHANGESET"
  ED="$DL_DIR/UnitySetup64-$UNITY_VERSION.exe"
  IL="$DL_DIR/UnitySetup-Windows-IL2CPP-$UNITY_VERSION.exe"
  [ -f "$ED" ] || curl -fL -o "$ED" "$base/Windows64EditorInstaller/UnitySetup64-$UNITY_VERSION.exe"
  [ -f "$IL" ] || curl -fL -o "$IL" "$base/TargetSupportInstaller/UnitySetup-Windows-IL2CPP-Support-for-Editor-$UNITY_VERSION.exe"
  echo "   installing editor silently under wine..."
  wine "$ED" /S "/D=C:\\Unity\\$UNITY_VERSION"
  echo "   installing Win64 IL2CPP module..."
  wine "$IL" /S "/D=C:\\Unity\\$UNITY_VERSION"
else
  echo "   already present at $EDITOR_DIR_UNIX"
fi

echo ">> [4/4] wine-compat fixes"
bash "$WINE_FIXES_DIR/prepare-prefix.sh"

echo
echo ">> setup complete."
echo "   next: put Unity creds in $REPO_ROOT/.env, run 'unity-license', then 'toygame-build'."
