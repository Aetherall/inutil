using System.Runtime.CompilerServices;      // Unsafe.Read/SizeOf/WriteUnaligned — copy a boxed value payload into a T? with no `unmanaged` constraint
using System.Runtime.InteropServices;       // GCHandle — pin a managed primitive so its data address survives an invoke
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;   // Il2CppObjectBase — every il2cpp proxy's boxed pointer

namespace Inutil.Marshal;

// The ONE value-type bridge. A ref-bearing il2cpp value type (a struct with a reference field — Loadout {int Gold;
// string Owner}) crossing into a container field slot must have its UNBOXED value bytes written through the GC write
// barrier — NOT the boxed-proxy pointer Il2CppInterop's generic ctor/set_Item hands over. Verified in-game: the naive
// Activator path drops the string (Owner "") and reads a garbage Gold. Centralized here so every value-type write
// site (the container bridge's element writes, a future Fields / Task-completion / Mirror site) calls it, not a re-derivation.
public static unsafe class ValueTypeBridge
{
    // THE single discriminator (C6) for "a ref-bearing il2cpp value type" — a struct-with-references Il2CppInterop
    // renders as a CLASS deriving Il2CppSystem.ValueType (so it mis-marshals through a generic ctor/set_Item/Add).
    // Il2CppConvShapeSource (leaf classification) and Hooks (ABI value-type sizing) BOTH call this instead of
    // re-spelling "Il2CppSystem.ValueType" — one invariant, one implementation (the string lives in exactly this
    // file; RegistryDriftTest enforces that). A blittable struct (Vec3) is a real value proxy (base System.ValueType)
    // and marshals fine; a reference proxy (Player) / primitive is not this case.
    public static bool IsRefBearingValueProxy(Type t) => t.BaseType?.FullName == "Il2CppSystem.ValueType";

    // The VALUE-level form: a boxed il2cpp proxy whose type is a ref-bearing value proxy (so a generic ctor/set_Item/
    // Add hands its boxed-proxy pointer where the UNBOXED value bytes belong — the hazard this bridge fixes).
    public static bool IsRefBearingValueType(object? value)
        => value is Il2CppObjectBase && IsRefBearingValueProxy(value.GetType());

    // Overwrite `il2cppObject`'s field `fieldName` with the UNBOXED bytes of `valueProxy` (a ref-bearing value
    // type). il2cpp_field_set_value copies the value bytes into the field THROUGH the GC write barrier, so the
    // embedded string reference survives — the correct representation the generic ctor got wrong. This is
    // Fields.SetStruct generalized (il2cpp_field_set_value already does roughly this for one site).
    public static void WriteField(object il2cppObject, string fieldName, object valueProxy)
    {
        nint obj = ((Il2CppObjectBase)il2cppObject).Pointer;
        nint field = IL2CPP.il2cpp_class_get_field_from_name(IL2CPP.il2cpp_object_get_class(obj), fieldName);
        if (field == 0)
            throw new NotSupportedException(
                $"ValueTypeBridge: no field '{fieldName}' on {il2cppObject.GetType().FullName} — cannot write the ref-bearing value type correctly (fail-loud, never a silent mis-marshal).");
        nint unboxed = IL2CPP.il2cpp_object_unbox(((Il2CppObjectBase)valueProxy).Pointer);
        IL2CPP.il2cpp_field_set_value(obj, field, (void*)unboxed);
    }

