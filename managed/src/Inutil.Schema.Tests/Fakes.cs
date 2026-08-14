using Inutil.Schema;

namespace Inutil.Schema.Tests;

// Synthetic type-graph shapes for the planner tests. A fake module carries a game/framework flag; a fake type
// carries a base link + its virtual methods; a fake method carries the slot facts (name, params, NewSlot) plus a
// test-only `Shape` string the fake families classify on.

sealed class FakeModule
{
    public string Id = "";
    public bool Framework;
}

sealed class FakeType : ISlotType
{
    public string FullName { get; init; } = "";
    public FakeModule Module { get; init; } = new();
    public FakeType? Base { get; set; }
    public List<FakeMethod> Methods { get; } = new();

    public object ModuleId => Module.Id;
    public bool IsFrameworkModule => Module.Framework;
    public ISlotType? BaseType => Base;
    public IReadOnlyList<ISlotMethod> VirtualMethods => Methods;

    public FakeMethod Add(FakeMethod m) { m.Owner = this; Methods.Add(m); return m; }
}

sealed class FakeMethod : ISlotMethod
{
    public FakeType Owner = null!;
    public string MName { get; init; } = "M";
    public string[] Params { get; init; } = Array.Empty<string>();
    public bool NewSlot { get; init; }
    public bool Body { get; init; } = true;
    public bool EH { get; init; }
    public bool Ret { get; init; } = true;
    public string Shape { get; init; } = "";   // test classification input for the fake families

    public ISlotType DeclaringType => Owner;
    public string Name => MName;
    public IReadOnlyList<string> ParameterTypeNames => Params;
    public bool IsNewSlot => NewSlot;
    public bool HasBody => Body;
    public bool HasExceptionHandler => EH;
    public bool HasReturn => Ret;
}

// A stand-in RETURN family. It classifies on the member's own Shape and — crucially for the anti-bug test —
// derives each member's payload from THAT member's shape, so a test can assert the planner carries per-member
// plans rather than one plan for the whole slot.
sealed class FakeReturnFamily : IFamilyPass
{
    public MemberOutcome PlanMember(ISlotMethod m)
    {
        string s = ((FakeMethod)m).Shape;
        return s switch
        {
            "flipped"    => MemberOutcome.AlreadyFlipped(),
            "unclosable" => MemberOutcome.Unplannable(DeferReason.OpenGenericMultiMember),
            _            => MemberOutcome.Flip("plan:" + s),   // payload encodes the member's OWN shape
        };
    }

    // Return-tail rewrite feasibility, via the shared helper (abstract decls need no rewrite -> allowed).
    public bool CanApplyFlip(ISlotMethod m) => !m.HasBody || BodyRewrite.CanRewriteReturnTail(m);
}

// A stand-in PARAM family, only to exercise the collision guard: a member whose Shape is "collides" reports a
// sibling-signature collision.
sealed class FakeParamFamily : IFamilyPass
{
    public MemberOutcome PlanMember(ISlotMethod m) => MemberOutcome.Flip("param");
    public bool CollidesWithSibling(ISlotMethod m, object payload) => ((FakeMethod)m).Shape == "collides";
}
