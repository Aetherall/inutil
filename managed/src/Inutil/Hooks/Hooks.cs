// The managed interceptor (L2) + the typed hook-registration API (L3): where L1 (Il2CppInterop typed proxy), the
// native generic thunk + the MethodInfo*->hooks table, and a reference/expression-based hook API fire together on a
// real method. The native hook engine (inutil_core.dll = native/core/interceptor.c) installs the call+post thunk at
// a methodPointer WE hand it (the `install` callback below) and reverse-P/Invokes into Dispatch (pre) / PostDispatch
// (post) on every hooked call. Codegen-free: a mod names the method with a typed expression —
// `(Player p) => p.TakeDamage(default)` — and works the native call frame through a typed `HookContext`
// (Arg<T>/SetArg<T>/Return<T>/SetReturn<T>). No per-method glue, no per-signature delegate.
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace Inutil.Hooks;

// Mirrors the register spill in native/core/generic_thunk_post.S: 4 GPR + 4 XMM slots
// (rcx,rdx,r8,r9 / xmm0..3) + a pointer to the caller's 5th+ stack args. A float arg is the low
// 32 bits of its XMM slot; a >4th positional arg lives at Stack[index-4].
[StructLayout(LayoutKind.Sequential)]
public unsafe struct CallFrame { public fixed ulong Gpr[4]; public fixed double Xmm[4]; public ulong* Stack; }

// The return registers the call+post thunk spills after CALLing the original.
[StructLayout(LayoutKind.Sequential)]
public unsafe struct RetFrame { public ulong Rax; public double Xmm0; }

// --- the per-method ABI layout plan (computed ONCE at Register from the proxy signature) ---
// The Win64 + il2cpp calling convention places each user-facing arg (and the return) into the
// call frame by category. We resolve the category for every arg/return up front so the hot path
// is a table lookup, not a per-call decision — and so the typed surface stays Arg<T>/Return<T>
// (the author writes C# types; the plan, not the author, knows the ABI).
//
//   Gpr  - value lives directly in the integer slot   (scalar int/enum/bool/ptr; a <=8B struct by
//                                                       value; or an object reference = Il2CppObject*)
//   Xmm  - value lives in the float slot              (a scalar float/double)
//   Ind  - the integer slot holds a POINTER to the value: a `ref`/`out` param, OR a value type whose
//          size is not 1/2/4/8 (Win64 passes it by hidden pointer). Same deref for both.
//   Sret - (return only) a non-by-value-size struct is built in a caller buffer; the callee returns
//          that buffer's pointer in RAX, and it occupies the hidden first arg slot on entry.
internal enum Loc : byte { Gpr, Xmm, Ind, Sret }

// One arg's placement: its category + its POSITIONAL register-slot index. Win64 numbers slots
// across both banks by argument position, so slot = thisOff + sretOff + argIndex regardless of
// whether the value rides a GPR or an XMM.
internal readonly struct ArgPlan
{
    public readonly Loc Loc;
    public readonly int Slot;
    public readonly bool Obj;   // a reference-type param (matters only on the invoke path: the il2cpp
                                // params slot then holds the Il2CppObject* itself, not a pointer to it)
    public readonly bool RefBearing;  // the (deref'd) element carries object references — a real object/string
                                      // ref OR a value type embedding refs (Loadout{int;string}). Drives the GC
                                      // write barrier: a write into a Loc.Ind (ref/out) destination of such a
                                      // value stores a heap pointer into possibly-live-heap storage, which the
                                      // incremental collector must be told about (raw blittable writes are roots).
    public ArgPlan(Loc loc, int slot, bool obj, bool refBearing) { Loc = loc; Slot = slot; Obj = obj; RefBearing = refBearing; }
}

internal sealed class MethodPlan
{
    public ArgPlan[] Args = Array.Empty<ArgPlan>();
    public Loc Ret;            // Gpr / Xmm / Sret  (void leaves this Gpr, unused)
    public int ThisOff;        // 1 instance (this = slot0), 0 static
    public int SretOff;        // 1 if the return is sret (hidden buffer ptr occupies slot 0)
    public int MiSlot;         // positional arg index of the trailing MethodInfo* (this+sret+params): 0-3 = a GPR
                               // home slot, 4+ = a caller-stack spill the native core reads from CallFrame.Stack
                               // [MiSlot-4]; <0 unknown

    // Win64: a struct travels by value in ONE register only when its size is 1/2/4/8; any other size
    // (3, 5..7, >8) goes by hidden pointer (arg) / sret buffer (return). Scalars float/double -> XMM.
    private static bool ByValueSize(int sz) => sz is 1 or 2 or 4 or 8;

    // A value type that has reference fields can't be blittable, so Il2CppInterop emits it as a CLASS
    // deriving from Il2CppSystem.ValueType (e.g. Loadout{int;string}) — t.IsValueType is then false even
    // though il2cpp passes/returns it BY VALUE. Treat both that and a real blittable struct as value types.
    // C6: the ref-bearing-value-proxy discriminator is ValueTypeBridge's ONE implementation, not a copy here.
    private static bool IsValueProxy(Type t) => Inutil.Marshal.ValueTypeBridge.IsRefBearingValueProxy(t);
    private static bool IsValueType(Type t) => (t.IsValueType && !t.IsPrimitive && !t.IsEnum) || IsValueProxy(t);

    // A patched interop proxy (managed/tools/InteropPatch) presents a Nullable<T>-returning method with the NATURAL
    // System.Nullable<U> signature instead of the broken Il2CppSystem.Nullable<U>. That managed type has NO il2cpp
    // class of its own, so deriving the native ABI (size / GC descriptor) from it fails — Of would misclassify the
    // sret boundary and diverge from OfNative (the native truth) on exactly the methods the patch touched. Map it
    // back to its il2cpp counterpart, which has the IDENTICAL native layout: this keeps Of == OfNative on patched
    // methods AND the typed-hook ABI correct. Runtime MakeGenericType on U (a game proxy type the SDK never
    // compile-time references) is fine — reflection over a live Type. out elem = U.
    private static bool IsPatchedNullable(Type t, out Type elem)
    {
        if (t.IsGenericType && !t.IsGenericTypeDefinition && t.GetGenericTypeDefinition() == typeof(System.Nullable<>))
        { elem = t.GetGenericArguments()[0]; return true; }
        elem = t; return false;
    }

    private static Type MapPatchedNullable(Type t)
        => IsPatchedNullable(t, out Type u) ? typeof(Il2CppSystem.Nullable<>).MakeGenericType(u) : t;

    // Per-call (thread-static) map of a patched System.Nullable<U> managed type -> its il2cpp class, resolved from
    // the native MethodInfo* signature. Set by Of(m, mi) and read by NativeClassOf; null when no native mi is known.
    [ThreadStatic] private static Dictionary<Type, nint>? _nativeNullableHint;

    // The il2cpp class pointer for a managed type, mapping a patched System.Nullable<U> back to its il2cpp form.
    // PREFER the native-metadata hint when Of supplied one: Il2CppClassPointerStore<Il2CppSystem.Nullable<U>>'s static
    // init can THROW (a fault .NET then CACHES) when the interop assembly was BYTE-LOADED — the Option-E in-process
    // patch seam on the first launch after a regen — and that degradation reaches the element store too, so neither
    // the generic instantiation nor its element resolves there. The hint (il2cpp_class_from_type off the method
    // signature) is exactly the class OfNative sizes, so it both avoids the throw AND keeps Of == OfNative. With no
    // hint (mi unknown) fall back to the managed store, swallowing a fault to 0 so Of never throws out to a caller.
    private static nint NativeClassOf(Type t)
    {
        if (_nativeNullableHint != null && IsPatchedNullable(t, out _) && _nativeNullableHint.TryGetValue(t, out nint hinted))
            return hinted;
        try { return Il2CppClassPointerStore.GetNativeClassPointer(MapPatchedNullable(t)); }
        catch { return 0; }
    }

    // Resolve, from native metadata, the il2cpp class of every patched System.Nullable<U> the managed signature
    // carries (return + params) — the one thing the erased managed type can't supply and the byte-load-degraded
    // managed store can't be trusted for. il2cpp_class_from_type on the native return/param type IS the class
    // OfNative sizes, so feeding these into Of keeps the proxy-derived plan equal to the native-derived one.
    private static Dictionary<Type, nint>? BuildNullableHint(MethodInfo m, nint mi)
    {
        Dictionary<Type, nint>? hint = null;
        void Map(Type managed, nint nativeType)
        {
            if (nativeType == 0 || !IsPatchedNullable(managed, out _)) return;
            nint cls = IL2CPP.il2cpp_class_from_type(nativeType);
            if (cls != 0) (hint ??= new Dictionary<Type, nint>())[managed] = cls;
        }
        Map(m.ReturnType, IL2CPP.il2cpp_method_get_return_type(mi));
        ParameterInfo[] ps = m.GetParameters();
        for (uint i = 0; i < ps.Length; i++)
        {
            Type pt = ps[(int)i].ParameterType;
            Map(pt.IsByRef ? pt.GetElementType()! : pt, IL2CPP.il2cpp_method_get_param(mi, i));
        }
        return hint;
    }

    // The native unboxed size that governs the Win64 by-value-vs-hidden-pointer split. This MUST be the
    // NATIVE il2cpp value size, not the managed proxy's Marshal.SizeOf: the two usually agree, but an
    // offline-generated proxy can emit a [StructLayout] whose managed size diverges from the native struct
    // (observed: a 12B Vec3 whose proxy Marshal.SizeOf was <=8, which mis-split it into a register instead
    // of a hidden pointer and read garbage). Trust il2cpp; fall back to Marshal only if the native class or
    // size is unavailable (e.g. a non-il2cpp value type the plan would never actually classify).
    private static int ValueSize(Type t)
    {
        nint cls = NativeClassOf(t);
        if (cls != 0) { uint align = 0; int sz = IL2CPP.il2cpp_class_value_size(cls, ref align); if (sz > 0) return sz; }
        if (t.IsValueType) { try { return System.Runtime.InteropServices.Marshal.SizeOf(t); } catch { } }   // fully-qualified: the Inutil.Marshal ns shadows the BCL Marshal here
        return 0;
    }

