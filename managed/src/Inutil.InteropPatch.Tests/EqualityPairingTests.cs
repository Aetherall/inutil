using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.InteropPatch;

namespace Inutil.InteropPatch.Tests;

// The equality-pairing pass over a SYNTHETIC proxy-root module (no game, no Cpp2IL).
//
// WHY THIS EXISTS. Il2CppInterop emits Il2CppSystem.Object's GetHashCode()/Equals(Il2CppSystem.Object) as NEWSLOT —
// parallel virtual slots that do not override System.Object's. The generated bodies are correct and forward to the
// game; nothing dispatches to them. So every managed hash container over any proxy silently uses wrapper identity,
// and `dict[key]` cannot hit an entry the game put there.
//
// PHRASED AGAINST THE SLOT, not against the type that reported it: the assertions are "no proxy type shadows a
// System.Object virtual with a parallel newslot" and "the added Equals dispatches virtually", so a generator that
// starts shadowing somewhere new is covered without editing them.
//
// WHAT THIS SUITE CANNOT PROVE, and why the battery exists: whether the reconnected slots actually reach the game.
// The first version of this pass paired per-type and passed every offline assertion here while producing a real
// Equals over a wrapper-identity hash — equal keys in different buckets. Only running it in a booted game caught
// that. Offline proves the SHAPE; `equality.*` in the battery proves the BEHAVIOUR.
static class EqualityPairingTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]");
            if (!ok) failures++;
        }

        ModuleDefinition module = Build();
        TypeDefinition root = module.GetTypes().First(t => t.FullName == "Il2CppSystem.Object");

        // ── non-vacuity: the fixture really does shadow before the pass, or every check below proves nothing ──
        int shadowedBefore = EqualityRewriter.Shadowed(module).Count();
        Check("fixture is non-vacuous: System.Object virtuals ARE shadowed before the pass", shadowedBefore >= 1);
        Check("...and specifically GetHashCode is newslot to begin with",
            root.Methods.First(m => m.Name == "GetHashCode").IsNewSlot);

        var result = new EqualityRewriter().RewriteModule(module);
        Check("the pass reports both root edits", result.Flipped == 2);

        // ── GetHashCode: reconnected by clearing NEWSLOT, and NOT by adding a second method ──
        Check("GetHashCode is now a ReuseSlot override of System.Object::GetHashCode",
            !root.Methods.First(m => m.Name == "GetHashCode").IsNewSlot);
        Check("...and no duplicate GetHashCode was introduced",
            root.Methods.Count(m => m.Name == "GetHashCode") == 1);

        // ── Equals(object): added, because a signature mismatch cannot be fixed by a flag ──
        MethodDefinition? added = root.Methods.FirstOrDefault(m => m.Name == "Equals" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.FullName == "System.Object");
        Check("an Equals(System.Object) override was added", added is not null);
        Check("...public virtual bool, ReuseSlot (so it fills System.Object's slot)",
            added is { IsPublic: true, IsVirtual: true, IsNewSlot: false } && added.ReturnType.FullName == "System.Boolean");
        Check("...and it dispatches VIRTUALLY to Equals(Il2CppSystem.Object) — not a pointer compare, so a derived "
              + "type's own equality is what runs",
            added is not null && added.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt
                && i.Operand is MethodReference mr && mr.Name == "Equals"
                && mr.Parameters.Count == 1 && mr.Parameters[0].ParameterType.FullName == "Il2CppSystem.Object"));

        // ── POSTCONDITION over the whole tree ──
        Check("POSTCONDITION: no proxy type shadows a System.Object equality virtual any more",
            !EqualityRewriter.Shadowed(module).Any());

        // ── the derived types are untouched: they already override the root's slots, which is the point ──
        TypeDefinition derived = module.GetTypes().First(t => t.FullName == "Game.ItemId");
        Check("a derived proxy needs NO edit (its ReuseSlot override now chains into System.Object's slot)",
            derived.Methods.Count(m => m.Name is "GetHashCode" or "Equals") == 2
            && !derived.Methods.Any(m => m.Name == "Equals" && m.Parameters[0].ParameterType.FullName == "System.Object"));

        // ── idempotent ──
        var second = new EqualityRewriter().RewriteModule(module);
        Check("a second run over an already-paired module changes nothing", second.Flipped == 0);

        // ── a module that does not declare the proxy root is not this pass's business ──
        var unrelated = ModuleDefinition.CreateModule("NoRoot", ModuleKind.Dll);
        Check("a module without Il2CppSystem.Object is left alone",
            new EqualityRewriter().RewriteModule(unrelated).Flipped == 0);

        // ── survives a Cecil write + re-read (the metadata is well-formed) ──
        string outPath = Path.Combine(Path.GetTempPath(), "inutil-equality-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            module.Write(outPath);
            using ModuleDefinition reread = ModuleDefinition.ReadModule(outPath);
            Check("the paired module round-trips through Cecil", !EqualityRewriter.Shadowed(reread).Any());
        }
        finally
        {
            if (File.Exists(outPath)) try { File.Delete(outPath); } catch { }
        }

        // ── the marker must SEE this pass, or a tree patched without it addresses identical to one patched WITH it ──
        Check("this pass is declared as a patch capability (so the content-address moves for it)",
            Inutil.Schema.PatchCapabilities.All.Any(c => c.StartsWith("equality-pairing/")));

        return failures;
    }

    // The generated shape, reproduced: an Il2CppSystem.Object root whose Object-shaped virtuals are NEWSLOT, plus a
    // derived proxy whose own overrides ReuseSlot onto the root's parallel slots.
    static ModuleDefinition Build()
    {
        var module = ModuleDefinition.CreateModule("SyntheticProxyRoot", ModuleKind.Dll);
        var runtimeRef = new AssemblyNameReference("Il2CppInterop.Runtime", new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(runtimeRef);
        var objectBase = new TypeReference("Il2CppInterop.Runtime.InteropTypes", "Il2CppObjectBase", module, runtimeRef);

        var root = new TypeDefinition("Il2CppSystem", "Object", TypeAttributes.Public | TypeAttributes.Class, objectBase);
        module.Types.Add(root);

        void Method(TypeDefinition t, string name, TypeReference ret, TypeReference? param, bool newSlot)
        {
            MethodAttributes attrs = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual
                                     | (newSlot ? MethodAttributes.NewSlot : MethodAttributes.ReuseSlot);
            var m = new MethodDefinition(name, attrs, ret);
            if (param is not null) m.Parameters.Add(new ParameterDefinition(param));
            m.Body = new MethodBody(m);
            ILProcessor il = m.Body.GetILProcessor();
            il.Emit(ret.FullName == "System.String" ? OpCodes.Ldnull : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            t.Methods.Add(m);
        }

        Method(root, "GetHashCode", module.TypeSystem.Int32, null, newSlot: true);
        Method(root, "Equals", module.TypeSystem.Boolean, root, newSlot: true);
        Method(root, "ToString", module.TypeSystem.String, null, newSlot: true);   // same flaw, deliberately not fixed

        // A derived proxy in the MongoID shape: it already overrides the root's slots, so the root fix reaches it.
        var itemId = new TypeDefinition("Game", "ItemId", TypeAttributes.Public | TypeAttributes.Class, root);
        module.Types.Add(itemId);
        Method(itemId, "GetHashCode", module.TypeSystem.Int32, null, newSlot: false);
        Method(itemId, "Equals", module.TypeSystem.Boolean, root, newSlot: false);

        return module;
    }
}
