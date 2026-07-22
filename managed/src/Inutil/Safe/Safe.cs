// Inutil.Safe — the fault-guarded ways to touch the game (docs/contribution/architecture/17-reach-faces.md). The
// managed consumer of the native SEH fault-guard (inutil_core `inutil_guarded_invoke` — a Vectored-Exception-Handler
// + setjmp shim, since mingw C has no __try/__except; validated under Wine, native/tests/guard_smoke.c) — so a
// modder's seamless `player.TakeDamage(5)` over a flipped proxy can't hardware-crash the process. Two shapes:
//
//   • Shape A — TryInvoke(methodInfo, instance, argPtrs): the PROVEN substrate. `invoke_fn` is the real
//     `il2cpp_runtime_invoke`, so the guard's risky window is PURE NATIVE — a caught fault longjmps back past only
//     il2cpp's own native frames, exactly what guard_smoke validated.
//
//   • Shape B — Run(() => player.TakeDamage(5)): the ERGONOMIC face — fault-safety as a SCOPE over the seamless call
//     the modder would normally write. It runs a MANAGED delegate inside the guard via a reverse-P/Invoke trampoline
//     (see RunThunk), so a caught fault's longjmp ALSO unwinds a CLR reverse-P/Invoke transition frame — the extra
//     risk, de-risked at native level (guard_managed_smoke.c) and in-game (`safe.run.faulted-survives`: fault, then
//     more managed work on the same thread returns Ok — proving the frame chain survived).
//
// HONEST CAVEAT (mirrors the native header): after a CAUGHT fault the runtime is TAINTED — report and restart, don't
// keep invoking in a loop. A leaf fault (runtime_invoke reading a bogus method struct; a method's first `this` deref)
// holds no il2cpp lock, so the taint is benign; a fault deep in allocation/class-init/GC may not be. The guard is a
// DIAGNOSTIC / a seatbelt for suspect territory, not a general retry mechanism.
//
// Game-AGNOSTIC (il2cpp C ABI + the interop object pool only). Call on the game (il2cpp-attached) thread.
using System.Runtime.InteropServices;        // DllImport, NativeLibrary, UnmanagedCallersOnly
using Il2CppInterop.Runtime;                 // IL2CPP.*
using Il2CppInterop.Runtime.InteropTypes;    // Il2CppObjectBase (.Pointer)

namespace Inutil;

