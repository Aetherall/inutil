namespace Inutil.Schema;

// The recursive element-conversion tree (docs/contribution/architecture/12-marshal.md) — the SHARED, unit-testable foundation the
// runtime marshaller stands on. Three properties a single-`inner` signature CANNOT give:
//   * MULTI-CHILD: a container node holds its OWN children — one for Array/List/Task<T>/Nullable, TWO for
//     Dictionary (key + value), N for ValueTuple. A single threaded `inner` can't express Dict's 2 or Tuple's N.
//   * HOOK-DRIVEN RECURSION: the tree is Built from the MANAGED (hook-SPELLED) type — the game's il2cpp element
//     is always already il2cpp-native, so recursion must key on what the mod spelled (Task<Player[]> -> recurse
//     Player[]), not on the game type.
//   * NO COMPLETED/PENDING DRIFT: one Conv node serves BOTH directions (ToIl2Cpp/ToManaged), routing every value
//     through the SAME node's children — no second copy of "how do I convert this shape" to desync.

// Per-ConvKind direction/fail-loud reminders (full family rationale in Families.cs, Mirror.cs):
//   Delegate    — BclToIl2Cpp, CALL-SITE-ONLY; NO reverse il2cpp->BCL bridge, so it FAILS LOUD if it reaches the
//                 runtime Conv tree (to observe the pointer instead, spell the raw Il2CppSystem.Action, a leaf).
//   Result      — Il2CppToBcl mirror; the natural game close is a broken il2cpp instantiation, so the mirror
//                 carries the natural element and the seam rebuilds the game Result via a per-game shim.
//   Callback    — inbound Result twin; HOLDS the game callback, marshals OUTBOUND on Ok/Fail. Return side fails loud.
//   DelegateIn  — inbound Delegate twin; invokes via the il2cpp proxy (NO per-game shim). Return side fails loud.
//   MutableList — IList<T> -> a LIVE write-through adapter over the game List<T'> (List/IReadOnlyList/IEnumerable
//                 materialize a COPY instead); ToIl2Cpp is identity for the adapter.
public enum ConvKind { Leaf, Nullable, Array, List, MutableList, Enumerable, Set, Dictionary, Tuple, Task, Delegate, DelegateIn, Object, Result, Callback, Exception }

public enum ConvDirection { ToIl2Cpp, ToManaged }

// The classified shape of one managed type: a leaf, or a container kind + the element types to recurse (0 for a
// terminal like non-generic Task/Object, 1 for Array/List/Task<T>/Nullable, 2 for Dictionary, N for ValueTuple).
// The seam supplies a classifier that knows il2cpp leaves; tests supply BCL shapes.
public readonly struct ConvShape
{
    public ConvKind Kind { get; }
    public IReadOnlyList<Type> Elements { get; }
    public bool IsLeaf => Kind == ConvKind.Leaf;

    ConvShape(ConvKind kind, IReadOnlyList<Type> elements) { Kind = kind; Elements = elements; }

    public static ConvShape Leaf() => new(ConvKind.Leaf, Array.Empty<Type>());
    public static ConvShape Container(ConvKind kind, params Type[] elements) => new(kind, elements);
}

public interface IConvShapeSource
{
    ConvShape Classify(Type managed);
}

// Supplies the EXPECTED il2cpp anchor for a container node at Build time (the map from a managed container shape
// to the open il2cpp element the game must spell — Task`1, Dictionary`2, …). Injectable, NOT hard-coded, because
// (1) the real anchor strings are game-facing and must be verified against real proxies, never guessed in the
// pure module; (2) tests supply synthetic anchors to prove the VALIDATION LOGIC without pinning an unverified
// name. Returns null when the seam has no correspondence — the node is then unvalidatable and accepts anything.
public interface IIl2CppAnchors
{
    string? Anchor(ConvKind kind, Type managed);
}

// One node of the conversion tree. A leaf converts identity (the native/bulk fast path); a container holds its
// OWN children and recurses THROUGH them. Immutable and built once per hook-boundary spelling.
public sealed class Conv
{
    public Type Managed { get; }
    public ConvKind Kind { get; }
    public IReadOnlyList<Conv> Children { get; }

    // The expected game-side il2cpp anchor for this container node: the OPEN element name the game must spell here
    // for the bridge to be honest. null for leaves (identity accepts any il2cpp-native type) and for shapes the
    // seam has no correspondence for. CorrespondenceValidator checks it node-by-node against the real game element
    // (`== gameElem`) — the recursive form of IBridge.CanBind's "never hard-bind, always probe".
    public string? Il2CppAnchor { get; }

