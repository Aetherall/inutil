using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;   // Il2CppObjectBase — the il2cpp value at a Task node
using Inutil.Schema;
using Inutil.Sugar;

namespace Inutil.Marshal;

// IConvRuntime over REAL il2cpp — the seam half of the recursive marshalling tree. Conv (inutil.Schema) owns the
// STRUCTURE (which shape, which children, both directions); this owns the per-kind il2cpp WORK. Kept out of the pure
// module because it touches Il2CppInterop; the same Conv tree is exercised offline against a synthetic runtime
// (Inutil.Schema.Tests) so the COMPOSITION is proven before this runs in a game. A Leaf is native identity
// pass-through; every container kind (Array / List / Enumerable / Set / MutableList / Dictionary / Nullable / Tuple /
// Task / Result / Callback / Object / Exception / DelegateIn) routes through ContainerBridge, each child recursing.
// The WRITE direction must RESOLVE and CONSTRUCT the il2cpp type (the read side gets it for free), so it is
// validated in-game; offline it fails loud at resolution.
public sealed class Il2CppConvRuntime : IConvRuntime
{
    // Il2CppSugar.WrapTaskT<T>(Il2CppObjectBase) — closed per spelled element type for the nested/boundary Task
    // path (the hot Task-RETURN path is closed at PATCH time by the seam's spliced call; this is the runtime
    // fallback for a Task reached as a nested element or at the hook boundary).
    static readonly MethodInfo _wrapTaskT = typeof(Il2CppSugar).GetMethods()
        .Single(m => m.Name == nameof(Il2CppSugar.WrapTaskT)
                     && m.GetParameters() is [{ ParameterType.Name: nameof(Il2CppObjectBase) }]);

    public object? Leaf(Conv node, object? value, ConvDirection dir) => value;

