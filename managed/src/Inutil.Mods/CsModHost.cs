// CsModHost — inutil's no-build .cs mod loop (§7.9), the HOT-RELOAD lifecycle (one of the three named ones;
// Coremods = load-once permanent, ModLibs = shared source, live in their own files).
//
// Watches <modsRoot>/<name>/*.cs; on boot and whenever a mod's sources change it compiles them in-process
// (CsModCompiler, on a worker thread — pure CPU), then hot-reloads through the collectible-ALC primitives:
// drop the old generation (Mods.RemoveAll + Hooks.RemoveAll, firing OnUnload), Unload its ALC so it collects,
// load the freshly-compiled bytes into a NEW ModContext, and Mods.Discover its Hook<T>/lifecycle. Every
// il2cpp-touching step (load / Discover / teardown) is marshalled onto the MAIN thread via MainThread.Post;
// the Roslyn compile stays off it.
//
// Change detection is by POLLING (a signature over each mod's .cs name/size/mtime), NOT FileSystemWatcher:
// under Wine/Proton a Windows-.NET FileSystemWatcher does not reliably see host-side writes through the
// pressure-vessel bind mount, so a polled signature is the portable choice.
//
// Two structural races it guards (§7.9):
//  (1) GENERATION CURRENCY — a slow compile of an OLD edit could finish AFTER a newer edit's already-applied
//      result and overwrite it ("stale write wins"). ModSlot carries a generation counter; a completed compile
//      applies only if it is still the slot's latest requested generation, else it is discarded.
//  (2) ATOMIC LOAD — a load/Discover that throws must not leak its collectible ALC. Discover is already atomic
//      (Mods.Discover), and LoadOnMain unwinds the ALC it created (RemoveAll + Unload) on ANY failure, so there
//      is no half-loaded generation.
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using Inutil.Host;   // ModContext, MainThread

namespace Inutil;

public sealed class CsModHost
{
    readonly string _modsRoot;
    readonly IReadOnlyList<string> _refDirs;
    readonly Action<string> _log;
    readonly object _gate = new();
    readonly Dictionary<string, ModSlot> _mods = new(StringComparer.OrdinalIgnoreCase);
    Timer? _poll;

    // Per-mod state. Alc is the CURRENTLY-loaded generation (null between unload and the next load) — the ONLY
    // host-side reference to a mod ALC, so nulling it on teardown lets the context collect. Sig is the last
    // source signature the poll saw; Debounce coalesces a burst of edits into one reload; Gen is the monotonic
    // request counter that fixes the stale-write-wins race.
    sealed class ModSlot { public string Name = ""; public ModContext? Alc; public string Sig = ""; public Timer? Debounce; public int Gen; }

    CsModHost(string modsRoot, IReadOnlyList<string> refDirs, Action<string> log)
    { _modsRoot = modsRoot; _refDirs = refDirs; _log = log; }

    // Start the loop: ensure the dir exists, compile+load every mod subdir present at boot, then poll for
    // changes. Call once from the host shim (after the engine is attached and the main-thread pump is up).
    public static CsModHost Start(string modsRoot, IReadOnlyList<string> refDirs, Action<string> log)
    {
        var h = new CsModHost(modsRoot, refDirs, log);
        try { Directory.CreateDirectory(modsRoot); } catch { }
        int n = h.Scan();
        log(n == 0
            ? $"[mods] watching {modsRoot} (no mods yet — drop a <name>/*.cs to go live)"
            : $"[mods] watching {modsRoot} — {n} mod(s) compiling…");
        h._poll = new Timer(_ => { try { h.Scan(); } catch { } }, null, 1000, 1000);
        return h;
    }

    // Stop polling (the loaded mods stay live until their host tears down / the process exits).
    public void StopWatching() { _poll?.Dispose(); _poll = null; }

