// The ergonomic hook base (L4 modder surface) — "make the modder feel like they are developing the game
// itself". A mod writes `class TallyHook : Hook<Game>` and names game methods directly as its own methods;
// Mods.Discover matches each by name + parameter types and installs it as ONE engine pre-hook running the
// AROUND adapter below. Inside a hook body: `Self` is the live receiver (typed T) and `Proceed(...)` runs the
// original method and returns its result.
//
// ENGINE-NATIVE, one chain. This rides the finished engine (Inutil.Hooks.Hooks): the body IS the method
// (REPLACES by default; its return value becomes the result), and `Proceed(...)` maps to the engine's
// HookContext.Proceed(), which runs the original UN-HOOKED (via the trampoline, no detour re-entry). So there
// is exactly one chain — the engine's Pre[] — and no second mechanism to keep in sync. Arg/return marshalling
// reuses the shared runtime marshaller where it applies.
using System.Reflection;
using Il2CppInterop.Runtime;                        // IL2CPP.Il2CppStringToManaged / ManagedStringToIl2Cpp
using Il2CppInterop.Runtime.InteropTypes;           // Il2CppObjectBase (.Pointer) + the (IntPtr) proxy ctor
using Inutil.Hooks;                                 // HookContext, Hooks.OnWarning
using Inutil.Marshal;                               // Il2CppMarshal — the ONE engine the container boundary routes through
// NB: the simple name `Marshal` binds to the sibling namespace Inutil.Marshal from inside `namespace Inutil`,
// so System.Runtime.InteropServices.Marshal is spelled global:: at each blittable read/write site below.

namespace Inutil;

// A mod's hook class extends this. T is the il2cpp proxy type to hook (Game, Player, …). Self/Proceed are
// static so they read identically from instance AND static hook methods: the around adapter populates a
// thread-local dispatch frame before invoking the body, and these read it.
public abstract class Hook<T> where T : class
{
    // The live receiver of the call being hooked, typed T — built from the engine frame's `this` each dispatch.
    // Null for a static game method (reading it there is a programmer error). The T cast always succeeds when T
    // matches the hooked method's declaring proxy.
    protected static T? Self => (T?)HookDispatch.CurrentSelf;

    // Run the ORIGINAL method now and return its (typed) result — the around `base.M(...)`. `args`: pass the
    // incoming values to run it unchanged, or new values to alter what it computes on. Maps to the engine's
    // HookContext.Proceed() (runs the original un-hooked). Because the body REPLACES the method by default, a
    // hook that wants the original to run MUST call Proceed — forgetting it means the original never runs
    // (loud-by-contract). ref/out params: a hook body may DECLARE them (matched + marshalled like the game
    // method), and their final values ARE the method's outputs. But `args` is by-VALUE — a params call can't
    // carry `ref` — so Proceed does NOT flow the original's ref/out mutations back into your locals; set the out
    // value yourself (replace), or use the raw HookContext tier to transform the original's.
    protected static R Proceed<R>(params object?[] args) => (R)HookDispatch.Proceed(typeof(R), args)!;
    protected static void Proceed(params object?[] args) => HookDispatch.Proceed(typeof(void), args);
}

// A wired hook method: the matched game method + how to marshal the native call frame <-> the hook body. Built
// once at Discover time (reflection resolved up front); re-used on every dispatch.
internal sealed unsafe class HookBinding
{
    public MethodInfo HookMethod = null!;           // the mod's method (invoked as the body)
    public object? Instance;                        // the hook-class instance, or null for a static hook method
    public Type ReceiverType = null!;               // T — the proxy type Self is built as
    public Type[] ParamTypes = null!;               // the hook method's managed parameter types
    public Type ReturnType = null!;                 // the hook method's return type
    public Type? GameReturnType;                    // the MATCHED game method's return type — Proceed<R> is validated against it
    public string Label = "";                       // "Game::Tally" — for warnings

    public bool IsStatic => HookMethod.IsStatic;

    // Build the typed receiver from the frame's `this` pointer (il2cpp proxies expose a (IntPtr) ctor).
    public object? MakeReceiver(nint thisPtr) => thisPtr == 0 ? null : Activator.CreateInstance(ReceiverType, (IntPtr)thisPtr);

