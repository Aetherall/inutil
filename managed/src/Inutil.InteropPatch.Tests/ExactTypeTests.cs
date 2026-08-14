using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.Schema;
using Inutil.InteropPatch;

namespace Inutil.InteropPatch.Tests;

// The exact-proxy-types seam, OFFLINE: the identity extraction, the map format, the pool retarget, and the
// "every materialization site routes through the one primitive" invariant (docs/reference/exact-proxy-types.md).
//
// Offline proves the SHAPE. Whether a Boss read through an Entity-typed seam actually arrives as a Boss can only be
// answered in a live il2cpp frame — that is `exact.*` in the battery, under both loaders. The split is deliberate
// and matches the equality pass's: the first version of THAT one passed every offline assertion while being wrong
// in-game, which is the standing reminder that a Cecil-level green means the IL is well-formed, not that it works.
static class ExactTypeTests
{
    public static int Run(string interopDir, string workDir)
    {
        int failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]" + (ok || detail is null ? "" : $"  -- {detail}"));
            if (!ok) failures++;
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(interopDir);
        resolver.AddSearchDirectory(workDir);

        ModuleDefinition Read(string dll) => ModuleDefinition.ReadModule(Path.Combine(interopDir, dll),
            new ReaderParameters { InMemory = true, AssemblyResolver = resolver });

        // ── identity extraction: the triple the GENERATOR wrote, not a name rule we re-derived ────────────────
        {
            using ModuleDefinition game = Read("Assembly-CSharp.dll");
            var rows = ExactTypeExtract.Rows(game).ToDictionary(r => r.ProxyFullName, r => r);

            Check("a plain game type states its own identity: ToyGame.Player -> (Assembly-CSharp.dll, ToyGame, Player)",
                rows.TryGetValue("ToyGame.Player", out var player)
                && player.Image == "Assembly-CSharp.dll" && player.Namespace == "ToyGame" && player.Name == "Player",
                rows.TryGetValue("ToyGame.Player", out var p2) ? p2.Key : "absent");

            // The nested arm is the reason the identity is read from IL rather than from the CLR name: the proxy is
            // called `__c`, the il2cpp class is called `<>c`, and only the cctor's own ldstr knows that.
            Check("a NESTED type keys by the declaring chain with its UN-MANGLED il2cpp name (`__c` -> `Bootstrap/<>c`)",
                rows.TryGetValue("Bootstrap+__c", out var nested)
                && nested.Name == "Bootstrap/<>c" && nested.Image == "Assembly-CSharp.dll",
                rows.TryGetValue("Bootstrap+__c", out var n2) ? n2.Key : "absent");

            Check("...and its reflection spelling uses `+`, which is what Assembly.GetType resolves",
                rows.ContainsKey("Bootstrap+__c"));

            // A generic definition MUST NOT be mapped: `Container<Player>`'s native class presents the same
            // (image, ns, name) as the open `Container`1`, so a row for it would resolve every instantiation to a
            // type that cannot be materialized at all.
            Check("a GENERIC type definition is excluded (its instantiations share its il2cpp identity)",
                !rows.Keys.Any(k => k.StartsWith("ToyGame.Container", StringComparison.Ordinal)),
                string.Join(",", rows.Keys.Where(k => k.StartsWith("ToyGame.Container", StringComparison.Ordinal))));

            // The fixture the whole feature exists for — a base and a derived, and a derived whose base lives in
            // ANOTHER proxy module. Both must be nameable or the in-game cases cannot resolve anything.
            Check("the base/derived fixture is mapped: ToyGame.Entity + ToyGame.Boss",
                rows.ContainsKey("ToyGame.Entity") && rows.ContainsKey("ToyGame.Boss"));
            Check("the CROSS-MODULE derived type is mapped: ToyGame.SessionEx (base Session is in ToyGame.Core)",
                rows.ContainsKey("ToyGame.SessionEx"));
        }
        {
            using ModuleDefinition corlib = Read("Il2Cppmscorlib.dll");
            var rows = ExactTypeExtract.Rows(corlib).ToDictionary(r => r.ProxyFullName, r => r);
            // The renaming case: the proxy is `Il2CppSystem.Object` in `Il2Cppmscorlib`, the il2cpp class is
            // `System.Object` in `mscorlib.dll`. Reading the literal gets this right with no rule at all.
            Check("a RENAMED framework proxy keys by the ORIGINAL identity: Il2CppSystem.Object -> (mscorlib.dll, System, Object)",
                rows.TryGetValue("Il2CppSystem.Object", out var obj)
                && obj.Image == "mscorlib.dll" && obj.Namespace == "System" && obj.Name == "Object",
                rows.TryGetValue("Il2CppSystem.Object", out var o2) ? o2.Key : "absent");
        }

