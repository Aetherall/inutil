using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The ONE place that answers, for a proxy method PARAMETER: is it flippable to a natural BCL type, and by which
// converter. It unifies the three param families the interop-patch owns — container (List/Dict/Set/Tuple),
// value-Nullable, and delegate (Action/Func) — behind one call, so the param pass (virtual + non-virtual) drives ONE
// detection and adding a param family is one arm HERE, never a fourth rewriter. Each family is disjoint by il2cpp
// shape, so the order of the checks is presentational.
public static class ParamFlipResolver
{
    static readonly CorrespondenceRegistry _families = Families.Default();

    // A cheap SHAPE gate for candidacy (no module / WrapHelpers): does this param's il2cpp type belong to a flippable
    // param family at all? A superset of Resolve — a delegate wrapper matched here may still fail Resolve if
    // unresolvable, and a container may have a non-naturalizable element — the real work + the honest null is Resolve's.
    public static bool IsFlippableShape(TypeReference paramType)
    {
        TypeCorrespondence? corr = _families.Classify(CecilTypeRef.Of(paramType));
        if (corr is null) return false;
        if (corr.IsFlippableContainer) return true;                         // List / Dict / Set / Tuple
        if (corr.Kind == ConvKind.Delegate) return true;                    // Action / Func
        // value-Nullable: a VALUE-type element flips to System.Nullable<T>; a REF-bearing element (a class proxy, e.g.
        // MongoID) flips to the bare proxy T used as a nullable reference. Both are param-flippable — Resolve's two arms.
        return NullableFamily.IsIl2CppNullableParam(paramType, out _)
               || NullableFamily.IsIl2CppNullableRefParam(paramType, out _);
    }

    // Resolve a flippable param to (natural BCL type, converter to splice at method entry). The converter is either
    // Il2CppMarshal.ToIl2CppTyped<natural> (container / Nullable — returns object, castclass'd back to the il2cpp type
    // by ParamFlip.Splice) or the delegate wrapper's OWN op_Implicit (returns the exact il2cpp type, so the castclass
    // is a verifiable no-op). Returns null for a param that is not flippable OR cannot be made FULLY natural (a
    // non-naturalizable container element, an unresolvable delegate wrapper) — the param is then left wrapper-typed.
    // Unlike a RETURN (where a half-natural container would leak an inner wrapper — a lie deferred by the return
    // family), a param left wrapper-typed is SAFE: the modder spells the il2cpp type or hits a fail-loud at the seam.
    public static (TypeReference natural, MethodReference converter)? Resolve(ModuleDefinition module, WrapHelpers wrap, TypeReference paramType)
    {
        // Container (List / Dict / Set / Tuple) — the same il2cpp<->BCL map as the return flip, closed the WRITE way.
        TypeReference? container = ContainerFlip.NaturalReturn(module, paramType);
        if (container is not null) return (container, wrap.MarshalToIl2CppClosed(container));

        // value-Nullable — an il2cpp Nullable<T'> whose element is a value type -> System.Nullable<T>.
        if (NullableFamily.IsIl2CppNullableParam(paramType, out TypeReference valType))
        {
            TypeReference natural = wrap.SysNullableOf(valType);
            return (natural, wrap.MarshalToIl2CppClosed(natural));
        }

        // ref-bearing value-Nullable — an il2cpp Nullable<T'> whose element is a ref-bearing value PROXY (a class, e.g.
        // MongoID) -> the BARE proxy T (a nullable reference). Its dematerializer is the DEDICATED ValueTypeBridge.
        // RefToNullable, NOT the generic ToIl2CppTyped: System.Nullable<class> can't be spelled, so the natural type
        // carries no Nullable-ness for the Conv tree to act on — the helper rebuilds the il2cpp Nullable<T> box itself.
        if (NullableFamily.IsIl2CppNullableRefParam(paramType, out TypeReference refElem))
            return (refElem, wrap.NullableRefToIl2CppClosed(refElem));

        // Delegate (Action / Func) — the wrapper's own op_Implicit is the converter (not the generic Il2CppMarshal helper).
        if (DelegateFamily.TryResolve(module, paramType, out TypeReference dNatural, out MethodReference opImplicit))
            return (dNatural, opImplicit);

        return null;
    }
}
