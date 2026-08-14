using Inutil.InteropPatch;
using Inutil.Metadata;

// inutil-metadata-extract — the metadata pillar's OPT-IN author-time CLI (docs/contribution/architecture/16-metadata.md):
// recover the authored JSON wire names + converter kinds the game's il2cpp metadata carries (and Il2CppInterop's
// proxies strip) into an `inutil.wiremap.json` sidecar — the wire-protocol map InteropPatch/WireMap.cs consumes at
// patch time. Separate from the patch CLI, never on the runtime/patch path.
// Usage:
//   inutil-metadata-extract --game <gameDir> [<wireMapPath>]
//     (default: <interopDir>/inutil.wiremap.json, beside the proxies the mod compiles against)
//   inutil-metadata-extract <GameAssembly.dll> <global-metadata.dat> <wireMapPath>
//     (direct form: no loader layout needed — e.g. straight off a build output dir)
// Exit: 0 on success, 2 on a usage/path error.

// Direct form: explicit GameAssembly.dll + global-metadata.dat + output path (bypasses the loader-layout locator).
if (args.Length == 3 && args[0] != "--game")
{
    string gaDll = args[0], metaDat = args[1], outPath = args[2];
    if (!File.Exists(gaDll)) { Console.Error.WriteLine($"!! GameAssembly.dll not found: {gaDll}"); return 2; }
    if (!File.Exists(metaDat)) { Console.Error.WriteLine($"!! global-metadata.dat not found: {metaDat}"); return 2; }
    Console.WriteLine($">> direct extract\n   game asm: {gaDll}\n   metadata: {metaDat}\n   wire-map: {outPath}");
    MetadataExtractResult dr = new MetadataExtractor().Extract(gaDll, metaDat, outPath, Console.Out);
    Console.WriteLine($"\n== {dr.WireMapTypesEmitted} mapped type(s) from {dr.TypesScanned} types ({dr.MembersClassified} members); "
                      + $"{dr.Warnings.Count} warning(s); wire-map {(dr.Changed ? "written" : "unchanged")} -> {dr.WireMapPath} ==");
    return 0;
}

if (args.Length < 2 || args[0] != "--game")
{
    Console.Error.WriteLine("usage: inutil-metadata-extract --game <gameDir> [<wireMapPath>]");
    Console.Error.WriteLine("       inutil-metadata-extract <GameAssembly.dll> <global-metadata.dat> <wireMapPath>");
    return 2;
}

string gameDir = args[1];
GameLayout? layout = GameLocator.Locate(gameDir);
if (layout is null)
{
    Console.Error.WriteLine(
        $"!! no known loader layout under {gameDir} (looked for BepInEx/interop, MelonLoader/Il2CppAssemblies)");
    return 2;
}
if (layout.GameAssemblyDll is null || layout.GlobalMetadataDat is null)
{
    Console.Error.WriteLine(
        $"!! {layout.Loader} layout found, but GameAssembly.dll / global-metadata.dat are missing under {gameDir} — cannot extract");
    return 2;
}

string wireMap = args.Length >= 3 ? args[2] : Path.Combine(layout.InteropDir, WireMapEmitter.FileName);

Console.WriteLine($">> --game {gameDir}: detected {layout.Loader}");
Console.WriteLine($"   game asm: {layout.GameAssemblyDll}");
Console.WriteLine($"   metadata: {layout.GlobalMetadataDat}");
Console.WriteLine($"   wire-map: {wireMap}");

MetadataExtractResult r = new MetadataExtractor().Extract(layout.GameAssemblyDll, layout.GlobalMetadataDat, wireMap, Console.Out);

Console.WriteLine($"\n== {r.WireMapTypesEmitted} mapped type(s) from {r.TypesScanned} types ({r.MembersClassified} members); "
                  + $"{r.Warnings.Count} warning(s); wire-map {(r.Changed ? "written" : "unchanged")} -> {r.WireMapPath} ==");
return 0;