    // Read the incoming args from the frame into typed managed values for the body.
    public object?[] ReadArgs(HookContext ctx)
    {
        var a = new object?[ParamTypes.Length];
        for (int i = 0; i < a.Length; i++) a[i] = ReadSlot(ctx, i, ParamTypes[i]);
        return a;
    }

    // Write the body's Proceed args back into the frame before running the original. Arity CONTRACT is
    // none-or-all: zero args = run the original on the incoming args unchanged (the forward idiom); exactly
    // ParamTypes.Length args = replace every slot. Anything between would write a silent PREFIX (mixing new and
    // stale arg slots), so it throws.
    public void WriteArgs(HookContext ctx, object?[] args)
    {
        if (args.Length == 0) return;                        // forward: the frame already holds the incoming args
        if (args.Length != ParamTypes.Length)
            throw new InvalidOperationException(
                $"Proceed for {Label}: got {args.Length} arg(s) but the method takes {ParamTypes.Length} — pass none " +
                $"(forward the incoming args unchanged) or all {ParamTypes.Length} (a partial write would silently mix " +
                $"new and stale argument slots).");
        for (int i = 0; i < ParamTypes.Length; i++) WriteArgSlot(ctx, i, ParamTypes[i], args[i]);
    }

    // Write the body's ref/out parameters back to the frame AFTER the body returns (the REPLACE writeback). A
    // ref/out param is a METHOD OUTPUT: reflection copies each ref/out local back into `args` on return, and —
    // because "the body IS the method" — those final values ARE the method's out values, stored into the caller's
    // ref/out targets as the real method would (barriered where the destination is heap-backed + ref-bearing, via
    // WriteArgSlot -> ArgWritesBarriered). Only by-ref params are written; by-value args are pure inputs. NB
    // Proceed() passes ref/out BY VALUE and does NOT import the original's ref/out mutations back into the body's
    // locals — so a hook that must TRANSFORM the original's ref/out output (rather than replace it) drops to the
    // raw HookContext tier (Arg<T>/SetArg<T>), where the frame slot is addressable directly. The one documented
    // ergonomic-tier boundary (a semantic limit, never a silent mis-marshal): the body's ref/out locals win.
    public void WriteRefOutArgs(HookContext ctx, object?[] args)
    {
        for (int i = 0; i < ParamTypes.Length && i < args.Length; i++)
            if (ParamTypes[i].IsByRef) WriteArgSlot(ctx, i, ParamTypes[i], args[i]);
    }

    public object? ReadReturn(HookContext ctx, Type rt) => ReadReturnSlot(ctx, rt);
    public void WriteReturn(HookContext ctx, object? value) => WriteReturnSlot(ctx, ReturnType, value);

    // ── per-slot marshalling ────────────────────────────────────────────────────────────────────────────────
    // The frame ABI is Layer A (a native slot <-> an il2cpp representation) — only the hook boundary owns it. Each
    // slot dispatches by how the il2cpp ABI carries the type, then hands Layer B (the natural<->il2cpp CONVERSION)
    // to the SHARED engine (Il2CppMarshal) — never a second marshaller; container/Task/Nullable/Tuple logic lives
    // ONLY in the engine. The kinds:
    //   * string / blittable value type / reference proxy — Layer B is IDENTITY or an ABI leaf; the fast paths
    //     read/write the frame directly (a blittable struct's bytes, an il2cpp string ptr, an object ptr).
    //   * VALUE passed BY VALUE (its bytes AT the slot): a Nullable<T>/ValueTuple (element-aware conversion) OR a
    //     ref-bearing game value struct (Loadout) the engine treats as a leaf — box the bytes into the value proxy,
    //     run the engine, unbox back (Il2CppMarshal.FrameValue* over ValueTypeBridge). A naive blit would put
    //     il2cpp pointers where CLR refs belong.
    //   * REFERENCE-kind container (arrays, List/IReadOnlyList/IEnumerable/HashSet, Dictionary, Task) — passed as an
    //     object POINTER; recover the object from the slot, run the engine (Il2CppMarshal.PointerToManaged / ToIl2Cpp).
    //   * ref/out param — the frame slot (Loc.Ind) HOLDS a pointer to the value; ctx.ArgPointer/ArgObject already
    //     deref it, so a ref/out READS/WRITES like its by-value ELEMENT (peel the ByRef, reuse the kinds above). On
    //     WRITE it needs the il2cpp GC write barrier for a ref-bearing destination (see WriteArgSlot).
    // Anything the engine can't marshal fails LOUD from the engine (Conv.Build / correspondence), never here — an
    // unknown shape surfaces a clear error, not a wild pointer read.
    private static bool IsProxy(Type t) => typeof(Il2CppObjectBase).IsAssignableFrom(t);

