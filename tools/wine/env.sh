#!/usr/bin/env bash
# Shared configuration for the wine-hosted Unity IL2CPP toy-game pipeline.
# Sourced by setup-unity.sh / activate-license.sh / build-toygame.sh.
#
# Everything derives from INUTIL_UNITY_DIR — the (large, gitignored) state dir
# holding the wine prefix, the Unity editor, and the msvc-wine toolchain.
# Default: <repo>/.unity-build. Override by exporting INUTIL_UNITY_DIR, e.g. in
# devenv.local.nix, to reuse an existing install elsewhere.

set -euo pipefail

# Repo root (devenv exports DEVENV_ROOT; fall back to git when run bare).
REPO_ROOT="${DEVENV_ROOT:-$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse --show-toplevel)}"

# In-repo by default (gitignored). Exported so `dotnet build` picks it up as an MSBuild
# property — the managed csprojs resolve the loader trees from $(INUTIL_UNITY_DIR).
export INUTIL_UNITY_DIR="${INUTIL_UNITY_DIR:-$REPO_ROOT/.unity-build}"
UNITY_VERSION="${UNITY_VERSION:-2022.3.62f3}"
UNITY_CHANGESET="${UNITY_CHANGESET:-96770f904ca7}"
MSVC_VER="${MSVC_VER:-14.51.36231}"
SDK_VER="${SDK_VER:-10.0.26100.0}"
SDK_VER_SHORT="${SDK_VER%.*}"            # 10.0.26100.0 -> 10.0.26100

# --- derived host paths ---
export WINEPREFIX="$INUTIL_UNITY_DIR/prefix"
export WINEDEBUG="${WINEDEBUG:--all}"
DL_DIR="$INUTIL_UNITY_DIR/dl"
MSVC_DIR="$INUTIL_UNITY_DIR/msvc"
MSVC_WINE_DIR="$INUTIL_UNITY_DIR/msvc-wine"
TOYGAME_SRC="${TOYGAME_SRC:-$REPO_ROOT/testgame/ToyGame}"
WINE_FIXES_DIR="$REPO_ROOT/tools/wine"

# --- guest (Windows) paths ---
EDITOR_WIN="C:\\Unity\\$UNITY_VERSION\\Editor\\Unity.exe"
EDITOR_DIR_UNIX="$WINEPREFIX/drive_c/Unity/$UNITY_VERSION/Editor"
TRIGGER_EXE="$EDITOR_DIR_UNIX/Data/Tools/ilpp/Unity.ILPP.Trigger/Unity.ILPP.Trigger.exe"
LICENSE_CLIENT_WIN="C:\\Unity\\$UNITY_VERSION\\Editor\\Data\\Resources\\Licensing\\Client\\Unity.Licensing.Client.exe"
VSWHERE_DIR_UNIX="$WINEPREFIX/drive_c/Program Files (x86)/Microsoft Visual Studio/Installer"

# Convert a unix path to the wine Z: drive path (no echo: avoids \a-style mangling).
winpath() { local p="${1%/}"; printf 'Z:%s' "${p//\//\\}"; }
MSVC_WIN="$(winpath "$MSVC_DIR")"

mingw_gcc() { command -v x86_64-w64-mingw32-gcc; }

# Scrub inutil's OWN deployable artifacts from a loader scan dir (BepInEx plugins/ or MelonLoader Mods/)
# before a fresh deploy, so nothing inutil left there on a PRIOR run can linger and poison this one. Two
# failure modes this prevents: (a) a stale duplicate carrying Inutil.Hooks.Hooks — notably the pre-split
# monolith InutilLoader.dll — which MelonLoader (loads every Mods/ DLL) then exposes to the REPL's
# reference-every-loaded-assembly scan as an AMBIGUOUS type (Roslyn CS0433); (b) a prior repl-validate's
# Roslyn closure breaking a later non-repl run's byte-clean no-Roslyn deploy. Only names inutil itself
# deploys are removed — never the loader's own core DLLs or the first-run interop proxies.
#
# The `Inutil*.dll` glob is drift-proof BY DESIGN: it matches Inutil.dll, both host plugins
# (Inutil.BepInEx.dll / Inutil.MelonLoader.dll), the legacy monolith (InutilLoader.dll), and ANY future
# Inutil.<X>.dll — so adding a plugin never silently breaks the scrub. It is case-sensitive, so the
# lowercase native core (inutil_core.dll) and the Roslyn closure are still listed explicitly. No loader DLL
# starts with "Inutil" (theirs are BepInEx.*/MelonLoader.*/Il2CppInterop.*/Assembly-CSharp/UnityEngine.*).
scrub_inutil_deploy() {
  local dir="$1"
  rm -f "$dir"/Inutil*.dll \
        "$dir"/ToyGameDemo.dll "$dir"/inutil_core.dll \
        "$dir"/Microsoft.CodeAnalysis*.dll "$dir"/Iced.dll
}
