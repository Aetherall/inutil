// Naming — the Cecil display-name + type-walk helpers the reverse index needs, ported verbatim from
// runtime's codegen (InteropNaming.CleanTypeName + Program.AllTypes/SimpleName) MINUS the emit-only paths
// (RenderType/Scope/RenderGenericNested), which depended on the surface generator. CleanTypeName is pure
// Cecil — no SurfaceHost state — so the inutil reverse-index reads identically to the old surface-query.

using Mono.Cecil;

namespace Inutil.Check;

internal static class Naming
{
    // Display name for a type reference: arrays as "T[]", closed generics as "Outer<A,B>", Il2CppSystem.*
    // mapped back to System.*. NOT for emitted C# — purely the mod-facing display key the index files under.
    public static string CleanTypeName(TypeReference t)
    {
        if (t.IsArray) return CleanTypeName(((ArrayType)t).ElementType) + "[]";
        if (t is GenericInstanceType cgi)
        {
            // Il2CppInterop surfaces an il2cpp array as Il2Cpp{Struct,Reference}Array<T> — a closed generic,
            // not a Cecil array. Render as "T[]" so the name matches il2cpp_type_get_name output.
            if (IsIl2CppArrayWrapper(cgi.ElementType))
                return CleanTypeName(cgi.GenericArguments[0]) + "[]";
            return MapBcl(StripArity(cgi.ElementType.FullName.Replace('/', '.'))) + "<" +
                   string.Join(",", cgi.GenericArguments.Select(CleanTypeName)) + ">";
        }
        if (t.FullName == "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray") return "System.String[]";
        string n = t.FullName.Replace('/', '.');
        return MapBcl(n);
    }

    private static bool IsIl2CppArrayWrapper(TypeReference elem)
        => elem.Namespace == "Il2CppInterop.Runtime.InteropTypes.Arrays"
           && (elem.Name == "Il2CppStructArray`1" || elem.Name == "Il2CppReferenceArray`1"
               || elem.Name == "Il2CppArrayBase`1");

    // Il2CppInterop prefixes BCL types as Il2CppSystem.*; strip for the display name.
    private static string MapBcl(string n)
        => n.StartsWith("Il2CppSystem.") ? "System." + n.Substring("Il2CppSystem.".Length) : n;

    private static string StripArity(string s)
    {
        int i;
        while ((i = s.IndexOf('`')) >= 0)
        {
            int j = i + 1;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            s = s.Substring(0, i) + s.Substring(j);
        }
        return s;
    }

    // Every type in a module, including nested (recursively).
    public static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition mod)
    {
        foreach (var t in mod.Types)
        {
            yield return t;
            foreach (var n in NestedRec(t)) yield return n;
        }
    }

    private static IEnumerable<TypeDefinition> NestedRec(TypeDefinition t)
    {
        foreach (var n in t.NestedTypes)
        {
            yield return n;
            foreach (var nn in NestedRec(n)) yield return nn;
        }
    }

    public static string SimpleName(string fqn)
    {
        int i = fqn.LastIndexOf('.');
        return i < 0 ? fqn : fqn.Substring(i + 1);
    }
}
