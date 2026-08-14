// docs/contribution/architecture/16-metadata.md — the in-game gate for the content-addressed interop marker (docs/reference/limits.md, Gap 3, the
// last silent-misbehavior seam). validate.sh patches the proxies in-place with `inutil-interoppatch` (the net9 CLI),
// which stamps the marker; the RUNNING build (net6) reads it at Attach via LoaderShim. These cases prove that
// end-to-end under BOTH loaders: the live-patched dir verifies Current against this build's own schema hash, and a
// foreign/absent marker is caught as Stale/Missing. The only per-loader difference is WHERE the interop dir lives
// (BepInEx/interop vs MelonLoader/Il2CppAssemblies) — the same path the production shim passes to the loader shim.
#if BEPINEX || MELONLOADER
using System;
using System.IO;
using Inutil.Host;
using Inutil.Schema;
#if BEPINEX
using BepInEx;
#else
using MelonLoader.Utils;
#endif

namespace Inutil.Battery;

public static class InteropMarkerCases
{
    // The interop dir THIS loader patched + reads its marker from — the exact path the production shim hands to
    // LoaderShim.WarnIfInteropUnpatched (BepInExPlugin / InutilMelonMod). One fact, spelled per loader.
    static string InteropDir =>
#if BEPINEX
        Path.Combine(Paths.BepInExRootPath, "interop");
#else
        Path.Combine(MelonEnvironment.MelonLoaderDirectory, "Il2CppAssemblies");
#endif

    public static void Register(Suite suite)
    {
        // The money case: the proxies THIS run loaded were patched by the offline net9 CLI, which stamped a marker
        // addressed by SchemaMarker.Hash(Families.Default()). The net6 runtime recomputes that hash and must agree —
        // proving stamp@patch -> read@load across the TFM boundary in a booted game (under whichever loader). If hash
        // determinism ever breaks between the CLI and the runtime, THIS goes red — not a modder's mod, later, with a
        // cryptic MissingMethodException.
        suite.Add("interop.marker.current", () =>
        {
            string interopDir = InteropDir;
            MarkerVerdict verdict = LoaderShim.CheckInterop(interopDir);
            Check.True(verdict == MarkerVerdict.Current,
                $"interop marker at '{interopDir}' verified {verdict}, expected Current — the live-patched proxies " +
                "are not recognized by this build's schema hash (a stamp/read drift or non-deterministic hash)");
            return $"live interop marker Current @ {interopDir}";
        });

        // The detector fires (the fail-loud half): an unpatched dir is Missing and a marker from a DIFFERENT schema is
        // Stale — proven in-game against a throwaway temp dir, so the real interop dir is never touched.
        suite.Add("interop.marker.detects-mismatch", () =>
        {
            string tmp = Path.Combine(Path.GetTempPath(), "inutil-marker-ingame-" + Guid.NewGuid().ToString("N"));
            try
            {
                Check.True(LoaderShim.CheckInterop(tmp) == MarkerVerdict.Missing,
                    "an unpatched (empty) dir must verify Missing");
                ContentMarker.Stamp(tmp, SchemaMarker.InteropMarkerFileName, "not-a-real-schema-hash", "foreign schema");
                Check.True(LoaderShim.CheckInterop(tmp) == MarkerVerdict.Stale,
                    "a foreign-hash marker must verify Stale");
                return "Missing (unpatched) + Stale (foreign schema) both detected in-game";
            }
            finally { try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ } }
        });
    }
}
#endif
