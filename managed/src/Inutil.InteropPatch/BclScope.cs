using Mono.Cecil;

namespace Inutil.InteropPatch;

// Where a BCL type the patch splices in gets its ASSEMBLY IDENTITY from.
//
// The rule: a reference this patcher writes into a proxy must carry the identity of the BCL the PATCHED MODULE runs
// against — never the identity of the BCL the TOOL happens to run on. `module.ImportReference(typeof(List<>))` gets
// the namespace/arity/valuetype-ness right and the VERSION wrong: it resolves the open type from the tool's own
// runtime, so it stamps (and ADDS to module.AssemblyReferences) a System.Private.CoreLib at the tool host's version
// (net9), while the proxy targets the game's runtime (net6). A single such reference — even one nothing is scoped to —
// makes Roslyn reject the whole assembly with CS1705 ("uses System.Private.CoreLib 9.0.0.0 which has a higher version
// than referenced assembly 6.0.0.0"), which takes out every offline consumer check of the patched tree.
//
// So the open type is BUILT, not imported: same namespace/name/arity/valuetype-ness, scoped to the module's OWN
// System.Private.CoreLib reference. The scope is the DEFINING assembly, deliberately not module.TypeSystem.CoreLibrary
// (the System.Runtime facade) — List`1 / Dictionary`2 / ValueTuple`N / Nullable`1 are forwarded from a different
// facade and naming them in System.Runtime fails to load (TypeLoadException, caught in-game). See ContainerFlip.
public static class BclScope
{
    // The module's own System.Private.CoreLib reference, or null when it has none (a synthetic fixture module —
    // callers fall back to ImportReference there, where the tool's identity is the only one available anyway).
    // Prefers the ref already agreeing with the module's System.Runtime version, since PatchDirectory.NormalizeCoreLibRef
    // aligns them all to it: a proxy accumulates several System.Private.CoreLib rows and only that version is loadable.
    public static AssemblyNameReference? CoreLib(ModuleDefinition module)
    {
        AssemblyNameReference? runtime = null, fallback = null;
        foreach (AssemblyNameReference r in module.AssemblyReferences)
        {
            if (r.Name == "System.Runtime") runtime = r;
            else if (r.Name == "System.Private.CoreLib") fallback ??= r;
        }
        if (fallback is null) return null;
        if (runtime is null) return fallback;
        foreach (AssemblyNameReference r in module.AssemblyReferences)
            if (r.Name == "System.Private.CoreLib" && r.Version == runtime.Version) return r;
        return fallback;
    }

    // The OPEN BCL generic type (List`1, Dictionary`2, Nullable`1, ValueTuple`N) as a reference INTO `module`, scoped
    // to the module's own corlib. `IsValueType` is carried over from the reflection type — Cecil encodes a value type
    // as ELEMENT_TYPE_VALUETYPE and a class as ELEMENT_TYPE_CLASS, and Nullable`1 / ValueTuple`N are structs, so
    // getting this wrong produces a signature the runtime rejects.
    public static TypeReference OpenGeneric(ModuleDefinition module, Type bclOpenType)
    {
        AssemblyNameReference? corlib = CoreLib(module);
        if (corlib is null) return module.ImportReference(bclOpenType);   // synthetic-only fallback

        var tr = new TypeReference(bclOpenType.Namespace, bclOpenType.Name, module, corlib)
        {
            IsValueType = bclOpenType.IsValueType,
        };
        for (int i = 0; i < bclOpenType.GetGenericArguments().Length; i++)
            tr.GenericParameters.Add(new GenericParameter(tr));
        return tr;
    }
}