        // ── the map format: round-trip, and the ambiguity rule ────────────────────────────────────────────────
        {
            var rows = new[]
            {
                new ExactTypeRow("A.dll", "N", "T", "A", "N.T"),
                new ExactTypeRow("A.dll", "", "Global", "A", "Global"),
                new ExactTypeRow("B.dll", "N", "Nest/Inner", "B", "N.Nest+Inner"),
            };
            var parsed = ExactTypeMap.Parse(ExactTypeMap.Serialize(rows));
            Check("map round-trips every row", parsed.Count == 3, $"{parsed.Count}");
            Check("...keyed exactly as the runtime rebuilds the key (image|ns|name-chain)",
                parsed.TryGetValue(ExactTypeMap.KeyOf("B.dll", "N", "Nest/Inner"), out var nest)
                && nest.Assembly == "B" && nest.Type == "N.Nest+Inner");
            Check("...including a type in the GLOBAL namespace (empty field, not a dropped row)",
                parsed.ContainsKey(ExactTypeMap.KeyOf("A.dll", "", "Global")));

            // Two proxies claiming ONE il2cpp identity: picking either would materialize objects as the wrong
            // sibling type. Dropping the key falls back to the declared type, which is merely imprecise.
            var clashing = rows.Concat(new[] { new ExactTypeRow("A.dll", "N", "T", "C", "N.TOther") });
            var deduped = ExactTypeMap.Parse(ExactTypeMap.Serialize(clashing));
            Check("an AMBIGUOUS identity (two proxy types) is dropped, not guessed",
                !deduped.ContainsKey(ExactTypeMap.KeyOf("A.dll", "N", "T")));
            Check("...and the drop is recorded in the file, so 'why is this never exact?' has an answer on disk",
                ExactTypeMap.Serialize(clashing).Contains("# ambiguous"));
            Check("a DUPLICATE row for the same proxy type is not ambiguity (a module read twice keeps its row)",
                ExactTypeMap.Parse(ExactTypeMap.Serialize(rows.Concat(rows)))
                    .ContainsKey(ExactTypeMap.KeyOf("A.dll", "N", "T")));
            Check("a malformed line degrades to a skipped row, never a throw (the map is an optimization)",
                ExactTypeMap.Parse("garbage\nA.dll|N|T|A|N.T\n|||\n").Count == 1);
        }

        // ── the retarget, over a REAL proxy module ────────────────────────────────────────────────────────────
        {
            string copy = Path.Combine(workDir, "Il2Cppmscorlib.retarget.dll");
            File.Copy(Path.Combine(interopDir, "Il2Cppmscorlib.dll"), copy, overwrite: true);
            var module = ModuleDefinition.ReadModule(copy, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });

            int before = PoolRetargetRewriter.Remaining(module).Count();
            Check("fixture is non-vacuous: the framework proxies DO materialize through Il2CppObjectPool", before > 100, $"{before}");

            // A shared generic body materializes through its OWN generic parameter (List`1::get_Item calls Get<T>).
            // That argument must survive the retarget raw — importing it would strip its owner and produce IL that
            // fails at JIT, which is precisely the class of bug the Task family's ImportPreservingGenericParams
            // exists for. Captured BEFORE, asserted AFTER.
            int openBefore = OpenArgSites(module);
            Check("...including sites whose generic argument is the method's/type's OWN parameter", openBefore > 0, $"{openBefore}");

            var result = new PoolRetargetRewriter().RewriteModule(module);
            Check("every pool call site is retargeted", result.Flipped == before, $"{result.Flipped} of {before}");
            Check("...leaving NONE behind (the pass's postcondition, read off the module)",
                !PoolRetargetRewriter.Remaining(module).Any());
            Check("...to Inutil.Marshal.Il2CppObjects", RetargetedSites(module) == before, $"{RetargetedSites(module)} of {before}");
            Check("...preserving the own-generic-parameter arguments raw", OpenArgSitesOnTarget(module) == openBefore,
                $"{OpenArgSitesOnTarget(module)} of {openBefore}");
            Check("the module gains the Inutil assembly reference the retargeted call resolves through",
                module.AssemblyReferences.Any(a => a.Name == "Inutil"));

            // Round-trip + idempotency, like every other pass: the rewritten module must write and re-read with the
            // retarget intact, and a second pass must find nothing (a retargeted call no longer names the pool).
            string outPath = Path.Combine(workDir, "Il2Cppmscorlib.retargeted.dll");
            module.Write(outPath);
            module.Dispose();
            var reloaded = ModuleDefinition.ReadModule(outPath, new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            Check("round-trip: the retarget survives Cecil write + re-read", RetargetedSites(reloaded) == before);
            var again = new PoolRetargetRewriter().RewriteModule(reloaded);
            Check("idempotent: re-running the pass retargets 0", again.Flipped == 0, $"{again.Flipped}");
            reloaded.Dispose();
        }

