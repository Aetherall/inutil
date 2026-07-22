using Inutil.Schema;

namespace Inutil.Schema.Tests;

// Synthetic IMemberRef / IAttributeRef for the wire classifier — the member-side analogue of ClassifyFakes'
// FakeTypeRef. Proves WireRegistry.Classify with NO game and NO Cpp2IL, exactly as FakeTypeRef proves
// CorrespondenceRegistry.Classify (docs/contribution/architecture/16-metadata.md: "tested against synthetic IMemberRef/IAttributeRef").
// The real projection (CecilMemberRef over a Cpp2IL stub) plugs into the SAME IMemberRef these fakes satisfy.
sealed class FakeAttributeRef : IAttributeRef
{
    public string AttributeTypeFullName { get; init; } = "";
    public IReadOnlyList<object?> ConstructorArgs { get; init; } = Array.Empty<object?>();
    public IReadOnlyList<(string Name, object? Value)> NamedArgs { get; init; } = Array.Empty<(string, object?)>();

    public static FakeAttributeRef Of(string fullName, params object?[] ctorArgs) =>
        new() { AttributeTypeFullName = fullName, ConstructorArgs = ctorArgs };

    public FakeAttributeRef WithNamed(params (string Name, object? Value)[] named) =>
        new() { AttributeTypeFullName = AttributeTypeFullName, ConstructorArgs = ConstructorArgs, NamedArgs = named };
}

sealed class FakeMemberRef : IMemberRef
{
    public string Name { get; init; } = "";
    public ITypeRef DeclaringType { get; init; } = FakeTypeRef.Simple("Game.Dto");
    public ITypeRef MemberType { get; init; } = FakeTypeRef.Simple("System.String");
    public MemberSort Sort { get; init; } = MemberSort.Field;
    public IReadOnlyList<IAttributeRef> Attributes { get; init; } = Array.Empty<IAttributeRef>();

    public static FakeMemberRef Field(string name, params IAttributeRef[] attrs) =>
        new() { Name = name, Sort = MemberSort.Field, Attributes = attrs };
}
