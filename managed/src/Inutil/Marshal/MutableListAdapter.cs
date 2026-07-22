using System.Collections;
using System.Reflection;
using Inutil.Schema;

namespace Inutil.Marshal;

// The identity seam for the write-through sequence family (ConvKind.MutableList): the runtime recognizes a value
// it minted itself by this carrier and hands the HELD il2cpp list back by IDENTITY on ToIl2Cpp — so a hook that
// Proceeds its IList<T> param forwards the game's OWN list, never a copy. A mod-fabricated IList (no carrier)
// dematerializes into a fresh il2cpp List instead.
public interface IIl2CppListCarrier
{
    object Il2CppList { get; }
}

// A LIVE IList<T> view over the game's il2cpp List<T'> proxy (the write-through twin of MaterializeList). Nothing is
// copied — every op forwards to the held proxy through its OWN members (resolved reflectively; the game-agnostic SDK
// names no Il2CppSystem type), converting the element per-op through the child Conv (natural T out, il2cpp T' in). So
// `pool.Remove(p)` shrinks the game's real pool, and the game sees a mod's Add/Insert immediately — the semantics the
// copying List<T> spelling cannot give. Reads are as cheap as the proxy's own indexer; a mod that only READS should
// still spell List<T>/IReadOnlyList<T> (one bulk copy beats N proxied reads).
//
// Equality-shaped ops (Remove/IndexOf/Contains) forward to the PROXY's own methods, so membership is decided by the
// game's own comparer over the il2cpp elements — the honest semantics for a live view (a managed-side comparison
// over converted copies could disagree with what the game's List actually holds). A ref-bearing value-type ELEMENT
// routes writes through ValueTypeBridge.InvokeUnboxed (the box-vs-bytes hazard at Add/Insert/set_Item, same as
// DematerializeList); the equality ops have no faithful unboxed path, so they FAIL LOUD for such elements rather
// than compare a boxed-proxy pointer where value bytes belong.
public sealed class Il2CppMutableListAdapter<T> : IList<T>, IIl2CppListCarrier
{
    readonly object _list;              // the game's il2cpp List<T'> proxy — the ONE object every op forwards to
    readonly Conv _elem;                // the element's Conv node (per-op recursion, both directions)
    readonly IConvRuntime _runtime;
    readonly PropertyInfo _count;
    readonly PropertyInfo _item;
    readonly MethodInfo _add, _insert, _removeAt, _clear, _indexOf, _remove, _contains;

    public Il2CppMutableListAdapter(object il2cppList, Conv elem, IConvRuntime runtime)
    {
        _list = il2cppList;
        _elem = elem;
        _runtime = runtime;
        Type lt = il2cppList.GetType();
        Type et = ContainerBridge.Il2CppTypeOf(elem);
        _count    = Prop(lt, "Count");
        _item     = Prop(lt, "Item");
        _add      = Method(lt, "Add", et);
        _insert   = Method(lt, "Insert", typeof(int), et);
        _removeAt = Method(lt, "RemoveAt", typeof(int));
        _clear    = Method(lt, "Clear");
        _indexOf  = Method(lt, "IndexOf", et);
        _remove   = Method(lt, "Remove", et);
        _contains = Method(lt, "Contains", et);
    }

    static PropertyInfo Prop(Type lt, string name) => lt.GetProperty(name)
        ?? throw new NotSupportedException($"Il2CppMutableListAdapter: il2cpp list {lt} has no {name} — cannot present a write-through IList view.");
    static MethodInfo Method(Type lt, string name, params Type[] args) => lt.GetMethod(name, args)
        ?? throw new NotSupportedException($"Il2CppMutableListAdapter: il2cpp list {lt} has no {name}({string.Join(",", (object[])args)}) — cannot present a write-through IList view.");

    // The identity carrier: ToIl2Cpp of this adapter IS this list (Il2CppConvRuntime's MutableList write arm).
    public object Il2CppList => _list;

    T Out(object? il2cppElem) => (T)_elem.Convert(il2cppElem, ConvDirection.ToManaged, _runtime)!;
    object? In(T value) => _elem.Convert(value, ConvDirection.ToIl2Cpp, _runtime);

    public int Count => (int)_count.GetValue(_list)!;
    public bool IsReadOnly => false;

    public T this[int index]
    {
        get => Out(_item.GetValue(_list, new object[] { index }));
        set
        {
            object? v = In(value);
            if (ValueTypeBridge.IsRefBearingValueType(v)) ValueTypeBridge.InvokeUnboxed(_list, "set_Item", index, v);
            else _item.SetValue(_list, v, new object[] { index });
        }
    }

    public void Add(T item)
    {
        object? v = In(item);
        if (ValueTypeBridge.IsRefBearingValueType(v)) ValueTypeBridge.InvokeUnboxed(_list, "Add", v);
        else _add.Invoke(_list, new[] { v });
    }

    public void Insert(int index, T item)
    {
        object? v = In(item);
        if (ValueTypeBridge.IsRefBearingValueType(v)) ValueTypeBridge.InvokeUnboxed(_list, "Insert", index, v);
        else _insert.Invoke(_list, new object?[] { index, v });
    }

    public void RemoveAt(int index) => _removeAt.Invoke(_list, new object[] { index });
    public void Clear() => _clear.Invoke(_list, null);

    public int IndexOf(T item) => (int)_indexOf.Invoke(_list, new[] { EqualityArg(item, "IndexOf") })!;
    public bool Remove(T item) => (bool)_remove.Invoke(_list, new[] { EqualityArg(item, "Remove") })!;
    public bool Contains(T item) => (bool)_contains.Invoke(_list, new[] { EqualityArg(item, "Contains") })!;

    // The equality-op argument: converted ToIl2Cpp so the PROXY's own comparer decides membership. A ref-bearing
    // value-type element would cross the reflected call as a boxed-proxy pointer where the unboxed value bytes
    // belong — and unlike the void writes there is no result-free unboxed route — so it fails loud here.
    object? EqualityArg(T item, string op)
    {
        object? v = In(item);
        if (ValueTypeBridge.IsRefBearingValueType(v))
            throw new NotSupportedException(
                $"Il2CppMutableListAdapter: {op} on a ref-bearing value-type element ({v!.GetType().FullName}) has no faithful " +
                "boundary (the §7.5 box-vs-bytes hazard on an equality argument) — iterate by index (this[i] / RemoveAt) instead. " +
                "Fail-loud, never a silent mis-compare.");
        return v;
    }

    public void CopyTo(T[] array, int arrayIndex)
    {
        int n = Count;
        for (int i = 0; i < n; i++) array[arrayIndex + i] = this[i];
    }

    public IEnumerator<T> GetEnumerator()
    {
        int n = Count;                          // a live view: snapshot the count, read each element through the proxy
        for (int i = 0; i < n; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
