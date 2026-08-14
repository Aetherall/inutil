// Il2Cppmscorlib ref-stub — the il2cpp BCL-projection types the inutil engine references, declared with the
// SAME full names + member signatures Il2CppInterop generates, so the engine compiles game-free and binds the
// REAL Il2Cppmscorlib at runtime by name. Bodies never run; `throw null` is the reference-assembly idiom.
using System;
using Il2CppInterop.Runtime.InteropTypes;          // Il2CppObjectBase — the real proxy base (game-free, in BepInEx core)
using Il2CppInterop.Runtime.InteropTypes.Arrays;   // Il2CppReferenceArray — the real game-free il2cpp array proxy

namespace Il2CppSystem
{
    public class Object : Il2CppObjectBase
    {
        public Object() : base(IntPtr.Zero) { }
        public Object(IntPtr pointer) : base(pointer) { }
    }

    public class ValueType : Object
    {
        public ValueType() : base(IntPtr.Zero) { }
        public ValueType(IntPtr pointer) : base(pointer) { }
    }

    public class Type : Object
    {
        public Type() : base(IntPtr.Zero) { }
        public Type(IntPtr pointer) : base(pointer) { }
        public bool IsValueType => throw null;
    }

    // Il2CppInterop renders il2cpp Nullable<T> as a CLASS deriving Il2CppSystem.ValueType (unconstrained T).
    // The engine only NAMES it (typeof(Il2CppSystem.Nullable<>), for Hooks' ABI sizing); members mirror the real proxy.
    public sealed class Nullable<T> : ValueType
    {
        public Nullable() : base(IntPtr.Zero) { }
        public Nullable(IntPtr pointer) : base(pointer) { }
        public Nullable(T value) : base(IntPtr.Zero) { }
        public bool HasValue => throw null;
        public T Value => throw null;
    }
}

namespace Il2CppSystem.Reflection
{
    // Flattened base (real chain: MethodBase <- MemberInfo <- Object) — the engine never cross-assigns, and
    // memberref resolution keys on the DECLARING type + signature, which match the real proxy exactly.
    public class MethodInfo : Il2CppSystem.Object
    {
        public MethodInfo() : base(IntPtr.Zero) { }
        public MethodInfo(IntPtr pointer) : base(pointer) { }
        public Il2CppReferenceArray<Il2CppSystem.Type> GetGenericArguments() => throw null;
        // The real proxy has BOTH this managed-array overload and the Il2CppReferenceArray<Type> one; the engine calls with a Type[].
        public MethodInfo MakeGenericMethod(Il2CppSystem.Type[] typeArguments) => throw null;
    }
}

namespace Il2CppSystem.Collections
{
    // The NON-generic enumerator projection — where MoveNext lives. Il2CppInterop projects an il2cpp INTERFACE as a
    // CLASS deriving Il2CppObjectBase DIRECTLY (not Il2CppSystem.Object), so these don't chain onto Object like the
    // types above — the base is part of the signature the engine's memberrefs bind by. Only MoveNext is declared:
    // Il2CppSugar.ToManaged casts the GENERIC enumerator to this proxy purely to reach it (same il2cpp object) and
    // reads Current off the generic side; the stub carries only what the engine references.
    public class IEnumerator : Il2CppObjectBase
    {
        public IEnumerator(IntPtr pointer) : base(pointer) { }
        // VIRTUAL to match the real projection: the C# compiler emits `callvirt` for a virtual member and `call` for
        // a non-virtual one, so a non-virtual stub would bake a DIFFERENT opcode into the shipped IL than a real build.
        public virtual bool MoveNext() => throw null;
    }
}

namespace Il2CppSystem.Collections.Generic
{
    // Named as Json.ToDict's deserialize target; no member is touched at compile time — the read-back goes through
    // the Conv engine (Il2CppMarshal.ToManaged).
    public class Dictionary<K, V> : Il2CppSystem.Object
    {
        public Dictionary() : base(IntPtr.Zero) { }
        public Dictionary(IntPtr pointer) : base(pointer) { }
    }

    // The generic enumerable/enumerator projections Il2CppSugar.ToManaged binds. Il2CppInterop splits an il2cpp
    // generic enumerable across TWO proxies — the generic one carries only Current, MoveNext lives on the non-generic
    // Il2CppSystem.Collections.IEnumerator above. The stub reproduces that split faithfully rather than merging them,
    // because the engine's IL binds each member on its REAL declaring type.
    public class IEnumerable<T> : Il2CppObjectBase
    {
        public IEnumerable(IntPtr pointer) : base(pointer) { }
        public virtual IEnumerator<T> GetEnumerator() => throw null;   // virtual: see the note on Collections.IEnumerator.MoveNext
    }

    public class IEnumerator<T> : Il2CppObjectBase
    {
        public IEnumerator(IntPtr pointer) : base(pointer) { }
        public virtual T Current => throw null;
    }
}
