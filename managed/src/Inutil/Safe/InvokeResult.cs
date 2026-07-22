// The verdict a fault-guarded call produced — docs/contribution/architecture/17-reach-faces.md's
// `InvokeResult { Ok | Faulted(code,addr) | Threw(ex) }`. A readonly struct (no allocation) with a readable ToString.
// It sits over inutil_core's SEH fault-guard (native/core/interceptor.c `inutil_guarded_invoke`, validated under Wine
// — native/tests/guard_smoke.c), which turns a HARDWARE fault (a bad `this`, a freed object, a garbage method handle)
// into a return value instead of a process-killing c0000005 CoreCLR cannot catch. Two throw sources: the raw native
// `il2cpp_runtime_invoke`'s managed throw comes back as an Il2CppException* out-param (`RawException`, not raised —
// the caller decides); a C# exception a `Safe.Run` delegate throws is caught at the trampoline (it must NOT unwind
// across the native boundary) and surfaced as `ManagedException`. Both feed `Threw`; exactly one of {Ok, Faulted, Threw} holds.
using System.Runtime.CompilerServices;      // Unsafe.Read — unbox a value-type return
using Il2CppInterop.Runtime;                 // IL2CPP.il2cpp_object_unbox / Il2CppStringToManaged
using Il2CppInterop.Runtime.InteropTypes;    // Il2CppObjectBase

namespace Inutil;

public readonly struct InvokeResult
{
    public readonly nint Result;         // the il2cpp return: BOXED for a value-type return (Unbox<T>()); the raw object
                                         // ptr for a reference return (As<T>()/AsString()); 0 for void / faulted / Safe.Run.
    public readonly uint FaultCode;      // hardware fault code (0 = none; e.g. 0xC0000005 access violation)
    public readonly nint FaultAddr;      // faulting address (0 = none / unknown)
    public readonly nint RawException;   // Il2CppException* a RAW guarded invoke's method threw (0 = none); NOT raised —
                                         // inspect or re-raise yourself. Only ever set by Safe.TryInvoke (Shape A).
    public readonly Exception? ManagedException;  // a C# exception a Safe.Run delegate threw (null = none), caught at the
                                                  // guard trampoline so only hardware faults reach the native longjmp.

    public InvokeResult(nint result, uint faultCode, nint faultAddr, nint rawException, Exception? managedException = null)
    {
        Result = result; FaultCode = faultCode; FaultAddr = faultAddr;
        RawException = rawException; ManagedException = managedException;
    }

    public bool Faulted => FaultCode != 0;                                    // a native fault was caught (the call did NOT complete)
    public bool Threw   => RawException != 0 || ManagedException is not null; // a managed exception surfaced (a normal, complete call)
    public bool Ok      => FaultCode == 0 && RawException == 0 && ManagedException is null;

    /// <summary>Unbox a value-type return (int/float/an il2cpp struct) — runtime_invoke boxes those. default(T) if no result.</summary>
    public unsafe T Unbox<T>() where T : unmanaged
        => Result == 0 ? default : Unsafe.Read<T>((void*)IL2CPP.il2cpp_object_unbox(Result));

    /// <summary>Wrap a reference-type return as its interop proxy (0 -> null). A reference return is the raw object ptr, not boxed.</summary>
    public T? As<T>() where T : Il2CppObjectBase
        => Result == 0 ? null : Il2CppInterop.Runtime.Runtime.Il2CppObjectPool.Get<T>(Result);

    /// <summary>Marshal a string return (null if the return was null).</summary>
    public string? AsString() => Result == 0 ? null : IL2CPP.Il2CppStringToManaged(Result);

    public override string ToString() =>
        Faulted                       ? $"<faulted 0x{FaultCode:x8} @0x{FaultAddr:x}>"
        : ManagedException is not null ? $"<threw {ManagedException.GetType().Name}: {ManagedException.Message}>"
        : RawException != 0            ? $"<threw il2cpp exception @0x{RawException:x}>"
        :                                $"ok result=0x{Result:x}";
}

// The value-returning twin of InvokeResult for Safe.Run<T>(Func<T>): the fault-status PLUS the delegate's managed
// return, which — unlike a raw il2cpp return (InvokeResult.Result) — is already a materialized C# T. Value is only
// meaningful when Ok; on Faulted/Threw the delegate never produced one, so Value is default.
public readonly struct SafeResult<T>
{
    public readonly InvokeResult Status;
    public readonly T? Value;
    public SafeResult(InvokeResult status, T? value) { Status = status; Value = value; }

    public bool Ok      => Status.Ok;
    public bool Faulted => Status.Faulted;
    public bool Threw   => Status.Threw;

    public override string ToString() => Status.Ok ? $"ok value={Value}" : Status.ToString();
}
