using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The seam-specific conversion data one Task member flips to: the wrap helper to splice + the flipped return
// type. Opaque to the pure planner (it carries it in MemberPlan.Payload), applied here.
public sealed record TaskFlip(MethodReference Helper, TypeReference NewReturn);

// The Task family for the IL-rewrite seam. It supplies the planner's per-member judgment (PlanMember) over real
// Cecil methods, and applies each member's OWN flip once a slot is planned. The pure VirtualSlotPlanner owns
// grouping / gate / all-or-nothing consistency; this owns only "what does a Task-returning member flip to, and
// can its body take the flip".
public sealed class TaskFamily : IFamilyPass
{
    // The Task family's il2cpp/BCL name pair comes from the single registry, not inline constants. The two Task rows —
    // non-generic (arity 0) and Task`1 (arity 1) — carry both spellings (Il2CppFullName + the BclOpenType whose
    // FullName is the System.* name), so the four names are one registration in Families.cs.
    static readonly CorrespondenceRegistry _families = Families.Default();
    static readonly string Il2CppTask  = _families.ByConvKind(ConvKind.Task, 0)!.Il2CppFullName;
    static readonly string SysTask     = _families.ByConvKind(ConvKind.Task, 0)!.BclOpenType.FullName!;
    static readonly string Il2CppTaskT = _families.ByConvKind(ConvKind.Task, 1)!.Il2CppFullName;
    static readonly string SysTaskT    = _families.ByConvKind(ConvKind.Task, 1)!.BclOpenType.FullName!;

    readonly ModuleDefinition _module;
    readonly WrapHelpers _wrap;

    public TaskFamily(ModuleDefinition module, WrapHelpers wrap) { _module = module; _wrap = wrap; }

    // The grouping candidate set for this module: a virtual, non-accessor method whose return is any Task shape
    // (broken OR already-flipped — a half-patched slot from a prior launch must still group).
    public static bool IsCandidate(MethodDefinition m)
        => m.IsVirtual && !m.IsGetter && !m.IsSetter && IsTaskReturn(m.ReturnType);

    // The NON-virtual counterpart (a separate, simpler pass): a non-virtual, non-accessor Task-returning method — no
    // override graph, so no planner/lockstep. Generic METHODS are flipped here too: same per-member judgment
    // (PlanMember) as the virtual path, whose Task element may be the method's own !!0 (LoadTyped<T>) or a generic
    // instance CONTAINING it (Send<T> : Task<Result<!!0>>) — both preserved raw by
    // WrapHelpers.ImportPreservingGenericParams.
    public static bool IsNonVirtualCandidate(MethodDefinition m)
        => !m.IsVirtual && !m.IsGetter && !m.IsSetter && IsTaskReturn(m.ReturnType);

    public static bool IsTaskReturn(TypeReference? rt)
        => rt != null && (rt.FullName == Il2CppTask || rt.FullName == SysTask
           || (rt is GenericInstanceType g && (g.ElementType.FullName == Il2CppTaskT || g.ElementType.FullName == SysTaskT)));

    static bool IsFlippedTask(TypeReference? rt)
        => rt != null && (rt.FullName == SysTask || (rt is GenericInstanceType g && g.ElementType.FullName == SysTaskT));

    // Classify ONE member's Task return -> its own flip. Derived PER MEMBER (from md.ReturnType), never from the
    // slot root — the structural guarantee against a root-derived half-flip.
    public MemberOutcome PlanMember(ISlotMethod member)
    {
        TypeReference rt = ((CecilSlotMethod)member).Definition.ReturnType;

        if (IsFlippedTask(rt)) return MemberOutcome.AlreadyFlipped();          // System.Task[<X>] already (prior launch)

        if (rt.FullName == Il2CppTask)                                          // non-generic Task
            return MemberOutcome.Flip(new TaskFlip(_wrap.WrapTask, _wrap.MakeSysTask()));

        if (rt is GenericInstanceType g && g.ElementType.FullName == Il2CppTaskT)
        {
            // The Task element `x`: a bare own-param (!!0), a concrete type, or a generic instance CONTAINING the
            // own-param (Result<!!0>). WrapTaskTClosed / MakeSysTaskT route each through ImportPreservingGenericParams
            // (import the element, keep any own-param raw), so PlanMember no longer needs the raw/import boolean —
            // which was WRONG for the containing case (x is not a bare param, so it took the import path and stripped
            // !!0's owner).
            TypeReference x = g.GenericArguments[0];
            return MemberOutcome.Flip(new TaskFlip(_wrap.WrapTaskTClosed(x), _wrap.MakeSysTaskT(x)));
        }

        // The candidate filter guarantees a Task shape, so this is unreachable; defer defensively.
        return MemberOutcome.Unplannable(DeferReason.OpenGenericMultiMember);
    }

    // Can this member's body take the return-tail rewrite? Abstract decls and already-flipped bodies need none;
    // otherwise it must have a ret and no exception-handler region (shared BodyRewrite check).
    public bool CanApplyFlip(ISlotMethod member)
    {
        MethodDefinition md = ((CecilSlotMethod)member).Definition;
        if (!md.HasBody || IsFlippedTask(md.ReturnType)) return true;
        return BodyRewrite.CanRewriteReturnTail(member);
    }

    // Apply one member's OWN plan: route its ret-tail through its wrap helper, then set its flipped return type.
    public void Apply(CecilSlotMethod member, TaskFlip flip)
    {
        MethodDefinition md = member.Definition;
        if (IsFlippedTask(md.ReturnType)) return;          // idempotent: never double-wrap
        if (md.HasBody) ReturnTail.RouteThroughHelper(_module, md, flip.Helper);
        md.ReturnType = flip.NewReturn;
    }
}
