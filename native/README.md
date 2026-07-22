# native/

Cross-compiled to **win-x64** with mingw-w64 (built/run under wine). The interceptor core
(`core/interceptor.{c,h}` + `core/generic_thunk_post.S`, the `inutil_core.dll` hook engine) is
present, built to the design in ../docs/contribution/architecture/13-hook-engine.md (dedup-key
unification across install/dispatch/restore, the shared-`__Canon` ABI-completeness fix,
exception-isolated `[UnmanagedCallersOnly]` dispatchers).

What's still here, kept as external dependencies rather than old implementation:

- **`third_party/minhook/`** — vendored [MinHook](https://github.com/TsudaKageyu/minhook), the x64
  inline-detour primitive (git submodule). The core still uses it — no bugs were
  found in MinHook itself, only in how the old core keyed and tracked its installs.
- **`third_party/dotnet/`** — vendored MIT `hostfxr.h` + `coreclr_delegates.h`: the
  `CORECLR_DELEGATE_CALLTYPE` typedefs a managed dispatcher signature needs.
- **`cmake/toolchain-mingw-w64.cmake`** — the mingw-w64 cross toolchain file.

Build (inside `devenv shell`, for the mcfgthread runtime lib):

```sh
cmake -S native -B native/build -G Ninja -DCMAKE_TOOLCHAIN_FILE=native/cmake/toolchain-mingw-w64.cmake
cmake --build native/build --target inutil_core
```
