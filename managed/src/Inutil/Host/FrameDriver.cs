// The ONE place per-frame lifecycle driving is implemented. Both loader shims — BepInEx's injected InutilPump
// and MelonLoader's OnUpdate/OnGUI — call THIS; neither hand-rolls the sequence. With one FrameDriver there is
// no second copy to forget half of: a prior bug (MelonLoader mods with ITick/IGui silently inert) was exactly a
// SECOND, incomplete copy of "drive the per-frame seams" — structurally impossible now. Same discipline the
// native side proves (LoaderAdapter/inutil_core.dll — two loaders, one core), one layer up.
namespace Inutil.Host;

public static class FrameDriver
{
    // Optional liveness heartbeat (INUTIL_HEARTBEAT_FILE): "<unixMs>,<frames>" written ~every 32 frames from
    // the same main-thread pump Tick() drives — a fresh write IS proof the game thread is pumping, which is
    // what a consumer's watchdog / status tooling wants to read from one file. Unset = a null check per frame.
    static readonly string? HeartbeatFile = System.Environment.GetEnvironmentVariable("INUTIL_HEARTBEAT_FILE");
    static long _frames;

    // Per-frame, ON the main thread. Drains the action queue and steps coroutines (MainThread.Drain — which also
    // captures the main-thread id and latches IsPumping), polls the config watcher (time-gated inside — a changed
    // cfg re-fires Configure BEFORE this frame's Ticks see the values), THEN drives every mod's ITick. Order
    // matters: an action/coroutine a mod's Tick posts this frame begins NEXT frame.
    public static void Tick()
    {
        MainThread.Drain(); Inutil.ModConfigStore.Tick(); Inutil.Mods.Tick();
        if (HeartbeatFile is null || (++_frames & 31) != 0) return;
        try { System.IO.File.WriteAllText(HeartbeatFile, System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "," + _frames); }
        catch { /* a transient FS error must never fault a frame */ }
    }

    // Per-IMGUI-frame — the HUD seam. Drives every mod's IGui. Kept separate from Tick because a loader delivers
    // OnGUI on its own cadence (distinct from Update), and IMGUI code must run inside that callback.
    public static void Gui() => Inutil.Mods.Gui();
}
