#nullable enable
using System;
using System.Runtime.CompilerServices;

// The runtime marshalling seam (ContainerBridge, in the Inutil assembly) reads the game shim + IMirrorResult.
[assembly: InternalsVisibleTo("Inutil")]

// A dedicated sub-namespace (NOT the Inutil.Schema root) on purpose: mods often `global using Inutil;` while ALSO
// `using` the game's Comfort.Common.Result — a root-level Result would make bare `Result` ambiguous. Mods spell
// the mirror explicitly: Inutil.Bridge.Result<T>.
namespace Inutil.Bridge;

// A managed MIRROR of a game "result" value-proxy generic (EFT's Comfort.Common.Result<T>).
//
// WHY it exists: Il2CppInterop only reified the Result instantiations the GAME uses (e.g.
// Result<Il2CppReferenceArray<Foo>>), so closing Result<> over the NATURAL element (Result<Foo[]>) is a BROKEN
// proxy — its Il2CppClassPointerStore static-init throws (measured 2026-07-05). So a hook spells
// Task<Inutil.Bridge.Result<Foo[]>> and the engine marshals this mirror to the game's Result<Il2CppReferenceArray
// <Foo>> at the seam (ContainerBridge.{Materialize,Dematerialize}Result), flipping the inner element through the
// recursive Conv child. The game-specific "how to build/read the real Result" is a per-game shim through
// Mirror.RegisterResult — inutil stays game-agnostic. Families.cs registers only the game-AGNOSTIC correspondence
// (this mirror <-> ConvKind.Result); it never names Comfort.Common.
public readonly struct Result<T> : IMirrorResult
{
    public readonly T Value;
    public readonly string? Error;
    public readonly int ErrorCode;

    public Result(T value) { Value = value; Error = null; ErrorCode = 0; }
    public Result(T value, string? error, int errorCode = 0) { Value = value; Error = error; ErrorCode = errorCode; }

    public bool Succeed => Error is null;

    object? IMirrorResult.BoxedValue => Value;
    string? IMirrorResult.Error => Error;
    int IMirrorResult.ErrorCode => ErrorCode;
}

// Non-generic read surface so ContainerBridge.DematerializeResult reads a boxed Inutil.Bridge.Result<T> without
// per-conversion reflection over the closed T.
internal interface IMirrorResult
{
    object? BoxedValue { get; }
    string? Error { get; }
    int ErrorCode { get; }
}

// A managed MIRROR of a game "callback" reference-proxy generic (EFT's Comfort.Common.Callback<T>) — the INBOUND
// twin of Result. WHY it exists: a game method hands a hook a Callback<Il2CppReferenceArray<Foo>> it must later
// invoke; the natural close Callback<Foo[]> is the SAME broken il2cpp instantiation, AND there is no il2cpp->BCL
// delegate bridge to receive it as a System.Action. So the hook spells Inutil.Bridge.Callback<Foo[]> and the seam
// (ContainerBridge.MaterializeCallback) hands it THIS mirror, which HOLDS the game callback and marshals OUTBOUND
// on Ok/Fail: it builds a Result<T>, routes it through the Result dematerialize path, then invokes the game
// callback through the per-game Mirror.RegisterCallback shim. A REFERENCE type on purpose: a game callback CAN be
// null (hooks null-check it), so the mirror must be nullable; the boundary hands a null game callback back as a
// null mirror.
public sealed class Callback<T>
{
    readonly System.Action<Result<T>> _invoke;   // seam-installed: build the game Result over T' and invoke the held game callback

    /// <summary>Constructed by the marshalling seam (ContainerBridge.MaterializeCallback); mods never <c>new</c> this.</summary>
    public Callback(System.Action<Result<T>> invoke) => _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));

    /// <summary>Invoke the game callback with a SUCCESS result carrying <paramref name="value"/> (marshalled through the recursive child).</summary>
    public void Ok(T value) => _invoke(new Result<T>(value));

    /// <summary>Invoke the game callback with an ERROR result (no value).</summary>
    public void Fail(string error, int errorCode = 0) => _invoke(new Result<T>(default!, error, errorCode));

    /// <summary>Invoke the game callback with a result you build yourself.</summary>
    public void Invoke(Result<T> result) => _invoke(result);
}

