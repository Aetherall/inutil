namespace Inutil.Schema;

// Why a slot (or the whole group) is left wrapper-typed instead of flipped (docs/contribution/architecture/10-schema.md). The planner
// names the reason so a diagnostic can say WHY, and a test can assert it deferred for the RIGHT reason.
public enum DeferReason
{
    ExternalRoot,                   // the resolved slot root is not a NewSlot (its true origin is a base we can't see) — not ours to flip
    CrossModuleRootUnverified,      // the root is a NewSlot but in a FRAMEWORK module we never flip — flipping the override alone half-flips the slot
    OpenGenericMultiMember,         // a member whose shape has no plan — a genuinely unclosable/mixed element
    ExceptionHandledBody,           // a member body can't be safely rewritten for this seam (exception-handler region / no ret)
    CollisionWithGeneratedSibling,  // flipping would collide with an existing generated sibling overload's signature
}

// The family's verdict on ONE member's flippability. The planner is purely structural (groups, gates, enforces
// slot-wide all-or-nothing); WHAT a member flips to is the family's call, returned here. Payload is opaque to the
// planner — the seam-specific conversion data it will splice. Crucially, each member carries its OWN payload, so
// the planner can never derive one plan from the root/first member and apply it to siblings with a different
// closed shape (the buggy Nullable/Enumerable passes' bug class).
public enum MemberOutcomeKind { Flip, AlreadyFlipped, Unplannable }

public readonly struct MemberOutcome
{
    public MemberOutcomeKind Kind { get; }
    public object? Payload { get; }        // set when Flip — the family's per-member conversion data
    public DeferReason? Reason { get; }    // set when Unplannable

    MemberOutcome(MemberOutcomeKind kind, object? payload, DeferReason? reason)
    { Kind = kind; Payload = payload; Reason = reason; }

    // This member flips; `payload` is ITS OWN conversion data (never shared across the slot).
    public static MemberOutcome Flip(object payload) => new(MemberOutcomeKind.Flip, payload, null);

    // Already the natural shape (flipped on a prior launch) — a no-op member that keeps the slot consistent and
    // needs no rewrite. Not a defer: an all-already-flipped slot is a clean idempotent no-op.
    public static MemberOutcome AlreadyFlipped() => new(MemberOutcomeKind.AlreadyFlipped, null, null);

    // This member can't be flipped; the whole slot must defer (a half-flip fails to load). The reason is the
    // family's — an unclosable element, an EH body, etc.
    public static MemberOutcome Unplannable(DeferReason reason) => new(MemberOutcomeKind.Unplannable, null, reason);
}

// The plan for a single flippable member: the opaque payload the seam splices. AlreadyFlipped / no-op members
// get a null MemberPlan in SlotPlan.PerMember (in the slot, but nothing to do).
public sealed class MemberPlan
{
    public object Payload { get; }
    public MemberPlan(object payload) => Payload = payload;
}

// The plan for one virtual slot. Either the whole slot defers (WholeSlotDefer set, PerMember empty) or it flips
// (WholeSlotDefer null, PerMember carries each member's own plan — or null for a no-op member).
public sealed class SlotPlan
{
    public ISlotMethod Root { get; }
    public IReadOnlyList<ISlotMethod> Members { get; }
    public DeferReason? WholeSlotDefer { get; }
    public IReadOnlyDictionary<ISlotMethod, MemberPlan?> PerMember { get; }

    public bool IsDeferred => WholeSlotDefer is not null;

    SlotPlan(ISlotMethod root, IReadOnlyList<ISlotMethod> members, DeferReason? defer,
             IReadOnlyDictionary<ISlotMethod, MemberPlan?> perMember)
    { Root = root; Members = members; WholeSlotDefer = defer; PerMember = perMember; }

    public static SlotPlan Deferred(ISlotMethod root, IReadOnlyList<ISlotMethod> members, DeferReason reason)
        => new(root, members, reason, new Dictionary<ISlotMethod, MemberPlan?>());

    public static SlotPlan Planned(ISlotMethod root, IReadOnlyList<ISlotMethod> members,
                                   IReadOnlyDictionary<ISlotMethod, MemberPlan?> perMember)
        => new(root, members, null, perMember);
}

// What a family plugs into the planner. Three hooks, each the seam-specific judgment the planner is NOT allowed
// to make; the planner supplies everything else (grouping, gate, all-or-nothing consistency) exactly once.
public interface IFamilyPass
{
    // Classify one member's shape: Flip (with its own payload), AlreadyFlipped, or Unplannable (defer the slot).
    MemberOutcome PlanMember(ISlotMethod member);

    // Can this member's BODY take the flip for this seam? Return families check the return tail
    // (BodyRewrite.CanRewriteReturnTail); param families check the argument load site. Default: always
    // (abstract-only families). A false here defers the whole slot as ExceptionHandledBody.
    bool CanApplyFlip(ISlotMethod member) => true;

    // Param-family collision guard: would flipping this member to `payload`'s type collide with an existing
    // sibling overload? Default: no (return families can't collide — the return isn't part of the signature). A
    // true defers the whole slot as CollisionWithGeneratedSibling.
    bool CollidesWithSibling(ISlotMethod member, object payload) => false;
}

// Shared body-rewritability predicates — one implementation of each check, so a family calls it instead of
// re-deriving CanRewriteRefRet per seam (the exact duplication the rewrite exists to kill).
public static class BodyRewrite
{
    // A return method whose tail can be routed through a wrap helper: has a body, at least one ret, and no
    // exception-handler region (a ret inside a protected block can't br out). Abstract decls (no body) are
    // handled by the caller as "nothing to rewrite", so this returns true only for a rewritable *body*.
    public static bool CanRewriteReturnTail(ISlotMethod m)
        => m.HasBody && m.HasReturn && !m.HasExceptionHandler;
}
