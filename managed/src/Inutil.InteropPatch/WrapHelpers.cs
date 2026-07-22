using Mono.Cecil;

namespace Inutil.InteropPatch;

// The wrap helpers a flipped Task-returning proxy body forwards its il2cpp Task through:
//   Inutil.Sugar.Il2CppSugar.WrapTask(Il2CppObjectBase)      -> System.Threading.Tasks.Task
//   Inutil.Sugar.Il2CppSugar.WrapTaskT<T>(Il2CppObjectBase)  -> System.Threading.Tasks.Task<T>
//
// In production, PatchDirectory resolves these from the built SDK DLL. For the OFFLINE seam and its tests the SDK
// doesn't exist yet, so these are SYNTHESIZED as references into a synthetic "Inutil" assembly with the real
// signatures — enough to splice a well-formed `call` and round-trip the module through Cecil. The real SDK method
// matches this signature, so the same rewrite then also loads+runs.
public sealed class WrapHelpers
{
    readonly ModuleDefinition _module;
    readonly AssemblyNameReference _inutil;
    readonly TypeReference _sugar;
    readonly TypeReference _il2cppObjectBase;

    public MethodReference WrapTask { get; }

    public WrapHelpers(ModuleDefinition module)
    {
        _module = module;
        // The synthetic SDK scope. Reuse an existing ref if a prior WrapHelpers already added it (a Plan()
        // then RewriteModule() on the same module), so the reference isn't duplicated.
        _inutil = module.AssemblyReferences.FirstOrDefault(a => a.Name == "Inutil")
                  ?? new AssemblyNameReference("Inutil", new Version(0, 1, 0, 0));
        if (!module.AssemblyReferences.Contains(_inutil)) module.AssemblyReferences.Add(_inutil);
        _sugar = new TypeReference("Inutil.Sugar", "Il2CppSugar", module, _inutil);

        // The helper's parameter type: the il2cpp reference proxy the body leaves on the stack —
        // Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase. An earlier spelling missing the .InteropTypes segment
        // ALWAYS fell back to System.Object; the spliced `call WrapTaskT(System.Object)` then could not bind the real
        // Il2CppSugar.WrapTaskT(Il2CppObjectBase) and threw MissingMethodException at JIT — a mismatch the offline
        // Cecil round-trip could not see (it never resolves the call against the SDK) and the in-game Task run caught.
        // Built from the module's OWN Il2CppInterop.Runtime assembly reference — every proxy derives from
        // Il2CppObjectBase, so that reference is always present — not hunted in GetTypeReferences().
        AssemblyNameReference? il2cppRuntimeRef = module.AssemblyReferences.FirstOrDefault(a => a.Name == "Il2CppInterop.Runtime");
        _il2cppObjectBase = il2cppRuntimeRef is not null
            ? new TypeReference("Il2CppInterop.Runtime.InteropTypes", "Il2CppObjectBase", module, il2cppRuntimeRef)
            : module.TypeSystem.Object;   // synthetic-only fallback (a real proxy always references Il2CppInterop.Runtime)

        var wrapTask = new MethodReference("WrapTask", MakeSysTask()) { DeclaringType = _sugar, HasThis = false };
        wrapTask.Parameters.Add(new ParameterDefinition(_il2cppObjectBase));
        WrapTask = wrapTask;
    }

    // WrapTaskT<T> instantiated over `x` (the member's own Task element). `x` is imported into this module while
    // any METHOD/TYPE generic PARAMETER it carries is kept RAW (ImportPreservingGenericParams): a bare own-param
    // (!!0) stays raw (ImportReference strips its owner context), a fully concrete type is imported, and a nested
    // instance CONTAINING the own-param (Result<!!0>) imports its element type yet preserves its !!0 arg raw.
    public MethodReference WrapTaskTClosed(TypeReference x)
    {
        var open = new MethodReference("WrapTaskT", _module.TypeSystem.Void) { DeclaringType = _sugar, HasThis = false };
        var tp = new GenericParameter(open);
        open.GenericParameters.Add(tp);
        open.ReturnType = MakeSysTaskT(tp);   // System.Task<!!0> over the helper's own param
        open.Parameters.Add(new ParameterDefinition(_il2cppObjectBase));

        var gim = new GenericInstanceMethod(open);
        gim.GenericArguments.Add(ImportPreservingGenericParams(x));
        return gim;
    }

