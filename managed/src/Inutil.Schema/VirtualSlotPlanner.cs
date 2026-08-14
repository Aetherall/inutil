namespace Inutil.Schema;

// The single highest-value extraction (docs/contribution/architecture/10-schema.md): the one implementation of "how to plan a
// virtual-slot flip". Every family that has virtual slots calls Plan and splices per member; there is no code
// path that CAN derive one plan for the whole group (the bug class that once derived ONE plan from the group's
// root and applied it to every member), so that bug cannot be written again.
//
// The planner owns exactly the structural, shared parts:
//   * grouping     — walk the full base chain to the true slot root, group by (name, params) ignoring return type;
//   * the gate     — a slot is flippable iff its root is a NewSlot in a non-framework (game) module (same-module
//                    and cross-module-game roots both fine, framework roots not);
//   * consistency  — a slot flips ALL its members or none (a half-flip fails to load); one member's own plan
//                    never leaks into a sibling.
// Everything family-specific (what a member flips to, whether its body/shape allows it, collisions) is the
// IFamilyPass's call.
public sealed class VirtualSlotPlanner
{
    // Plan every slot reachable from `candidates` — the family's already-shape-filtered virtual methods across
    // one module. Returns one SlotPlan per distinct slot (flipped or deferred, with the reason).
    public IReadOnlyList<SlotPlan> Plan(IEnumerable<ISlotMethod> candidates, IFamilyPass family)
    {
        // Group candidates by their resolved slot root. The root's own ISlotMethod instance is the key, so
        // every override that climbs to it lands in the same bucket. Gate status is a property of the root,
        // computed once.
        var members = new Dictionary<ISlotMethod, List<ISlotMethod>>();
        var gate = new Dictionary<ISlotMethod, (bool flippable, DeferReason? defer)>();
        foreach (ISlotMethod m in candidates)
        {
            (ISlotMethod root, bool flippable, DeferReason? defer) = ResolveSlot(m);
            if (!members.TryGetValue(root, out List<ISlotMethod>? list))
            {
                members[root] = list = new List<ISlotMethod>();
                gate[root] = (flippable, defer);
            }
            list.Add(m);
        }

        var plans = new List<SlotPlan>(members.Count);
        foreach (KeyValuePair<ISlotMethod, List<ISlotMethod>> kv in members)
        {
            ISlotMethod root = kv.Key;
            List<ISlotMethod> group = kv.Value;
            (bool flippable, DeferReason? defer) = gate[root];
            plans.Add(flippable ? PlanSlot(root, group, family)
                                : SlotPlan.Deferred(root, group, defer!.Value));
        }
        return plans;
    }

    // Resolve `m`'s slot root by walking the WHOLE base chain (not just the immediate base), so an override
    // several types below its root — with intermediate bases that inherit the virtual without redeclaring it —
    // still roots to the one true declaration. Then gate: a NewSlot in a game module is flippable (same- or
    // cross-module — the sibling module gets the same pass, so it flips in lockstep); a NewSlot in a framework
    // module is unverified (we never flip framework virtuals); anything else has no visible NewSlot origin and is
    // external.
    static (ISlotMethod root, bool flippable, DeferReason? defer) ResolveSlot(ISlotMethod m)
    {
        ISlotMethod root = m;
        for (ISlotType? bt = m.DeclaringType.BaseType; bt is not null; bt = bt.BaseType)
        {
            ISlotMethod? baseM = null;
            foreach (ISlotMethod cand in bt.VirtualMethods)
                if (cand.Name == root.Name && SameParams(cand, root)) { baseM = cand; break; }
            if (baseM is not null) root = baseM;   // this ancestor (re)declares the slot — climb to it
            // else: an intermediate that inherits without redeclaring — keep walking up to the true root
        }

        if (!root.IsNewSlot) return (root, false, DeferReason.ExternalRoot);
        if (root.DeclaringType.IsFrameworkModule) return (root, false, DeferReason.CrossModuleRootUnverified);
        return (root, true, null);
    }

    // Same parameter list by type identity — the slot-identity check (return type deliberately ignored, it's
    // what we flip).
    static bool SameParams(ISlotMethod a, ISlotMethod b)
    {
        if (a.ParameterTypeNames.Count != b.ParameterTypeNames.Count) return false;
        for (int i = 0; i < a.ParameterTypeNames.Count; i++)
            if (a.ParameterTypeNames[i] != b.ParameterTypeNames[i]) return false;
        return true;
    }

    // Plan a flippable slot. All-or-nothing, in three consistency gates, then per-member plans:
    //   1. any member the family can't classify (unclosable/mixed shape) -> defer the whole slot;
    //   2. any member whose body can't take the flip -> defer (ExceptionHandledBody);
    //   3. any flip that would collide with a sibling overload -> defer (CollisionWithGeneratedSibling).
    // Only if all three pass does the slot flip — and each member carries ITS OWN payload, never the root's.
    static SlotPlan PlanSlot(ISlotMethod root, List<ISlotMethod> group, IFamilyPass family)
    {
        var outcomes = new Dictionary<ISlotMethod, MemberOutcome>(group.Count);
        foreach (ISlotMethod m in group) outcomes[m] = family.PlanMember(m);

        // 1. unplannable shape anywhere -> defer whole slot with that member's reason.
        foreach (ISlotMethod m in group)
            if (outcomes[m].Kind == MemberOutcomeKind.Unplannable)
                return SlotPlan.Deferred(root, group, outcomes[m].Reason!.Value);

        // 2. a flip whose body can't be rewritten -> defer. (AlreadyFlipped members need no rewrite, so they're
        //    exempt — the check is only for members we'd actually splice.)
        foreach (ISlotMethod m in group)
            if (outcomes[m].Kind == MemberOutcomeKind.Flip && !family.CanApplyFlip(m))
                return SlotPlan.Deferred(root, group, DeferReason.ExceptionHandledBody);

        // 3. a flip that collides with a sibling overload -> defer.
        foreach (ISlotMethod m in group)
            if (outcomes[m].Kind == MemberOutcomeKind.Flip && family.CollidesWithSibling(m, outcomes[m].Payload!))
                return SlotPlan.Deferred(root, group, DeferReason.CollisionWithGeneratedSibling);

        // Per-member plans. Each Flip member gets ITS OWN payload; AlreadyFlipped members are in the slot with a
        // null plan (no-op) so the slot stays consistent and re-running is idempotent.
        var perMember = new Dictionary<ISlotMethod, MemberPlan?>(group.Count);
        foreach (ISlotMethod m in group)
            perMember[m] = outcomes[m].Kind == MemberOutcomeKind.Flip ? new MemberPlan(outcomes[m].Payload!) : null;
        return SlotPlan.Planned(root, group, perMember);
    }
}
