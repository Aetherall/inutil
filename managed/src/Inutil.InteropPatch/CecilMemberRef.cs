using System.Linq;
using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The metadata pillar's projection of a Mono.Cecil member + its custom attributes onto the pure IMemberRef /
// IAttributeRef the WIRE registry classifies (docs/contribution/architecture/16-metadata.md, Generalization A) — the
// member-side mirror of CecilTypeRef, keeping Cecil out of the pure Inutil.Schema module (only this adapter references
// both). Reads a Cpp2IL "DummyDll" stub — Mono.Cecil 0.11.5 (already this seam's reader) loads Cpp2IL's output and
// surfaces the authored attribute rows Il2CppInterop later strips.
public sealed class CecilMemberRef : IMemberRef
{
    readonly IMemberDefinition _m;
    readonly TypeReference _memberType;
    readonly MemberSort _sort;

    CecilMemberRef(IMemberDefinition m, TypeReference memberType, MemberSort sort)
    {
        _m = m;
        _memberType = memberType;
        _sort = sort;
    }

    public static IMemberRef OfField(FieldDefinition f)    => new CecilMemberRef(f, f.FieldType, MemberSort.Field);
    public static IMemberRef OfProperty(PropertyDefinition p) => new CecilMemberRef(p, p.PropertyType, MemberSort.Property);
    // A type projects onto itself for MemberType; DeclaringType is its enclosing type (nested) or itself (top-level),
    // so DeclaringType.FullName is always well-defined (the classifier's warning path reads it).
    public static IMemberRef OfType(TypeDefinition t)      => new CecilMemberRef(t, t, MemberSort.Type);

    public string Name => _m.Name;
    public ITypeRef DeclaringType => CecilTypeRef.Of(_m.DeclaringType ?? (TypeReference)_m);
    public ITypeRef MemberType => CecilTypeRef.Of(_memberType);
    public MemberSort Sort => _sort;
    public IReadOnlyList<IAttributeRef> Attributes =>
        _m.CustomAttributes.Select(CecilAttributeRef.Of).ToArray();
}

// The projection of one Mono.Cecil CustomAttribute onto the pure IAttributeRef: AttributeType.FullName is the match
// key; ConstructorArguments carry the positional blob values; Properties (and Fields) carry the named ones.
public sealed class CecilAttributeRef : IAttributeRef
{
    readonly CustomAttribute _ca;

    CecilAttributeRef(CustomAttribute ca) => _ca = ca;
    public static IAttributeRef Of(CustomAttribute ca) => new CecilAttributeRef(ca);

    public string AttributeTypeFullName => _ca.AttributeType.FullName;

    public IReadOnlyList<object?> ConstructorArgs
    {
        get
        {
            try
            {
                return _ca.HasConstructorArguments
                    ? _ca.ConstructorArguments.Select(a => Decode(a.Value)).ToArray()
                    : Array.Empty<object?>();
            }
            catch (Exception ex) when (IsStubGap(ex)) { return Array.Empty<object?>(); }
        }
    }

    // ECMA-335 named args are either property-setters OR field-setters; both are "named". The extractors read a few
    // by name (e.g. JsonProperty's PropertyName), so surface both flattened.
    public IReadOnlyList<(string Name, object? Value)> NamedArgs
    {
        get
        {
            var named = new List<(string, object?)>();
            try
            {
                foreach (CustomAttributeNamedArgument p in _ca.Properties) named.Add((p.Name, Decode(p.Argument.Value)));
                foreach (CustomAttributeNamedArgument f in _ca.Fields) named.Add((f.Name, Decode(f.Argument.Value)));
            }
            catch (Exception ex) when (IsStubGap(ex)) { /* partial read kept; see IsStubGap */ }
            return named;
        }
    }

    // Reading an attribute blob makes Cecil Resolve() the argument types (an enum arg decodes via CheckedResolve of
    // its declaring TypeReference). ExtractFromStub puts the whole stub dir on the resolver path, so this resolves
    // for every type Cpp2IL emitted. But a GENUINE per-game stub gap — an enum type Cpp2IL omitted or emitted
    // unresolvably — would still throw here and, uncaught, tear down the ENTIRE extraction over one member.
    // Quarantine that Cecil failure to this adapter (Inutil.Schema must stay Cecil-free) and degrade to "no readable
    // args" so the recognizer yields no fact; WireRegistry.Classify then surfaces a fail-loud "matched but malformed"
    // WARNING for that member — never a silent skip, never a whole-run abort. Narrow on purpose: only Cecil's
    // resolution exceptions, so an unrelated bug still fails loud.
    static bool IsStubGap(Exception ex) => ex is ResolutionException or AssemblyResolutionException;

    // Project a Cecil-decoded argument to the pure model's object?, keeping Cecil out of the extractors:
    //   - a typeof(...) arg is a TypeReference -> the type's full-name STRING (so JsonConverter(typeof(X)) reads as
    //     "X" without the extractor touching Cecil);
    //   - an array arg is a CustomAttributeArgument[] -> object?[] of decoded elements (shared with synthetic fakes so
    //     a byte[] Nullable flag reads the same from either source);
    //   - a boxed typed value nested in an object-typed arg unwraps recursively;
    //   - everything else (primitives, strings, boxed enum integers) passes through.
    static object? Decode(object? value) => value switch
    {
        TypeReference tr             => tr.FullName,
        CustomAttributeArgument inner => Decode(inner.Value),
        CustomAttributeArgument[] arr => arr.Select(a => Decode(a.Value)).ToArray(),
        _                            => value,
    };
}
