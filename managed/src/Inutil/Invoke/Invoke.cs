// Inutil.Invoke — call a game method BY NAME on a handle whose type you can't (or won't) name at the call site. The
// method-side twin of Inutil.Fields: resolve name+arity on the object's RUNTIME class, marshal natural C# args, and
// invoke. The GUARDED invoke itself is Inutil.Safe's (Safe.TryInvoke over inutil_core's fault-guard) — this layer
// adds only name resolution + arg marshalling + allocation, so there is ONE guarded-invoke implementation, not two.
//
// docs/contribution/architecture/17-reach-faces.md — the ESCAPE HATCH, not the default reach. When a typed face is
// possible (the type is known at author time) a modder writes `player.TakeDamage(5)` or wraps it in `Safe.Run(...)`.
// This exists for the irreducible cases: an ERASED Il2CppSystem.Object handle (a hook arg / collection element held
// as a base ref), a type not knowable at author time (DLC / runtime-generated), or the REPL.
//
// Game-AGNOSTIC (il2cpp C ABI + the interop object pool). Call on the game (il2cpp-attached) thread. Guarded results
// are Inutil.InvokeResult (Ok/Faulted/Threw); a NULL/absent method throws (a typo is a programmer error), but the
// CALL itself is fault-guarded.
using System;
using System.Runtime.CompilerServices;      // RuntimeHelpers.RunClassConstructor (force T's cctor)
using System.Runtime.InteropServices;        // GCHandle (pin a value-type arg)
using Il2CppInterop.Runtime;                 // IL2CPP.il2cpp_*, Il2CppClassPointerStore<T>
using Il2CppInterop.Runtime.InteropTypes;    // Il2CppObjectBase (.Pointer)

namespace Inutil;

public static class Invoke
{
    // ── method resolution (name -> MethodInfo*, chain-walked like Fields.FindField) ─────────────────────────

    /// <summary>Resolve a method by name + arg count on `klass` OR any ancestor; 0 if no class in the chain has it.
    /// (il2cpp_class_get_method_from_name keys on name+argc only — it can't separate same-arity overloads.)</summary>
    public static nint Resolve(nint klass, string method, int argc)
    {
        for (nint k = klass; k != 0; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            nint m = IL2CPP.il2cpp_class_get_method_from_name(k, method, argc);
            if (m != 0) return m;
        }
        return 0;
    }

    /// <summary>Resolve a method on `target`'s RUNTIME class (the actual most-derived type). 0 for null / absent.</summary>
    public static nint MethodOf(Il2CppObjectBase? target, string method, int argc)
        => target is null ? 0 : Resolve(IL2CPP.il2cpp_object_get_class(target.Pointer), method, argc);

    // ── allocation (il2cpp_object_new — zero-init, NO constructor) ───────────────────────────────────────────

    /// <summary>Allocate a zero-initialized instance of `klass` (NO constructor runs). Run one via
    /// Call(obj, ".ctor", …) if the type needs it. 0 if klass is 0.</summary>
    public static nint New(nint klass) => klass == 0 ? 0 : IL2CPP.il2cpp_object_new(klass);

