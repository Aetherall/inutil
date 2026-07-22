using System.IO;
using Inutil.Schema;

namespace Inutil.Schema.Tests;

// The mirror-image of RegistryDriftTest for the WIRE aspect (docs/contribution/architecture/16-metadata.md): WireFamilies.cs is the ONLY
// non-test code file allowed to spell an attribute type name, exactly as Families.cs is the only one allowed to spell
// an `Il2CppSystem.*` family name (C4).
//
// Deliberately STRONGER than RegistryDriftTest's static-regex table (enforce the invariant, not its last instance):
// it derives the forbidden anchor set FROM WireFamilies.Default() at run time and greps for each, so a recognizer
// added tomorrow is covered automatically — no frozen alternation to forget to update. The invariant is "no
// attribute anchor is spelled outside WireFamilies.cs", asserted against the registry's ACTUAL current contents.
static class WireRegistryDriftTest
{
    const string Home = "WireFamilies.cs";

    public static void Run()
    {
        string? src = RegistryDriftTest.FindManagedSrc();
        if (src is null) { T.Check("wire-drift: located managed/src to scan", false); return; }

        // The anchors, straight from the single site — the invariant's live definition, not a copy.
        var anchors = new List<string>();
        foreach (WireCorrespondence c in WireFamilies.Default().All)
            anchors.Add(c.AttributeTypeFullName);

        T.Check("wire-drift: WireFamilies.Default() registers at least one recognizer", anchors.Count > 0);

        foreach (string anchor in anchors)
        {
            var offenders = new List<string>();
            foreach (string file in RegistryDriftTest.EnumerateCodeFiles(src))
            {
                string name = Path.GetFileName(file);
                if (name == Home) continue;                       // the single legitimate spelling site
                if (File.ReadAllText(file).Contains(anchor)) offenders.Add(name);
            }
            T.Check($"C4(wire): anchor \"{anchor}\" spelled only in {Home} (offenders: {string.Join(", ", offenders)})",
                offenders.Count == 0);
        }
    }
}
