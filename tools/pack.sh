#!/usr/bin/env bash
# tools/pack.sh — produce the distributable inutil ENGINE BUNDLE into dist/<version>/.
#
# This is the fix for the coupling described in docs/reference/packaging.md: today inutil has no release
# artifact, so its only consumer (the OpenTarkov engine) rebuilds inutil FROM SOURCE inside its own pack.sh,
# naming inutil's internal project layout. This script re-homes that build HERE and emits a versioned,
# consumer-agnostic bundle — so a consumer copies the artifact instead of knowing how to build us.
#
# WHAT IT PRODUCES (per docs/reference/packaging.md — "Per-loader trees + manifest", both loaders):
#   dist/<version>/
#     bepinex/BepInEx/plugins/    the BepInEx host tree  — copy wholesale beside a BepInEx install
#     bepinex/BepInEx/patchers/   the PRELOADER interop patcher — natural-types the proxies ON DISK in
#                                 the pre-read window (its ctor runs the same PatchDirectory the CLI
#                                 runs, before BepInEx opens any interop stream), with an in-memory
#                                 session fallback; the offline CLI below remains the manual/dev interface
#     melonloader/Mods/           the MelonLoader host tree — copy wholesale into a MelonLoader install
#     tools/inutil-interoppatch/  the OFFLINE proxy patcher (the manual/dev deployment-time interface)
#     tools/inutil-metadata-extract/  the opt-in wire-map extractor (Cpp2IL closure; only WireMap consumers need it)
#     manifest.json               machine-readable: version, git identity, per-loader file->deploy map, tools
#     MARKER                      human-readable identity stamp
#
# The shared engine (Inutil.dll + Inutil.Schema.dll + Inutil.Mods.dll + Roslyn closure + native inutil_core.dll)
# is loader-INVARIANT (the #if BEPINEX / #if MELONLOADER guards live only in the two thin host projects), so it
# is built ONCE and copied into both trees; only the ~one thin host DLL differs per loader.
#
# BUILD INPUTS (not shipped): the managed projects compile against the provisioned .unity-build loader trees
# with Private=false refs, so NOTHING game-derived ends up in the output (the outputs name only Il2CppInterop +
# Il2Cppmscorlib + BCL). You need the provisioned tree (setup-bepinex / setup-melon) + the mingw toolchain for
# the native core — i.e. run inside `devenv shell`. A game boot is NOT required (this builds the game-agnostic
# engine; it never touches interop/ proxies).
#
# Usage:  tools/pack.sh [<version>]
#   <version> / PACK_VERSION   bundle version (default: `git describe --tags --always`)
#   PACK_OUT                   output dir       (default: <repo>/dist)
#   PACK_RID                   publish the CLI tools for this RID (e.g. win-x64); default: portable (no RID)
#   PACK_SELF_CONTAINED=1      self-contained CLI publish (implies you should set PACK_RID); default: framework-dependent
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/wine/env.sh"   # REPO_ROOT, INUTIL_UNITY_DIR, mingw_gcc

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
mg="$REPO_ROOT/managed/src"

# --- version + git identity --------------------------------------------------------------------------
version="${1:-${PACK_VERSION:-$(git -C "$REPO_ROOT" describe --tags --always)}}"
git_sha="$(git -C "$REPO_ROOT" rev-parse HEAD)"
git_short="$(git -C "$REPO_ROOT" rev-parse --short HEAD)"
git_describe="$(git -C "$REPO_ROOT" describe --tags --always)"
if git -C "$REPO_ROOT" diff --quiet HEAD -- managed native 2>/dev/null; then dirty=false; else dirty=true; fi
dirty_note=""; [ "$dirty" = true ] && dirty_note=" (dirty)"   # $dirty is the STRING "true"/"false" — test the value, not emptiness

out="${PACK_OUT:-$REPO_ROOT/dist}/$version"
echo ">> packing inutil bundle  version=$version  (git $git_short$dirty_note)"

# --- preflight: build inputs must be provisioned -----------------------------------------------------
bep="$INUTIL_UNITY_DIR/loaders/bepinex/BepInEx"
mel="$INUTIL_UNITY_DIR/loaders/melonloader/MelonLoader"
[ -f "$bep/core/Il2CppInterop.Runtime.dll" ] || { echo "!! BepInEx build refs missing at $bep — run 'setup-bepinex' first" >&2; exit 1; }
[ -f "$mel/net6/MelonLoader.dll" ]           || { echo "!! MelonLoader build refs missing at $mel — run 'setup-melon' first" >&2; exit 1; }