    // Invoke `il2cppObject`.`methodName`(convertedArgs) through raw il2cpp_runtime_invoke, marshalling each argument
    // by its il2cpp KIND so a ref-bearing value type is passed UNBOXED. This is WriteField's sibling for the case a
    // field write can't reach: where a tuple's element is a NAMED Item field (patched after the generic ctor),
    // a Dictionary stores values in internal entry storage exposed ONLY through set_Item — whose Il2CppInterop
    // wrapper hands a ref-bearing value-type argument as its boxed-proxy pointer where the UNBOXED value bytes
    // belong (the embedded string dropped, the value garbled — the same box-vs-bytes hazard, verified: SumBook
    // NRE'd / summed garbage). Bypassing the wrapper, we marshal each arg per the il2cpp_runtime_invoke convention
    // (params[i] -> the argument DATA for a value type, the object for a reference type):
    //   * an il2cpp value-type proxy (ref-bearing Loadout OR blittable Vec3) -> il2cpp_object_unbox: its DATA ptr
    //   * an il2cpp reference proxy (Player, a nested List)                   -> its object ptr
    //   * a managed string                                                   -> an il2cpp string (reference) ptr
    //   * a managed primitive/blittable value (an int/float/bool key)        -> pinned, its data address
    // Returns the raw return object pointer (0 for a void method), left to the caller to interpret. Fail-loud on a
    // missing method or an il2cpp-side exception — never a silent mis-marshal.
    public static nint InvokeUnboxed(object il2cppObject, string methodName, params object?[] convertedArgs)
    {
        nint objPtr = ((Il2CppObjectBase)il2cppObject).Pointer;
        nint method = IL2CPP.il2cpp_class_get_method_from_name(
            IL2CPP.il2cpp_object_get_class(objPtr), methodName, convertedArgs.Length);
        if (method == 0)
            throw new NotSupportedException(
                $"ValueTypeBridge: no method '{methodName}'({convertedArgs.Length} args) on {il2cppObject.GetType().FullName} — cannot raw-invoke the unboxed value-type write (fail-loud, never a silent mis-marshal).");

        var pins = new List<GCHandle>(convertedArgs.Length);
        try
        {
            void** args = stackalloc void*[convertedArgs.Length == 0 ? 1 : convertedArgs.Length];
            for (int i = 0; i < convertedArgs.Length; i++)
                args[i] = (void*)ArgPointer(convertedArgs[i], pins);
            nint exc = 0;
            nint ret = IL2CPP.il2cpp_runtime_invoke(method, objPtr, args, ref exc);
            Il2CppException.RaiseExceptionIfNecessary(exc);
            return ret;
        }
        finally
        {
            foreach (GCHandle h in pins) h.Free();
        }
    }

    // Invoke a value-type-Nullable-returning game method and marshal its return NULL-AWARE — the fix for
    // Il2CppInterop's broken value-type Nullable return (verified in a booted game: the EMPTY case NREs constructing
    // the proxy, AND the PRESENT case reads HasValue=False on a Vec3? — both directions wrong, so the whole return
    // must be done by us). We invoke the native method ourselves: il2cpp_runtime_invoke applies Nullable BOXING
    // semantics to the value-type return — an EMPTY Nullable<T> boxes to a NULL object, a PRESENT one to the boxed
    // UNDERLYING value — so a null return pointer IS the empty case, and a non-null one is the boxed T we read back
    // with Il2CppInterop's own value-from-pointer primitive (version-safe, the same one its working paths use). TVal
    // is the underlying value type the mod spells naturally (Vec3, Faction); the return is a natural TVal?.
    public static TVal? InvokeNullableStructReturn<TVal>(object self, string methodName, params object?[] args)
        where TVal : struct
    {
        nint ret = InvokeUnboxed(self, methodName, args);         // reuse the raw invoke + arg marshalling + exc check
        if (ret == 0) return null;                                // empty Nullable<TVal> boxed to a null object
        return IL2CPP.PointerToValueGeneric<TVal>(ret, isFieldPointer: false, valueTypeWouldBeBoxed: true);
    }

    // The REF-BEARING twin of InvokeNullableStructReturn: a game method returning a value-type Nullable whose inner is
    // a ref-bearing value proxy (Il2CppSystem.Nullable<MongoID>, MongoID = a struct with a string). System.Nullable<T>
    // can't wrap a class, so the flip's natural return is the BARE proxy T used as a nullable reference (null == empty).
    // il2cpp_runtime_invoke applies the SAME Nullable boxing to the return — an EMPTY Nullable boxes to a NULL object, a
    // PRESENT one to the boxed INNER value — so a null return pointer is the empty case, and a non-null one is the boxed
    // inner we wrap as the element proxy through Il2CppInterop's own pool (BoxedToRefNullable tail). This is the
    // full-body-replace splice target NullableRefReturnClosed synthesizes. T : Il2CppObjectBase.
    public static T? InvokeNullableRefReturn<T>(object self, string methodName, params object?[] args)
        where T : Il2CppObjectBase
        => BoxedToRefNullable<T>(InvokeUnboxed(self, methodName, args));   // raw invoke + the shared ref getter tail (BoxedToRefNullable)

