using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The il2cpp-container-return -> natural-BCL-return type mapping for the container flip (the IL-rewrite boundary —
// the second seam that consumes the same Conv schema the runtime marshaller does). Given a proxy method's il2cpp
// return TypeReference, produce the NATURAL BCL TypeReference the flip sets it to; null when not a flippable
// container shape.
//
// The "which il2cpp family maps to which BCL type" knowledge is the registry (Families.Default), NOT inline anchor
// constants (docs/contribution/01-philosophy.md): this file classifies through CorrespondenceRegistry and reads the
// row's BclOpenType, so a new container family is one Register call in Families.cs, not an edit here.
public static class ContainerFlip
{
    static readonly CorrespondenceRegistry _families = Families.Default();

    public static TypeReference? NaturalReturn(ModuleDefinition module, TypeReference il2cppReturn)
    {
        if (il2cppReturn is not GenericInstanceType g) return null;

        // Classify the il2cpp return; flip only the CONCRETE container families THIS pass owns — the write-target
        // rows of List / Dictionary / ValueTuple. Nullable is deferred (an empty il2cpp value-Nullable NREs inside
        // Il2CppInterop's own return-wrapping before the spliced ToManaged runs); Task is TaskFamily's own pass; the
        // IReadOnlyList/IEnumerable read spellings (WriteTarget:false) and arrays (a free op_Implicit) are not
        // standalone-return flips here.
        TypeCorrespondence? corr = _families.Classify(CecilTypeRef.Of(il2cppReturn));
        if (corr is not { IsFlippableContainer: true })
            return null;

        // Nested-return recursion: naturalize each element too, so a container whose element is itself an il2cpp
        // wrapper ((Il2CppReferenceArray<Player>, string) -> (Player[], string)) becomes FULLY natural — never a
        // SHALLOW flip that leaks an inner wrapper. The runtime already materializes the nested shape (Conv recurses
        // MaterializeTuple -> MaterializeArray); this maps the matching type. A non-naturalizable element (a deferred
        // Nullable, a Task, an unknown wrapper) DEFERS the whole return (null), keeping it fully il2cpp.
        var natArgs = new TypeReference[g.GenericArguments.Count];
        for (int i = 0; i < natArgs.Length; i++)
        {
            TypeReference? na = Naturalize(module, g.GenericArguments[i]);
            if (na is null) return null;
            natArgs[i] = na;
        }
        return BclGeneric(module, corr.BclOpenType, natArgs);
    }

    // The Il2CppInterop array-wrapper namespace — a DIFFERENT namespace from the Il2CppSystem families (kept literal).
    // Il2CppReferenceArray<T>/Il2CppStructArray<T> carry the element as their generic arg; Il2CppStringArray is
    // non-generic (its element is always System.String).
    const string ArrayNs = "Il2CppInterop.Runtime.InteropTypes.Arrays.";

    // Is `t` a TOP-LEVEL Il2CppInterop array wrapper (Il2CppReferenceArray<T> / Il2CppStructArray<T> / Il2CppStringArray)?
    // The standalone-array gate ArrayRewriter uses to decide a method return/param or a field is a flippable array — the
    // case the family classify excludes (arrays aren't an Il2CppSystem.* family). Naturalize then maps it to T[].
    public static bool IsArrayWrapper(TypeReference t)
        => (t is GenericInstanceType gi
            && (gi.ElementType.FullName == ArrayNs + "Il2CppReferenceArray`1" || gi.ElementType.FullName == ArrayNs + "Il2CppStructArray`1"))
           || t.FullName == ArrayNs + "Il2CppStringArray";

    // Map an il2cpp element TYPE to its NATURAL BCL form, recursively. Returns null when the element cannot be made
    // FULLY natural (a deferred Nullable, a Task carrier, an unrecognized wrapper) — the caller then defers the whole
    // return rather than emit a half-flip. The IL-seam mirror of the runtime write-side ContainerBridge.Il2CppTypeOf
    // recursion, one direction over. PUBLIC so ArrayRewriter can naturalize a TOP-LEVEL array — the standalone case
    // NaturalReturn's family gate rejects.
    public static TypeReference? Naturalize(ModuleDefinition module, TypeReference t)
    {
        // An il2cpp array wrapper -> a BCL array of the naturalized element (Il2CppReferenceArray<Player> -> Player[],
        // Il2CppStructArray<int> -> int[], Il2CppStringArray -> string[]; recurse for a jagged Player[][]).
        if (TryArrayElement(module, t, out TypeReference? arrElem))
        {
            TypeReference? natElem = Naturalize(module, arrElem!);
            return natElem is null ? null : new ArrayType(natElem);
        }

        // A registered container family (List/Dict/Tuple/Set) -> its BCL open type over naturalized args (recurse).
        if (t is GenericInstanceType g)
        {
            TypeCorrespondence? corr = _families.Classify(CecilTypeRef.Of(t));
            if (corr is not { IsFlippableContainer: true })
                return null;   // a non-flippable generic (Nullable, Task, an IReadOnlyList read spelling) -> defer whole
            var natArgs = new TypeReference[g.GenericArguments.Count];
            for (int i = 0; i < natArgs.Length; i++)
            {
                TypeReference? na = Naturalize(module, g.GenericArguments[i]);
                if (na is null) return null;
                natArgs[i] = na;
            }
            return BclGeneric(module, corr.BclOpenType, natArgs);
        }

        // A leaf — a primitive, string, enum, or a GAME reference/value proxy (already the mod's natural spelling,
        // its identity preserved: Player stays Il2CppToyGame.Player). ImportReference into this module's scope.
        return module.ImportReference(t);
    }

    // Is `t` an Il2CppInterop array wrapper, and if so its ELEMENT type (via out-param)?
    static bool TryArrayElement(ModuleDefinition module, TypeReference t, out TypeReference? elem)
    {
        elem = null;
        if (t is GenericInstanceType gi
            && (gi.ElementType.FullName == ArrayNs + "Il2CppReferenceArray`1"
                || gi.ElementType.FullName == ArrayNs + "Il2CppStructArray`1"))
        {
            elem = gi.GenericArguments[0];
            return true;
        }
        if (t.FullName == ArrayNs + "Il2CppStringArray")
        {
            elem = module.TypeSystem.String;                        // the string array's element is always System.String
            return true;
        }
        return false;
    }

    // A closed BCL generic type ref (bclOpenType<args...>) over the (already-naturalized) elements. ImportReference
    // resolves the open type from the tool's OWN runtime, so its scope is the assembly that actually DEFINES it
    // (System.Private.CoreLib) — NOT module.TypeSystem.CoreLibrary. That distinction is load-critical and NOT the
    // same as Task`1: Task`1 IS in the CoreLibrary facade (System.Runtime), so WrapHelpers can name it there;
    // List`1 / Dictionary`2 / ValueTuple`N are forwarded from a different facade, so naming them in System.Runtime
    // fails to load (TypeLoadException, caught in-game).
    static GenericInstanceType BclGeneric(ModuleDefinition module, Type bclOpenType, params TypeReference[] args)
    {
        var inst = new GenericInstanceType(module.ImportReference(bclOpenType));
        foreach (TypeReference a in args) inst.GenericArguments.Add(module.ImportReference(a));
        return inst;
    }
}