# --- 1. build the SHARED engine ONCE (loader-invariant) ---------------------------------------------
# Inutil.dll (+Inutil.Schema.dll) is the runtime SDK; Inutil.Mods.dll drags the Roslyn closure (the no-build
# .cs host + REPL). Built against the BepInEx tree; the Il2CppInterop identity these bind is the SAME assembly
# MelonLoader loads, so one build serves both loaders (Private=false — identity bound at load, never shipped).
echo ">> [1/5] shared engine (Inutil + Inutil.Mods + Roslyn closure)"
dotnet build "$mg/Inutil/Inutil.csproj"           -c Release -v q --nologo
dotnet build "$mg/Inutil.Mods/Inutil.Mods.csproj" -c Release -v q --nologo
sdk_bin="$mg/Inutil/bin/Release"
mods_bin="$mg/Inutil.Mods/bin/Release"
[ -f "$sdk_bin/Inutil.dll" ] && [ -f "$sdk_bin/Inutil.Schema.dll" ] || { echo "!! shared engine build produced no Inutil.dll/Inutil.Schema.dll" >&2; exit 1; }
ls "$mods_bin"/Microsoft.CodeAnalysis*.dll >/dev/null 2>&1 || { echo "!! Inutil.Mods build produced no Roslyn closure (Microsoft.CodeAnalysis*.dll)" >&2; exit 1; }

# --- 2. build the two thin loader hosts + the BepInEx preloader patcher -----------------------------
echo ">> [2/5] loader hosts (Inutil.BepInEx + Inutil.MelonLoader) + preloader patcher"
dotnet build "$mg/Inutil.BepInEx/Inutil.BepInEx.csproj"         -c Release -v q --nologo -p:Loader=BepInEx
dotnet build "$mg/Inutil.MelonLoader/Inutil.MelonLoader.csproj" -c Release -v q --nologo -p:Loader=MelonLoader
dotnet build "$mg/Inutil.BepInEx.Patcher/Inutil.BepInEx.Patcher.csproj" -c Release -v q --nologo
# Inutil.InteropPatch.dll (net6) backs the host plugin's phase-2 disk persist (Private=false ref — deployed
# here, beside the host, exactly like the SDK).
dotnet build "$mg/Inutil.InteropPatch/Inutil.InteropPatch.csproj" -c Release -v q --nologo -f net6.0
bep_host="$mg/Inutil.BepInEx/bin/Release/Inutil.BepInEx.dll"
mel_host="$mg/Inutil.MelonLoader/bin/Release/Inutil.MelonLoader.dll"
bep_patcher="$mg/Inutil.BepInEx.Patcher/bin/Release/Inutil.BepInEx.Patcher.dll"
interoppatch_dll="$mg/Inutil.InteropPatch/bin/Release/net6.0/Inutil.InteropPatch.dll"
[ -f "$bep_host" ] && [ -f "$mel_host" ] || { echo "!! a loader host build produced no DLL" >&2; exit 1; }
[ -f "$bep_patcher" ] && [ -f "$interoppatch_dll" ] || { echo "!! the preloader patcher / InteropPatch build produced no DLL" >&2; exit 1; }

# --- 3. build the native hook engine (mingw + ninja) ------------------------------------------------
# inutil_core.dll — the MinHook interceptor + SEH fault guard; game-agnostic. Cross-compiled for Windows.
echo ">> [3/5] native hook engine (inutil_core.dll)"
core_dll="$REPO_ROOT/native/build/inutil_core.dll"
if command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
  [ -f "$REPO_ROOT/native/third_party/minhook/src/hook.c" ] || git -C "$REPO_ROOT" submodule update --init --recursive
  cmake -S "$REPO_ROOT/native" -B "$REPO_ROOT/native/build" -G Ninja -Wno-dev \
        -DCMAKE_TOOLCHAIN_FILE="$REPO_ROOT/native/cmake/toolchain-mingw-w64.cmake" >/dev/null
  ninja -C "$REPO_ROOT/native/build" inutil_core >/dev/null
elif [ -f "$core_dll" ]; then
  echo "   !! mingw toolchain absent — reusing PREBUILT $core_dll (may be stale; run inside 'devenv shell' to rebuild)"
else
  echo "!! no mingw toolchain and no prebuilt native/build/inutil_core.dll — run inside 'devenv shell'" >&2; exit 1
fi
[ -f "$core_dll" ] || { echo "!! native build produced no inutil_core.dll" >&2; exit 1; }

# --- 4. stage the per-loader trees ------------------------------------------------------------------
echo ">> [4/5] staging per-loader trees -> $out"
rm -rf "$out"; mkdir -p "$out"
bep_deploy="$out/bepinex/BepInEx/plugins"; mkdir -p "$bep_deploy"
mel_deploy="$out/melonloader/Mods";        mkdir -p "$mel_deploy"

