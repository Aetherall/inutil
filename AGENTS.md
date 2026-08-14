# Repository Guidelines

## Project Structure & Module Organization

`managed/src/` contains the C# SDK, schema, metadata, loader adapters, command-line tools, and their adjacent `*.Tests` projects. Shared test infrastructure and the in-game battery live under `managed/test/`. Native Win64 interception code is in `native/core/`, with smoke tests in `native/tests/` and vendored dependencies in `native/third_party/`. `testgame/ToyGame/` is the Unity IL2CPP fixture. Wine/Unity automation belongs in `tools/wine/`, while user and contributor documentation lives in `docs/guide/` and `docs/contribution/`.

## Build, Test, and Development Commands

Run commands inside `devenv shell`; use `doctor` to verify .NET, mingw, CMake, and Wine.

- `check`: runs the complete offline managed gate; run it before every commit.
- `schema-test`, `sdk-test`, or `testkit-selftest`: run a focused offline suite.
- `cmake -S native -B native/build -G Ninja -DCMAKE_TOOLCHAIN_FILE=native/cmake/toolchain-mingw-w64.cmake`: configure the native cross-build.
- `cmake --build native/build --target inutil_core`: build the Win64 hook DLL.
- `toygame-build`: build the Unity fixture after the one-time `unity-setup` and `unity-license` steps.
- `bepinex-validate` / `melon-validate`: run the structured in-game battery after provisioning with `setup-bepinex` or `setup-melon`.

## Coding Style & Naming Conventions

Follow existing C# conventions: file-scoped namespaces, four-space indentation, PascalCase for types and public members, camelCase for locals and parameters, and descriptive interface names prefixed with `I`. Keep nullable annotations accurate and use concise expression-bodied members where they improve clarity. Native C uses four-space indentation, `snake_case` functions, and explicit fixed-width/Win32 types. No repository-wide formatter is configured, so match the surrounding file and keep comments focused on design constraints or non-obvious behavior.

## Testing Guidelines

Tests are executable custom suites rather than a conventional test-runner tree. Add managed regression cases to the relevant `*.Tests` project; in-game behavior belongs in `managed/test/Battery/Cases/` and must be registered in the shared `Suite` for both loaders. Use stable dotted battery IDs such as `hooks.proceed.nested`. Every battery must emit a manifest, one result per declared ID, and a final `done` record. Run the narrow suite while iterating, then `check`; use both loader validations for loader-facing changes.

## Commit & Pull Request Guidelines

Recent commits use short, imperative, lowercase subjects with a component prefix, for example `hooks: prevent duplicate original calls`. Avoid vague subjects such as `wip`. Pull requests should explain the behavior change and rationale, list validation commands and results, link relevant issues or architecture docs, and call out loader-specific impact. Include logs for harness failures and screenshots only for visible Unity or tooling changes.