    // Dematerialize a natural ref-bearing value (the bare proxy T, or null) into a boxed il2cpp Nullable<T> — the
    // PARAM-flip write twin of InvokeNullableRefReturn's read, the WriteNullableRefField box-rebuild MINUS the field
    // store. System.Nullable<T> can't express a ref-bearing inner, so the flipped param is the bare proxy T and this
    // DEDICATED helper (NOT the Conv-tree ToIl2Cpp — which sees T as a Leaf and never builds a Nullable) rebuilds the box
    // via metadata: il2cpp_object_new(Nullable<T> klass) is zero-init -> the empty Nullable (hasValue = 0); a PRESENT
    // value gets a GC-aware copy of its UNBOXED inner bytes into `value` (the embedded string preserved, the barriered
    // set the raw ctor gets wrong) + hasValue = 1. Returned wrapped as its typed proxy so ParamFlip.Splice's castclass to
    // the il2cpp Nullable param type is a no-op. Fail-loud on a missing field / unresolvable class — never a silent
    // mis-marshal. T : Il2CppObjectBase. (Deployed target of WrapHelpers.NullableRefToIl2CppClosed.)
    public static unsafe object RefToNullable<T>(T? value) where T : Il2CppObjectBase
    {
        Type nullableProxyType = ContainerBridge.NullableProxyOf(typeof(T));   // il2cpp Il2CppSystem.Nullable<T> (resolved reflectively)
        nint klass = ValueClassOf(nullableProxyType);
        nint box = IL2CPP.il2cpp_object_new(klass);                            // boxed Nullable<T>, zero-init -> empty (hasValue = 0)
        if (value is not null)
        {
            nint vf = IL2CPP.il2cpp_class_get_field_from_name(klass, "value");
            nint hf = IL2CPP.il2cpp_class_get_field_from_name(klass, "hasValue");
            if (vf == 0 || hf == 0)
                throw new NotSupportedException(
                    $"ValueTypeBridge: il2cpp {nullableProxyType.FullName} has no 'value'/'hasValue' field — cannot build a ref-bearing Nullable (fail-loud, never a silent mis-marshal).");
            nint src = IL2CPP.il2cpp_object_unbox(value.Pointer);
            IL2CPP.il2cpp_field_set_value(box, vf, (void*)src);               // GC-aware copy of the inner value (embedded ref preserved)
            byte one = 1; IL2CPP.il2cpp_field_set_value(box, hf, &one);       // hasValue = 1
        }
        GC.KeepAlive(value);
        return Activator.CreateInstance(nullableProxyType, (IntPtr)box)!;     // wrap the boxed Nullable as its typed proxy
    }

    // ── Nullable FIELD/PROPERTY accessor marshalling (NullableFieldRewriter's tail-swap + setter-rebuild targets) ──
    // A field-backed il2cpp Nullable<T> accessor has NO native get_X to invoke (it reads the field via
    // il2cpp_field_get_value), so — unlike a METHOD return (InvokeNullable*Return, which invokes by name) — the field
    // GETTER keeps its own field-read/box body and only its broken `newobj Nullable<T>(ptr)` TAIL is swapped for a call
    // to one of these; the SETTER body is rebuilt to forward to a Write* helper. Two element rungs (value / ref-bearing),
    // game-agnostic, bound by name at load.

    // GETTER tail (VALUE T): the getter's il2cpp_value_box already null-special-cases the field (empty -> 0, present ->
    // the boxed INNER T). 0 -> empty (null); else read the boxed underlying T. `where T : struct` (not `unmanaged`) keeps
    // the spliced `call` free of an `unmanaged` modreq; an il2cpp Nullable<T> guarantees T is a blittable value proxy.
    public static unsafe T? BoxedToNullable<T>(nint boxed) where T : struct
    {
        if (boxed == 0) return null;                               // empty Nullable -> boxed to a null object
        return Unsafe.Read<T>((void*)IL2CPP.il2cpp_object_unbox(boxed));   // has-value -> boxed underlying T -> unbox
    }

