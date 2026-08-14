using Il2CppInterop.Runtime.InteropTypes;   // Il2CppObjectBase — the base of every game reference proxy
using Inutil.Schema;

namespace Inutil.Marshal;

// The seam's shape classifier: wraps ReflectionConvShapeSource to add il2cpp-specific leaves. The pure base knows
// the BCL container families; this adds the il2cpp LEAVES so the recursion bottoms out
// correctly: a spelled game reference proxy (Il2CppObjectBase-derived) or a ref-bearing value proxy
// (Il2CppSystem.ValueType-derived) is a LEAF — passed by identity / bridged whole — never recursed as a
// container even if it happens to implement one. In natural typing the mod spells BCL containers (List<T>, T[]),
// which are NOT Il2CppObjectBase, so they still route to the container path; only a directly-spelled game proxy
// short-circuits here.
public sealed class Il2CppConvShapeSource : ReflectionConvShapeSource
{
    protected override bool IsLeaf(Type t)
        => base.IsLeaf(t)
           || typeof(Il2CppObjectBase).IsAssignableFrom(t)       // game reference proxy — identity pass-through
           || ValueTypeBridge.IsRefBearingValueProxy(t);         // ref-bearing value proxy — bridged whole (C6 one discriminator)

    // A NATURAL (BCL) delegate spelled at the hook boundary classifies as ConvKind.Delegate so it fails LOUD in the
    // runtime (Il2CppConvRuntime), not as a Leaf identity pass-through. The delegate family is BclToIl2Cpp-only: the
    // IL seam flips an Il2CppSystem.Action/Func PARAM to System.Action/Func for a DIRECT call, but there is no
    // reverse (il2cpp->BCL) bridge — so a hook that SPELLS System.Action for such a flipped param cannot be marshaled,
    // and identity-passing the raw il2cpp pointer as a CLR delegate would be a silent mis-marshal (a wild invoke).
    // A hook that wants to OBSERVE the delegate spells the raw Il2CppSystem.Action instead (Il2CppObjectBase -> leaf,
    // above). Excludes the il2cpp proxy (already a leaf) — only a genuine System.Delegate-derived CLR type is caught.
    public override ConvShape Classify(Type t)
    {
        if (typeof(Delegate).IsAssignableFrom(t) && !typeof(Il2CppObjectBase).IsAssignableFrom(t))
            return ConvShape.Container(ConvKind.Delegate);       // no children: the node exists only to fail loud

        // The erased top: a hook that SPELLS the natural `object` for a bare il2cpp handle (a Callback<object> element,
        // an object param) gets ConvKind.Object — a CHILDLESS terminal (like non-generic Task/Object) whose runtime
        // marshal bridges object <-> Il2CppSystem.Object (Families.cs, ADDITIVE). This is the mod-SPELLED System.Object
        // ONLY — the raw Il2CppSystem.Object (an Il2CppObjectBase) still short-circuits to the identity leaf via IsLeaf
        // above, so the two spellings diverge by what the mod wrote, never removing the raw one. Excludes any real CLR
        // subtype (a mod's own class): EXACT typeof(object), never IsAssignableFrom.
        if (t == typeof(object))
            return ConvShape.Container(ConvKind.Object);

        // The game exception: a hook SPELLS the natural System.Exception for an Il2CppSystem.Exception param (an
        // Il2CppObjectBase, not a System.Exception). ConvKind.Exception — a childless terminal whose runtime marshal
        // wraps the il2cpp exception in the GameException facade (in) and unwraps it (out). EXACT System.Exception only
        // (a derived game/CLR exception is a follow-up); the GameException facade it produces IS a System.Exception
        // subclass but is never re-classified — the Conv tree is built from the hook's spelled type, always System.Exception.
        if (t == typeof(System.Exception))
            return ConvShape.Container(ConvKind.Exception);

        // Non-generic Task: the pure base leaves it a Leaf and defers to "the seam's subclass" to refine it (Conv.cs
        // Classify). Refine it here to a CHILDLESS Task container (its correspondence IS registered — Families.cs,
        // arity 0) so a hook that FABRICATES a bare `Task` return — a completed no-result op like RegenerateToken /
        // ValidateVersion / GetFinals / LocalRaidEnded — marshals through the Task bridge (DematerializeTask rides a
        // Task`1<bool> upcast, since Task<T> : Task) instead of identity-LEAKING the managed Task object into the
        // il2cpp return slot (which threw InvalidCastException: Task`1[VoidTaskResult] -> Il2CppObjectBase). Only the
        // EXACT non-generic Task — Task<T> is generic and already classified by the base via ByBclOpenType.
        if (t == typeof(System.Threading.Tasks.Task))
            return ConvShape.Container(ConvKind.Task);

        return base.Classify(t);
    }
}
