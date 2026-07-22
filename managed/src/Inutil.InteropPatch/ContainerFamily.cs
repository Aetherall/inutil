using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The seam-specific conversion data one container-returning member flips to: the ToManaged materializer to splice
// + the natural BCL return type. Opaque to the pure planner (carried in MemberPlan.Payload), applied here.
public sealed record ContainerFlipPlan(MethodReference Helper, TypeReference NewReturn);

// The container-RETURN family for the IL-rewrite seam, plugged into the SAME VirtualSlotPlanner the Task and Nullable
// families use. It supplies the planner's per-member judgment (PlanMember) and applies each member's OWN flip; the
// planner owns grouping / gate / all-or-nothing lockstep.
//
// Like Task's ret-tail SPLICE (and unlike value-Nullable's full-body replace), the container flip rides Il2CppInterop's
// own return value: the body leaves the il2cpp container on the stack and Il2CppMarshal.ToManaged<natural> materializes
// it to the BCL shape at the ret tail. WHAT a member flips to — per member, from ITS OWN return type, incl. nested-
// element naturalization and defer-on-non-naturalizable — is ContainerFlip.NaturalReturn's; none of it is root-derived.
public sealed class ContainerFamily : IFamilyPass
{
    static readonly CorrespondenceRegistry _families = Families.Default();

    // The already-flipped BCL container spellings (System.Collections.Generic.List`1 / Dictionary`2 /
    // System.ValueTuple`N) — DERIVED from the registry's write-target rows, not inline constants, so a new container
    // family is one Register call in Families.cs. A member already wearing one of these was flipped on a prior launch
    // (an idempotent no-op / AlreadyFlipped), so a half-patched slot still GROUPS and reaches consistency.
    static readonly HashSet<string> _flippedNames =
        _families.All
            .Where(c => c.IsFlippableContainer)
            .Select(c => c.BclOpenType.FullName!)
            .ToHashSet(StringComparer.Ordinal);

    readonly ModuleDefinition _module;
    readonly WrapHelpers _wrap;

    public ContainerFamily(ModuleDefinition module, WrapHelpers wrap) { _module = module; _wrap = wrap; }

    // Grouping candidate: a virtual, non-accessor, non-generic-METHOD whose return is a container this family owns —
    // an il2cpp List/Dict/Tuple (flippable) OR the already-flipped BCL spelling (a half-patched slot must still group).
    // Generic METHODS (a `T`-parameterized element) are deferred elsewhere, same as the Task pass.
    public static bool IsCandidate(MethodDefinition m)
        => m.IsVirtual && !m.IsGetter && !m.IsSetter && !m.HasGenericParameters && IsContainerReturn(m.ReturnType);

    // The NON-virtual counterpart (simpler — no override graph, so no planner / lockstep). Same shape gate;
    // PlanMember/Apply are shared, so the two paths convert through ONE implementation (no drift).
    public static bool IsNonVirtualCandidate(MethodDefinition m)
        => !m.IsVirtual && !m.IsGetter && !m.IsSetter && !m.HasGenericParameters && IsContainerReturn(m.ReturnType);

    // Is `rt` a container return this family owns — either the il2cpp List/Dict/Tuple WRITE-TARGET spelling, or the
    // already-flipped BCL spelling? Only the OUTER shape is decided here (candidacy); whether the ELEMENTS can be made
    // fully natural is a per-member call in PlanMember (a non-naturalizable nested element defers the whole slot).
    static bool IsContainerReturn(TypeReference? rt)
    {
        if (rt is not GenericInstanceType g) return false;
        if (_flippedNames.Contains(g.ElementType.FullName)) return true;                 // already-flipped BCL container
        TypeCorrespondence? corr = _families.Classify(CecilTypeRef.Of(rt));              // il2cpp container
        return corr is { IsFlippableContainer: true };
    }

    static bool IsFlippedContainer(TypeReference? rt)
        => rt is GenericInstanceType g && _flippedNames.Contains(g.ElementType.FullName);

    // Classify ONE member's container return -> its own flip. Derived PER MEMBER (from md.ReturnType), never from the
    // slot root — the structural guarantee against a root-derived half-flip.
    public MemberOutcome PlanMember(ISlotMethod member)
    {
        TypeReference rt = ((CecilSlotMethod)member).Definition.ReturnType;
        if (IsFlippedContainer(rt)) return MemberOutcome.AlreadyFlipped();               // System.* container already (prior launch)
        TypeReference? natural = ContainerFlip.NaturalReturn(_module, rt);
        if (natural is not null)
            return MemberOutcome.Flip(new ContainerFlipPlan(_wrap.MarshalToManagedClosed(natural), natural));
        // A container whose element cannot be made FULLY natural (a nested Nullable / Task / unknown wrapper) has no
        // plan — defer the whole slot rather than emit a half-flip that leaks an inner wrapper.
        return MemberOutcome.Unplannable(DeferReason.OpenGenericMultiMember);
    }

    // Can this member's body take the return-tail rewrite? Abstract decls and already-flipped bodies need none;
    // otherwise a ret and no exception-handler region (the shared BodyRewrite check — a ret inside a protected block
    // can't br out to the wrap tail).
    public bool CanApplyFlip(ISlotMethod member)
    {
        MethodDefinition md = ((CecilSlotMethod)member).Definition;
        if (!md.HasBody || IsFlippedContainer(md.ReturnType)) return true;
        return BodyRewrite.CanRewriteReturnTail(member);
    }

    // Apply one member's OWN plan: route its ret-tail through ToManaged, then set its flipped natural return type.
    public void Apply(CecilSlotMethod member, ContainerFlipPlan flip)
    {
        MethodDefinition md = member.Definition;
        if (IsFlippedContainer(md.ReturnType)) return;          // idempotent: never re-flip
        if (md.HasBody) ReturnTail.RouteThroughHelper(_module, md, flip.Helper);
        md.ReturnType = flip.NewReturn;
    }
}
