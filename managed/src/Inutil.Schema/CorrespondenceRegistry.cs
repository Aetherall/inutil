namespace Inutil.Schema;

// The single registry both seams consult via Classify. A family is registered once here (pick the BCL
// counterpart, write one IBridge, register the TypeCorrespondence); nothing else in either seam changes.
//
// The families themselves (Task, Nullable, IEnumerable, …) live in the seam/families layer — their full bridges
// implement the seam conversion interfaces (Cecil/runtime) this pure module must not depend on. This module owns
// the registry MECHANISM and Classify; the families plug in from above.
public sealed class CorrespondenceRegistry
{
    readonly List<TypeCorrespondence> _all = new();

    public CorrespondenceRegistry Register(TypeCorrespondence correspondence)
    {
        _all.Add(correspondence);
        return this;
    }

    public IReadOnlyList<TypeCorrespondence> All => _all;

    // Classify a CLOSED il2cpp type: the first registered family whose anchor name matches AND whose bridge probes
    // true. Returns null when nothing binds — the caller leaves the type wrapper-typed.
    //
    // The two-part test IS the "never hard-bind, always probe" rule: a name match alone is not enough. An open
    // Task`1<!T> has the right anchor name but CanBind rejects the open argument, so Classify returns null — the
    // open-parameter case is handled at the slot level, not by pretending this closed-type classifier can bind it.
    public TypeCorrespondence? Classify(ITypeRef il2cppType)
    {
        string anchor = il2cppType.IsGenericInstance ? il2cppType.ElementFullName : il2cppType.FullName;
        foreach (TypeCorrespondence c in _all)
            if (c.Il2CppFullName == anchor && c.Bridge.CanBind(il2cppType))
                return c;
        return null;
    }

    // The RUNTIME-WRITE lookup: the canonical il2cpp type a value of `kind` materializes into. Returns the
    // WriteTarget row, so ConvKind.List resolves to the concrete List`1 (never a read-only IReadOnlyList`1/
    // IEnumerable`1 spelling). `arity` disambiguates a per-arity family (ValueTuple`N): pass the child count;
    // omit (<0) where unique.
    public TypeCorrespondence? ByConvKind(ConvKind kind, int arity = -1)
    {
        foreach (TypeCorrespondence c in _all)
            if (c.Kind == kind && c.WriteTarget
                && (arity < 0 || c.BclOpenType.GetGenericArguments().Length == arity))
                return c;
        return null;
    }

    // The RUNTIME-READ / SHAPE lookup: the ConvKind a spelled BCL open type classifies to (List<> -> List,
    // IReadOnlyList<> -> List, IEnumerable<> -> Enumerable, ValueTuple<...> -> Tuple, …). Keyed on the BCL open
    // type, unique per row, so first-match is exact.
    public TypeCorrespondence? ByBclOpenType(Type bclOpenType)
    {
        foreach (TypeCorrespondence c in _all)
            if (c.BclOpenType == bclOpenType)
                return c;
        return null;
    }
}