    // refBearing: does the (deref'd) element carry object references? Read il2cpp's GC descriptor directly —
    // the SAME source OfNative.NativeRefBearing uses — instead of the managed `!elem.IsValueType` hack. That
    // hack FALSE-POSITIVES on a non-ref-bearing value-type proxy that Il2CppInterop renders as a CLASS (e.g.
    // Il2CppSystem.Nullable<int>: IsValueType is false, so the hack flags it ref-bearing, but il2cpp reports
    // has_references=false). Reading the descriptor makes Of agree with OfNative (the self-test) and is the
    // same class pointer ValueSize already trusts; fall back to the managed heuristic only when the element has
    // no resolvable native class.
    private static bool RefBearing(Type elem)
    {
        nint cls = NativeClassOf(elem);
        if (cls == 0) return !elem.IsValueType;                          // no native class: managed fallback
        if (!IL2CPP.il2cpp_class_is_valuetype(cls)) return true;         // reference element
        return IL2CPP.il2cpp_class_has_references(cls);                  // value type: does it embed refs?
    }

    private static Loc ClassifyArg(Type t)
    {
        if (t.IsByRef) return Loc.Ind;                                   // ref/out: slot holds a pointer
        if (t == typeof(float) || t == typeof(double)) return Loc.Xmm;
        if (IsValueType(t)) return ByValueSize(ValueSize(t)) ? Loc.Gpr : Loc.Ind;
        return Loc.Gpr;                                                  // int/enum/bool/ptr/object ref
    }

    public static MethodPlan Of(MethodInfo m) => Of(m, 0);

    // Build the layout plan from the managed proxy signature. When the native MethodInfo* is known, pass it: every
    // patched System.Nullable<U> in the signature is then sized from native metadata (BuildNullableHint) rather than
    // the managed Il2CppClassPointerStore, which is unreliable for a byte-loaded interop assembly (the Option-E seam).
    // The hint is thread-static + restored in finally, so a nested/re-entrant Of never sees a stale map.
    public static MethodPlan Of(MethodInfo m, nint mi)
    {
        Dictionary<Type, nint>? prevHint = _nativeNullableHint;
        _nativeNullableHint = mi != 0 ? BuildNullableHint(m, mi) : null;
        try
        {
            int thisOff = m.IsStatic ? 0 : 1;
            Type rt = m.ReturnType;
            Loc ret; int sretOff = 0;
            if (rt == typeof(void) || rt == typeof(float) || rt == typeof(double))
                ret = rt == typeof(void) ? Loc.Gpr : Loc.Xmm;
            else if (IsValueType(rt) && !ByValueSize(ValueSize(rt))) { ret = Loc.Sret; sretOff = 1; }
            else ret = Loc.Gpr;                                             // <=8B value type in RAX, or object ptr

            var ps = m.GetParameters();
            var args = new ArgPlan[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                Type pt = ps[i].ParameterType;
                Type elem = pt.IsByRef ? pt.GetElementType()! : pt;
                args[i] = new ArgPlan(ClassifyArg(pt), thisOff + sretOff + i,
                                      obj: !elem.IsValueType && !IsValueProxy(elem),    // true only for real reference types
                                      // refBearing: the (deref'd) element carries object refs — read from il2cpp's GC
                                      // descriptor (see RefBearing) so Of agrees with OfNative even for a non-ref-bearing
                                      // value-type proxy Il2CppInterop renders as a class (Il2CppSystem.Nullable<int>).
                                      refBearing: RefBearing(elem));
            }

            int miPos = thisOff + sretOff + ps.Length;
            return new MethodPlan { Args = args, Ret = ret, ThisOff = thisOff, SretOff = sretOff,
                                    MiSlot = miPos };   // uncapped — a spilled (miPos>=4) MethodInfo* is read from CallFrame.Stack, not lost to -1
        }
        finally { _nativeNullableHint = prevHint; }
    }

    // --- OfNative: the SAME plan, built purely from il2cpp metadata keyed on the native MethodInfo* ---
    // Derives the ABI plan WITHOUT a System.Reflection.MethodInfo — i.e. for a method Il2CppInterop never PROJECTED
    // (obfuscated / private / base-typed). `Of` reads the managed proxy signature; `OfNative` reads il2cpp metadata
    // directly, category for category:
    //   byref      <- il2cpp_type_is_byref
    //   float/dbl  <- the type enum R4/R8
    //   value type <- il2cpp_class_is_valuetype   (il2cpp reports a ref-bearing VT class as a value type DIRECTLY,
    //                 so Of's Il2CppSystem.ValueType base-class HACK is unneeded here)
    //   native size<- il2cpp_class_value_size     (same source `Of` trusts for the by-value split)
    //   instance   <- il2cpp_method_is_instance
    // For a fully-projected target (ToyGame) this MUST agree with Of on every method (cross-checked by the
    // self-test); the payoff is that it ALSO works where Of has no input at all.
    public static unsafe MethodPlan OfNative(nint mi)
    {
        int thisOff = IL2CPP.il2cpp_method_is_instance(mi) ? 1 : 0;

        nint rtp = IL2CPP.il2cpp_method_get_return_type(mi);
        var rEnum = (Il2CppTypeEnum)IL2CPP.il2cpp_type_get_type(rtp);
        Loc ret; int sretOff = 0;
        if (rEnum == Il2CppTypeEnum.IL2CPP_TYPE_VOID) ret = Loc.Gpr;       // void: slot unused
        else if (rEnum == Il2CppTypeEnum.IL2CPP_TYPE_R4 || rEnum == Il2CppTypeEnum.IL2CPP_TYPE_R8) ret = Loc.Xmm;
        else if (IsNativeValueType(rtp, out int rsz) && !ByValueSize(rsz)) { ret = Loc.Sret; sretOff = 1; }
        else ret = Loc.Gpr;                                               // <=8B value type in RAX, or object ptr

        uint n = IL2CPP.il2cpp_method_get_param_count(mi);
        var args = new ArgPlan[n];
        for (uint i = 0; i < n; i++)
        {
            nint pt = IL2CPP.il2cpp_method_get_param(mi, i);
            // obj = the (deref'd) element is a reference type. il2cpp_class_from_type strips the byref bit,
            // so IsNativeValueType already reports on the element for `ref VT`/`ref Class` alike.
            args[i] = new ArgPlan(ClassifyNative(pt), thisOff + sretOff + (int)i,
                                  obj: !IsNativeValueType(pt, out _), refBearing: NativeRefBearing(pt));
        }

        int miPos = thisOff + sretOff + (int)n;
        return new MethodPlan { Args = args, Ret = ret, ThisOff = thisOff, SretOff = sretOff,
                                MiSlot = miPos };   // uncapped — see Of (the reflection twin); a spilled MethodInfo* reads from CallFrame.Stack

    }

    private static Loc ClassifyNative(nint t)
    {
        if (IL2CPP.il2cpp_type_is_byref(t)) return Loc.Ind;               // ref/out: slot holds a pointer
        var e = (Il2CppTypeEnum)IL2CPP.il2cpp_type_get_type(t);
        if (e == Il2CppTypeEnum.IL2CPP_TYPE_R4 || e == Il2CppTypeEnum.IL2CPP_TYPE_R8) return Loc.Xmm;
        if (IsNativeValueType(t, out int sz)) return ByValueSize(sz) ? Loc.Gpr : Loc.Ind;
        return Loc.Gpr;                                                   // primitive int/enum/ptr/object ref
    }

    // Is this Il2CppType a value type, and if so its native unboxed size? il2cpp_class_from_type strips the
    // byref bit and returns the ELEMENT class, so this answers about the pointee for `ref VT`/`out VT` too —
    // matching `Of`, which classifies on the deref'd element. Primitives/enums are value types here (size
    // 1/2/4/8 -> Gpr), exactly as `Of`'s Gpr fallthrough places them.
    private static bool IsNativeValueType(nint type, out int size)
    {
        size = 0;
        nint cls = IL2CPP.il2cpp_class_from_type(type);
        if (cls == 0 || !IL2CPP.il2cpp_class_is_valuetype(cls)) return false;
        uint align = 0;
        size = IL2CPP.il2cpp_class_value_size(cls, ref align);
        return true;
    }

    // Does the (deref'd) element of this Il2CppType carry object references? il2cpp_class_from_type strips
    // the byref bit, so this answers for `ref VT`/`ref Class` alike. A non-value-type element IS a reference;
    // a value type embeds refs iff il2cpp_class_has_references (the GC descriptor — DIRECTLY answers it, no
    // field walk). This MUST agree with Of's managed `!elem.IsValueType` on every projected method: a managed
    // ref-bearing VT proxy is a CLASS (non-value-type) for which il2cpp reports has_references=true, so both
    // sides flag it; a blittable struct is a value type with no references on both. (Cross-checked by the
    // self-test, which compares RefBearing too.)
    private static bool NativeRefBearing(nint type)
    {
        nint cls = IL2CPP.il2cpp_class_from_type(type);
        if (cls == 0) return false;
        if (!IL2CPP.il2cpp_class_is_valuetype(cls)) return true;     // object/string reference element
        return IL2CPP.il2cpp_class_has_references(cls);              // value type: does it embed refs?
    }