    // Import a type into this module while keeping any METHOD/TYPE generic PARAMETER raw — ImportReference strips a
    // generic parameter's owner context (the WrapTaskTClosed note above), so a nested instance like Result<!!0> must
    // import its element type (Result`1) yet preserve its own-param arg (!!0) raw. Recurses through generic instances:
    // a bare param -> raw, a concrete type -> imported, a param-CONTAINING instance -> partial import.
    TypeReference ImportPreservingGenericParams(TypeReference x)
    {
        if (x.IsGenericParameter) return x;                                   // !!0 / !0 — raw, valid in scope
        // No generic parameter anywhere below -> import the whole reference (a bare leaf or a fully-concrete generic
        // instance like Task<IReadOnlyList<Player>>).
        if (!x.ContainsGenericParameter) return _module.ImportReference(x);
        if (x is GenericInstanceType g)                                       // Result<!!0> — import element, keep own-param raw
        {
            var gi = new GenericInstanceType(_module.ImportReference(g.ElementType));   // import the OPEN type (Result`1)
            foreach (var a in g.GenericArguments) gi.GenericArguments.Add(ImportPreservingGenericParams(a));  // recurse args
            return gi;
        }
        if (x is ArrayType at) return new ArrayType(ImportPreservingGenericParams(at.ElementType), at.Rank);
        if (x is ByReferenceType br) return new ByReferenceType(ImportPreservingGenericParams(br.ElementType));
        return _module.ImportReference(x);                                    // any other param-bearing shape — import
    }

    // Il2CppMarshal.ToManaged<natural>(object?) — the container/value MATERIALIZER the container-return flip splices
    // (the analog of WrapTask for the Task carrier). Closed over the NATURAL spelled type; resolves at load to the
    // SDK's Il2CppMarshal.ToManaged<T>, which drives the Conv tree. Same synthetic-Inutil-scope trick as WrapTask.
    public MethodReference MarshalToManagedClosed(TypeReference natural)
    {
        var declType = new TypeReference("Inutil.Marshal", "Il2CppMarshal", _module, _inutil);
        var open = new MethodReference("ToManaged", _module.TypeSystem.Void) { DeclaringType = declType, HasThis = false };
        var tp = new GenericParameter(open);
        open.GenericParameters.Add(tp);
        open.ReturnType = tp;                                                    // ToManaged<T> returns T
        open.Parameters.Add(new ParameterDefinition(_module.TypeSystem.Object)); // (object? il2cppValue)

        var gim = new GenericInstanceMethod(open);
        gim.GenericArguments.Add(_module.ImportReference(natural));
        return gim;
    }

    // Il2CppMarshal.ToIl2CppTyped<natural>(natural) -> object — the PARAM-flip splice target (the write analog of
    // MarshalToManagedClosed): at a flipped param's load site the seam calls this to DEMATERIALIZE the natural value
    // into its il2cpp counterpart, then castclass to the il2cpp param type. Closed over the natural spelled type.
    public MethodReference MarshalToIl2CppClosed(TypeReference natural)
    {
        var declType = new TypeReference("Inutil.Marshal", "Il2CppMarshal", _module, _inutil);
        var open = new MethodReference("ToIl2CppTyped", _module.TypeSystem.Object) { DeclaringType = declType, HasThis = false };
        var tp = new GenericParameter(open);
        open.GenericParameters.Add(tp);
        open.ReturnType = _module.TypeSystem.Object;         // ToIl2CppTyped<T> returns object
        open.Parameters.Add(new ParameterDefinition(tp));    // (T managedValue)

        var gim = new GenericInstanceMethod(open);
        gim.GenericArguments.Add(_module.ImportReference(natural));
        return gim;
    }

    // ValueTypeBridge.InvokeNullableStructReturn<TVal>(object self, string methodName, object[] args) -> TVal? — the
    // FULL-BODY-REPLACE splice target for a value-type Nullable return. Il2CppInterop's own value-type Nullable return
    // is broken both ways (empty NREs, present reads HasValue=False), so the flipped body abandons it and calls this,
    // which does its OWN null-aware il2cpp_runtime_invoke. Closed over the underlying value type.
    public MethodReference NullableStructReturnClosed(TypeReference valType)
    {
        var declType = new TypeReference("Inutil.Marshal", "ValueTypeBridge", _module, _inutil);
        var open = new MethodReference("InvokeNullableStructReturn", _module.TypeSystem.Void) { DeclaringType = declType, HasThis = false };
        var tp = new GenericParameter(open);
        open.GenericParameters.Add(tp);
        open.ReturnType = SysNullableOf(tp);                                                    // TVal? (Nullable<!!0>)
        open.Parameters.Add(new ParameterDefinition(_module.TypeSystem.Object));                // (object self)
        open.Parameters.Add(new ParameterDefinition(_module.TypeSystem.String));                // (string methodName)
        open.Parameters.Add(new ParameterDefinition(new ArrayType(_module.TypeSystem.Object))); // (object[] args)

        var gim = new GenericInstanceMethod(open);
        gim.GenericArguments.Add(_module.ImportReference(valType));
        return gim;
    }