# The shared engine payload both trees carry, byte-identical (the canonical -p:Loader SDK build + the closure).
stage_shared() {
  local dst="$1"
  cp -f "$sdk_bin/Inutil.dll" "$sdk_bin/Inutil.Schema.dll" "$dst/"
  cp -f "$mods_bin/Inutil.Mods.dll" "$dst/"
  cp -f "$mods_bin"/Microsoft.CodeAnalysis*.dll "$dst/"
  cp -f "$core_dll" "$dst/"
}
stage_shared "$bep_deploy"; cp -f "$bep_host" "$bep_deploy/"
stage_shared "$mel_deploy"; cp -f "$mel_host" "$mel_deploy/"

# BepInEx-only extras: the phase-2 persist engine beside the host (plugins/), and the phase-1 preloader
# patcher in its own BepInEx dir (patchers/ — NOT plugins/; the preloader loads it before the chainloader).
# MelonLoader has no preloader-patcher seam — its deployment-time interface stays the offline CLI.
cp -f "$interoppatch_dll" "$bep_deploy/"
bep_patchers="$out/bepinex/BepInEx/patchers"; mkdir -p "$bep_patchers"
cp -f "$bep_patcher" "$bep_patchers/"

# --- 5. publish the CLI tools as runnables ----------------------------------------------------------
echo ">> [5/5] publishing CLI tools (inutil-interoppatch + inutil-metadata-extract)"
pub_args=( -c Release -v q --nologo )
[ -n "${PACK_RID:-}" ] && pub_args+=( -r "$PACK_RID" )
if [ "${PACK_SELF_CONTAINED:-0}" = 1 ]; then pub_args+=( --self-contained true ); else pub_args+=( --self-contained false ); fi
dotnet publish "$mg/Inutil.InteropPatch.Cli/Inutil.InteropPatch.Cli.csproj" "${pub_args[@]}" -o "$out/tools/inutil-interoppatch"
dotnet publish "$mg/Inutil.Metadata.Cli/Inutil.Metadata.Cli.csproj"         "${pub_args[@]}" -o "$out/tools/inutil-metadata-extract"
[ -f "$out/tools/inutil-interoppatch/inutil-interoppatch.dll" ] || { echo "!! interoppatch publish produced no entry dll" >&2; exit 1; }

# --- manifest.json + MARKER (the consumption contract, machine + human readable) --------------------
# The per-loader file lists are DERIVED from what was actually staged (never hand-maintained), so the manifest
# can't drift from the bundle. jq -R -s -c splits the ls output into a JSON string array.
bep_files="$(cd "$bep_deploy" && ls -1 | jq -R . | jq -s -c .)"
bep_patcher_files="$(cd "$bep_patchers" && ls -1 | jq -R . | jq -s -c .)"
mel_files="$(cd "$mel_deploy" && ls -1 | jq -R . | jq -s -c .)"
cat > "$out/manifest.json" <<EOF
{
  "name": "inutil",
  "version": "$version",
  "git": { "sha": "$git_sha", "short": "$git_short", "describe": "$git_describe", "dirty": $dirty },
  "schemaMarker": null,
  "loaders": {
    "bepinex":     { "root": "bepinex",     "deploy": "BepInEx/plugins", "files": $bep_files,
                     "patchers": { "deploy": "BepInEx/patchers", "files": $bep_patcher_files } },
    "melonloader": { "root": "melonloader", "deploy": "Mods",            "files": $mel_files }
  },
  "tools": {
    "inutil-interoppatch":     { "path": "tools/inutil-interoppatch",     "entry": "inutil-interoppatch.dll",     "invoke": "inutil-interoppatch --game <gameDir>",     "required": true },
    "inutil-metadata-extract": { "path": "tools/inutil-metadata-extract", "entry": "inutil-metadata-extract.dll", "invoke": "inutil-metadata-extract --game <gameDir>", "required": false }
  }
}
EOF

cat > "$out/MARKER" <<EOF
inutil engine bundle
version:  $version
git:      $git_short  ($git_describe)$dirty_note
built:    per docs/reference/packaging.md — consume via manifest.json
loaders:  bepinex (BepInEx/plugins), melonloader (Mods)
patch:    tools/inutil-interoppatch --game <gameDir>   (offline, idempotent; exit 0 ok / 2 usage)
NOTE:     schemaMarker is not yet stamped (packaging.md item 4 — tie to SchemaMarker.Hash(Families.Default()))
EOF

echo ">> packed $out"
echo "   bepinex/BepInEx/plugins  ($(ls -1 "$bep_deploy" | wc -l)): $(ls -1 "$bep_deploy" | tr '\n' ' ')"
echo "   bepinex/BepInEx/patchers ($(ls -1 "$bep_patchers" | wc -l)): $(ls -1 "$bep_patchers" | tr '\n' ' ')"
echo "   melonloader/Mods         ($(ls -1 "$mel_deploy" | wc -l)): $(ls -1 "$mel_deploy" | tr '\n' ' ')"
echo "   tools: inutil-interoppatch inutil-metadata-extract"
