using System.IO;

namespace Inutil.InteropPatch;

// Which mod loader's layout a game directory presents. The one fact that says "where does this loader keep its
// Il2CppInterop proxies", named ONCE here on the library side — the single source of truth for the `--game`
// locator (validate.sh spells the same paths independently, for its own deploy prechecks; that is orchestration,
// not the engine's locator).
public enum LoaderKind { BepInEx, MelonLoader }

// The located inputs of a `--game <gameDir>` pass — the SHARED inputs the multi-stage pass consumes
// (docs/contribution/architecture/16-metadata.md, Generalization C). Today only the patch stage runs, consuming
// InteropDir; the metadata extract stage (to come) consumes GameAssemblyDll + GlobalMetadataDat. Locating all three
// in one place lets the second stage hang off this same record, not re-detect the layout.
public sealed record GameLayout(
    LoaderKind Loader,
    string GameDir,
    string InteropDir,            // the generated proxies to patch — always present (it is HOW the loader is detected)
    string? GameAssemblyDll,      // the native metadata image — null if not beside the game
    string? GlobalMetadataDat);   // <Game>_Data/il2cpp_data/Metadata/global-metadata.dat — null if absent

// Auto-detect a game directory's loader layout and locate its inputs — the `--game` locator. Pure path logic (no
// Cecil, no game), so it is unit-testable against synthetic dir trees. The precursor the pillar's multi-stage pass
// hangs off (docs/contribution/architecture/16-metadata.md): one place to locate the shared inputs.
public static class GameLocator
{
    // Where each known loader keeps its Il2CppInterop proxies, RELATIVE to the game dir. The single library-side
    // spelling of this fact; detection is simply "which of these subdirs exists under gameDir".
    static readonly (LoaderKind Loader, string RelInterop)[] Layouts =
    {
        (LoaderKind.BepInEx,     Path.Combine("BepInEx", "interop")),
        (LoaderKind.MelonLoader, Path.Combine("MelonLoader", "Il2CppAssemblies")),
    };

    // Detect + locate. Returns null when gameDir presents no known loader layout (neither proxy dir exists) — the
    // caller then fails LOUD with a precise message, never silently proceeds. GameAssembly.dll / global-metadata.dat
    // are best-effort (nullable): the patch stage does not need them, and the metadata stage fails loud on its own
    // if it runs without them — so a game missing them still patches fine today.
    public static GameLayout? Locate(string gameDir)
    {
        if (!Directory.Exists(gameDir)) return null;

        foreach ((LoaderKind loader, string relInterop) in Layouts)
        {
            string interop = Path.Combine(gameDir, relInterop);
            if (!Directory.Exists(interop)) continue;
            return new GameLayout(loader, gameDir, interop, FindGameAssembly(gameDir), FindGlobalMetadata(gameDir));
        }
        return null;
    }

    static string? FindGameAssembly(string gameDir)
    {
        string p = Path.Combine(gameDir, "GameAssembly.dll");
        return File.Exists(p) ? p : null;
    }

    // <Game>_Data/il2cpp_data/Metadata/global-metadata.dat — the "*_Data" folder name is exe-specific (ToyGame_Data
    // here), so glob for it rather than hard-code the game's name.
    static string? FindGlobalMetadata(string gameDir)
    {
        foreach (string dataDir in Directory.EnumerateDirectories(gameDir, "*_Data"))
        {
            string p = Path.Combine(dataDir, "il2cpp_data", "Metadata", "global-metadata.dat");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
