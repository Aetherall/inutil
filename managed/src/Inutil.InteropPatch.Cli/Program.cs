using Inutil.InteropPatch;

// inutil-interoppatch — apply the IL-rewrite to Il2CppInterop proxies, in place and atomically.
// Usage:
//   inutil-interoppatch <interopDir>        patch a proxy directory directly (or set INUTIL_INTEROP_DIR)
//   inutil-interoppatch --game <gameDir>    auto-detect the loader layout under gameDir, then patch its proxies
// Exit: 0 on success (including an already-patched no-op), 2 on a usage/path error.

string? interopDir;
if (args.Length >= 1 && args[0] == "--game")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: inutil-interoppatch --game <gameDir>");
        return 2;
    }
    string gameDir = args[1];
    // Detect the loader layout, locate the shared inputs (the metadata extract stage hangs off this SAME layout).
    GameLayout? layout = GameLocator.Locate(gameDir);
    if (layout is null)
    {
        Console.Error.WriteLine(
            $"!! no known loader layout under {gameDir} " +
            "(looked for BepInEx/interop, MelonLoader/Il2CppAssemblies)");
        return 2;
    }
    Console.WriteLine($">> --game {gameDir}: detected {layout.Loader}");
    Console.WriteLine($"   proxies:  {layout.InteropDir}");
    Console.WriteLine($"   game asm: {layout.GameAssemblyDll ?? "(absent)"}");
    Console.WriteLine($"   metadata: {layout.GlobalMetadataDat ?? "(absent)"}");
    interopDir = layout.InteropDir;
}
else
{
    interopDir = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("INUTIL_INTEROP_DIR");
}

if (interopDir is null)
{
    Console.Error.WriteLine("usage: inutil-interoppatch <interopDir> | --game <gameDir>   (or set INUTIL_INTEROP_DIR)");
    return 2;
}
if (!Directory.Exists(interopDir))
{
    Console.Error.WriteLine($"!! interop dir not found: {interopDir}");
    return 2;
}

Console.WriteLine($">> patching proxies in {interopDir}");
DirectoryPatchResult r = InteropPatcher.PatchDirectory(interopDir, Console.Out);

Console.WriteLine($"\n== patched {r.Patched.Count} DLL(s), {r.TotalFlipped} member(s) flipped; " +
                  $"{r.Unchanged.Count} unchanged, {r.Unreadable.Count} non-.NET ==");
// Every defer, from every module — including ones that flipped nothing (r.Patched holds only the modules that did),
// which is where a wholly-deferred module used to go silent. GAME modules are printed individually; FRAMEWORK ones
// (Il2Cppmscorlib, UnityEngine.*) are summarized: they defer in bulk by design, and 59 lines of them is how a single
// game defer gets missed. The full list is on DirectoryPatchResult.Defers for anything that wants it.
var gameDefers = r.Defers.Where(x => !CecilProjector.IsFrameworkAssembly(Path.GetFileNameWithoutExtension(x.Dll))).ToList();
foreach (var (dll, d) in gameDefers)
    Console.WriteLine($"   defer  {dll}: {d}");
if (r.Defers.Count > gameDefers.Count)
    Console.WriteLine($"   defer  ({r.Defers.Count - gameDefers.Count} more in framework proxies — deferred by design)");

// What the patch LEFT behind, whether or not any pass noticed it. Known deferrals are a count; a residual with no
// known reason is a hole a consumer meets as a raw il2cpp spelling, so it is named individually.
if (r.Residual.Count > 0)
{
    Console.WriteLine($"\n== residual: {r.Residual.Count} member(s) left wearing a naturalizable il2cpp type " +
                      $"({r.Residual.Count - r.Unexplained.Count} known-deferred, {r.Unexplained.Count} unexplained) ==");
    foreach (Residual x in r.Unexplained)
        Console.WriteLine($"   !! HOLE  {x.Module}: {x.Member}  ({x.Why})");
}

// Disk-only step: re-attach the recovered wire names onto the proxies. The wiremap (produced offline by the
// metadata pillar) sits in the interop dir; INUTIL_WIREMAP overrides.
string wireMapPath = Environment.GetEnvironmentVariable("INUTIL_WIREMAP")
                     ?? Path.Combine(interopDir, "inutil.wiremap.json");
Console.WriteLine($"\n>> re-attaching wire names ({Path.GetFileName(wireMapPath)})");
int stampedTotal = InteropPatcher.StampWireAttributesDirectory(interopDir, wireMapPath, Console.Out);
Console.WriteLine($"== stamped {stampedTotal} wire attribute(s) ==");
return 0;
