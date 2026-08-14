using Inutil.Schema;

namespace Inutil.Schema.Tests;

// Planner unit tests over synthetic shapes — the per-family template PLUS a direct regression for the two
// confirmed HIGH bugs the planner retires (InteropPatchCore.cs:534 / :1195 — one plan for the group).
static class PlannerTests
{
    public static void Run()
    {
        var game = new FakeModule { Id = "Game", Framework = false };
        var game2 = new FakeModule { Id = "Game2", Framework = false };
        var fx = new FakeModule { Id = "Framework", Framework = true };
        var planner = new VirtualSlotPlanner();
        var ret = new FakeReturnFamily();

        static SlotPlan One(IReadOnlyList<SlotPlan> ps) => ps.Single();
        static MemberPlan? PlanOf(SlotPlan sp, ISlotMethod m) => sp.PerMember.TryGetValue(m, out var p) ? p : null;

        // ── singleton virtual slot ───────────────────────────────────────────────────────────────
        {
            var t = new FakeType { FullName = "T", Module = game };
            var m = t.Add(new FakeMethod { MName = "Get", NewSlot = true, Shape = "Task<A>" });
            var plan = One(planner.Plan(new[] { (ISlotMethod)m }, ret));
            T.Check("singleton: flips", !plan.IsDeferred && plan.Members.Count == 1);
            T.Check("singleton: carries its own plan", (PlanOf(plan, m)?.Payload as string) == "plan:Task<A>");
        }

        // ── multi-member slot, DIFFERING per-member closed args (the :534/:1195 regression) ───────
        {
            var b = new FakeType { FullName = "Base", Module = game };
            var mb = b.Add(new FakeMethod { MName = "Get", NewSlot = true, Shape = "Task<A>" });
            var d = new FakeType { FullName = "Derived", Module = game, Base = b };
            var md = d.Add(new FakeMethod { MName = "Get", NewSlot = false, Shape = "Task<B>" });

            var plan = One(planner.Plan(new[] { (ISlotMethod)mb, md }, ret));
            T.Check("multimember: one slot, both members", !plan.IsDeferred && plan.Members.Count == 2 && plan.Root == mb);
            string? pb = PlanOf(plan, mb)?.Payload as string;
            string? pd = PlanOf(plan, md)?.Payload as string;
            T.Check("multimember: base keeps its own plan", pb == "plan:Task<A>", $"got {pb}");
            T.Check("REGRESSION :534/:1195 — override keeps ITS OWN plan, not the root's",
                pd == "plan:Task<B>", $"override plan was {pd} (root's is plan:Task<A>)");
        }

        // ── open-generic-parameter member ─────────────────────────────────────────────────────────
        {
            var t = new FakeType { FullName = "G", Module = game };
            var m = t.Add(new FakeMethod { MName = "Load", NewSlot = true, Shape = "Task<!T>" });
            var plan = One(planner.Plan(new[] { (ISlotMethod)m }, ret));
            T.Check("open-generic: flips with own-param plan",
                !plan.IsDeferred && (PlanOf(plan, m)?.Payload as string) == "plan:Task<!T>");
        }

        // ── cross-module slot root in another GAME module (v15) ──────────────────────────────────
        {
            var b = new FakeType { FullName = "BackendBase", Module = game };
            var mb = b.Add(new FakeMethod { MName = "Open", NewSlot = true, Shape = "Task<S>" });
            var d = new FakeType { FullName = "Backend", Module = game2, Base = b };
            var md = d.Add(new FakeMethod { MName = "Open", NewSlot = false, Shape = "Task<S>" });
            var plan = One(planner.Plan(new[] { (ISlotMethod)md, mb }, ret));
            T.Check("cross-module game root: flips (v15)", !plan.IsDeferred && plan.Members.Count == 2);
        }

        // ── framework slot root: defer CrossModuleRootUnverified ─────────────────────────────────
        {
            var b = new FakeType { FullName = "FxBase", Module = fx };
            var mb = b.Add(new FakeMethod { MName = "Op", NewSlot = true, Shape = "Task<A>" });
            var d = new FakeType { FullName = "GameOverride", Module = game, Base = b };
            var md = d.Add(new FakeMethod { MName = "Op", NewSlot = false, Shape = "Task<A>" });
            var plan = One(planner.Plan(new[] { (ISlotMethod)md }, ret));
            T.Check("framework root: deferred", plan.IsDeferred && plan.WholeSlotDefer == DeferReason.CrossModuleRootUnverified,
                $"{plan.WholeSlotDefer}");
        }

        // ── reuse-slot with no visible NewSlot origin: ExternalRoot ──────────────────────────────
        {
            var t = new FakeType { FullName = "Orphan", Module = game };
            var m = t.Add(new FakeMethod { MName = "X", NewSlot = false, Shape = "Task<A>" });
            var plan = One(planner.Plan(new[] { (ISlotMethod)m }, ret));
            T.Check("reuse-slot w/o NewSlot origin: ExternalRoot", plan.IsDeferred && plan.WholeSlotDefer == DeferReason.ExternalRoot);
        }

        // ── base-chain walk past a non-declaring intermediate (v12) ──────────────────────────────
        {
            var b = new FakeType { FullName = "B", Module = game };
            var mb = b.Add(new FakeMethod { MName = "Find", NewSlot = true, Shape = "Task<A>" });
            var mid = new FakeType { FullName = "Mid", Module = game, Base = b };   // inherits Find WITHOUT redeclaring
            var leaf = new FakeType { FullName = "Leaf", Module = game, Base = mid };
            var ml = leaf.Add(new FakeMethod { MName = "Find", NewSlot = false, Shape = "Task<A>" });
            var plan = One(planner.Plan(new[] { (ISlotMethod)ml, mb }, ret));
            T.Check("v12 base-chain walk: leaf roots to B, one slot", !plan.IsDeferred && plan.Root == mb && plan.Members.Count == 2);
        }

        // ── all-or-nothing: one unplannable member defers the whole slot ─────────────────────────
        {
            var b = new FakeType { FullName = "B2", Module = game };
            var mb = b.Add(new FakeMethod { MName = "G", NewSlot = true, Shape = "Task<A>" });
            var d = new FakeType { FullName = "D2", Module = game, Base = b };
            var md = d.Add(new FakeMethod { MName = "G", NewSlot = false, Shape = "unclosable" });
            var plan = One(planner.Plan(new[] { (ISlotMethod)mb, md }, ret));
            T.Check("all-or-nothing: unplannable member defers slot", plan.IsDeferred && plan.WholeSlotDefer == DeferReason.OpenGenericMultiMember);
        }

        // ── EH-bodied member defers the whole slot ───────────────────────────────────────────────
        {
            var b = new FakeType { FullName = "B3", Module = game };
            var mb = b.Add(new FakeMethod { MName = "G", NewSlot = true, Shape = "Task<A>" });
            var d = new FakeType { FullName = "D3", Module = game, Base = b };
            var md = d.Add(new FakeMethod { MName = "G", NewSlot = false, Shape = "Task<A>", EH = true });
            var plan = One(planner.Plan(new[] { (ISlotMethod)mb, md }, ret));
            T.Check("EH body defers slot", plan.IsDeferred && plan.WholeSlotDefer == DeferReason.ExceptionHandledBody);
        }

        // ── collision guard (v12, param family) ──────────────────────────────────────────────────
        {
            var t = new FakeType { FullName = "P", Module = game };
            var m = t.Add(new FakeMethod { MName = "Set", NewSlot = true, Shape = "collides" });
            var plan = One(planner.Plan(new[] { (ISlotMethod)m }, new FakeParamFamily()));
            T.Check("collision guard defers slot", plan.IsDeferred && plan.WholeSlotDefer == DeferReason.CollisionWithGeneratedSibling);
        }

        // ── half-flipped slot: idempotency ───────────────────────────────────────────────────────
        {
            var b = new FakeType { FullName = "B4", Module = game };
            var mb = b.Add(new FakeMethod { MName = "G", NewSlot = true, Shape = "flipped" });
            var d = new FakeType { FullName = "D4", Module = game, Base = b };
            var md = d.Add(new FakeMethod { MName = "G", NewSlot = false, Shape = "Task<A>" });
            var plan = One(planner.Plan(new[] { (ISlotMethod)mb, md }, ret));
            T.Check("half-flipped: not deferred", !plan.IsDeferred);
            T.Check("half-flipped: already-flipped member is a no-op (null plan)", PlanOf(plan, mb) is null);
            T.Check("half-flipped: sibling still flips with its own plan", (PlanOf(plan, md)?.Payload as string) == "plan:Task<A>");
        }
    }
}
