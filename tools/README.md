# tools/

Developer tooling for the Linux→Win64 build/validate loop — all **wine-based**, no Windows machine:

- **`wine/`** — cross-build the **ToyGame** fixture into a real Win64 IL2CPP player under wine, then
  provision a real external loader (BepInEx 6 / MelonLoader) over it. The devenv scripts
  (`toygame-build`, `setup-bepinex`, `setup-melon`, …) are thin wrappers over these scripts. v1's
  `*-validate` scripts (which ran the hook stack inside the provisioned loader and scraped PASS/FAIL)
  were removed with the rest of the old implementation — see `../docs/contribution/architecture/20-testing.md` for the v2
  harness design. See [`wine/README.md`](wine/README.md).

inutil reuses two third-party pieces, neither committed here:
[MinHook](https://github.com/TsudaKageyu/minhook) is **vendored as source** in
`native/third_party/minhook` (cross-compiled into `inutil_core.dll`); Il2CppInterop proxies come from
the **loader's own first-run interop** (Cpp2IL, generated into `<game>/BepInEx/interop` by
`setup-bepinex`).
