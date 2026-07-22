{ pkgs, lib, ... }:

# inutil — typed-hook SDK generator + live mod host for IL2CPP Unity games.
#
# Target:  Windows x64 (IL2CPP `GameAssembly.dll`).
# Dev box: Linux. We cross-compile the native bootstrap with mingw-w64 and run /
#          test all Windows artifacts under wine — no Windows machine required for
#          the day-to-day loop. See docs/contribution/02-system-map.md for the full topology rationale.

let
  # Proton runner for the loader-VALIDATION run side ONLY (the BUILD side — Unity editor + msvc-wine —
  # stays bare wine64). umu drives Proton inside the Steam Linux Runtime (pressure-vessel), which
  # supplies the GPU driver + libs a bare wine lacks: ToyGame renders and il2cpp boots where bare wine
  # dies at window creation ("no driver could be loaded"). GE-Proton9-27 is the proven il2cpp+BepInEx
  # runner — GE-Proton10-x faults early in il2cpp during BepInEx logger init. This store path is held
  # alive by the sibling opentarkov project; override with INUTIL_PROTON for any GE-Proton steamcompattool.
  protonDefault = "/nix/store/5paaznwm0qz5ljn9yn3mi3w7y2qrqq80-proton-ge-bin-GE-Proton9-27-steamcompattool";