    public object? Container(Conv node, object? value, ConvDirection dir)
    {
        // Null short-circuits to null for every REFERENCE kind (a null List/Task/Array/Object round-trips as null,
        // and an empty Nullable already read as null on the ToManaged side). The ONE exception is WRITING an empty
        // value-type Nullable: an il2cpp Nullable<T'> is a VALUE, so a null reference would NRE when the proxy body
        // unboxes the param — an empty (HasValue=false) Nullable<T'> is the correct ToIl2Cpp image of a null T?.
        if (value is null)
            return node.Kind == ConvKind.Nullable && dir == ConvDirection.ToIl2Cpp
                ? ContainerBridge.EmptyNullable(node)
                : null;
        return node.Kind switch
        {
            ConvKind.Task when dir == ConvDirection.ToManaged => WrapTask(node, value),
            ConvKind.Task when dir == ConvDirection.ToIl2Cpp => ContainerBridge.DematerializeTask(node, value, dir, this),
            ConvKind.Array when dir == ConvDirection.ToManaged => ContainerBridge.MaterializeArray(node, value, dir, this),
            ConvKind.Array when dir == ConvDirection.ToIl2Cpp => ContainerBridge.DematerializeArray(node, value, dir, this),
            (ConvKind.List or ConvKind.Enumerable or ConvKind.Set) when dir == ConvDirection.ToManaged => ContainerBridge.MaterializeList(node, value, dir, this),
            (ConvKind.List or ConvKind.Enumerable or ConvKind.Set) when dir == ConvDirection.ToIl2Cpp => ContainerBridge.DematerializeList(node, value, dir, this),
            // MutableList — the WRITE-THROUGH IList<T> spelling: ToManaged wraps the game's
            // il2cpp list in a live adapter (no copy); ToIl2Cpp hands the HELD list back by identity (a fabricated
            // IList falls back to the copying dematerialize). See ContainerBridge.WrapMutableList.
            ConvKind.MutableList when dir == ConvDirection.ToManaged => ContainerBridge.WrapMutableList(node, value, this),
            ConvKind.MutableList when dir == ConvDirection.ToIl2Cpp => ContainerBridge.DematerializeMutableList(node, value, dir, this),
            ConvKind.Dictionary when dir == ConvDirection.ToManaged => ContainerBridge.MaterializeDictionary(node, value, dir, this),
            ConvKind.Dictionary when dir == ConvDirection.ToIl2Cpp => ContainerBridge.DematerializeDictionary(node, value, dir, this),
            ConvKind.Nullable when dir == ConvDirection.ToManaged => ContainerBridge.MaterializeNullable(node, value, dir, this),
            ConvKind.Nullable when dir == ConvDirection.ToIl2Cpp => ContainerBridge.DematerializeNullable(node, value, dir, this),
            ConvKind.Tuple when dir == ConvDirection.ToManaged => ContainerBridge.MaterializeTuple(node, value, dir, this),
            ConvKind.Tuple when dir == ConvDirection.ToIl2Cpp => ContainerBridge.DematerializeTuple(node, value, dir, this),
            // Result — the per-game reference-wrapper mirror (Inutil.Bridge.Result<T> <-> the game's Comfort.Common
            // .Result<T'>). The element recurses through node.Children[0]; the game Result is built/read through the
            // per-game Mirror shim (ContainerBridge keeps the il2cpp construction game-free).
            ConvKind.Result when dir == ConvDirection.ToManaged => ContainerBridge.MaterializeResult(node, value, dir, this),
            ConvKind.Result when dir == ConvDirection.ToIl2Cpp => ContainerBridge.DematerializeResult(node, value, dir, this),
            // Callback — the INBOUND twin of Result (Inutil.Bridge.Callback<T> <-> the game's Comfort.Common.Callback<T'>).
            // ToManaged wraps the received game callback in the mirror (its Ok/Fail marshal outbound internally, reusing
            // the Result path). ToIl2Cpp is a hook RETURNING a callback — meaningless; fail loud, like the delegate reverse.
            ConvKind.Callback when dir == ConvDirection.ToManaged => ContainerBridge.MaterializeCallback(node, value, dir, this),
            ConvKind.Callback when dir == ConvDirection.ToIl2Cpp => throw CallbackReturn(node),
            // Object — the erased top (a hook SPELLED `object` for a bare Il2CppSystem.Object handle). ToManaged: the game
            // handed us an il2cpp object; give the mod the proxy AS object by identity (it casts to a concrete il2cpp
            // type). ToIl2Cpp: the mod hands a value back to the game (a Callback<object>.Ok payload) — a null already
            // short-circuited above; a non-null must BE an il2cpp reference to cross, else fail loud (no wild pointer).
            ConvKind.Object when dir == ConvDirection.ToManaged => value,
            ConvKind.Object when dir == ConvDirection.ToIl2Cpp => ObjectToIl2Cpp(node, value),
            // Exception — a hook SPELLS System.Exception for a game Il2CppSystem.Exception param. ToManaged wraps the
            // il2cpp exception (an Il2CppObjectBase) in the GameException facade (so .Message/.ToString forward); ToIl2Cpp
            // (a Proceed forwarding it on) unwraps the facade back to the original il2cpp exception. Null short-circuits.
            ConvKind.Exception when dir == ConvDirection.ToManaged => WrapException(node, value),
            ConvKind.Exception when dir == ConvDirection.ToIl2Cpp => UnwrapException(node, value),
            // DelegateIn — the inbound-delegate mirror (Inutil.Bridge.Action/Func<..> <-> the game's System.Action`N/Func`N
            // param, the GENERAL twin of Callback). ToManaged MATERIALIZES the received game delegate into the mirror (its
            // Invoke flips args/return + invokes the game delegate internally, via Il2CppInterop — no per-game shim). ToIl2Cpp
            // is a hook RETURNING a delegate mirror — meaningless; fail loud, like the delegate reverse / callback return.
            ConvKind.DelegateIn when dir == ConvDirection.ToManaged => ContainerBridge.MaterializeDelegate(node, value, dir, this),
            ConvKind.DelegateIn when dir == ConvDirection.ToIl2Cpp => throw DelegateReverse(node, dir),
            ConvKind.Delegate => throw DelegateReverse(node, dir),
            _ => throw Pending(node, dir),
        };
    }

    // A hook's natural `object` -> the game's Il2CppSystem.Object (ConvKind.Object write side). Null short-circuits in
    // Container; a non-null value must already BE an il2cpp reference (an Il2CppObjectBase proxy — the game handed it to
    // the hook, or the hook holds a game object) to cross back. A foreign CLR object (a mod's OWN class instance) has no
    // il2cpp identity, so it FAILS LOUD rather than silently marshalling a wild pointer — the erased top's honest limit.
    static object ObjectToIl2Cpp(Conv node, object value)
        => value is Il2CppObjectBase ? value : throw ForeignObject(node, value);

    static NotSupportedException ForeignObject(Conv node, object value) => new(
        $"Il2CppConvRuntime: a hook handed a foreign CLR object ({value.GetType().FullName}) to an `object` position " +
        "marshalled to the game's Il2CppSystem.Object — only an il2cpp reference (an Il2CppObjectBase proxy) or null can " +
        "cross (the erased top has no il2cpp identity for an arbitrary managed object). Fail-loud, never a wild pointer.");

