// The collectible-AssemblyLoadContext mod-host primitive — a collectible ALC that loads ONE mod assembly while
// SHARING the host's already-loaded assemblies (Inutil.dll, the interop proxies, Il2CppInterop), so the mod's
// hooks land in the host's one true table and the only host-side root of the ALC is the mod itself. Drop the
// mod's hooks + lifecycle (Mods.RemoveAll) and the ALC collects.
//
// Substrate-AGNOSTIC: knows nothing about a game or which loader hosts. Lives in Inutil.dll so the mod host
// (Inutil.Mods) — and any consumer — reuses it, and so it defers a mod's shared references to the context that
// loaded INUTIL.DLL, which is exactly the host plugin's context (where the proxies + Il2CppInterop live).
using System.Reflection;
using System.Runtime.Loader;

namespace Inutil.Host;

// Crucially we defer shared assemblies to the HOST plugin's load context, NOT AssemblyLoadContext.Default: a
// real loader (BepInEx / MelonLoader) loads the host (Inutil.dll) into its OWN context, and the interop proxies
// + Inutil.dll are loaded THERE — a plain null-return (which falls back to Default) could spawn FRESH copies
// from the app base, breaking type identity (two Il2CppInterop runtimes, one initialized). We hand back the
// host's live copies by deferring to the context that loaded ModContext's own assembly.
public sealed class ModContext : AssemblyLoadContext
{
    private static readonly AssemblyLoadContext Host =
        GetLoadContext(typeof(ModContext).Assembly) ?? Default;

    public ModContext(string name) : base(name, isCollectible: true) { }

    protected override Assembly? Load(AssemblyName name)
    {
        // Resolve EVERY dependency the mod references against the host's context — loading it there if the host
        // hasn't lazily touched it yet. The subtle part: a mod's registration deps (e.g. System.Linq.Expressions
        // behind the Hooks Expression<> selectors) may not be loaded until the FIRST Hooks.Register — which, for
        // a mod, happens INSIDE this ALC. A plain null-return would spawn a private copy here, so the mod's
        // Expression<Func<T,R>> would differ from the host Hooks parameter type and binding fails. Pulling the dep
        // into the host guarantees ONE shared copy. The mod assembly itself is loaded via LoadFromStream /
        // LoadFromAssemblyPath, so Load() is never called for it; a genuinely mod-private dep the host can't
        // resolve falls back to a local load.
        try { return Host.LoadFromAssemblyName(name); }
        catch { return null; }
    }
}
