using System.Collections.Concurrent;
using System.Reflection;
using Il2CppInterop.Runtime;                  // IL2CPP.il2cpp_* + RuntimeSpecificsStore
using Il2CppInterop.Runtime.InteropTypes;     // Il2CppObjectBase
using Il2CppInterop.Runtime.Runtime;          // Il2CppObjectPool
using Inutil.Schema;                          // ExactTypeMap — the ONE map format, shared with the patch seam

namespace Inutil.Marshal;

// THE materialization primitive: an il2cpp object pointer -> a proxy typed at the object's ACTUAL il2cpp class.
//
// THE LEAK THIS CLOSES (docs/reference/exact-proxy-types.md). Il2CppInterop materializes at the DECLARED type of
// the seam you read through: a Boss read through an Entity-typed property arrives as an Entity proxy, so `is Boss`
// is false and a `switch` over subtypes matches nothing — silently, and asymmetrically (code that constructs its
// own objects sees `is`/`switch` work perfectly until it is handed one from the game). Natural typing already makes
// a proxy's SIGNATURES speak BCL; this makes its IDENTITY speak the game.
//
// ONE PRIMITIVE, EVERY SITE. Materializing a proxy from a pointer happens in more places than the generated
// proxies: the hook boundary builds `Self`, its reference args and its return from raw frame pointers; the
// marshaller recovers an object at a Conv leaf; the by-name reach faces (Invoke/Safe) return one. Fixing only the
// proxies would leave a mod's OWN hook receiving a base-typed `this` — the same bug through a different door. So
// every one of those routes through here, and the offline check `ExactTypeSitesTests` fails if a new site
// hand-rolls a proxy from a pointer instead.
//
// WHY TWO CONSTRUCTION SHAPES. `Get<T>` is what the IL-rewrite seam splices over Il2CppObjectPool.Get<T>, so it
// keeps the POOLED semantics it replaced (a pointer's wrapper is cached and reused). `Materialize` serves the
// sites that always built their wrapper directly through the proxy's (IntPtr) ctor, and keeps doing that. Only the
// TYPE changes at those sites — the invariant here is about type identity, not about wrapper lifetime, and
// quietly re-homing five call sites onto the pool would be a second, unrelated behaviour change riding along.
//
// FAIL-SAFE, NOT FAIL-LOUD — deliberately, and this is the one place in inutil where that is the right call. Every
// unresolvable case (no map, an unmapped class, a generic instantiation, an injected type, a class whose proxy
// assembly is not loaded) falls back to EXACTLY today's behaviour: materialize at the declared type. There is no
// mis-marshal to protect against — the fallback is the status quo the whole codebase was written against — and a
// throw (or a warning) on the hottest path in interop would turn a precision improvement into an outage. What
// would be silent-and-wrong is resolving to the WRONG type, and that cannot happen: a resolution is used only when
// the resolved type is assignable to the declared one, so a mis-keyed row is rejected rather than materialized.
// `Stats` is how "is exact typing actually working here?" stays answerable without a log line per read.
public static class Il2CppObjects
{
    // ── the two entry points ────────────────────────────────────────────────────────────────────────────────

    // The IL-rewrite seam's splice target — signature-identical to Il2CppInterop's Il2CppObjectPool.Get<T>, which
    // is what lets PoolRetargetRewriter swap the declaring type and change nothing else. Resolve the object's real
    // class; if it names a proxy assignable to T, materialize THAT through the pool; otherwise delegate to the
    // original, whose injected-type branch, null handling and cache stay authoritative (never re-implemented here).
    public static T Get<T>(IntPtr ptr)
    {
        Type? exact = ExactTypeOf(typeof(T), ptr);
        return exact is null ? Il2CppObjectPool.Get<T>(ptr) : (T)PoolGet(exact)(ptr);
    }

