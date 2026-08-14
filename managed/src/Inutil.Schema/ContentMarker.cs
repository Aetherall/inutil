using System;
using System.IO;

namespace Inutil.Schema;

// The verdict a marker read yields. Current = present and its content-address equals what this build expects;
// Stale = present but different (produced by a DIFFERENT schema); Missing = absent. Only Current is silent —
// the other two are fail-loud.
public enum MarkerVerdict { Current, Missing, Stale }

// The generic content-address marker mechanism (docs/contribution/architecture/16-metadata.md, Generalization D). A marker is a tiny
// sidecar file carrying a content-address (hash) of the artifact beside it, so "is this artifact current for this
// build?" is a STRUCTURAL equality check. The ONE home for the marker file FORMAT: both the writer (offline
// patcher/extractor) and the reader (runtime loader shim) go through here, so the format can never drift between
// a stamp and a read.
//
// It hashes NOTHING itself — the caller supplies the content-address (SchemaMarker.Hash for the natural-typing
// registry, a wire address for the sidecar); ContentMarker only writes/reads/compares it on disk, atomically and
// idempotently.
public static class ContentMarker
{
    const string HashPrefix = "hash: ";

    // Stamp `hash` into <dir>/<fileName>. Atomic (temp + rename in the same dir — a mid-write failure never leaves
    // a torn marker) and IDEMPOTENT (a byte-identical marker is not rewritten). `note` is a human preamble NOT part
    // of the content-address — only the `hash:` line is. Returns true if written (changed), false if identical.
    public static bool Stamp(string dir, string fileName, string hash, string? note = null)
    {
        Directory.CreateDirectory(dir);
        string content = Compose(hash, note);
        string path = Path.Combine(dir, fileName);
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;

        string tmp = path + ".inutil-tmp";
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch { /* best effort — a failed write left it */ }
        }
        return true;
    }

    // The stamped content-address, or null if absent / no readable `hash:` line. A plain on-disk text read — NO
    // assembly load — so the loader shim can call it before the loader lazily binds the proxies.
    public static string? ReadHash(string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return null;
        try
        {
            foreach (string line in File.ReadAllLines(path))
                if (line.StartsWith(HashPrefix, StringComparison.Ordinal))
                {
                    string h = line.Substring(HashPrefix.Length).Trim();
                    return h.Length > 0 ? h : null;
                }
        }
        catch { /* unreadable marker — treat as absent, i.e. Missing */ }
        return null;
    }

    // The verdict — the single comparison both the fail-loud loader warning and the in-game battery gate read.
    public static MarkerVerdict Verify(string dir, string fileName, string expectedHash)
    {
        string? stamped = ReadHash(dir, fileName);
        if (stamped is null) return MarkerVerdict.Missing;
        return string.Equals(stamped, expectedHash, StringComparison.Ordinal) ? MarkerVerdict.Current : MarkerVerdict.Stale;
    }

    static string Compose(string hash, string? note)
    {
        string preamble = string.IsNullOrEmpty(note) ? "" : PrefixLines(note!) + "\n";
        return
            "# inutil content marker — DO NOT EDIT. A content-address of the artifact beside this file, so a\n" +
            "# consumer can tell current from stale/absent as a structural check (§7.2 / docs/contribution/architecture/16-metadata.md).\n" +
            preamble +
            HashPrefix + hash + "\n";
    }

    static string PrefixLines(string note)
    {
        string[] lines = note.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++) lines[i] = "# " + lines[i];
        return string.Join("\n", lines);
    }
}
