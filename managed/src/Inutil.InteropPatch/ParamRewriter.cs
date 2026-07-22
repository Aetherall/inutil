using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The unified PARAM pass (the IL-rewrite boundary): flip every game proxy method PARAMETER that is an il2cpp
// container / value-Nullable / delegate to its NATURAL BCL type, dematerializing at method entry so the body runs
// unchanged. Replaces the three separate non-virtual param rewriters with ONE pass over ONE family (ParamFamily) and
// ONE detection (ParamFlipResolver): the VIRTUAL slots go through the shared VirtualSlotPlanner (grouping +
// framework-root gate + lockstep), and the NON-virtual params flip on their own via the family's SAME per-member
// judgment. IDEMPOTENT: a flipped param is System.* (ParamFlipResolver returns null), so it stops being a candidate
// and a re-run flips nothing.
public sealed class ParamRewriter
{
    // Plan this module's virtual param slots WITHOUT mutating it — exposed so a test can assert grouping / lockstep.
    public IReadOnlyList<SlotPlan> Plan(ModuleDefinition module) => PlanInternal(module).plans;

    (ParamFamily family, IReadOnlyList<SlotPlan> plans) PlanInternal(ModuleDefinition module)
    {
        var projector = new CecilProjector();
        var family = new ParamFamily(module, new WrapHelpers(module));
        var candidates = module.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(ParamFamily.IsCandidate)
            .Select(m => (ISlotMethod)projector.Method(m))
            .ToList();
        return (family, new VirtualSlotPlanner().Plan(candidates, family));
    }

    public RewriteResult RewriteModule(ModuleDefinition module)
    {
        (ParamFamily family, IReadOnlyList<SlotPlan> plans) = PlanInternal(module);

        var flips = new List<string>();
        var defers = new List<string>();
        int flipped = 0;

        // Virtual param slots: flip the whole vtable slot in LOCKSTEP via the planner — a half-flip leaves the
        // override's PARAM not matching the base's (fails to load). The planner's framework-root gate leaves an
        // override of a UnityEngine / Il2Cppmscorlib virtual wrapper-typed for free.
        foreach (SlotPlan slot in plans)
        {
            if (slot.IsDeferred)
            {
                defers.Add($"{Describe(slot.Root)}  (x{slot.Members.Count})  ->  DEFER {slot.WholeSlotDefer}");
                continue;
            }

            foreach (var (member, plan) in slot.PerMember)
            {
                if (plan is null) continue;                            // no-op member (nothing resolvable)
                var cm = (CecilSlotMethod)member;
                var pp = (ParamFlipPlan)plan.Payload;
                string before = cm.Definition.FullName;
                family.Apply(cm, pp);
                flips.Add($"{Describe(member)}({before}  ->  {cm.Definition.FullName})  (param x{pp.Items.Count}, virtual)");
                flipped += pp.Items.Count;
            }
        }

        // Non-virtual param methods: no override graph, so no planner — each flips on its own via the family's SAME
        // per-member judgment. Gated to the GAME's own proxies (a framework module's params are never ours; the planner
        // gave the virtual pass this gate for free via the slot root).
        if (!CecilProjector.IsFrameworkAssembly(module.Assembly.Name.Name))
        {
            var projector = new CecilProjector();
            foreach (MethodDefinition m in module.GetTypes().SelectMany(t => t.Methods).Where(ParamFamily.IsNonVirtualCandidate))
            {
                var cm = (CecilSlotMethod)projector.Method(m);
                MemberOutcome outcome = family.PlanMember(cm);
                if (outcome.Kind != MemberOutcomeKind.Flip) continue;      // no-op / nothing resolvable
                if (family.CollidesWithSibling(cm, outcome.Payload!))
                {
                    defers.Add($"{Describe(cm)}  (non-virtual param)  ->  DEFER {DeferReason.CollisionWithGeneratedSibling}");
                    continue;
                }
                var pp = (ParamFlipPlan)outcome.Payload!;
                string before = cm.Definition.FullName;
                family.Apply(cm, pp);
                flips.Add($"{Describe(cm)}({before}  ->  {cm.Definition.FullName})  (param x{pp.Items.Count}, non-virtual)");
                flipped += pp.Items.Count;
            }
        }

        return new RewriteResult(flipped, flips, defers);
    }

    static string Describe(ISlotMethod m)
    {
        var d = ((CecilSlotMethod)m).Definition;
        return $"{d.DeclaringType.FullName}::{d.Name}";
    }
}
