# inutil

**inutil** is a C#/native framework for modding IL2CPP/Unity games (validated on Escape from Tarkov).
It gives a mod three capabilities — **hook** game methods, write against **natural BCL types** (`Task<T>`,
`int?`, `List<T>`, `Action`) instead of Il2CppInterop's generated wrappers, and run **no-build `.cs` mods**
with hot reload — over two loaders (BepInEx, MelonLoader) on one shared engine, at full in-game parity.

## Documentation

Everything lives in **[`docs/`](./docs/README.md)** — three doors: write a mod, understand it, contribute.

- **Write a mod** → [`docs/guide/`](./docs/guide/00-orient.md) — zero to a hot-reloading `.cs` mod: setup,
  hooking, natural typing, escape hatches, the REPL.
- **Understand / extend it** → [`docs/contribution/02-system-map.md`](./docs/contribution/02-system-map.md)
  — the architecture, one page per component (`architecture/10`–`20`), plus the
  [design philosophy](./docs/contribution/01-philosophy.md).

## Repo layout

- **`managed/`** — the SDK (`Inutil`), the IL-rewrite seam (`Inutil.InteropPatch`), the metadata
  tooling (`Inutil.Metadata`), the thin per-loader shims (`Inutil.BepInEx`,
  `Inutil.MelonLoader`), and the tests (`*.Tests` offline, `managed/test/Battery` in-game).
- **`native/`** — `inutil_core`: the MinHook-based interceptor + SEH fault guard, plus vendored MinHook and
  the mingw cross-compile toolchain.
- **`testgame/`** — the ToyGame Unity IL2CPP fixture the engine validates against.
- **`tools/wine/`** — the wine-based Unity headless-build + loader-provisioning harness (`setup-unity`,
  `build-toygame`, `setup-bepinex`, `setup-melon`, `validate.sh` — the authoritative build+validate path).
- Dev-environment scaffolding (`devenv.nix`, `.gitmodules`, …).
