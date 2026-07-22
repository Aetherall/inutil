namespace Inutil.Schema;

// Which way a family can be bridged faithfully (docs/guide/03-natural-typing.md's direction table). A Task result
// is Il2Cpp->Bcl (we hand the modder a natural Task); a Nullable field is Both; some families are one-way.
public enum Directionality { BclToIl2Cpp, Il2CppToBcl, Both }

// The three structural kinds every family reduces to — NOT per-type code. The shape tells a seam which shared
// machinery to use, so a new family is data (a shape + a bridge), not a switch arm:
//   ValueLayout       — Nullable<T>, ValueTuple<...>: a derived, generic layout/field copy (no per-type code).
//   ReferenceBespoke  — Task<T>, List<T>, Dictionary<K,V>: a hand-written semantic bridge, bounded in count.
//   ContainerAdapter  — IEnumerable<T>, IReadOnlyList<T>, arrays: the container bridge.
public enum BridgeShape { ValueLayout, ReferenceBespoke, ContainerAdapter }

// The classification contract every family implements exactly once, consumed by BOTH seams: given a CLOSED
// il2cpp type, can this family bind it (the honest probe — never hard-bind), and what shape/direction is it.
//
// The seam-specific CONVERSION machinery is deliberately NOT here — it would drag a heavy dependency (the IL
// seam's Mono.Cecil) or the wrong shape (the runtime seam's recursive Conv tree) into this pure module. Each
// seam resolves its own conversion from the correspondence; only what is genuinely SHARED ("is this a Task, what
// shape, which direction") lives here, and that is what makes drift between the seams impossible.
public interface IBridge
{
    // Probe whether this family binds a specific CLOSED il2cpp type. Never hard-binds a compile-time type name
    // against a game — always inspects the runtime/rewrite-time type (NATURAL-TYPING's stripping honest-limit).
    bool CanBind(ITypeRef il2cppClosedType);

    BridgeShape Shape { get; }
    Directionality Direction { get; }
}

// How one il2cpp family maps to one BCL family — the ONE object both seams share. Neither seam owns "how does
// type X correspond to type Y"; both consume this.
public sealed class TypeCorrespondence
{
    // The il2cpp anchor: the OPEN element name for a generic family ("Il2CppSystem.Threading.Tasks.Task`1") or
    // the plain type name. Resolved reflectively off the anchor type — NEVER hard-bound at compile time.
    public string Il2CppFullName { get; }

    // The open BCL counterpart, e.g. typeof(Task<>) — the seams close it over the element to build the natural
    // type. typeof(Task) (non-generic) for a non-generic family.
    public Type BclOpenType { get; }

    // The runtime-seam join key: the Conv-tree kind a value of this family marshals as. The runtime read (BCL type
    // -> ConvKind) and write (ConvKind -> il2cpp name) both key on it, so the registry is the single source for the
    // runtime seam too, not just the IL seam.
    public ConvKind Kind { get; }

    // Is THIS row the canonical il2cpp type a value of `Kind` materializes INTO (the write direction)? True for
    // List`1 / Dictionary`2 / Nullable`1 / ValueTuple`N / Task[`1]; FALSE for a read-only SPELLING (IReadOnlyList`1 /
    // IEnumerable`1 read as a sequence yet write to the concrete List`1). ByConvKind returns the write-target row.
    public bool WriteTarget { get; }

    public IBridge Bridge { get; }
    public BridgeShape Shape => Bridge.Shape;
    public Directionality Direction => Bridge.Direction;

    // The ONE definition of "which families the container flip owns" — a WRITE-TARGET concrete container
    // (List / Dictionary / ValueTuple / HashSet) whose il2cpp return/param the IL-rewrite seam rewrites to its
    // natural BCL spelling. Both the return family (ContainerFamily) and the type-mapper (ContainerFlip) key on
    // THIS, so the flip roster can never drift between the seam's spots (the HashSet add once left ContainerFlip.
    // Naturalize a Set behind the other three — exactly this drift). Excludes read-only spellings (IReadOnlyList/
    // IEnumerable), Nullable/Task (their own passes), and Leaf/Object/Array.
    public bool IsFlippableContainer =>
        WriteTarget && Kind is ConvKind.List or ConvKind.Dictionary or ConvKind.Tuple or ConvKind.Set;

    public TypeCorrespondence(string il2cppFullName, Type bclOpenType, ConvKind kind, IBridge bridge, bool writeTarget = true)
    {
        Il2CppFullName = il2cppFullName;
        BclOpenType = bclOpenType;
        Kind = kind;
        Bridge = bridge;
        WriteTarget = writeTarget;
    }
}