// The INBOUND-DELEGATE mirror family — the GENERAL twin of Callback<T> (inbound counterpart of the outbound-only
// ConvKind.Delegate). WHY it exists: a game method hands a hook a System.Action/Func param it must invoke; the
// hook can neither receive it as a natural System.Action (no il2cpp->BCL bridge — ConvKind.Delegate fails loud)
// nor spell a broken il2cpp close. So the hook spells Inutil.Bridge.Action/Func and the seam
// (ContainerBridge.MaterializeDelegate) hands it one of THESE, which HOLD the game delegate (<see cref="Raw"/>) +
// a seam-installed invoker. On Invoke the mirror boxes the SPELLED managed args and calls the invoker, which
// flips each ToIl2Cpp through the recursive Conv child, invokes the game delegate via the il2cpp Action`N/Func`N
// proxy's OWN Invoke, and (for Func) flips the return ToManaged. UNLIKE Callback there is NO per-game shim — the
// il2cpp delegate proxies live in Il2Cppmscorlib (game-agnostic). Families.cs registers only the game-AGNOSTIC
// correspondence (these mirrors <-> ConvKind.DelegateIn); it never names a game type.
public abstract class DelegateMirror
{
    /// <summary>The held game delegate (an il2cpp Action`N/Func`N proxy) — for advanced manual use. Prefer <c>Invoke</c>.</summary>
    public object Raw { get; }

    // The seam-installed invoker: boxed SPELLED managed args -> (flip -> game delegate Invoke -> flip) -> boxed managed return.
    private protected readonly System.Func<object?[], object?> _fn;

    private protected DelegateMirror(object raw, System.Func<object?[], object?> fn)
    {
        Raw = raw ?? throw new ArgumentNullException(nameof(raw));
        _fn = fn ?? throw new ArgumentNullException(nameof(fn));
    }
}

/// <summary>Inbound mirror of a game <c>System.Action&lt;T&gt;</c> param. Constructed by the marshalling seam; mods never <c>new</c> this.</summary>
public sealed class Action<T> : DelegateMirror
{
    public Action(object raw, System.Func<object?[], object?> fn) : base(raw, fn) { }
    public void Invoke(T arg) => _fn(new object?[] { arg });
}

/// <summary>Inbound mirror of a game <c>System.Action&lt;T1,T2&gt;</c> param.</summary>
public sealed class Action<T1, T2> : DelegateMirror
{
    public Action(object raw, System.Func<object?[], object?> fn) : base(raw, fn) { }
    public void Invoke(T1 arg1, T2 arg2) => _fn(new object?[] { arg1, arg2 });
}

/// <summary>Inbound mirror of a game <c>System.Action&lt;T1,T2,T3&gt;</c> param.</summary>
public sealed class Action<T1, T2, T3> : DelegateMirror
{
    public Action(object raw, System.Func<object?[], object?> fn) : base(raw, fn) { }
    public void Invoke(T1 arg1, T2 arg2, T3 arg3) => _fn(new object?[] { arg1, arg2, arg3 });
}

/// <summary>Inbound mirror of a game <c>System.Func&lt;T,TResult&gt;</c> param. The invoker's boxed return is cast to <typeparamref name="TResult"/>.</summary>
public sealed class Func<T, TResult> : DelegateMirror
{
    public Func(object raw, System.Func<object?[], object?> fn) : base(raw, fn) { }
    public TResult Invoke(T arg) => (TResult)_fn(new object?[] { arg })!;
}

/// <summary>Inbound mirror of a game <c>System.Func&lt;T1,T2,TResult&gt;</c> param.</summary>
public sealed class Func<T1, T2, TResult> : DelegateMirror
{
    public Func(object raw, System.Func<object?[], object?> fn) : base(raw, fn) { }
    public TResult Invoke(T1 arg1, T2 arg2) => (TResult)_fn(new object?[] { arg1, arg2 })!;
}

/// <summary>The ONE home for "which inbound-delegate mirror corresponds to which <c>System.Action/Func</c>, and
/// is it a Func". Both seams consult it so the il2cpp naming (ContainerBridge) and hook-param binding (HookMatch)
/// can never drift. A mirror's generic-arg COUNT equals the System delegate's arity, and the il2cpp
/// <c>Action`N</c>/<c>Func`N</c> proxy's type args are that same full list — so one mapping serves every site.</summary>
public static class DelegateMirrors
{
    // (mirror open) -> the System.Action/Func open it stands in for (null if not a mirror) + whether it is a Func.
    static (Type? System, bool IsFunc) Map(Type mirrorOpen)
    {
        if (mirrorOpen == typeof(Action<>))   return (typeof(System.Action<>),   false);
        if (mirrorOpen == typeof(Action<,>))  return (typeof(System.Action<,>),  false);
        if (mirrorOpen == typeof(Action<,,>)) return (typeof(System.Action<,,>), false);
        if (mirrorOpen == typeof(Func<,>))    return (typeof(System.Func<,>),    true);
        if (mirrorOpen == typeof(Func<,,>))   return (typeof(System.Func<,,>),   true);
        return (null, false);
    }

