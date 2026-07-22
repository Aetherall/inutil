using System.Collections;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;          // Il2CppObjectBase — the base of every game reference proxy
using Il2CppInterop.Runtime.InteropTypes.Arrays;   // Il2CppStructArray / Il2CppStringArray / Il2CppReferenceArray
using Inutil.Schema;
using Inutil.Sugar;                                // Il2CppSugar.UnwrapTask / IsCarrier — Task carrier identity map

namespace Inutil.Marshal;

// The container/array bridge: ONE generic materialization per container kind, parameterized by the per-element
// child Conv, with one loop + ONE enumeration-strategy resolver. Element conversion recurses through
// node.Children[i] (the Conv tree), so a nested spelling (Player[][], a List<int[]>, an int[] inside a Task)
// marshals by construction, not by a second copy of the loop.
//
// Kept in the SDK (it touches il2cpp), but written against the plain sequence surfaces il2cpp containers expose,
// so the loop LOGIC is exercised offline (a BCL array/list stands in) and only the real element types differ
// in-game.
public static class ContainerBridge
{
    // il2cpp array -> natural BCL T[] (typed to the SPELLED element, not object[]), each element recursing
    // through the array's single child Conv.
    public static Array MaterializeArray(Conv node, object il2cppArray, ConvDirection dir, IConvRuntime runtime)
    {
        Conv elem = node.Children[0];
        var converted = new List<object?>();
        foreach (object? item in Enumerate(il2cppArray))
            converted.Add(elem.Convert(item, dir, runtime));

        Array managed = Array.CreateInstance(elem.Managed, converted.Count);
        for (int i = 0; i < converted.Count; i++)
            managed.SetValue(converted[i], i);
        return managed;
    }

    // natural BCL T[] -> the il2cpp array wrapper for this node, each element RECURSING ToIl2Cpp through the child
    // Conv. The write-side mirror of MaterializeArray — but where the read side got the il2cpp type for free (it
    // was the input), here it is RESOLVED (Il2CppTypeOf) and CONSTRUCTED (needs a booted runtime -> validated
    // in-game). The intermediate array is typed to the ELEMENT's il2cpp type (Il2CppTypeOf(elem)): for a leaf that
    // is the managed leaf itself (identity), for a nested container it is the child's own il2cpp wrapper — so a
    // jagged int[][] materializes each inner int[] into an Il2CppStructArray<int> and packs them into the outer
    // Il2CppReferenceArray, by construction rather than a second loop.
    public static object DematerializeArray(Conv node, object managedArray, ConvDirection dir, IConvRuntime runtime)
    {
        Conv elem = node.Children[0];
        var src = (Array)managedArray;
        Array converted = Array.CreateInstance(Il2CppTypeOf(elem), src.Length);
        for (int i = 0; i < src.Length; i++)
            converted.SetValue(elem.Convert(src.GetValue(i), dir, runtime), i);   // each element recurses ToIl2Cpp
        return Activator.CreateInstance(Il2CppTypeOf(node), new object[] { converted })!;
    }

    // natural Task[<T>] -> il2cpp Task[`1<T'>] (the hook-boundary FABRICATE Il2CppSugar documents).
    // A CARRIER (a forwarded game task — one WrapTask minted) round-trips by IDENTITY: UnwrapTask gives the original
    // il2cpp pointer, rebuilt into its proxy (.ctor(IntPtr)), so `return Next.X()` hands the game its OWN task, never a
    // copy. A mod-FABRICATED task (Task.FromResult / a pending TCS) has no il2cpp twin, so we build one — il2cpp's
    // TaskCompletionSource is stripped, but Task`1 IS the promise: a COMPLETED managed task -> Task`1<T'>(result) (the
    // .ctor(TResult) completed-task ctor); a PENDING one -> a promise Task`1<T'>() returned NOW and driven to completion
    // by TrySetResult when the managed task finishes (a managed continuation bridges the completion — the pending half
    // Il2CppSugar split out). A generic result recurses ToIl2Cpp through the child; a non-generic Task has no result, so
    // it rides a Task`1<bool> upcast (Task<T> : Task). Caveat: the completion continuation calls an il2cpp proxy, so a
    // genuinely async managed task must complete on an il2cpp-ATTACHED thread (the common inline/same-thread case is fine).
    public static object DematerializeTask(Conv node, object value, ConvDirection dir, IConvRuntime runtime)
    {
        var managedTask = (System.Threading.Tasks.Task)value;

        // Carrier -> forward the ORIGINAL il2cpp task by identity (rebuild its proxy from the stashed pointer).
        nint fwd = Il2CppSugar.UnwrapTask(managedTask);
        if (fwd != 0)
            return Activator.CreateInstance(Il2CppTypeOf(node), new object[] { (IntPtr)fwd })!;

        // Fabricate. Generic Task<T> builds Task`1<T'>; a non-generic Task rides a Task`1<bool> upcast (Task<T> : Task).
        bool generic = node.Children.Count == 1;
        Type il2cppResult = generic ? Il2CppTypeOf(node.Children[0]) : typeof(bool);
        Type taskT = ResolveIl2CppProxy(Il2CppName(ConvKind.Task, 1)).MakeGenericType(il2cppResult);

        object Result() => generic ? node.Children[0].Convert(TaskResult(managedTask), dir, runtime)! : true;

        // Complete an il2cpp Task`1<T'> with the converted result. A ref-bearing value-type result (the game's
        // Comfort.Common.Result<T> — a struct whose field is a reference — or any struct-with-references) reaches BOTH
        // the Task`1<T'>(TResult) completed-ctor AND the reflected TrySetResult(T) as its boxed-proxy POINTER where the
        // UNBOXED value bytes belong; Task.Result then holds a garbage struct (its embedded reference reads void) and the
        // game faults when it unwraps .Value (measured: GetBaseTraderDialogs -> DialogStorage.AddTemplates AV at menu).
        // Route such a result through the raw UNBOXED invoke — the very box-vs-bytes fix ValueTypeBridge already applies
        // at list Add / dict set_Item / tuple Item, and the Task-completion site its own header anticipated. A
        // blittable/reference result keeps the proven managed-wrapper path (the completed-ctor when already completed).
        void Complete(object task, object result)
        {
            if (ValueTypeBridge.IsRefBearingValueType(result))
                ValueTypeBridge.InvokeUnboxed(task, "TrySetResult", result);     // unboxed struct bytes cross correctly
            else
            {
                MethodInfo trySet = taskT.GetMethod("TrySetResult", new[] { il2cppResult })
                    ?? throw new NotSupportedException($"ContainerBridge: il2cpp {taskT} has no TrySetResult({il2cppResult}) — cannot complete a fabricated task.");
                trySet.Invoke(task, new[] { result });
            }
        }