    // Needs the box/unbox frame bridge: a naturally-spelled value-type container (int?, (a,b)) OR a ref-bearing
    // game value struct (Il2CppSystem.ValueType-derived, e.g. Loadout). Blittable structs (Vec3) and enums
    // (Faction) are NOT this — they marshal correctly through PtrToStructure.
    private static bool NeedsValueFrameBridge(Type t)
        => Nullable.GetUnderlyingType(t) is not null
           || (t.IsGenericType && t.FullName is { } fn && fn.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
           || ValueTypeBridge.IsRefBearingValueProxy(t);

    private object? ReadSlot(HookContext ctx, int i, Type t)
    {
        if (t.IsByRef) t = t.GetElementType()!;   // ref/out: ArgPointer/ArgObject already deref'd the Loc.Ind slot -> read its element
        if (t == typeof(string)) { nint p = ctx.ArgObject(i); return p == 0 ? null : IL2CPP.Il2CppStringToManaged(p); }
        if (NeedsValueFrameBridge(t)) return Il2CppMarshal.FrameValueToManaged((nint)ctx.ArgPointer(i), t);   // by-value: box -> engine
        if (t.IsValueType)            return ReadBlittable((nint)ctx.ArgPointer(i), t);
        if (IsProxy(t)) { nint p = ctx.ArgObject(i); return p == 0 ? null : Activator.CreateInstance(t, (IntPtr)p); }
        return Il2CppMarshal.PointerToManaged(ctx.ArgObject(i), t);   // reference-kind container -> the shared engine
    }

    // Read/write a leaf VALUE type's bytes at a frame slot. Marshal.PtrToStructure/StructureToPtr reject `bool`,
    // `char`, and ENUM types outright ("must be blittable or have layout information") — so a hook parameter/return
    // of one of those (e.g. ESessionMode, `bool`) crashed the ergonomic dispatch. Handle those three by their
    // underlying blittable representation (bool = 1 byte, char = int16, enum = its underlying primitive), and defer
    // a genuine layout struct (Vec3, MongoID) to Marshal. Mirrors the raw HookContext tier's `*(T*)ptr`.
    private static object ReadBlittable(nint p, Type t)
    {
        if (t == typeof(bool)) return global::System.Runtime.InteropServices.Marshal.ReadByte((IntPtr)p) != 0;
        if (t == typeof(char)) return (char)global::System.Runtime.InteropServices.Marshal.ReadInt16((IntPtr)p);
        if (t.IsEnum) return Enum.ToObject(t, global::System.Runtime.InteropServices.Marshal.PtrToStructure((IntPtr)p, Enum.GetUnderlyingType(t))!);
        return global::System.Runtime.InteropServices.Marshal.PtrToStructure((IntPtr)p, t)!;   // a real layout struct
    }

    private static void WriteBlittable(nint p, Type t, object v)
    {
        if (t == typeof(bool)) { global::System.Runtime.InteropServices.Marshal.WriteByte((IntPtr)p, (byte)((bool)v ? 1 : 0)); return; }
        if (t == typeof(char)) { global::System.Runtime.InteropServices.Marshal.WriteInt16((IntPtr)p, (short)(char)v); return; }
        if (t.IsEnum) { global::System.Runtime.InteropServices.Marshal.StructureToPtr(Convert.ChangeType(v, Enum.GetUnderlyingType(t)), (IntPtr)p, false); return; }
        global::System.Runtime.InteropServices.Marshal.StructureToPtr(v, (IntPtr)p, false);
    }

    private void WriteArgSlot(HookContext ctx, int i, Type t, object? v)
    {
        // A ref/out destination carrying object references (into possibly-live heap) must be stored THROUGH the
        // il2cpp GC write barrier. ctx.SetArg*(string/object) already do this; the only path that bypasses ctx is
        // the value-frame write, so thread the flag there. A by-value slot and a blittable ref (ref Vec3, no refs)
        // both report false -> a raw store.
        bool barriered = t.IsByRef && ctx.ArgWritesBarriered(i);
        if (t.IsByRef) t = t.GetElementType()!;   // ref/out: write the element through the (already-deref'd) slot
        if (t == typeof(string)) { if (v is null) ctx.SetArgObject(i, 0); else ctx.SetArgString(i, (string)v); return; }
        if (NeedsValueFrameBridge(t)) { Il2CppMarshal.ManagedToFrameValue(v, t, (nint)ctx.ArgPointer(i), barriered); return; }
        if (t.IsValueType)
        {
            // A boxed value of the WRONG type here would be a silent bit-reinterpretation (StructureToPtr writes
            // the box's own layout: an int box for a float slot writes int bits into XMM territory). Refuse it
            // loudly. The one sanctioned mismatch is an enum slot fed its underlying primitive (WriteBlittable's
            // enum arm Convert.ChangeTypes that).
            if (v is null)
                throw new InvalidOperationException($"Proceed/ref-out for {Label}: arg {i} is null but '{t.Name}' is a value type.");
            Type vt = v.GetType();
            if (vt != t && !(t.IsEnum && vt == Enum.GetUnderlyingType(t)))
                throw new InvalidOperationException(
                    $"Proceed/ref-out for {Label}: arg {i} is a '{vt.Name}' but the parameter is '{t.Name}' — refusing " +
                    $"the raw bit-reinterpretation; pass the exact type (e.g. 0f not 0, or an explicit cast).");
            WriteBlittable((nint)ctx.ArgPointer(i), t, v);
            return;
        }
        if (IsProxy(t)) { ctx.SetArgObject(i, v is null ? 0 : ((Il2CppObjectBase)v).Pointer); return; }
        object? il2 = Il2CppMarshal.ToIl2Cpp(v, t);                   // natural container -> il2cpp proxy (shared engine)
        ctx.SetArgObject(i, il2 is null ? 0 : ((Il2CppObjectBase)il2).Pointer);
    }

    private object? ReadReturnSlot(HookContext ctx, Type t)
    {
        if (t == typeof(void))   return null;
        if (t == typeof(string)) { nint p = ctx.ReturnObject(); return p == 0 ? null : IL2CPP.Il2CppStringToManaged(p); }
        if (NeedsValueFrameBridge(t)) return Il2CppMarshal.FrameValueToManaged((nint)ctx.ReturnPointer(), t);
        if (t.IsValueType)            return ReadBlittable((nint)ctx.ReturnPointer(), t);
        if (IsProxy(t)) { nint p = ctx.ReturnObject(); return p == 0 ? null : Activator.CreateInstance(t, (IntPtr)p); }
        return Il2CppMarshal.PointerToManaged(ctx.ReturnObject(), t); // reference-kind container -> the shared engine
    }

    private void WriteReturnSlot(HookContext ctx, Type t, object? v)
    {
        if (t == typeof(void))   return;
        if (t == typeof(string)) { if (v is null) *(nint*)ctx.ReturnPointer() = 0; else ctx.SetReturnString((string)v); return; }
        if (NeedsValueFrameBridge(t)) { Il2CppMarshal.ManagedToFrameValue(v, t, (nint)ctx.ReturnPointer()); return; }
        if (t.IsValueType)            { WriteBlittable((nint)ctx.ReturnPointer(), t, v!); return; }
        if (IsProxy(t)) { *(nint*)ctx.ReturnPointer() = v is null ? 0 : ((Il2CppObjectBase)v).Pointer; return; }
        object? il2 = Il2CppMarshal.ToIl2Cpp(v, t);                   // natural container -> il2cpp proxy (shared engine)
        *(nint*)ctx.ReturnPointer() = il2 is null ? 0 : ((Il2CppObjectBase)il2).Pointer;
    }
}

// The thread-local dispatch frame the around adapter sets up before invoking a hook body, so Self/Proceed read
// identically from any static/instance body. Re-entry is synchronous on one stack, so a single saved/restored
// slot suffices (onion nesting: a hook body that provokes another hooked call saves+restores around it).
internal static unsafe class HookDispatch
{
    [ThreadStatic] private static object? _self;
    [ThreadStatic] private static HookContext _ctx;      // the frame is valid for the SYNCHRONOUS dispatch only
    [ThreadStatic] private static HookBinding? _binding;

