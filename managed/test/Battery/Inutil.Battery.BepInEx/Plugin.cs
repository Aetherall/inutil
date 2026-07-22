// The BepInEx-specific surface of the battery: derive BasePlugin, run the shared Suite on Load(), write the
// sidecar into the game root. Everything testable lives in ../Cases; this file is only the loader glue.
#if BEPINEX
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Inutil.TestKit;

namespace Inutil.Battery;

[BepInPlugin("aetherall.inutil.battery", "inutil-battery", "0.1.0")]
public sealed class BatteryPlugin : BasePlugin
{
    public override void Load()
    {
        // Paths.GameRootPath is the game directory, which is the host-mapped loader tree under wine — the
        // driver reads the sidecar back from there after the game exits.
        var sidecar = Path.Combine(Paths.GameRootPath, Suite.SidecarName);
        Log.LogInfo($"[inutil-battery] running smoke battery -> {sidecar}");
        // Surface a hook dispatch's SWALLOWED warning (Around isolates a throwing hook body to OnWarning so it
        // can't crash the game) into LogOutput.log — otherwise a hook that fails at the boundary shows only as a
        // wrong assertion with no cause. Diagnostics only; the case still passes/fails purely on its own oracle.
        Inutil.Hooks.Hooks.OnWarning += m => Log.LogWarning($"[inutil-hook] {m}");
        try
        {
            var suite = new Suite("smoke");
            SmokeCases.Register(suite);
            TaskFlipCases.Register(suite);
            ContainerCases.Register(suite);
            ContainerFlipCases.Register(suite);
            HookCases.Register(suite);   // native hook engine — its first case attaches inutil_core.dll
            ValueTypeCases.Register(suite);   // ValueTypeBridge — ref-bearing VT in tuple/dict container writes
            ModHostCases.Register(suite);   // CAPSTONE — a compiled Hook<Game> mod fires + FrameDriver drives it
            CallbackCases.Register(suite);   // ConvKind.Callback — the inbound callback mirror end-to-end (needs the ToyGame fixture rebuild; SKIPs otherwise)
            ReplCases.Register(suite);   // the in-game C# REPL: Roslyn evaluates live submissions that hook the running game
            InteropMarkerCases.Register(suite);   // docs/contribution/architecture/16-metadata.md — the content-addressed interop marker (docs/reference/limits.md, Gap 3)
            SafeCases.Register(suite);   // docs/contribution/architecture/17-reach-faces.md — Inutil.Safe fault guard (TryInvoke substrate + Safe.Run face); LAST: its caught faults must not perturb earlier cases
            suite.RunAll(new FileResultSink(sidecar, "smoke"));
            Log.LogInfo("[inutil-battery] DONE smoke");
        }
        catch (Exception ex)
        {
            // A throw here means the driver itself broke before writing `done`; the aggregator will read the
            // missing/absent file as red. Log loudly so the cause is visible in LogOutput.log.
            Log.LogError($"[inutil-battery] battery driver threw before completing: {ex}");
        }
    }
}
#endif