    // Structural equality for the self-test: null if the two plans match in every field a dispatcher reads,
    // else a short description of the FIRST divergence (so a mismatch names the exact method+field).
    public static string? Diff(MethodPlan a, MethodPlan b)
    {
        if (a.ThisOff != b.ThisOff) return $"ThisOff {a.ThisOff}!={b.ThisOff}";
        if (a.SretOff != b.SretOff) return $"SretOff {a.SretOff}!={b.SretOff}";
        if (a.Ret != b.Ret)         return $"Ret {a.Ret}!={b.Ret}";
        if (a.MiSlot != b.MiSlot)   return $"MiSlot {a.MiSlot}!={b.MiSlot}";
        if (a.Args.Length != b.Args.Length) return $"argc {a.Args.Length}!={b.Args.Length}";
        for (int i = 0; i < a.Args.Length; i++)
        {
            ArgPlan x = a.Args[i], y = b.Args[i];
            if (x.Loc != y.Loc || x.Slot != y.Slot || x.Obj != y.Obj || x.RefBearing != y.RefBearing)
                return $"arg{i} ({x.Loc},s{x.Slot},obj{x.Obj},rb{x.RefBearing})!=({y.Loc},s{y.Slot},obj{y.Obj},rb{y.RefBearing})";
        }
        return null;
    }
}

// What a hook body receives. Wraps the native frame (valid only for the synchronous duration of the
// call) + the method's layout plan; reads/writes args + return value by the Win64/il2cpp ABI rules.
// A plain unsafe struct (not ref struct) so it can flow through the HookCallback delegate.
public readonly unsafe struct HookContext
{
    // register path (methodPointer detour): the spilled call frame
    private readonly CallFrame* _f;
    private readonly RetFrame* _r;     // null in a pre-hook
    // invoke path (__Canon generic methods): il2cpp's params array + return out-buffer
    private readonly void** _prm;      // non-null ONLY on the invoke path
    private readonly void* _ret;
    private readonly MethodPlan _p;
    private readonly int* _skip;       // pre-hooks only: a cell in the dispatcher's frame; Skip() writes it
    private readonly nint _thisObj;    // invoke path: il2cpp hands `this` separately (the detour path reads it from Gpr[0])

    internal HookContext(CallFrame* f, RetFrame* r, MethodPlan p, int* skip) { _f = f; _r = r; _p = p; _prm = null; _ret = null; _skip = skip; _thisObj = 0; }
    internal HookContext(MethodPlan p, void** prm, void* ret, int* skip, nint thisObj) { _p = p; _prm = prm; _ret = ret; _f = null; _r = null; _skip = skip; _thisObj = thisObj; }

    // The receiver (`this`) as a raw Il2CppObject* — 0 for a static method (no receiver). On the detour path
    // `this` is the FIRST positional GPR slot AFTER any hidden sret buffer: an sret return puts its buffer in
    // slot 0, pushing `this` to slot 1 (so the slot is SretOff, not a hard 0). On the invoke path il2cpp passes
    // `this` separately (captured into _thisObj). The Hook<T> facade wraps this into a typed Self/Next proxy.
    public nint ThisPtr => _prm != null ? _thisObj : (_p.ThisOff == 1 ? (nint)_f->Gpr[_p.SretOff] : 0);

    // Cancel the original call (pre-hooks only). The original body is bypassed and the caller gets
    // whatever the return is at that point — so pair Skip() with SetReturn<T>()/SetReturnString() in
    // the SAME pre-hook to replace the result. No-op in a post-hook (the original already ran).
    public void Skip() { if (_skip != null) *_skip = 1; }

    // Run the ORIGINAL method NOW, from inside a PRE hook — the "around" wrap (override M { …; base.M(); … }).
    // Its return is captured into THIS context: read it with Return<T>()/ReturnString()/ReturnObject(), rewrite
    // it with SetReturn<T>()/SetReturnString(). Pair with Skip() so the thunk does NOT call the original a
    // SECOND time and returns what Proceed left:  pre-work → Proceed() → read/transform → SetReturn(…) → Skip().
    // Calling Proceed() twice runs the original twice. The original runs un-hooked (no detour re-entry). Returns
    // false (no-op) in a post-hook or with no live frame. Works on both the methodPointer-detour and __Canon
    // invoke paths (the result lands in the same RetFrame / out-buffer this context already reads).
    public bool Proceed() => Hooks.RunProceed();

    // Address of arg i's bytes, honoring the active backing.
    private void* ArgAddr(int i)
    {
        ArgPlan a = _p.Args[i];
        if (_prm != null)
            // invoke path: params[i] points to the value for value-types & by-ref; for a reference-type
            // param passed by value the slot HOLDS the Il2CppObject*, so its address is &params[i].
            return (a.Obj && a.Loc != Loc.Ind) ? (void*)(&_prm[i]) : _prm[i];

        void* slot = a.Slot >= 4 ? (void*)(_f->Stack + (a.Slot - 4))           // caller-stack arg (5th+)
                   : a.Loc == Loc.Xmm ? (void*)(_f->Xmm + a.Slot)
                   : (void*)(_f->Gpr + a.Slot);
        return a.Loc == Loc.Ind ? (void*)*(nint*)slot : slot;                  // Ind: slot holds a pointer
    }

    // Writing arg i stores into possibly-LIVE-HEAP storage (so the incremental GC must be told) iff the
    // destination is a Loc.Ind pointee (a ref/out param points at the caller's local / array slot / field —
    // any of which can be a heap object) AND the value carries object references. Register/stack args live in
    // the spilled CallFrame (a GC root the runtime scans); a blittable ref (ref Vec3) carries no pointers. So
    // the barrier fires for exactly the ref-bearing-VT / ref-object `ref`/`out` write — everything else is a
    // raw store, byte-for-byte the prior behavior (the existing green paths are untouched).
    private bool ArgBarriered(int i) => _p.Args[i].Loc == Loc.Ind && _p.Args[i].RefBearing;

    // Copy `size` bytes from src into the (possibly heap-backed) dst, re-storing every pointer-sized slot through
    // the il2cpp GC write barrier so embedded object references survive incremental collection; the sub-pointer
    // tail is copied raw. The barrier is ADDRESS-keyed on this il2cpp build (Boehm, incremental GC) — `obj` is not
    // dereferenced (passing 0 is correct), so it can barrier a store to the arbitrary pointer a ref/out destination
    // is. A non-pointer slot's bytes are written verbatim and the dirty-mark triggers a PRECISE rescan of dst's
    // owner via its GC descriptor — harmless, so we needn't walk field metadata.
    // Public: the manual-barrier escape hatch for hooks that store into raw il2cpp heap addresses themselves
    // (the typed SetArg<T>/SetReturn paths apply it automatically; samples/ToyGameDemo exercises it directly).
    public static void WriteBarriered(void* dst, void* src, int size)
    {
        int n = size / sizeof(nint);
        for (int i = 0; i < n; i++)
            IL2CPP.il2cpp_gc_wbarrier_set_field((nint)0, (nint)((byte*)dst + i * sizeof(nint)), ((nint*)src)[i]);
        for (int b = n * sizeof(nint); b < size; b++) ((byte*)dst)[b] = ((byte*)src)[b];
    }

    // i = the user-facing argument index (0-based, `this` excluded). Covers scalars, <=8B structs
    // in-register, ref/out params, >8B by-pointer structs, caller-stack args, AND the invoke path.
    public T Arg<T>(int i) where T : unmanaged => *(T*)ArgAddr(i);
    public void SetArg<T>(int i, T v) where T : unmanaged
    {
        if (ArgBarriered(i)) WriteBarriered(ArgAddr(i), &v, sizeof(T));   // ref-bearing VT into heap-backed ref/out
        else *(T*)ArgAddr(i) = v;
    }

    // Return value, valid in post-hooks only. Register: Gpr -> RAX (incl. a <=8B struct), Xmm -> XMM0,
    // Sret -> the caller's return buffer (slot 0, captured at entry). Invoke: the out-buffer il2cpp
    // passed (the raw value for value-types, the Il2CppObject* slot for reference types).
    private void* RetAddr()
    {
        if (_prm != null) return _ret;
        return _p.Ret switch
        {
            Loc.Xmm  => &_r->Xmm0,
            Loc.Sret => (void*)(nint)_f->Gpr[0],
            _        => &_r->Rax,
        };
    }

    public T Return<T>() where T : unmanaged => *(T*)RetAddr();
    public void SetReturn<T>(T v) where T : unmanaged => *(T*)RetAddr() = v;

    // --- object / string marshalling (an object reference is an Il2CppObject*) ---
    // The <=8B-by-value accessors above can't express a managed string/proxy; these bridge through
    // Il2CppInterop. ArgAddr/RetAddr already resolve to the address that HOLDS the Il2CppObject*.
    // Store an Il2CppObject* (a string/object reference) into arg i's slot — barriered when that slot is a
    // ref/out destination (writing a heap pointer into possibly-live-heap storage), raw otherwise (a by-value
    // object arg lives in the spilled frame, a GC root). One pointer-sized slot, so size = sizeof(nint).
    private void SetArgPtr(int i, nint p) { if (ArgBarriered(i)) WriteBarriered(ArgAddr(i), &p, sizeof(nint)); else *(nint*)ArgAddr(i) = p; }

    public string? ArgString(int i) { nint p = *(nint*)ArgAddr(i); return p == 0 ? null : IL2CPP.Il2CppStringToManaged(p); }
    public void SetArgString(int i, string s) => SetArgPtr(i, IL2CPP.ManagedStringToIl2Cpp(s));
    public string? ReturnString() { nint p = *(nint*)RetAddr(); return p == 0 ? null : IL2CPP.Il2CppStringToManaged(p); }
    public void SetReturnString(string s) => *(nint*)RetAddr() = IL2CPP.ManagedStringToIl2Cpp(s);  // returns are Gpr/Xmm/Sret (roots), never a Loc.Ind heap dst -> raw

    // Set a mod's managed Task[<T>] as the return. The managed Task is dematerialized ToIl2Cpp
    // (ContainerBridge.DematerializeTask: a forwarded carrier round-trips by identity, a mod-fabricated
    // Task.FromResult/promise becomes a real il2cpp Task`1<T'>), then its pointer is written to the return slot — a
    // root register, like SetReturnString, so a raw write is GC-safe (the game reads it at once). Pass the SPELLED
    // Task type so the fabricate knows the result T. Pair with Skip() to replace the original's return.
    public void SetReturnTask<T>(System.Threading.Tasks.Task<T> t) => SetReturnTaskPtr(t, typeof(System.Threading.Tasks.Task<T>));
    public void SetReturnTask(System.Threading.Tasks.Task t) => SetReturnTaskPtr(t, typeof(System.Threading.Tasks.Task));
    private void SetReturnTaskPtr(System.Threading.Tasks.Task? t, Type spelled)
    {
        object? il2cpp = t is null ? null : Inutil.Marshal.Il2CppMarshal.ToIl2Cpp(t, spelled);
        *(nint*)RetAddr() = il2cpp is null ? 0 : ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)il2cpp).Pointer;
    }

    // Raw Il2CppObject* of an object-typed arg / the return — the escape hatch for wrapping into a
    // typed Il2CppInterop proxy at the call site (e.g. new Player(ctx.ArgObject(0))).
    public nint ArgObject(int i) => *(nint*)ArgAddr(i);
    public void SetArgObject(int i, nint obj) => SetArgPtr(i, obj);   // barriered iff a ref/out object destination
    public nint ReturnObject() => *(nint*)RetAddr();

    // --- raw-address escape hatches for the Hook<T> facade's reflection-driven marshaller ---
    // The typed Arg<T>/Return<T> accessors above require an `unmanaged` T known at compile time. The facade
    // resolves the proxy parameter/return Types at REGISTRATION (reflection), not compile time, so it copies
    // value-type bytes itself through these addresses (a boxed read/write). The address already honours the
    // active backing (register/stack/Loc.Ind deref, or the invoke params array), identical to Arg<T>.
    public void* ArgPointer(int i) => ArgAddr(i);          // address of arg i's value bytes
    public void* ReturnPointer() => RetAddr();             // address of the return value bytes (post-hooks)
    // Does writing arg i need the il2cpp GC write barrier? True only for a ref/out destination carrying object
    // references (a ref-bearing value type / object ref into possibly-live heap). The facade routes such a
    // write through WriteBarriered; everything else is a raw store. Mirrors the private ArgBarriered.
    public bool ArgWritesBarriered(int i) => ArgBarriered(i);
}

