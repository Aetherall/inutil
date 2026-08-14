// Inutil.Fields — read/write an il2cpp field BY NAME, the field-side twin of the metadata-addressed method
// hooking in Hooks.cs: a name + an Il2CppObjectBase handle, resolved through the il2cpp C ABI (IL2CPP.il2cpp_*).
// Game-AGNOSTIC — ships in Inutil.dll for every game, identically on BepInEx and MelonLoader.
//
// docs/contribution/architecture/17-reach-faces.md — a by-name member write reaches through
// `Inutil.Fields.SetStruct(_obj, "Gear", g)`.
//
// WHY this exists when Il2CppInterop already projects typed `proxy.Field` accessors: that projection only works when
// (a) a proxy member exists and (b) your handle's STATIC type declares the field. This reaches the cases it can't —
// and the cases it gets WRONG:
//   - a BASE-typed handle whose runtime object is a derived/obfuscated subclass that declares the field (interop
//     projects a field onto its declaring type only) — FindField walks the runtime class chain;
//   - a Nullable<T> field, whose interop typed projection throws/misreads (il2cpp boxes it to the inner T) —
//     GetNullable/SetNullable read/write the inline { hasValue, value } correctly;
//   - a value-type field carrying managed refs (a struct with a string/array), where a raw *(T*) store drops the
//     ref — SetStruct routes through il2cpp_field_set_value (the runtime's own GC-aware, ref-preserving store).
//
// Threading: call on the game (il2cpp-attached) thread. Null-safe: a null handle or an absent field is a quiet
// default/false, never a throw — a by-name miss is a normal, recoverable outcome (the silent-probe convention,
// docs/contribution/architecture/17-reach-faces.md: reads probe, an assertion-shaped write path would throw).
using System;
using System.Runtime.CompilerServices;   // Unsafe.SizeOf — the Nullable inline layout offset, no `unmanaged` modreq leak
using Il2CppInterop.Runtime;             // IL2CPP.il2cpp_* + Il2CppStringToManaged / ManagedStringToIl2Cpp
using Il2CppInterop.Runtime.InteropTypes;// Il2CppObjectBase (.Pointer)

namespace Inutil;

public static unsafe class Fields
{
    // FIELD_ATTRIBUTE_STATIC (ECMA-335 §II.23.1.5). A static field's offset is into the per-class static block,
    // not the instance, so a value static routes through il2cpp_field_static_*_value, never base+offset.
    private const int FIELD_ATTRIBUTE_STATIC = 0x10;

    // Find a field by name on `klass` OR any ancestor — the chain walk that reaches a field declared on a
    // base/obfuscated class the handle's static type doesn't name. IntPtr.Zero if no class in the chain has it.
    private static IntPtr FindField(IntPtr klass, string name)
    {
        for (IntPtr k = klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            IntPtr f = IL2CPP.il2cpp_class_get_field_from_name(k, name);
            if (f != IntPtr.Zero) return f;
        }
        return IntPtr.Zero;
    }

    private static bool FieldStatic(IntPtr f) => (IL2CPP.il2cpp_field_get_flags(f) & FIELD_ATTRIBUTE_STATIC) != 0;

    // Resolve a field on `o`'s RUNTIME class (il2cpp_object_get_class — the actual most-derived type, not the
    // proxy's static type), then walk to its declarer. Zero for a null handle or an absent field.
    private static IntPtr FieldOf(Il2CppObjectBase? o, string name)
        => o is null ? IntPtr.Zero : FindField(IL2CPP.il2cpp_object_get_class(o.Pointer), name);

    // The address of an INSTANCE field's storage: the object pointer + the field's class-relative offset.
    private static void* FieldAddr(Il2CppObjectBase o, IntPtr f)
        => (void*)(o.Pointer + (int)IL2CPP.il2cpp_field_get_offset(f));

    // ── value / enum / blittable-struct fields (instance OR static, auto-routed) ──────────────────────────

    /// <summary>Read a value/enum/blittable-struct field by name (instance or static); default(T) if absent.</summary>
    public static T GetValue<T>(Il2CppObjectBase? o, string field) where T : unmanaged
    {
        IntPtr f = FieldOf(o, field); if (f == IntPtr.Zero) return default;
        T v = default;
        if (FieldStatic(f)) IL2CPP.il2cpp_field_static_get_value(f, &v);
        else v = *(T*)FieldAddr(o!, f);
        return v;
    }

