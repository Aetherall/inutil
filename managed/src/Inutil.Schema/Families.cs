using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Inutil.Schema;

// The SINGLE registration site (single-source-of-truth — docs/contribution/01-philosophy.md). Every fact about a natural-typing
// family — il2cpp anchor name, BCL counterpart, Conv kind, bridge shape/direction — lives HERE and nowhere else.
// Both seams build from Families.Default() and reach every fact through Classify / ByConvKind / ByBclOpenType.
// Adding a family is one Register call here, not four edits.
//
// This is the ONLY code file allowed to spell an `Il2CppSystem.*` family name (the C4 guardrail enforces it).
public static class Families
{
    const string Sys  = "Il2CppSystem.";
    const string Coll = "Il2CppSystem.Collections.Generic.";
    const string Task_ = "Il2CppSystem.Threading.Tasks.Task";

    // The registry both seams consume. Order matters only for Classify's first-match. Anchors are distinct with ONE
    // deliberate exception: the MutableList row shares the List row's List`1 anchor and is registered AFTER it, so
    // Classify(List`1) always resolves the List row (flip seam + read direction untouched); MutableList is reached
    // only by spelling (ByBclOpenType).
    public static CorrespondenceRegistry Default()
    {
        var r = new CorrespondenceRegistry();

        // Task — the IL-seam Task family. Il2Cpp->Bcl (the modder gets a natural Task). Both the non-generic body
        // (arity 0: a bare type, not a generic instance) and Task`1.
        r.Register(new TypeCorrespondence(Task_, typeof(Task), ConvKind.Task,
            new AnchorBridge(Task_, 0, BridgeShape.ReferenceBespoke, Directionality.Il2CppToBcl)));
        r.Register(new TypeCorrespondence(Task_ + "`1", typeof(Task<>), ConvKind.Task,
            new AnchorBridge(Task_ + "`1", 1, BridgeShape.ReferenceBespoke, Directionality.Il2CppToBcl)));

        // Sequences. The concrete List`1 is the WRITE target for ConvKind.List; IReadOnlyList`1 / IEnumerable`1
        // are READ-ONLY spellings (writeTarget:false) that classify to a sequence kind but materialize INTO List`1.
        r.Register(new TypeCorrespondence(Coll + "List`1", typeof(List<>), ConvKind.List,
            new AnchorBridge(Coll + "List`1", 1, BridgeShape.ReferenceBespoke, Directionality.Both)));
        r.Register(new TypeCorrespondence(Coll + "IReadOnlyList`1", typeof(IReadOnlyList<>), ConvKind.List,
            new AnchorBridge(Coll + "IReadOnlyList`1", 1, BridgeShape.ContainerAdapter, Directionality.Il2CppToBcl), writeTarget: false));
        r.Register(new TypeCorrespondence(Coll + "IEnumerable`1", typeof(IEnumerable<>), ConvKind.Enumerable,
            new AnchorBridge(Coll + "IEnumerable`1", 1, BridgeShape.ContainerAdapter, Directionality.Il2CppToBcl), writeTarget: false));

        // MutableList — the WRITE-THROUGH sequence spelling: a hook spells IList<T> for a game il2cpp List<T'> param
        // and gets a LIVE view (every op forwards to the held proxy, so a Remove shrinks the game's own pool), where
        // List/IReadOnlyList/IEnumerable materialize a COPY. Same List`1 anchor as the List row, registered AFTER it
        // so Classify (first-match) still resolves an il2cpp List`1 to the List row (interop-patch flip untouched;
        // IsFlippableContainer excludes MutableList — IList is a hook-boundary spelling, never a proxy rewrite).
        // writeTarget:true so ByConvKind resolves the adapter's expected anchor for validation (can't collide with
        // ByConvKind(List) — keyed on Kind). ToIl2Cpp is IDENTITY (the held proxy); a mod-FABRICATED IList
        // dematerializes into a fresh List`1 like any sequence.
        r.Register(new TypeCorrespondence(Coll + "List`1", typeof(IList<>), ConvKind.MutableList,
            new AnchorBridge(Coll + "List`1", 1, BridgeShape.ContainerAdapter, Directionality.Both)));

        // Set (docs/contribution/01-philosophy.md): HashSet`1 is its OWN write target (distinct ConvKind.Set —
        // materializes INTO HashSet<T>, writes INTO Il2CppSystem…HashSet`1), NOT a List re-spelling: ConvKind drives
        // BOTH the managed and il2cpp target, and a set differs on each. Reuses the shared collection materializer +
        // DematerializeList unchanged.
        r.Register(new TypeCorrespondence(Coll + "HashSet`1", typeof(HashSet<>), ConvKind.Set,
            new AnchorBridge(Coll + "HashSet`1", 1, BridgeShape.ReferenceBespoke, Directionality.Both)));

        // Dictionary.
        r.Register(new TypeCorrespondence(Coll + "Dictionary`2", typeof(Dictionary<,>), ConvKind.Dictionary,
            new AnchorBridge(Coll + "Dictionary`2", 2, BridgeShape.ReferenceBespoke, Directionality.Both)));

        // Delegates — Il2CppSystem.Action[`N] / Func`N -> natural System.Action/Func. The only BclToIl2Cpp family:
        // natural-typing is faithful ONLY in the direction the GAME CONSUMES the delegate — a modder PASSES a bare
        // lambda into a game param, bridged by Il2CppInterop's op_Implicit at the IL seam. No reverse (il2cpp->BCL)
        // bridge, so WriteTarget:false (never a runtime materialization target) and ConvKind.Delegate fails loud if
        // it reaches the runtime Conv tree. Bounded arity like ValueTuple; beyond the bound a param stays
        // wrapper-typed (safe, not silent). One row per arity so each anchor is an exact Classify match.
        r.Register(new TypeCorrespondence(Sys + "Action", typeof(Action), ConvKind.Delegate,
            new AnchorBridge(Sys + "Action", 0, BridgeShape.ReferenceBespoke, Directionality.BclToIl2Cpp), writeTarget: false));

        Type[] actionOpens =
        {
            typeof(Action<>), typeof(Action<,>), typeof(Action<,,>), typeof(Action<,,,>),
            typeof(Action<,,,,>), typeof(Action<,,,,,>), typeof(Action<,,,,,,>), typeof(Action<,,,,,,,>),
        };
        for (int n = 1; n <= actionOpens.Length; n++)
            r.Register(new TypeCorrespondence(Sys + "Action`" + n, actionOpens[n - 1], ConvKind.Delegate,
                new AnchorBridge(Sys + "Action`" + n, n, BridgeShape.ReferenceBespoke, Directionality.BclToIl2Cpp), writeTarget: false));

        Type[] funcOpens =
        {
            typeof(Func<>), typeof(Func<,>), typeof(Func<,,>), typeof(Func<,,,>), typeof(Func<,,,,>),
            typeof(Func<,,,,,>), typeof(Func<,,,,,,>), typeof(Func<,,,,,,,>), typeof(Func<,,,,,,,,>),
        };
        for (int n = 1; n <= funcOpens.Length; n++)
            r.Register(new TypeCorrespondence(Sys + "Func`" + n, funcOpens[n - 1], ConvKind.Delegate,
                new AnchorBridge(Sys + "Func`" + n, n, BridgeShape.ReferenceBespoke, Directionality.BclToIl2Cpp), writeTarget: false));

        // Value-layout families: Nullable + the ValueTuple arities. A distinct concrete row per tuple arity (the
        // write side disambiguates by child count) keeps every anchor an exact match — no name-building at a site.
        r.Register(new TypeCorrespondence(Sys + "Nullable`1", typeof(Nullable<>), ConvKind.Nullable,
            new AnchorBridge(Sys + "Nullable`1", 1, BridgeShape.ValueLayout, Directionality.Both)));

        Type[] tupleOpens =
        {
            typeof(ValueTuple<>), typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>),
            typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>), typeof(ValueTuple<,,,,,,>),
        };
        for (int n = 1; n <= tupleOpens.Length; n++)
            r.Register(new TypeCorrespondence(Sys + "ValueTuple`" + n, tupleOpens[n - 1], ConvKind.Tuple,
                new AnchorBridge(Sys + "ValueTuple`" + n, n, BridgeShape.ValueLayout, Directionality.Both)));

        // Result — the per-GAME reference-wrapper family (EFT's Comfort.Common.Result<T>), reached through inutil's
        // OWN mirror Inutil.Bridge.Result<T> (Mirror.cs). This row is the game-AGNOSTIC half: it names the MIRROR +
        // ConvKind.Result, NEVER the game Result (C4 spirit — keeps inutil from spelling an il2cpp family name). The
        // il2cpp side is supplied at RUNTIME by a per-game shim (Mirror.RegisterResult); the anchor here is inutil's
        // own mirror name (a sentinel — no game Result matches it, so Classify never touches the patch seam) and
        // writeTarget:FALSE. writeTarget:false has three intended effects: (1) ByConvKind(Result) is null, so a
        // Result node is left UNVALIDATABLE (identity pass-through); (2) IsFlippableContainer is false, so no
        // interop-patch pass rewrites it (Result is runtime-ONLY — Hook matching binds by name+params, never the
        // return); (3) ContainerBridge.Il2CppTypeOf resolves a Result node through the Mirror shim's game type,
        // never ByConvKind. ByBclOpenType(Result<>) still classifies it (read direction), all the Conv tree needs.
        // Folded into the SchemaMarker hash like every family, so both seams compute the same address.
        r.Register(new TypeCorrespondence("Inutil.Bridge.Result`1", typeof(Inutil.Bridge.Result<>), ConvKind.Result,
            new AnchorBridge("Inutil.Bridge.Result`1", 1, BridgeShape.ReferenceBespoke, Directionality.Both), writeTarget: false));

        // Callback — the INBOUND twin of Result (EFT's Comfort.Common.Callback<T>), reached through inutil's mirror
        // Inutil.Bridge.Callback<T> (Mirror.cs). Same game-AGNOSTIC shape as Result (names the mirror + ConvKind;
        // writeTarget:false -> runtime-only, no interop-patch pass, il2cpp type via the shim). Discriminator is
        // Directionality.Il2CppToBcl (inbound-ONLY): a hook RECEIVES a game callback and the seam MATERIALIZES it
        // into the mirror (Ok/Fail marshal outbound, reusing the Result path); a hook never RETURNS a callback, so a
        // Callback node reaching the write side fails loud.
        r.Register(new TypeCorrespondence("Inutil.Bridge.Callback`1", typeof(Inutil.Bridge.Callback<>), ConvKind.Callback,
            new AnchorBridge("Inutil.Bridge.Callback`1", 1, BridgeShape.ReferenceBespoke, Directionality.Il2CppToBcl), writeTarget: false));

        // DelegateIn — the INBOUND-DELEGATE mirror family, GENERAL twin of Callback / inbound counterpart of the
        // outbound-only Delegate. A hook RECEIVES a game System.Action/Func param it can neither receive as a
        // System.Action (reverse bridge is call-site-only — ConvKind.Delegate fails loud) nor spell as a broken
        // close, so it spells inutil's mirror Inutil.Bridge.Action/Func (Mirror.cs) and the seam MATERIALIZES the
        // game delegate into it. Same game-AGNOSTIC, runtime-only shape as Callback (writeTarget:false -> ByConvKind
        // null, no interop-patch flip; il2cpp type resolved by NAME via the OUTBOUND Delegate row for the mirror's
        // System open). Il2CppToBcl (inbound-ONLY): reaching the write side fails loud. UNLIKE Callback there is NO
        // per-game shim — the game delegate is invoked through the il2cpp Action`N/Func`N proxy (Il2Cppmscorlib,
        // game-agnostic). One row per mirror arity (anchor arity = the mirror's generic-arg count: Func<T,TResult> =
        // Func`2), so each mirror open is an exact ByBclOpenType match; bounded like ValueTuple.
        r.Register(new TypeCorrespondence("Inutil.Bridge.Action`1", typeof(Inutil.Bridge.Action<>), ConvKind.DelegateIn,
            new AnchorBridge("Inutil.Bridge.Action`1", 1, BridgeShape.ReferenceBespoke, Directionality.Il2CppToBcl), writeTarget: false));
        r.Register(new TypeCorrespondence("Inutil.Bridge.Action`2", typeof(Inutil.Bridge.Action<,>), ConvKind.DelegateIn,
            new AnchorBridge("Inutil.Bridge.Action`2", 2, BridgeShape.ReferenceBespoke, Directionality.Il2CppToBcl), writeTarget: false));
        r.Register(new TypeCorrespondence("Inutil.Bridge.Action`3", typeof(Inutil.Bridge.Action<,,>), ConvKind.DelegateIn,
            new AnchorBridge("Inutil.Bridge.Action`3", 3, BridgeShape.ReferenceBespoke, Directionality.Il2CppToBcl), writeTarget: false));
        r.Register(new TypeCorrespondence("Inutil.Bridge.Func`2", typeof(Inutil.Bridge.Func<,>), ConvKind.DelegateIn,
            new AnchorBridge("Inutil.Bridge.Func`2", 2, BridgeShape.ReferenceBespoke, Directionality.Il2CppToBcl), writeTarget: false));
        r.Register(new TypeCorrespondence("Inutil.Bridge.Func`3", typeof(Inutil.Bridge.Func<,,>), ConvKind.DelegateIn,
            new AnchorBridge("Inutil.Bridge.Func`3", 3, BridgeShape.ReferenceBespoke, Directionality.Il2CppToBcl), writeTarget: false));

        // Object — the ERASED TOP (il2cpp System.Object) ↔ natural System.Object (docs/reference/deferred-zero-il2cpp.md):
        // a bare il2cpp handle a hook holds only as Il2CppSystem.Object (docs/guide/04-escape-hatches.md's "erased
        // handle"). ADDITIVE, never destructive — writeTarget:false, and ConvKind.Object is NOT an
        // IsFlippableContainer kind, so NO interop-patch pass rewrites a bare-object signature (the raw
        // Il2CppSystem.Object spelling stays a Tier-0 bind). What this ADDS is the OPTION to spell `object` at a hook
        // boundary — the runtime marshals it (identity in, proxy/null out, fail-loud on a foreign CLR object), and
        // ContainerBridge resolves its game type by name here (ByBclOpenType, not ByConvKind — so writeTarget:false
        // is fine). Arity 0: a bare non-generic anchor, like non-generic Task.
        r.Register(new TypeCorrespondence("Il2CppSystem.Object", typeof(object), ConvKind.Object,
            new AnchorBridge("Il2CppSystem.Object", 0, BridgeShape.ReferenceBespoke, Directionality.Both), writeTarget: false));

        // Exception — the game's Il2CppSystem.Exception (an Il2CppObjectBase, NOT a System.Exception) <-> natural
        // System.Exception. A hook RECEIVES a game exception and wants .Message/.StackTrace/.ToString. Like the
        // erased top, a reference-proxy leaf (arity 0, writeTarget:false — not a flippable container, no
        // interop-patch pass). But UNLIKE object it needs a real marshal, not identity: the runtime wraps the il2cpp
        // exception in a System.Exception FACADE (Inutil.Marshal.GameException, .Message via Il2CppInterop's
        // Il2CppException.BuildMessage) on the way in and unwraps on the way back — both game-AGNOSTIC.
        // ByBclOpenType(System.Exception) keys the game name here.
        r.Register(new TypeCorrespondence("Il2CppSystem.Exception", typeof(System.Exception), ConvKind.Exception,
            new AnchorBridge("Il2CppSystem.Exception", 0, BridgeShape.ReferenceBespoke, Directionality.Il2CppToBcl), writeTarget: false));

        return r;
    }
}

// The reusable classification bridge: a family is bound iff the closed il2cpp type's anchor name matches AND its
// arity matches AND no argument is an open generic parameter (the "never hard-bind, always probe" contract; the
// open-parameter case is the slot planner's concern). `arity == 0` is a non-generic family (a bare type, e.g.
// plain Task) that binds a non-generic-instance type of the same name.
public sealed class AnchorBridge : IBridge
{
    readonly string _anchor;
    readonly int _arity;

    public BridgeShape Shape { get; }
    public Directionality Direction { get; }

    public AnchorBridge(string anchor, int arity, BridgeShape shape, Directionality direction)
    {
        _anchor = anchor;
        _arity = arity;
        Shape = shape;
        Direction = direction;
    }

    public bool CanBind(ITypeRef t)
    {
        if (_arity == 0) return !t.IsGenericInstance && t.FullName == _anchor;
        if (!t.IsGenericInstance || t.ElementFullName != _anchor || t.GenericArguments.Count != _arity) return false;
        foreach (ITypeRef a in t.GenericArguments)
            if (a.IsGenericParameter) return false;
        return true;
    }
}
