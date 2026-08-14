namespace Inutil.Schema;

// The PURE view of the proxy type graph the virtual-slot planner needs — nothing more. The IL-rewrite seam
// projects Mono.Cecil (TypeDefinition/MethodDefinition) onto this; the runtime seam projects System.Reflection;
// unit tests project synthetic fakes. Everything the planner reasons about (slot rooting, the flip gate, body
// rewritability) is expressed here, so the rules are testable against synthetic shapes before any real proxy.

// A type in the proxy graph, module-scoped.
public interface ISlotType
{
    string FullName { get; }

    // Opaque, equatable module identity. The planner never inspects this beyond IsFrameworkModule — it exists so
    // an adapter can carry the real module handle for richer diagnostics.
    object ModuleId { get; }

    // A framework proxy module (Il2Cppmscorlib, UnityEngine.*, …) whose virtuals inutil never rewrites. A slot
    // rooted here is external — we can't flip the base to match, so the whole slot must stay wrapper-typed.
    bool IsFrameworkModule { get; }

    // The RESOLVED base type, or null (no base / unresolvable). The planner walks this chain to find a slot's true
    // root — including past intermediate bases that inherit a virtual WITHOUT redeclaring it (EFT's
    // LeaveMapLayerAssault : LeaveMapLayer : … : BaseLogicLayer, where only BaseLogicLayer declares the slot).
    // Stopping at the first non-declaring base mis-roots the override to itself and half-flips.
    ISlotType? BaseType { get; }

    // The VIRTUAL methods declared on THIS type (used to detect which ancestor (re)declares a slot). Non-virtual
    // methods are irrelevant to slot rooting and need not appear.
    IReadOnlyList<ISlotMethod> VirtualMethods { get; }
}

// A virtual method the planner may flip. Object identity matters: the base declaration's ISlotMethod instance
// is the group key, so every override that resolves to it must return the SAME instance from the walk (an
// adapter backed by Cecil/reflection gets this for free — Resolve() returns a stable definition).
public interface ISlotMethod
{
    ISlotType DeclaringType { get; }
    string Name { get; }

    // The parameter TYPE identities, in order — the slot-identity key (SameParams). The return type is
    // deliberately excluded: it's what we're flipping, so two members of one slot may spell it differently
    // (e.g. Task<A> vs Task<B> over a generic base) and must still group together.
    IReadOnlyList<string> ParameterTypeNames { get; }

    // True if this method INTRODUCES its slot (IsNewSlot) rather than overriding an inherited one. The true
    // root of a flippable slot is a NewSlot; a resolved root that is NOT a NewSlot means the real origin is a
    // base we couldn't see, so the slot is external.
    bool IsNewSlot { get; }

    // Body facts for the shared rewritability check (BodyRewrite): a method with no body (abstract decl) needs no
    // rewrite; one whose body has an exception-handler region or no `ret` can't have its return tail safely
    // rewritten (a `ret` inside a protected region can't `br` out). Raw facts; the family decides what "rewritable"
    // means for its seam via IFamilyPass.CanApplyFlip.
    bool HasBody { get; }
    bool HasExceptionHandler { get; }
    bool HasReturn { get; }
}
