using Inutil.InteropPatch;

namespace Inutil.InteropPatch.Tests;

// Pure offline tests for the `--game <gameDir>` locator (docs/contribution/architecture/16-metadata.md). Builds SYNTHETIC loader dir trees
// in temp — no provisioned game, no wine — so the locator's path logic is gated even on a box with no ToyGame.
static class GameLocatorTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]");
            if (!ok) failures++;
        }

        string root = Path.Combine(Path.GetTempPath(), "inutil-locator-" + Guid.NewGuid().ToString("N"));
        try
        {
            // BepInEx tree, fully populated: proxies + GameAssembly.dll + <Game>_Data metadata.
            string bep = Path.Combine(root, "bepinex");
            MakeDir(bep, "BepInEx", "interop");
            Touch(bep, "GameAssembly.dll");
            MakeDir(bep, "ToyGame_Data", "il2cpp_data", "Metadata");
            Touch(Path.Combine(bep, "ToyGame_Data", "il2cpp_data", "Metadata"), "global-metadata.dat");

            GameLayout? b = GameLocator.Locate(bep);
            Check("BepInEx layout detected", b?.Loader == LoaderKind.BepInEx);
            Check("BepInEx interop dir located", b is not null && Path.GetFullPath(b.InteropDir) == Path.GetFullPath(Path.Combine(bep, "BepInEx", "interop")));
            Check("BepInEx GameAssembly.dll located", b?.GameAssemblyDll is not null && File.Exists(b.GameAssemblyDll));
            Check("BepInEx global-metadata.dat located (glob on *_Data)", b?.GlobalMetadataDat is not null && File.Exists(b.GlobalMetadataDat));

            // MelonLoader tree, proxies only (metadata inputs absent) — best-effort nulls, still a valid layout.
            string melon = Path.Combine(root, "melon");
            MakeDir(melon, "MelonLoader", "Il2CppAssemblies");

            GameLayout? m = GameLocator.Locate(melon);
            Check("MelonLoader layout detected", m?.Loader == LoaderKind.MelonLoader);
            Check("MelonLoader interop dir located", m is not null && Path.GetFullPath(m.InteropDir) == Path.GetFullPath(Path.Combine(melon, "MelonLoader", "Il2CppAssemblies")));
            Check("absent GameAssembly.dll -> null (best-effort, not a failure)", m?.GameAssemblyDll is null);
            Check("absent global-metadata.dat -> null (best-effort, not a failure)", m?.GlobalMetadataDat is null);

            // No known loader layout -> null (the caller fails loud; never silently proceeds).
            string bare = Path.Combine(root, "bare");
            MakeDir(bare, "SomethingElse");
            Check("no known loader layout -> null", GameLocator.Locate(bare) is null);

            // A nonexistent dir -> null (not an exception).
            Check("missing game dir -> null", GameLocator.Locate(Path.Combine(root, "does-not-exist")) is null);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
        return failures;
    }

    static void MakeDir(string baseDir, params string[] parts) =>
        Directory.CreateDirectory(Path.Combine(new[] { baseDir }.Concat(parts).ToArray()));

    static void Touch(string dir, string file) => File.WriteAllText(Path.Combine(dir, file), "");
}
