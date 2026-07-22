// Inutil.Introspect — human-readable il2cpp object/type discovery: "what's in here?" in one call. The DISCOVERY
// tier of the raw by-name surface (docs/contribution/architecture/17-reach-faces.md), complement of:
//   - Inutil.Fields    — READ/WRITE a field's value by name (typed).
//   - Inutil.Invoke    — CALL a method by name.
//   - Inutil.Probe     — is a SUSPECT pointer a live object at all (fault-safe validation)?
// Introspect answers the question that precedes all three: given a live handle, what fields / methods / properties /
// type shape does it have? A DEVELOPMENT / REPL act, and what makes the erased-handle / unknown-type cases workable:
// you can't write `obj.Field` for a type you can't name, but you CAN Dump it to learn the names, then reach by name.
//
// It walks a live proxy's RUNTIME class via the il2cpp C ABI. Names are read through Probe's guarded UTF-8 helper so
// a partially-torn object degrades to "" / "<null>" rather than crashing. Game-AGNOSTIC. Call on the game thread.
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;   // Il2CppObjectBase (.Pointer)

namespace Inutil;

public static class Introspect
{
    private const int FIELD_ATTRIBUTE_STATIC = 0x10;   // ECMA-335 §II.23.1.5 (mirrors Fields.cs)
    private const int MaxFields = 400, MaxMethods = 600, MaxProps = 400, MaxIfaces = 64;   // runaway caps

    private static bool IsStatic(nint f) => (IL2CPP.il2cpp_field_get_flags(f) & FIELD_ATTRIBUTE_STATIC) != 0;

    // Full declared-type name (with generic args / array brackets), "" if null. il2cpp_type_get_name allocates a
    // C-heap string we deliberately DON'T free (freeing risks a crash if a build ever returns non-heap; the per-call
    // leak is negligible for a dev/introspection tool). Read guarded, like Probe's class names.
    private static string TypeName(nint type)
        => type == 0 ? "" : Probe.SafeUtf8(IL2CPP.il2cpp_type_get_name(type));

    // Class name + every field: its declared type = the CURRENT value's runtime type. The fastest "what is this
    // really?" — a reference field showing a subclass name is the tell you're after.
    public static string Dump(Il2CppObjectBase? o)
    {
        if (o is null) return "<null>";
        nint klass = IL2CPP.il2cpp_object_get_class(o.Pointer);
        var sb = new StringBuilder(Probe.ClassName(klass)).Append('\n');
        for (nint k = klass; k != 0; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            nint it = 0, f; int guard = 0;
            while ((f = IL2CPP.il2cpp_class_get_fields(k, ref it)) != 0 && guard++ < MaxFields)
            {
                nint valp = IL2CPP.il2cpp_field_get_value_object(f, o.Pointer);
                sb.Append("  ").Append(Utf8(IL2CPP.il2cpp_field_get_name(f)))
                  .Append(IsStatic(f) ? " (static)" : "").Append(" : ").Append(TypeName(IL2CPP.il2cpp_field_get_type(f)))
                  .Append(" = ").Append(valp == 0 ? "null" : Probe.ClassName(IL2CPP.il2cpp_object_get_class(valp)))
                  .Append('\n');
            }
        }
        return sb.ToString();
    }

    // Hop one reference field (null-safe) then Dump it: Dump(o,"Foo") == Dump(field value). "<null>" if the field is
    // absent/null. Saves the read + null-check + dump idiom when you know the root.
    public static string Dump(Il2CppObjectBase? o, string field) => Dump(Fields.GetObject(o, field));

    // Every field across the inheritance chain: "Class.field [static] : type @0xoffset". Named FieldList (not Fields)
    // so it doesn't shadow the Inutil.Fields type this same file references.
    public static string FieldList(Il2CppObjectBase? o)
    {
        if (o is null) return "<null>";
        var sb = new StringBuilder();
        for (nint k = IL2CPP.il2cpp_object_get_class(o.Pointer); k != 0; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            string cn = Utf8(IL2CPP.il2cpp_class_get_name(k));
            nint it = 0, f; int guard = 0;
            while ((f = IL2CPP.il2cpp_class_get_fields(k, ref it)) != 0 && guard++ < MaxFields)
            {
                sb.Append(cn).Append('.').Append(Utf8(IL2CPP.il2cpp_field_get_name(f)))
                  .Append(IsStatic(f) ? " static" : "").Append(" : ").Append(TypeName(IL2CPP.il2cpp_field_get_type(f)))
                  .Append(" @0x").Append(((int)IL2CPP.il2cpp_field_get_offset(f)).ToString("x")).Append('\n');
            }
        }
        return sb.ToString();
    }

