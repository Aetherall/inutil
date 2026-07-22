// EXTERNAL-LOADER adapter (the substrate-specific glue, "we are a plugin" side).
// The control-flow inverse of a native host that owns the process: when an external IL2CPP mod
// loader (BepInEx, MelonLoader, …) hosts us, IT is the process: it has already il2cpp_init'd
// the domain, embedded CoreCLR, and started Il2CppInterop, then loaded THIS assembly. So we do
// NOT bring up interop — we only:
//   1. take the addresses of our own [UnmanagedCallersOnly] dispatchers (we are already managed, so this is
//      just `&` — a native host would resolve them through hostfxr precisely because IT is native), and
//   2. P/Invoke the agnostic core (inutil_core.dll = native/core/interceptor.c built SHARED) to bring up the
//      hook engine and get the `install` callback back, then wire it into Inutil.Hooks via HostCore.Init.
//
// Loader-AGNOSTIC: every Il2CppInterop-based loader hands a plugin the exact same world (domain up, CoreCLR
// up, interop up). The ONLY per-loader code is the entrypoint shim that calls Attach()/Detach():
// BepInExPlugin.cs (#if BEPINEX, BasePlugin.Load) and
// MelonLoaderMod.cs (#if MELONLOADER, MelonMod.OnInitializeMelon).
//
// (NB MelonLoader: this targets its .NET/CoreCLR backend — the one that uses Il2CppInterop and
// supports [UnmanagedCallersOnly] / delegate* unmanaged — not the legacy Mono backend.)
//
// The core DLL is byte-for-byte the same translation unit core/interceptor.c — built SHARED
// (CMake: add_library(inutil_core SHARED core/interceptor.c ...)), no dllexport edits: two loaders, one core,
// zero core changes.
using System.Runtime.InteropServices;
using Inutil.Hooks;

namespace Inutil.Host;

public static unsafe class LoaderAdapter
{
    // The agnostic hook engine as a loadable DLL. [DllImport] resolves inutil_core.dll beside
    // the host executable / the plugin assembly (plain C exports; x64 ABI == Cdecl == StdCall).
    [DllImport("inutil_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint inutil_interceptor_init(nint pre, nint post, nint ipre, nint ipost,
        out nint outInstallInvoker, out nint outProceed,
        out nint outInstallVtable, out nint outRestoreVtable);

    [DllImport("inutil_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern void inutil_interceptor_shutdown();

    // Wire inutil into a host that has ALREADY brought up il2cpp + CoreCLR + Il2CppInterop
    // (BepInEx / MelonLoader / …). Call once on plugin load, before registering hooks via the
    // Inutil.Hooks API.
    public static void Attach()
    {
        // Our reverse-P/Invoke targets: because we are managed, the dispatchers are just `&` of the
        // [UnmanagedCallersOnly] methods — the SAME pointers hostfxr would return to a native host.
        nint pre  = (nint)(delegate* unmanaged<nint, CallFrame*, RetFrame*, int>)&Hooks.Hooks.Dispatch;
        nint post = (nint)(delegate* unmanaged<nint, CallFrame*, RetFrame*, void>)&Hooks.Hooks.PostDispatch;
        nint ipre  = (nint)(delegate* unmanaged<nint, nint, void**, nint, int>)&Hooks.Hooks.InvokePre;
        nint ipost = (nint)(delegate* unmanaged<nint, nint, void**, nint, void>)&Hooks.Hooks.InvokePost;

        nint install = inutil_interceptor_init(pre, post, ipre, ipost,
            out nint installInvoker, out nint proceed,
            out nint installVtable, out nint restoreVtable);
        if (install == 0)
            throw new InvalidOperationException("inutil_core: interceptor init failed (MinHook init?)");

        HostCore.Init(install);                          // NB: deliberately NO BringUpInterop — the loader already did it.
        Hooks.Hooks.SetInvokerInstall(installInvoker);   // the invoke-path install (__Canon generic methods)
        Hooks.Hooks.SetProceed(proceed);                 // ctx.Proceed() — run the original from inside a hook (the "around" wrap)
        Hooks.Hooks.SetInstallVtable(installVtable);     // vtable-slot install (class-scoped, shared-stub-safe)
        Hooks.Hooks.SetRestoreVtable(restoreVtable);     // vtable-slot restore (called on ALC unload / explicit remove)
    }

    // Tear the hook engine down on plugin unload (whole-engine shutdown).
    public static void Detach() => inutil_interceptor_shutdown();
}
