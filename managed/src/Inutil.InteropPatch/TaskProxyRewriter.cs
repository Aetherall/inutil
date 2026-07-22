using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// One rewritten module's outcome — what flipped and what deferred (with the planner's reason). Merge combines the
// several family passes' outcomes over one module into a single per-module result.
public sealed record RewriteResult(
    int Flipped,
    IReadOnlyList<string> Flips,
    IReadOnlyList<string> Defers)
{
    public static RewriteResult Empty { get; } = new(0, Array.Empty<string>(), Array.Empty<string>());

    public RewriteResult Merge(RewriteResult other)
        => new(Flipped + other.Flipped, Flips.Concat(other.Flips).ToList(), Defers.Concat(other.Defers).ToList());
}

// The virtual Task-return pass, driven by the pure engine: gather this module's virtual Task candidates, hand them to
// the VirtualSlotPlanner, and apply each flippable slot's PER-MEMBER plan — no slot-walking / gate / consistency logic
// here; that's the planner's, shared with every family. (Non-virtual Task returns are a separate, simpler pass — no
// planner needed.)
public sealed class TaskProxyRewriter
{
    // Plan this module's virtual Task slots WITHOUT mutating it — the grouping + gate decisions (which slots flip,
    // which defer and why). Exposed so a test can assert the cross-module gate independently of whether a member was
    // already flipped on a prior pass.
    public IReadOnlyList<SlotPlan> Plan(ModuleDefinition module) => PlanInternal(module).plans;

    (TaskFamily family, IReadOnlyList<SlotPlan> plans) PlanInternal(ModuleDefinition module)
    {
        var projector = new CecilProjector();
        var family = new TaskFamily(module, new WrapHelpers(module));
        var candidates = module.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(TaskFamily.IsCandidate)
            .Select(m => (ISlotMethod)projector.Method(m))
            .ToList();
        return (family, new VirtualSlotPlanner().Plan(candidates, family));
    }

    public RewriteResult RewriteModule(ModuleDefinition module)
    {
        (TaskFamily family, IReadOnlyList<SlotPlan> plans) = PlanInternal(module);

        var flips = new List<string>();
        var defers = new List<string>();
        int flipped = 0;

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
                family.Apply(cm, (TaskFlip)plan.Payload);
                flips.Add($"{Describe(member)}:  {before}  ->  {cm.Definition.ReturnType.FullName}");
                flipped++;
            }
        }

        // Non-virtual Task returns (a separate, simpler pass): no override graph, so no planner — each flips on its
        // own via the family's SAME per-member judgment (PlanMember/CanApplyFlip/Apply). Gated to the GAME's own
        // proxies: a framework module's Task methods are never ours to flip (the planner hands the virtual pass this
        // gate for free; the non-virtual pass must apply it explicitly, at the module level).
        if (!CecilProjector.IsFrameworkAssembly(module.Assembly.Name.Name))
        {
            var projector = new CecilProjector();
            foreach (MethodDefinition m in module.GetTypes().SelectMany(t => t.Methods).Where(TaskFamily.IsNonVirtualCandidate))
            {
                var cm = (CecilSlotMethod)projector.Method(m);
                MemberOutcome outcome = family.PlanMember(cm);
                if (outcome.Kind != MemberOutcomeKind.Flip) continue;   // AlreadyFlipped / Unplannable -> nothing to do
                if (!family.CanApplyFlip(cm))
                {
                    defers.Add($"{Describe(cm)}  (non-virtual)  ->  DEFER {DeferReason.ExceptionHandledBody}");
                    continue;
                }
                string before = cm.Definition.ReturnType.FullName;
                family.Apply(cm, (TaskFlip)outcome.Payload!);
                flips.Add($"{Describe(cm)}:  {before}  ->  {cm.Definition.ReturnType.FullName}  (non-virtual)");
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
