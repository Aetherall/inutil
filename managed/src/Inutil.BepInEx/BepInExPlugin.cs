// The PRODUCTION BepInEx entrypoint (§7.10) — a thin BasePlugin over the SDK: derive BasePlugin, attach the
// loader-agnostic engine (LoaderAdapter, in Inutil.dll), inject a resident per-frame pump, start the no-build
// .cs mod loop. The hook engine, Hook<T>, FrameDriver, the mod host are shared verbatim with the MelonLoader host.
#if BEPINEX
using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using Inutil.Host;

namespace Inutil.Host;

[BepInPlugin("aetherall.inutil", "inutil", "0.1.0")]
public sealed class BepInExPlugin : BasePlugin
{
    public override void Load()
    {
        // BepInEx already il2cpp_init'd + started Il2CppInterop, so NO interop bring-up — purely additive.
        Inutil.Hooks.Hooks.OnWarning = m => Log.LogWarning(m);   // route inline-awareness + mod warnings to BepInEx's log
        LoaderAdapter.Attach();
        Log.LogInfo("inutil attached: hook engine wired via inutil_core.dll");

        // Where IConfigure mods' cfg files live (the ONE loader-specific fact the config seam needs) — the
        // standard BepInEx config dir, so the launcher's config editor finds them. Set before any mod loads.
        Inutil.ModConfigStore.Root = Path.Combine(Paths.BepInExRootPath, "config");

        // FALLBACK disk persist of the preloader patch (Inutil.BepInEx.Patcher). NORMALLY a no-op — the patcher's
        // ctor already disk-patched the proxies. Only when that failed and the in-memory session pass rescued the
        // boot does this persist to disk, BEFORE ModLibs/Coremods/mods compile (CsModCompiler reads its references
        // from disk, so compile-time and runtime must agree). Best-effort: deferred Cecil streams / resolver caches
        // usually still hold the files (Windows/wine denies rename-over), so it often fails into the warning below.
        // Gated on the preloader breadcrumb — a PARTIAL in-memory patch must never get a full disk persist (the
        // sides disagree at bind). NoInlining like StartCsModLoop so an absent Inutil.InteropPatch.dll degrades.
        try { PersistInteropPatch(); }
        catch (Exception ex)
        {
            Log.LogWarning($"inutil: interop disk persist failed ({ex.GetType().Name}: {ex.Message}) — natural-typed " +
                           "mod COMPILES may fail this session (the runtime side is patched in memory).");
        }

        // Shared interop-unpatched safety net (one impl, both shims call it). AFTER the persist, so a tree the
        // preloader+persist just healed reads Current and stays silent.
        LoaderShim.WarnIfInteropUnpatched(Path.Combine(Paths.BepInExRootPath, "interop"), m => Log.LogWarning(m));

        // BasePlugin has no per-frame callback (MelonLoader gives OnUpdate for free), so inject a resident
        // MonoBehaviour whose Update/OnGUI drive FrameDriver. Before the mod loop, so a mod's posted action has a tick.
        InstallPump();

        // The no-build .cs mod loop (Inutil.Mods — carries the Roslyn dep). Non-fatal: an absent Inutil.Mods
        // leaves the engine + built-DLL mods working (the JIT-time load is inside the try).
        try { StartCsModLoop(); }
        catch (Exception ex) { Log.LogWarning($"inutil: .cs mod subsystem not started ({ex.GetType().Name}: {ex.Message}); the engine + built-DLL mods still work."); }
    }

    public override bool Unload() { LoaderAdapter.Detach(); return true; }