    // SETTER (VALUE T): write the inline il2cpp Nullable layout { hasValue@0, value@vo } directly at the field offset —
    // il2cpp's Nullable<T> layout matches .NET's, so a value-typed T copies byte-for-byte. A value type carries no
    // managed references, so no GC barrier. The rebuilt setter forwards `this, NativeFieldInfoPtr_X, value` here.
    public static unsafe void WriteNullableField<T>(Il2CppObjectBase obj, nint fieldInfo, T? value) where T : struct
    {
        byte* p = (byte*)(obj.Pointer + (int)IL2CPP.il2cpp_field_get_offset(fieldInfo));
        int vo = Unsafe.SizeOf<T?>() - Unsafe.SizeOf<T>();        // the value sits after the hasValue byte + alignment padding
        if (value.HasValue) { p[0] = 1; Unsafe.WriteUnaligned((void*)(p + vo), value.GetValueOrDefault()); }
        else { p[0] = 0; Unsafe.InitBlockUnaligned((void*)(p + vo), 0, (uint)Unsafe.SizeOf<T>()); }
    }

    // GETTER tail (REF-BEARING T): the ref counterpart of BoxedToNullable — and InvokeNullableRefReturn's shared tail.
    // The getter's box already null-special-cases the Nullable (empty -> 0, present -> a boxed INNER value); wrap that
    // boxed inner as the element proxy via the same pool Il2CppInterop's own reference getters use. 0 -> null (empty).
    public static T? BoxedToRefNullable<T>(nint boxed) where T : Il2CppObjectBase
        => boxed == 0 ? null : Il2CppInterop.Runtime.Runtime.Il2CppObjectPool.Get<T>(boxed);

    // SETTER (REF-BEARING T): the BARRIERED ref counterpart of WriteNullableField. The generated setter would raw-copy
    // the Nullable<T> bytes, storing the embedded managed reference(s) with NO GC write barrier (the incremental
    // collector can miss them). Rebuild the Nullable box via il2cpp metadata and GC-aware-copy it into the field.
    // `value` = the element proxy or null. Same box-rebuild as RefToNullable, but writing the field, not returning the box.
    public static unsafe void WriteNullableRefField<T>(Il2CppObjectBase obj, nint fieldInfo, T value) where T : Il2CppObjectBase
    {
        nint nkt = IL2CPP.il2cpp_class_from_il2cpp_type(IL2CPP.il2cpp_field_get_type(fieldInfo));
        nint box = IL2CPP.il2cpp_object_new(nkt);                          // boxed Nullable<T>, zero-init (hasValue = 0)
        if (value is not null)
        {
            nint vf = IL2CPP.il2cpp_class_get_field_from_name(nkt, "value");
            nint hf = IL2CPP.il2cpp_class_get_field_from_name(nkt, "hasValue");
            nint src = IL2CPP.il2cpp_object_unbox(value.Pointer);
            IL2CPP.il2cpp_field_set_value(box, vf, (void*)src);           // GC-aware copy of the inner value (refs preserved)
            byte one = 1; IL2CPP.il2cpp_field_set_value(box, hf, &one);   // hasValue = 1
        }
        IL2CPP.il2cpp_field_set_value(obj.Pointer, fieldInfo, (void*)IL2CPP.il2cpp_object_unbox(box));  // GC-aware copy -> field
        GC.KeepAlive(value);
    }

