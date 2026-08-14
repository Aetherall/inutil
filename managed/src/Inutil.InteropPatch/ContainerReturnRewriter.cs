using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The container-return pass: flip a game proxy method whose return is an il2cpp container (List / Dictionary /
// ValueTuple) to the NATURAL BCL return, routing its ret-tail through Il2CppMarshal.ToManaged<T> (materialize) — the
// SAME marshaller the runtime uses, so a hook boundary and a flipped proxy convert through one implementation (no
// drift). Driven by the pure engine like the other return families: VIRTUAL slots through the shared VirtualSlotPlanner
// (grouping + gate + lockstep), NON-virtual returns on their own via the family's SAME per-member judgment. The
// seam-specific part (WHAT flips, ret-tail splice) is ContainerFamily's. Gated to the GAME's own proxies; IDEMPOTENT
// (once flipped the return is System.*, grouped AlreadyFlipped, so a re-run flips nothing).
public sealed class ContainerReturnRewriter
{
    // Plan this module's virtual container slots WITHOUT mutating it — exposed for tests to assert grouping / lockstep.
    public IReadOnlyList<SlotPlan> Plan(ModuleDefinition module) => PlanInternal(module).plans;

    (ContainerFamily family, IReadOnlyList<SlotPlan> plans) PlanInternal(ModuleDefinition module)
    {
        var projector = new CecilProjector();
        var family = new ContainerFamily(module, new WrapHelpers(module));
        var candidates = module.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(ContainerFamily.IsCandidate)
            .Select(m => (ISlotMethod)projector.Method(m))
            .ToList();
        return (family, new VirtualSlotPlanner().Plan(candidates, family));
    }

    public RewriteResult RewriteModule(ModuleDefinition module)
    {
        (ContainerFamily family, IReadOnlyList<SlotPlan> plans) = PlanInternal(module);

        var flips = new List<string>();
        var defers = new List<string>();
        int flipped = 0;

        // Virtual container slots: flip the whole vtable slot in LOCKSTEP via the planner's per-member plans — a
        // half-flip leaves the override's return not matching the base's (fails to load). The planner's cross-module
        // gate handles a framework-rooted slot for free.
        foreach (SlotPlan slot in plans)
        {
            if (slot.IsDeferred)
            {
                defers.Add($"{Describe(slot.Root)}  (x{slot.Members.Count})  ->  DEFER {slot.WholeSlotDefer}");
                continue;
            }

            foreach (var (member, plan) in slot.PerMember)
            {
                if (plan is null) continue;                        // already-flipped no-op member
                var cm = (CecilSlotMethod)member;
                string before = cm.Definition.ReturnType.FullName;
                family.Apply(cm, (ContainerFlipPlan)plan.Payload);
                flips.Add($"{Describe(member)}:  {before}  ->  {cm.Definition.ReturnType.FullName}  (container, virtual)");
                flipped++;
            }
        }

        // Non-virtual container returns: no override graph, so no planner — each flips on its own via the family's SAME
        // per-member judgment + ret-tail splice. Gated to the GAME's own proxies (a framework module's container
        // methods are never ours; the planner gave the virtual pass this gate for free).
        if (!CecilProjector.IsFrameworkAssembly(module.Assembly.Name.Name))
        {
            var projector = new CecilProjector();
            foreach (MethodDefinition m in module.GetTypes().SelectMany(t => t.Methods).Where(ContainerFamily.IsNonVirtualCandidate))
            {
                var cm = (CecilSlotMethod)projector.Method(m);
                MemberOutcome outcome = family.PlanMember(cm);
                if (outcome.Kind != MemberOutcomeKind.Flip) continue;   // AlreadyFlipped / Unplannable -> nothing to do
                if (!family.CanApplyFlip(cm))
                {
                    defers.Add($"{Describe(cm)}  (non-virtual container)  ->  DEFER {DeferReason.ExceptionHandledBody}");
                    continue;
                }
                string before = cm.Definition.ReturnType.FullName;
                family.Apply(cm, (ContainerFlipPlan)outcome.Payload!);
                flips.Add($"{Describe(cm)}:  {before}  ->  {cm.Definition.ReturnType.FullName}  (container, non-virtual)");
                flipped++;
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