public delegate void HookCallback(HookContext ctx);

// The managed interceptor: the MethodInfo*->hooks table + the typed registration API +
// the reverse-P/Invoke targets the native thunk lands in. ONE table keyed by the native
// MethodInfo* (S5: distinct generic instantiations are distinct keys, never special).
public static unsafe class Hooks
{
    // One method's hooks. Pre/Post are COPY-ON-WRITE arrays: the dispatchers (hot path, possibly the
    // game's own thread) read the reference once and iterate lock-free; registration/removal (rare, a
    // mod attaching or an ALC unloading on another thread) rebuilds the array under _reg. So a mod can
    // add/remove hooks WHILE the game runs hot without tearing a dispatch — the M-live prerequisite.
    private sealed class Entry
    {
        public volatile HookCallback[] Pre = Array.Empty<HookCallback>();
        public volatile HookCallback[] Post = Array.Empty<HookCallback>();
        public MethodPlan Plan = new();   // immutable after creation
    }

    // Keyed by the native MethodInfo* (S5: distinct generic instantiations are distinct keys). Concurrent
    // so dispatch's TryGetValue is safe against a concurrent Register/Remove; structural writes hold _reg.
    private static readonly ConcurrentDictionary<nint, Entry> Table = new();
    private static readonly object _reg = new();

    // native install(methodPointer, methodInfo, miSlot) -> ok; handed in by the host at startup.
    // miSlot lets ONE detour at a shared __Canon methodPointer route distinct generic instantiations
    // to distinct keys by the live MethodInfo* in the frame (S5, in the agnostic core).
    private static delegate* unmanaged<nint, nint, int, int> _install;
    public static void SetInstall(nint installFnPtr) => _install = (delegate* unmanaged<nint, nint, int, int>)installFnPtr;

    // native install_invoker(methodInfo) -> ok; overwrites MethodInfo::invoker_method so a fully-shared
    // __Canon generic method (whose real entry isn't a detourable methodPointer) routes through us.
    private static delegate* unmanaged<nint, int> _installInvoker;
    public static void SetInvokerInstall(nint fnPtr) => _installInvoker = (delegate* unmanaged<nint, int>)fnPtr;

    // native proceed() -> run the ORIGINAL of the method whose hook is firing on this thread (the core
    // tracks the current frame in thread-local state, so this takes no args). HookContext.Proceed() routes
    // here; the fnptr stays private to the engine. Returns true iff a frame was live (inside a pre-hook).
    private static delegate* unmanaged<int> _proceed;
    public static void SetProceed(nint fnPtr) => _proceed = (delegate* unmanaged<int>)fnPtr;
    internal static bool RunProceed() => _proceed != null && _proceed() != 0;

    // native install_vtable(slotAddr, methodInfo, miSlot) -> origPtr: patches a specific class's vtable
    // slot with a generic-thunk closure and returns the saved original pointer (0 on failure). Idempotent.
    private static delegate* unmanaged<nint, nint, int, nint> _installVtable;
    public static void SetInstallVtable(nint fnPtr) => _installVtable = (delegate* unmanaged<nint, nint, int, nint>)fnPtr;

    // native restore_vtable(slotAddr, origMethodPtr): writes origMethodPtr back into slotAddr.
    private static delegate* unmanaged<nint, nint, void> _restoreVtable;
    public static void SetRestoreVtable(nint fnPtr) => _restoreVtable = (delegate* unmanaged<nint, nint, void>)fnPtr;

    // Per-install record for vtable hooks: (slot address, saved orig, bound mi). Under _reg lock.
    // Drives the restore lifecycle: when an entry's callbacks go empty, TryRestoreEmptyEntry restores
    // all slots registered for that mi and removes the records so a future re-install starts clean.
    private static readonly List<(nint slot, nint orig, nint mi)> _vtableInstalls = new();
    private const int VtableInvokeDataStride = 16;   // sizeof(VirtualInvokeData) = 2 × sizeof(void*) on x64

    // --- inline-awareness --------------------------------------------------------------------
    // A methodPointer detour catches reflection / virtual / delegate / non-inlined-direct calls, but it CANNOT catch
    // call sites where IL2CPP or the C++ compiler folded the body into the caller — the call instruction is gone, so
    // there is nothing to detour and nothing recovers it at runtime. The methods most prone to this are trivial
    // leaves: field getters/setters and tiny arithmetic (e.g. `GetHealth() => Health`). We can't intercept their
    // inlined sites, but we CAN flag them at registration so an author's hook silently missing the hot path is never
    // a surprise. Routable so a loader (BepInEx / MelonLoader) can send it to its own logger.
    public static Action<string>? OnWarning = Console.Error.WriteLine;

    // Cheap byte heuristic (NOT a disassembler): a short, call-free body that returns almost
    // immediately is a prime inline candidate. 0xC3 = ret (body end), 0xCC = int3 padding,
    // 0xE8/0xFF = call (=> not a leaf, unlikely to be fully inlined). Advisory only.
    private static bool LooksInlineProne(nint methodPointer, out int bodyLen)
    {
        bodyLen = -1;
        if (methodPointer == 0) return false;
        byte* p = (byte*)methodPointer;
        for (int i = 0; i < 48; i++)
        {
            byte b = p[i];
            if (b == 0xE8 || b == 0xFF) return false;          // a call -> has real work, won't fully inline
            if (b == 0xC3) { bodyLen = i + 1; return i < 24; } // ret within ~24B -> trivial leaf
            if (b == 0xCC) { bodyLen = i;     return i < 24; } // hit padding -> body already ended
        }
        return false;
    }

    // --- the typed, reference/expression-based registration API (L3) ---
    // The lambda is used ONLY to name the method (its body is a single call); overload
    // resolution routes void-returning methods to the Action forms and the rest to Func.
    // Each returns an IDisposable: dispose to remove just THIS hook (the granular path). For
    // bulk removal on mod/ALC unload, use RemoveAll(AssemblyLoadContext) — neither requires the
    // mod to have tracked anything, but the handle is there when fine control is wanted.
    public static IDisposable Pre<T>(Expression<Action<T>> sel, HookCallback cb) => Register(sel, cb, post: false);
    public static IDisposable Pre<T, R>(Expression<Func<T, R>> sel, HookCallback cb) => Register(sel, cb, post: false);
    public static IDisposable Post<T>(Expression<Action<T>> sel, HookCallback cb) => Register(sel, cb, post: true);
    public static IDisposable Post<T, R>(Expression<Func<T, R>> sel, HookCallback cb) => Register(sel, cb, post: true);

    // --- name-based addressing (L3') — hook by proxy TYPE + method NAME, no expression selector ---
    // For methods an expression can't (or shouldn't) name: Il2CppInterop FLATTENS interfaces, so an
    // explicit interface impl lands as a regular public method with a MANGLED name (e.g. ITicker.Tick ->
    // `ToyGame_ITicker_Tick`) and no `: IFoo` on the proxy — there is no interface to dispatch through, so
    // you address the concrete method directly. Also covers non-public proxy methods the expression API
    // can't reference. Resolution is plain reflection on the proxy, so it returns the MOST-DERIVED method
    // for a name (the override, not the slot base) — no virtual re-resolution needed. Pass `signature`
    // (the parameter proxy types) to pick one of an overloaded name. The native install path is identical
    // to the expression API. (Truly UNPROJECTED methods — no proxy member at all — need il2cpp-metadata
    // resolution + a native-derived MethodPlan; the metadata path below, not this.)
    public static IDisposable Pre(Type owner, string method, HookCallback cb) => RegisterMethod(ResolveByName(owner, method, null), cb, post: false);
    public static IDisposable Post(Type owner, string method, HookCallback cb) => RegisterMethod(ResolveByName(owner, method, null), cb, post: true);
    public static IDisposable Pre(Type owner, string method, Type[] signature, HookCallback cb) => RegisterMethod(ResolveByName(owner, method, signature), cb, post: false);
    public static IDisposable Post(Type owner, string method, Type[] signature, HookCallback cb) => RegisterMethod(ResolveByName(owner, method, signature), cb, post: true);

