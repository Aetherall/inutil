using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// What one VIRTUAL Nullable accessor flips to. `Converter` is set only for a setter (the entry dematerializer
// ParamFlip.Splice calls); a getter needs none — its broken `newobj Nullable<T>(ptr)` tail is swapped for the
// null-aware read, exactly as in the non-virtual arm.
public sealed record NullableAccessorFlip(TypeReference Natural, MethodReference? Converter, TypeReference Element, bool RefBearing);

// The VIRTUAL Nullable-accessor family: per-member judgment for get_/set_ accessors whose type is an il2cpp
// Nullable, plugged into the SAME VirtualSlotPlanner every other virtual family uses (grouping by slot root, the
// game-module gate, slot-wide all-or-nothing).
//
// A virtual accessor is always METHOD-backed — Il2CppInterop's field wrappers (<X>k__BackingField) are never
// virtual — so both arms are the method-backed mechanics the non-virtual pass already proved: getter TAIL-SWAP,
// setter PARAM FLIP. What virtual adds is not new mechanics but LOCKSTEP, and of a wider kind than any other family
// here needs: a property is THREE things that must agree (get_, set_, and the property's own type) across EVERY
// type in the override graph. The planner enforces lockstep per slot; get_X and set_X are SEPARATE slots, so the
// caller must additionally couple them — see NullableFieldRewriter's virtual phase.
public sealed class NullableAccessorFamily : IFamilyPass
{
    static readonly CorrespondenceRegistry _families = Families.Default();

    readonly ModuleDefinition _module;
    readonly WrapHelpers _wrap;

    public NullableAccessorFamily(ModuleDefinition module, WrapHelpers wrap) { _module = module; _wrap = wrap; }

    // A candidate accessor: virtual, and typed il2cpp Nullable on the side that carries the property's type —
    // the getter's return or the setter's single param. Already-flipped forms do not classify (idempotency).
    public static bool IsCandidate(MethodDefinition m)
        => m.IsVirtual
           && ((m.IsGetter && IsIl2CppNullable(m.ReturnType))
               || (m.IsSetter && m.Parameters.Count == 1 && IsIl2CppNullable(m.Parameters[0].ParameterType)));

    public MemberOutcome PlanMember(ISlotMethod member)
    {
        MethodDefinition md = ((CecilSlotMethod)member).Definition;
        TypeReference il2cppType = md.IsGetter ? md.ReturnType : md.Parameters[0].ParameterType;
        if (!IsIl2CppNullable(il2cppType)) return MemberOutcome.AlreadyFlipped();

        TypeReference elem = ((GenericInstanceType)il2cppType).GenericArguments[0];
        if (elem.IsGenericParameter) return MemberOutcome.Unplannable(DeferReason.OpenGenericMultiMember);

        // Same discriminator as every other Nullable arm: within a confirmed il2cpp Nullable, a NON-value-type
        // element is a ref-bearing value proxy, whose natural spelling is the BARE proxy (System.Nullable<class>
        // is illegal), not System.Nullable<T>.
        bool refBearing = !elem.IsValueType;
        TypeReference natural = refBearing ? _module.ImportReference(elem) : _wrap.SysNullableOf(elem);

        if (md.IsGetter)
            return MemberOutcome.Flip(new NullableAccessorFlip(natural, null, elem, refBearing));

        // The setter takes the ordinary param flip; if the resolver cannot produce a converter there is nothing to
        // splice, so the whole slot defers rather than half-flipping.
        if (ParamFlipResolver.Resolve(_module, _wrap, il2cppType) is not { } resolved)
            return MemberOutcome.Unplannable(DeferReason.OpenGenericMultiMember);
        if (resolved.natural.FullName != natural.FullName)
            return MemberOutcome.Unplannable(DeferReason.OpenGenericMultiMember);   // two derivations disagree -> never half-flip
        return MemberOutcome.Flip(new NullableAccessorFlip(natural, resolved.converter, elem, refBearing));
    }

    // A getter must have the broken `newobj Nullable<T>(ptr)` tail to swap (an abstract/bodyless accessor has
    // nothing to rewrite but still flips its signature — the planner keeps the slot consistent). A setter must
    // have a body and a load of its value param to splice in front of.
    public bool CanApplyFlip(ISlotMethod member)
    {
        MethodDefinition md = ((CecilSlotMethod)member).Definition;
        if (!md.HasBody) return true;                                   // signature-only flip; body comes from elsewhere
        if (md.IsGetter) return NullableFieldRewriter.FindNullableNewobjTail(md) is not null;
        return md.Body.Instructions.Any(i => ParamFlip.LoadsParam(i, md, md.Parameters[0]));
    }

    // Apply ONE member's own plan. Getter: swap the tail, then the return type. Setter: splice the entry
    // dematerialization, then the param type. The property's own type is set by the caller, which owns the
    // get/set/property coupling.
    public void Apply(CecilSlotMethod member, NullableAccessorFlip flip)
    {
        MethodDefinition md = member.Definition;
        if (md.IsGetter)
        {
            if (md.HasBody && NullableFieldRewriter.FindNullableNewobjTail(md) is { } newobj)
            {
                newobj.OpCode = Mono.Cecil.Cil.OpCodes.Call;
                newobj.Operand = flip.RefBearing ? _wrap.BoxedToRefNullableClosed(flip.Element)
                                                 : _wrap.BoxedToNullableClosed(flip.Element);
            }
            md.ReturnType = flip.Natural;
            return;
        }

        ParameterDefinition p = md.Parameters[0];
        if (md.HasBody) ParamFlip.Splice(_module, md, p, p.ParameterType, flip.Converter!);
        p.ParameterType = flip.Natural;
    }

    static bool IsIl2CppNullable(TypeReference? t)
        => t is GenericInstanceType && _families.Classify(CecilTypeRef.Of(t)) is { Kind: ConvKind.Nullable };
}
