// Naming — the Cecil display-name + type-walk helpers the reverse index needs, ported verbatim from
// runtime's codegen (InteropNaming.CleanTypeName + Program.AllTypes/SimpleName) MINUS the emit-only paths
// (RenderType/Scope/RenderGenericNested), which depended on the surface generator. CleanTypeName is pure
// Cecil — no SurfaceHost state — so the inutil reverse-index reads identically to the old surface-query.

using Mono.Cecil;

namespace Inutil.Check;

internal static class Naming
{
    // TWO renderings, for two consumers that want opposite things. Keeping them apart is the whole point:
    //
    //   CleanTypeName  — the INDEX KEY. il2cpp-shaped, matching il2cpp_type_get_name, so a reference files
    //                    under the same key however Il2CppInterop happened to spell it. Lossy ON PURPOSE.
    //   AuthorTypeName — what a C# mod must literally WRITE. Lossless: no BCL remap, no wrapper collapse.
    //
    // They were one function until a real consumer was misled by it. CleanTypeName renders an UNFLIPPED
    // `Il2CppSystem.Nullable<MongoID>` as `System.Nullable<MongoID>` and an UNFLIPPED `Il2CppArrayBase<T>`
    // as `T[]` — i.e. identically to their flipped forms. So the one question the `methods`/`query` output
    // is consulted for ("can I assign this naturally?") was answered wrong, in the confident direction, for
    // exactly the members where the answer is no. Author-facing output must use AuthorTypeName.
    public static string AuthorTypeName(TypeReference t)
    {
        if (t.IsArray) return AuthorTypeName(((ArrayType)t).ElementType) + "[]";
        if (t is GenericInstanceType gi)
            return StripArity(gi.ElementType.FullName.Replace('/', '.')) + "<" +
                   string.Join(",", gi.GenericArguments.Select(AuthorTypeName)) + ">";
        return t.FullName.Replace('/', '.');
    }

    // Is this type still wearing an il2cpp spelling a mod cannot assign naturally?
    //
    // Phrased as "do the two renderings DISAGREE", not as a list of known wrappers. CleanTypeName is exactly
    // the set of il2cpp-isms we paper over, so any spelling it collapses is one an author must be warned
    // about — including a wrapper family added to it later, with no edit here.
    public static bool IsRawIl2Cpp(TypeReference? t)
        => t is not null && !string.Equals(AuthorTypeName(t), CleanTypeName(t), StringComparison.Ordinal);

    // Display name for a type reference: arrays as "T[]", closed generics as "Outer<A,B>", Il2CppSystem.*
    // mapped back to System.*. NOT for emitted C# and NOT author-facing — purely the index key.
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
