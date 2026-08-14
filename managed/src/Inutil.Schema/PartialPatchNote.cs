using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Inutil.Schema;

// The note a patch leaves BESIDE the proxies when it reached the tree but could not write every module — the
// third state between "no marker" (never patched) and a stamped marker (at the schema). It exists because a
// loader can BIND a proxy before any patcher gets to run, and a bound module is unwritable for the process
// lifetime: measured under BepInEx 6, Il2Cppmscorlib is loaded before the first patcher is constructed on EVERY
// boot, so an in-process pass reaches 139 of 140 assemblies and can never reach the last one.
//
// Two readers need that fact and neither can derive it:
//   * the loader shim's unpatched-proxies warning — with only a missing marker it must say "they were not
//     patched", which is false about the 139 that were, and reads like a broken install on a first boot;
//   * the patcher itself, which otherwise re-walks all 140 modules on EVERY launch (~4.3s, measured) to
//     rediscover that the one module it cannot write is still the one module it cannot write.
//
// So the note carries what a later run needs to decide: the SCHEMA it was at, whether that walk CONVERGED
// (flipped nothing, so there is nothing left for another walk to find), a FINGERPRINT of the proxy set, and
// which modules were refused. Skipping is only ever safe when all four agree — same schema, converged, the
// proxies are byte-for-byte the ones the note describes, and every refused module is still bound. Any change to
// the proxies (a game update regenerates them) moves the fingerprint and the next run does the full walk.
//
// It is deleted by the run that finally completes, so it can never outlive the condition it describes.
public sealed record PartialPatchNote(
    string Schema,
    int Patched,
    bool Converged,
    string Fingerprint,
    IReadOnlyList<(string Dll, string Why)> Refused)
{
    // The identity of a proxy SET, cheap enough to compute on every boot: how many, how big, how recent. Not a
    // content hash — hashing ~140 DLLs (hundreds of MB) every launch would cost more than the walk it saves.
    // It has to catch a REGENERATION, not a forgery: Il2CppInterop rewrites every file, so count, total size and
    // newest write time all move together, and any one of them moving is enough to fall back to the full walk.
    public static string Compute(string interopDir)
    {
        long count = 0, bytes = 0, newest = 0;
        foreach (string p in Directory.EnumerateFiles(interopDir, "*.dll"))
        {
            var fi = new FileInfo(p);
            count++;
            bytes += fi.Length;
            long t = fi.LastWriteTimeUtc.Ticks;
            if (t > newest) newest = t;
        }
        return $"{count}/{bytes}/{newest}";
    }

    static string Path_(string interopDir) => Path.Combine(interopDir, SchemaMarker.InteropPartialFileName);

    // Written as lines a human can read without a tool, because the person who meets this file is reading it to
    // find out why their proxies are not fully patched. The parser below reads only the keyed lines; the prose
    // after them is for the reader and is ignored.
    public void Write(string interopDir)
    {
        var lines = new List<string>
        {
            $"schema {Schema}",
            $"patched {Patched}",
            $"converged {(Converged ? "yes" : "no")}",
            $"fingerprint {Fingerprint}",
        };
        foreach ((string dll, string why) in Refused) lines.Add($"refused {dll}: {why}");
        lines.Add("");
        lines.Add("These modules were patched in memory but could not be written to disk. A module the LOADER has");
        lines.Add("already bound cannot be replaced by the process it is bound in — patch it out-of-process with");
        lines.Add("`inutil-interoppatch <interopDir>` while the game is closed, and this note goes away.");
        File.WriteAllText(Path_(interopDir), string.Join("\n", lines) + "\n");
    }

    public static void Remove(string interopDir)
    {
        try { string p = Path_(interopDir); if (File.Exists(p)) File.Delete(p); } catch { }
    }

    // Null when there is no note, or when it cannot be read as one. A malformed note is treated as ABSENT rather
    // than as a reason to fail: its only power is to let a caller SKIP work, so being unreadable must cost work,
    // never correctness.
    public static PartialPatchNote? Read(string interopDir)
    {
        string p = Path_(interopDir);
        string[] lines;
        try { if (!File.Exists(p)) return null; lines = File.ReadAllLines(p); } catch { return null; }

        string schema = "", fingerprint = "";
        int patched = 0;
        bool converged = false;
        var refused = new List<(string, string)>();
        foreach (string line in lines)
        {
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            string key = line.Substring(0, sp), val = line.Substring(sp + 1).Trim();
            switch (key)
            {
                case "schema": schema = val; break;
                case "fingerprint": fingerprint = val; break;
                case "converged": converged = val == "yes"; break;
                case "patched": int.TryParse(val, out patched); break;
                case "refused":
                    int colon = val.IndexOf(':');
                    if (colon > 0) refused.Add((val.Substring(0, colon).Trim(), val.Substring(colon + 1).Trim()));
                    else refused.Add((val, ""));
                    break;
            }
        }
        if (schema.Length == 0 || refused.Count == 0) return null;
        return new PartialPatchNote(schema, patched, converged, fingerprint, refused);
    }

    // THE skip question, asked in one place so no caller re-derives it (and gets it subtly wrong): is another
    // in-process walk of this directory capable of achieving anything? Only when every part agrees — this note
    // describes THIS schema, over THESE proxies, a walk that already found nothing left to flip, and every module
    // it could not write is STILL unwritable for the same reason. `isBound` is the caller's, because "is this
    // assembly loaded" is a runtime question the schema tier has no business answering.
    public bool NothingLeftToDo(string interopDir, string currentSchema, Func<string, bool> isBound) =>
        Converged
        && Schema == currentSchema
        && Fingerprint == Compute(interopDir)
        && Refused.All(x => isBound(Path.GetFileNameWithoutExtension(x.Dll)));
}