    internal static object? CurrentSelf => _self;

    // The engine callback installed per hook method (Discover binds one of these into Hooks.Pre). Runs the
    // body as the method: read args -> invoke -> the body's return becomes the result (Skip stops the original
    // auto-running). Exception-isolated to Hooks.OnWarning so one throwing hook never crashes the dispatch.
    internal static void Around(HookBinding b, HookContext ctx)
    {
        object? s0 = _self; HookContext c0 = _ctx; HookBinding? b0 = _binding;
        try
        {
            _binding = b;
            _ctx = ctx;
            _self = b.IsStatic ? null : b.MakeReceiver(ctx.ThisPtr);
            object?[] args = b.ReadArgs(ctx);
            object? result = b.HookMethod.Invoke(b.Instance, args);
            ctx.Skip();                                  // the body IS the method now — don't also auto-run the original
            b.WriteReturn(ctx, result);
            b.WriteRefOutArgs(ctx, args);                // ref/out params are the replaced method's outputs -> the caller's storage
        }
        catch (TargetInvocationException tie)
        {
            var inner = tie.InnerException ?? tie;
            Hooks.Hooks.OnWarning?.Invoke($"inutil hook {b.Label} threw {inner.GetType().Name}: {inner.Message}");
        }
        catch (Exception ex)
        {
            Hooks.Hooks.OnWarning?.Invoke($"inutil hook {b.Label} dispatch error: {ex.GetType().Name}: {ex.Message}");
        }
        finally { _self = s0; _ctx = c0; _binding = b0; }
    }

