using System.Runtime.CompilerServices;      // ConditionalWeakTable — the CLR-carrier -> il2cpp-pointer identity map
using Il2CppInterop.Runtime.InteropTypes;   // Il2CppObjectBase (.Pointer) — the flipped proxy leaves this on the stack

namespace Inutil.Sugar;

// The runtime counterpart to the IL-rewrite seam's Task return-flip. When InteropPatch flips a Task-returning proxy
// to System.Task[<T>], it appends `call Il2CppSugar.WrapTask[T]` to the body (see InteropPatch/WrapHelpers.cs — it
// synthesizes references to EXACTLY these signatures). At load those calls resolve here, so the flipped proxy hands
// its caller a NATURAL CLR Task carrying the il2cpp task by identity.
//
// This is a REFERENCE bridge (Il2CppObjectBase), not a value LAYOUT one — so the bridge is by IDENTITY through one
// shared ConditionalWeakTable (Task and Task<T> share it — Task<T> : Task). The completed/pending FABRICATE (building
// an il2cpp task from a mod's returned Task) lives in the OTHER half — the hook-boundary marshaller, not here.
public static class Il2CppSugar
{
    // ---- natural iteration over il2cpp generic enumerables --------------------------------------------------
    // Il2CppInterop projects il2cpp interfaces per-interface: the generic IEnumerator<T> proxy carries only its
    // OWN member (Current) — MoveNext lives on the separate non-generic IEnumerator proxy — so neither pattern-
    // based foreach nor the BCL interface path binds, and LINQ is out entirely. Consumers were hand-rolling the
    // two-proxy dance (and one call site crashed the game walking the wrong cast). This is that dance, once:
    //   foreach (var item in inventory.GetPlayerItems(All).ToManaged()) …

    /// <summary>A managed IEnumerable view over an il2cpp generic enumerable — foreach/LINQ work naturally.
    /// Lazy; enumeration crosses the interop boundary per element (fine for control flow, not hot loops).</summary>
    public static System.Collections.Generic.IEnumerable<T> ToManaged<T>(
        this Il2CppSystem.Collections.Generic.IEnumerable<T> source)
    {
        if (source == null) yield break;
        var generic = source.GetEnumerator();                                   // Current lives here
        var mover = generic.Cast<Il2CppSystem.Collections.IEnumerator>();       // MoveNext lives here (same object)
        while (mover.MoveNext()) yield return generic.Current;
    }

    // CLR carrier -> il2cpp task pointer (boxed nint). A ConditionalWeakTable so a carrier the GC reclaims drops
    // its entry automatically — Task/Array identity forwarding with nowhere on the plain BCL type to stash
    // state. One instance, shared by WrapTask and WrapTaskT.
    static readonly ConditionalWeakTable<System.Threading.Tasks.Task, object> _taskPtr = new();

    // ---- non-generic Task ----------------------------------------------------------------------------------
    // The carrier is driven by the il2cpp task's OWN completion: a COMPLETED il2cpp task completes the carrier now; a
    // genuinely PENDING one is bridged so `await` on the CLR side actually waits. Identity is still stashed in _taskPtr
    // so a forwarding hook (`return Next.X()`) round-trips the SAME il2cpp task by pointer on the write side
    // (DematerializeTask/UnwrapTask), never a copy.
    public static System.Threading.Tasks.Task? WrapTask(Il2CppObjectBase? il2cppTask)
    {
        if (il2cppTask is null) return null;                   // a null il2cpp task round-trips as a null CLR task
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        _taskPtr.Add(tcs.Task, il2cppTask.Pointer);            // identity: carrier -> il2cpp pointer (write-side forwarding)
        if (Il2CppCompleted(il2cppTask)) tcs.TrySetResult(true);
        else BridgePending(il2cppTask, () => tcs.TrySetResult(true), ex => tcs.TrySetException(ex));   // pending: bridge completion
        return tcs.Task;
    }

    // Pointer-only fallback (no typed proxy to inspect/await): the historical pre-completed carrier. Kept for a caller
    // that has only a nint; the flip's spliced call and the runtime both bind the Il2CppObjectBase overload above.
    public static System.Threading.Tasks.Task? WrapTask(nint il2cppPtr)
    {
        if (il2cppPtr == 0) return null;
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        tcs.SetResult(true);
        _taskPtr.Add(tcs.Task, il2cppPtr);
        return tcs.Task;
    }

    // ---- generic Task<T> -----------------------------------------------------------------------------------
    // The generic twin. Task<T> : Task, so it SHARES _taskPtr / UnwrapTask for identity. Completed with the il2cpp
    // task's REAL result (marshalled ToManaged<T>) — so a CLR consumer that reads .Result (e.g. a mod awaiting a
    // flipped Task<Session>) gets the game's actual value; a pending task is bridged.
    public static System.Threading.Tasks.Task<T>? WrapTaskT<T>(Il2CppObjectBase? il2cppTask)
    {
        if (il2cppTask is null) return null;                   // a null il2cpp task round-trips as a null CLR task
        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
        _taskPtr.Add(tcs.Task, il2cppTask.Pointer);            // identity: shares the non-generic _taskPtr
        if (Il2CppCompleted(il2cppTask)) SetResultFromIl2Cpp(tcs, il2cppTask);   // completed: the REAL result now
        else BridgePending(il2cppTask, () => SetResultFromIl2Cpp(tcs, il2cppTask), ex => tcs.TrySetException(ex));   // pending: bridge completion
        return tcs.Task;
    }

    public static System.Threading.Tasks.Task<T>? WrapTaskT<T>(nint il2cppPtr)
    {
        if (il2cppPtr == 0) return null;
        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
        tcs.SetResult(default!);                               // pointer-only fallback (no proxy to read the result from)
        _taskPtr.Add(tcs.Task, il2cppPtr);
        return tcs.Task;
    }