    // ValueTypeBridge.InvokeNullableRefReturn<T>(object self, string methodName, object[] args) -> T — the FULL-BODY-
    // REPLACE splice target for a REF-BEARING value-type Nullable return (the ref twin of NullableStructReturnClosed).
    // Il2CppSystem.Nullable<MongoID> can NOT flip to System.Nullable<MongoID> (T:struct — a value type carrying a
    // reference renders as a class), so it flips to the BARE proxy T used as a nullable reference: the helper invokes
    // the native method (il2cpp_runtime_invoke Nullable-boxes the return — empty -> null, present -> the boxed inner)
    // and pools the result (0 -> null). Closed over the element proxy T (the return T is `tp`, NOT SysNullableOf(tp)).
    public MethodReference NullableRefReturnClosed(TypeReference elemProxy)
    {
        var declType = new TypeReference("Inutil.Marshal", "ValueTypeBridge", _module, _inutil);
        var open = new MethodReference("InvokeNullableRefReturn", _module.TypeSystem.Void) { DeclaringType = declType, HasThis = false };
        var tp = new GenericParameter(open);
        open.GenericParameters.Add(tp);
        open.ReturnType = tp;                                                                   // T (the proxy) — a nullable reference
        open.Parameters.Add(new ParameterDefinition(_module.TypeSystem.Object));                // (object self)
        open.Parameters.Add(new ParameterDefinition(_module.TypeSystem.String));                // (string methodName)
        open.Parameters.Add(new ParameterDefinition(new ArrayType(_module.TypeSystem.Object))); // (object[] args)

        var gim = new GenericInstanceMethod(open);
        gim.GenericArguments.Add(_module.ImportReference(elemProxy));
        return gim;
    }

    // ValueTypeBridge.RefToNullable<T>(T value) -> object — the PARAM-flip splice target for a REF-BEARING value-type
    // Nullable param (the ref twin of MarshalToIl2CppClosed). The flipped param is the BARE proxy T; the generic
    // Il2CppMarshal.ToIl2CppTyped<System.Nullable<T>> CANNOT serve it — System.Nullable<class> is illegal, so the flip
    // ERASES the Nullable-ness from the signature (the Conv tree would see T as a Leaf and never build a Nullable). So
    // a DEDICATED helper rebuilds the il2cpp Nullable<T> box via metadata and returns it (as object; ParamFlip.Splice
    // castclass'es it to the il2cpp Nullable param type). Closed over the element proxy T; same synthetic-scope trick.
    public MethodReference NullableRefToIl2CppClosed(TypeReference elemProxy)
    {
        var declType = new TypeReference("Inutil.Marshal", "ValueTypeBridge", _module, _inutil);
        var open = new MethodReference("RefToNullable", _module.TypeSystem.Object) { DeclaringType = declType, HasThis = false };
        var tp = new GenericParameter(open);
        open.GenericParameters.Add(tp);
        open.ReturnType = _module.TypeSystem.Object;         // returns the boxed il2cpp Nullable<T> as object
        open.Parameters.Add(new ParameterDefinition(tp));    // (T value) — the natural proxy or null

        var gim = new GenericInstanceMethod(open);
        gim.GenericArguments.Add(_module.ImportReference(elemProxy));
        return gim;
    }

    // ── Nullable FIELD/PROPERTY accessor helpers (NullableFieldRewriter): getter TAIL-SWAP + setter REBUILD targets ──
    // A field-backed accessor has no native get_X to invoke, so the GETTER keeps its field-read/box body and only its
    // broken `newobj Nullable<T>(IntPtr)` tail is swapped for a call to BoxedToNullable / BoxedToRefNullable (same
    // IntPtr-in stack), and the SETTER body is rebuilt to forward `this, fieldInfoPtr, value` to WriteNullableField /
    // WriteNullableRefField. All four resolve to Inutil.Marshal.ValueTypeBridge at load (same synthetic-scope trick).

    // ValueTypeBridge.BoxedToNullable<T>(nint) -> System.Nullable<T> — the VALUE getter tail-swap target.
    public MethodReference BoxedToNullableClosed(TypeReference valType)
    {
        var declType = new TypeReference("Inutil.Marshal", "ValueTypeBridge", _module, _inutil);
        var open = new MethodReference("BoxedToNullable", _module.TypeSystem.Void) { DeclaringType = declType, HasThis = false };
        var tp = new GenericParameter(open);
        open.GenericParameters.Add(tp);
        open.ReturnType = SysNullableOf(tp);                                       // System.Nullable<!!0>
        open.Parameters.Add(new ParameterDefinition(_module.TypeSystem.IntPtr));   // (nint boxed)

        var gim = new GenericInstanceMethod(open);
        gim.GenericArguments.Add(_module.ImportReference(valType));
        return gim;
    }

