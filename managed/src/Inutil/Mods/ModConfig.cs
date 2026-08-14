// Mod configuration — the `config.file` capability.
//
// One flat, typed key->value bag PER MOD (all of a mod's IConfigure implementers share it), backed by
// <configRoot>/<modName>.cfg in the BepInEx cfg DIALECT — `[section]`, `## description`, `# Setting type:` /
// `# Default value:` comment metadata, `key = value` — chosen deliberately: it is what BepInEx itself writes,
// every BepInEx user can read it, and the launcher's STRUCTURED config editor understands it (a .json would get
// the raw editor only). The parser/writer is inutil's own (~100 lines, below), NOT BepInEx.Configuration: the
// mod-facing contract must be loader-agnostic (MelonLoader hosts the same seam), and hot-reload mods are not
// chainloader plugins.
//
// PULL model: `Configure(ModConfig cfg)` reads keys with code-side defaults (and optional descriptions), and the
// store MATERIALIZES the file from what was read — so the file a user opens is self-documented, created on first
// load, healed if deleted. Configure fires after instantiation and BEFORE OnLoad, and RE-FIRES (on the main
// thread) whenever the file changes on disk — live re-apply with NO ALC churn (an edit never recompiles or
// reloads the mod). A mod's one obligation is that Configure be re-appliable (read keys, assign fields).
//
// Unknown keys in the file are PRESERVED across materializations (a newer generation's keys are never destroyed
// by an older one still running), and the store tracks its own writes so a materialization never re-triggers the
// watcher. Teardown is ALC-scoped like everything else (Mods.RemoveAll).
using System.Globalization;
using System.Runtime.Loader;
using System.Text;

namespace Inutil;

// The config lifecycle seam: implement on any mod type. Configure fires BEFORE OnLoad (config is ready when
// the mod wires) and again on every on-disk edit of the mod's cfg file. Write it re-appliable.
public interface IConfigure { void Configure(ModConfig cfg); }

// The typed, flat bag one mod's IConfigure implementers share. Getters record (key, default, type,
// description) so the store can materialize a self-documented file; reads fall back to the code-side
// default when the key is absent or unparsable (unparsable warns once — the file value is left for the
// user to fix, never clobbered).
public sealed class ModConfig
{
    internal sealed record Entry(string Key, string Default, string TypeName, string? Description);

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    public string Name { get; }
    internal string? Path { get; }          // null = no config root configured (memory-only defaults)

    internal ModConfig(string name, string? path) { Name = name; Path = path; }

    public string GetString(string key, string def, string? description = null)
    {
        Record(key, def, "String", description);
        return _values.TryGetValue(key, out string? v) ? v : def;
    }

    public bool GetBool(string key, bool def, string? description = null)
    {
        Record(key, def ? "true" : "false", "Boolean", description);
        if (_values.TryGetValue(key, out string? v))
        {
            if (bool.TryParse(v.Trim(), out bool b)) return b;
            WarnOnce(key, v, "Boolean");
        }
        return def;
    }