    // Phase 2 of the two-phase natural-typing pass (phase 1 = Inutil.BepInEx.Patcher, in the preloader). Reads the
    // breadcrumb verdict, then runs the SAME on-disk driver the offline CLI runs (InteropPatcher.PatchDirectory —
    // atomic, idempotent, stamps the schema marker). The preloader byte-loaded the files, so they are unlocked on
    // disk. NoInlining: the Inutil.InteropPatch reference is JIT'd here, inside the caller's try.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void PersistInteropPatch()
    {
        string bep = Paths.BepInExRootPath;
        string interop = Path.Combine(bep, "interop");
        string breadcrumb = Path.Combine(bep, ".inutil-preload-patch");

        string? verdict = null;
        try { if (File.Exists(breadcrumb)) verdict = File.ReadAllText(breadcrumb).Trim(); } catch { }
        try { File.Delete(breadcrumb); } catch { }   // one-shot: consumed by this boot, never read again

        if (verdict is not null && verdict.StartsWith("fail", StringComparison.Ordinal))
        {
            Log.LogWarning($"inutil: the preloader's in-memory interop patch was PARTIAL ({verdict}) — skipping the " +
                           "disk persist so compile references never disagree with the loaded assemblies. " +
                           "Run inutil-interoppatch offline and relaunch.");
            return;
        }
        if (!Directory.Exists(interop)) return;
        if (LoaderShim.CheckInterop(interop) == Inutil.Schema.MarkerVerdict.Current) return;   // already on disk (offline flow / prior boot)

        Inutil.InteropPatch.DirectoryPatchResult r = Inutil.InteropPatch.InteropPatcher.PatchDirectory(interop, null);
        Log.LogInfo($"inutil: persisted the natural-typing patch to disk — {r.TotalFlipped} member(s) across " +
                    $"{r.Patched.Count} assembl{(r.Patched.Count == 1 ? "y" : "ies")} (schema {r.SchemaHash}); " +
                    "mod compiles now see the same surface the runtime binds.");
    }

    // Isolated (NoInlining) so its Inutil.Mods reference is JIT'd HERE — inside the caller's try — making an
    // absent Inutil.Mods non-fatal rather than faulting when Load() itself is JIT'd.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void StartCsModLoop()
    {
        string bep = Paths.BepInExRootPath;
        string gameRoot = Path.GetDirectoryName(bep)!;
        string engineDir = Path.GetDirectoryName(typeof(BepInExPlugin).Assembly.Location)!;   // where Inutil.dll + Roslyn sit
        string libsDir = Path.Combine(bep, "inutil-libs");
        var refDirs = new[]
        {
            Path.Combine(gameRoot, "dotnet"),   // the .NET BCL
            Path.Combine(bep, "interop"),       // the loader's il2cpp game proxies (Assembly-CSharp, UnityEngine, …)
            Path.Combine(bep, "core"),          // Il2CppInterop + BepInEx
            Paths.PluginPath,                   // Inutil.dll (flat dev overlay)
            engineDir,                          // …or the plugin's own dir (tier installer)
            libsDir,                            // boot-emitted shared libraries
        };
        // Three named lifecycles (§7.9), in order: shared libs -> pre-menu coremods -> hot-reload mods.
        ModLibs.LoadSync(libsDir, refDirs, m => Log.LogInfo(m));
        Coremods.LoadSync(Path.Combine(bep, "inutil-coremods"), refDirs, m => Log.LogInfo(m));
        CsModHost.Start(Path.Combine(bep, "inutil-mods"), refDirs, m => Log.LogInfo(m));
    }

    private static UnityEngine.GameObject? _pump;

    // Inject a resident host-owned MonoBehaviour ticker (BasePlugin is not one; BepInEx exposes no plugin-facing
    // per-frame hook). Roots only this already-permanent plugin context — never a collectible mod ALC.
    private static void InstallPump()
    {
        if (!ClassInjector.IsTypeRegisteredInIl2Cpp<InutilPump>())
            ClassInjector.RegisterTypeInIl2Cpp<InutilPump>();
        _pump = new UnityEngine.GameObject();                         // parameterless ctor (GameObject(string) can be stripped)
        _pump.hideFlags = UnityEngine.HideFlags.HideAndDontSave;      // hidden + not serialized
        UnityEngine.Object.DontDestroyOnLoad(_pump);                  // survive scene loads (host owns it)
        _pump.AddComponent<InutilPump>();
    }
}

// The injected resident ticker: Update/OnGUI fire every frame on the main thread and one-line into FrameDriver
// — the SAME single per-frame seam the MelonLoader host drives. No second copy of "drain + tick + gui" to
// forget half of (the ITick/IGui-inert bug, structurally retired).
public sealed class InutilPump : UnityEngine.MonoBehaviour
{
    public InutilPump(IntPtr ptr) : base(ptr) { }                    // the il2cpp-injection ctor convention
    public void Update() => FrameDriver.Tick();                      // MainThread.Drain + Coroutines.Tick + Mods.Tick
    public void OnGUI()  => FrameDriver.Gui();                       // Mods.Gui
}
#endif