        if (managedTask.IsCompleted)
        {
            object completed = Result();
            // Fast path: a blittable/reference result rides the proven completed-task ctor Task`1<T'>(result). SELECT it by
            // the KNOWN il2cpp result type (il2cppResult), not the value's runtime type — a NULL result (Task.FromResult<T>
            // (null): a "no active survey" SurveyData, an empty response) has no runtime type, so Activator.CreateInstance's
            // binder can't disambiguate it from Task`1's other 1-arg ctor (.ctor(IntPtr)) and throws AmbiguousMatchException,
            // which surfaced as a hook dispatch error and left the game's awaited Task unset (RequestSurvey -> NewsHub NRE).
            if (!ValueTypeBridge.IsRefBearingValueType(completed))
            {
                ConstructorInfo? resultCtor = taskT.GetConstructor(new[] { il2cppResult });   // Task`1<T'>(T') — exact, unambiguous
                return resultCtor is not null
                    ? resultCtor.Invoke(new[] { completed })!
                    : Activator.CreateInstance(taskT, new[] { completed })!;      // no such ctor -> fall back (a non-null result is unambiguous)
            }
            // A ref-bearing value-type result can't cross the ctor (boxed-proxy vs unboxed bytes) — complete an empty
            // promise UNBOXED instead, so the game reads the real struct (with its embedded reference intact).
            object done = Activator.CreateInstance(taskT)!;
            Complete(done, completed);
            return done;
        }

        // Pending: hand the game a promise NOW; complete it (unboxed when ref-bearing) when the managed task finishes.
        object promise = Activator.CreateInstance(taskT)!;                        // Task`1<T'>() — a not-yet-completed promise
        managedTask.ContinueWith(_ => Complete(promise, Result()),
            System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
        return promise;
    }

    // The result of a completed Task<T> by reflection (the base Task type hides the generic Result). Only reached on the
    // completed/generic path — a carrier returned earlier and a non-generic Task has no result.
    static object? TaskResult(System.Threading.Tasks.Task t) => t.GetType().GetProperty("Result")?.GetValue(t);

    // The il2cpp representation type a Conv node marshals to on the WRITE side — the type ToIl2Cpp constructs, and
    // the element type a container's intermediate array is built over. Where the read side RECEIVED this type as
    // input, the write side must DERIVE it. Pure metadata (touches no il2cpp runtime), so it is unit-tested offline
    // even though the actual construction needs a booted runtime. (Per-kind derivation is in the switch below.)

    // The single family registry (C1): the runtime WRITE seam looks up the canonical il2cpp type name for a
    // ConvKind here instead of spelling "Il2CppSystem.…" inline. ByConvKind returns the WriteTarget row, so a
    // sequence (List OR Enumerable) resolves to the concrete List`1 and a tuple disambiguates by arity.
    static readonly CorrespondenceRegistry _families = Families.Default();
    static string Il2CppName(ConvKind kind, int arity = -1)
        => _families.ByConvKind(kind, arity)?.Il2CppFullName
           ?? throw new NotSupportedException($"ContainerBridge: no il2cpp write target registered for {kind}"
                + (arity >= 0 ? $" arity {arity}" : "") + " (Families.Default incomplete?) — fail-loud, never silent mis-marshal.");

    public static Type Il2CppTypeOf(Conv node)
    {
        switch (node.Kind)
        {
            case ConvKind.Leaf:
                return node.Managed;
            case ConvKind.Array:
                Conv elem = node.Children[0];
                Type e = Il2CppTypeOf(elem);                                       // recurse: the element's il2cpp type
                if (elem.Kind == ConvKind.Leaf && e == typeof(string)) return typeof(Il2CppStringArray);
                if (elem.Kind == ConvKind.Leaf && e.IsValueType) return typeof(Il2CppStructArray<>).MakeGenericType(e);
                return typeof(Il2CppReferenceArray<>).MakeGenericType(e);         // reference proxy leaf OR nested container
            case ConvKind.List:
            case ConvKind.Enumerable:
            case ConvKind.MutableList:                                            // every LIST spelling -> concrete il2cpp List<T'>
                return ResolveIl2CppProxy(Il2CppName(ConvKind.List)).MakeGenericType(Il2CppTypeOf(node.Children[0]));
            case ConvKind.Set:                                                    // HashSet<T> -> il2cpp HashSet<T'> (its OWN write target)
                return ResolveIl2CppProxy(Il2CppName(ConvKind.Set)).MakeGenericType(Il2CppTypeOf(node.Children[0]));
            case ConvKind.Dictionary:
                return ResolveIl2CppProxy(Il2CppName(ConvKind.Dictionary))
                    .MakeGenericType(Il2CppTypeOf(node.Children[0]), Il2CppTypeOf(node.Children[1]));
            case ConvKind.Nullable:
                return ResolveIl2CppProxy(Il2CppName(ConvKind.Nullable)).MakeGenericType(Il2CppTypeOf(node.Children[0]));
            case ConvKind.Tuple:
            {
                var targs = new Type[node.Children.Count];
                for (int i = 0; i < targs.Length; i++) targs[i] = Il2CppTypeOf(node.Children[i]);
                return ResolveIl2CppProxy(Il2CppName(ConvKind.Tuple, targs.Length)).MakeGenericType(targs);
            }
            case ConvKind.Task:                                                   // Task<T> -> il2cpp Task`1<T'>; Task -> non-generic Task
                return node.Children.Count == 1
                    ? ResolveIl2CppProxy(Il2CppName(ConvKind.Task, 1)).MakeGenericType(Il2CppTypeOf(node.Children[0]))
                    : ResolveIl2CppProxy(Il2CppName(ConvKind.Task, 0));
            case ConvKind.Result:                                                 // Inutil.Bridge.Result<T> -> the game Result<T'> (per-game shim), element recursing
                return GameResultOpen().MakeGenericType(Il2CppTypeOf(node.Children[0]));
            case ConvKind.Callback:                                               // Inutil.Bridge.Callback<T> -> the game Callback<T'> (per-game shim), element recursing
                return GameCallbackOpen().MakeGenericType(Il2CppTypeOf(node.Children[0]));
            case ConvKind.DelegateIn:                                             // Inutil.Bridge.Action/Func<..> -> the il2cpp Action`N/Func`N proxy (Il2Cppmscorlib, game-agnostic — NO per-game shim), each child recursing
            {
                // The il2cpp proxy NAME is the OUTBOUND Delegate family row for this mirror's System.Action/Func open
                // (C4 — registry-sourced, never spelled here; Action-vs-Func from the mirror open). Children are ALL the
                // type args for BOTH kinds: an Action mirror's children are its args; a Func mirror's LAST child is the
                // return — and the il2cpp Func`N proxy's type args ARE that same full list — so one MakeGenericType serves both.
                Type sysOpen = Inutil.Bridge.DelegateMirrors.SystemOpen(node.Managed.GetGenericTypeDefinition())
                    ?? throw new NotSupportedException($"ContainerBridge: {node.Managed} is a DelegateIn node but not a registered inbound-delegate mirror — fail-loud.");
                string proxyName = _families.ByBclOpenType(sysOpen)?.Il2CppFullName
                    ?? throw new NotSupportedException($"ContainerBridge: no il2cpp delegate proxy registered for {sysOpen} (Families.Default incomplete?) — fail-loud, never silent mis-marshal.");
                var targs = new Type[node.Children.Count];
                for (int i = 0; i < targs.Length; i++) targs[i] = Il2CppTypeOf(node.Children[i]);
                return ResolveIl2CppProxy(proxyName).MakeGenericType(targs);
            }
            case ConvKind.Object:                                                 // erased top: natural `object` -> the game's Il2CppSystem.Object (non-generic)
                // Resolve the game type by the registry NAME (ByBclOpenType, not ByConvKind — the object row is
                // writeTarget:false, runtime-only, so it isn't a ByConvKind write target; but ByBclOpenType still keys it,
                // exactly as the read/shape site does). C1: no "Il2CppSystem.Object" string spelled here — it comes from Families.
                return ResolveIl2CppProxy(_families.ByBclOpenType(typeof(object))?.Il2CppFullName
                    ?? throw new NotSupportedException("ContainerBridge: no ConvKind.Object correspondence registered (Families.Default incomplete?) — fail-loud."));
            case ConvKind.Exception:                                              // natural System.Exception -> the game's Il2CppSystem.Exception (non-generic; defensive — a top-level Exception unwraps by identity, but a nested/container element resolves here)
                return ResolveIl2CppProxy(_families.ByBclOpenType(typeof(System.Exception))?.Il2CppFullName
                    ?? throw new NotSupportedException("ContainerBridge: no ConvKind.Exception correspondence registered (Families.Default incomplete?) — fail-loud."));
            default:
                // Unreachable: the shape classifier emits only Leaf (handled above) + the container kinds cased here.
                // A defensive fail-loud only, for a new kind added without a write target.
                throw new NotSupportedException($"ContainerBridge: write-side il2cpp type of {node.Kind} ({node.Managed}) has no write target — unhandled kind (should be unreachable; fail-loud, never silent mis-marshal).");
        }
    }

