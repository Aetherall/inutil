using System.IO;
using System.Text.RegularExpressions;

namespace Inutil.Schema.Tests;

// The HARD LINE, enforced structurally: the shipped runtime SDK (Inutil.dll) must have NO transitive edge to the
// author-time metadata pillar (Inutil.Metadata) or its heavy decompiler dependency (Cpp2IL / AsmResolver). The
// pillar is offline-only; if it leaked into the runtime graph, Inutil.dll would drag a metadata-reading toolchain
// into every modded game — the "one engine, not two" violation the isolation invariant exists to prevent.
//
// Walks the PROJECT GRAPH from Inutil.csproj over its ProjectReference edges and asserts no reachable project
// references the pillar or Cpp2IL. Enforces the INVARIANT (no runtime-graph project reaches the pillar/decompiler),
// not an instance: a new consumer that pulls Cpp2IL trips this the moment its csproj joins the runtime closure,
// without the test being updated.
static class RuntimeIsolationTest
{
    // The runtime SDK root whose transitive closure must stay pillar-free.
    const string RuntimeRoot = "Inutil/Inutil.csproj";

    // Tokens that must never appear as a reference anywhere in the runtime closure (project or package).
    static readonly string[] Forbidden = { "Inutil.Metadata", "Cpp2IL", "LibCpp2IL", "AsmResolver" };

    public static void Run()
    {
        string? src = RegistryDriftTest.FindManagedSrc();
        if (src is null) { T.Check("isolation: located managed/src", false); return; }

        string root = Path.GetFullPath(Path.Combine(src, RuntimeRoot));
        if (!File.Exists(root)) { T.Check($"isolation: located {RuntimeRoot}", false); return; }

        // BFS the ProjectReference graph from the runtime root; record every reachable csproj.
        var reachable = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(root);
        seen.Add(root);
        while (queue.Count > 0)
        {
            string proj = queue.Dequeue();
            reachable.Add(proj);
            string dir = Path.GetDirectoryName(proj)!;
            foreach (string rel in References(proj, "ProjectReference"))
            {
                string abs = Path.GetFullPath(Path.Combine(dir, rel));
                if (seen.Add(abs)) queue.Enqueue(abs);
            }
        }

        // No reachable project (nor the root) may reference a forbidden token, by project OR package.
        var violations = new List<string>();
        foreach (string proj in reachable)
        {
            string name = Path.GetFileName(proj);
            foreach (string reference in References(proj, "ProjectReference").Concat(References(proj, "PackageReference")))
                foreach (string token in Forbidden)
                    if (reference.Contains(token, StringComparison.OrdinalIgnoreCase))
                        violations.Add($"{name} -> {reference}");
        }

        T.Check($"§7: Inutil.dll runtime closure ({reachable.Count} projects) reaches no pillar/Cpp2IL edge "
                + $"(violations: {string.Join("; ", violations)})",
            violations.Count == 0);

        // Sanity: the walk actually traversed edges (Inutil -> Inutil.Schema at least), so a green result means the
        // graph was inspected, not that the root was unreadable.
        T.Check("§7: runtime closure includes Inutil.Schema (walk reached the shared core)",
            reachable.Any(p => Path.GetFileName(p) == "Inutil.Schema.csproj"));
    }

    static IEnumerable<string> References(string csproj, string kind)
    {
        if (!File.Exists(csproj)) yield break;
        foreach (Match m in Regex.Matches(File.ReadAllText(csproj), kind + @"\s+Include=""([^""]+)"""))
            yield return m.Groups[1].Value;
    }
}
