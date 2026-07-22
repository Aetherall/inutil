using Inutil.Schema;

namespace Inutil.Marshal;

// The high-level face of the runtime marshalling seam: build the Conv tree for a SPELLED managed type
// once, cache it, and drive a value through it with the real il2cpp runtime. This is what a hook boundary
// (future Facade) and a mod's explicit `.ToManaged()` both reduce to — one entry, one Conv tree per type, one
// IConvRuntime. The Conv COMPOSITION is proven offline (Inutil.Schema.Tests); this just binds it to il2cpp.
public static class Il2CppMarshal
{
    static readonly CorrespondenceRegistry _families = Families.Default();
    static readonly IConvShapeSource _shapes = new Il2CppConvShapeSource();
    static readonly IConvRuntime _runtime = new Il2CppConvRuntime();
    static readonly IIl2CppAnchors _anchors = new RegistryAnchors(_families);   // C2: stamp each node's expected il2cpp anchor
    static readonly Dictionary<Type, Conv> _cache = new();
    static readonly object _gate = new();

    static Conv For(Type managed)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(managed, out Conv? c))
                _cache[managed] = c = Conv.Build(managed, _shapes, _anchors);
            return c;
        }
    }

    // il2cpp value -> the natural spelled type T (an il2cpp array -> T[], an il2cpp Task -> Task<T>, a leaf as-is).
    // C2: before converting, validate the hook-SPELLED Conv tree against the game's ACTUAL il2cpp type node-by-node
    // (CorrespondenceValidator). A mismatch — mod spells List<int> where the game returns a Dictionary — fails LOUD
    // HERE with a correspondence error, instead of a downstream cast/marshalling crash with an opaque il2cpp trace.
    // Leaves and unanchored kinds (arrays, sequence read-spellings) accept anything, so the bulk fast path is
    // untouched. A null il2cpp value has no runtime type to check — it converts straight through (null -> null).
    public static T? ToManaged<T>(object? il2cppValue) => (T?)ToManagedCore(il2cppValue, typeof(T));

    // The non-generic core of the READ direction: validate the hook-spelled shape against the game's ACTUAL
    // il2cpp type (C2), then drive the Conv tree. ToManaged<T> and the Hook<T> boundary's PointerToManaged both
    // reduce to THIS — one validate+convert path, never a second reader.
    static object? ToManagedCore(object? il2cppValue, Type managedType)
    {
        Conv conv = For(managedType);
        if (il2cppValue is not null)
        {
            CorrespondenceValidator.Result v = CorrespondenceValidator.Matches(conv, ReflectionTypeRef.Of(il2cppValue.GetType()));
            if (!v.Ok)
                throw new InvalidOperationException(
                    $"inutil: correspondence mismatch marshalling to {managedType} — {v.Mismatch}. The hook-spelled shape " +
                    "does not line up with the game's actual il2cpp type (fail-loud, never a silent mis-marshal).");
        }
        return conv.Convert(il2cppValue, ConvDirection.ToManaged, _runtime);
    }

    // The Hook<T> boundary's READ entry: an il2cpp reference-object POINTER read from a native hook frame
    // slot -> the natural spelled managed value. The boundary holds a raw pointer, not a typed proxy (the return-
    // flip seam gets its value already typed off the managed stack; a hook frame does not), so recover the
    // concrete il2cpp proxy — its type is the Conv's resolved il2cpp anchor (ContainerBridge.Il2CppTypeOf) — via
    // its (IntPtr) ctor, then run the SAME validate+convert as ToManaged<T>. A null pointer is a null value. This
    // is the READ half the Hook<T> tier composes with the frame ABI; the WRITE half is the existing ToIl2Cpp —
    // one engine, both directions, no second marshaller at the hook boundary.
    public static object? PointerToManaged(nint il2cppPtr, Type managedType)
    {
        if (il2cppPtr == 0) return null;
        Type il2cppType = ContainerBridge.Il2CppTypeOf(For(managedType));
        object proxy = Activator.CreateInstance(il2cppType, (IntPtr)il2cppPtr)!;
        return ToManagedCore(proxy, managedType);
    }

    // natural managed value -> its il2cpp counterpart (the param direction). `managedType` is the spelled type.
    public static object? ToIl2Cpp(object? managedValue, Type managedType)
        => For(managedType).Convert(managedValue, ConvDirection.ToIl2Cpp, _runtime);

    // The Hook<T> boundary for a VALUE-type container (Nullable<T>, ValueTuple) — passed/returned BY VALUE, so
    // its unboxed bytes sit AT a frame slot rather than behind an object pointer. The read/write halves compose the
    // frame-ABI box/unbox (ValueTypeBridge) with the SAME Conv engine the object-pointer paths use: box the frame
    // bytes into the il2cpp value proxy, then ToManaged; and ToIl2Cpp, then unbox back into the frame bytes. One
    // engine, both directions, both representations (object pointer AND inline value) — no second marshaller.

    // frame value bytes -> natural spelled value. `frameBytes` is the address OF the value (Gpr inline / Ind|Sret
    // deref'd), which the boundary reads from HookContext.Arg/ReturnPointer.
    public static object? FrameValueToManaged(nint frameBytes, Type managedType)
    {
        object boxed = ValueTypeBridge.BoxFrameValue(ContainerBridge.Il2CppTypeOf(For(managedType)), frameBytes);
        return ToManagedCore(boxed, managedType);
    }

    // natural spelled value -> il2cpp value bytes written into the frame slot at `dst`. A value-type container is
    // never a null il2cpp value (an empty Nullable is an EmptyNullable VALUE, not a null ref), so a null proxy here
    // is a genuine engine fault, surfaced loud rather than written as a wild pointer. `barriered` (the Hook boundary's
    // HookContext.ArgWritesBarriered) is true only when `dst` is a ref-bearing ref/out destination — heap-backed
    // storage the incremental GC must be told about — so the unbox writes THROUGH the write barrier; every by-value
    // slot (arg/return root) passes false and gets a raw store, unchanged.
    public static void ManagedToFrameValue(object? managedValue, Type managedType, nint dst, bool barriered = false)
    {
        object? proxy = ToIl2Cpp(managedValue, managedType);
        if (proxy is null)
            throw new InvalidOperationException(
                $"inutil: value-type container {managedType} marshalled to a null il2cpp value — cannot write it into a by-value frame slot (fail-loud, never a silent mis-marshal).");
        ValueTypeBridge.UnboxToFrame(proxy, ContainerBridge.Il2CppTypeOf(For(managedType)), dst, barriered);
    }

    // The IL-rewrite seam's PARAM-flip splice target: convert a natural value to its il2cpp counterpart, closing
    // the spelled Type from the STATIC generic argument — so the seam emits `call ToIl2CppTyped<Natural>` at the
    // flipped param's load site with no ldtoken/Type push (the write analog of ToManaged<T> for returns).
    public static object? ToIl2CppTyped<T>(T managedValue) => ToIl2Cpp(managedValue, typeof(T));
}
