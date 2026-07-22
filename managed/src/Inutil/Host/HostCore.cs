// substrate-AGNOSTIC managed core. Given the native install callback (from a live il2cpp domain + CoreCLR the
// host already brought up), it wires the interceptor into the typed Inutil.Hooks engine. It assumes nothing
// about WHO created the domain or embedded CoreCLR — that's the adapter's job (the per-loader entrypoint shim
// in Inutil.BepInEx / Inutil.MelonLoader, via LoaderAdapter.Attach).
//
// The seam an adapter implements: call Init(install) once, with the native install(methodPointer, methodInfo,
// miSlot) callback the host's hook engine (native/core/interceptor.c) returned, then register hooks via the
// Inutil.Hooks typed API.

namespace Inutil.Host;

public static class HostCore
{
    // Wire the native hook engine into the managed interceptor. Required, substrate-agnostic.
    public static void Init(nint install) => Hooks.Hooks.SetInstall(install);
}
