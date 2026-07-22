namespace Inutil.Schema;

// A reusable IIl2CppAnchors backed by a per-ConvKind table — the seam fills it once with the open il2cpp element
// name each container kind must present at the game boundary (verified against real proxies, NOT guessed here).
// ValueTuple is the one arity-dependent kind: the game spells `ValueTuple`2`/`ValueTuple`3`/…, so its entry is
// the arity-less prefix ("…ValueTuple`") and the arity is appended from the spelled managed type. Every other
// modeled kind is a single fixed anchor.
public sealed class DictionaryAnchors : IIl2CppAnchors
{
    readonly IReadOnlyDictionary<ConvKind, string> _map;

    public DictionaryAnchors(IReadOnlyDictionary<ConvKind, string> map) => _map = map;

    public string? Anchor(ConvKind kind, Type managed)
    {
        if (!_map.TryGetValue(kind, out string? baseName)) return null;
        // ValueTuple`N: the game element's anchor carries the arity, so append it from the spelled tuple.
        if (kind == ConvKind.Tuple && managed.IsGenericType)
            return baseName + managed.GetGenericArguments().Length;
        return baseName;
    }
}