    // The SECOND splice target. A read whose element type is a generic PARAMETER (`List<T>::get_Item`, an array
    // indexer) never touches the pool directly — it calls Il2CppInterop's PointerToValueGeneric, which reaches the
    // pool one frame deeper, inside Il2CppInterop.Runtime where our patch does not reach. Retargeting only the pool
    // left every container ELEMENT base-typed while every property seam looked fixed.
    //
    // DELEGATE, THEN REFINE — never re-implement. The original owns a pile of shape rules (box a value type, deref
    // a ref slot, the System.String special case, the null case) and getting any of them wrong is a wild memory
    // read, not a wrong type. So the original runs exactly as before and its result is refined only when it is a
    // reference proxy we can name more precisely. The extra wrapper the base case allocated is not wasted: the
    // exact one replaces it in the pool's cache, so every later read of that pointer reuses the precise wrapper.
    public static T PointerToValueGeneric<T>(IntPtr objectPointer, bool valueType, bool refType)
    {
        T value = IL2CPP.PointerToValueGeneric<T>(objectPointer, valueType, refType);
        if (value is not Il2CppObjectBase proxy) return value;                 // a string, a struct, a null — not ours
        try
        {
            IntPtr p = proxy.Pointer;
            Type? exact = ExactTypeOf(typeof(T), p);
            return exact is null ? value : (T)PoolGet(exact)(p)!;
        }
        catch (ObjectCollectedException) { return value; }                     // collected mid-read — the original's answer stands
    }

    // The non-generic face for every site that holds a raw pointer plus a declared proxy Type — the hook frame's
    // receiver/args/return, the marshaller's leaf recovery. Builds through the proxy's (IntPtr) ctor, as those
    // sites always did; only the type it constructs can now be the derived one.
    public static object? Materialize(Type declared, nint ptr)
    {
        if (ptr == 0) return null;
        Type t = ExactTypeOf(declared, (IntPtr)ptr) ?? declared;
        return Activator.CreateInstance(t, (IntPtr)ptr)!;
    }

    // Wrap a pointer at EXACTLY `type`, with no resolution at all — for the caller that MINTED the object and so
    // already knows its class better than any lookup could. `ValueTypeBridge` builds its own boxes
    // (`il2cpp_object_new(ValueClassOf(t))`) and hands them straight back as that proxy; asking "what is this
    // really?" there is pure cost, and the answer can even DISAGREE with the wrapper on purpose — il2cpp's
    // ref-bearing Nullable is boxed as its INNER value, so the box's class is `Loadout` while the proxy is
    // deliberately `Nullable<Loadout>`, and the bridge reads it structurally by field name.
    //
    // This is the ONE sanctioned exemption from resolution, and it is narrow BY CONTRACT: "I created this object."
    // It is not an escape hatch for a site that merely holds a pointer from the game — that site cannot know the
    // class, which is the entire premise of this file. Keeping it here (rather than letting such callers keep
    // hand-rolling Activator.CreateInstance) is what keeps the site invariant checkable at all.
    public static object MaterializeAs(Type type, nint ptr) => Activator.CreateInstance(type, (IntPtr)ptr)!;

    // The resolution itself, exposed so a caller that already has a proxy (or wants to reason about a pointer
    // without building anything) can ask the same question the two entries ask. Null means "no better answer than
    // `declared`" — including when the object IS exactly `declared`.
    public static Type? ExactTypeOf(Type declared, IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return null;
        var map = Map(declared);
        if (map is null) return null;

        IntPtr cls = IL2CPP.il2cpp_object_get_class(ptr);
        if (cls == IntPtr.Zero) return null;

        Type? exact = ProxyForClass(cls, map);
        if (exact is null || exact == declared) { Interlocked.Increment(ref _declared); return null; }
        // The safety net that makes a wrong map row harmless: a resolution is only ever USED when it is a subtype
        // of what the seam promised. A caller's `(Entity)` cast, a field store, a vtable slot — all still hold.
        if (!declared.IsAssignableFrom(exact)) { Reject(declared, exact, cls); return null; }
        Interlocked.Increment(ref _exact);
        return exact;
    }