    // Is the il2cpp task completed? (reflective — the proxy's runtime type is Task[`1<T'>], IsCompleted inherited from Task).
    static bool Il2CppCompleted(Il2CppObjectBase il2cppTask)
        => il2cppTask.GetType().GetProperty("IsCompleted")?.GetValue(il2cppTask) is true;

    // Read the il2cpp Task<T'>.Result and complete the carrier with its MARSHALLED (ToManaged<T>) value — an identity
    // leaf (Session/int) passes through, a container result recurses. A faulted il2cpp task surfaces as a faulted carrier.
    static void SetResultFromIl2Cpp<T>(System.Threading.Tasks.TaskCompletionSource<T> tcs, Il2CppObjectBase il2cppTask)
    {
        try
        {
            object? il2cppResult = il2cppTask.GetType().GetProperty("Result")?.GetValue(il2cppTask);
            tcs.TrySetResult(Inutil.Marshal.Il2CppMarshal.ToManaged<T>(il2cppResult)!);
        }
        catch (System.Reflection.TargetInvocationException tie) { tcs.TrySetException(tie.InnerException ?? tie); }
        catch (System.Exception ex) { tcs.TrySetException(ex); }
    }

    // Bridge a genuinely-PENDING il2cpp task's completion to the CLR carrier (the read mirror of the pending WRITE
    // fabricate). Attach an il2cpp continuation via the NON-generic TaskAwaiter (Task<T> : Task): its awaiter
    // machinery is compiled wherever the game awaits ANY task, so it dodges the Task<T>-SPECIFIC-awaiter stripping (the
    // game never awaits its own returned Task<int>, so Task`1<int>.GetAwaiter may not be inflated). UnsafeOnCompleted
    // takes an Il2CppSystem.Action, so a managed continuation is marshalled through DelegateSupport.ConvertDelegate; it
    // fires on the il2cpp completing thread (attached — reading the result there via get_Result is safe). A wiring
    // failure faults the carrier via onError rather than hanging the awaiter (P4 — never a silent stall).
    static void BridgePending(Il2CppObjectBase il2cppTask, System.Action onComplete, System.Action<System.Exception> onError)
    {
        try
        {
            // The continuation (managed delegate + its il2cpp Action wrapper) must outlive this stack frame or GC
            // reclaims it before the task completes and it silently never fires. Root it in _pendingKeepAlive, and
            // have the continuation drop its OWN entry once fired — so the set stays bounded by IN-FLIGHT pending tasks.
            object[] keep = new object[2];
            System.Action fire = () => { try { onComplete(); } finally { lock (_pendingKeepAlive) _pendingKeepAlive.Remove(keep); } };

            Type awaiterType = ResolveIl2CppType("Il2CppSystem.Runtime.CompilerServices.TaskAwaiter");   // non-generic
            object awaiter = System.Activator.CreateInstance(awaiterType, new object[] { il2cppTask })!;  // .ctor(Task)
            System.Reflection.MethodInfo onCompleted = awaiterType
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .First(m => m.Name.EndsWith("UnsafeOnCompleted", System.StringComparison.Ordinal) && m.GetParameters().Length == 1);
            Type actionType = onCompleted.GetParameters()[0].ParameterType;                               // Il2CppSystem.Action
            object il2cppAction = typeof(Il2CppInterop.Runtime.DelegateSupport).GetMethod("ConvertDelegate")!
                .MakeGenericMethod(actionType).Invoke(null, new object[] { fire })!;
            keep[0] = fire; keep[1] = il2cppAction;
            lock (_pendingKeepAlive) _pendingKeepAlive.Add(keep);
            onCompleted.Invoke(awaiter, new object[] { il2cppAction });
        }
        catch (System.Exception ex) { onError(ex); }
    }

    // In-flight pending-task continuations, rooted against GC until each fires and drops its own entry (see BridgePending).
    static readonly System.Collections.Generic.List<object[]> _pendingKeepAlive = new();

    // Resolve an il2cpp proxy TYPE by full name from the loaded assemblies (the game-agnostic SDK names no Il2Cppmscorlib
    // type at compile time). Cached — the loaded-assembly set is stable after startup.
    static readonly System.Collections.Generic.Dictionary<string, Type> _typeCache = new();
    static Type ResolveIl2CppType(string fullName)
    {
        lock (_typeCache)
        {
            if (_typeCache.TryGetValue(fullName, out Type? cached)) return cached;
            foreach (System.Reflection.Assembly a in System.AppDomain.CurrentDomain.GetAssemblies())
                if (a.GetType(fullName, throwOnError: false) is { } t) return _typeCache[fullName] = t;
            throw new System.NotSupportedException($"Il2CppSugar: could not resolve il2cpp type '{fullName}' among loaded assemblies.");
        }
    }

    // Recover the il2cpp task pointer a carrier wraps (0 if `t` is not one of our carriers — e.g. a mod's freshly
    // fabricated Task.CompletedTask, which the hook-boundary marshaller then rebuilds into an il2cpp task).
    public static nint UnwrapTask(System.Threading.Tasks.Task? t)
        => t is not null && _taskPtr.TryGetValue(t, out object? p) ? (nint)p! : 0;

    // Whether `t` is one of OUR carriers (a forwarded il2cpp task) vs a mod-fabricated CLR task. The hook-
    // boundary marshaller branches on this: carrier -> forward by identity (UnwrapTask); non-carrier -> fabricate.
    public static bool IsCarrier(System.Threading.Tasks.Task? t)
        => t is not null && _taskPtr.TryGetValue(t, out _);
}
