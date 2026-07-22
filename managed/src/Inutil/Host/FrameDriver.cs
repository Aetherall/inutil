// The ONE place per-frame lifecycle driving is implemented. Both loader shims — BepInEx's injected InutilPump
// and MelonLoader's OnUpdate/OnGUI — call THIS; neither hand-rolls the sequence. With one FrameDriver there is
// no second copy to forget half of: a prior bug (MelonLoader mods with ITick/IGui silently inert) was exactly a
// SECOND, incomplete copy of "drive the per-frame seams" — structurally impossible now. Same discipline the
// native side proves (LoaderAdapter/inutil_core.dll — two loaders, one core), one layer up.
namespace Inutil.Host;

public static class FrameDriver
{
    // Per-frame, ON the main thread. Drains the action queue and steps coroutines (MainThread.Drain — which also
    // captures the main-thread id and latches IsPumping), polls the config watcher (time-gated inside — a changed
    // cfg re-fires Configure BEFORE this frame's Ticks see the values), THEN drives every mod's ITick. Order
    // matters: an action/coroutine a mod's Tick posts this frame begins NEXT frame.
    public static void Tick() { MainThread.Drain(); Inutil.ModConfigStore.Tick(); Inutil.Mods.Tick(); }

    // Per-IMGUI-frame — the HUD seam. Drives every mod's IGui. Kept separate from Tick because a loader delivers
    // OnGUI on its own cadence (distinct from Update), and IMGUI code must run inside that callback.
    public static void Gui() => Inutil.Mods.Gui();
}