    // ConvKind.Exception ToManaged: wrap the game's il2cpp exception (an Il2CppObjectBase) in the GameException facade so
    // the hook reads .Message/.ToString off a System.Exception. Null short-circuited in Container.
    static object WrapException(Conv node, object value)
        => value is Il2CppObjectBase o ? new GameException(o)
            : throw new NotSupportedException($"Il2CppConvRuntime: a game Exception param arrived as {value.GetType().FullName}, not an Il2CppObjectBase — cannot present it as System.Exception. Fail-loud, never a silent mis-marshal.");

    // ConvKind.Exception ToIl2Cpp: a hook Proceed-ing the exception on hands back the GameException facade inutil minted
    // -> the ORIGINAL il2cpp exception it holds, by identity. A foreign CLR exception (the mod built its own) has no
    // il2cpp identity, so it FAILS LOUD rather than marshalling a wild pointer.
    static object UnwrapException(Conv node, object value)
        => value is GameException g ? g.Il2Cpp
            : throw new NotSupportedException($"Il2CppConvRuntime: a hook handed a foreign System.Exception ({value.GetType().FullName}) to a game Exception position — only the GameException facade inutil minted (holding the game's own il2cpp exception) can cross back. Fail-loud, never a wild pointer.");

    // The delegate family is BclToIl2Cpp-ONLY (call-site op_Implicit splice at the IL seam; no runtime bridge). A
    // Delegate node reaching HERE is a hook that SPELLED a natural System.Action/Func for a flipped delegate PARAM —
    // the reverse (il2cpp->BCL) direction, which has no faithful bridge. Fail loud with the escape hatch (spell the
    // raw Il2CppSystem.Action to observe the pointer), never identity-pass a raw il2cpp pointer as a CLR delegate.
    // A null delegate never reaches here (the null short-circuit above returns null — null is null either way).
    static NotSupportedException DelegateReverse(Conv node, ConvDirection dir) => new(
        $"Il2CppConvRuntime: {node.Managed} is a delegate reached {dir} — the natural System.Action/Func delegate family " +
        "is call-site-only (a bare lambda PASSED into a game method flips at the IL seam via op_Implicit); there is no " +
        "reverse il2cpp->BCL bridge for the NATURAL spelling. To RECEIVE a game delegate param, spell the inbound mirror " +
        "Inutil.Bridge.Action<..>/Func<..> (ConvKind.DelegateIn — holds the game delegate and invokes it), or spell the " +
        "raw Il2CppSystem.Action to observe the pointer. (A DelegateIn mirror reached ToIl2Cpp is a hook RETURNING a mirror, " +
        "which is meaningless — the mirror's own Invoke marshals outbound internally.) Fail-loud, never a silent mis-marshal.");

    // The callback family is Il2CppToBcl-ONLY: a hook RECEIVES a game callback param and the seam MATERIALIZES it into
    // the Inutil.Bridge.Callback<T> mirror. A Callback node reaching the ToIl2Cpp (write/return) side is a hook that
    // RETURNS a callback — which has no meaning (you don't hand the game a mirror). Fail loud, like the delegate reverse.
    static NotSupportedException CallbackReturn(Conv node) => new(
        $"Il2CppConvRuntime: {node.Managed} is an Inutil.Bridge.Callback reached ToIl2Cpp — a callback mirror is " +
        "INBOUND only (a hook RECEIVES a game callback param; the mirror's own Ok/Fail marshal outbound internally). " +
        "A hook cannot RETURN a callback. Fail-loud, never a silent mis-marshal.");

    // node.Children[0] is the spelled element T; closing WrapTaskT<T> over it yields the natural Task<T> that
    // carries the il2cpp task by identity (the one shared carrier — Task<T> : Task).
    static object? WrapTask(Conv node, object value)
        => _wrapTaskT.MakeGenericMethod(node.Children[0].Managed)
                     .Invoke(null, new object?[] { (Il2CppObjectBase)value });

    // Unreachable safety net: the shape classifier emits only Leaf (routed through Leaf(), not here) and the container
    // kinds cased above (now including ConvKind.Object — the erased top — and ConvKind.Exception). Fail loud if a new
    // kind ever reaches here rather than silently mis-marshal an unhandled kind.
    static NotSupportedException Pending(Conv node, ConvDirection dir) => new(
        $"Il2CppConvRuntime: {node.Kind} {dir} has no container conversion and is not a Leaf — unhandled kind " +
        "(should be unreachable; fail-loud, never silent mis-marshal).");
}