    // ── class pointer -> proxy type ─────────────────────────────────────────────────────────────────────────

    static readonly ConcurrentDictionary<IntPtr, Type?> _byClass = new();

    // Resolve ONE native class to its generated proxy type, memoized on the class pointer (native class objects
    // live for the process, so the pointer is a stable key). The key is rebuilt from the runtime exactly as the
    // patcher wrote it: the image name, the namespace of the OUTERMOST type, and the declaring-type chain of names.
    static Type? ProxyForClass(IntPtr cls, Dictionary<string, (string Assembly, string Type)> map)
    {
        if (_byClass.TryGetValue(cls, out Type? hit)) return hit;

        // An INJECTED class (a managed type injected into il2cpp) is not a generated proxy at all, and the original
        // pool has a dedicated branch for it — leave it entirely alone.
        if (RuntimeSpecificsStore.IsInjected(cls)) return _byClass[cls] = null;

        string? name = IL2CPP.il2cpp_class_get_name_(cls);
        if (string.IsNullOrEmpty(name)) return _byClass[cls] = null;

        IntPtr outer = cls;
        for (int depth = 0; depth < 16; depth++)
        {
            IntPtr parent = IL2CPP.il2cpp_class_get_declaring_type(outer);
            if (parent == IntPtr.Zero) break;
            string? pn = IL2CPP.il2cpp_class_get_name_(parent);
            if (string.IsNullOrEmpty(pn)) return _byClass[cls] = null;
            name = pn + "/" + name;
            outer = parent;
        }

        string ns = IL2CPP.il2cpp_class_get_namespace_(outer) ?? "";
        string image = IL2CPP.il2cpp_image_get_name_(IL2CPP.il2cpp_class_get_image(cls)) ?? "";
        if (!map.TryGetValue(ExactTypeMap.KeyOf(image, ns, name!), out var row)) return _byClass[cls] = null;

        Type? t = FindProxyType(row.Assembly, row.Type);
        // A row whose proxy assembly is not loaded YET is not cached: interop assemblies load lazily, and a
        // permanent negative recorded during boot would keep a type base-typed for the rest of the process.
        if (t is null) return null;
        if (t.ContainsGenericParameters) return _byClass[cls] = null;   // an open definition is not materializable
        return _byClass[cls] = t;
    }