    /// <summary>The <c>System.Action/Func</c> open the mirror open corresponds to, or null if it is not an inbound-delegate mirror.</summary>
    public static Type? SystemOpen(Type mirrorOpen) => Map(mirrorOpen).System;

    /// <summary>True iff the mirror open is a Func mirror (its LAST generic arg is the return), false for an Action mirror.</summary>
    public static bool IsFunc(Type mirrorOpen) => Map(mirrorOpen).IsFunc;

    /// <summary>True iff the open type is a registered inbound-delegate mirror.</summary>
    public static bool IsMirror(Type mirrorOpen) => Map(mirrorOpen).System is not null;
}

/// <summary>Registration slot for a game's value-proxy <c>Result&lt;T&gt;</c>. The build/read lambdas reference
/// the game's own proxy (which inutil must not), so a per-game shim calls <see cref="RegisterResult"/> ONCE at
/// startup, before inutil wires the hooks (use a <c>[ModuleInitializer]</c>). Held in the pure Schema module
/// (only System.Type + object cross the delegate boundary — no il2cpp type named here), so Inutil.Schema stays
/// dependency-free while ContainerBridge consumes the registered lambdas.</summary>
public static class Mirror
{
    internal static Type? GameResultOpen;                                     // typeof(Comfort.Common.Result<>)
    internal static System.Func<Type, object?, string?, int, object>? BuildResult;   // (elemIl2, valueIl2, error, code) -> boxed game Result<elemIl2>
    internal static System.Func<object, (object? value, string? error, int code)>? ReadResult;   // game Result -> its parts

    /// <summary>True once a game Result has been registered (the engine gates the ConvKind.Result bridge on it).</summary>
    public static bool IsResultRegistered => GameResultOpen is not null && BuildResult is not null && ReadResult is not null;

    /// <summary>Teach inutil how to build/read the game's <c>Result&lt;T&gt;</c> value-proxy.</summary>
    /// <param name="gameResultOpen">The OPEN generic, e.g. <c>typeof(Comfort.Common.Result&lt;&gt;)</c>.</param>
    /// <param name="build">(closed il2cpp element type, already-flipped il2cpp value, error, code) -> the boxed game Result.</param>
    /// <param name="read">a boxed game Result -> its (il2cpp value, error, code).</param>
    public static void RegisterResult(
        Type gameResultOpen,
        System.Func<Type, object?, string?, int, object> build,
        System.Func<object, (object? value, string? error, int code)> read)
    {
        GameResultOpen = gameResultOpen ?? throw new ArgumentNullException(nameof(gameResultOpen));
        BuildResult = build ?? throw new ArgumentNullException(nameof(build));
        ReadResult = read ?? throw new ArgumentNullException(nameof(read));
    }

    internal static Type? GameCallbackOpen;                                   // typeof(Comfort.Common.Callback<>)
    internal static System.Action<object, object>? InvokeCallback;                   // (gameCallback, gameResult) -> gameCallback.Invoke(gameResult)

    /// <summary>True once a game Callback has been registered (the engine gates the ConvKind.Callback bridge on it).</summary>
    public static bool IsCallbackRegistered => GameCallbackOpen is not null && InvokeCallback is not null;

    /// <summary>Teach inutil how to invoke the game's <c>Callback&lt;T&gt;</c> reference-proxy. A Callback is invoked WITH
    /// a Result, so this REUSES the registered Result shim (<see cref="RegisterResult"/>) to build the game Result — only
    /// "how to call <c>Callback.Invoke(Result)</c>" is new here.</summary>
    /// <param name="gameCallbackOpen">The OPEN generic, e.g. <c>typeof(Comfort.Common.Callback&lt;&gt;)</c>.</param>
    /// <param name="invoke">(the game Callback, the game Result) -> invoke it (<c>callback.Invoke(result)</c>).</param>
    public static void RegisterCallback(Type gameCallbackOpen, System.Action<object, object> invoke)
    {
        GameCallbackOpen = gameCallbackOpen ?? throw new ArgumentNullException(nameof(gameCallbackOpen));
        InvokeCallback = invoke ?? throw new ArgumentNullException(nameof(invoke));
    }
}
