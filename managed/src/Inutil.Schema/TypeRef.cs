namespace Inutil.Schema;

// The PURE view of ONE type the classifier needs — the type-side analogue of ISlotType/ISlotMethod. The IL seam
// projects a Mono.Cecil TypeReference onto this; the runtime seam a System.Type; tests synthetic instances. So
// Classify (CorrespondenceRegistry) never touches Cecil or reflection, and the "what family is this, can we bind
// it" logic is testable against synthetic shapes.
public interface ITypeRef
{
    // Fully-qualified name of the type as spelled, e.g. "Il2CppSystem.Threading.Tasks.Task`1<System.Int32>"
    // for a closed instance, or "Il2CppSystem.Threading.Tasks.Task" for a plain type.
    string FullName { get; }

    // A closed or partly-closed generic instantiation (Task`1<X>), as opposed to a plain type or open element.
    bool IsGenericInstance { get; }

    // The OPEN element of a generic instance ("Il2CppSystem.Threading.Tasks.Task`1"); equals FullName when this
    // is not a generic instance. This is what the registry matches a family's anchor name against.
    string ElementFullName { get; }

    // The instantiation's type arguments (empty when not a generic instance). A family's CanBind probes these —
    // e.g. a value-layout family rejects a ref-bearing argument; every family rejects an open parameter.
    IReadOnlyList<ITypeRef> GenericArguments { get; }

    // An open generic parameter (T / !!0). The "never hard-bind, always probe" case: a family's anchor name can
    // match while the instantiation is still open, so CanBind must reject it and Classify returns null (the
    // slot-level open-parameter handling is a separate concern).
    bool IsGenericParameter { get; }
}
