using Inutil.Schema;

namespace Inutil.Schema.Tests;

// Synthetic ITypeRef for classifier tests — the type-side analogue of the planner's FakeType/FakeMethod.
sealed class FakeTypeRef : ITypeRef
{
    public string FullName { get; init; } = "";
    public bool IsGenericInstance { get; init; }
    public string ElementFullName { get; init; } = "";
    public IReadOnlyList<ITypeRef> GenericArguments { get; init; } = Array.Empty<ITypeRef>();
    public bool IsGenericParameter { get; init; }

    public static FakeTypeRef Simple(string name) => new() { FullName = name, ElementFullName = name };
    public static FakeTypeRef Param(string name = "T") => new() { FullName = name, ElementFullName = name, IsGenericParameter = true };
    public static FakeTypeRef Generic(string element, params ITypeRef[] args) => new()
    {
        FullName = element + "<" + string.Join(",", args.Select(a => a.FullName)) + ">",
        IsGenericInstance = true,
        ElementFullName = element,
        GenericArguments = args,
    };
}

// Stand-in family bridges — classification only (CanBind + Shape + Direction), one per BridgeShape. CanBind is
// self-contained (checks the anchor name AND rejects an open argument), modelling the real "never hard-bind,
// always probe" contract.
sealed class FakeTaskBridge : IBridge
{
    public BridgeShape Shape => BridgeShape.ReferenceBespoke;
    public Directionality Direction => Directionality.Il2CppToBcl;
    public bool CanBind(ITypeRef t)
        => t.IsGenericInstance && t.ElementFullName == "Il2CppSystem.Threading.Tasks.Task`1"
           && !t.GenericArguments[0].IsGenericParameter;
}

sealed class FakeNullableBridge : IBridge
{
    public BridgeShape Shape => BridgeShape.ValueLayout;
    public Directionality Direction => Directionality.Both;
    public bool CanBind(ITypeRef t)
        => t.IsGenericInstance && t.ElementFullName == "Il2CppSystem.Nullable`1"
           && !t.GenericArguments[0].IsGenericParameter;
}

sealed class FakeEnumerableBridge : IBridge
{
    public BridgeShape Shape => BridgeShape.ContainerAdapter;
    public Directionality Direction => Directionality.Il2CppToBcl;
    public bool CanBind(ITypeRef t)
        => t.IsGenericInstance && t.ElementFullName == "Il2CppSystem.Collections.Generic.IEnumerable`1"
           && !t.GenericArguments[0].IsGenericParameter;
}
