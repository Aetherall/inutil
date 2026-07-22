// Shared loader-shim helpers — the loader-AGNOSTIC bring-up both production shims (Inutil.BepInEx,
// Inutil.MelonLoader) call, so neither hand-rolls it. Same "two loaders, one core" discipline the native
// LoaderAdapter proves, one layer up: per-frame driving is FrameDriver (one impl), the interop-unpatched safety
// net is HERE (one impl), each shim a thin call — never a second copy to drift. Shim-SPECIFIC parts stay in the
// shims: BepInEx injects a MonoBehaviour pump (its BasePlugin has no per-frame callback) while MelonLoader gets
// OnUpdate/OnGUI for free; and the .cs mod loop lives in the shims because it references the optional
// Roslyn-carrying Inutil.Mods, which the SDK doesn't.
using Inutil.Schema;

namespace Inutil.Host;

public static class LoaderShim
{
    // Warn — loudly and actionably — if the loader's il2cpp proxies were not patched by inutil, so a mod compiled
    // against natural types doesn't die later with a cryptic MissingMethodException. BOTH shims call this one impl
    // right after Attach. It reads the content-addressed marker the interop patcher stamps
    // (SchemaMarker.InteropMarkerFileName) and compares it to THIS build's own schema hash — catching both "never
    // patched" (Missing) and "patched by a different inutil schema" (Stale). Plain on-disk read (no Assembly.Load)
    // so it stays correct before the loader lazily loads the proxies.
    public static void WarnIfInteropUnpatched(string interopDir, System.Action<string> warn)
    {
        switch (CheckInterop(interopDir))
        {
            case MarkerVerdict.Missing:
                warn($"inutil: the il2cpp proxies in '{interopDir}' carry no inutil patch marker — they were not " +
                     "patched (or predate marker support). A mod compiled against natural types may fail later with a " +
                     "cryptic MissingMethodException. Regenerate: inutil-interoppatch --game <gameDir>.");
                break;
            case MarkerVerdict.Stale:
                warn($"inutil: the il2cpp proxies in '{interopDir}' were patched by a DIFFERENT inutil schema than " +
                     "this build (stale marker) — natural-typed signatures may not match, risking MissingMethodException. " +
                     "Re-run inutil-interoppatch --game <gameDir> after updating inutil.");
                break;
            case MarkerVerdict.Current:
                break;   // silent — the proxies on disk were patched by an inutil whose schema matches this build
        }
    }

    // The ONE marker check both the warning above and the in-game battery gate read — no second copy. The expected
    // content-address is THIS build's own registry (Families.Default()), computed like the patcher computed the
    // stamped one (SchemaMarker is TFM-independent, so the net6 runtime and the net9 patch CLI agree).
    public static MarkerVerdict CheckInterop(string interopDir) =>
        ContentMarker.Verify(interopDir, SchemaMarker.InteropMarkerFileName, SchemaMarker.Hash(Families.Default()));
}
