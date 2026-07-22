using System.Collections.Generic;
using Inutil.Schema;

namespace Inutil.Schema.Tests;

// C1 (docs/contribution/01-philosophy.md): the production Families.Default() registry is the SINGLE source the four seam
// sites collapse to. This proves the three lookups a seam uses all resolve the SAME family consistently —
//   Classify(il2cppAnchor) -> row      (site 1, the IL flip: il2cpp -> BCL)
//   ByConvKind(kind[, arity]) -> row   (site 2, the runtime write: ConvKind -> il2cpp name)
//   ByBclOpenType(bclOpen) -> row      (site 3, the runtime read: BCL -> ConvKind)
// If they ever disagree, the "one registry" invariant is broken — the exact drift C1 exists to kill. Also proves
// the read-only-spelling and tuple-arity wrinkles, and that an unregistered family is seen by none of the three.
static class FamiliesTests
{
    public static void Run()
    {
        var reg = Families.Default();
        var i32 = FakeTypeRef.Simple("System.Int32");
        var str = FakeTypeRef.Simple("System.String");

        // List round-trips through all three lookups to the same row.
        var listInt = FakeTypeRef.Generic("Il2CppSystem.Collections.Generic.List`1", i32);
        T.Check("List`1<int> classifies (site 1)", reg.Classify(listInt)?.Kind == ConvKind.List);
        T.Check("List row BclOpenType is List<>", reg.Classify(listInt)?.BclOpenType == typeof(List<>));
        T.Check("ByBclOpenType(List<>) -> List (site 3)", reg.ByBclOpenType(typeof(List<>))?.Kind == ConvKind.List);
        T.Check("ByConvKind(List) -> List`1 (site 2)", reg.ByConvKind(ConvKind.List)?.Il2CppFullName == "Il2CppSystem.Collections.Generic.List`1");
        T.Check("List: all three lookups agree on one il2cpp name",
            reg.Classify(listInt)?.Il2CppFullName == reg.ByConvKind(ConvKind.List)?.Il2CppFullName
            && reg.ByBclOpenType(typeof(List<>))?.Il2CppFullName == reg.ByConvKind(ConvKind.List)?.Il2CppFullName);

        // Read-only spelling wrinkle: IReadOnlyList classifies to List but is NOT the write target — site 2 stays
        // concrete (List`1), never the interface spelling.
        T.Check("IReadOnlyList<> -> ConvKind.List (read spelling)", reg.ByBclOpenType(typeof(IReadOnlyList<>))?.Kind == ConvKind.List);
        T.Check("ByConvKind(List) is List`1, never IReadOnlyList`1", reg.ByConvKind(ConvKind.List)?.BclOpenType == typeof(List<>));
        T.Check("IEnumerable<> -> ConvKind.Enumerable (read spelling)", reg.ByBclOpenType(typeof(IEnumerable<>))?.Kind == ConvKind.Enumerable);

        // MutableList — the WRITE-THROUGH IList<T> spelling. It shares the List row's List`1 anchor (one il2cpp type,
        // two hook spellings), so the invariants are: (a) site 3 classifies IList<> to its OWN kind; (b) site 2
        // resolves that kind to the concrete List`1 anchor (C2 validation + the write fallback target), WITHOUT
        // disturbing ByConvKind(List); (c) the shared anchor NEVER changes site 1 — an il2cpp List`1 still classifies
        // to the List row, so the interop-patch flip seam is untouched; (d) MutableList is not flippable.
        T.Check("IList<> -> ConvKind.MutableList (write-through spelling, site 3)", reg.ByBclOpenType(typeof(IList<>))?.Kind == ConvKind.MutableList);
        T.Check("ByConvKind(MutableList) -> List`1 (site 2: the adapter's expected anchor)", reg.ByConvKind(ConvKind.MutableList)?.Il2CppFullName == "Il2CppSystem.Collections.Generic.List`1");
        T.Check("ByConvKind(List) is still the List row (kind-keyed, no collision)", reg.ByConvKind(ConvKind.List)?.BclOpenType == typeof(List<>));
        T.Check("shared anchor: Classify(List`1) still resolves the List row (site 1 untouched)", reg.Classify(listInt)?.Kind == ConvKind.List);
        T.Check("MutableList is not a flippable container (hook-boundary spelling only)", reg.ByBclOpenType(typeof(IList<>))?.IsFlippableContainer == false);

        // Dictionary + Nullable.
        var dictSI = FakeTypeRef.Generic("Il2CppSystem.Collections.Generic.Dictionary`2", str, i32);
        T.Check("Dictionary`2 -> Dictionary<,>", reg.Classify(dictSI)?.BclOpenType == typeof(Dictionary<,>));
        T.Check("ByConvKind(Dictionary) -> Dictionary`2", reg.ByConvKind(ConvKind.Dictionary)?.Il2CppFullName == "Il2CppSystem.Collections.Generic.Dictionary`2");
        T.Check("ByConvKind(Nullable) -> Nullable`1", reg.ByConvKind(ConvKind.Nullable)?.Il2CppFullName == "Il2CppSystem.Nullable`1");

        // Tuple arity wrinkle: ByConvKind(Tuple, N) picks the ValueTuple`N row exactly.
        T.Check("ValueTuple`2 -> ValueTuple<,>", reg.Classify(FakeTypeRef.Generic("Il2CppSystem.ValueTuple`2", str, str))?.BclOpenType == typeof(ValueTuple<,>));
        T.Check("ByConvKind(Tuple, 2) -> ValueTuple`2", reg.ByConvKind(ConvKind.Tuple, 2)?.Il2CppFullName == "Il2CppSystem.ValueTuple`2");
        T.Check("ByConvKind(Tuple, 3) -> ValueTuple`3", reg.ByConvKind(ConvKind.Tuple, 3)?.Il2CppFullName == "Il2CppSystem.ValueTuple`3");

        // Task: non-generic + generic.
        T.Check("plain Task (non-generic) classifies", reg.Classify(FakeTypeRef.Simple("Il2CppSystem.Threading.Tasks.Task"))?.Kind == ConvKind.Task);
        T.Check("Task`1<Player> -> Task<>", reg.Classify(FakeTypeRef.Generic("Il2CppSystem.Threading.Tasks.Task`1", FakeTypeRef.Simple("ToyGame.Player")))?.BclOpenType == typeof(System.Threading.Tasks.Task<>));

        // Never hard-bind: an OPEN Task`1<!T> anchor matches but the probe rejects it.
        T.Check("open Task`1<!T> -> null (probe rejects)", reg.Classify(FakeTypeRef.Generic("Il2CppSystem.Threading.Tasks.Task`1", FakeTypeRef.Param("T"))) is null);

        // Delegates: non-generic Action + generic Action`N/Func`N classify to ConvKind.Delegate (the BclToIl2Cpp
        // family — the IL seam's op_Implicit param flip). WriteTarget:false, so ByConvKind(Delegate) is null — a
        // delegate has NO runtime materialization target (reaching the runtime write path fails loud, never a
        // silent mis-marshal). The BCL open type round-trips through site 3 for a generic delegate.
        T.Check("plain Action (non-generic) classifies as Delegate", reg.Classify(FakeTypeRef.Simple("Il2CppSystem.Action"))?.Kind == ConvKind.Delegate);
        T.Check("Action`1<int> -> Delegate", reg.Classify(FakeTypeRef.Generic("Il2CppSystem.Action`1", i32))?.Kind == ConvKind.Delegate);
        T.Check("Action`1 BclOpenType is Action<>", reg.Classify(FakeTypeRef.Generic("Il2CppSystem.Action`1", i32))?.BclOpenType == typeof(System.Action<>));
        T.Check("Func`2<int,int> -> Func<,>", reg.Classify(FakeTypeRef.Generic("Il2CppSystem.Func`2", i32, i32))?.BclOpenType == typeof(System.Func<,>));
        T.Check("ByBclOpenType(Action<>) -> Delegate (site 3)", reg.ByBclOpenType(typeof(System.Action<>))?.Kind == ConvKind.Delegate);
        T.Check("Delegate has no write target (BclToIl2Cpp only)", reg.ByConvKind(ConvKind.Delegate) is null);
        T.Check("open Action`1<!T> -> null (probe rejects)", reg.Classify(FakeTypeRef.Generic("Il2CppSystem.Action`1", FakeTypeRef.Param("T"))) is null);

        // Result — the per-GAME reference-wrapper mirror. Registered game-AGNOSTIC: the row names inutil's OWN mirror
        // (Inutil.Bridge.Result<>) + ConvKind.Result, NEVER the game Result. It classifies through site 3 (read
        // direction) so the runtime Conv tree recurses it; but it is writeTarget:false — like the read-only sequence
        // spellings and the delegate family — so ByConvKind(Result) is null (no registry-resolved il2cpp write target:
        // ContainerBridge resolves a Result node through the per-game Mirror shim instead). The mirror anchor never
        // matches a real game Result, so the patch-seam Classify never binds it (runtime-only family).
        T.Check("ByBclOpenType(Result<>) -> ConvKind.Result (site 3, read)", reg.ByBclOpenType(typeof(Inutil.Bridge.Result<>))?.Kind == ConvKind.Result);
        T.Check("Result row BclOpenType is the mirror Inutil.Bridge.Result<>", reg.ByBclOpenType(typeof(Inutil.Bridge.Result<>))?.BclOpenType == typeof(Inutil.Bridge.Result<>));
        T.Check("Result has no registry write target (game type comes from the per-game shim)", reg.ByConvKind(ConvKind.Result) is null);
        T.Check("Result is not a flippable container (runtime-only, no interop-patch flip)", reg.ByBclOpenType(typeof(Inutil.Bridge.Result<>))?.IsFlippableContainer == false);
        T.Check("Result anchor is inutil's own mirror, never a game type (game-agnostic)", reg.ByBclOpenType(typeof(Inutil.Bridge.Result<>))?.Il2CppFullName == "Inutil.Bridge.Result`1");

        // Callback — the INBOUND twin of Result (EFT's Comfort.Common.Callback<T>), reached through inutil's OWN mirror
        // Inutil.Bridge.Callback<>. Same game-AGNOSTIC, runtime-only shape as Result: classifies through site 3 (the
        // inbound/read direction) so the Conv tree recurses it; writeTarget:false so ByConvKind is null and it is NOT a
        // flippable container (the game callback param is resolved via the per-game Mirror shim, never patch-flipped).
        T.Check("ByBclOpenType(Callback<>) -> ConvKind.Callback (site 3, read)", reg.ByBclOpenType(typeof(Inutil.Bridge.Callback<>))?.Kind == ConvKind.Callback);
        T.Check("Callback row BclOpenType is the mirror Inutil.Bridge.Callback<>", reg.ByBclOpenType(typeof(Inutil.Bridge.Callback<>))?.BclOpenType == typeof(Inutil.Bridge.Callback<>));
        T.Check("Callback has no registry write target (game type from the per-game shim)", reg.ByConvKind(ConvKind.Callback) is null);
        T.Check("Callback is not a flippable container (runtime-only, no interop-patch flip)", reg.ByBclOpenType(typeof(Inutil.Bridge.Callback<>))?.IsFlippableContainer == false);
        T.Check("Callback anchor is inutil's own mirror, never a game type (game-agnostic)", reg.ByBclOpenType(typeof(Inutil.Bridge.Callback<>))?.Il2CppFullName == "Inutil.Bridge.Callback`1");

        // DelegateIn — the INBOUND-DELEGATE mirror family (Inutil.Bridge.Action/Func<..>), the GENERAL twin of Callback and
        // the inbound counterpart of the outbound Delegate family. Same game-AGNOSTIC, runtime-only shape as Callback: every
        // mirror open classifies through site 3 (ByBclOpenType) to ConvKind.DelegateIn so the Conv tree recurses it; each is
        // Directionality.Il2CppToBcl (inbound-ONLY — a hook never RETURNS a delegate mirror); writeTarget:false so ByConvKind
        // is null and it is NOT a flippable container (the game System.Action/Func param is bound via HookMatch's mirror tier,
        // never patch-flipped). The anchor is inutil's own mirror name (never a game/Il2CppSystem type), one row per arity.
        Type[] delegateMirrors = { typeof(Inutil.Bridge.Action<>), typeof(Inutil.Bridge.Action<,>), typeof(Inutil.Bridge.Action<,,>), typeof(Inutil.Bridge.Func<,>), typeof(Inutil.Bridge.Func<,,>) };
        foreach (Type m in delegateMirrors)
        {
            T.Check($"ByBclOpenType({m.Name}) -> ConvKind.DelegateIn (site 3, inbound read)", reg.ByBclOpenType(m)?.Kind == ConvKind.DelegateIn);
            T.Check($"{m.Name} row BclOpenType is the mirror itself", reg.ByBclOpenType(m)?.BclOpenType == m);
            T.Check($"{m.Name} is Directionality.Il2CppToBcl (inbound twin of Delegate)", reg.ByBclOpenType(m)?.Direction == Directionality.Il2CppToBcl);
            T.Check($"{m.Name} is not a flippable container (runtime-only, no interop-patch flip)", reg.ByBclOpenType(m)?.IsFlippableContainer == false);
            T.Check($"{m.Name} anchor is inutil's own mirror, never a game/Il2CppSystem type", reg.ByBclOpenType(m)?.Il2CppFullName?.StartsWith("Inutil.Bridge.") == true);
        }
        T.Check("DelegateIn has no registry write target (il2cpp Action/Func proxy resolved by name)", reg.ByConvKind(ConvKind.DelegateIn) is null);
        T.Check("Action`2 anchor arity matches its 2 generic args", reg.ByBclOpenType(typeof(Inutil.Bridge.Action<,>))?.Il2CppFullName == "Inutil.Bridge.Action`2");
        T.Check("Func`2 anchor arity is the generic-arg count (arg+return)", reg.ByBclOpenType(typeof(Inutil.Bridge.Func<,>))?.Il2CppFullName == "Inutil.Bridge.Func`2");

        // Object — the ERASED TOP (il2cpp System.Object) <-> natural System.Object. A game-AGNOSTIC, runtime-only family
        // like Result/Callback: classifies through site 3 (ByBclOpenType(object)) so the Conv tree recurses it, but
        // writeTarget:false so ByConvKind is null (its il2cpp type is resolved by NAME via ByBclOpenType at the write
        // site, not as a ByConvKind write target) and — crucially, the ADDITIVE guarantee — it is NOT a flippable
        // container, so no interop-patch pass ever rewrites a bare-object signature (the raw Il2CppSystem.Object spelling
        // is preserved). Arity 0 (a bare, non-generic anchor). Unlike Result/Callback the anchor IS a game/framework type
        // name (Il2CppSystem.Object), because object's faithful twin is the il2cpp erased top itself, not a bespoke mirror.
        T.Check("ByBclOpenType(object) -> ConvKind.Object (site 3, read)", reg.ByBclOpenType(typeof(object))?.Kind == ConvKind.Object);
        T.Check("Object row BclOpenType is System.Object", reg.ByBclOpenType(typeof(object))?.BclOpenType == typeof(object));
        T.Check("Object anchor is the il2cpp erased top", reg.ByBclOpenType(typeof(object))?.Il2CppFullName == "Il2CppSystem.Object");
        T.Check("Object is NOT a flippable container (ADDITIVE — never a destructive interop-patch signature rewrite)", reg.ByBclOpenType(typeof(object))?.IsFlippableContainer == false);
        T.Check("Object has no ByConvKind write target (writeTarget:false; game type resolved by name via ByBclOpenType)", reg.ByConvKind(ConvKind.Object) is null);
        // Classify a bare il2cpp Il2CppSystem.Object -> the Object row (the interop-patch side sees it; but IsFlippableContainer
        // being false above is what keeps every pass from acting on it — this Classify hit only powers the runtime read).
        T.Check("Classify(bare Il2CppSystem.Object) -> Object row", reg.Classify(FakeTypeRef.Simple("Il2CppSystem.Object"))?.Kind == ConvKind.Object);

        // Exception — natural System.Exception <-> the game's Il2CppSystem.Exception, same additive/runtime-only shape as
        // Object (arity 0, writeTarget:false, not a flippable container) but ConvKind.Exception (the runtime wraps it in a
        // System.Exception facade, not identity). The game name (Il2CppSystem.Exception) is the anchor, keyed by System.Exception.
        T.Check("ByBclOpenType(Exception) -> ConvKind.Exception (site 3, read)", reg.ByBclOpenType(typeof(System.Exception))?.Kind == ConvKind.Exception);
        T.Check("Exception row BclOpenType is System.Exception", reg.ByBclOpenType(typeof(System.Exception))?.BclOpenType == typeof(System.Exception));
        T.Check("Exception anchor is the il2cpp Il2CppSystem.Exception", reg.ByBclOpenType(typeof(System.Exception))?.Il2CppFullName == "Il2CppSystem.Exception");
        T.Check("Exception is NOT a flippable container (ADDITIVE — never a destructive Exception signature rewrite)", reg.ByBclOpenType(typeof(System.Exception))?.IsFlippableContainer == false);
        T.Check("Exception has no ByConvKind write target (writeTarget:false)", reg.ByConvKind(ConvKind.Exception) is null);
        T.Check("Classify(bare Il2CppSystem.Exception) -> Exception row", reg.Classify(FakeTypeRef.Simple("Il2CppSystem.Exception"))?.Kind == ConvKind.Exception);

        // An unregistered family is seen by NONE of the three lookups (SortedSet — HashSet is now a real family).
        T.Check("unregistered anchor -> Classify null", reg.Classify(FakeTypeRef.Generic("Il2CppSystem.Collections.Generic.SortedSet`1", i32)) is null);
        T.Check("unregistered BCL type -> ByBclOpenType null", reg.ByBclOpenType(typeof(SortedSet<>)) is null);
    }
}
