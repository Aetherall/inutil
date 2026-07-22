using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The seam-specific conversion data one Nullable member flips to: the closed null-aware invoke helper + the natural
// return + the element type. The natural return is System.Nullable<T> for a VALUE-type element, or the BARE proxy T
// (used as a nullable REFERENCE, null == empty) for a REF-bearing one. Opaque to the pure planner (carried in
// MemberPlan.Payload), applied here.
public sealed record NullableFlip(MethodReference Helper, TypeReference NewReturn, TypeReference ValType);

// The value-type Nullable-RETURN family for the IL-rewrite seam, plugged into the SAME VirtualSlotPlanner the Task
// family uses. It supplies the planner's per-member judgment (PlanMember) and applies each member's OWN flip; the
// planner owns grouping / gate / all-or-nothing lockstep.
//
// Unlike Task's ret-tail SPLICE (over Il2CppInterop's own return value), the value-type Nullable flip is a FULL-BODY
// REPLACE: Il2CppInterop's value-type Nullable return is broken BOTH ways (an empty Nullable NREs constructing the
// proxy, a present one reads HasValue=False), so there is no correct value on the stack to splice over — the body is
// abandoned for a call to the null-aware SDK invoke. Because the whole body is discarded, CanApplyFlip has no
// ret-tail constraint (a body with an exception handler still flips fine).
public sealed class NullableFamily : IFamilyPass
{
    static readonly CorrespondenceRegistry _families = Families.Default();

    readonly ModuleDefinition _module;
    readonly WrapHelpers _wrap;

    public NullableFamily(ModuleDefinition module, WrapHelpers wrap) { _module = module; _wrap = wrap; }

    // Grouping candidate: a virtual, non-accessor method whose return is a value-type Nullable — broken
    // (Il2CppSystem.Nullable) OR already-flipped (System.Nullable), so a half-patched slot still groups.
    public static bool IsCandidate(MethodDefinition m)
        => m.IsVirtual && !m.IsGetter && !m.IsSetter
           && (NullableValueElement(m.ReturnType) is not null || NullableRefElement(m.ReturnType) is not null);

    // The NON-virtual counterpart (the simpler pass, no override graph): a non-virtual, non-generic-method value-type
    // OR ref-bearing il2cpp Nullable return. Already-flipped forms are excluded (nothing to do -> idempotent): a flipped
    // value return is System.Nullable (IsIl2CppNullable false), a flipped ref return is the bare proxy (no longer a
    // Nullable at all, so NullableRefElement misses it).
    public static bool IsNonVirtualCandidate(MethodDefinition m)
        => !m.IsVirtual && !m.IsGetter && !m.IsSetter && !m.HasGenericParameters && m.HasBody
           && (IsIl2CppNullable(m.ReturnType) || NullableRefElement(m.ReturnType) is not null);

    // The underlying value type of a value-type Nullable return — either spelling (Il2CppSystem.Nullable<Vec3> or the
    // already-flipped System.Nullable<Vec3>) -> Vec3, or null if not a value-type Nullable. A Nullable<T>'s T is a
    // value type by the CLR constraint; a ref-bearing game value type renders as a CLASS proxy (not Nullable-able), so
    // IsValueType always holds for a real one. Il2CppSystem.Nullable is classified through the registry; the flipped
    // System.Nullable is matched by name (not a registry row).
    static TypeReference? NullableValueElement(TypeReference? rt)
    {
        if (rt is not GenericInstanceType g) return null;
        bool isNullable = g.ElementType.FullName == "System.Nullable`1"
                          || _families.Classify(CecilTypeRef.Of(rt)) is { Kind: ConvKind.Nullable };
        if (!isNullable) return null;
        TypeReference elem = g.GenericArguments[0];
        return elem.IsValueType ? elem : null;
    }

    // The ref-bearing proxy element of an il2cpp Nullable return (a value type carrying a reference — MongoID {string}
    // — which Il2CppInterop renders as a CLASS proxy deriving Il2CppSystem.ValueType), or null. The ref TWIN of
    // NullableValueElement: an il2cpp Nullable's inner is a value type BY CONSTRUCTION, and Il2CppInterop renders a
    // value type as EITHER a VALUETYPE (blittable -> NullableValueElement's arm) OR a ref-bearing CLASS — so within a
    // confirmed il2cpp Nullable, `!elem.IsValueType` IS "a ref-bearing value proxy". System.Nullable<class> is illegal,
    // so it flips to the BARE proxy T used as a nullable reference (null == empty); that target no longer classifies as
    // a Nullable, the natural idempotency.
    static TypeReference? NullableRefElement(TypeReference? rt)
    {
        if (rt is not GenericInstanceType g) return null;
        if (_families.Classify(CecilTypeRef.Of(rt)) is not { Kind: ConvKind.Nullable }) return null;   // il2cpp Nullable only
        TypeReference elem = g.GenericArguments[0];
        return elem.IsValueType ? null : elem;   // value element -> NullableValueElement; class element -> a ref-bearing value proxy
    }

    static bool IsIl2CppNullable(TypeReference? rt) => IsIl2CppNullableParam(rt, out _);