    // R1 — the runtime il2cpp proxy resolver ("resolved reflectively off the anchor type, NEVER hard-bound at compile
    // time"): the game-agnostic SDK does NOT reference Il2Cppmscorlib, so it resolves Il2CppSystem.* proxy types by
    // full name from the LOADED assemblies at runtime. Cached by name. In-game-only: offline (no Il2Cppmscorlib
    // loaded) it fails LOUD, which is why the List/Dict/value-type write RESOLUTION is validated in-game.
    // NullableProxyOf: the closed Il2CppSystem.Nullable<inner> proxy Type (resolved as Il2CppTypeOf's Nullable arm
    // does) — the class ValueTypeBridge.RefToNullable boxes a ref-bearing Nullable PARAM into. Public so that helper
    // reuses this ONE resolver instead of re-inlining the il2cpp Nullable proxy name. Fails loud offline, like the rest of R1.
    public static Type NullableProxyOf(Type innerProxy)
        => ResolveIl2CppProxy(Il2CppName(ConvKind.Nullable)).MakeGenericType(innerProxy);

    static readonly Dictionary<string, Type> _proxyCache = new();
    static Type ResolveIl2CppProxy(string il2cppFullName)
    {
        lock (_proxyCache)
        {
            if (_proxyCache.TryGetValue(il2cppFullName, out Type? cached)) return cached;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetType(il2cppFullName, throwOnError: false) is { } t)
                    return _proxyCache[il2cppFullName] = t;
            throw new NotSupportedException($"ContainerBridge: could not resolve il2cpp proxy type '{il2cppFullName}' among loaded assemblies (is Il2Cppmscorlib loaded?) — the write direction needs the game's il2cpp BCL proxies present.");
        }
    }

    // il2cpp sequence (List<T> / IReadOnlyList<T> / IEnumerable<T> / HashSet<T>) -> the SPELLED natural collection.
    // Construct the spelled CONCRETE type (List<T>, HashSet<T>) so a Set stays a Set; for an interface read-spelling
    // (IReadOnlyList/IEnumerable, which can't be `new`'d) a List<T> satisfies it. Populate through the collection's
    // OWN Add(T), so ONE materializer serves every constructible sequence family — a new one (Set today, a Queue
    // tomorrow) reuses this rather than a per-family copy. Eager for now; a lazy adapter is a later optimization.
    public static object MaterializeList(Conv node, object il2cppSeq, ConvDirection dir, IConvRuntime runtime)
    {
        Conv elem = node.Children[0];
        Type target = node.Managed.IsInterface ? typeof(List<>).MakeGenericType(elem.Managed) : node.Managed;
        object coll = Activator.CreateInstance(target)!;
        MethodInfo add = target.GetMethod("Add", new[] { elem.Managed })
            ?? throw new NotSupportedException($"ContainerBridge: collection {target} has no Add({elem.Managed}) — cannot materialize the spelled sequence.");
        foreach (object? item in Enumerate(il2cppSeq))
            add.Invoke(coll, new[] { elem.Convert(item, dir, runtime) });
        return coll;
    }

    // natural BCL sequence (List<T> / IReadOnlyList<T> / IEnumerable<T>) -> a concrete il2cpp
    // Il2CppSystem.Collections.Generic.List<T'> (the constructible target for every sequence spelling — the il2cpp
    // interfaces IReadOnlyList/IEnumerable can't be `new`'d), each element RECURSING ToIl2Cpp through the child.
    // The write mirror of MaterializeList; the il2cpp element type T' comes from the write-side resolver, and the
    // list is populated through the proxy's own Add(T').
    public static object DematerializeList(Conv node, object managedSeq, ConvDirection dir, IConvRuntime runtime)
    {
        Conv elem = node.Children[0];
        Type il2cppListType = Il2CppTypeOf(node);
        Type elemIl2cpp = Il2CppTypeOf(elem);
        object list = Activator.CreateInstance(il2cppListType)!;
        MethodInfo add = il2cppListType.GetMethod("Add", new[] { elemIl2cpp })
            ?? throw new NotSupportedException($"ContainerBridge: il2cpp list {il2cppListType} has no Add({elemIl2cpp})");
        foreach (object? item in (IEnumerable)managedSeq)
        {
            object? converted = elem.Convert(item, dir, runtime);
            // A ref-bearing value-type ELEMENT (Loadout) reaches Add(T')'s Il2CppInterop wrapper as a boxed-proxy
            // pointer where the UNBOXED value bytes belong (embedded string dropped, value garbled) — the same
            // box-vs-bytes hazard the dict set_Item path fixes. Route Add through the raw unboxed invoke so the value
            // bytes cross correctly; blittable/reference elements keep the proven managed-wrapper Add. One primitive,
            // every value-write site (list Add, dict set_Item, tuple/nullable field) — verified in-game vs ToyGame.Loadout.
            if (ValueTypeBridge.IsRefBearingValueType(converted))
                ValueTypeBridge.InvokeUnboxed(list, "Add", converted);
            else
                add.Invoke(list, new[] { converted });
        }
        return list;
    }