    // Every method across the chain: "Class.method(paramType,...) : returnType".
    public static string Methods(Il2CppObjectBase? o)
    {
        if (o is null) return "<null>";
        var sb = new StringBuilder();
        for (nint k = IL2CPP.il2cpp_object_get_class(o.Pointer); k != 0; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            string cn = Utf8(IL2CPP.il2cpp_class_get_name(k));
            nint it = 0, m; int guard = 0;
            while ((m = IL2CPP.il2cpp_class_get_methods(k, ref it)) != 0 && guard++ < MaxMethods)
            {
                int pc = (int)IL2CPP.il2cpp_method_get_param_count(m);
                var ps = new string[pc];
                for (uint i = 0; i < pc; i++) ps[i] = TypeName(IL2CPP.il2cpp_method_get_param(m, i));
                sb.Append(cn).Append('.').Append(Utf8(IL2CPP.il2cpp_method_get_name(m)))
                  .Append('(').Append(string.Join(", ", ps)).Append(") : ")
                  .Append(TypeName(IL2CPP.il2cpp_method_get_return_type(m))).Append('\n');
            }
        }
        return sb.ToString();
    }

    // Declared properties across the chain, with accessor presence: "Class.Prop {get;set;}".
    public static string Props(Il2CppObjectBase? o)
    {
        if (o is null) return "<null>";
        var sb = new StringBuilder();
        for (nint k = IL2CPP.il2cpp_object_get_class(o.Pointer); k != 0; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            string cn = Utf8(IL2CPP.il2cpp_class_get_name(k));
            nint it = 0, p; int guard = 0;
            while ((p = IL2CPP.il2cpp_class_get_properties(k, ref it)) != 0 && guard++ < MaxProps)
            {
                bool g = IL2CPP.il2cpp_property_get_get_method(p) != System.IntPtr.Zero;
                bool s = IL2CPP.il2cpp_property_get_set_method(p) != System.IntPtr.Zero;
                sb.Append(cn).Append('.').Append(Utf8(IL2CPP.il2cpp_property_get_name(p)))
                  .Append(" {").Append(g ? "get;" : "").Append(s ? "set;" : "").Append("}\n");
            }
        }
        return sb.ToString();
    }

    // Type shape: kind (enum + base / struct / interface), abstract/generic, base chain, interfaces.
    public static string TypeInfo(Il2CppObjectBase? o)
    {
        if (o is null) return "<null>";
        nint k = IL2CPP.il2cpp_object_get_class(o.Pointer);
        var sb = new StringBuilder(Probe.ClassName(k));
        if (IL2CPP.il2cpp_class_is_enum(k)) sb.Append(" enum:").Append(TypeName(IL2CPP.il2cpp_class_enum_basetype(k)));
        else if (IL2CPP.il2cpp_class_is_valuetype(k)) sb.Append(" struct");
        else if (IL2CPP.il2cpp_class_is_interface(k)) sb.Append(" interface");
        if (IL2CPP.il2cpp_class_is_abstract(k)) sb.Append(" abstract");
        if (IL2CPP.il2cpp_class_is_generic(k)) sb.Append(" generic");

        var bases = new System.Collections.Generic.List<string>();
        for (nint b = IL2CPP.il2cpp_class_get_parent(k); b != 0; b = IL2CPP.il2cpp_class_get_parent(b))
            bases.Add(Probe.ClassName(b));
        if (bases.Count > 0) sb.Append("\n  : ").Append(string.Join(" -> ", bases));

        nint it = 0, ip; var ifs = new System.Collections.Generic.List<string>();
        while ((ip = IL2CPP.il2cpp_class_get_interfaces(k, ref it)) != 0 && ifs.Count < MaxIfaces)
            ifs.Add(Probe.ClassName(ip));
        if (ifs.Count > 0) sb.Append("\n  impl: ").Append(string.Join(", ", ifs));
        return sb.ToString();
    }

    private static string Utf8(nint p) => p == 0 ? "" : (System.Runtime.InteropServices.Marshal.PtrToStringUTF8(p) ?? "");
}
