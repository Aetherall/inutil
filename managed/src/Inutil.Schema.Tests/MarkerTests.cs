using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace Inutil.Schema.Tests;

// docs/contribution/architecture/16-metadata.md — the content-addressed marker, offline. Two halves:
//   * SchemaMarker.Hash — a STABLE, ORDER-independent, SENSITIVE content-address of the registry. These are the
//     properties the whole scheme rests on: the patch CLI (net9) and the loader shim (net6) must derive the SAME
//     address for the same registry, reordering a family must NOT churn it, and changing ANY family fact MUST.
//   * ContentMarker — stamp/read/verify round-trips, and the three verdicts (Current/Stale/Missing) the loader
//     shim branches on. Idempotent + atomic (no temp file left).
static class MarkerTests
{
    public static void Run()
    {
        RunSchemaHash();
        RunContentMarker();
    }

    static void RunSchemaHash()
    {
        // Two hand-built registries with the SAME rows in DIFFERENT order — the address must not depend on order
        // (reordering registration is a semantic no-op: anchors are distinct, first-match is unaffected).
        TypeCorrespondence list = Row("Il2CppSystem.Collections.Generic.List`1", typeof(List<>), ConvKind.List, 1);
        TypeCorrespondence dict = Row("Il2CppSystem.Collections.Generic.Dictionary`2", typeof(Dictionary<,>), ConvKind.Dictionary, 2);

        var ab = new CorrespondenceRegistry().Register(list).Register(dict);
        var ba = new CorrespondenceRegistry().Register(dict).Register(list);
        T.Check("SchemaMarker.Hash is order-independent (same rows, swapped order -> same hash)",
            SchemaMarker.Hash(ab) == SchemaMarker.Hash(ba));

        // Fewer families -> different address (adding/removing a family is exactly "these proxies are stale").
        var justList = new CorrespondenceRegistry().Register(list);
        T.Check("SchemaMarker.Hash is sensitive to the family SET (dropping a family changes it)",
            SchemaMarker.Hash(ab) != SchemaMarker.Hash(justList));

        // Same family set, one CHANGED fact (writeTarget flipped) -> different address.
        TypeCorrespondence listRO = Row("Il2CppSystem.Collections.Generic.List`1", typeof(List<>), ConvKind.List, 1, writeTarget: false);
        var abChanged = new CorrespondenceRegistry().Register(listRO).Register(dict);
        T.Check("SchemaMarker.Hash is sensitive to a changed fact (writeTarget)",
            SchemaMarker.Hash(ab) != SchemaMarker.Hash(abChanged));

        // The real registry: deterministic + a well-formed 64-char lowercase-hex SHA-256.
        string h1 = SchemaMarker.Hash(Families.Default());
        string h2 = SchemaMarker.Hash(Families.Default());
        T.Check("SchemaMarker.Hash(Families.Default()) is deterministic", h1 == h2);
        T.Check($"...and is 64-char lowercase hex ({h1})",
            h1.Length == 64 && h1.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));

        // WireHash is a distinct, self-consistent address (proves the sidecar side of Generalization D hashes too).
        string w1 = SchemaMarker.WireHash(WireFamilies.Default());
        T.Check("SchemaMarker.WireHash(WireFamilies.Default()) is deterministic + differs from the interop hash",
            w1 == SchemaMarker.WireHash(WireFamilies.Default()) && w1 != h1);
    }

    static void RunContentMarker()
    {
        string dir = Path.Combine(Path.GetTempPath(), "inutil-marker-test-" + Guid.NewGuid().ToString("N"));
        const string file = "test.marker";
        try
        {
            T.Check("Verify -> Missing when the marker (dir) is absent",
                ContentMarker.Verify(dir, file, "abc") == MarkerVerdict.Missing);

            bool wrote = ContentMarker.Stamp(dir, file, "abc", "a human note\nspanning two lines");
            T.Check("Stamp writes a fresh marker (returns changed=true)", wrote);
            T.Check("ReadHash round-trips the stamped hash", ContentMarker.ReadHash(dir, file) == "abc");
            T.Check("Verify -> Current when the address matches", ContentMarker.Verify(dir, file, "abc") == MarkerVerdict.Current);
            T.Check("Verify -> Stale when the address differs", ContentMarker.Verify(dir, file, "xyz") == MarkerVerdict.Stale);

            bool wroteAgain = ContentMarker.Stamp(dir, file, "abc", "a human note\nspanning two lines");
            T.Check("Stamp is idempotent (identical marker -> not rewritten, returns changed=false)", !wroteAgain);

            bool wroteChanged = ContentMarker.Stamp(dir, file, "def");
            T.Check("Stamp rewrites on a changed address", wroteChanged && ContentMarker.ReadHash(dir, file) == "def");

            T.Check("atomic: no .inutil-tmp left behind after stamping",
                !Directory.EnumerateFiles(dir, "*.inutil-tmp").Any());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    static TypeCorrespondence Row(string anchor, Type bcl, ConvKind kind, int arity, bool writeTarget = true) =>
        new(anchor, bcl, kind, new AnchorBridge(anchor, arity, BridgeShape.ReferenceBespoke, Directionality.Both), writeTarget);
}
