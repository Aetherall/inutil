using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The value-type Nullable-RETURN pass (the IL-rewrite boundary), driven by the pure engine. Structure mirrors
// TaskProxyRewriter exactly: the virtual slots go through the planner (grouping + gate + lockstep), the non-virtual
// returns flip on their own via the family's SAME per-member judgment. The seam-specific part — WHAT a value-type
// Nullable return flips to, and that its (broken) body is FULLY REPLACED, not ret-tail-spliced — is NullableFamily's.
//
// Why full-body replace: Il2CppInterop's value-type Nullable return is broken BOTH ways (verified in-game — an EMPTY
// Nullable NREs constructing the proxy BEFORE any ret-tail helper runs, a PRESENT one reads HasValue=False), so there
// is no correct value on the stack to salvage. The replacement calls the null-aware SDK invoke. Gated to the GAME's
// own proxies; idempotent (System.Nullable no longer classifies as the il2cpp family / groups as already-flipped).
public sealed class NullableReturnRewriter
{
    // Plan this module's virtual value-Nullable slots WITHOUT mutating it — exposed for tests to assert grouping / lockstep.
    public IReadOnlyList<SlotPlan> Plan(ModuleDefinition module) => PlanInternal(module).plans;

    (NullableFamily family, IReadOnlyList<SlotPlan> plans) PlanInternal(ModuleDefinition module)
    {
        var projector = new CecilProjector();
        var family = new NullableFamily(module, new WrapHelpers(module));
        var candidates = module.GetTypes()
            .SelectMany(t => t.Methods)
            .Where(NullableFamily.IsCandidate)
            .Select(m => (ISlotMethod)projector.Method(m))
            .ToList();
        return (family, new VirtualSlotPlanner().Plan(candidates, family));
    }

    public RewriteResult RewriteModule(ModuleDefinition module)
    {
        (NullableFamily family, IReadOnlyList<SlotPlan> plans) = PlanInternal(module);

        var flips = new List<string>();
        var defers = new List<string>();
        int flipped = 0;

        // Virtual value-Nullable slots: flip the whole vtable slot in LOCKSTEP via the planner's per-member plans — a
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
                family.Apply(cm, (NullableFlip)plan.Payload);
                flips.Add($"{Describe(member)}:  {before}  ->  {cm.Definition.ReturnType.FullName}  (nullable-return, virtual)");
                flipped++;
            }
        }

        // Non-virtual value-Nullable returns: no override graph, so no planner — each flips on its own via the family's
        // SAME per-member judgment + body-replace. Gated to the GAME's own proxies (a framework module's methods are
        // never ours; the planner gave the virtual pass this gate for free).
        if (!CecilProjector.IsFrameworkAssembly(module.Assembly.Name.Name))
        {
            var projector = new CecilProjector();
            foreach (MethodDefinition m in module.GetTypes().SelectMany(t => t.Methods).Where(NullableFamily.IsNonVirtualCandidate))
            {
                var cm = (CecilSlotMethod)projector.Method(m);
                MemberOutcome outcome = family.PlanMember(cm);
                if (outcome.Kind != MemberOutcomeKind.Flip) continue;   // AlreadyFlipped / Unplannable -> nothing to do
                if (!family.CanApplyFlip(cm))
                {
                    defers.Add($"{Describe(cm)}  (non-virtual nullable)  ->  DEFER {DeferReason.ExceptionHandledBody}");
                    continue;
                }
                string before = cm.Definition.ReturnType.FullName;
                family.Apply(cm, (NullableFlip)outcome.Payload!);
                flips.Add($"{Describe(cm)}:  {before}  ->  {cm.Definition.ReturnType.FullName}  (nullable-return, non-virtual)");
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
