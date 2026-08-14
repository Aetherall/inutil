#!/usr/bin/env bash
# guard-smoke — proof that inutil_core's SEH fault-guard (inutil_guard_read / inutil_guarded_invoke) catches a
# c0000005 WITHOUT killing the process, under mingw + Wine. Needs NO il2cpp / Unity / BepInEx: it builds the real
# inutil_core.dll and the dependency-free harnesses in native/tests/ that call the exported guard fns.
#
# Two harnesses, run in order (each scraped for its own "RESULT: <name> PASS"):
#   • guard_smoke          — the base guard: a bad read + a garbage-class invoke are caught (one native frame).
#   • guard_managed_smoke  — the ../../docs/contribution/architecture/17-reach-faces.md de-risk for Inutil.Safe.Run: nested (multi-frame) faults, 64 repeated
#                            faults (guard reusability), and a clean call after a fault. See the harness header for
#                            what a pure-C test CANNOT prove (the CLR reverse-P/Invoke frame — that is the in-game
#                            battery case `safe.run.faulted-survives`).
#
# The de-risk gate for Inutil.Safe (P2 §7.1): if this is green, the native fault-guard both shapes rest on works
# on this toolchain + Wine. (Restored + generalized from the v1 runner the v2 rewrite dropped.)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
native="$(cd "$here/../../native" && pwd)"
tri=x86_64-w64-mingw32

echo ">> 1. build inutil_core.dll (mingw cross-compile)"
cmake -S "$native" -B "$native/build" -G Ninja \
      -DCMAKE_TOOLCHAIN_FILE="$native/cmake/toolchain-mingw-w64.cmake" >/dev/null
cmake --build "$native/build" --target inutil_core >/dev/null
core="$native/build/inutil_core.dll"
[ -f "$core" ] || { echo "!! inutil_core.dll not built" >&2; exit 1; }

out="$native/build/guard-smoke"
mkdir -p "$out"
cp -f "$core" "$out/"                          # beside the exes so the Win32 loader resolves it

export WINEDEBUG="${WINEDEBUG:--all}"
rc=0

# Build + run one native harness; scrape its RESULT line. $1 = source basename (no .c), $2 = RESULT tag.
run_harness() {
  local name="$1" tag="$2"
  echo ">> build $name.exe (links the DLL's import lib)"
  "$tri-gcc" -O2 -I "$native/core" -I "$native/third_party/dotnet" \
    "$native/tests/$name.c" -o "$out/$name.exe" \
    -L "$native/build" -linutil_core
  echo ">> run $name.exe under wine"
  local log="$out/$name.log"
  ( cd "$out" && wine "./$name.exe" ) 2>/dev/null | tee "$log"
  echo
  if grep -q "RESULT: $tag PASS" "$log"; then
    echo ">> $name: PASS"
  else
    echo ">> $name: FAILED — see $log" >&2
    rc=1
  fi
  echo
}

run_harness guard_smoke         guard-smoke
run_harness guard_managed_smoke guard-managed-smoke

if [ "$rc" -eq 0 ]; then
  echo ">> SUCCESS — SEH fault-guard catches c0000005 under mingw+Wine (base + Safe.Run nested/repeated/interleaved); process survived"
else
  echo ">> FAILED — one or more guard harnesses did not pass" >&2
fi
exit "$rc"
