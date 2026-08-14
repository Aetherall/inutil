# third_party/dotnet

Vendored .NET hosting headers (MIT, © .NET Foundation), copied verbatim from the
.NET 9 host pack (`Microsoft.NETCore.App.Host.*/9.0.17/runtimes/*/native/`). The
API is platform-agnostic; on Windows `char_t` is 2-byte UTF-16 and the calltype
macros collapse to the single x64 convention.

- `hostfxr.h`            — hostfxr_initialize_for_runtime_config / get_runtime_delegate / close
- `coreclr_delegates.h`  — load_assembly_and_get_function_pointer_fn + UNMANAGEDCALLERSONLY_METHOD

`coreclr_delegates.h` is the live dependency: `interceptor.h` includes it for the
`CORECLR_DELEGATE_CALLTYPE` typedefs the managed-dispatcher signatures use. *(`hostfxr.h` was used by
the retired standalone host to embed CoreCLR via `LoadLibrary(hostfxr.dll)` + `GetProcAddress`, with no
link-time nethost dependency — retired in Fork 2; under an external loader, CoreCLR is already up.)*
