// The PRODUCTION MelonLoader entrypoint (§7.10) — a thin MelonMod over the SDK, the MelonLoader sibling of
// BepInExPlugin.cs. Targets MelonLoader 0.6+ (the Il2CppInterop / .NET CoreCLR backend). OnUpdate/OnGUI below
// one-line into FrameDriver, the SAME seam the BepInEx pump drives.
#if MELONLOADER
using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using Inutil;
using Inutil.Host;

[assembly: MelonInfo(typeof(Inutil.Host.InutilMelonMod), "inutil", "0.1.0", "aetherall")]
[assembly: MelonGame]   // universal; specify game-specific names when deploying to a specific game

namespace Inutil.Host;

public sealed class InutilMelonMod : MelonMod
{
    public override void OnInitializeMelon()
    {
        // MelonLoader (.NET6/Il2CppInterop backend) has already il2cpp_init'd + started Il2CppInterop — the same
        // contract BepInEx provides, hence the same adapter, no interop bring-up.
        Inutil.Hooks.Hooks.OnWarning = m => MelonLogger.Warning(m);
        LoaderAdapter.Attach();
        MelonLogger.Msg("inutil attached: hook engine wired via inutil_core.dll");

        // Shared interop-unpatched safety net (§7.10 — one impl, both shims call it).
        LoaderShim.WarnIfInteropUnpatched(Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Il2CppAssemblies"), m => MelonLogger.Warning(m));

        // Where IConfigure mods' cfg files live (the ONE loader-specific fact the config seam needs) — under
        // UserData, MelonLoader's conventional per-game settings home.
        Inutil.ModConfigStore.Root = Path.Combine(MelonEnvironment.UserDataDirectory, "inutil-config");

        // The no-build .cs mod loop (Inutil.Mods). Non-fatal (JIT-time load inside the try).
        try { StartCsModLoop(); }
        catch (Exception ex) { MelonLogger.Warning($"inutil: .cs mod subsystem not started ({ex.GetType().Name}: {ex.Message}); the engine + built-DLL mods still work."); }
    }

    public override void OnDeinitializeMelon() => LoaderAdapter.Detach();

    // The per-frame seam. MelonLoader calls OnUpdate every frame on the main thread (its own resident support
    // module drives it — no injection needed, unlike BepInEx), and MelonMod exposes OnGUI for free. BOTH one-line
    // into FrameDriver: no second copy of the per-frame sequence (the ITick/IGui-inert bug cannot recur).
    public override void OnUpdate() => FrameDriver.Tick();   // MainThread.Drain + Coroutines.Tick + Mods.Tick
    public override void OnGUI()    => FrameDriver.Gui();    // Mods.Gui

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void StartCsModLoop()
    {
        string ml = MelonEnvironment.MelonLoaderDirectory;
        string gameRoot = MelonEnvironment.GameRootDirectory;
        string engineDir = Path.GetDirectoryName(typeof(InutilMelonMod).Assembly.Location)!;
        string libsDir = Path.Combine(ml, "inutil-libs");
        var refDirs = new[]
        {
            Path.Combine(gameRoot, "dotnet"),                 // the .NET BCL (if bundled)
            Path.Combine(ml, "Il2CppAssemblies"),             // the loader's il2cpp game proxies
            Path.Combine(ml, "net6"),                         // Il2CppInterop + MelonLoader
            Path.Combine(gameRoot, "Mods"),                   // Inutil.dll deployed beside the mods
            engineDir,                                        // …or the mod's own dir
            libsDir,
        };
        // Same three named lifecycles (§7.9), same order — shared verbatim with the BepInEx host.
        ModLibs.LoadSync(libsDir, refDirs, MelonLogger.Msg);
        Coremods.LoadSync(Path.Combine(ml, "inutil-coremods"), refDirs, MelonLogger.Msg);
        CsModHost.Start(Path.Combine(ml, "inutil-mods"), refDirs, MelonLogger.Msg);
    }
}
#endif