    private static IDisposable Register(LambdaExpression sel, HookCallback cb, bool post)
    {
        if (sel.Body is not MethodCallExpression call)
            throw new ArgumentException($"hook selector must be a single method call, got {sel.Body.NodeType}");
        MethodInfo method = call.Method;

        // A virtual-call selector binds to the SLOT-INTRODUCING declaration, not the override the author
        // named. C# member lookup for `(Boss b) => b.Evaluate(x)` yields Entity.Evaluate (the `newslot`
        // base) because an `override` introduces no new member — yet il2cpp dispatches to the override
        // (Boss.Evaluate) at runtime (Il2CppInterop emits il2cpp_object_get_virtual_method + invoke), so
        // detouring the base's body would never fire. Re-resolve to the most-derived override on the
        // selector's RECEIVER type — the body every dispatch path actually enters. (An interface-typed
        // selector can't be resolved this way: the runtime target is the unknown implementor, not the
        // interface method itself — hook the concrete type, or use the name-based Pre/Post(Type,string,..)
        // overload to reach a flattened/mangled explicit impl directly.)
        if (method.IsVirtual && !method.IsFinal && call.Object?.Type is Type recv && !recv.IsInterface)
            method = MostDerivedOverride(recv, method);

        return RegisterMethod(method, cb, post);
    }