    // ValueTypeBridge.BoxedToRefNullable<T>(nint) -> T — the REF-BEARING getter tail-swap target (natural = bare proxy).
    public MethodReference BoxedToRefNullableClosed(TypeReference refElem)
    {
        var declType = new TypeReference("Inutil.Marshal", "ValueTypeBridge", _module, _inutil);
        var open = new MethodReference("BoxedToRefNullable", _module.TypeSystem.Void) { DeclaringType = declType, HasThis = false };
        var tp = new GenericParameter(open);
        open.GenericParameters.Add(tp);
        open.ReturnType = tp;                                                      // T (the proxy) — a nullable reference
        open.Parameters.Add(new ParameterDefinition(_module.TypeSystem.IntPtr));   // (nint boxed)

        var gim = new GenericInstanceMethod(open);
        gim.GenericArguments.Add(_module.ImportReference(refElem));
        return gim;
    }

    // ValueTypeBridge.WriteNullableField<T>(Il2CppObjectBase, nint, System.Nullable<T>) -> void — the VALUE setter target.
    public MethodReference WriteNullableFieldClosed(TypeReference valType)
    {
        var declType = new TypeReference("Inutil.Marshal", "ValueTypeBridge", _module, _inutil);
        var open = new MethodReference("WriteNullableField", _module.TypeSystem.Void) { DeclaringType = declType, HasThis = false };
        var tp = new GenericParameter(open);
        open.GenericParameters.Add(tp);
        open.Parameters.Add(new ParameterDefinition(_il2cppObjectBase));           // (Il2CppObjectBase obj)
        open.Parameters.Add(new ParameterDefinition(_module.TypeSystem.IntPtr));   // (nint fieldInfo)
        open.Parameters.Add(new ParameterDefinition(SysNullableOf(tp)));           // (System.Nullable<T> value)

        var gim = new GenericInstanceMethod(open);
        gim.GenericArguments.Add(_module.ImportReference(valType));
        return gim;
    }

    // ValueTypeBridge.WriteNullableRefField<T>(Il2CppObjectBase, nint, T) -> void — the REF-BEARING setter target.
    public MethodReference WriteNullableRefFieldClosed(TypeReference refElem)
    {
        var declType = new TypeReference("Inutil.Marshal", "ValueTypeBridge", _module, _inutil);
        var open = new MethodReference("WriteNullableRefField", _module.TypeSystem.Void) { DeclaringType = declType, HasThis = false };
        var tp = new GenericParameter(open);
        open.GenericParameters.Add(tp);
        open.Parameters.Add(new ParameterDefinition(_il2cppObjectBase));           // (Il2CppObjectBase obj)
        open.Parameters.Add(new ParameterDefinition(_module.TypeSystem.IntPtr));   // (nint fieldInfo)
        open.Parameters.Add(new ParameterDefinition(tp));                          // (T value)

        var gim = new GenericInstanceMethod(open);
        gim.GenericArguments.Add(_module.ImportReference(refElem));
        return gim;
    }

    // System.Nullable`1<x>. The open type is ImportReference'd from its DEFINING assembly (System.Private.CoreLib) —
    // the same proven scope ContainerFlip uses for the container families (module.TypeSystem.CoreLibrary is the
    // System.Runtime facade, which does not always carry a forwarded type). `x` used RAW when it is the method's own
    // generic parameter (already valid in scope), imported when it is a concrete value type.
    public GenericInstanceType SysNullableOf(TypeReference x)
    {
        var inst = new GenericInstanceType(_module.ImportReference(typeof(System.Nullable<>)));
        inst.GenericArguments.Add(x.IsGenericParameter ? x : _module.ImportReference(x));
        return inst;
    }

    public TypeReference MakeSysTask()
        => new TypeReference("System.Threading.Tasks", "Task", _module, _module.TypeSystem.CoreLibrary);

    // System.Task`1 over `x` (the flipped RETURN element). Routes `x` through ImportPreservingGenericParams so
    // `Result<!!0>` builds System.Task<Result<!!0>> with Result imported + !!0 raw — while a bare own-param stays
    // raw and a concrete type is imported.
    public GenericInstanceType MakeSysTaskT(TypeReference x)
    {
        var open = new TypeReference("System.Threading.Tasks", "Task`1", _module, _module.TypeSystem.CoreLibrary);
        open.GenericParameters.Add(new GenericParameter(open));
        var inst = new GenericInstanceType(open);
        inst.GenericArguments.Add(ImportPreservingGenericParams(x));
        return inst;
    }
}