public static unsafe class Safe
{
    // inutil_core's SEH-guarded il2cpp_runtime_invoke: run `invoke_fn`(method,obj,params,&exc) under the fault guard.
    // Returns 0 on a clean call (*out_ret = boxed/raw return, *out_exc = any Il2CppException*), or the hardware fault
    // code (with *out_fault_addr) if it faulted — NO process crash either way. `params` is the void** args array (0 =
    // none); the out-params marshal void** <-> out nint.
    [DllImport("inutil_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint inutil_guarded_invoke(nint invoke_fn, nint method, nint obj, nint @params,
        out nint out_exc, out nint out_ret, out nint out_fault_addr);

    // The raw address of il2cpp_runtime_invoke, resolved from the game's native module (Unity IL2CPP always emits it
    // as GameAssembly.dll — both loaders load it; abi/boot/compat all key on that name). Cached; the guard needs a
    // real C function pointer, not the interop managed wrapper. TryLoad("GameAssembly") returns the already-loaded
    // module's handle (mapped once il2cpp is up), so this neither reloads nor needs a path.
    private static nint s_il2cppInvoke;
    private static nint Il2CppInvokeAddr()
    {
        nint a = s_il2cppInvoke;
        if (a != 0) return a;
        foreach (var name in new[] { "GameAssembly", "GameAssembly.dll" })
            if (NativeLibrary.TryLoad(name, out nint h) && NativeLibrary.TryGetExport(h, "il2cpp_runtime_invoke", out nint fn) && fn != 0)
                return s_il2cppInvoke = fn;
        throw new InvalidOperationException("Inutil.Safe: could not resolve il2cpp_runtime_invoke from GameAssembly (is il2cpp up?)");
    }

    // ── Shape A: the proven substrate — guarded raw invoke by MethodInfo* ────────────────────────────────────────

    /// <summary>Guarded call of `methodInfo` on `instance` (0 for a static method). `argPtrs` follow the
    /// il2cpp_runtime_invoke convention: value-type arg -> &amp;value; reference-type arg -> the object pointer.
    /// A NULL methodInfo throws (programmer error); a garbage-but-non-null one is caught by the guard -> Faulted.</summary>
    public static InvokeResult TryInvoke(nint methodInfo, nint instance, params nint[]? argPtrs)
    {
        if (methodInfo == 0)
            throw new ArgumentException("methodInfo is null — resolve a method first", nameof(methodInfo));
        argPtrs ??= Array.Empty<nint>();
        int n = argPtrs.Length;
        void** args = stackalloc void*[n == 0 ? 1 : n];
        for (int i = 0; i < n; i++) args[i] = (void*)argPtrs[i];
        uint code = inutil_guarded_invoke(Il2CppInvokeAddr(), methodInfo, instance, n == 0 ? 0 : (nint)args,
                                          out nint exc, out nint ret, out nint faultAddr);
        return code != 0 ? new InvokeResult(0, code, faultAddr, 0)
                         : new InvokeResult(ret, 0, 0, exc);
    }

    /// <summary>Guarded call taking an Il2CppObjectBase instance (null -> a static call, instance 0).</summary>
    public static InvokeResult TryInvoke(nint methodInfo, Il2CppObjectBase? instance, params nint[]? argPtrs)
        => TryInvoke(methodInfo, instance is null ? 0 : instance.Pointer, argPtrs);

    // ── Shape B: the ergonomic face — a fault-safe SCOPE over a seamless call ─────────────────────────────────────

    // The pending delegate + its outputs, per thread (the guard is a per-thread, synchronous window). Saved/restored
    // across each guard call so a nested Safe.Run inside a Safe.Run (the seamless call re-entering) doesn't clobber
    // the outer frame — mirroring the native ProceedFrame save/restore.
    [ThreadStatic] static Func<object?>? t_body;
    [ThreadStatic] static object? t_result;
    [ThreadStatic] static Exception? t_exc;

    // Passed AS invoke_fn: the guard calls this under setjmp with our dummy (method,obj,params,exc) — all ignored. We
    // run the pending managed delegate HERE. Three outcomes:
    //   • clean            -> stash the return, return 0 (the guard reports code 0).
    //   • managed throw     -> CAUGHT and stashed; return 0. A managed exception must NOT unwind across this native
    //                          boundary (that is undefined at a reverse-P/Invoke edge), so only hardware faults escape.
    //   • hardware fault    -> the VEH longjmps back INTO the guard; we never return, the try/catch never completes,
    //                          t_result/t_exc stay as they were (ignored on a Faulted verdict).
    [UnmanagedCallersOnly]
    static nint RunThunk(nint method, nint obj, nint @params, nint exc)
    {
        Func<object?>? body = t_body;                 // captured once at entry (a nested Run may rebind t_body)
        try { t_result = body!(); return 0; }
        catch (Exception e) { t_exc = e; return 0; }
    }

    // Run `body` under the guard and hand back the fault-status + the delegate's (boxed) managed return.
    static InvokeResult RunBoxed(Func<object?> body, out object? result)
    {
        Func<object?>? savedBody = t_body; object? savedResult = t_result; Exception? savedExc = t_exc;
        t_body = body; t_result = null; t_exc = null;
        uint code = inutil_guarded_invoke((nint)(delegate* unmanaged<nint, nint, nint, nint, nint>)&RunThunk,
                                          0, 0, 0, out nint _, out nint _, out nint faultAddr);
        result = t_result;
        Exception? mex = t_exc;
        t_body = savedBody; t_result = savedResult; t_exc = savedExc;   // restore for re-entrancy
        if (code != 0)      return new InvokeResult(0, code, faultAddr, 0);   // hardware fault: delegate did NOT complete
        if (mex is not null) return new InvokeResult(0, 0, 0, 0, mex);        // managed throw
        return new InvokeResult(0, 0, 0, 0);                                  // Ok (managed return carried in `result`)
    }

    /// <summary>Run a seamless game action under the fault guard. A hardware fault ANYWHERE in the call (a torn
    /// `this`, a freed object, a native null deref inside the proxy's il2cpp call) becomes InvokeResult.Faulted +
    /// the code/address instead of a process death; a managed exception becomes InvokeResult.Threw; otherwise Ok.
    /// This is the shape a modder writes normally, wrapped — `Safe.Run(() => player.TakeDamage(5))`.</summary>
    public static InvokeResult Run(Action body)
    {
        if (body is null) throw new ArgumentNullException(nameof(body));
        return RunBoxed(() => { body(); return null; }, out _);
    }

    /// <summary>The value-returning form: run a seamless game function under the guard and get back its result
    /// (valid only when Ok) alongside the fault-status — `Safe.Run(() => player.GetHealth())`.</summary>
    public static SafeResult<T> Run<T>(Func<T> body)
    {
        if (body is null) throw new ArgumentNullException(nameof(body));
        InvokeResult status = RunBoxed(() => body(), out object? r);
        return new SafeResult<T>(status, status.Ok && r is T tv ? tv : default);
    }
}
