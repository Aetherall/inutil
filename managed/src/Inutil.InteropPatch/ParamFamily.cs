using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// One param flip a member applies: the param INDEX + its natural type + the converter to splice at entry. A method
// may flip SEVERAL params, so a member's payload is a list of these (opaque to the planner, carried in MemberPlan).
public sealed record ParamFlipItem(int Index, TypeReference Natural, MethodReference Converter);
public sealed record ParamFlipPlan(IReadOnlyList<ParamFlipItem> Items);

// The unified PARAM family for the IL-rewrite seam (the BclToIl2Cpp direction) — the ONE param-flip engine across all
// three param families (container / value-Nullable / delegate), plugged into the SAME VirtualSlotPlanner the return
// families use. ParamFlipResolver is the one detection, ParamFlip.Splice the one mechanism, and this family supplies
// the planner's per-member judgment for BOTH the virtual (lockstep) and non-virtual paths — so a container/Nullable/
// delegate param converts through ONE implementation whether on a vtable slot or a plain method (no drift).
//
// Why the planner is needed even though a param slot is trivially lockstep-consistent (all members share IDENTICAL
// param types, so each flips the SAME way): the planner's FRAMEWORK-ROOT GATE. An override of a UnityEngine /
// Il2Cppmscorlib virtual must NOT flip (we cannot flip the framework base to match) — ResolveSlot roots it to the
// framework decl and defers the whole slot. And a flipped param simply stops being a candidate (ParamFlipResolver
// returns null for System.*), so a re-run is a clean no-op — no AlreadyFlipped machinery needed.
public sealed class ParamFamily : IFamilyPass
{
    readonly ModuleDefinition _module;
    readonly WrapHelpers _wrap;

    public ParamFamily(ModuleDefinition module, WrapHelpers wrap) { _module = module; _wrap = wrap; }

    // Grouping candidate: a virtual, non-accessor, non-generic-METHOD with a body and at least one param whose il2cpp
    // SHAPE is flippable. The non-virtual twin is the same sans IsVirtual. Shape is a superset of Resolve; PlanMember
    // does the real resolve and a member with no actually-resolvable param becomes a no-op (never a defer).
    public static bool IsCandidate(MethodDefinition m) => Base(m) && m.IsVirtual;
    public static bool IsNonVirtualCandidate(MethodDefinition m) => Base(m) && !m.IsVirtual;
    static bool Base(MethodDefinition m)
        => !m.IsGetter && !m.IsSetter && !m.HasGenericParameters && m.HasBody
           && m.Parameters.Any(p => ParamFlipResolver.IsFlippableShape(p.ParameterType));

    // Classify ONE member's params -> its own set of flips. Derived PER MEMBER (from md.Parameters), never from the
    // slot root — the structural guarantee against a root-derived half-flip. A member with no actually-resolvable param
    // (a shape-flippable delegate whose wrapper won't resolve, a non-naturalizable container element) yields no items
    // -> a no-op member (AlreadyFlipped keeps the slot consistent, never a defer: a param left wrapper-typed is safe).
    public MemberOutcome PlanMember(ISlotMethod member)
    {
        MethodDefinition md = ((CecilSlotMethod)member).Definition;
        var items = new List<ParamFlipItem>();
        for (int i = 0; i < md.Parameters.Count; i++)
            if (ParamFlipResolver.Resolve(_module, _wrap, md.Parameters[i].ParameterType) is { } f)
                items.Add(new ParamFlipItem(i, f.natural, f.converter));
        return items.Count > 0 ? MemberOutcome.Flip(new ParamFlipPlan(items)) : MemberOutcome.AlreadyFlipped();
    }

    // A flippable param is an Il2CppSystem container / Nullable / Action-Func CLASS proxy, loaded by ldarg (never
    // ldarga/starg — Il2CppInterop's generated bodies marshal-and-forward a reference param, they do not reassign or
    // address-of it), so ParamFlip's ldarg redirect covers every use. No ret-tail constraint (this rewrites method
    // ENTRY, not the return), so a body with an exception handler flips fine.
    public bool CanApplyFlip(ISlotMethod member) => true;

    // Collision guard: would flipping this member to the payload's natural signature collide with an EXISTING sibling
    // overload of the same name on the same type? A param flip CHANGES the signature (unlike a return), so a sibling
    // already wearing the post-flip signature would make the flipped method a duplicate — defer the slot.
    public bool CollidesWithSibling(ISlotMethod member, object payload)
    {
        MethodDefinition md = ((CecilSlotMethod)member).Definition;
        string[] after = md.Parameters.Select(p => p.ParameterType.FullName).ToArray();
        foreach (ParamFlipItem it in ((ParamFlipPlan)payload).Items) after[it.Index] = it.Natural.FullName;
        foreach (MethodDefinition sib in md.DeclaringType.Methods)
        {
            if (ReferenceEquals(sib, md) || sib.Name != md.Name || sib.Parameters.Count != after.Length) continue;
            bool same = true;
            for (int i = 0; i < after.Length; i++)
                if (sib.Parameters[i].ParameterType.FullName != after[i]) { same = false; break; }
            if (same) return true;
        }
        return false;
    }

    // Apply one member's OWN plan: splice each param's entry dematerialization (the shared ParamFlip.Splice), then set
    // its flipped natural type. il2cppType is the param's CURRENT (pre-flip) type; multiple splices compose (each
    // prepends its own convert + stloc and redirects only its OWN ldargs, so the order between params is immaterial).
    public void Apply(CecilSlotMethod member, ParamFlipPlan plan)
    {
        MethodDefinition md = member.Definition;
        foreach (ParamFlipItem item in plan.Items)
        {
            ParameterDefinition p = md.Parameters[item.Index];
            TypeReference il2cppType = p.ParameterType;
            ParamFlip.Splice(_module, md, p, il2cppType, item.Converter);
            p.ParameterType = item.Natural;
        }
    }
}