    // Scan the mods root: queue a reload for every mod whose .cs signature changed (new mods included), and for
    // any mod whose dir vanished (-> empty sources -> teardown). Returns the number of mods queued this scan.
    int Scan()
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(_modsRoot); } catch { return 0; }
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int queued = 0;
        foreach (string d in dirs.OrderBy(x => x, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(d);
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            present.Add(name);
            string sig = SigOf(d);
            lock (_gate)
            {
                if (!_mods.TryGetValue(name, out var slot)) { slot = new ModSlot { Name = name }; _mods[name] = slot; }
                if (slot.Sig == sig) continue;     // unchanged
                slot.Sig = sig;
            }
            QueueReload(name); queued++;
        }
        lock (_gate)
        {
            foreach (var slot in _mods.Values)
                if (!present.Contains(slot.Name) && slot.Sig.Length != 0) { slot.Sig = ""; QueueReload(slot.Name); queued++; }
        }
        return queued;
    }

    // The source signature of a mod dir: each .cs's relpath + size + mtime, order-independent. Changes iff a
    // file is added/removed/edited — the cheap poll key (no content hash).
    static string SigOf(string dir)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (string f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                var fi = new FileInfo(f);
                sb.Append(Path.GetFileName(f)).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append('|');
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    // Debounce: a save can land as several writes; coalesce into ONE reload ~250ms after the last.
    void QueueReload(string name)
    {
        lock (_gate)
        {
            if (!_mods.TryGetValue(name, out var slot)) { slot = new ModSlot { Name = name }; _mods[name] = slot; }
            slot.Debounce?.Dispose();
            slot.Debounce = new Timer(_ => ReloadOnBg(name), null, 250, Timeout.Infinite);
        }
    }

    // Worker thread (the debounce timer fires on the pool): assign this reload its generation, COMPILE here
    // (pure CPU, off the game thread), then post the il2cpp-touching load/teardown to the main thread. A compile
    // error keeps the running generation.
    void ReloadOnBg(string name)
    {
        int gen;
        lock (_gate)
        {
            if (!_mods.TryGetValue(name, out var slot)) { slot = new ModSlot { Name = name }; _mods[name] = slot; }
            gen = ++slot.Gen;                     // this request's generation — the currency token
        }

        string dir = Path.Combine(_modsRoot, name);
        List<string> cs;
        try { cs = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToList() : new List<string>(); }
        catch { return; }

        if (cs.Count == 0) { MainThread.Post(() => TeardownIfCurrent(name, gen, announce: true)); return; }   // emptied -> unload

        var sw = Stopwatch.StartNew();
        CsModCompiler.Result r;
        try { r = CsModCompiler.Compile("inutilmod_" + Sanitize(name), cs, _refDirs); }
        catch (Exception ex)
        {
            _log($"[mods] {name}: compiler threw {ex.GetType().Name}: {ex.Message}");
            Diagnostics.RecordFailure(name, "compiler-threw", $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!r.Ok)
        {
            _log($"[mods] {name}: {r.Errors.Length} compile error(s) (refs={r.RefCount}) — keeping previous generation:");
            foreach (string e in r.Errors.Take(12)) _log($"[mods]   {e}");
            // The human-facing signal (the renderer shows this): the count + the FIRST diagnostic — enough to
            // recognize "my edit broke" and where, without the full list a toast can't hold.
            Diagnostics.RecordFailure(name, "compile-error",
                r.Errors.Length == 1 ? r.Errors[0] : $"{r.Errors.Length} errors — first: {r.Errors[0]}");
            return;
        }
        foreach (string w in r.Warnings.Take(4)) _log($"[mods] {name}: warn {w}");
        byte[] pe = r.Pe!, pdb = r.Pdb ?? Array.Empty<byte>();
        long ms = sw.ElapsedMilliseconds;
        MainThread.Post(() => LoadOnMain(name, gen, pe, pdb, ms));
    }

    // Main thread: apply a completed compile IF it is still the slot's latest generation (fix 1 — a stale
    // compile of an older edit that finished late is DISCARDED, never overwriting a newer applied one). Then
    // tear down the previous generation, load the new bytes into a fresh collectible ALC, and Discover — with
    // atomic unwind (fix 2 — a load/Discover failure Unloads the ALC it created rather than leaking it).
    void LoadOnMain(string name, int gen, byte[] pe, byte[] pdb, long compileMs)
    {
        lock (_gate)
        {
            if (!_mods.TryGetValue(name, out var slot) || slot.Gen != gen)
            { _log($"[mods] {name}: gen {gen} superseded (current {(_mods.TryGetValue(name, out var s) ? s.Gen : -1)}) — discarding stale compile"); return; }
        }

        Teardown(name, announce: false);
        ModContext? alc = null;
        try
        {
            alc = new ModContext("inutilmod:" + name);
            Assembly asm = pdb.Length > 0
                ? alc.LoadFromStream(new MemoryStream(pe), new MemoryStream(pdb))
                : alc.LoadFromStream(new MemoryStream(pe));
            int wired = Mods.Discover(asm, null, name);     // atomic: either fully wires or unwinds its own hooks (name -> the mod's config filename)
            lock (_gate) { if (_mods.TryGetValue(name, out var slot)) slot.Alc = alc; }
            _log($"[mods] {name}: compiled ({compileMs}ms) + loaded — {wired} hook/lifecycle wired (gen {gen})");
            Diagnostics.RecordSuccess(name);   // clears any prior failure banner for this mod (the edit took)
        }
        catch (Exception ex)
        {
            // ATOMIC (fix 2): the freshly-created ALC must not outlive a failed load. Drop anything Discover may
            // have wired before throwing, then Unload — no orphaned collectible context, no half-loaded slot.
            if (alc is not null)
            {
                try { Mods.RemoveAll(alc); } catch { }
                try { Inutil.Hooks.Hooks.RemoveAll(alc); } catch { }
                try { alc.Unload(); } catch { }
            }
            _log($"[mods] {name}: load failed ({ex.GetType().Name}: {ex.Message}) — ALC unwound, previous generation stays");
            Diagnostics.RecordFailure(name, "load-failed", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Gen-guarded empty-source teardown: only unload if `gen` is still current (a re-add that landed after the
    // dir emptied advances the gen and supersedes this teardown).
    void TeardownIfCurrent(string name, int gen, bool announce)
    {
        lock (_gate) { if (!_mods.TryGetValue(name, out var slot) || slot.Gen != gen) return; }
        Teardown(name, announce);
    }

    // Main thread: drop this mod's Hook<T> subscriptions + lifecycle (fires OnUnload) + any raw engine hooks it
    // registered directly, then Unload its ALC. The detours stay installed (they point at the host's stable
    // Dispatch); only the per-mod hook arrays shrink — and with no host root left on the ALC, it collects.
    void Teardown(string name, bool announce)
    {
        ModContext? alc;
        lock (_gate)
        {
            if (!_mods.TryGetValue(name, out var slot) || slot.Alc is null)
            { if (announce) _log($"[mods] {name}: removed (nothing loaded)"); return; }
            alc = slot.Alc; slot.Alc = null;
        }
        try
        {
            int facade = Mods.RemoveAll(alc);                 // Hook<T> subs + ILoad/IUnload/ITick/IGui (fires OnUnload)
            int engine = Inutil.Hooks.Hooks.RemoveAll(alc);   // raw Hooks.Pre/PreNative the mod registered directly
            alc.Unload();
            if (announce) _log($"[mods] {name}: unloaded ({facade} sub(s) / {engine} raw hook(s) dropped)");
        }
        catch (Exception ex) { _log($"[mods] {name}: teardown threw {ex.GetType().Name}: {ex.Message}"); }
    }

    static string Sanitize(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++) if (!char.IsLetterOrDigit(a[i])) a[i] = '_';
        return new string(a);
    }
}
