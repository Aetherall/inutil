using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Inutil.Schema;

// One row: the il2cpp class identity a generated proxy type stands for.
//
// The identity is the triple the GENERATOR itself baked into that proxy's static ctor —
// `IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", "ToyGame", "Player")` — not a naming rule reconstructed from the
// proxy's CLR name. That matters: Il2CppInterop mangles namespaces (System -> Il2CppSystem), assemblies
// (mscorlib -> Il2Cppmscorlib) and member names (`<>c` -> `__c`) by rules that are its business and change with its
// version. Reading the literal it emitted is exact by construction and needs no rule at all.
//
// `Name` is a CHAIN for a nested type ("Kernel32/WIN32_FIND_DATA"), which is how the runtime side spells it too:
// il2cpp names a nested class by its simple name and hangs the parent off il2cpp_class_get_declaring_type, so
// walking that chain and joining reproduces this key exactly.
public sealed class ExactTypeRow
{
    public string Image { get; }             // "Assembly-CSharp.dll" — the il2cpp image name
    public string Namespace { get; }         // "ToyGame" (the ORIGINAL namespace, "" for the global one)
    public string Name { get; }              // "Player", or "Kernel32/WIN32_FIND_DATA" for a nested chain
    public string Assembly { get; }          // "Assembly-CSharp" — the PROXY assembly's simple name
    public string ProxyFullName { get; }     // "ToyGame.Player" / "Interop+Kernel32" — reflection spelling

    public ExactTypeRow(string image, string ns, string name, string assembly, string proxyFullName)
    {
        Image = image; Namespace = ns; Name = name; Assembly = assembly; ProxyFullName = proxyFullName;
    }

    public string Key => ExactTypeMap.KeyOf(Image, Namespace, Name);
}

// The EXACT-TYPE MAP file: il2cpp class identity -> generated proxy type, written beside the patched proxies by the
// IL-rewrite seam and read at runtime by the materializer (docs/reference/exact-proxy-types.md).
//
// WHY A SIDECAR AT ALL. The runtime question is "given this object's native class, which proxy type IS it?" and
// Il2CppInterop can only answer the inverse: `Il2CppClassPointerStore<T>.NativeClassPtr` is filled by running T's
// static ctor, so inverting it at runtime would mean forcing the static ctor of every candidate proxy — each of
// which resolves every field and method pointer of its type. That is the cost the patcher does NOT have to pay: it
// is already reading every proxy module with Cecil, and the triple is sitting in the IL as three `ldstr`s.
//
// THE FORMAT LIVES HERE, not in either seam, for the same reason ContentMarker's does: the writer (InteropPatch)
// and the reader (the runtime marshaller) must agree, so they share the object that says what they agree on rather
// than two parsers kept in step by hand. Deliberately line-oriented text, not JSON — a real game's map is ~10^5
// rows, and this parses with no allocation per field beyond the strings themselves and no dependency at all.
public static class ExactTypeMap
{
    public const string FileName = "inutil.typemap";

    const char FieldSep = '|';
    const string Header = "# inutil exact-type map — DO NOT EDIT. il2cpp class identity -> the generated proxy type\n" +
                          "# that stands for it, so a proxy can be materialized at the object's ACTUAL type rather\n" +
                          "# than at the type of the seam it was read through (docs/reference/exact-proxy-types.md).\n" +
                          "# image|namespace|name|proxy-assembly|proxy-type\n";

    // The lookup key. The runtime builds the same string from the native class (image name, namespace, and the
    // declaring-type chain of names), so this method is the ONE place the key's shape is decided.
    public static string KeyOf(string image, string ns, string name) => image + FieldSep + ns + FieldSep + name;

    // Serialize. Rows are emitted sorted so the file is byte-stable across runs — which is what lets the writer skip
    // an unchanged file, and what makes a diff of two patched trees readable.
    //
    // AMBIGUITY IS DROPPED, LOUDLY IN THE FILE. Two proxy types claiming ONE il2cpp class identity cannot both be
    // right, and picking either would silently materialize objects as the wrong type — worse than not resolving at
    // all, because the fallback (the declared type) is merely imprecise while a wrong sibling type is a lie. Such
    // keys are omitted and listed in the header comment, so "why is this type never exact?" has an answer on disk.
    public static string Serialize(IEnumerable<ExactTypeRow> rows)
    {
        var byKey = new Dictionary<string, ExactTypeRow>(StringComparer.Ordinal);
        var ambiguous = new SortedSet<string>(StringComparer.Ordinal);
        foreach (ExactTypeRow r in rows)
        {
            if (byKey.TryGetValue(r.Key, out ExactTypeRow? prior))
            {
                if (prior.ProxyFullName != r.ProxyFullName || prior.Assembly != r.Assembly) ambiguous.Add(r.Key);
                continue;
            }
            byKey[r.Key] = r;
        }
        foreach (string k in ambiguous) byKey.Remove(k);

        var keys = new List<string>(byKey.Keys);
        keys.Sort(StringComparer.Ordinal);

        var sb = new StringBuilder();
        sb.Append(Header);
        foreach (string k in ambiguous)
            sb.Append("# ambiguous (two proxy types claim it) — deliberately unresolvable: ").Append(k).Append('\n');
        foreach (string k in keys)
        {
            ExactTypeRow r = byKey[k];
            sb.Append(r.Image).Append(FieldSep).Append(r.Namespace).Append(FieldSep).Append(r.Name)
              .Append(FieldSep).Append(r.Assembly).Append(FieldSep).Append(r.ProxyFullName).Append('\n');
        }
        return sb.ToString();
    }

    // Parse a serialized map into key -> (proxy assembly, proxy type). Malformed lines are skipped rather than
    // thrown on: a map is an OPTIMIZATION over a correct fallback (materialize at the declared type), so a damaged
    // one must degrade to today's behaviour, never take the game down at a property read.
    public static Dictionary<string, (string Assembly, string Type)> Parse(string text)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Length > 0 && raw[raw.Length - 1] == '\r' ? raw.Substring(0, raw.Length - 1) : raw;
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split(FieldSep);
            if (f.Length != 5) continue;
            map[KeyOf(f[0], f[1], f[2])] = (f[3], f[4]);
        }
        return map;
    }

    // Write <dir>/inutil.typemap. Atomic + idempotent, exactly like ContentMarker.Stamp: temp file in the same
    // directory then rename, and a byte-identical map is not rewritten (so a re-patch of an unchanged tree touches
    // nothing). Returns true if the file changed.
    public static bool Write(string dir, IEnumerable<ExactTypeRow> rows)
    {
        Directory.CreateDirectory(dir);
        string content = Serialize(rows);
        string path = Path.Combine(dir, FileName);
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

    // Read <dir>/inutil.typemap, or null when absent/unreadable — "no map" is a supported state (an unpatched tree,
    // or a tree patched by an inutil that predates this pass), and it means exact typing is simply off.
    public static Dictionary<string, (string Assembly, string Type)>? Read(string dir)
    {
        string path = Path.Combine(dir, FileName);
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch { return null; }
    }
}