    // Resolve a hook target by name on its proxy type. Reflection returns the most-derived method for the
    // name (so a virtual name yields the override, not the slot base). Throws a directed error on no-match
    // or an ambiguous (overloaded) name — pass `signature` to disambiguate.
    private static MethodInfo ResolveByName(Type owner, string name, Type[]? signature)
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? m;
        if (signature is not null)
            m = owner.GetMethod(name, F, binder: null, types: signature, modifiers: null);
        else
            try { m = owner.GetMethod(name, F); }
            catch (AmbiguousMatchException)
            {
                throw new InvalidOperationException($"'{name}' is overloaded on {owner.FullName}; pass a signature Type[] to disambiguate");
            }
        if (m is null)
            throw new InvalidOperationException($"no method '{name}'{(signature is null ? "" : " with the given signature")} on {owner.FullName}");
        return m;
    }

    // RegisterMethod (proxy path) and RegisterNative (metadata path) both produce a native MethodInfo* + a
    // MethodPlan, then funnel into InstallAndAppend — the ONE place that wires the detour and appends the
    // hook. They differ ONLY in their input: a System.Reflection.MethodInfo (an Il2CppInterop-projected
    // proxy) vs a raw MethodInfo* named straight from il2cpp metadata (the unprojected long tail).
    private static IDisposable RegisterMethod(MethodInfo method, HookCallback cb, bool post)
    {
        nint mi = ResolveMethodInfo(method);
        if (mi == 0) throw new InvalidOperationException($"no il2cpp MethodInfo* for {method.DeclaringType!.Name}.{method.Name}");
        // A reference-type-only generic method is the ONE shape with no detourable methodPointer (il2cpp runs
        // it through MethodInfo::invoker_method); a value-type instantiation has its own body. Managed
        // reflection carries the instantiation, so the proxy path can tell which.
        bool canon = method.IsGenericMethod;
        if (canon) foreach (Type t in method.GetGenericArguments()) if (t.IsValueType) { canon = false; break; }
        return InstallAndAppend(mi, MethodPlan.Of(method, mi), canon, $"{method.DeclaringType!.Name}.{method.Name}", cb, post);
    }

    // The shared install pipeline: wrap the MethodInfo*, detour its entry pointer(s) ONCE (or overwrite the
    // invoker for a __Canon generic), then append this hook to the copy-on-write array. `label` is for
    // diagnostics only; `plan` was built by whichever front-end resolved `mi` (Of for proxy, OfNative for
    // metadata) — both produce the identical plan (proven by the self-test), so the hot path is agnostic.
    private static IDisposable InstallAndAppend(nint mi, MethodPlan plan, bool canon, string label, HookCallback cb, bool post)
    {
        var w = UnityVersionHandler.Wrap((Il2CppMethodInfo*)mi);
        // IL2CPP MethodInfo carries TWO entry pointers: `methodPointer` (the invoke/reflection path) and
        // `virtualMethodPointer` (what compiled call sites + the shared-generic invoker actually enter,
        // verified against the il2cpp source for a __Canon generic method — they diverge there). We detour
        // BOTH (when distinct), keyed by the SAME MethodInfo*, so the trailing-MethodInfo* routing in the
        // core disambiguates instantiations no matter which entry the runtime took.
        nint mp = w.MethodPointer;
        nint vmp = w.VirtualMethodPointer;
        if (mp == 0 && vmp == 0) throw new InvalidOperationException($"no entry pointer for {label}");

        lock (_reg)
        {
            if (!Table.TryGetValue(mi, out var e))
            {
                // First hook on this method: install the native detour ONCE. Subsequent hooks (this mod or
                // another) just append to the array — one detour, N hooks.
                e = new Entry { Plan = plan };

                // Il2CppInterop drives EVERY proxy call through il2cpp_runtime_invoke (verified by decompiling
                // the generated proxy); its invoker thunk unpacks the args into registers and enters the
                // method's `methodPointer`. So a methodPointer detour catches interop calls AND compiled in-game
                // call sites (direct + vtable virtual) alike. The ONE shape with no detourable methodPointer is a
                // fully-shared __Canon generic METHOD: il2cpp runs it through MethodInfo::invoker_method, which
                // we overwrite instead.
                // Read the original body BEFORE the detour patches the prologue (advisory; non-canon leaves only —
                // a __Canon body is shared real code, never a trivial inlined leaf).
                if (!canon && mp != 0 && LooksInlineProne(mp, out int bodyLen))
                    OnWarning?.Invoke(
                        $"inutil: {label} has a trivial leaf body (~{bodyLen}B) and is " +
                        "likely inlined at its call sites; this hook fires for virtual/reflection/delegate/non-inlined " +
                        "calls only — inlined sites cannot be intercepted (fundamental; see DESIGN 'Inlining boundary').");

                // Detour the body pointer(s) — for a non-generic method AND a fully-shared __Canon generic
                // instantiation alike. A __Canon instantiation DOES expose a detourable body: its
                // methodPointer/virtualMethodPointer point to the SHARED body that EVERY reference-type
                // instantiation enters — and so does the reflection invoker (il2cpp_runtime_invoke's
                // invoker_method tail-calls it), so a body detour subsumes the invoke path too. The core dedups
                // the shared pointer (install) and routes each call by its LIVE trailing MethodInfo* (dispatch_key
                // + miSlot), so distinct instantiations stay distinct and an UNregistered one falls through to the
                // original (Dispatch returns 0 on a key miss). This is the entry the game's OWN compiled generic
                // call sites take — which overwriting MethodInfo::invoker_method (the il2cpp_runtime_invoke /
                // reflection path ONLY) silently misses.
                if (_install == null) throw new InvalidOperationException("native install callback not set");
                bool ok = mp != 0 && _install(mp, mi, plan.MiSlot) != 0;
                if (vmp != 0 && vmp != mp) ok |= _install(vmp, mi, plan.MiSlot) != 0;
                if (!ok)
                {
                    // The body-pointer detour failed to take. For a fully-shared __Canon generic, fall back to
                    // overwriting MethodInfo::invoker_method — strictly worse (catches reflection-invoked calls
                    // only, not compiled call sites) but better than nothing on a build whose shared body pointer
                    // is not directly detourable. A non-generic method has no such fallback.
                    if (!canon) throw new InvalidOperationException($"native install failed for {label}");
                    if (_installInvoker == null) throw new InvalidOperationException("invoke-path install callback not set");
                    if (_installInvoker(mi) == 0) throw new InvalidOperationException($"invoker install failed for {label}");
                }
                Table[mi] = e;
            }

            // Append to the copy-on-write array (ordered: pre in registration order, post reversed at
            // dispatch for onion nesting). The volatile write publishes the new array to dispatchers.
            if (post) e.Post = Append(e.Post, cb);
            else      e.Pre  = Append(e.Pre, cb);
            return new Subscription(mi, post, cb);
        }
    }

    // --- unprojected addressing (L3'') — hook by a raw MethodInfo* / by il2cpp METADATA, no proxy at all ---
    // Il2CppInterop only projects a fraction of a real
    // game's methods; the high-value targets (obfuscated, private, base-typed) have NO proxy member to name
    // with an expression or a Type+string. This reaches them through il2cpp metadata directly. Two layers:
    //
    //   PreNative/PostNative(nint mi, cb) — the RAW primitive: hook a method by its native MethodInfo*. This
    //     is the "detour this method right now" capability ot-eval/a REPL needs (it can already FIND a
    //     MethodInfo*, but had no way to detour one without generated glue). Plan built by MethodPlan.OfNative.
    //   ResolveNative(asm, ns, type, method, ...) — name a method PURELY from metadata strings (loader-
    //     independent: the il2cpp namespace is the GAME's, never the interop "Il2Cpp" proxy prefix) and return
    //     its MethodInfo*. Pre/Post(string asm, ...) compose the two for one-call ergonomics.
    public static IDisposable PreNative(nint methodInfo, HookCallback cb) => RegisterNative(methodInfo, cb, post: false);
    public static IDisposable PostNative(nint methodInfo, HookCallback cb) => RegisterNative(methodInfo, cb, post: true);

    public static IDisposable Pre(string assembly, string @namespace, string type, string method, HookCallback cb, int argc = -1, string[]? paramTypeNames = null)
        => RegisterNative(ResolveNative(assembly, @namespace, type, method, argc, paramTypeNames), cb, post: false);
    public static IDisposable Post(string assembly, string @namespace, string type, string method, HookCallback cb, int argc = -1, string[]? paramTypeNames = null)
        => RegisterNative(ResolveNative(assembly, @namespace, type, method, argc, paramTypeNames), cb, post: true);

    private static IDisposable RegisterNative(nint mi, HookCallback cb, bool post)
    {
        if (mi == 0) throw new ArgumentException("native MethodInfo* is null", nameof(mi));
        // An OPEN generic method definition (unbound T) has no body the runtime ever enters; you must inflate
        // it to a closed instantiation first. ResolveNative finds the def like any method, so direct it to
        // InflateNative rather than installing on a phantom body. (A closed/inflated mi reports is_generic=false
        // and falls through — InflateNative's result lands here and hooks like any other method.)
        if (IL2CPP.il2cpp_method_is_generic(mi))
            throw new NotSupportedException($"{NativeLabel(mi)} is an OPEN generic method definition; inflate it with InflateNative(mi, typeArgClasses) to a closed instantiation first, then hook that.");
        // A closed instantiation self-routes: a value-type instantiation has its OWN compiled body (methodPointer
        // detour, canon=false); an all-reference-type instantiation is the fully-shared __Canon body il2cpp runs
        // via MethodInfo::invoker_method (invoker overwrite, canon=true). Non-generic methods are never canon.
        bool canon = IsCanonInflated(mi);
        return InstallAndAppend(mi, MethodPlan.OfNative(mi), canon, NativeLabel(mi), cb, post);
    }

    // --- vtable-scoped class hooks (L3v): patch a specific class's vtable slot directly. ----------------
    // Use when the target method body is a shared empty stub — il2cpp collapses all empty virtual bodies
    // to one address, so a global methodPointer detour on such a method fires for a flood of unrelated
    // empty methods and crashes. A vtable hook patches ONLY the slot of the named class, so dispatch is
    // scoped to instances whose runtime class carries that slot value. The slot is restored when all
    // callbacks for the method are removed (ALC unload / explicit Dispose).
    //
    // classPtr MUST be the live Il2CppClass* resolved from an actual instance (not by name) — resolving by
    // name at startup may miss the real runtime subclass the game actually allocates. Proceed() works: the
    // thunk calls through the saved original methodPtr directly (no MinHook trampoline needed since we did
    // not modify the function body).
    public static IDisposable PreVtable<T>(nint classPtr, Expression<Action<T>> sel, HookCallback cb)
        => RegisterVtable(classPtr, sel, cb, post: false);
    public static IDisposable PreVtable<T, R>(nint classPtr, Expression<Func<T, R>> sel, HookCallback cb)
        => RegisterVtable(classPtr, sel, cb, post: false);
    public static IDisposable PostVtable<T>(nint classPtr, Expression<Action<T>> sel, HookCallback cb)
        => RegisterVtable(classPtr, sel, cb, post: true);
    public static IDisposable PostVtable<T, R>(nint classPtr, Expression<Func<T, R>> sel, HookCallback cb)
        => RegisterVtable(classPtr, sel, cb, post: true);

    // Raw vtable hook by native MethodInfo* — the REPL / metadata path (no proxy available).
    public static IDisposable PreVtable(nint classPtr, nint mi, HookCallback cb)
        => RegisterVtableCore(classPtr, mi, MethodPlan.OfNative(mi), cb, post: false);
    public static IDisposable PostVtable(nint classPtr, nint mi, HookCallback cb)
        => RegisterVtableCore(classPtr, mi, MethodPlan.OfNative(mi), cb, post: true);

    // Resolve the address of VirtualInvokeData.methodPtr for `mi`'s slot in `classPtr`'s vtable.
    // Returns 0 if the vtable is not yet initialised or the slot index is out of range.
    public static nint ResolveVtableSlotAddress(nint classPtr, nint mi)
    {
        if (classPtr == 0 || mi == 0) return 0;
        var ks = UnityVersionHandler.Wrap((Il2CppClass*)classPtr);
        if (!ks.IsVtableInitialized) return 0;
        var ms = UnityVersionHandler.Wrap((Il2CppMethodInfo*)mi);
        int slot = ms.Slot;
        int count = ks.VtableCount;
        nint vtable = ks.VTable;
        if (vtable == 0 || slot < 0 || slot >= count) return 0;
        return vtable + slot * VtableInvokeDataStride;
    }

    private static IDisposable RegisterVtable(nint classPtr, LambdaExpression sel, HookCallback cb, bool post)
    {
        if (sel.Body is not MethodCallExpression call)
            throw new ArgumentException($"hook selector must be a single method call, got {sel.Body.NodeType}");
        MethodInfo method = call.Method;
        if (method.IsVirtual && !method.IsFinal && call.Object?.Type is Type recv && !recv.IsInterface)
            method = MostDerivedOverride(recv, method);
        nint mi = ResolveMethodInfo(method);
        if (mi == 0) throw new InvalidOperationException($"no il2cpp MethodInfo* for {method.DeclaringType!.Name}.{method.Name}");
        return RegisterVtableCore(classPtr, mi, MethodPlan.Of(method, mi), cb, post);
    }

    private static IDisposable RegisterVtableCore(nint classPtr, nint mi, MethodPlan plan, HookCallback cb, bool post)
    {
        if (_installVtable == null) throw new InvalidOperationException("vtable install callback not set — was SetInstallVtable called?");
        nint slotAddr = ResolveVtableSlotAddress(classPtr, mi);
        if (slotAddr == 0) throw new InvalidOperationException(
            $"could not resolve vtable slot for {NativeLabel(mi)} on class 0x{classPtr:x} (vtable not initialised?)");

        lock (_reg)
        {
            if (!Table.TryGetValue(mi, out var e))
                e = new Entry { Plan = plan };

            // Install on this class's slot if not already tracked. The same mi can appear for multiple
            // classes (same method, different runtime subclasses) — each gets its own slot patched,
            // dispatching into the shared entry keyed by mi.
            bool slotTracked = false;
            foreach (var t in _vtableInstalls) if (t.slot == slotAddr) { slotTracked = true; break; }
            if (!slotTracked)
            {
                nint orig = _installVtable(slotAddr, mi, -1);   // miSlot=-1: always key by boundMethodInfo
                if (orig == 0) throw new InvalidOperationException(
                    $"vtable install failed for {NativeLabel(mi)} slot 0x{slotAddr:x}");
                _vtableInstalls.Add((slotAddr, orig, mi));
            }

            Table[mi] = e;
            if (post) e.Post = Append(e.Post, cb);
            else      e.Pre  = Append(e.Pre, cb);
            return new Subscription(mi, post, cb);
        }
    }

    // Restore all vtable slots registered for `mi` if its entry now has no callbacks. Called under _reg
    // lock. No-op for regular (MinHook) hooks — those have no _vtableInstalls entries for their mi.
    private static void TryRestoreEmptyEntry(nint mi)
    {
        if (!Table.TryGetValue(mi, out var e) || e.Pre.Length > 0 || e.Post.Length > 0) return;
        if (_restoreVtable == null) return;
        for (int i = _vtableInstalls.Count - 1; i >= 0; i--)
        {
            var (slot, orig, slotMi) = _vtableInstalls[i];
            if (slotMi != mi) continue;
            try { _restoreVtable(slot, orig); } catch { }
            _vtableInstalls.RemoveAt(i);
        }
    }

    // True iff `mi` is a fully-shared generic-METHOD instantiation — every type argument a reference type, so
    // il2cpp folds it onto ONE __Canon body with no per-instantiation methodPointer and dispatches it through
    // MethodInfo::invoker_method. This is the EXACT shape the proxy path flags `canon` for (method.IsGenericMethod
    // && every arg a reference type); here it's read from the closed mi alone (so PreNative(inflated) routes
    // itself). Determined via the inflated method's reflection object: only an inflated method can be __Canon,
    // and its own generic arguments (empty for a non-generic method on a generic TYPE, e.g. Container<int>.Get,
    // which keeps its own methodPointer body) decide it.
    private static bool IsCanonInflated(nint mi)
    {
        if (!IL2CPP.il2cpp_method_is_inflated(mi)) return false;   // non-generic, or open — neither is __Canon
        nint declClass = IL2CPP.il2cpp_method_get_class(mi);
        var rm = new Il2CppSystem.Reflection.MethodInfo(IL2CPP.il2cpp_method_get_object(mi, declClass));
        var ga = rm.GetGenericArguments();
        if (ga is null || ga.Length == 0) return false;           // inflated via its TYPE only -> methodPointer body
        foreach (Il2CppSystem.Type ta in ga) if (ta.IsValueType) return false;   // any value-type arg -> own body
        return true;                                              // all reference-type args -> shared __Canon -> invoker
    }

    // Public mirror of the canon classification: shows how a hook on `mi` will be installed — invoker overwrite
    // (true) for a fully-shared all-reference generic instantiation, methodPointer detour (false) otherwise.
    public static bool IsSharedGenericInstantiation(nint mi) => IsCanonInflated(mi);

    // --- generic-method inflation: an OPEN generic definition + concrete type args -> the CLOSED instantiation's
    // MethodInfo* (the one il2cpp actually compiles/dispatches). ResolveNative names the open def like any method,
    // but an open def has no detourable body — inflate it. This drives il2cpp's OWN inflation, the exact chain
    // Il2CppInterop's generated MethodInfoStoreGeneric<T> cctor uses (il2cpp_method_get_object -> MakeGenericMethod
    // -> il2cpp_method_get_from_reflection), so the result is the SAME interned MethodInfo* the proxy path keys on
    // (cross-checked in the demo: native-inflated mi == proxy-inflated mi). Type arguments are given as il2cpp
    // CLASS pointers — what an ot-eval metadata walk already holds; ResolveClass names one from metadata strings.
    // The closed mi then hooks via PreNative/PostNative like any method (it self-routes value-type vs __Canon).
    public static nint InflateNative(nint genericMethodDef, params nint[] typeArgClasses)
    {
        if (genericMethodDef == 0) throw new ArgumentException("generic method definition is null", nameof(genericMethodDef));
        if (!IL2CPP.il2cpp_method_is_generic(genericMethodDef))
            throw new ArgumentException($"{NativeLabel(genericMethodDef)} is not an open generic method definition (nothing to inflate)", nameof(genericMethodDef));
        if (typeArgClasses is null || typeArgClasses.Length == 0)
            throw new ArgumentException("at least one type-argument class is required", nameof(typeArgClasses));

        // class -> its Il2CppType* -> a System.Type reflection object: the element MakeGenericMethod wants.
        var typeArgs = new Il2CppSystem.Type[typeArgClasses.Length];
        for (int i = 0; i < typeArgClasses.Length; i++)
        {
            nint cls = typeArgClasses[i];
            if (cls == 0) throw new ArgumentException($"type-argument class #{i} is null", nameof(typeArgClasses));
            typeArgs[i] = new Il2CppSystem.Type(IL2CPP.il2cpp_type_get_object(IL2CPP.il2cpp_class_get_type(cls)));
        }

        nint declClass = IL2CPP.il2cpp_method_get_class(genericMethodDef);
        var openRefl = new Il2CppSystem.Reflection.MethodInfo(IL2CPP.il2cpp_method_get_object(genericMethodDef, declClass));
        Il2CppSystem.Reflection.MethodInfo closedRefl;
        try { closedRefl = openRefl.MakeGenericMethod(typeArgs); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"inflating {NativeLabel(genericMethodDef)} with {typeArgClasses.Length} type arg(s) failed: {ex.Message} " +
                "(arity mismatch or a type that violates a generic constraint?)", ex);
        }
        nint closed = IL2CPP.il2cpp_method_get_from_reflection(closedRefl.Pointer);
        if (closed == 0) throw new InvalidOperationException($"inflation of {NativeLabel(genericMethodDef)} produced no closed MethodInfo*");
        return closed;
    }

    // Resolve a type to its native il2cpp class pointer purely from metadata — by assembly image name +
    // namespace + type name. The atom both ResolveNative (to find the owning class) and InflateNative (to name a
    // type argument) build on. Throws a directed error if the image or type isn't found.
    public static nint ResolveClass(string assembly, string @namespace, string type)
    {
        nint image = FindImage(assembly);
        if (image == 0) throw new InvalidOperationException($"no loaded assembly image named '{assembly}'");
        nint klass = IL2CPP.il2cpp_class_from_name(image, @namespace, type);
        if (klass == 0) throw new InvalidOperationException($"no type '{@namespace}.{type}' in '{assembly}'");
        return klass;
    }

    // Resolve a method to its native MethodInfo* purely from il2cpp metadata — NO Il2CppInterop proxy. Finds
    // the assembly image + class by name (ResolveClass), then the method by name (disambiguating with `argc`
    // and/or `paramTypeNames` when a name is overloaded). Iterates the class's own methods so same-arity
    // overloads (which il2cpp_class_get_method_from_name, keyed on name+argc only, can't separate) are reachable
    // via paramTypeNames. Each paramTypeName matches the il2cpp type name exactly OR as a suffix ("Int32" matches
    // "System.Int32"). Throws a directed error on no-match / ambiguity. (A generic METHOD resolves to its OPEN
    // definition here — pass it to InflateNative to reach a closed instantiation.)
    public static nint ResolveNative(string assembly, string @namespace, string type, string method, int argc = -1, string[]? paramTypeNames = null)
    {
        nint klass = ResolveClass(assembly, @namespace, type);

        var matches = new List<nint>();
        nint iter = 0, m;
        while ((m = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != 0)
        {
            if (Utf8(IL2CPP.il2cpp_method_get_name(m)) != method) continue;
            int pc = (int)IL2CPP.il2cpp_method_get_param_count(m);
            if (argc >= 0 && pc != argc) continue;
            if (paramTypeNames is not null)
            {
                if (pc != paramTypeNames.Length) continue;
                bool sigOk = true;
                for (uint i = 0; i < pc; i++)
                    if (!ParamTypeMatches(IL2CPP.il2cpp_method_get_param(m, i), paramTypeNames[i])) { sigOk = false; break; }
                if (!sigOk) continue;
            }
            matches.Add(m);
        }

        if (matches.Count == 0)
            throw new InvalidOperationException($"no method '{method}' on {@namespace}.{type}" +
                (argc >= 0 ? $" with {argc} arg(s)" : "") +
                (paramTypeNames is not null ? $" matching ({string.Join(",", paramTypeNames)})" : ""));
        if (matches.Count > 1)
            throw new InvalidOperationException($"'{method}' on {@namespace}.{type} is overloaded ({matches.Count} matches); pass argc and/or paramTypeNames to disambiguate");
        return matches[0];
    }

    // Does method-param type `paramType` satisfy the caller's `want` type name? Matched generously so a caller
    // needn't echo il2cpp's exact formatting — `want` is accepted as ANY of:
    //   - the full il2cpp type name        ("System.Int32", "ToyGame.Vec3", "System.Int32&" for a `ref int`)
    //   - its dotted suffix                ("Int32" ~ "System.Int32", "Vec3" ~ "ToyGame.Vec3")
    //   - the param's simple CLASS name    (il2cpp_class_from_type strips byref -> "Int32", "Vec3", "Player")
    // The simple-class form makes primitive disambiguation robust regardless of whether il2cpp renders a
    // keyword or the FQN; the full-name/suffix forms keep precision (e.g. pass "Int32&" to pick a `ref int`
    // overload specifically, since the class name alone can't tell `int` from `ref int`).
    private static bool ParamTypeMatches(nint paramType, string want)
    {
        string tn = Utf8(IL2CPP.il2cpp_type_get_name(paramType));
        if (tn == want || tn.EndsWith("." + want, StringComparison.Ordinal)) return true;
        nint cls = IL2CPP.il2cpp_class_from_type(paramType);
        return cls != 0 && Utf8(IL2CPP.il2cpp_class_get_name(cls)) == want;
    }

    // The loaded image whose name is `assembly` (with or without a ".dll" suffix), or 0. il2cpp keys images
    // by their on-disk name (e.g. "Assembly-CSharp.dll").
    private static nint FindImage(string assembly)
    {
        nint domain = IL2CPP.il2cpp_domain_get();
        uint n = 0;
        nint* asms = IL2CPP.il2cpp_domain_get_assemblies(domain, ref n);
        for (uint i = 0; i < n; i++)
        {
            nint img = IL2CPP.il2cpp_assembly_get_image(asms[i]);
            string nm = Utf8(IL2CPP.il2cpp_image_get_name(img));
            if (nm == assembly || nm == assembly + ".dll") return img;
        }
        return 0;
    }

    // "Class.Method" for diagnostics, straight from the native MethodInfo* (no proxy needed).
    private static string NativeLabel(nint mi)
    {
        string name = Utf8(IL2CPP.il2cpp_method_get_name(mi));
        nint klass = IL2CPP.il2cpp_method_get_class(mi);
        string cls = klass == 0 ? "?" : Utf8(IL2CPP.il2cpp_class_get_name(klass));
        return $"{cls}.{name}";
    }

    // il2cpp metadata strings are UTF-8 char*; null pointer -> empty.
    private static string Utf8(nint p) => p == 0 ? "" : (System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p) ?? "");

    // Introspection / cross-check: the native MethodInfo* an expression selector resolves to (the same key
    // the proxy hook path keys on, after the same virtual-override re-resolution). Lets a caller VERIFY that
    // metadata naming (ResolveNative) lands on the EXACT method the typed proxy path would — proof that the
    // unprojected front-end addresses the genuine il2cpp method, not a lookalike. Returns 0 if unresolvable.
    public static nint ProxyMethodInfo<T>(Expression<Action<T>> sel) => ProxyMethodInfoCore(sel.Body);
    public static nint ProxyMethodInfo<T, R>(Expression<Func<T, R>> sel) => ProxyMethodInfoCore(sel.Body);  // value/object-returning selectors (e.g. Echo<int>)

    // The native il2cpp MethodInfo* a PROXY MethodInfo resolves to — the table key the hook engine uses. The
    // Hook<T> facade resolves its matched game method by reflection (name + parameter types) and needs this key
    // to (a) dedupe a single engine detour per game method across many hook classes and (b) install via
    // PreNative. Returns 0 if the proxy shape carries no il2cpp MethodInfo* (not interop-projected).
    public static nint NativeMethodInfoOf(MethodInfo proxyMethod) => ResolveMethodInfo(proxyMethod);

    private static nint ProxyMethodInfoCore(Expression body)
    {
        if (body is not MethodCallExpression call)
            throw new ArgumentException($"selector must be a single method call, got {body.NodeType}");
        MethodInfo method = call.Method;
        if (method.IsVirtual && !method.IsFinal && call.Object?.Type is Type recv && !recv.IsInterface)
            method = MostDerivedOverride(recv, method);
        return ResolveMethodInfo(method);
    }

    private static HookCallback[] Append(HookCallback[] a, HookCallback cb)
    {
        var n = new HookCallback[a.Length + 1];
        Array.Copy(a, n, a.Length);
        n[a.Length] = cb;
        return n;
    }

    // --- removal: granular (the IDisposable handle) + bulk-by-owner (mod/ALC unload) ----------------
    // The hook delegate's Target/Method live in the mod's assembly, so storing it in this host-static
    // table is the ONLY thing rooting that mod's ALC (DESIGN hard-constraint #2). Removing it un-roots
    // the ALC — which is exactly how M-live makes a collectible context actually collect.

    // Remove every hook registered from `owner`'s assembly load context. The host's M-live driver calls
    // this just before alc.Unload(); the owner is derived from each delegate (the mod tracked nothing).
    // For vtable hooks, if filtering empties an entry, TryRestoreEmptyEntry patches the original
    // methodPtr back — mandatory before the collectible ALC retires (its closures become dangling).
    public static int RemoveAll(AssemblyLoadContext owner)
    {
        int removed = 0;
        lock (_reg)
            foreach (var kvp in Table)
            {
                var e = kvp.Value;
                e.Pre  = Filter(e.Pre,  owner, ref removed);
                e.Post = Filter(e.Post, owner, ref removed);
                TryRestoreEmptyEntry(kvp.Key);
            }
        return removed;
    }

    private static HookCallback[] Filter(HookCallback[] a, AssemblyLoadContext owner, ref int removed)
    {
        int keep = 0;
        foreach (var cb in a) if (OwnerOf(cb) != owner) keep++;
        if (keep == a.Length) return a;                  // nothing of this owner here
        var n = keep == 0 ? Array.Empty<HookCallback>() : new HookCallback[keep];
        int j = 0;
        foreach (var cb in a) if (OwnerOf(cb) != owner) n[j++] = cb;
        removed += a.Length - keep;
        return n;
    }

    private static void Remove(nint mi, bool post, HookCallback cb)
    {
        lock (_reg)
        {
            if (!Table.TryGetValue(mi, out var e)) return;
            var a = post ? e.Post : e.Pre;
            int idx = Array.IndexOf(a, cb);
            if (idx < 0) return;
            var n = a.Length == 1 ? Array.Empty<HookCallback>() : new HookCallback[a.Length - 1];
            Array.Copy(a, 0, n, 0, idx);
            Array.Copy(a, idx + 1, n, idx, n.Length - idx);
            if (post) e.Post = n; else e.Pre = n;
            // For MinHook detours the slot stays permanently installed (a cheap empty-dispatch no-op).
            // For vtable hooks the slot MUST be restored when the entry goes empty — the closure is in a
            // collectible ALC that may retire, making the pointer dangling.
            TryRestoreEmptyEntry(mi);
        }
    }

    // The ALC that owns a hook delegate = the load context of the assembly that defined it. Default/null
    // (host code) maps to null — RemoveAll(modAlc) never matches host hooks. Derived on demand (removal
    // is rare), so nothing extra is stored per hook and nothing extra roots the ALC.
    private static AssemblyLoadContext? OwnerOf(HookCallback cb)
        => AssemblyLoadContext.GetLoadContext(cb.Method.Module.Assembly);

    private sealed class Subscription : IDisposable
    {
        private readonly nint _mi; private readonly bool _post; private HookCallback? _cb;
        public Subscription(nint mi, bool post, HookCallback cb) { _mi = mi; _post = post; _cb = cb; }
        public void Dispose() { var cb = _cb; if (cb is null) return; _cb = null; Remove(_mi, _post, cb); }
    }

    // Resolve a proxy MethodInfo to its native il2cpp MethodInfo* — the table key + the source of the
    // methodPointer to detour. Il2CppInterop holds the pointer in a generated static field; ONE path
    // covers all three shapes because the helper, given the CLOSED method, returns the field on the
    // CLOSED owner, and running THAT owner's cctor populates the pointer:
    //   - non-generic method (Player.TakeDamage) ... `NativeMethodInfoPtr_*` on the declaring proxy
    //   - generic-TYPE method (Container<int>.Get) . same field, but on the CLOSED proxy type — .NET
    //       gives each closed generic its own static copy, so each instantiation carries its own key
    //   - generic METHOD (Game.Echo<int>) .......... the INFLATED pointer lives in a generated
    //       `MethodInfoStoreGeneric<int>.Pointer` (cctor: il2cpp_method_get_object -> MakeGenericMethod
    //       -> il2cpp_method_get_from_reflection). The helper returns exactly that closed field.
    private static nint ResolveMethodInfo(MethodInfo method)
    {
        RuntimeHelpers.RunClassConstructor(method.DeclaringType!.TypeHandle);
        FieldInfo? fld = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
        if (fld is null) return 0;
        // For a generic method the field lives on the closed MethodInfoStoreGeneric<T>, not the proxy;
        // run its cctor too so the inflated pointer is populated before we read it.
        if (fld.DeclaringType != method.DeclaringType)
            RuntimeHelpers.RunClassConstructor(fld.DeclaringType!.TypeHandle);
        return (nint)(fld.GetValue(null) ?? (nint)0);
    }

    // The method an instance of `receiver` actually dispatches `baseMethod` to: receiver's most-derived
    // override of the same virtual slot. Reflection collapses an overridden method to its override on the
    // derived type, so the single declared method whose GetBaseDefinition() matches IS the runtime target.
    // Falls back to baseMethod when receiver doesn't override it (or isn't in its hierarchy).
    private static MethodInfo MostDerivedOverride(Type receiver, MethodInfo baseMethod)
    {
        MethodInfo baseDef = baseMethod.GetBaseDefinition();
        foreach (MethodInfo m in receiver.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (m.GetBaseDefinition() == baseDef) return m;
        return baseMethod;
    }

    // --- self-test: the native-metadata plan must MATCH the proxy-derived plan --------------------
    // For a fully-projected target (ToyGame) MethodPlan.OfNative(mi) and MethodPlan.Of(proxyMethod) must
    // produce identical plans on EVERY method — the proxy path is the trusted oracle (it's what the live
    // hook battery validates). Any divergence is a bug in the native classifier (or a real ABI subtlety to
    // chase). Methods with no il2cpp MethodInfo* (not interop-projected) are skipped: they're exactly what
    // OfNative will later serve, but here neither path has both inputs to cross-check. Returns the number
    // compared + a list of "Type.Method: <first-divergence>" for any that disagreed (empty == all agree).
    public static (int compared, List<string> mismatches) SelfTestNativePlan(IEnumerable<MethodInfo> methods)
    {
        var bad = new List<string>();
        int compared = 0;
        foreach (MethodInfo m in methods)
        {
            nint mi;
            try { mi = ResolveMethodInfo(m); } catch { continue; }   // unresolvable proxy shape — not in scope
            if (mi == 0) continue;                                    // not interop-projected — nothing to compare
            try
            {
                string? diff = MethodPlan.Diff(MethodPlan.Of(m, mi), MethodPlan.OfNative(mi));
                compared++;
                if (diff != null) bad.Add($"{m.DeclaringType!.Name}.{m.Name}: {diff}");
            }
            catch (Exception ex)
            {
                // Unwrap reflection/type-init WRAPPERS to the ROOT cause so the mismatch names the real failure
                // (e.g. Il2CppClassPointerStore<Nullable<enum>>'s static init failing on a deeper il2cpp lookup) —
                // a bare "TypeInitializationException" hides which native resolution actually threw.
                Exception root = ex;
                while (root is System.Reflection.TargetInvocationException or TypeInitializationException && root.InnerException is { } inner)
                    root = inner;
                bad.Add($"{m.DeclaringType!.Name}.{m.Name}: threw {root.GetType().Name}: {root.Message}");
            }
        }
        return (compared, bad);
    }

    // --- reverse-P/Invoke targets (the native thunk's pre/post dispatchers land here) ---
    // Pre-hooks run in registration order and return SKIP (any hook called ctx.Skip()); post-hooks run
    // in REVERSE order (onion nesting). Each reads the volatile array reference once, so a concurrent
    // Register/Remove never tears the iteration — it just changes what the NEXT dispatch sees.
    [UnmanagedCallersOnly]
    public static int Dispatch(nint methodInfo, CallFrame* f, RetFrame* r)
    {
        if (!Table.TryGetValue(methodInfo, out var e)) return 0;
        var pre = e.Pre;
        if (pre.Length == 0) return 0;
        int skip = 0;
        var ctx = new HookContext(f, r, e.Plan, &skip);
        for (int i = 0; i < pre.Length; i++) pre[i](ctx);
        // sret + skip: the original never ran, but the caller still expects the sret buffer POINTER in
        // RAX (Win64). It is the hidden first arg the caller passed — slot 0 of the captured frame.
        if (skip != 0 && e.Plan.Ret == Loc.Sret) r->Rax = f->Gpr[0];
        return skip;
    }

    [UnmanagedCallersOnly]
    public static void PostDispatch(nint methodInfo, CallFrame* f, RetFrame* r)
    {
        if (!Table.TryGetValue(methodInfo, out var e)) return;
        var post = e.Post;
        if (post.Length == 0) return;
        var ctx = new HookContext(f, r, e.Plan, null);   // skip is meaningless in post (original already ran)
        for (int i = post.Length - 1; i >= 0; i--) post[i](ctx);
    }

    // --- the invoke-path dispatchers (the invoker thunk lands here for __Canon generic methods) ---
    // The frame is il2cpp's invoke form: `params` (the args array) + `ret` (the return out-buffer),
    // wrapped by the SAME typed HookContext (Arg<T>/Return<T>/...), so a hook body is identical
    // whether the method was intercepted by methodPointer-detour or by invoker overwrite.
    [UnmanagedCallersOnly]
    public static int InvokePre(nint methodInfo, nint obj, void** prms, nint ret)
    {
        if (!Table.TryGetValue(methodInfo, out var e)) return 0;
        var pre = e.Pre;
        if (pre.Length == 0) return 0;
        int skip = 0;
        var ctx = new HookContext(e.Plan, prms, (void*)ret, &skip, obj);
        for (int i = 0; i < pre.Length; i++) pre[i](ctx);
        return skip;
    }

    [UnmanagedCallersOnly]
    public static void InvokePost(nint methodInfo, nint obj, void** prms, nint ret)
    {
        if (!Table.TryGetValue(methodInfo, out var e)) return;
        var post = e.Post;
        if (post.Length == 0) return;
        var ctx = new HookContext(e.Plan, prms, (void*)ret, null, obj);
        for (int i = post.Length - 1; i >= 0; i--) post[i](ctx);
    }
}
