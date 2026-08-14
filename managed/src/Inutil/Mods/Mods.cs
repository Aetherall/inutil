// The mod-facing surface (L4 modder API) — lifecycle seams + the registry the host drives per frame and tears
// down per collectible ALC. The hook ENGINE is its own thing (Inutil.Hooks.Hooks); Mods is only the mod-facing
// facade OVER it (lifecycle + the ergonomic Hook<T> discovery chain).
//
// Substrate-AGNOSTIC: names no loader type. The per-frame seams (Tick/Gui) are PUSHED in by FrameDriver on the
// main thread; the teardown seam (RemoveAll) is called by the mod host on ALC unload. Lives in Inutil.dll
// (namespace Inutil) so a mod writes `using Inutil;` and any host reuses the same registry.
using System.Runtime.Loader;

namespace Inutil;

// ── lifecycle seams ─────────────────────────────────────────────────────────────────────────────────────
// A mod implements any of these on any type; the host discovers them (Discover) or registers an instance
// directly (Mods.Add). OnLoad fires once at registration; OnUnload at teardown (Mods.RemoveAll for the mod's
// ALC). Tick/OnGui are per-frame, driven by FrameDriver via Mods.Tick()/Mods.Gui() — the same main-thread seam
// MainThread.Drain already owns.
public interface ILoad   { void OnLoad(); }
public interface IUnload { void OnUnload(); }
public interface ITick   { void Tick(); }
public interface IGui    { void OnGui(); }

// The host-wide registry of live mod lifecycle objects. Static so every loader and the REPL share one set.
// Each per-frame / teardown callback is EXCEPTION-ISOLATED (routed to Hooks.OnWarning) so one bad mod never
// stalls the frame, its siblings, or teardown (P4). Snapshot-iterated so a mod registering another mid-tick
// doesn't mutate the loop it's running inside.
public static partial class Mods
{
    private static readonly List<object> _lifecycle = new();
    private static readonly object _gate = new();

    // Register an already-instantiated mod object, firing OnLoad now if it implements ILoad. The object is
    // retained for per-frame driving (ITick/IGui) and teardown (IUnload via RemoveAll). Discover instantiates a
    // mod's lifecycle types and routes each through here — one registration path.
    public static void Add(object modObject)
    {
        if (modObject is null) throw new ArgumentNullException(nameof(modObject));
        lock (_gate) _lifecycle.Add(modObject);
        if (modObject is ILoad l) Safe(l.OnLoad);
    }

    // Per-frame seams the host pump calls via FrameDriver, ON the main thread. Snapshot so a mod registering
    // another mid-tick begins next frame; each implementer exception-isolated.
    public static void Tick() { foreach (object o in Snapshot()) if (o is ITick t) Safe(t.Tick); }
    public static void Gui()  { foreach (object o in Snapshot()) if (o is IGui g) Safe(g.OnGui); }

    // Drop the lifecycle objects owned by `owner`, firing OnUnload on each. Split out so the fuller RemoveAll
    // (in Mods.Discover.cs) can drop this mod's ergonomic hook subscriptions FIRST, then its lifecycle — one
    // RemoveAll fully retires a mod so its collectible ALC can collect (M-live un-root). Returns the count.
    private static int RemoveLifecycle(AssemblyLoadContext owner)
    {
        int removed = 0;
        lock (_gate)
        {
            for (int i = _lifecycle.Count - 1; i >= 0; i--)
                if (AssemblyLoadContext.GetLoadContext(_lifecycle[i].GetType().Assembly) == owner)
                {
                    if (_lifecycle[i] is IUnload u) Safe(u.OnUnload);
                    _lifecycle.RemoveAt(i);
                    removed++;
                }
        }
        return removed;
    }

    // Count of registered lifecycle objects (diagnostics / tests).
    public static int Count { get { lock (_gate) return _lifecycle.Count; } }

    private static object[] Snapshot() { lock (_gate) return _lifecycle.ToArray(); }

    // Exception-isolate a lifecycle callback: a throw routes to Hooks.OnWarning, never crashing the frame or a
    // sibling mod (the same isolation the pump / coroutine runner use).
    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex)
        {
            Hooks.Hooks.OnWarning?.Invoke($"inutil mod: lifecycle callback threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
