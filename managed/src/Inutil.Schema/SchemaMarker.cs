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

    // Written INSTEAD of the marker when a patch reached the tree but could not write every module (a module the
    // filesystem refused — in-game, one the loader already has mapped). Names the modules that stayed raw, so the
    // "these proxies are unpatched" warning can tell an incomplete patch from no patch at all: on the boot that
    // GENERATES the proxies, "they were not patched" is both alarming and false about the other ~139. Deleted by
    // the patch run that finally completes, so it can never outlive the condition it describes.
    public const string InteropPartialFileName = "inutil.interop-partial";

    // The sidecar marker sits beside the wire-map as "<sidecar>.marker". A suffix (not a fixed name) because the
    // sidecar path is caller-chosen; the marker tracks whatever the sidecar is called.
    public const string SidecarMarkerSuffix = ".marker";

    const string InteropTag = "inutil-interop-marker/1";
    const string WireTag = "inutil-wire-marker/1";

    // The content-address of a proxy patch: the natural-typing registry (Families.Default()) that drove it, PLUS
    // every capability the patch has that the registry does not describe (PatchCapabilities). The registry half
    // captures each fact the IL-rewrite seam flips on (anchor, BCL counterpart, ConvKind, write-target, shape,
    // direction), one canonical line per family; same family set (any order) hashes equal, and changing any fact
    // changes the hash — exactly "these proxies are stale".
    //
    // The capability half is what keeps that claim TRUE. Hashing the registry alone was sound only while every
    // rewriter was registry-driven; a pass that flips on something else (equality pairing) would otherwise leave a
    // tree patched without it addressing identical to one patched with it. Both halves, or the address is a
    // description of part of the patch presented as a description of all of it.
    public static string Hash(CorrespondenceRegistry registry) => Hash(registry, PatchCapabilities.All);

    // The capability list is a PARAMETER here only so a test can prove the address is actually sensitive to it —
    // with the list fixed at a constant, "the hash folds in capabilities" is otherwise unfalsifiable. Production
    // callers use the one-arg form; there is no second capability set in the wild.
    public static string Hash(CorrespondenceRegistry registry, IEnumerable<string> capabilities)
    {
        IEnumerable<string> rows = registry.All.Select(c => string.Join("|",
            c.Il2CppFullName,
            c.BclOpenType.FullName,
            c.Kind.ToString(),
            c.WriteTarget ? "W" : "-",
            c.Shape.ToString(),
            c.Direction.ToString()));
        return HashRows(InteropTag, rows.Concat(capabilities.Select(c => "capability|" + c)));
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