    // A native leaf — the fast path. Most elements resolve here (existing hooks stay on their bulk marshalling
    // path); the recursive code is reached only by a nested spelling.
    public bool Identity => Kind == ConvKind.Leaf;

    Conv(Type managed, ConvKind kind, IReadOnlyList<Conv> children, string? il2cppAnchor)
    {
        Managed = managed;
        Kind = kind;
        Children = children;
        Il2CppAnchor = il2cppAnchor;
    }

    // Build the tree for `managed` (the hook's SPELLED type), recursing each element into its own child. Well-
    // founded: containers strictly decrease toward leaves. `anchors` (optional) stamps each container node with the
    // il2cpp type it expects, for CorrespondenceValidator; omit it and the tree is unannotated (validation inert).
    public static Conv Build(Type managed, IConvShapeSource shapes, IIl2CppAnchors? anchors = null)
    {
        ConvShape shape = shapes.Classify(managed);
        if (shape.IsLeaf) return new Conv(managed, ConvKind.Leaf, Array.Empty<Conv>(), null);

        var children = new Conv[shape.Elements.Count];
        for (int i = 0; i < children.Length; i++)
            children[i] = Build(shape.Elements[i], shapes, anchors);
        return new Conv(managed, shape.Kind, children, anchors?.Anchor(shape.Kind, managed));
    }

    // Walk this node in either direction (leaf -> native leaf path, container -> per-kind marshalling recursing
    // THIS node's stored children). Both directions dispatch on the same node, so a value can never be completed
    // one way through a path that lost the recursion the other has.
    public object? Convert(object? value, ConvDirection dir, IConvRuntime runtime)
        => Identity ? runtime.Leaf(this, value, dir) : runtime.Container(this, value, dir);

    public override string ToString()
        => Identity ? $"Leaf({Managed.Name})" : $"{Kind}<{string.Join(",", Children)}>";
}

// The seam's actual marshalling, invoked by Conv.Convert. Leaf = native pass-through; Container = materialize/
// iterate the il2cpp or BCL container, converting each element through node.Children. Kept OUT of the pure module
// (it touches the il2cpp runtime); tests supply a synthetic impl to prove the tree composes offline.
public interface IConvRuntime
{
    object? Leaf(Conv node, object? value, ConvDirection dir);
    object? Container(Conv node, object? value, ConvDirection dir);
}

// The default reflective shape classifier: recognizes the BCL container families and treats everything else as
// a leaf. The seam wraps this to add il2cpp-specific leaves (Il2CppObjectBase-derived proxies, ref-bearing
// value proxies) via `extraLeaf`, and refines the terminal cases (non-generic Task, Object, Inutil.Result).
public class ReflectionConvShapeSource : IConvShapeSource
{
    readonly Func<Type, bool> _extraLeaf;
    readonly CorrespondenceRegistry _families;

    public ReflectionConvShapeSource(Func<Type, bool>? extraLeaf = null, CorrespondenceRegistry? families = null)
    {
        _extraLeaf = extraLeaf ?? (_ => false);
        _families = families ?? Families.Default();
    }

    protected virtual bool IsLeaf(Type t)
        => t.IsPrimitive || t.IsEnum || t == typeof(string) || _extraLeaf(t);

    public virtual ConvShape Classify(Type t)
    {
        if (IsLeaf(t)) return ConvShape.Leaf();

        if (t.IsArray && t.GetArrayRank() == 1)
            return ConvShape.Container(ConvKind.Array, t.GetElementType()!);

        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            Type def = t.GetGenericTypeDefinition();
            // The "which BCL family / which ConvKind" map is the registry, not a per-def literal chain.
            // ByBclOpenType keys on the OPEN def; the type arguments ARE the children for every family (1 for
            // Nullable/List/Task, 2 for Dictionary, N for a tuple), so one Container(kind, args) serves all. Array
            // stays above (its il2cpp form is element-kind-dependent). An unmodelled generic (incl. ValueTuple`8+)
            // falls through to a leaf — pass the raw value through, never a crash.
            if (_families.ByBclOpenType(def) is { } corr)
                return ConvShape.Container(corr.Kind, t.GetGenericArguments());
        }

        // Everything else (non-generic Task, System.Object, unmodelled concretes) is a leaf here; the seam's
        // subclass refines those terminals. An unknown as an identity leaf means "pass the raw value through".
        return ConvShape.Leaf();
    }
}