    /// <summary>Allocate a zero-initialized `T` and wrap it as its interop proxy (NO constructor runs — see New(nint)).
    /// null if T's il2cpp class can't be resolved. Forces T's static cctor first so the class pointer is populated.</summary>
    public static T? New<T>() where T : Il2CppObjectBase
    {
        RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);   // populate Il2CppClassPointerStore<T>.NativeClassPtr
        nint klass = Il2CppClassPointerStore<T>.NativeClassPtr;
        if (klass == 0) return null;
        nint obj = IL2CPP.il2cpp_object_new(klass);
        return obj == 0 ? null : Il2CppInterop.Runtime.Runtime.Il2CppObjectPool.Get<T>(obj);
    }

    // ── guarded invoke by name (the CALL is fault-guarded via Inutil.Safe; a garbage target -> Faulted) ─────

    /// <summary>Guarded call resolving the method BY NAME on `target`'s runtime class (argc = argPtrs.Length). Raw
    /// arg pointers follow the il2cpp_runtime_invoke convention (value-type arg -> &amp;value; reference-type arg -> the
    /// object pointer). Throws if the name+arity doesn't resolve (a typo is a programmer error); the CALL is guarded.</summary>
    public static InvokeResult TryInvoke(Il2CppObjectBase target, string method, params nint[]? argPtrs)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        int argc = argPtrs?.Length ?? 0;
        nint mi = MethodOf(target, method, argc);
        if (mi == 0)
            throw new ArgumentException($"method '{method}' (argc {argc}) not found on {Probe.KlassName(target.Pointer) ?? "<obj>"}", nameof(method));
        return Safe.TryInvoke(mi, target.Pointer, argPtrs);   // the ONE guarded-invoke implementation
    }

    /// <summary>Guarded call resolving BY NAME with natural C# args auto-marshalled — no unsafe pointers at the call
    /// site. Marshalling follows the (ground-truth) runtime_invoke convention: a value type (int/float/bool/an
    /// unmanaged struct) is passed as a PINNED pointer to its bytes; an Il2CppObjectBase (any proxy) is passed as its
    /// object pointer; a string is passed as a fresh il2cpp string; null is a null reference arg. Throws for an
    /// unresolved name or an unsupported arg type — the CALL itself is guarded (a fault -> InvokeResult.Faulted). For a
    /// ref/out param, a raw il2cpp value struct you only hold as a pointer, or a hot path, use TryInvoke with your own
    /// arg pointers. To run a constructor: New&lt;T&gt;() then Call(obj, ".ctor", args).</summary>
    public static unsafe InvokeResult Call(Il2CppObjectBase target, string method, params object?[]? args)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        args ??= Array.Empty<object?>();
        int argc = args.Length;
        nint mi = MethodOf(target, method, argc);
        if (mi == 0)
            throw new ArgumentException($"method '{method}' (argc {argc}) not found on {Probe.KlassName(target.Pointer) ?? "<obj>"}", nameof(method));

        var pins = new System.Collections.Generic.List<GCHandle>(argc);
        try
        {
            nint[] argPtrs = new nint[argc];
            for (int i = 0; i < argc; i++)
            {
                switch (args[i])
                {
                    case null:               argPtrs[i] = 0; break;                                 // null reference arg
                    case Il2CppObjectBase o: argPtrs[i] = o.Pointer; break;                         // reference: the object ptr, direct
                    case string s:           argPtrs[i] = IL2CPP.ManagedStringToIl2Cpp(s); break;   // il2cpp string: direct
                    case { } v when v.GetType().IsValueType:                                        // value type: pinned pointer to the bytes
                        GCHandle h;
                        try { h = GCHandle.Alloc(v, GCHandleType.Pinned); }
                        catch (ArgumentException) { throw new NotSupportedException($"arg {i}: {v.GetType()} is not blittable — use TryInvoke with your own arg pointer"); }
                        pins.Add(h);
                        argPtrs[i] = h.AddrOfPinnedObject();
                        break;
                    default:
                        throw new NotSupportedException($"arg {i}: {args[i]!.GetType()} — Call marshals value types, Il2CppObjectBase, and string; use TryInvoke for others");
                }
            }
            return Safe.TryInvoke(mi, target.Pointer, argPtrs);   // pins stay alive across the SYNCHRONOUS guarded call, freed below
        }
        finally { foreach (var h in pins) h.Free(); }
    }

    // ── unguarded invoke (normal il2cpp semantics: a native fault CRASHES; a managed exception is RAISED) ────

    /// <summary>Fast-path call with NO fault guard — a hardware fault takes the process down (normal il2cpp behaviour)
    /// and a managed exception is raised as an Il2CppException. Use when you KNOW the method+args are good and don't
    /// want the guard's overhead; use TryInvoke/Call when the target is suspect.</summary>
    public static unsafe nint Unguarded(nint methodInfo, nint instance, params nint[]? argPtrs)
    {
        if (methodInfo == 0) throw new ArgumentException("methodInfo is null", nameof(methodInfo));
        argPtrs ??= Array.Empty<nint>();
        int n = argPtrs.Length;
        void** args = stackalloc void*[n == 0 ? 1 : n];
        for (int i = 0; i < n; i++) args[i] = (void*)argPtrs[i];
        nint exc = 0;
        nint ret = IL2CPP.il2cpp_runtime_invoke(methodInfo, instance, n == 0 ? null : args, ref exc);
        Il2CppException.RaiseExceptionIfNecessary(exc);
        return ret;
    }
}
