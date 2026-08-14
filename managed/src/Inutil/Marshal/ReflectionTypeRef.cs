using Inutil.Schema;

namespace Inutil.Marshal;

// The runtime seam's projection of a System.Type (a game il2cpp proxy type) onto the pure ITypeRef the
// correspondence validator walks (C2). It lets the runtime boundary check the game's ACTUAL il2cpp type
// against the hook-SPELLED Conv tree, node-by-node, before converting — the recursive form of CanBind. The
// runtime twin of the IL seam's CecilTypeRef; reflection never enters the pure Inutil.Schema module.
public sealed class ReflectionTypeRef : ITypeRef
{
    readonly Type _t;

    ReflectionTypeRef(Type t) => _t = t;
    public static ITypeRef Of(Type t) => new ReflectionTypeRef(t);

    public string FullName => _t.FullName ?? _t.Name;
    public bool IsGenericInstance => _t.IsGenericType && !_t.IsGenericTypeDefinition;
    public string ElementFullName => IsGenericInstance ? _t.GetGenericTypeDefinition().FullName ?? _t.GetGenericTypeDefinition().Name : FullName;
    public IReadOnlyList<ITypeRef> GenericArguments =>
        IsGenericInstance ? _t.GetGenericArguments().Select(Of).ToArray() : Array.Empty<ITypeRef>();
    public bool IsGenericParameter => _t.IsGenericParameter;
}

// The runtime seam's IIl2CppAnchors (C2): the expected il2cpp anchor a container node presents at the game
// boundary, resolved from the ONE registry (never a hard-coded name — C4). ByConvKind(kind, arity) returns the
// canonical il2cpp name of the spelled family (List`1, Dictionary`2, Nullable`1, ValueTuple`N, Task[`1]); Array
// and the sequence read-spellings have no write-target row, so they resolve to null and stay unvalidatable —
// identity pass-through, exactly the honest limit CorrespondenceValidator already documents for a leaf.
public sealed class RegistryAnchors : IIl2CppAnchors
{
    readonly CorrespondenceRegistry _families;

    public RegistryAnchors(CorrespondenceRegistry families) => _families = families;

    public string? Anchor(ConvKind kind, Type managed)
    {
        int arity = managed.IsGenericType ? managed.GetGenericArguments().Length : 0;
        return _families.ByConvKind(kind, arity)?.Il2CppFullName;
    }
}
