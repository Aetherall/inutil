using Inutil.InteropPatch;
using Inutil.InteropPatch.Cli;

// inutil-interoppatch — apply the IL-rewrite to Il2CppInterop proxies, in place and atomically.
// Usage:
//   inutil-interoppatch <interopDir>        patch a proxy directory directly (or set INUTIL_INTEROP_DIR)
//   inutil-interoppatch --game <gameDir>    auto-detect the loader layout under gameDir, then patch its proxies
//   [--summary] [--log <file>]              see below
// Exit: 0 on success (including an already-patched no-op), 1 when a module could not be written (the tree is
//       NOT at the schema — the marker was withheld), 2 on a usage/path error.

// Flags first, so the positional argument keeps its meaning wherever they appear. Both exist for a caller that
// is NOT a person at a terminal — a consumer's pre-launch pass, which hosts this assembly inside its own
// process and has one channel out.
//   --log <file>   mirror everything written here to <file> as well as stdout. stdout still gets every line;
//                  this is the copy that survives when stdout is a console nobody is reading (a Windows
//                  console-subsystem host under wine allocates one and swallows both std streams).
//   --summary      omit the PER-MEMBER detail — the ~100k FLIP lines a full patch emits. What is left is one
//                  line per assembly plus the totals, which is what "progress" means to someone watching a
//                  launch. The detail is a maintainer's tool, and a maintainer runs this at a terminal.
string? logPath = null;
bool summary = false;
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--log" && i + 1 < args.Length) { logPath = args[++i]; continue; }
    if (args[i] == "--summary") { summary = true; continue; }
    rest.Add(args[i]);
}
args = rest.ToArray();

TextWriter outw = Console.Out;
if (logPath is not null)
{
    try
    {
        // FileShare.ReadWrite: the host that handed us this path appends to it too, before and after this run.
        var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        outw = TextWriter.Synchronized(new TeeTextWriter(Console.Out, new StreamWriter(fs) { AutoFlush = true }));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"!! could not open --log {logPath} ({ex.GetType().Name}) — continuing on stdout only");
    }
}

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
    outw.WriteLine($">> --game {gameDir}: detected {layout.Loader}");
    outw.WriteLine($"   proxies:  {layout.InteropDir}");
    outw.WriteLine($"   game asm: {layout.GameAssemblyDll ?? "(absent)"}");
    outw.WriteLine($"   metadata: {layout.GlobalMetadataDat ?? "(absent)"}");
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

outw.WriteLine($">> patching proxies in {interopDir}");
DirectoryPatchResult r = InteropPatcher.PatchDirectory(interopDir, outw, detail: !summary);

outw.WriteLine($"\n== patched {r.Patched.Count} DLL(s), {r.TotalFlipped} member(s) flipped; " +
                  $"{r.Unchanged.Count} unchanged, {r.Unreadable.Count} non-.NET ==");

// An incomplete patch NEVER reports success. PatchDirectory withholds the schema marker when a module could not be
// written, so the tree is not at the schema and every consumer that trusts the marker would be right to say so — a
// zero exit here is the one thing that could make a caller record the opposite. Bail before the wire pass: stamping
// names onto a tree that is not natural-typed is work on a state that is about to be redone.
if (!r.Complete)
{
    Console.Error.WriteLine($"\n!! {r.Refused.Count} module(s) could not be written — the schema marker was WITHHELD and " +
                            "this tree is NOT patched. Close whatever holds them open, then re-run:");
    foreach ((string dll, string why) in r.Refused)
        Console.Error.WriteLine($"   REFUSED  {dll}: {why}");
    return 1;
}
// Every defer, from every module — including ones that flipped nothing (r.Patched holds only the modules that did),
// which is where a wholly-deferred module used to go silent. GAME modules are printed individually; FRAMEWORK ones
// (Il2Cppmscorlib, UnityEngine.*) are summarized: they defer in bulk by design, and 59 lines of them is how a single
// game defer gets missed. The full list is on DirectoryPatchResult.Defers for anything that wants it.
var gameDefers = r.Defers.Where(x => !CecilProjector.IsFrameworkAssembly(Path.GetFileNameWithoutExtension(x.Dll))).ToList();
if (summary)
{
    // Per-member, so it goes with the rest of the detail — this list alone is ~700 lines on a real game, which
    // is how a "what happened during that pause" report turns into something nobody reads. The COUNT stays.
    outw.WriteLine($"   defer  {r.Defers.Count} member(s) deferred ({gameDefers.Count} in game proxies) — run this " +
                   "tool at a terminal without --summary to list them");
}
else
{
    foreach (var (dll, d) in gameDefers)
        outw.WriteLine($"   defer  {dll}: {d}");
    if (r.Defers.Count > gameDefers.Count)
        outw.WriteLine($"   defer  ({r.Defers.Count - gameDefers.Count} more in framework proxies — deferred by design)");
}

// NB — the residual report is NOT printed here. PatchDirectory already wrote it to the log handed to it above, and
// printing it again is what made a reader count 68 hole lines against a summary of 34 and conclude the audit
// contradicted itself. There were always 34; they were emitted twice. One emitter, in the library.

// Disk-only step: re-attach the recovered wire names onto the proxies. The wiremap (produced offline by the
// metadata pillar) sits in the interop dir; INUTIL_WIREMAP overrides.
string wireMapPath = Environment.GetEnvironmentVariable("INUTIL_WIREMAP")
                     ?? Path.Combine(interopDir, "inutil.wiremap.json");
outw.WriteLine($"\n>> re-attaching wire names ({Path.GetFileName(wireMapPath)})");
WireStampResult wire = InteropPatcher.StampWireAttributesDirectory(interopDir, wireMapPath, outw);
// The summary line a caller greps carries BOTH numbers: stamping 0 onto an already-stamped tree is the idempotent
// no-op, not a failure, and only the second number separates it from "the recovered names never reached the proxies".
outw.WriteLine($"== stamped {wire.Stamped} wire attribute(s); {wire.AlreadyPresent} already present ==");
return 0;