in
{
  packages = [
    # --- Managed side: host, SDK generator, mod host, REPL (IL, runs on the
    #     win-x64 CoreCLR we embed in the target). dotnet on Linux can build and
    #     `publish -r win-x64`, pulling the win-x64 runtime pack from NuGet. ---
    pkgs.dotnet-sdk_9

    # --- Native bootstrap: injector + CoreCLR host DLL, cross-compiled to win-x64.
    #     Provides x86_64-w64-mingw32-{gcc,g++,ld,ar,windres} via the cc wrapper. ---
    pkgs.pkgsCross.mingwW64.stdenv.cc
    pkgs.cmake
    pkgs.ninja
    pkgs.pkg-config

    # --- Run / test the Windows artifacts on Linux, and host the Unity IL2CPP
    #     toy-game build under wine (editor + msvc-wine). See tools/wine/. ---
    pkgs.wine64
    pkgs.curl          # download Unity installers
    pkgs.python3       # msvc-wine vsdownload.py
    pkgs.msitools      # msvc-wine MSI extraction (msiextract)

    # --- RUN / validate the IL2CPP loader stack (a v2 harness, tools/wine/). We do NOT use bare wine
    #     here: on this box wine's winex11.drv won't load (the game dies at window creation). Proton-
    #     via-umu runs inside the Steam Linux Runtime (pressure-vessel) which supplies the GPU driver +
    #     libs wine lacks, wrapped in a RAW (no-setcap) gamescope headless surface — a GPU-backed
    #     virtual display with no window on the host desktop. See INUTIL_PROTON/INUTIL_GAMESCOPE below. ---
    pkgs.umu-launcher
    pkgs.gamescope

    pkgs.git
  ];

  env = {
    DOTNET_CLI_TELEMETRY_OPTOUT = "1";
    DOTNET_NOLOGO = "1";
    # Cross toolchain triple — referenced by CMake toolchain file & scripts.
    MINGW_TRIPLE = "x86_64-w64-mingw32";
    # Quiet wine unless we're debugging the loader.
    WINEDEBUG = "-all";

    # --- Loader-validation run harness (v2, not yet written — see docs/contribution/architecture/20-testing.md) consumes these. ---
    # GE-Proton runner for the RUN side (see protonDefault above). Override to pick another build.
    INUTIL_PROTON = protonDefault;
    # The RAW store gamescope — NOT /run/wrappers/bin/gamescope. The setcap wrapper carries cap_sys_nice
    # as inheritable/ambient and leaks it to umu's pressure-vessel bwrap ("Unexpected capabilities but
    # not setuid" -> child aborts). The store binary has no file caps, so bwrap sees a clean env.
    INUTIL_GAMESCOPE = "${pkgs.gamescope}/bin/gamescope";
  };

  scripts = {
    # Run a cross-compiled Windows exe under wine, quietly.
    # wine 11 uses a single unified WoW64 `wine` binary (no separate `wine64`).
    xwine.exec = ''wine "$@"'';
    # Print toolchain health — handy after `devenv update`.
    doctor.exec = ''
      echo "dotnet : $(dotnet --version 2>/dev/null || echo MISSING)"
      echo "mingw  : $(x86_64-w64-mingw32-gcc --version 2>/dev/null | head -1 || echo MISSING)"
      echo "cmake  : $(cmake --version 2>/dev/null | head -1 || echo MISSING)"
      echo "wine   : $(wine --version 2>/dev/null || echo MISSING)"
    '';

    # --- Wine-hosted Unity IL2CPP toy-game pipeline (see tools/wine/).
    #     Produces a real GameAssembly.dll + global-metadata.dat to validate the
    #     interceptor against — entirely on Linux, no Windows machine. Run in order:
    #       unity-setup      # one-time: wine prefix + msvc-wine + editor + fixes
    #       unity-license    # activate Unity Personal (needs your own .env creds)
    #       toygame-build    # build testgame/ToyGame -> Win64 IL2CPP artifacts
    unity-setup.exec   = ''exec bash "$DEVENV_ROOT/tools/wine/setup-unity.sh" "$@"'';
    unity-license.exec = ''exec bash "$DEVENV_ROOT/tools/wine/activate-license.sh" "$@"'';
    toygame-build.exec = ''exec bash "$DEVENV_ROOT/tools/wine/build-toygame.sh" "$@"'';

    # SETUP-BEPINEX — one-time provisioning for bepinex-validate: lays the pinned BepInEx 6 (IL2CPP)
    # build over a fresh ToyGame copy, then does the first run under GE-Proton (umu) so BepInEx's
    # preloader generates its own Il2CppInterop assemblies (Cpp2IL). Idempotent; --force reprovisions.
    # Needs `toygame-build`.
    setup-bepinex.exec = ''exec bash "$DEVENV_ROOT/tools/wine/setup-bepinex.sh" "$@"'';

    # SETUP-MELON — the MelonLoader analog of setup-bepinex: lays pinned MelonLoader v0.7.3 (IL2CPP) over a
    # fresh ToyGame copy via its version.dll proxy, then does the first run under GE-Proton (umu) so
    # MelonLoader's preloader generates its own Il2CppInterop assemblies (Cpp2IL). Idempotent; --force
    # reprovisions. Needs `toygame-build`.
    setup-melon.exec = ''exec bash "$DEVENV_ROOT/tools/wine/setup-melon.sh" "$@"'';

    # BEPINEX-VALIDATE / MELON-VALIDATE — the v2 structured validation harness (docs/contribution/architecture/20-testing.md),
    # replacing v1's removed log-scrapers. Each: builds + SELF-TESTS the aggregator (proves the judge can't
    # lie before trusting it), builds the loader battery shim (managed/test/Battery), scrubs + deploys it,
    # launches the real IL2CPP ToyGame under GE-Proton, waits for the battery's `done` record in its JSONL
    # sidecar, then aggregates — the exit code IS the verdict (0 green / 1 red), nothing is scraped from a
    # log. `--fail` arms the canary case to prove a real run turns red end-to-end. Need setup-bepinex/-melon.
    bepinex-validate.exec = ''exec bash "$DEVENV_ROOT/tools/wine/validate.sh" bepinex "$@"'';
    melon-validate.exec   = ''exec bash "$DEVENV_ROOT/tools/wine/validate.sh" melon "$@"'';

    # TESTKIT-SELFTEST — the aggregator's own test suite, standalone (no game, no wine): feeds it every way a
    # battery can fail or hide a failure and requires each to go red, and a complete run to go green. Fast;
    # run it in CI as the gate that the verdict engine itself is sound.
    testkit-selftest.exec = ''
      cd "$DEVENV_ROOT/managed/test"
      dotnet build Inutil.TestKit.Cli/Inutil.TestKit.Cli.csproj -c Release -v q --nologo
      exec dotnet Inutil.TestKit.Cli/bin/Release/net9.0/inutil-testkit.dll selftest
    '';

    # SCHEMA-TEST — unit tests for the v2 engine core (inutil.Schema, §7.4 VirtualSlotPlanner), standalone: no
    # game, no wine, no real proxy. Exercises the planner against synthetic type shapes covering §8's mandatory
    # per-family template + a direct regression for the confirmed :534/:1195 one-plan-for-group bugs.
    schema-test.exec = ''
      cd "$DEVENV_ROOT/managed/src"
      dotnet build Inutil.Schema.Tests/Inutil.Schema.Tests.csproj -c Release -v q --nologo
      exec dotnet Inutil.Schema.Tests/bin/Release/net9.0/inutil-schema-tests.dll
    '';

    # INTEROPPATCH-TEST — the IL-rewrite seam (§7.2) driven by the pure engine over the REAL generated ToyGame
    # proxies, OFFLINE (no game, no wine). Proves the Cecil->ISlotMethod projection + Task family flip the
    # cross-module virtual Task<Session> slot (v15) and round-trip through Cecil. Needs setup-bepinex's interop.
    interoppatch-test.exec = ''
      cd "$DEVENV_ROOT/managed/src"
      dotnet build Inutil.InteropPatch.Tests/Inutil.InteropPatch.Tests.csproj -c Release -v q --nologo
      exec dotnet Inutil.InteropPatch.Tests/bin/Release/net9.0/inutil-interoppatch-tests.dll
    '';

    # SDK-TEST — the runtime marshaller seam's game-INDEPENDENT surface (§7.3): the Il2CppSugar Task carrier
    # (nint identity + shared CWT — no il2cpp domain) and the Il2CppConvRuntime structure (Leaf identity, null,
    # fail-loud deferral). net9 runner over the net6 Inutil.dll; copies the loader's Il2CppInterop.Runtime.dll
    # only so its type refs RESOLVE. Builds fully offline against .unity-build. Runs the module in-game separately.
    sdk-test.exec = ''
      cd "$DEVENV_ROOT/managed/src"
      dotnet build Inutil.Tests/Inutil.Tests.csproj -c Release -v q --nologo
      exec dotnet Inutil.Tests/bin/Release/net9.0/inutil-sdk-tests.dll
    '';

    # CHECK — the OFFLINE GATE: every suite that needs no running game, in ONE fail-fast command. The four suites
    # were separate manual scripts with no aggregate and no CI, which is exactly how sdk-test silently rotted (it
    # crashed from the moment C2 landed and nothing ran it). `set -e` => any suite's non-zero exit fails the whole
    # gate. interoppatch-test needs setup-bepinex's real proxies; if absent it is reported SKIPPED out loud, never
    # silently dropped. Run this before every commit; bepinex-validate stays the separate in-game gate.
    check.exec = ''
      set -e
      echo "── inutil offline gate ──────────────────────────────────────"
      cd "$DEVENV_ROOT/managed"
      echo ">> [1/6] testkit-selftest (prove the verdict engine itself)"
      dotnet build test/Inutil.TestKit.Cli/Inutil.TestKit.Cli.csproj -c Release -v q --nologo
      dotnet test/Inutil.TestKit.Cli/bin/Release/net9.0/inutil-testkit.dll selftest
      echo ">> [2/6] schema-test (pure engine: VirtualSlotPlanner, Conv, registry)"
      dotnet build src/Inutil.Schema.Tests/Inutil.Schema.Tests.csproj -c Release -v q --nologo
      dotnet src/Inutil.Schema.Tests/bin/Release/net9.0/inutil-schema-tests.dll
      echo ">> [3/6] sdk-test (runtime marshaller seam + scheduling + lifecycle registry)"
      dotnet build src/Inutil.Tests/Inutil.Tests.csproj -c Release -v q --nologo
      dotnet src/Inutil.Tests/bin/Release/net9.0/inutil-sdk-tests.dll
      echo ">> [4/6] mods-test (no-build mod host: compile -> collectible ALC -> Discover -> teardown)"
      dotnet build src/Inutil.Mods.Tests/Inutil.Mods.Tests.csproj -c Release -v q --nologo
      dotnet src/Inutil.Mods.Tests/bin/Release/inutil-mods-tests.dll
      echo ">> [5/6] metadata-test (pillar: wire-map emit; Cpp2IL integration when a game is provisioned)"
      dotnet build src/Inutil.Metadata.Tests/Inutil.Metadata.Tests.csproj -c Release -v q --nologo
      dotnet src/Inutil.Metadata.Tests/bin/Release/net9.0/inutil-metadata-tests.dll
      INTEROP="''${INUTIL_UNITY_DIR:-$DEVENV_ROOT/.unity-build}/loaders/bepinex/BepInEx/interop"
      if [ -d "$INTEROP" ]; then
        echo ">> [6/6] interoppatch-test (IL-rewrite seam over real proxies)"
        dotnet build src/Inutil.InteropPatch.Tests/Inutil.InteropPatch.Tests.csproj -c Release -v q --nologo
        dotnet src/Inutil.InteropPatch.Tests/bin/Release/net9.0/inutil-interoppatch-tests.dll "$INTEROP"
      else
        echo "!! [6/6] SKIPPED interoppatch-test — no interop proxies at $INTEROP (run 'setup-bepinex')"
      fi
      echo "── offline gate GREEN ───────────────────────────────────────"
    '';
  };

  enterShell = ''
    echo "── inutil devshell · IL2CPP typed-hook SDK + live mod host (win-x64 target) ──"
    echo "  dotnet : $(dotnet --version 2>/dev/null || echo MISSING)"
    echo "  mingw  : $(x86_64-w64-mingw32-gcc --version 2>/dev/null | head -1 || echo MISSING)"
    echo "  wine   : $(wine --version 2>/dev/null || echo MISSING)"
    echo "  ('doctor' for full toolchain health, 'xwine foo.exe' to run win artifacts)"
    echo "  toy game (real il2cpp under wine): unity-setup → unity-license → toygame-build"
    echo "  loader provisioning (first-run interop): setup-bepinex · setup-melon"
    echo "  validate (structured, machine-checked — §8): bepinex-validate · melon-validate · testkit-selftest"
  '';
}
