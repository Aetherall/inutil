// Coremods — the LOAD-ONCE, PERMANENT mod lifecycle (§7.9, one of the three named ones). Compile + load + Discover
// every <coreDir>/<name>/*.cs SYNCHRONOUSLY on the calling (main / chainload) thread, so a boot-time fix's detour
// is live BEFORE the menu — a fix that must patch during menu load can't wait for the polled hot-reload loop. A
// coremod does NOT hot-reload: it loads once into a PERMANENT (rooted) collectible ALC and stays for the session.
// Same CsModCompiler + Mods.Discover as CsModHost; only the timing (synchronous, pre-menu) and lifetime (never
// torn down) differ — its own named type with its own contract, not a path inside the hot-reload host.
using System.Diagnostics;
using System.Reflection;
using Inutil.Host;   // ModContext

namespace Inutil;

public static class Coremods
{
    static readonly List<ModContext> _rooted = new();   // roots the coremod ALCs so they never collect (permanent)

    // Compile + load + Discover every <coreDir>/<name>/*.cs synchronously. Returns the total hook/lifecycle count
    // wired. Call once from the host shim's Load() (main thread), before the hot-reload loop.
    public static int LoadSync(string coreDir, IReadOnlyList<string> refDirs, Action<string> log)
    {
        if (!Directory.Exists(coreDir)) return 0;
        string[] dirs;
        try { dirs = Directory.GetDirectories(coreDir); } catch { return 0; }
        int total = 0, loaded = 0;
        foreach (string d in dirs.OrderBy(x => x, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(d);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            List<string> cs;
            try { cs = Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToList(); }
            catch { continue; }
            if (cs.Count == 0) continue;

            var sw = Stopwatch.StartNew();
            CsModCompiler.Result r;
            try { r = CsModCompiler.Compile("inutilcore_" + Sanitize(name), cs, refDirs); }
            catch (Exception ex) { log($"[coremods] {name}: compiler threw {ex.GetType().Name}: {ex.Message}"); continue; }
            if (!r.Ok)
            {
                log($"[coremods] {name}: {r.Errors.Length} compile error(s) (refs={r.RefCount}) — NOT loaded:");
                foreach (string e in r.Errors.Take(12)) log($"[coremods]   {e}");
                continue;
            }
            try
            {
                var alc = new ModContext("inutilcore:" + name);
                Assembly asm = r.Pdb is { Length: > 0 }
                    ? alc.LoadFromStream(new MemoryStream(r.Pe!), new MemoryStream(r.Pdb))
                    : alc.LoadFromStream(new MemoryStream(r.Pe!));
                _rooted.Add(alc);                         // permanent root — a coremod never unloads
                int wired = Mods.Discover(asm, null, name);   // name -> the coremod's config filename

                total += wired; loaded++;
                log($"[coremods] {name}: compiled ({sw.ElapsedMilliseconds}ms) + loaded pre-menu — {wired} hook/lifecycle wired");
            }
            catch (Exception ex) { log($"[coremods] {name}: load failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        if (loaded > 0) log($"[coremods] {loaded} coremod(s) loaded pre-menu, {total} hook/lifecycle wired");
        return total;
    }

    static string Sanitize(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++) if (!char.IsLetterOrDigit(a[i])) a[i] = '_';
        return new string(a);
    }
}
