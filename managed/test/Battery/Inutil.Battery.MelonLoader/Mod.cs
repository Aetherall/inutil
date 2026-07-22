// The MelonLoader-specific surface of the battery: a MelonMod that runs the shared Suite on init and writes
// the sidecar into the game root. The mirror of the BepInEx shim's Plugin.cs — same Suite, same cases, only
// the loader entry differs.
#if MELONLOADER
using MelonLoader;
using MelonLoader.Utils;
using Inutil.TestKit;

[assembly: MelonInfo(typeof(Inutil.Battery.BatteryMelonMod), "inutil-battery", "0.1.0", "aetherall")]
[assembly: MelonGame]   // universal — the battery is game-agnostic bring-up

namespace Inutil.Battery;

public sealed class BatteryMelonMod : MelonMod
{
    public override void OnInitializeMelon()
    {
        var sidecar = Path.Combine(MelonEnvironment.GameRootDirectory, Suite.SidecarName);
        MelonLogger.Msg($"[inutil-battery] running smoke battery -> {sidecar}");
        try
        {
            var suite = new Suite("smoke");
            SmokeCases.Register(suite);
            TaskFlipCases.Register(suite);
            ContainerCases.Register(suite);
            ContainerFlipCases.Register(suite);
            HookCases.Register(suite);   // native hook engine — its first case attaches inutil_core.dll
            ValueTypeCases.Register(suite);   // ValueTypeBridge — ref-bearing VT in tuple/dict container writes
            ModHostCases.Register(suite);   // CAPSTONE — a compiled Hook<Game> mod fires (namespace adapted to Il2CppToyGame)
            CallbackCases.Register(suite);   // ConvKind.Callback — the inbound callback mirror end-to-end (needs the ToyGame fixture rebuild; SKIPs otherwise)
            ReplCases.Register(suite);   // the in-game C# REPL: Roslyn evaluates live submissions that hook the running game
            InteropMarkerCases.Register(suite);   // the content-addressed interop marker, proven under melon too
            SafeCases.Register(suite);   // docs/contribution/architecture/17-reach-faces.md — Inutil.Safe fault guard (TryInvoke substrate + Safe.Run face); LAST: its caught faults must not perturb earlier cases
            suite.RunAll(new FileResultSink(sidecar, "smoke"));
            MelonLogger.Msg("[inutil-battery] DONE smoke");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[inutil-battery] battery driver threw before completing: {ex}");
        }
    }
}
#endif
