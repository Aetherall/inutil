using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// Read each generated proxy type's OWN il2cpp identity out of the static ctor Il2CppInterop emitted for it, and
// hand it to the shared map format (Inutil.Schema.ExactTypeMap) the runtime materializer reads back.
//
// WHY THE CCTOR AND NOT THE TYPE'S NAME. Il2CppInterop renames as it generates — `System` -> `Il2CppSystem`,
// `mscorlib` -> `Il2Cppmscorlib`, `<>c` -> `__c` — by rules that belong to it and move with its version. Any
// reconstruction of the original identity from the proxy's CLR name is a re-implementation of those rules that is
// wrong the moment they change, and wrong SILENTLY (a mis-keyed row resolves some other type). The generator
// already wrote the answer down: every proxy's cctor opens by resolving its own class from the literal triple.
//
//     ToyGame.Player::.cctor       ldstr "Assembly-CSharp.dll"; ldstr "ToyGame"; ldstr "Player"
//                                  call IL2CPP::GetIl2CppClass; stsfld Il2CppClassPointerStore<Player>::NativeClassPtr
//     Bootstrap/__c::.cctor        ldsfld Il2CppClassPointerStore<Bootstrap>::NativeClassPtr; ldstr "<>c"
//                                  call IL2CPP::GetIl2CppNestedType; stsfld Il2CppClassPointerStore<__c>::NativeClassPtr
//
// So the extraction is a READ of the generator's own decision, and the nested arm gets the un-mangled member name
// (`<>c`) for free — the one the native side reports.
//
// The `stsfld` is part of the pattern on purpose, not incidental: a generic proxy type ALSO opens with the same
// three `ldstr`s, but feeds them through il2cpp_class_get_type/MakeGenericType instead of storing them, and a row
// for `Container`1` would be a trap — the native class of `Container<Player>` presents the SAME (image, ns, name),
// so it would resolve to the open type definition, which is not a materializable type at all. Requiring the store
// to THIS type's own NativeClassPtr excludes them structurally; HasGenericParameters excludes them again.
public static class ExactTypeExtract
{
    const string PointerStore = "Il2CppInterop.Runtime.Il2CppClassPointerStore`1";
    const string Il2CppApi = "Il2CppInterop.Runtime.IL2CPP";

    // Every non-generic proxy type in `module` whose cctor states its il2cpp identity. A type whose cctor does not
    // (a stub, an injected helper, a generic) is simply absent from the map: it falls back to being materialized at
    // its declared type, which is exactly today's behaviour.
    public static IEnumerable<ExactTypeRow> Rows(ModuleDefinition module)
    {
        string assembly = module.Assembly.Name.Name;
        var memo = new Dictionary<TypeDefinition, Identity?>();
        foreach (TypeDefinition t in module.GetTypes())
        {
            if (AnyGeneric(t)) continue;
            if (Resolve(t, memo) is not { } id) continue;
            yield return new ExactTypeRow(id.Image, id.Namespace, id.Name, assembly, ReflectionName(t));
        }
    }

    // A type or any of its enclosing types carrying generic parameters — see the header.
    static bool AnyGeneric(TypeDefinition t)
    {
        for (TypeDefinition? x = t; x is not null; x = x.DeclaringType)
            if (x.HasGenericParameters) return true;
        return false;
    }

    // Cecil spells a nested type `Outer/Inner`; reflection (Assembly.GetType) spells it `Outer+Inner`. The map
    // stores the RUNTIME spelling because the runtime is the only consumer that resolves it.
    static string ReflectionName(TypeDefinition t) => t.FullName.Replace('/', '+');

    readonly struct Identity
    {
        public readonly string Image, Namespace, Name;
        public Identity(string image, string ns, string name) { Image = image; Namespace = ns; Name = name; }
    }

    static Identity? Resolve(TypeDefinition t, Dictionary<TypeDefinition, Identity?> memo)
    {
        if (memo.TryGetValue(t, out Identity? cached)) return cached;
        memo[t] = null;                                  // pre-seed: a malformed cyclic chain terminates instead of recursing
        Identity? id = Compute(t, memo);
        memo[t] = id;
        return id;
    }

    static Identity? Compute(TypeDefinition t, Dictionary<TypeDefinition, Identity?> memo)
    {
        MethodDefinition? cctor = t.Methods.FirstOrDefault(m => m.IsConstructor && m.IsStatic && m.HasBody);
        if (cctor is null) return null;

        foreach (Instruction i in cctor.Body.Instructions)
        {
            if (i.Operand is not MethodReference call || call.DeclaringType?.FullName != Il2CppApi) continue;
            if (!StoresOwnClassPointer(i.Next, t)) continue;

            if (call.Name == "GetIl2CppClass" && call.Parameters.Count == 3)
            {
                // ldstr image; ldstr ns; ldstr name; call
                if (Str(i.Previous?.Previous?.Previous) is not { } image) continue;
                if (Str(i.Previous?.Previous) is not { } ns) continue;
                if (Str(i.Previous) is not { } name) continue;
                return new Identity(image, ns, name);
            }

            if (call.Name == "GetIl2CppNestedType" && call.Parameters.Count == 2)
            {
                // <parent class ptr>; ldstr simpleName; call — the parent's identity carries the image + namespace
                if (Str(i.Previous) is not { } simple) continue;
                if (t.DeclaringType is null || Resolve(t.DeclaringType, memo) is not { } parent) continue;
                return new Identity(parent.Image, parent.Namespace, parent.Name + "/" + simple);
            }
        }
        return null;
    }

    // Is `i` the store of the resolved pointer into THIS type's own Il2CppClassPointerStore<T>::NativeClassPtr?
    static bool StoresOwnClassPointer(Instruction? i, TypeDefinition t)
        => i is not null
           && i.OpCode == OpCodes.Stsfld
           && i.Operand is FieldReference f
           && f.Name == "NativeClassPtr"
           && f.DeclaringType is GenericInstanceType g
           && g.ElementType.FullName == PointerStore
           && g.GenericArguments.Count == 1
           && g.GenericArguments[0].FullName == t.FullName;

    static string? Str(Instruction? i) => i is not null && i.OpCode == OpCodes.Ldstr ? (string)i.Operand : null;
}
