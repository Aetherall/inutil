using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Inutil.Schema;

// The content-ADDRESSES the ContentMarker mechanism stamps (docs/contribution/architecture/16-metadata.md). The pure half: given a
// registry, derive a stable hash of the FACTS that drove an artifact's production, so "was this artifact produced
// by the same schema this build carries?" reduces to a string compare. ContentMarker owns the file; SchemaMarker
// owns what goes in it and where the marker lives.
//
// Every address here is:
//   * ORDER-independent  — rows are sorted before hashing, so re-ordering a family's registration (a no-op:
//     anchors are distinct) does not churn the address.
//   * TFM-independent    — hashed over stable strings (full type names, enum NAMES not ordinals), so the net9
//     patch CLI and the net6 runtime shim compute the SAME address (they MUST: the shim compares its own hash
//     against the CLI's stamped one).
//   * VERSIONED          — a format tag is folded in, so bumping the marker format changes every address at once.
public static class SchemaMarker
{
    // The proxy marker's filename, beside the patched proxies. The SINGLE spelling — patcher stamps and loader
    // shim reads through this one constant, so there is no second string to drift.
    public const string InteropMarkerFileName = "inutil.interop-marker";

    // The sidecar marker sits beside the wire-map as "<sidecar>.marker". A suffix (not a fixed name) because the
    // sidecar path is caller-chosen; the marker tracks whatever the sidecar is called.
    public const string SidecarMarkerSuffix = ".marker";

    const string InteropTag = "inutil-interop-marker/1";
    const string WireTag = "inutil-wire-marker/1";

    // The content-address of the natural-typing registry (Families.Default()) that drove a proxy patch. Captures
    // every fact the IL-rewrite seam flips on (anchor, BCL counterpart, ConvKind, write-target, shape, direction),
    // one canonical line per family. Same family set (any order) hashes equal; changing any fact changes the hash
    // — exactly "these proxies are stale". Sound because every rewriter builds from this one registry, so it IS
    // the full description of what a patch does — hashing it is the honest content-address, not a proxy for it.
    public static string Hash(CorrespondenceRegistry registry)
    {
        IEnumerable<string> rows = registry.All.Select(c => string.Join("|",
            c.Il2CppFullName,
            c.BclOpenType.FullName,
            c.Kind.ToString(),
            c.WriteTarget ? "W" : "-",
            c.Shape.ToString(),
            c.Direction.ToString()));
        return HashRows(InteropTag, rows);
    }

    // The STRUCTURAL address of the wire registry (WireFamilies.Default()) — one line per recognizer (attribute
    // anchor + fact kind). The Extract closure is not hashable, so a recognizer that changed ONLY its extraction
    // logic (same anchor + kind) is not caught here — an accepted limit for the sidecar's marker (it can be
    // cheaply re-derived, unlike a patched proxy at game-load time).
    public static string WireHash(WireRegistry registry)
    {
        IEnumerable<string> rows = registry.All.Select(c => string.Join("|",
            c.AttributeTypeFullName,
            c.Kind.ToString()));
        return HashRows(WireTag, rows);
    }

    // SHA-256, lowercase hex, of a UTF-8 string. The one crypto primitive; callers combine addresses (e.g. the
    // extractor folds WireHash with its input files' hashes into one sidecar address) through this.
    public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // SHA-256, lowercase hex, of a file's bytes — streamed, so a large GameAssembly.dll is not read whole into
    // memory. Used offline to fold a game build's inputs into the sidecar's content-address.
    public static string Sha256HexOfFile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        using SHA256 sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    static string HashRows(string tag, IEnumerable<string> rows)
    {
        var sorted = rows.ToList();
        sorted.Sort(StringComparer.Ordinal);        // order-independent: reordering registration is a no-op
        return Sha256Hex(tag + "\n" + string.Join("\n", sorted));
    }
}