    // il2cpp List<T'> -> a LIVE write-through IList<T> view (ConvKind.MutableList).
    // NOTHING is copied: the adapter holds the game's own il2cpp list and forwards every op to it, converting the
    // element per-op through the single child Conv. The read twin of the identity write below — together they keep
    // a hook's IList<T> param ON the game's real list across the whole dispatch (read -> mutate -> Proceed).
    public static object WrapMutableList(Conv node, object il2cppList, IConvRuntime runtime)
    {
        Conv elem = node.Children[0];
        Type adapter = typeof(Il2CppMutableListAdapter<>).MakeGenericType(elem.Managed);
        return Activator.CreateInstance(adapter, il2cppList, elem, runtime)!;
    }

    // natural IList<T> -> il2cpp (ConvKind.MutableList write side). The adapter the runtime minted round-trips by
    // IDENTITY — the HELD il2cpp list, so `Proceed(pool, …)` hands the original method the game's OWN pool and its
    // mutations (a withDelete Remove) land where the game looks. A mod-FABRICATED IList (no carrier) has no il2cpp
    // twin, so it dematerializes into a fresh concrete List`1 exactly like the copying sequence spellings.
    public static object DematerializeMutableList(Conv node, object managedSeq, ConvDirection dir, IConvRuntime runtime)
        => managedSeq is IIl2CppListCarrier carrier ? carrier.Il2CppList : DematerializeList(node, managedSeq, dir, runtime);

