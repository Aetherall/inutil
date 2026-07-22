// Inutil.Probe — fault-safe il2cpp pointer inspection: given a raw pointer (an il2cpp object handle you're unsure
// about), "is this a live object, and what is it?" — WITHOUT eating the c0000005 the game would take when it
// dereferences a bad/void class pointer. The READ side of inutil_core's SEH fault-guard, companion to Inutil.Safe
// (the INVOKE side): this wires the guarded read (inutil_guard_read — a VEH + setjmp shim, validated under Wine,
// native/tests/guard_smoke.c).
//
// docs/contribution/architecture/17-reach-faces.md — a raw by-name / discovery primitive for the irreducible cases:
// an ERASED Il2CppSystem.Object handle from a hook arg or a collection element may or may not be a live object of a
// nameable type; IsLiveObject validates it before Fields/Invoke reach into it, and Describe answers "what is it?" for the REPL.
//
// HOW IT STAYS SAFE. Every suspect deref goes through inutil_guard_read: a Vectored-Exception-Handler + setjmp shim
// that turns an access violation into a `false` return instead of a process death (mingw C has no __try/__except).
// So IsLiveObject walks ptr -> its klass slot -> the klass, and a fault at any hop is just "not live", never a crash.
//
// HONEST LIMITATION. This proves "plausibly live", not "provably live": Boehm is non-moving and non-compacting, so a
// FREED object's memory usually still validates. It catches null / garbage / bad-class pointers; it does NOT catch
// use-after-free. Named accordingly.
//
// Game-AGNOSTIC (il2cpp C ABI only). Call on the game (il2cpp-attached) thread, like any il2cpp touch.
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;              // IL2CPP.il2cpp_* (class/array metadata)
using Il2CppInterop.Runtime.InteropTypes; // Il2CppObjectBase (.Pointer)

namespace Inutil;

// What Describe reports about a pointer. A struct (no allocation) with a readable ToString so an eval line
// (`Inutil.Probe.Describe(ptr)`) prints something useful with no ceremony.
public readonly struct ProbeResult
{
    public readonly bool Valid;         // ptr is a plausibly-live Il2CppObject* (its klass is mapped)
    public readonly nint Klass;         // the object's Il2CppClass* (0 when invalid)
    public readonly string? KlassName;  // "Namespace.Type" (null when invalid / unnameable)
    public readonly bool IsArray;       // an il2cpp array (rank > 0)
    public readonly string? ElementType;// the element class name for an array (null otherwise)
    public readonly long Length;        // array length (-1 when not an array / unknown)

    public ProbeResult(bool valid, nint klass, string? klassName, bool isArray, string? elementType, long length)
    { Valid = valid; Klass = klass; KlassName = klassName; IsArray = isArray; ElementType = elementType; Length = length; }

    public override string ToString() => !Valid
        ? "<invalid il2cpp object>"
        : IsArray ? $"{KlassName}[{Length}] (elem {ElementType}) klass=0x{Klass:x}"
                  : $"{KlassName} klass=0x{Klass:x}";
}

public static class Probe
{
    // inutil_core's SEH-guarded pointer read: 1 = read ok (value in `out`), 0 = the read faulted (addr
    // unmapped/unreadable) with NO process crash. `addr`/`value` marshal as void*/void** (nint <-> pointer).
    [DllImport("inutil_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern int inutil_guard_read(nint addr, out nint value);

    // Fault-safe pointer-sized read. False (value=0) if `addr` is null or unreadable — no crash either way.
    public static bool TryRead(nint addr, out nint value)
    {
        if (addr == 0) { value = 0; return false; }
        bool ok = inutil_guard_read(addr, out value) != 0;
        if (!ok) value = 0;
        return ok;
    }

    // Is `ptr` a plausibly-live il2cpp object? The two-level guarded check:
    //   1. ptr readable            -> read its klass slot (Il2CppObject offset 0)
    //   2. that klass readable     -> a mapped class pointer, not garbage
    // Catches null / garbage / bad-class handles; NOT use-after-free (see file header). No crash on a bad ptr.
    public static bool IsLiveObject(nint ptr)
        => TryRead(ptr, out nint klass) && klass != 0 && TryRead(klass, out _);

    public static bool IsLiveObject(Il2CppObjectBase? o) => o is not null && IsLiveObject(o.Pointer);

    // The object's Il2CppClass* (0 if ptr isn't a live object).
    public static nint KlassOf(nint ptr) => IsLiveObject(ptr) && TryRead(ptr, out nint k) ? k : 0;

    // "Namespace.Type" of the object at ptr, or null if ptr isn't a live object.
    public static string? KlassName(nint ptr)
    {
        nint k = KlassOf(ptr);
        return k == 0 ? null : ClassName(k);
    }

    // Full classification of a suspect pointer — the one-call answer to "what is this?" that ends the pointer
    // archaeology: valid? class? array (element type + length)? Never throws, never crashes.
    public static ProbeResult Describe(nint ptr)
    {
        if (!TryRead(ptr, out nint klass) || klass == 0 || !TryRead(klass, out _))
            return default;   // Valid=false

        string name = ClassName(klass);
        int rank = IL2CPP.il2cpp_class_get_rank(klass);
        if (rank <= 0)
            return new ProbeResult(true, klass, name, isArray: false, elementType: null, length: -1);

        nint elem = IL2CPP.il2cpp_class_get_element_class(klass);
        string? elemName = elem == 0 ? null : ClassName(elem);
        long len = (long)IL2CPP.il2cpp_array_length(ptr);   // ptr confirmed valid above
        return new ProbeResult(true, klass, name, isArray: true, elementType: elemName, length: len);
    }

    public static ProbeResult Describe(Il2CppObjectBase? o) => o is null ? default : Describe(o.Pointer);

    // "Namespace.Type" for a klass. The caller has already guard-confirmed klass is mapped; we additionally guard
    // each metadata char* before marshalling it, so a mapped-but-garbage klass degrades to "" rather than faulting
    // on a wild name pointer. Internal so Introspect reuses the same guarded marshal for il2cpp_type_get_name etc.
    internal static string ClassName(nint klass)
    {
        string ns = SafeUtf8(IL2CPP.il2cpp_class_get_namespace(klass));
        string nm = SafeUtf8(IL2CPP.il2cpp_class_get_name(klass));
        return ns.Length == 0 ? nm : ns + "." + nm;
    }

    // Marshal a UTF-8 char* to a managed string, but only after guard-reading it (so a garbage name pointer yields
    // "" instead of a crash). PtrToStringUTF8 still scans to NUL — acceptable once the head is mapped. Internal so
    // Introspect reuses the same guarded marshal for il2cpp_type_get_name etc.
    internal static string SafeUtf8(nint p)
        => p != 0 && TryRead(p, out _) ? (System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p) ?? "") : "";
}
