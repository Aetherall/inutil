using Mono.Cecil;
using Inutil.Check;

namespace Inutil.InteropPatch.Tests;

// The author-facing surface renderer (`inutil-check methods` / `query`) must never show an il2cpp spelling as its
// natural twin.
//
// WHY THIS EXISTS. The renderer had ONE name function, Naming.CleanTypeName, built for the reverse INDEX: it maps
// Il2CppSystem.X -> System.X and collapses every il2cpp array wrapper to T[], so a reference files under one key
// however Il2CppInterop spelled it. Correct for a key; catastrophic for the mod author reading the same output,
// because a member the patch did NOT flip renders IDENTICALLY to one it did. `ItemTemplate.ParentId` printed as
// `System.Nullable<EFT.MongoID>` while C# saw `Il2CppSystem.Nullable<EFT.MongoID>` and rejected the natural
// assignment. The tool answered "yes you can write this naturally" for exactly the members where the answer is no.
//
// PHRASED AGAINST THE FACT, not against those two families. The check is "the AUTHOR rendering of a member never
// equals the INDEX rendering when the underlying type is an il2cpp spelling" — so a wrapper family added to
// CleanTypeName later is covered here with no edit, and a renderer rewritten to some third scheme still has to
// satisfy it. The tag is verified through the real TypeSurface OUTPUT (the bytes a human reads), not by calling
// the predicate twice.
//
// NON-VACUITY IS ASSERTED. Run against proxies containing no il2cpp spellings at all, every loop below would pass
// having proven nothing — the same trap this suite guards elsewhere with the pristine-marker gate. So the fixture
// is required to actually contain raw members before any conclusion is drawn.
static class SurfaceRenderingTests
{
    public static int Run(string interopDir)
    {
        int failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]" + (detail is null ? "" : $" — {detail}"));
            if (!ok) failures++;
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(interopDir);
        var rp = new ReaderParameters { AssemblyResolver = resolver };

        var modules = new List<ModuleDefinition>();
        foreach (string dll in Directory.GetFiles(interopDir, "*.dll"))
        {
            try { modules.Add(ModuleDefinition.ReadModule(dll, rp)); }
            catch { /* native or non-.NET DLL in the dir — the CLI skips these too */ }
        }
        if (modules.Count == 0) { Check("surface rendering: proxies readable", false, $"no readable modules in {interopDir}"); return 1; }

        // ── 1. the two renderings must genuinely disagree on an il2cpp spelling, and agree on a natural one ──
        // Sampled from the REAL proxies rather than synthesised, so this tracks whatever Il2CppInterop emits.
        var raw = new List<(TypeDefinition Type, string Member, TypeReference Ty)>();
        var natural = new List<(TypeDefinition Type, string Member, TypeReference Ty)>();
        foreach (ModuleDefinition m in modules)
            foreach (TypeDefinition t in m.GetTypes())
                foreach (PropertyDefinition p in t.Properties)
                    (Naming.IsRawIl2Cpp(p.PropertyType) ? raw : natural).Add((t, p.Name, p.PropertyType));

        // This is also where a collapsed renderer lands: make the two renderings agree again and NOTHING classifies
        // as raw, so every case below would pass by having nothing to check. Verified by sabotage — reverting
        // AuthorTypeName to CleanTypeName turns this case, and only this case, red.
        Check("surface rendering: fixture actually CONTAINS il2cpp-spelled members (else every case below is vacuous)",
              raw.Count > 0,
              $"{raw.Count} raw / {natural.Count} natural propert(ies)"
              + (raw.Count == 0 ? " — pristine proxies with ZERO il2cpp spellings is not credible; the likely cause"
                                + " is AuthorTypeName having collapsed back into CleanTypeName" : ""));
        if (raw.Count == 0) return failures;   // nothing meaningful left to assert

        string? collapsed = null;   // the first member whose author rendering hides the il2cpp spelling
        foreach (var (t, member, ty) in raw.Take(200))
        {
            string author = Naming.AuthorTypeName(ty);
            // The author rendering must carry the REAL name Cecil holds, never a naturalized alias.
            if (author == Naming.CleanTypeName(ty) || !author.Contains("Il2Cpp", StringComparison.Ordinal))
            {
                collapsed = $"{t.Name}::{member} rendered '{author}' for Cecil '{ty.FullName}'";
                break;
            }
        }
        Check("every il2cpp-spelled member renders its REAL spelling for an author",
              collapsed is null, collapsed ?? $"checked {Math.Min(raw.Count, 200)}");

        // ── 2. the rendered OUTPUT a human reads must carry the warning tag ──
        // Through TypeSurface itself: a predicate that is right while the printer drops the tag helps nobody.
        var sample = raw.Select(r => r.Type)
                        .Where(t => !t.HasGenericParameters)       // `methods Foo\`1` resolves; keep the sample simple
                        .Distinct()
                        .Take(5).ToList();
        foreach (TypeDefinition t in sample)
        {
            var res = ReverseIndex.TypeSurface(modules, Naming.CleanTypeName(t), 500);
            bool tagged = res.Text.Contains("[raw il2cpp]", StringComparison.Ordinal);
            bool legend = res.Text.Contains("natural typing does NOT reach it", StringComparison.Ordinal);
            Check($"TypeSurface flags il2cpp spellings: {t.Name}", tagged && legend,
                  tagged ? (legend ? null : "tagged but no legend") : "no [raw il2cpp] tag in the rendered output");
        }

        // ── 3. the specific collapse that caused the incident, stated as itself ──
        // A regression test on top of the general invariant above: Nullable and the array wrappers are the two
        // families the index rendering flattens, and they are what a consumer actually met.
        var nullable = raw.FirstOrDefault(r => r.Ty.FullName.StartsWith("Il2CppSystem.Nullable`1", StringComparison.Ordinal));
        if (nullable.Ty is not null)
            Check("Il2CppSystem.Nullable<T> is NOT rendered as System.Nullable<T> for an author",
                  !Naming.AuthorTypeName(nullable.Ty).StartsWith("System.Nullable", StringComparison.Ordinal),
                  Naming.AuthorTypeName(nullable.Ty));

        var arrayWrap = raw.FirstOrDefault(r => r.Ty.FullName.Contains("InteropTypes.Arrays.Il2Cpp", StringComparison.Ordinal));
        if (arrayWrap.Ty is not null)
            Check("an il2cpp array wrapper is NOT rendered as T[] for an author",
                  !Naming.AuthorTypeName(arrayWrap.Ty).EndsWith("[]", StringComparison.Ordinal),
                  Naming.AuthorTypeName(arrayWrap.Ty));

        foreach (ModuleDefinition m in modules) m.Dispose();
        return failures;
    }

}
