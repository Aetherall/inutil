{
  description = "inutil IL2CPP modding framework distributions";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
    in {
      packages = forAllSystems (system:
        let
          pkgs = import nixpkgs {
            inherit system;
            # The loader ABI is still net6.0. Keep this narrowly scoped to the
            # SDK needed to compile it; the published tools themselves use net9.
            config.permittedInsecurePackages = [ "dotnet-sdk-6.0.428" ];
          };

          bepinex = pkgs.fetchzip {
            url = "https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip";
            hash = "sha256-kmtqeIar8cV1MoZs34r4K4ZorGnqN1zrZ5eEOejprNQ=";
            stripRoot = false;
          };

          melonloader = pkgs.fetchzip {
            url = "https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip";
            hash = "sha256-AR+EAokSlWZLhp5q7CybxTl9ZZxe/gtv6rnO5llEUIc=";
            stripRoot = false;
          };

          dotnet = pkgs.dotnetCorePackages.combinePackages [
            pkgs.dotnet-sdk_9
            pkgs.dotnet-sdk_8
            pkgs.dotnet-sdk_6
          ];

          bundle = pkgs.buildDotnetModule {
            pname = "inutil-bundle";
            version = "0.0.0";
            src = ./.;

            projectFile = [
              "managed/src/Inutil.BepInEx/Inutil.BepInEx.csproj"
              "managed/src/Inutil.BepInEx.Patcher/Inutil.BepInEx.Patcher.csproj"
              "managed/src/Inutil.MelonLoader/Inutil.MelonLoader.csproj"
              "managed/src/Inutil.InteropPatch.Cli/Inutil.InteropPatch.Cli.csproj"
              "managed/src/Inutil.Metadata.Cli/Inutil.Metadata.Cli.csproj"
              "managed/src/Inutil.Check.Cli/Inutil.Check.Cli.csproj"
            ];
            nugetDeps = ./nix/deps.json;
            dotnet-sdk = dotnet;
            dotnet-runtime = dotnet;

            nativeBuildInputs = [
              pkgs.cmake
              pkgs.git
              pkgs.jq
              pkgs.ninja
              pkgs.pkgsCross.mingwW64.stdenv.cc
            ];
            dontUseCmakeConfigure = true;

            buildPhase = ''
              runHook preBuild
              export DEVENV_ROOT="$PWD"
              export PACK_BEPINEX_DIR=${bepinex}/BepInEx
              export PACK_MELONLOADER_DIR=${melonloader}/MelonLoader
              export PACK_OUT="$TMPDIR/dist"
              export PACK_VERSION="${self.shortRev or self.dirtyShortRev or "source"}"
              export PACK_GIT_SHA="${self.rev or "unknown"}"
              export PACK_GIT_SHORT="${self.shortRev or self.dirtyShortRev or "source"}"
              export PACK_GIT_DESCRIBE="${self.shortRev or self.dirtyShortRev or "source"}"
              export PACK_DIRTY=false
              export -n version
              bash tools/pack.sh "$PACK_VERSION"
              runHook postBuild
            '';

            installPhase = ''
              runHook preInstall
              cp -R "$TMPDIR/dist/${self.shortRev or self.dirtyShortRev or "source"}" "$out"
              runHook postInstall
            '';
          };

          variant = loader: pkgs.runCommand "inutil-${loader}-${bundle.version}" {
            nativeBuildInputs = [ pkgs.jq ];
          } ''
            mkdir -p "$out"
            cp -R ${bundle}/${loader} "$out/${loader}"
            cp -R ${bundle}/tools "$out/tools"
            jq --arg loader "${loader}" \
              '.loaders = { ($loader): .loaders[$loader] }' \
              ${bundle}/manifest.json > "$out/manifest.json"
            sed 's/^loaders:.*/loaders:  ${loader}/' ${bundle}/MARKER > "$out/MARKER"
          '';
        in {
          bepinex = variant "bepinex";
          melonloader = variant "melonloader";
          default = variant "bepinex";
        });

      # Make `nix flake check` build both end-user distributions.
      checks = forAllSystems (system:
        let packages = self.packages.${system};
        in {
          bepinex = packages.bepinex;
          melonloader = packages.melonloader;
        });
    };
}