    /// <summary>Write a value/enum/blittable-struct field by name (instance or static); false if absent.</summary>
    public static bool SetValue<T>(Il2CppObjectBase? o, string field, T v) where T : unmanaged
    {
        IntPtr f = FieldOf(o, field); if (f == IntPtr.Zero) return false;
        if (FieldStatic(f)) IL2CPP.il2cpp_field_static_set_value(f, &v);
        else *(T*)FieldAddr(o!, f) = v;
        return true;
    }

    // ── Nullable<UT> value fields (interop's typed projection misreads these) ──────────────────────────────

    /// <summary>Read a Nullable&lt;UT&gt; value field by name (UT unmanaged); none if absent / no-value.</summary>
    /// <remarks>il2cpp boxes a Nullable field to the inner UT (or a null object), so we unbox that. GetValue&lt;UT?&gt;
    /// can't express it (Nullable&lt;UT&gt; isn't an unmanaged type), and the interop typed projection throws/misreads.</remarks>
    public static UT? GetNullable<UT>(Il2CppObjectBase? o, string field) where UT : unmanaged
    {
        var box = GetObject(o, field); if (box is null) return null;
        IntPtr data = IL2CPP.il2cpp_object_unbox(box.Pointer);
        return data == IntPtr.Zero ? null : *(UT*)data;
    }

    /// <summary>Write a Nullable&lt;UT&gt; value field by name (UT unmanaged) inline; false if absent or static.</summary>
    /// <remarks>Writes the inline { hasValue@0, value@vo } directly (il2cpp's Nullable layout matches .NET's) — the
    /// inverse of GetNullable's boxing read. No GC barrier (UT carries no refs). Instance fields only.</remarks>
    public static bool SetNullable<UT>(Il2CppObjectBase? o, string field, UT? v) where UT : unmanaged
    {
        IntPtr f = FieldOf(o, field); if (f == IntPtr.Zero || FieldStatic(f)) return false;
        byte* p = (byte*)FieldAddr(o!, f);
        int vo = Unsafe.SizeOf<UT?>() - sizeof(UT);   // value sits after the hasValue byte + its alignment padding
        if (v.HasValue) { p[0] = 1; *(UT*)(p + vo) = v.GetValueOrDefault(); } else { p[0] = 0; *(UT*)(p + vo) = default; }
        return true;
    }

    // ── reference / string fields (GC-correct via the runtime's own object store) ─────────────────────────

    /// <summary>Read a reference (or value-type-boxing) field by name as the agnostic base; Cast&lt;T&gt; at the call
    /// site. A value-type field is returned BOXED. Null-safe; handles static. Null if absent / null.</summary>
    public static Il2CppObjectBase? GetObject(Il2CppObjectBase? o, string field)
    {
        IntPtr f = FieldOf(o, field); if (f == IntPtr.Zero) return null;
        IntPtr p = IL2CPP.il2cpp_field_get_value_object(f, o!.Pointer);
        return p == IntPtr.Zero ? null : new Il2CppSystem.Object(p);
    }

    /// <summary>Write a reference field by name (honours the GC write barrier); false if absent or static.</summary>
    public static bool SetObject(Il2CppObjectBase? o, string field, Il2CppObjectBase? v)
    {
        IntPtr f = FieldOf(o, field); if (f == IntPtr.Zero || FieldStatic(f)) return false;
        IL2CPP.il2cpp_field_set_value_object(o!.Pointer, f, v is null ? IntPtr.Zero : v.Pointer);
        return true;
    }

    /// <summary>Read a string field by name (null if absent / null).</summary>
    public static string? GetString(Il2CppObjectBase? o, string field)
    {
        var r = GetObject(o, field);
        return r is null ? null : IL2CPP.Il2CppStringToManaged(r.Pointer);
    }

    /// <summary>Write a string field by name; false if absent or static.</summary>
    public static bool SetString(Il2CppObjectBase? o, string field, string? v)
    {
        IntPtr f = FieldOf(o, field); if (f == IntPtr.Zero || FieldStatic(f)) return false;
        IL2CPP.il2cpp_field_set_value_object(o!.Pointer, f, v is null ? IntPtr.Zero : IL2CPP.ManagedStringToIl2Cpp(v));
        return true;
    }

