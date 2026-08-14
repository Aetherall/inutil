namespace Inutil.Schema;

// The PURE view of ONE member (and its authored attributes) the WIRE classifier needs — the member-side analogue
// of ITypeRef (TypeRef.cs). Generalization A of docs/contribution/architecture/16-metadata.md: the spine classifies a *type shape*;
// the pillar adds *member -> authored facts* (wire name / converter / persisted / nullability), which needs a
// view of a member + its attribute blobs that ITypeRef/ISlotType/ISlotMethod do not carry.
//
// Same discipline as ITypeRef: NO Cecil/reflection dependency here. The offline pillar projects Cpp2IL-stub Cecil
// members onto these (CecilMemberRef/CecilAttributeRef); unit tests project synthetic fakes — so
// WireRegistry.Classify is testable with no game.
public enum MemberSort { Field, Property, Type }

public interface IMemberRef
{
    // The C# name as spelled in the stub — may be obfuscated/mangled (`_f7`); recovering what it MEANS is the
    // whole point of reading the attributes, since Il2CppInterop strips them off the shipped proxies.
    string Name { get; }

    ITypeRef DeclaringType { get; }

    // The field/property type (undefined for MemberSort.Type). Reuses the EXISTING pure type view — the member
    // aspect composes with the type aspect rather than re-deriving one.
    ITypeRef MemberType { get; }

    MemberSort Sort { get; }

    // The authored attributes carried on this member in the stub — the residue Il2CppInterop strips and the live
    // runtime cannot conveniently reach. WireRegistry.Classify walks these.
    IReadOnlyList<IAttributeRef> Attributes { get; }
}

public interface IAttributeRef
{
    // The match key WireRegistry classifies on — the attribute's full type name, the analogue of
    // ITypeRef.ElementFullName as CorrespondenceRegistry's anchor. The literal anchor strings are single-site-
    // spelled in WireFamilies.cs; this comment names the concept, not the string, so the drift guard stays strict.
    string AttributeTypeFullName { get; }

    // The serialized constructor arguments (ECMA-335 §II.23.3), already decoded. A typeof(...) arg (e.g.
    // JsonConverter(typeof(StringEnumConverter))) is projected to the type's full name STRING at the pure-view
    // boundary, keeping this model free of Cecil/reflection.
    IReadOnlyList<object?> ConstructorArgs { get; }

    // The named (property/field) arguments, decoded. Kept for extractors that read a named value rather than a
    // positional one (e.g. [JsonProperty(PropertyName = "wire")]).
    IReadOnlyList<(string Name, object? Value)> NamedArgs { get; }
}