    public int GetInt(string key, int def, string? description = null)
    {
        Record(key, def.ToString(CultureInfo.InvariantCulture), "Int32", description);
        if (_values.TryGetValue(key, out string? v))
        {
            if (int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
            WarnOnce(key, v, "Int32");
        }
        return def;
    }

    public float GetFloat(string key, float def, string? description = null)
    {
        Record(key, def.ToString(CultureInfo.InvariantCulture), "Single", description);
        if (_values.TryGetValue(key, out string? v))
        {
            if (float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return f;
            WarnOnce(key, v, "Single");
        }
        return def;
    }

    private void Record(string key, string def, string type, string? description)
    {
        // Last registration wins so a re-fired Configure refreshes metadata; keys are single-line by contract.
        _entries[key] = new Entry(key, def, type, description);
    }

    private void WarnOnce(string key, string value, string type)
    {
        if (!_warned.Add(key)) return;
        Hooks.Hooks.OnWarning?.Invoke(
            $"inutil config: '{Name}.cfg' key '{key}' = '{value}' is not a valid {type} — using the code default " +
            "(the file value was left for you to fix).");
    }

    // ── the cfg dialect (parse + materialize) ───────────────────────────────────────────────────────────

    // Parse the on-disk file into the value bag. Dialect: blank + `#`/`##` comment lines ignored, `[section]`
    // lines ignored (keys are flat), `key = value` split on the FIRST '=', both sides trimmed. Values are
    // single-line raw strings (no quoting/escaping — the corpus needs none).
    internal void Reload()
    {
        _values.Clear();
        if (Path is null || !File.Exists(Path)) return;
        foreach (string raw in File.ReadAllLines(Path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            _values[line[..eq].TrimEnd()] = line[(eq + 1)..].TrimStart();
        }
    }

    // Write the self-documented file: every entry Configure read this round (description, type, default, and
    // CURRENT value — the file's if present, else the default), then any file keys the current mod build did not
    // read, preserved verbatim. Returns true when the content actually changed (the store uses this to update its
    // own-write mtime tracking; an identical file is not rewritten).
    internal bool Materialize()
    {
        if (Path is null) return false;
        var sb = new StringBuilder();
        sb.Append("## Settings for the '").Append(Name).Append("' mod — created by inutil.\n");
        sb.Append("## Edits apply LIVE: every save re-fires the mod's Configure on the next frame (no restart, no reload).\n\n");
        sb.Append('[').Append(Name).Append("]\n");
        foreach (Entry e in _entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            sb.Append('\n');
            if (!string.IsNullOrEmpty(e.Description))
                foreach (string dl in e.Description!.Split('\n'))
                    sb.Append("## ").Append(dl.TrimEnd()).Append('\n');
            sb.Append("# Setting type: ").Append(e.TypeName).Append('\n');
            sb.Append("# Default value: ").Append(e.Default).Append('\n');
            sb.Append(e.Key).Append(" = ").Append(_values.TryGetValue(e.Key, out string? v) ? v : e.Default).Append('\n');
        }
        var unknown = _values.Keys.Where(k => !_entries.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            sb.Append("\n## Keys below are not read by the currently loaded build of this mod — preserved, not deleted\n");
            sb.Append("## (another generation or a future build may read them).\n");
            foreach (string k in unknown) sb.Append(k).Append(" = ").Append(_values[k]).Append('\n');
        }
        string content = sb.ToString();
        try { if (File.Exists(Path) && File.ReadAllText(Path) == content) return false; } catch { }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, content);
        return true;
    }
}

// The host-wide store: one watched entry per (mod, ALC generation). The loader shim sets Root once at
// attach; Discover registers a mod's IConfigure instances (firing the first Configure), FrameDriver drives
// the mtime poll on the main thread, and Mods.RemoveAll retires a generation's entries with the rest of it.
public static class ModConfigStore
{
    // Where the cfg files live — loader-specific, injected by the shim (BepInEx: BepInEx/config; MelonLoader:
    // UserData/inutil-config). Null = no shim set it: Configure still fires with code defaults (memory-only)
    // and a one-time warning, so a bare/embedded host degrades soft.
    public static string? Root;

    // Poll cadence for the on-disk watcher (ms). Public so tests (and unusual hosts) can tighten it; the
    // default is human-edit-speed — config edits are not a hot path.
    public static int PollIntervalMs = 1000;

    private sealed class Watched
    {
        public ModConfig Cfg = null!;
        public List<IConfigure> Instances = new();
        public AssemblyLoadContext? Owner;
        public DateTime LastWriteUtc;
    }

    private static readonly List<Watched> _watched = new();
    private static readonly object _gate = new();
    private static long _nextPollAt;
    private static bool _warnedNoRoot;

    // Register a mod's IConfigure implementers and fire their FIRST Configure (called by Mods.Discover after
    // instantiation, BEFORE Mods.Add fires OnLoad — config is ready when the mod wires). Materializes the
    // self-documented file (creating BepInEx/config/<name>.cfg on a first run) and arms the watcher.
    internal static void Register(string modName, AssemblyLoadContext? owner, List<IConfigure> instances)
    {
        if (instances.Count == 0) return;
        string? path = Root is null ? null : System.IO.Path.Combine(Root, modName + ".cfg");
        if (path is null && !_warnedNoRoot)
        {
            _warnedNoRoot = true;
            Hooks.Hooks.OnWarning?.Invoke(
                "inutil config: no config root was set by the loader shim (ModConfigStore.Root) — IConfigure mods " +
                "run on code defaults only; nothing is persisted.");
        }
        var w = new Watched { Cfg = new ModConfig(modName, path), Instances = new(instances), Owner = owner };
        w.Cfg.Reload();
        foreach (IConfigure c in w.Instances) SafeConfigure(c, w.Cfg);
        w.Cfg.Materialize();
        w.LastWriteUtc = Mtime(path);
        lock (_gate) _watched.Add(w);
    }

    // The on-disk watcher, driven per frame by FrameDriver ON the main thread (so re-fired Configures may touch
    // il2cpp state), time-gated to PollIntervalMs. A deleted file is re-materialized (self-healing); a changed
    // file reloads the bag, re-fires every Configure, then re-materializes — the store's own writes update
    // LastWriteUtc so they never re-trigger the poll.
    public static void Tick()
    {
        long now = Environment.TickCount64;
        if (PollIntervalMs > 0 && now < _nextPollAt) return;   // <=0 = poll every call (tests / unusual hosts)
        _nextPollAt = now + Math.Max(0, PollIntervalMs);

        Watched[] snapshot;
        lock (_gate) snapshot = _watched.ToArray();
        foreach (Watched w in snapshot)
        {
            if (w.Cfg.Path is null) continue;
            DateTime m = Mtime(w.Cfg.Path);
            if (m == w.LastWriteUtc) continue;
            w.Cfg.Reload();
            foreach (IConfigure c in w.Instances) SafeConfigure(c, w.Cfg);
            w.Cfg.Materialize();
            w.LastWriteUtc = Mtime(w.Cfg.Path);
        }
    }

    // Retire every entry owned by `owner` — part of Mods.RemoveAll's one-call teardown, so an unloaded
    // generation's Configure can never re-fire into a dead ALC. Returns the count dropped.
    internal static int RemoveAll(AssemblyLoadContext owner)
    {
        lock (_gate) return _watched.RemoveAll(w => w.Owner == owner);
    }

    private static DateTime Mtime(string? path)
    {
        try { return path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private static void SafeConfigure(IConfigure c, ModConfig cfg)
    {
        try { c.Configure(cfg); }
        catch (Exception ex)
        {
            Hooks.Hooks.OnWarning?.Invoke($"inutil config: {c.GetType().Name}.Configure threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