    // The pointer to pass for one already-converted argument, per the il2cpp_runtime_invoke convention InvokeUnboxed
    // documents. A managed primitive/blittable value is PINNED (its handle parked in `pins`, freed after the invoke)
    // so its boxed data address stays fixed across the call — AddrOfPinnedObject on a boxed value type is its data,
    // no object header, exactly the value-type argument pointer il2cpp expects.
    static nint ArgPointer(object? converted, List<GCHandle> pins)
    {
        switch (converted)
        {
            case null:
                return 0;                                             // a null reference argument
            case string s:
                return IL2CPP.ManagedStringToIl2Cpp(s);               // il2cpp string is a reference -> object ptr
            case Il2CppObjectBase proxy:
            {
                nint p = proxy.Pointer;
                nint klass = IL2CPP.il2cpp_object_get_class(p);
                return IL2CPP.il2cpp_class_is_valuetype(klass)
                    ? IL2CPP.il2cpp_object_unbox(p)                   // value type (ref-bearing or blittable) -> DATA ptr
                    : p;                                              // reference proxy -> object ptr
            }
            default:
            {
                // A managed primitive/blittable value. GCHandle pinning requires a BLITTABLE object, but a few CLR
                // primitives (bool, char) are non-blittable to the marshaller — normalize them to the byte/ushort of
                // their il2cpp representation (il2cpp bool = 1 byte, char = 2-byte UTF-16) so the pinned data matches
                // what il2cpp_runtime_invoke copies for that value-type param. All other primitives pin as-is.
                object blittable = converted switch
                {
                    bool b => (byte)(b ? 1 : 0),
                    char c => (ushort)c,
                    _ => converted,
                };
                GCHandle h = GCHandle.Alloc(blittable, GCHandleType.Pinned);
                pins.Add(h);
                return h.AddrOfPinnedObject();                        // its data address (boxed value data, no header)
            }
        }
    }

    // ── value type at a raw hook FRAME slot (the box/unbox dance) ─────────────────────────────────────────
    // A value-type container (il2cpp Nullable<T> / ValueTuple) is passed/returned BY VALUE — the frame slot holds
    // its unboxed value BYTES, not an object pointer — but the marshalling engine (MaterializeNullable/Tuple) reads
    // FIELDS off a boxed proxy. These two primitives bridge the gap: BOX the frame bytes into a proxy the engine
    // reads (arg/return READ), and UNBOX a proxy the engine built back into the frame bytes (arg/return WRITE). The
    // ONE value-type-ABI implementation lives here beside WriteField/InvokeUnboxed — the hook frame is just another
    // site (fields, containers, now the frame) routed through it, never a re-derivation.

    // The il2cpp class of a value-proxy Type (Il2CppSystem.Nullable<int>, ...ValueTuple<...>). The managed
    // Il2CppClassPointerStore is the fast path, but it is UNRELIABLE for some proxies — notably the interop-PATCHED
    // Il2CppSystem.Nullable<T>, for which the engine's own hook plan resorts to a native class hint (Hooks.cs). So
    // fall back to CONSTRUCTING an instance and reading its class: Il2CppInterop's ctor performs its own reliable
    // class resolution (ContainerBridge.EmptyNullable relies on exactly this parameterless construct for Nullable).
    // Fail loud if still unresolved — a wrong class would box/size the value at the wrong shape, precisely the
    // silent mis-marshal this bridge exists to prevent.
    public static nint ValueClassOf(Type valueProxyType)
    {
        nint klass = Il2CppClassPointerStore.GetNativeClassPointer(valueProxyType);
        if (klass != 0) return klass;
        try
        {
            object probe = Activator.CreateInstance(valueProxyType)!;   // forces Il2CppInterop's own class resolution
            klass = IL2CPP.il2cpp_object_get_class(((Il2CppObjectBase)probe).Pointer);
        }
        catch { }
        if (klass == 0)
            throw new NotSupportedException(
                $"ValueTypeBridge: no il2cpp class for value proxy {valueProxyType.FullName} — cannot box/unbox it at the hook frame (fail-loud, never a silent mis-marshal).");
        return klass;
    }