    // A flippable value-type Nullable PARAM: an il2cpp Nullable<T'> (NOT the already-flipped System.Nullable — that
    // classifies to no registry row) whose element is a VALUE type — the underlying `valType` for the flip. A
    // ref-bearing game value type renders as a CLASS proxy (IsValueType=false), so it is excluded here AND fails loud
    // in DematerializeNullable; only blittable/enum inners (int, Vec3, Faction) — which the write direction handles —
    // pass. The PARAM analog of NullableValueElement, restricted to the il2cpp spelling (a param flip is write-only).
    public static bool IsIl2CppNullableParam(TypeReference? rt, out TypeReference valType)
    {
        valType = null!;
        if (rt is not GenericInstanceType g) return false;
        if (_families.Classify(CecilTypeRef.Of(rt)) is not { Kind: ConvKind.Nullable }) return false;
        TypeReference elem = g.GenericArguments[0];
        if (!elem.IsValueType) return false;
        valType = elem;
        return true;
    }

    // A flippable REF-BEARING value-type Nullable PARAM: an il2cpp Nullable<T'> whose element is a ref-bearing value
    // PROXY (a class deriving Il2CppSystem.ValueType — e.g. MongoID). The ref twin of IsIl2CppNullableParam (and its
    // disjoint complement — that arm requires elem.IsValueType). The natural param is the BARE proxy T (a nullable
    // reference, null == empty Nullable), dematerialized at method entry by ValueTypeBridge.RefToNullable — the write
    // mirror of the ref RETURN's InvokeNullableRefReturn.
    public static bool IsIl2CppNullableRefParam(TypeReference? rt, out TypeReference refElem)
    {
        refElem = NullableRefElement(rt)!;
        return refElem is not null;
    }

    static bool IsFlippedNullable(TypeReference? rt)
        => rt is GenericInstanceType g && g.ElementType.FullName == "System.Nullable`1";

    // Classify ONE member's Nullable return -> its own flip. Derived PER MEMBER (from md.ReturnType), never from the
    // slot root — the structural guarantee against a root-derived half-flip.
    public MemberOutcome PlanMember(ISlotMethod member)
    {
        TypeReference rt = ((CecilSlotMethod)member).Definition.ReturnType;
        if (IsFlippedNullable(rt)) return MemberOutcome.AlreadyFlipped();       // System.Nullable already (prior launch)
        if (NullableValueElement(rt) is { } valType)
            return MemberOutcome.Flip(new NullableFlip(
                _module.ImportReference(_wrap.NullableStructReturnClosed(valType)),
                _wrap.SysNullableOf(valType),
                valType));
        // Ref-bearing (MongoID): flip the return to the BARE proxy T (a nullable reference), body-replaced with the
        // null-aware ref invoke. Mirrors the value arm — same full-body replace, only the helper + the natural return
        // differ (T, not System.Nullable<T>). The bare proxy T is already an in-module reference (the return's own
        // element), imported for safety like SysNullableOf does for the value arm.
        if (NullableRefElement(rt) is { } refElem)
            return MemberOutcome.Flip(new NullableFlip(
                _module.ImportReference(_wrap.NullableRefReturnClosed(refElem)),
                _module.ImportReference(refElem),
                refElem));
        return MemberOutcome.Unplannable(DeferReason.OpenGenericMultiMember);   // unreachable (candidate guarantees the shape)
    }

    // The body is FULLY REPLACED, so any concrete body (or an abstract decl) takes the flip — the only bar is the
    // compound case the non-virtual body-replace can't marshal: a param that is an already-natural BCL container
    // (fail loud via a slot defer, never a silent mis-marshal). No ToyGame fixture has that combo.
    public bool CanApplyFlip(ISlotMethod member)
        => !((CecilSlotMethod)member).Definition.Parameters
            .Any(p => p.ParameterType.FullName.StartsWith("System.Collections.", StringComparison.Ordinal));

    // Apply one member's OWN plan: replace its (broken) body with the null-aware invoke, then set its flipped return.
    public void Apply(CecilSlotMethod member, NullableFlip flip)
    {
        MethodDefinition md = member.Definition;
        if (IsFlippedNullable(md.ReturnType)) return;              // idempotent: never re-flip
        if (md.HasBody) ReplaceBody(md, flip);
        md.ReturnType = flip.NewReturn;
    }

    // Replace the body with:  return ValueTypeBridge.InvokeNullableStructReturn<valType>(this, "<name>",
    //   new object[]{ (object)p0, ... });  — each value-type param boxed, reference params as-is. The native method
    // is resolved by name+argc inside the helper on the object's ACTUAL class, so a virtual member naturally dispatches
    // to the most-derived override; the discarded body's NativeMethodInfoPtr is not needed.
    void ReplaceBody(MethodDefinition m, NullableFlip flip)
    {
        MethodBody body = m.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        ILProcessor il = body.GetILProcessor();

        il.Emit(OpCodes.Ldarg_0);                                  // this (the Il2CppObjectBase proxy)
        il.Emit(OpCodes.Ldstr, m.Name);                            // methodName
        il.Emit(OpCodes.Ldc_I4, m.Parameters.Count);
        il.Emit(OpCodes.Newarr, _module.TypeSystem.Object);        // new object[paramCount]
        for (int i = 0; i < m.Parameters.Count; i++)
        {
            ParameterDefinition p = m.Parameters[i];
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldarg, p);
            if (p.ParameterType.IsValueType) il.Emit(OpCodes.Box, _module.ImportReference(p.ParameterType));
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Call, flip.Helper);
        il.Emit(OpCodes.Ret);
    }
}