    static Type? FindProxyType(string assemblyName, string typeName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, assemblyName, StringComparison.Ordinal)) continue;
            try { return asm.GetType(typeName, throwOnError: false); }
            catch { return null; }
        }
        return null;
    }

    // ── the map ─────────────────────────────────────────────────────────────────────────────────────────────

    static readonly object _gate = new();
    static Dictionary<string, (string Assembly, string Type)>? _map;
    static string? _dir;
    static bool _loaded;

    // The interop directory, injected by the loader shim (which is the one place that knows the loader's layout).
    // Setting it after the map has already been located re-reads it — how a consumer that regenerates proxies
    // mid-process gets the new map rather than a stale one.
    public static void UseInteropDir(string dir)
    {
        lock (_gate) { _dir = dir; _loaded = false; _map = null; _byClass.Clear(); }
    }

    // Locate + load the map ONCE. If the shim has not injected a directory yet — patched proxy bodies can run
    // before any plugin's Attach — fall back to the directory the DECLARED type's own assembly was loaded from,
    // which for a generated proxy IS the interop directory. A failed location is not remembered, so the next call
    // (with a located type, or after the shim runs) still gets its chance.
    static Dictionary<string, (string Assembly, string Type)>? Map(Type declared)
    {
        if (_loaded) return _map;
        lock (_gate)
        {
            if (_loaded) return _map;
            string? dir = _dir ?? DirectoryOf(declared);
            if (dir is null) return null;
            _map = ExactTypeMap.Read(dir);
            _loaded = true;
            return _map;
        }
    }

    static string? DirectoryOf(Type t)
    {
        try
        {
            string loc = t.Assembly.Location;
            return string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
        }
        catch { return null; }
    }

    // ── pooled construction of a runtime-chosen type ────────────────────────────────────────────────────────

    static readonly ConcurrentDictionary<Type, Func<IntPtr, object>> _poolGets = new();

    // `Il2CppObjectPool.Get<exact>` as a delegate, built once per type. Going through the ORIGINAL pool rather
    // than a ctor is the point: the exact wrapper then lands in the pool's own cache, so every later read of that
    // pointer — including a base-typed one, which now satisfies its `cachedObject is T` test — reuses it. Where
    // today a base-typed first read PINS the base proxy for the pointer's lifetime, this makes the cache strictly
    // more effective.
    static Func<IntPtr, object> PoolGet(Type proxy) => _poolGets.GetOrAdd(proxy, static t =>
    {
        MethodInfo mi = typeof(Il2CppObjectPool).GetMethod(nameof(Il2CppObjectPool.Get))!.MakeGenericMethod(t);
        // Reference-type return covariance: Func<IntPtr, object> binds Get<T>'s T directly, no reflection per call.
        try { return (Func<IntPtr, object>)mi.CreateDelegate(typeof(Func<IntPtr, object>)); }
        catch { return p => mi.Invoke(null, new object[] { p })!; }
    });

    // ── diagnostics ─────────────────────────────────────────────────────────────────────────────────────────

    static long _exact, _declared, _rejected;
    static readonly List<string> _rejections = new();   // capped samples — a count alone cannot be investigated

    // A resolution the assignability guard refused. Counted AND named (the first few), because "4 rejections" is a
    // number nobody can act on while "declared Il2CppSystem.Object, resolved Il2CppSystem.String" points straight
    // at the row. Capped so a systematic mismatch cannot grow without bound on a hot path.
    static void Reject(Type declared, Type exact, IntPtr cls)
    {
        Interlocked.Increment(ref _rejected);
        lock (_rejections)
            if (_rejections.Count < 8)
            {
                // The caller is the useful half: a rejection says "this seam's declared type is not the truth about
                // the object behind it", and only the frame that asked can say whether that is a bad map row or a
                // seam that was never honest (il2cpp boxes a ref-bearing Nullable AS its inner value, for one).
                // Captured without file info, and only for the first few, so it stays affordable.
                var st = new System.Diagnostics.StackTrace(2, false);
                string via = string.Join(" <- ", Enumerable.Range(0, Math.Min(3, st.FrameCount))
                    .Select(f => st.GetFrame(f)?.GetMethod() is { } mi ? $"{mi.DeclaringType?.Name}.{mi.Name}" : "?"));
                string pair = $"declared {declared.Name} <- class '{IL2CPP.il2cpp_class_get_name_(cls)}' -> {exact.Name}  via {via}";
                if (!_rejections.Contains(pair)) _rejections.Add(pair);
            }
    }

    // The refused resolutions, for a diagnostic that has to say WHICH. Empty is the expected state.
    public static IReadOnlyList<string> Rejections { get { lock (_rejections) return _rejections.ToArray(); } }

    // How exact typing is actually behaving in this process — the observable substitute for a log line per
    // materialization. `Exact` counts reads that produced a more precise type than the seam declared; `Declared`
    // reads with nothing better to offer (the common, correct case: the object IS the declared type); `Rejected`
    // resolutions refused by the assignability guard, which should be ZERO and is the number to investigate.
    // `MapSize` is 0 when no map was found at all — i.e. exact typing is off for this tree.
    public static (long Exact, long Declared, long Rejected, int MapSize) Stats
        => (Interlocked.Read(ref _exact), Interlocked.Read(ref _declared), Interlocked.Read(ref _rejected), _map?.Count ?? 0);

    // Is a type one of the generated il2cpp proxies (the shape both entries above materialize)? Shared so the hook
    // boundary and the marshaller ask this ONE way.
    public static bool IsProxy(Type t) => typeof(Il2CppObjectBase).IsAssignableFrom(t);
}