    // il2cpp Dictionary<K,V> -> a natural BCL Dictionary<K,V>, the KEY recursing through node.Children[0] and the
    // VALUE through node.Children[1] (the 2-child container — the one shape a single threaded `inner` could not
    // express). A dictionary is PAIR-shaped, not positionally indexed, so it does NOT go through the sequence
    // Enumerate() resolver (whose Count+indexer fallback assumes an int indexer an il2cpp dict has no equivalent
    // of); it enumerates KeyValuePairs through EnumeratePairs and converts each half.
    public static object MaterializeDictionary(Conv node, object il2cppDict, ConvDirection dir, IConvRuntime runtime)
    {
        Conv kc = node.Children[0], vc = node.Children[1];
        var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(kc.Managed, vc.Managed))!;
        foreach ((object? k, object? v) in EnumeratePairs(il2cppDict))
            dict.Add(kc.Convert(k, dir, runtime)!, vc.Convert(v, dir, runtime));
        return dict;
    }

    // natural BCL Dictionary<K,V> -> a concrete il2cpp Il2CppSystem.Collections.Generic.Dictionary<K',V'>, the KEY
    // recursing ToIl2Cpp through Children[0] and the VALUE through Children[1] (the 2-child write), populated
    // through the proxy's own set_Item indexer (proven by the read-side battery's dict construction). The write
    // mirror of MaterializeDictionary; the il2cpp key/value types come from the write-side resolver.
    public static object DematerializeDictionary(Conv node, object managedDict, ConvDirection dir, IConvRuntime runtime)
    {
        Conv kc = node.Children[0], vc = node.Children[1];
        Type il2cppDictType = Il2CppTypeOf(node);
        Type kIl2cpp = Il2CppTypeOf(kc), vIl2cpp = Il2CppTypeOf(vc);
        object dict = Activator.CreateInstance(il2cppDictType)!;
        MethodInfo setItem = il2cppDictType.GetMethod("set_Item", new[] { kIl2cpp, vIl2cpp })
            ?? throw new NotSupportedException($"ContainerBridge: il2cpp dictionary {il2cppDictType} has no set_Item({kIl2cpp},{vIl2cpp})");
        foreach (DictionaryEntry entry in (IDictionary)managedDict)
        {
            object? k = kc.Convert(entry.Key, dir, runtime);
            object? v = vc.Convert(entry.Value, dir, runtime);
            // A ref-bearing value-type KEY or VALUE (Loadout) would reach the generic set_Item wrapper as a
            // boxed-proxy pointer where the UNBOXED value bytes belong — and, unlike a tuple's Item field, a dict
            // entry exposes no field to patch afterward. Route the whole set_Item through a raw runtime-invoke that
            // marshals each arg unboxed. Blittable/reference-only pairs keep the proven managed-wrapper path.
            if (ValueTypeBridge.IsRefBearingValueType(k) || ValueTypeBridge.IsRefBearingValueType(v))
                ValueTypeBridge.InvokeUnboxed(dict, "set_Item", k, v);
            else
                setItem.Invoke(dict, new[] { k, v });
        }
        return dict;
    }

    // Key/value pair enumeration for a dictionary, resolved like Enumerate() but over the PAIR surface: prefer the
    // BCL IEnumerable-of-KeyValuePair a BCL dict (and any il2cpp dict Il2CppInterop bridges) exposes; else the
    // universal GetEnumerator/MoveNext/Current protocol an il2cpp collection proxy implements but does NOT bridge
    // to BCL IEnumerable (the same gap List<T> hit in-game). Each entry — BCL or il2cpp KeyValuePair — yields its
    // Key/Value by property.
    static IEnumerable<(object? Key, object? Value)> EnumeratePairs(object dict)
    {
        if (dict is IEnumerable seq)
        {
            foreach (object? entry in seq) yield return ReadPair(entry!);
            yield break;
        }
        // The universal GetEnumerator/MoveNext/Current protocol (shared with Enumerate via IterateRaw) — each entry
        // is an il2cpp KeyValuePair whose Key/Value ReadPair splits.
        foreach (object? entry in IterateRaw(dict)) yield return ReadPair(entry!);
    }

    // Read the Key/Value off one dictionary entry (a BCL or il2cpp KeyValuePair<,>) by property — the one surface
    // both spellings share.
    static (object?, object?) ReadPair(object entry)
    {
        Type et = entry.GetType();
        object? k = (et.GetProperty("Key") ?? throw new NotSupportedException($"ContainerBridge: dictionary entry {et.FullName} has no Key")).GetValue(entry);
        object? v = (et.GetProperty("Value") ?? throw new NotSupportedException($"ContainerBridge: dictionary entry {et.FullName} has no Value")).GetValue(entry);
        return (k, v);
    }

    // il2cpp Nullable<T> -> natural T? : read HasValue; if false the natural value is null; else convert the inner
    // Value through the single child Conv. The converted inner value, boxed, IS a valid T? (a boxed Nullable<T> is
    // either null or the boxed underlying value), so no explicit Nullable<T> construction is needed. This is the
    // READ side — a direct proxy property read Il2CppInterop already unboxes; the write-into-a-generic-slot hazard
    // (ValueTypeBridge) is the ToIl2Cpp direction, not this one.
    public static object? MaterializeNullable(Conv node, object il2cppNullable, ConvDirection dir, IConvRuntime runtime)
    {
        Type t = il2cppNullable.GetType();
        PropertyInfo hasValue = t.GetProperty("HasValue") ?? throw new NotSupportedException($"ContainerBridge: nullable {t.FullName} has no HasValue");
        if (!(bool)hasValue.GetValue(il2cppNullable)!) return null;
        object? inner = (t.GetProperty("Value") ?? throw new NotSupportedException($"ContainerBridge: nullable {t.FullName} has no Value")).GetValue(il2cppNullable);
        return node.Children[0].Convert(inner, dir, runtime);
    }

    // The ToIl2Cpp image of a null T? (an EMPTY Nullable): a fresh Il2CppSystem.Nullable<T'> via its PARAMETERLESS
    // ctor — HasValue=false, bytes zero. A value-type Nullable PARAM is unboxed by the proxy body, so an empty one
    // must be a real (zeroed) VALUE, never a null reference (which NREs on unbox). Container routes the null case
    // here instead of short-circuiting to null (the param write direction); the PRESENT case is DematerializeNullable.
    public static object EmptyNullable(Conv node) => Activator.CreateInstance(Il2CppTypeOf(node))!;

    // natural T? -> il2cpp Il2CppSystem.Nullable<T'> via the proxy ctor(T'). Container fabricates the empty Nullable
    // (EmptyNullable) for a null value, so `value` here is always PRESENT — the boxed inner value converted ToIl2Cpp
    // through the single child and bound to the ctor. UNLIKE the tuple/dict/list element writes, NO ref-bearing case
    // reaches here: System.Nullable<T> is `where T : struct`, and a ref-bearing il2cpp value type renders as a CLASS
    // proxy — so `System.Nullable<ref-bearing>` can be neither spelled nor type-loaded (the interop-patch flips such a
    // param to the BARE proxy T, dematerialized by ValueTypeBridge.RefToNullable, outside this Conv tree). The
    // IsRefBearingValueType check is thus an UNREACHABLE backstop: were the type system subverted, fail LOUD instead
    // of feeding the ctor bytes it would garble — never a silent mis-marshal.
    public static object DematerializeNullable(Conv node, object value, ConvDirection dir, IConvRuntime runtime)
    {
        object convertedInner = node.Children[0].Convert(value, dir, runtime)!;
        if (ValueTypeBridge.IsRefBearingValueType(convertedInner))
            throw new NotSupportedException($"ContainerBridge: Nullable inner is a ref-bearing value type ({convertedInner.GetType().FullName}) — UNREACHABLE (System.Nullable<T> requires T:struct; a ref-bearing value type is a CLASS proxy), kept as a P4 fail-loud backstop, never a silent mis-marshal.");
        return Activator.CreateInstance(Il2CppTypeOf(node), new[] { convertedInner })!;
    }

    // il2cpp ValueTuple<T1..Tn> -> natural (T1..Tn) : read Item1..ItemN (property or field), convert each through
    // node.Children[i], and construct the BCL ValueTuple of the spelled arity. This is the N-CHILD container node
    // — the shape a single threaded `inner` could not express; each element recurses through its own child.
    public static object MaterializeTuple(Conv node, object il2cppTuple, ConvDirection dir, IConvRuntime runtime)
    {
        Type t = il2cppTuple.GetType();
        int n = node.Children.Count;
        var items = new object?[n];
        var managed = new Type[n];
        for (int i = 0; i < n; i++)
        {
            object? raw = ReadItem(t, il2cppTuple, i + 1);
            items[i] = node.Children[i].Convert(raw, dir, runtime);
            managed[i] = node.Children[i].Managed;
        }
        return Activator.CreateInstance(TupleTypeOf(n).MakeGenericType(managed), items)!;
    }

    // natural (T1..Tn) -> il2cpp Il2CppSystem.ValueTuple<T1'..Tn'> via the proxy ctor(T1'..Tn'), each Item read off
    // the BCL tuple (field) and converted ToIl2Cpp through its child. Blittable/reference elements bind the ctor;
    // a ref-bearing value-type element is then overwritten with its correct unboxed bytes via ValueTypeBridge (below).
    public static object DematerializeTuple(Conv node, object managedTuple, ConvDirection dir, IConvRuntime runtime)
    {
        Type t = managedTuple.GetType();
        int n = node.Children.Count;
        var items = new object?[n];
        for (int i = 0; i < n; i++)
            items[i] = node.Children[i].Convert(ReadItem(t, managedTuple, i + 1), dir, runtime);
        object tuple = Activator.CreateInstance(Il2CppTypeOf(node), items)!;
        // Activator hands a ref-bearing value-type ELEMENT to the generic ctor as a boxed-proxy pointer where
        // the UNBOXED value bytes belong — the embedded string is dropped and the value reads garbage. Overwrite
        // each such Item field with its correct unboxed bytes through ValueTypeBridge (blittable/reference elements
        // the ctor already placed right are untouched). Verified in-game against ToyGame.Loadout.
        for (int i = 0; i < n; i++)
            if (ValueTypeBridge.IsRefBearingValueType(items[i]))
                ValueTypeBridge.WriteField(tuple, "Item" + (i + 1), items[i]!);
        return tuple;
    }

    // ── Result (the per-game reference-wrapper mirror) ──────────────────────────────────────────────────────────
    // Inutil.Bridge.Result<T> <-> the game's Comfort.Common.Result<T'> (element T recursing through node.Children[0]).
    // A hook spells Task<Inutil.Bridge.Result<Foo[]>> in place of Task<Result<Il2CppReferenceArray<Foo>>> (whose Foo[]
    // close is a broken il2cpp instantiation — measured); this bridges the mirror to/from the game Result at the seam,
    // then the usual Task path (DematerializeTask, its child = this Result node) carries it home. The game-specific
    // build/read is delegated to the per-game Mirror shim (Inutil.Bridge.Mirror.RegisterResult), so inutil holds no
    // game type — a ConvKind.Result node in the Conv tree.

    // il2cpp game Result<T'> -> natural Inutil.Bridge.Result<T> : read the game Result's parts through the shim, convert
    // its il2cpp value ToManaged through the single child, and build the mirror of the SPELLED element type.
    public static object? MaterializeResult(Conv node, object il2cppResult, ConvDirection dir, IConvRuntime runtime)
    {
        (object? valueIl2, string? error, int code) = ReadResult()(il2cppResult);
        object? value = node.Children[0].Convert(valueIl2, dir, runtime);
        Type mirror = typeof(Inutil.Bridge.Result<>).MakeGenericType(node.Children[0].Managed);
        return Activator.CreateInstance(mirror, value, error, code)!;
    }

    // natural Inutil.Bridge.Result<T> -> il2cpp game Result<T'> : read the mirror's parts (IMirrorResult), convert its
    // value ToIl2Cpp through the single child, and build the game Result via the shim over the CHILD's il2cpp type.
    public static object DematerializeResult(Conv node, object value, ConvDirection dir, IConvRuntime runtime)
    {
        var mr = (Inutil.Bridge.IMirrorResult)value;
        object? valueIl2 = node.Children[0].Convert(mr.BoxedValue, dir, runtime);
        Type elemIl2 = Il2CppTypeOf(node.Children[0]);
        return BuildResult()(elemIl2, valueIl2, mr.Error, mr.ErrorCode);
    }

    // The per-game Result shim (Inutil.Bridge.Mirror), resolved fail-loud: a hook that spells Inutil.Bridge.Result<T>
    // without the game shim registered (a missing [ModuleInitializer] Mirror.RegisterResult) surfaces a clear error
    // instead of an opaque NRE at the ctor. inutil never names the game Result type; these delegates supply it.
    static Type GameResultOpen()
        => Inutil.Bridge.Mirror.GameResultOpen ?? throw NoResultShim();
    static Func<Type, object?, string?, int, object> BuildResult()
        => Inutil.Bridge.Mirror.BuildResult ?? throw NoResultShim();
    static Func<object, (object? value, string? error, int code)> ReadResult()
        => Inutil.Bridge.Mirror.ReadResult ?? throw NoResultShim();
    static NotSupportedException NoResultShim() => new(
        "ContainerBridge: a hook spells Inutil.Bridge.Result<T> but no game Result was registered — call " +
        "Inutil.Bridge.Mirror.RegisterResult(typeof(GameResult<>), build, read) ONCE at startup (a [ModuleInitializer] " +
        "in a per-game shim), so inutil can build/read the game's value-proxy Result. Fail-loud, never a silent mis-marshal.");

    // ── Callback (the per-game reference-wrapper mirror, INBOUND twin of Result) ─────────────────────────────────
    // il2cpp game Callback<T'> -> natural Inutil.Bridge.Callback<T>. A hook can't RECEIVE a game callback as a
    // System.Action (no il2cpp->BCL delegate bridge) nor spell the broken Callback<T[]> close, so it spells the mirror
    // and gets one that HOLDS the game callback in a seam-installed invoker. On Ok/Fail the mirror hands the invoker an
    // Inutil.Bridge.Result<T>; the invoker flips the element ToIl2Cpp through the SAME child, builds the game Result via
    // the Result shim (a callback is invoked WITH a Result), and invokes the game callback via the per-game Callback
    // shim. Inbound-ONLY: no DematerializeCallback — a hook never RETURNS a callback (that fails loud in Il2CppConvRuntime).
    static readonly MethodInfo _makeCbInvoker =
        typeof(ContainerBridge).GetMethod(nameof(MakeCallbackInvoker), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static object MaterializeCallback(Conv node, object il2cppCallback, ConvDirection dir, IConvRuntime runtime)
    {
        Conv elem = node.Children[0];
        Type elemIl2 = Il2CppTypeOf(elem);                                        // the game Result's element type (in-game resolution)
        // Close the typed invoker over the SPELLED element T (the WrapTask pattern) so the mirror's public ctor
        // Callback(Action<Result<T>>) binds — the seam holds the non-generic marshalling; T only names the delegate.
        object invoker = _makeCbInvoker.MakeGenericMethod(elem.Managed)
            .Invoke(null, new object[] { elem, elemIl2, il2cppCallback, runtime })!;
        Type cbType = typeof(Inutil.Bridge.Callback<>).MakeGenericType(elem.Managed);
        return Activator.CreateInstance(cbType, invoker)!;
    }

    // The typed invoker (closed over T by MaterializeCallback): Result<T> -> flip element ToIl2Cpp -> build game Result
    // -> invoke the game callback. `res.Value` is the SPELLED T; the recursive child does the (possibly nested) flip.
    static object MakeCallbackInvoker<T>(Conv elem, Type elemIl2, object il2cppCallback, IConvRuntime runtime)
    {
        Action<Inutil.Bridge.Result<T>> a = res =>
        {
            object? valueIl2 = elem.Convert(res.Value, ConvDirection.ToIl2Cpp, runtime);
            object gameResult = BuildResult()(elemIl2, valueIl2, res.Error, res.ErrorCode);
            InvokeCallback()(il2cppCallback, gameResult);
        };
        return a;
    }

    // The per-game Callback shim (Inutil.Bridge.Mirror), resolved fail-loud — like the Result shim above. inutil never
    // names the game Callback type; RegisterCallback supplies how to invoke it. BuildResult (above) supplies the Result
    // it is invoked WITH, so a Callback shim needs the Result shim registered too (both from the same [ModuleInitializer]).
    static Type GameCallbackOpen()
        => Inutil.Bridge.Mirror.GameCallbackOpen ?? throw NoCallbackShim();
    static Action<object, object> InvokeCallback()
        => Inutil.Bridge.Mirror.InvokeCallback ?? throw NoCallbackShim();
    static NotSupportedException NoCallbackShim() => new(
        "ContainerBridge: a hook spells Inutil.Bridge.Callback<T> but no game Callback was registered — call " +
        "Inutil.Bridge.Mirror.RegisterCallback(typeof(GameCallback<>), invoke) ONCE at startup (a [ModuleInitializer] " +
        "in a per-game shim, alongside RegisterResult), so inutil can invoke the game's Callback with a game Result. Fail-loud, never a silent mis-marshal.");

    // ── DelegateIn (the inbound-delegate mirror, the GENERAL twin of Callback) ────────────────────────────────────
    // il2cpp game System.Action`N / Func`N (the interop proxy's natural delegate param) -> natural Inutil.Bridge.Action/Func.
    // A hook can't RECEIVE a game delegate as a System.Action (ConvKind.Delegate fails loud — the reverse bridge is call-
    // site-only) nor spell the broken close, so it spells the mirror and the seam hands it one that HOLDS the game delegate
    // (as Raw) plus a seam-installed invoker. UNLIKE Callback there is NO per-game shim: the il2cpp Action`N/Func`N proxies
    // live in Il2Cppmscorlib (game-agnostic), so the invoker calls their OWN Invoke directly through Il2CppInterop. Inbound-
    // ONLY: no DematerializeDelegate — a hook never RETURNS a delegate mirror (that fails loud in Il2CppConvRuntime).

    // il2cpp game delegate proxy -> natural Inutil.Bridge.Action/Func<T..> : build a game-agnostic invoker that flips each
    // arg ToIl2Cpp through its child, invokes the game delegate, and (for Func) flips the return ToManaged through the last
    // child; then construct the mirror over the SPELLED element/return types. `il2cppDelegate` already arrives as the closed
    // il2cpp Action`N/Func`N proxy (Il2CppMarshal.PointerToManaged wrapped the frame pointer via Il2CppTypeOf(node)).
    public static object MaterializeDelegate(Conv node, object il2cppDelegate, ConvDirection dir, IConvRuntime runtime)
    {
        bool isFunc = Inutil.Bridge.DelegateMirrors.IsFunc(node.Managed.GetGenericTypeDefinition());
        int argCount = isFunc ? node.Children.Count - 1 : node.Children.Count;   // a Func mirror's LAST child is the return
        Conv? retChild = isFunc ? node.Children[node.Children.Count - 1] : null;

        // The invoker the mirror hands its boxed SPELLED args: flip each arg ToIl2Cpp (recursive child) -> invoke the game
        // delegate -> (Func) flip the il2cpp return ToManaged (recursive last child). A plain System.Func the seam installs,
        // exactly as MakeCallbackInvoker installs a System.Action<Result<T>> — the mirror stays game-free.
        System.Func<object?[], object?> invoker = args =>
        {
            var il2Args = new object?[argCount];
            for (int i = 0; i < argCount; i++)
                il2Args[i] = node.Children[i].Convert(args[i], ConvDirection.ToIl2Cpp, runtime);
            object? retIl2 = InvokeIl2CppDelegate(node, il2cppDelegate, il2Args, isFunc);
            return isFunc ? retChild!.Convert(retIl2, ConvDirection.ToManaged, runtime) : null;
        };

        // node.Managed IS the closed mirror (Inutil.Bridge.Action<T..>/Func<T..,TR>), whose type args ARE the children's
        // spelled managed types — so it constructs directly; its public ctor is (object raw, System.Func<object?[],object?>).
        return Activator.CreateInstance(node.Managed, il2cppDelegate, invoker)!;
    }

    // Invoke the received game delegate with already-flipped il2cpp args, via the closed il2cpp Action`N/Func`N proxy's OWN
    // typed Invoke — game-agnostic (the delegate proxies are Il2Cppmscorlib, not a game assembly), so no per-game shim is
    // needed. The value arrives already wrapped as the closed proxy; re-wrap defensively (its Pointer) when a nested path
    // hands a base proxy. Reference args (the critical Callback<..> path) cross correctly; a ref-bearing value-type arg would
    // reach the reflective Invoke as a boxed-proxy pointer where the unboxed bytes belong — fail LOUD, never silent.
    static object? InvokeIl2CppDelegate(Conv node, object il2cppDelegate, object?[] il2Args, bool isFunc)
    {
        Type proxyType = Il2CppTypeOf(node);                                     // closed Il2CppSystem.Action`N / Func`N
        object proxy = proxyType.IsInstanceOfType(il2cppDelegate)
            ? il2cppDelegate
            : Activator.CreateInstance(proxyType, ((Il2CppObjectBase)il2cppDelegate).Pointer)!;

        int argCount = isFunc ? node.Children.Count - 1 : node.Children.Count;
        var argTypes = new Type[argCount];
        for (int i = 0; i < argCount; i++)
        {
            argTypes[i] = Il2CppTypeOf(node.Children[i]);
            if (ValueTypeBridge.IsRefBearingValueType(il2Args[i]))
                throw new NotSupportedException(
                    $"ContainerBridge: invoking a received game delegate ({node.Managed}) with a ref-bearing value-type arg "
                    + $"({il2Args[i]!.GetType().FullName}) is unsupported — the reflective delegate Invoke would mis-marshal its "
                    + "boxed bytes (§7.5). Reference args (the common case) and blittable value args work; fail-loud, never a silent mis-marshal.");
        }
        MethodInfo invoke = proxyType.GetMethod("Invoke", argTypes)
            ?? throw new NotSupportedException($"ContainerBridge: il2cpp delegate proxy {proxyType} has no Invoke({string.Join(", ", argTypes.Select(t => t.Name))}) — cannot invoke a received game delegate.");
        return invoke.Invoke(proxy, il2Args);
    }

    // Read Item{oneBased} off a ValueTuple by property (the il2cpp proxy exposes its fields AS properties) or by
    // field (a BCL ValueTuple's Item1..N are fields) — the two surfaces the spellings differ on.
    static object? ReadItem(Type t, object tuple, int oneBased)
    {
        string name = "Item" + oneBased;
        if (t.GetProperty(name) is { } p) return p.GetValue(tuple);
        if (t.GetField(name) is { } f) return f.GetValue(tuple);
        throw new NotSupportedException($"ContainerBridge: tuple {t.FullName} has no {name}");
    }

    static Type TupleTypeOf(int n) => n switch
    {
        1 => typeof(ValueTuple<>), 2 => typeof(ValueTuple<,>), 3 => typeof(ValueTuple<,,>),
        4 => typeof(ValueTuple<,,,>), 5 => typeof(ValueTuple<,,,,>), 6 => typeof(ValueTuple<,,,,,>),
        7 => typeof(ValueTuple<,,,,,,>),
        _ => throw new NotSupportedException($"ContainerBridge: ValueTuple arity {n} unsupported (max 7; 8+ needs TRest nesting)"),
    };

    // The ONE enumeration-strategy resolver, shared by every SEQUENCE container: prefer the BCL IEnumerable an
    // il2cpp ARRAY wrapper implements directly; else the Count/Length + indexer surface an il2cpp INDEXED collection
    // (List<T>, IReadOnlyList<T>) exposes; else the universal GetEnumerator/MoveNext/Current protocol a NON-indexed
    // collection (HashSet<T> — Count but no `this[int]`) exposes. Tried in that order, once, instead of each
    // container re-deriving its own fallback chain.
    static IEnumerable<object?> Enumerate(object il2cppSeq)
    {
        if (il2cppSeq is IEnumerable direct)
        {
            foreach (object? x in direct) yield return x;
            yield break;
        }

        Type t = il2cppSeq.GetType();
        PropertyInfo? count = t.GetProperty("Count") ?? t.GetProperty("Length");
        PropertyInfo? item = t.GetProperty("Item");
        if (count is not null && item?.GetGetMethod() is { } getItem)   // indexed (List) — positional read
        {
            int n = Convert.ToInt32(count.GetValue(il2cppSeq));
            Type idxType = getItem.GetParameters()[0].ParameterType;
            for (int i = 0; i < n; i++)
                yield return getItem.Invoke(il2cppSeq, new[] { Convert.ChangeType(i, idxType) });
            yield break;
        }

        // A non-indexed ICollection<T> whose in-place enumerator is unusable: an il2cpp HashSet<T> whose DIRECT struct
        // GetEnumerator IL2CPP stripped (the game never iterates it in-code), leaving only the explicit-interface
        // enumerator — which returns the Enumerator STRUCT boxed, and under Il2CppInterop's reflective struct-invoke
        // MoveNext reads a stale captured _version and throws a spurious "Collection was modified" against the set's
        // live _version. Read it the version-FREE way ICollection<T> guarantees: CopyTo into a fresh il2cpp array (no
        // _version field to check), which bridges to managed IEnumerable — the typed read the enumerator can't give.
        // A list took the indexer branch and a dictionary is pair-shaped (EnumeratePairs), so this is the set's path;
        // IterateRaw stays the last resort for a constructible sequence that exposes neither indexer nor CopyTo.
        if (TryCopyToArray(il2cppSeq, out object? copied))
        {
            foreach (object? x in (IEnumerable)copied!) yield return x;
            yield break;
        }

        foreach (object? x in IterateRaw(il2cppSeq)) yield return x;     // last resort — struct-enumerator protocol
    }

    // ICollection<T>'s version-free read: get_Count + CopyTo(Il2CppArrayBase<T'>, 0) into a fresh il2cpp array whose
    // wrapper matches the element — Il2CppStructArray for a value/blittable element (int, enum), Il2CppReferenceArray
    // for a game proxy, Il2CppStringArray for string. The array carries no _version, so it dodges the enumerator's
    // "Collection was modified" check entirely. Returns false (fall through to IterateRaw) when the collection is not
    // a generic ICollection exposing CopyTo(array, int) — not every constructible sequence is one.
    static bool TryCopyToArray(object coll, out object? array)
    {
        array = null;
        Type t = coll.GetType();
        if (!t.IsGenericType) return false;
        PropertyInfo? count = t.GetProperty("Count");
        // CopyTo(Il2CppArrayBase, int) — ICollection<T>.CopyTo (2-arg; the 1-arg and 3-arg HashSet overloads are the
        // other CopyTos). Suffix-match the name so an explicit-impl mangling on some collection still binds it.
        MethodInfo? copyTo = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name.EndsWith("CopyTo", StringComparison.Ordinal)
                && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(int));
        if (count is null || copyTo is null) return false;
        int n = Convert.ToInt32(count.GetValue(coll));
        Type elemT = t.GetGenericArguments()[0];                    // T' — the il2cpp element type
        Type wrapper =
            elemT == typeof(string)                          ? typeof(Il2CppStringArray) :
            typeof(Il2CppObjectBase).IsAssignableFrom(elemT) ? typeof(Il2CppReferenceArray<>).MakeGenericType(elemT) :
                                                               typeof(Il2CppStructArray<>).MakeGenericType(elemT);
        array = Activator.CreateInstance(wrapper, new object[] { (long)n })!;
        copyTo.Invoke(coll, new object[] { array, 0 });
        return true;
    }

    // The universal GetEnumerator/MoveNext/Current protocol every il2cpp collection proxy implements but does NOT
    // bridge to BCL IEnumerable — the ONE iterator both the sequence (Enumerate) and pair (EnumeratePairs) resolvers
    // fall back to (a List's indexer is a faster special case; a HashSet and a Dictionary have only this). Yields each
    // raw Current; the caller reads it as an element or splits it as a KeyValuePair.
    static IEnumerable<object?> IterateRaw(object collection)
    {
        Type t = collection.GetType();
        // The public GetEnumerator may be name-MANGLED to its explicit-interface-impl form when IL2CPP stripped the
        // plain one (an il2cpp HashSet whose GetEnumerator the game never calls in-code surfaces only
        // System_Collections_(Generic_)IEnumerable[_T]__GetEnumerator). Match any parameterless *GetEnumerator,
        // preferring the NON-generic one: Il2CppInterop's generic IEnumerator`1 proxy exposes only get_Current (T),
        // while the non-generic IEnumerator base carries MoveNext (+ get_Current as object) — the PAIR we need. A plain
        // "GetEnumerator" (il2cpp Dictionary, whose Enumerator has both) has no "Generic" in its name, so it wins too.
        MethodInfo getEnum = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetParameters().Length == 0 && m.Name.EndsWith("GetEnumerator", StringComparison.Ordinal))
            .OrderBy(m => m.Name.Contains("Generic"))
            .FirstOrDefault()
            ?? throw new NotSupportedException($"ContainerBridge: cannot enumerate {t.FullName} — no IEnumerable, no Count+indexer, no *GetEnumerator()");
        object en = getEnum.Invoke(collection, null)!;
        Type et = en.GetType();
        // MoveNext / Current may ALSO be explicit-impl-mangled on the enumerator — match by suffix, preferring the
        // GENERIC get_Current (returns T, not object) so an element comes back typed rather than boxed-as-object.
        MethodInfo? moveNext = et.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.GetParameters().Length == 0 && m.Name.EndsWith("MoveNext", StringComparison.Ordinal));
        MethodInfo? getCurrent = et.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetParameters().Length == 0 && m.Name.EndsWith("get_Current", StringComparison.Ordinal))
            .OrderByDescending(m => m.ReturnType != typeof(object))
            .FirstOrDefault();
        if (moveNext is null || getCurrent is null)
            throw new NotSupportedException($"ContainerBridge: enumerator {et.FullName} exposes no MoveNext/get_Current pair — cannot iterate this il2cpp collection.");
        while (true)
        {
            bool has; object? cur;
            try { has = (bool)moveNext.Invoke(en, null)!; cur = has ? getCurrent.Invoke(en, null) : null; }
            catch (TargetInvocationException tie)
            {
                // An il2cpp collection whose plain enumerator IL2CPP stripped surfaces only an EXPLICIT-IMPL enumerator
                // that throws "Collection was modified" under reflective MoveNext (a stale captured _version — an
                // Il2CppInterop struct-invoke quirk). A HashSet dodges this via the CopyTo array read (Enumerate,
                // above) BEFORE it reaches here, so this fires only for a NEW collection shape that exposes neither an
                // indexer nor CopyTo(array, int) — fail LOUD (P4) rather than silently drop elements.
                throw new NotSupportedException($"ContainerBridge: reflective enumeration of {t.FullName} failed ({tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}) — its il2cpp enumerator version-checks under reflective MoveNext and it exposes no indexer/CopyTo array read; needs the Il2CppInterop typed-enumeration path.");
            }
            if (!has) yield break;
            yield return cur;
        }
    }
}
