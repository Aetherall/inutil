// ModLibs — the SHARED-SOURCE-LIBRARY lifecycle (§7.9): the mod code-sharing primitive. Compile + emit + load
// every <libsDir>/<name>/*.cs into a STABLE, named assembly BEFORE coremods and hot-reload mods, so any can
// reference it by name. Unlike a coremod (loads into its OWN collectible ALC, never referenced), a LIBRARY must:
//   1. be EMITTED TO DISK (<libsDir>/<name>.dll) so the in-process compiler (which scans ref DIRS, not source)
//      AND an offline IDE can reference it; and
//   2. be loaded into the HOST context (the one ModContext defers to) so a consumer mod — compiled against that
//      on-disk DLL — binds to the SAME loaded instance (one type identity, not a private copy).
// The emitted DLL sits at the libsDir ROOT (a flat *.dll scan finds it); libsDir is on the ref-dir list so later
// libs/coremods/mods see it. Rebuilt only when a source is newer than the DLL. Its own lifecycle, its own type.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace Inutil;

public static class ModLibs
{
    static readonly List<Assembly> _rooted = new();   // roots the loaded libraries for the session (permanent)

    // Compile (if stale) + load every <libsDir>/<name>/*.cs. Returns the count loaded. Call once, before
    // Coremods.LoadSync + CsModHost.Start, so later mods can reference the libraries by name.
    public static int LoadSync(string libsDir, IReadOnlyList<string> refDirs, Action<string> log)
    {
        if (!Directory.Exists(libsDir)) return 0;
        string[] dirs;
        try { dirs = Directory.GetDirectories(libsDir); } catch { return 0; }
        // The context that loaded THIS assembly (Inutil.Mods.dll) is the host shim's context — the same one
        // ModContext defers a mod's references to. Loading a library here makes it the consumer's copy.
        var host = AssemblyLoadContext.GetLoadContext(typeof(ModLibs).Assembly) ?? AssemblyLoadContext.Default;
        int loaded = 0;
        foreach (string d in dirs.OrderBy(x => x, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(d);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            List<string> cs;
            try { cs = Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToList(); }
            catch { continue; }
            if (cs.Count == 0) continue;

            string dll = Path.Combine(libsDir, name + ".dll");
            string pdb = Path.Combine(libsDir, name + ".pdb");
            try
            {
                if (IsStale(dll, cs))
                {
                    var sw = Stopwatch.StartNew();
                    CsModCompiler.Result r;
                    try { r = CsModCompiler.Compile(name, cs, refDirs); }
                    catch (Exception ex) { log($"[libs] {name}: compiler threw {ex.GetType().Name}: {ex.Message}"); continue; }
                    if (!r.Ok)
                    {
                        log($"[libs] {name}: {r.Errors.Length} compile error(s) (refs={r.RefCount}) — NOT built:");
                        foreach (string e in r.Errors.Take(12)) log($"[libs]   {e}");
                        continue;
                    }
                    try
                    {
                        File.WriteAllBytes(dll, r.Pe!);
                        if (r.Pdb is { Length: > 0 }) File.WriteAllBytes(pdb, r.Pdb);
                        log($"[libs] {name}: compiled ({sw.ElapsedMilliseconds}ms) -> {Path.GetFileName(dll)} (refs={r.RefCount})");
                    }
                    catch (Exception ex)
                    {
                        // Unwritable (e.g. a mid-session re-run holds the lock) — fall through to load whatever is on disk.
                        log($"[libs] {name}: emit failed ({ex.GetType().Name}: {ex.Message}); loading the existing {Path.GetFileName(dll)}");
                    }
                }
                if (!File.Exists(dll)) { log($"[libs] {name}: no {Path.GetFileName(dll)} to load"); continue; }
                _rooted.Add(host.LoadFromAssemblyPath(dll));   // into the host context -> consumer mods bind to THIS copy
                loaded++;
                log($"[libs] {name}: loaded (shared with coremods + mods)");
            }
            catch (Exception ex) { log($"[libs] {name}: load failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        if (loaded > 0) log($"[libs] {loaded} library(ies) loaded — referenceable by coremods + mods");
        return loaded;
    }

    // A library DLL is stale if it's absent or any of its sources is newer than it. A fresh boot process holds
    // no lock on last session's emit, so a stale overwrite is always safe here.
    static bool IsStale(string dll, IReadOnlyList<string> sources)
    {
        try
        {
            if (!File.Exists(dll)) return true;
            DateTime built = File.GetLastWriteTimeUtc(dll);
            foreach (string s in sources)
                if (File.GetLastWriteTimeUtc(s) > built) return true;
            return false;
        }
        catch { return true; }
    }
}