    // Hook<T>.Proceed(...) routes here: write the args into the current frame, run the original un-hooked, read
    // its (typed) result back. Only valid from inside a hook body (the frame is thread-local to the dispatch).
    internal static object? Proceed(Type returnType, object?[]? args)
    {
        HookBinding b = _binding ?? throw new InvalidOperationException(
            "Hook.Proceed(...) called outside a hook body — it can only run the original method DURING a dispatch.");
        // C# binds a literal `Proceed<R>(null)` to a NULL params array (not a one-null-arg array) — treat it as
        // the forward idiom; a single null argument must be passed as `new object?[] { null }`.
        args ??= Array.Empty<object?>();
        // R must be validated: Proceed<int>() on a float-returning method would read the return slot's raw bytes
        // as int, and a wrong proxy R would construct a wrong-typed proxy over the pointer — both silent. R must
        // be the hook's declared return, the game method's return, or a proxy base of it.
        if (returnType != typeof(void) && !ProceedReturnOk(b, returnType))
            throw new InvalidOperationException(
                $"Proceed<{returnType.Name}> for {b.Label}: the game method returns '{b.GameReturnType!.Name}' and the " +
                $"hook declares '{b.ReturnType.Name}' — reading the frame as '{returnType.Name}' would reinterpret raw " +
                $"bytes/pointers. Use the declared (or the game) return type.");
        b.WriteArgs(_ctx, args);
        _ctx.Proceed();                                  // runs the original un-hooked; leaves its return in the frame
        // The body has taken control of running the original: from here on the engine must NEVER auto-run it a
        // second time — including when the return-marshal below throws. Without this Skip, a throw after Proceed
        // left the skip cell unset and the dispatcher re-ran a side-effecting original (the double-run seam); the
        // frame already holds the original's return, the right degraded result.
        _ctx.Skip();
        return returnType == typeof(void) ? null : b.ReadReturn(_ctx, returnType);
    }

    // The R contract for Proceed<R>: the hook's declared return (the usual spelling — marshal-consistent with the
    // frame by construction), the matched game method's return (its proxy spelling), or a proxy BASE of the game
    // return (reading a derived proxy through a base handle is well-defined). A null GameReturnType (a binding
    // built outside Discover) skips validation rather than inventing a guess.
    static bool ProceedReturnOk(HookBinding b, Type r)
        => b.GameReturnType is null
           || r == b.ReturnType
           || r == b.GameReturnType
           || (typeof(Il2CppObjectBase).IsAssignableFrom(r) && r.IsAssignableFrom(b.GameReturnType));
}
