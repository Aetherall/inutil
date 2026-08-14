using System.IO;
using System.Text.RegularExpressions;

namespace Inutil.Schema.Tests;

// C4 + C6 (docs/contribution/01-philosophy.md): the structural guardrail that keeps the "one il2cpp fact, one home" invariants
// from silently drifting back into the four-hand-kept-tables shape. Each invariant below is a (pattern, allowed
// home(s)) pair; ANY OTHER non-test code file that spells it means a second implementation is creeping in, and this
// fails NOW — offline, in `check`.
//
// Enforce the invariant, not its last instance. A string grep is blind to (a) the ref-bearing-value-proxy
// discriminator, tripled as a bare `t.BaseType?.FullName == "Il2CppSystem.ValueType"` across three files (C6.1 —
// now one home: ValueTypeBridge), and (b) the `typeof(Il2CppSystem.*)` TOKEN form — the same family fact spelled as
// a type. Both are now invariants in the table. The one legitimate typeof — Hooks' patched-Nullable ABI sizing — is
// its allowed home; because a grep cannot compare a typeof to a registry STRING, a separate source-level tie
// (RunNullableTie) binds Hooks' typeof to the registry's Nullable row so neither can be renamed without the other (C6.2).
static class RegistryDriftTest
{
    // (description, offending pattern, the ONLY non-test code file(s) allowed to spell it).
    static readonly (string Desc, Regex Pattern, string[] Homes)[] Invariants =
    {
        // The "which il2cpp family <-> which BCL type" map: one home = the registry (Families.cs). The two
        // doc-comment examples in the registry's own type definitions are the named exceptions. The alternation
        // tracks EVERY registered family (Action|Func added with the delegate family) — enforce the invariant, not
        // its last instance: a new family name spelled outside Families.cs must trip HERE, not slip through because
        // the guard was frozen at the pre-delegate roster.
        ("family-map name-string",
         new Regex("\"Il2CppSystem\\.(Collections|Threading|Nullable|ValueTuple|Action|Func)"),
         new[] { "Families.cs", "Correspondence.cs", "TypeRef.cs" }),

        // The ref-bearing-value-proxy discriminator (C6.1): one RUNTIME home = ValueTypeBridge. Il2CppConvShapeSource
        // (leaf classification) and Hooks (ABI value-type sizing) call IsRefBearingValueProxy instead of re-spelling it.
        ("ref-bearing-value-proxy discriminator",
         new Regex("\"Il2CppSystem\\.ValueType\""),
         new[] { "ValueTypeBridge.cs" }),

        // The typeof(Il2CppSystem.*) TOKEN form the old grep was deliberately blind to (C6): the same family fact
        // spelled as a type. The one legitimate use is Hooks' patched-Nullable ABI sizing (an ABI-sizing concern, not
        // a marshalling-family map — so it stays out of the pure module) — tied to the registry by RunNullableTie.
        ("il2cpp typeof token",
         new Regex("typeof\\(\\s*Il2CppSystem\\."),
         new[] { "Hooks.cs" }),

        // The container-flip roster ("which ConvKinds the interop-patch flip owns"): one home = Correspondence.cs
        // (TypeCorrespondence.IsFlippableContainer). Before centralization this predicate was hand-copied across
        // ContainerFamily (2 spots) and ContainerFlip (2 spots) — and had ALREADY drifted (Naturalize was a Set
        // behind the other three after the HashSet add). Dictionary+Tuple appear in a ConvKind or-chain ONLY in this
        // roster: the separate materialize-as-sequence dispatch (Il2CppConvRuntime) is List/Enumerable/Set — no
        // Dictionary, no Tuple. So an or-ADJACENT Dictionary/Tuple anywhere else is a seam re-inlining the roster.
        // Enforces the SET-MEMBERSHIP fact, not a literal string: a reordered or partial re-inline (e.g. dropping
        // Set again, or "List or Tuple or Dictionary") still trips it — it's blind only to a spelling with NEITHER
        // Dictionary NOR Tuple, which by definition is not the flip roster.
        ("container-flip roster",
         new Regex(@"ConvKind\.(Dictionary|Tuple)\s+or\b|\bor\s+ConvKind\.(Dictionary|Tuple)\b"),
         new[] { "Correspondence.cs" }),
    };

    public static void Run()
    {
        string? src = FindManagedSrc();
        if (src is null) { T.Check("C4: located managed/src to scan", false); return; }

        foreach ((string desc, Regex pattern, string[] homes) in Invariants)
        {
            var homeSet = new HashSet<string>(homes, StringComparer.Ordinal);
            var offenders = new List<string>();
            foreach (string file in EnumerateCodeFiles(src))
            {
                string name = Path.GetFileName(file);
                if (homeSet.Contains(name)) continue;
                if (pattern.IsMatch(File.ReadAllText(file))) offenders.Add(name);
            }
            T.Check($"C4/C6: {desc} lives only in [{string.Join(", ", homes)}] (offenders: {string.Join(", ", offenders)})",
                offenders.Count == 0);
        }

        RunNullableTie(src);
    }

    // C6.2 — the tie a grep cannot make. The registry's Nullable row (a STRING, Families.cs) and Hooks' ABI typeof
    // (a TYPE TOKEN, Hooks.cs) must name the SAME il2cpp type; renaming one without the other would silently diverge
    // Of's ABI plan from the marshaller's family map. We can't resolve Il2CppSystem.Nullable in the pure test, so we
    // tie the two at the SOURCE level: the registry's own Il2CppFullName vs the type Hooks' typeof(Il2CppSystem.…<>)
    // names. Mismatch => fail, offline, right next to the drift guard.
    static void RunNullableTie(string src)
    {
        string? registry = Families.Default().ByConvKind(ConvKind.Nullable)?.Il2CppFullName;
        string regBase = registry?.Split('`')[0] ?? "<no Nullable row>";          // "Il2CppSystem.Nullable`1" -> base

        string hooks = Path.Combine(src, "Inutil", "Hooks", "Hooks.cs");
        Match m = File.Exists(hooks)
            ? Regex.Match(File.ReadAllText(hooks), @"typeof\(\s*(Il2CppSystem\.[A-Za-z0-9_.]*Nullable)\s*<")
            : Match.Empty;
        string hookBase = m.Success ? m.Groups[1].Value : "<no typeof in Hooks.cs>";

        T.Check($"C6.2: Hooks patched-Nullable typeof ({hookBase}) ties registry Nullable row ({regBase})",
            m.Success && registry is not null && hookBase == regBase);
    }

    internal static IEnumerable<string> EnumerateCodeFiles(string src)
    {
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains(".Tests")) continue;                       // tests legitimately spell these
            yield return file;
        }
    }

    // Walk up from the test binary to the repo root (identified by its managed/src tree), then into managed/src.
    internal static string? FindManagedSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "managed", "src");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
