using System.Linq;
using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The IL-rewrite seam's projection of a Mono.Cecil TypeReference onto the pure ITypeRef the registry classifies.
// It lets ContainerFlip and TaskFamily reach family knowledge through CorrespondenceRegistry.Classify instead of
// matching Il2CppSystem.* anchor names with inline constants — so the family map lives once, in Families.cs, and
// Cecil never leaks into the pure Inutil.Schema module (only this adapter references both).
public sealed class CecilTypeRef : ITypeRef
{
    readonly TypeReference _tr;

    CecilTypeRef(TypeReference tr) => _tr = tr;
    public static ITypeRef Of(TypeReference tr) => new CecilTypeRef(tr);

    public string FullName => _tr.FullName;
    public bool IsGenericInstance => _tr is GenericInstanceType;
    public string ElementFullName => _tr is GenericInstanceType g ? g.ElementType.FullName : _tr.FullName;
    public IReadOnlyList<ITypeRef> GenericArguments =>
        _tr is GenericInstanceType g ? g.GenericArguments.Select(Of).ToArray() : Array.Empty<ITypeRef>();
    public bool IsGenericParameter => _tr.IsGenericParameter;
}