    // ── ref-bearing value-type fields (a struct carrying managed refs — SetValue<T> can't, it drops the ref) ──

    /// <summary>Write a value-type field by name from a BOXED value of the field's type, GC-aware (embedded
    /// references preserved). For il2cpp structs that carry managed refs (a string/array), which SetValue&lt;T&gt;
    /// (T:unmanaged) can't. Pass a boxed value of the field's EXACT type. False if absent / static / null.
    /// il2cpp_field_set_value is the runtime's own ref-preserving, write-barriered value store. Instance only.</summary>
    public static bool SetStruct(Il2CppObjectBase? o, string field, Il2CppObjectBase? value)
    {
        if (o is null || value is null) return false;
        IntPtr f = FieldOf(o, field); if (f == IntPtr.Zero || FieldStatic(f)) return false;
        IntPtr src = IL2CPP.il2cpp_object_unbox(value.Pointer); if (src == IntPtr.Zero) return false;
        IL2CPP.il2cpp_field_set_value(o.Pointer, f, (void*)src);
        GC.KeepAlive(value);
        return true;
    }

    /// <summary>Write a Nullable&lt;T&gt; field by name where T is an il2cpp value type that may carry managed refs.
    /// Builds the Nullable box via il2cpp metadata so the nested refs SURVIVE (managed `new Nullable&lt;T&gt;(struct)`
    /// drops them), then GC-aware-copies it into the field. Pass a BOXED inner value, or null to clear. The
    /// unmanaged-T case is SetNullable&lt;UT&gt;; this is its ref-bearing counterpart. Instance fields only.</summary>
    public static bool SetNullableStruct(Il2CppObjectBase? o, string field, Il2CppObjectBase? value)
    {
        if (o is null) return false;
        IntPtr f = FieldOf(o, field); if (f == IntPtr.Zero || FieldStatic(f)) return false;
        IntPtr nkt = IL2CPP.il2cpp_class_from_il2cpp_type(IL2CPP.il2cpp_field_get_type(f));
        if (nkt == IntPtr.Zero) return false;
        IntPtr box = IL2CPP.il2cpp_object_new(nkt);   // boxed Nullable<T>, zero-init (hasValue = 0)
        if (box == IntPtr.Zero) return false;
        if (value is not null)
        {
            IntPtr vf = IL2CPP.il2cpp_class_get_field_from_name(nkt, "value");
            IntPtr hf = IL2CPP.il2cpp_class_get_field_from_name(nkt, "hasValue");
            if (vf == IntPtr.Zero || hf == IntPtr.Zero) return false;
            IntPtr src = IL2CPP.il2cpp_object_unbox(value.Pointer); if (src == IntPtr.Zero) return false;
            IL2CPP.il2cpp_field_set_value(box, vf, (void*)src);          // GC-aware copy of the inner value (refs preserved)
            byte one = 1; IL2CPP.il2cpp_field_set_value(box, hf, &one);  // hasValue = 1
        }
        IL2CPP.il2cpp_field_set_value(o.Pointer, f, (void*)IL2CPP.il2cpp_object_unbox(box));   // GC-aware copy Nullable<T> -> field
        GC.KeepAlive(value);
        return true;
    }

    // ── typed shortcuts (il2cpp bool is one byte) ─────────────────────────────────────────────────────────
    public static int    GetInt  (Il2CppObjectBase? o, string field) => GetValue<int>(o, field);
    public static long   GetLong (Il2CppObjectBase? o, string field) => GetValue<long>(o, field);
    public static float  GetFloat(Il2CppObjectBase? o, string field) => GetValue<float>(o, field);
    public static bool   GetBool (Il2CppObjectBase? o, string field) => GetValue<byte>(o, field) != 0;
    public static bool   SetInt  (Il2CppObjectBase? o, string field, int v)   => SetValue<int>(o, field, v);
    public static bool   SetLong (Il2CppObjectBase? o, string field, long v)  => SetValue<long>(o, field, v);
    public static bool   SetFloat(Il2CppObjectBase? o, string field, float v) => SetValue<float>(o, field, v);
    public static bool   SetBool (Il2CppObjectBase? o, string field, bool v)  => SetValue<byte>(o, field, (byte)(v ? 1 : 0));
}