    // BOX the value bytes at `frameBytes` (a hook frame slot) into a boxed `valueProxyType` object the engine reads
    // its Nullable/Tuple fields off. il2cpp_object_new gives a RAW box (NOT il2cpp_value_box, which applies Nullable
    // boxing — an empty Nullable collapses to a null object, a present one to the boxed underlying — both wrong for
    // feeding MaterializeNullable, which wants the Nullable<T> object itself). A ref-bearing value (ValueTuple of
    // strings) has its pointer words copied THROUGH the GC write barrier: the box is a HEAP object, so an unbarriered
    // store could miss the card mark and the embedded refs be collected under this game's incremental GC.
    public static unsafe object BoxFrameValue(Type valueProxyType, nint frameBytes)
    {
        nint klass = ValueClassOf(valueProxyType);
        nint boxed = IL2CPP.il2cpp_object_new(klass);                 // a raw boxed value object (no Nullable collapse)
        nint data = IL2CPP.il2cpp_object_unbox(boxed);               // its value slot — a HEAP location
        uint align = 0; int size = IL2CPP.il2cpp_class_value_size(klass, ref align);
        if (IL2CPP.il2cpp_class_has_references(klass)) WriteBarrieredInto(boxed, data, frameBytes, size);   // heap dst carries refs
        else Buffer.MemoryCopy((void*)frameBytes, (void*)data, size, size);
        return Activator.CreateInstance(valueProxyType, (IntPtr)boxed)!;   // wrap the boxed object as its typed proxy
    }

    // UNBOX `valueProxy` (a boxed il2cpp value the engine built) and write its value BYTES into the frame slot `dst`.
    // A by-VALUE arg/return slot (register/stack arg, Gpr/Sret return) is a GC ROOT scanned precisely, like the
    // SetArgString/SetReturnString object slots — so a raw sized copy is GC-safe there (`barriered` false, the default
    // every by-value caller uses). A ref/out destination (Loc.Ind) is DIFFERENT: the slot's pointer targets the
    // caller's local/field/array element — possibly-live HEAP — so a ref-bearing value written there must go THROUGH
    // the il2cpp GC write barrier or the incremental collector can miss the embedded refs (the caller passes
    // `barriered` = HookContext.ArgWritesBarriered; a non-ref-bearing value, e.g. a blittable Vec3, never needs it and
    // never reaches this method — the Hook boundary routes blittable ref/out through StructureToPtr). The length is
    // the value type's own unboxed size, so a <=8B (Gpr) or >8B (Ind/Sret) value writes exactly its bytes. The proxy
    // keeps the embedded refs alive across the immediately-following original call (the same synchronous,
    // allocation-free window SetReturnString relies on before the game reads the slot).
    public static unsafe void UnboxToFrame(object valueProxy, Type valueProxyType, nint dst, bool barriered = false)
    {
        // Size the copy by the EXPECTED value class (the same reliable ValueClassOf the box used), NOT the boxed
        // object's RUNTIME class: a boxed Nullable<T> reports a COLLAPSED class (Int32-sized), so sizing by it copies
        // only part of the {value, has_value} pair and leaves the frame's value field stale (verified in-game:
        // GrantGold saw the un-rewritten arg). The unbox DATA region still holds the full value; only the length was
        // wrong. ValueTuple's class is not collapsed, so this is identical to its runtime class there.
        nint klass = ValueClassOf(valueProxyType);
        uint align = 0; int size = IL2CPP.il2cpp_class_value_size(klass, ref align);
        nint data = IL2CPP.il2cpp_object_unbox(((Il2CppObjectBase)valueProxy).Pointer);
        // A ref-bearing value into a heap-backed ref/out slot -> barriered (owner 0: address-keyed on this incremental
        // GC, not dereferenced, exactly like HookContext.WriteBarriered); every other case is a raw root store.
        if (barriered && IL2CPP.il2cpp_class_has_references(klass)) WriteBarrieredInto(0, dst, data, size);
        else Buffer.MemoryCopy((void*)data, (void*)dst, size, size);
    }

    // Copy `size` bytes from `src` into a HEAP-object destination through the il2cpp GC write barrier: each pointer-
    // sized word via wbarrier_set_field (so an incremental GC rescans `owner` and keeps the embedded refs alive), the
    // sub-word tail raw. Mirrors Hooks.WriteBarriered; kept here so the Marshal layer needn't depend on the engine.
    static unsafe void WriteBarrieredInto(nint owner, nint dst, nint src, int size)
    {
        int n = size / sizeof(nint);
        for (int i = 0; i < n; i++)
            IL2CPP.il2cpp_gc_wbarrier_set_field(owner, dst + i * sizeof(nint), ((nint*)src)[i]);
        for (int b = n * sizeof(nint); b < size; b++) ((byte*)dst)[b] = ((byte*)src)[b];
    }
}