        // ── a module with no pool call must not gain a dependency it never uses ──────────────────────────────
        {
            var empty = ModuleDefinition.CreateModule("NoPool", ModuleKind.Dll);
            var t = new TypeDefinition("N", "T", TypeAttributes.Public, empty.TypeSystem.Object);
            empty.Types.Add(t);
            var r = new PoolRetargetRewriter().RewriteModule(empty);
            Check("a module with no materialization site is untouched", r.Flipped == 0);
            Check("...and gains NO Inutil reference (a dependency it would never resolve)",
                !empty.AssemblyReferences.Any(a => a.Name == "Inutil"));
        }

        // ── the INVARIANT: one materializer, every site ───────────────────────────────────────────────────────
        failures += SiteInvariant(Check);
        return failures;
    }

    // Phrased against the FACT — "does anything in the SDK build a proxy from a raw pointer without going through
    // the one materializer" — rather than against the five call sites that had to be converted when this landed.
    // A sixth site added next month is caught by the same check with no edit here, which is the whole point: a
    // guardrail that names the instances it already fixed only ever catches a re-run of the same bug.
    //
    // Two ways to build a proxy from a pointer exist, and both are covered: Il2CppInterop's pool (a reference to
    // Il2CppObjectPool::Get), and the generated (IntPtr) ctor (an Activator.CreateInstance in a method that boxes
    // an IntPtr to pass it). Il2CppObjects itself is exempt because it IS the implementation — the one exemption
    // that is a definition rather than a blind spot.
    static int SiteInvariant(Action<string, bool, string?> check)
    {
        string? sdk = FindSdk();
        if (sdk is null)
        {
            check("SDK materialization-site invariant: Inutil.dll not built — SKIPPED (build Inutil.csproj first)", true, null);
            return 0;
        }

        using var module = ModuleDefinition.ReadModule(sdk, new ReaderParameters { InMemory = true });
        var offenders = new List<string>();
        int materializerUses = 0;

        foreach (TypeDefinition t in module.GetTypes())
        {
            bool isMaterializer = t.FullName == "Inutil.Marshal.Il2CppObjects";
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;
                bool boxesIntPtr = m.Body.Instructions.Any(i => i.OpCode == OpCodes.Box
                                                                && i.Operand is TypeReference b && b.FullName == "System.IntPtr");
                foreach (Instruction i in m.Body.Instructions)
                {
                    if (i.Operand is not MethodReference mr) continue;
                    if (mr.DeclaringType?.FullName == "Inutil.Marshal.Il2CppObjects") materializerUses++;
                    if (isMaterializer) continue;
                    if (mr.DeclaringType?.FullName == PoolRetargetRewriter.PoolFullName && mr.Name == PoolRetargetRewriter.GetName)
                        offenders.Add($"{t.FullName}::{m.Name} calls Il2CppObjectPool::Get");
                    else if (boxesIntPtr && mr.DeclaringType?.FullName == "System.Activator" && mr.Name == "CreateInstance")
                        offenders.Add($"{t.FullName}::{m.Name} builds a proxy via Activator.CreateInstance(type, ptr)");
                }
            }
        }

        // Non-vacuity first: a check that finds no offenders because it is looking at the wrong assembly (or at an
        // SDK where nothing materializes at all) would read as green forever.
        check("SDK materialization-site invariant is non-vacuous: the SDK DOES route through Il2CppObjects",
            materializerUses > 0, $"{materializerUses} use(s)");
        check("every proxy-from-pointer site in the SDK routes through the ONE materializer",
            offenders.Count == 0, string.Join("; ", offenders));
        return offenders.Count == 0 && materializerUses > 0 ? 0 : 1;
    }

    static string? FindSdk()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "managed", "src"))) dir = dir.Parent;
        if (dir is null) return null;
        string path = Path.Combine(dir.FullName, "managed", "src", "Inutil", "bin", "Release", "Inutil.dll");
        return File.Exists(path) ? path : null;
    }

    static int OpenArgSites(ModuleDefinition m)
        => Sites(m, i => i.Operand is GenericInstanceMethod g && PoolRetargetRewriter.Match(g) is not null
                         && g.GenericArguments[0].ContainsGenericParameter);

    static int OpenArgSitesOnTarget(ModuleDefinition m)
        => Sites(m, i => i.Operand is GenericInstanceMethod g && IsExactGet(g) && g.GenericArguments[0].ContainsGenericParameter);

    static int RetargetedSites(ModuleDefinition m)
        => Sites(m, i => i.Operand is GenericInstanceMethod g && IsExactGet(g));

    static bool IsExactGet(GenericInstanceMethod g)
        => g.ElementMethod.DeclaringType?.FullName == "Inutil.Marshal.Il2CppObjects";

    static int Sites(ModuleDefinition m, Func<Instruction, bool> pred)
        => m.GetTypes().SelectMany(t => t.Methods).Where(x => x.HasBody)
            .SelectMany(x => x.Body.Instructions).Count(pred);
}
