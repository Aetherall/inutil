using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The CONTAINER field/property accessor pass — the container twin of NullableFieldRewriter. Il2CppInterop renders a
// List/Dictionary/HashSet/… FIELD as a property with get_/set_ accessors, which the container return/param families
// deliberately EXCLUDE (they skip getters/setters). This pass owns them, and — because a getter IS "a method returning a
// container" and a setter IS "a method taking a container param" — it reuses the SAME two splices those families use,
// verified against the real Il2CppInterop accessor shape (a single-ret pooled getter; a ldarg.1 -> Il2CppObjectBaseToPtr
// -> il2cpp_gc_wbarrier_set_field setter):
//   GETTER: splice Il2CppMarshal.ToManaged<natural>(object) before the ret — the proxy/null on the stack is a reference,
//           assignable to ToManaged's object param — then flip the return type.
//   SETTER: flip the param to natural + splice Il2CppMarshal.ToIl2CppTyped<natural>(natural) + castclass to the il2cpp
//           container AFTER the ldarg.1 value load, so the existing Il2CppObjectBaseToPtr + gc_wbarrier_set_field get the
//           dematerialized proxy — the barriered field-set is UNCHANGED.
// So NO new runtime helper: getter reuses MarshalToManagedClosed, setter MarshalToIl2CppClosed (the method flips' own
// helpers — one implementation, no drift).
//
// SEMANTIC NOTE (copy-on-get): a flipped getter returns a fresh MATERIALIZED copy of the il2cpp container, not a live
// view — so `list.categories.Add(k,v)` does NOT write back; re-assign instead (`list.categories = dict`). Correct for
// assign sites; a read-modify-write footgun (a live-view getter is a follow-on).
//
// Guardrails match NullableFieldRewriter: NON-VIRTUAL only (a virtual accessor needs vtable lockstep — DEFER);
// ALL-OR-NOTHING per property (a half-flipped get/set/property is invalid); GAME-scoped; IDEMPOTENT (a flipped property
// is System.*, no longer classified by ContainerFlip.NaturalReturn, so a re-run flips 0).
public sealed class ContainerFieldRewriter
{
    public RewriteResult RewriteModule(ModuleDefinition module)
    {
        var flips = new List<string>();
        var defers = new List<string>();

        // Game-scoped, like the non-virtual return/param arms: never flip a framework module's BCL container accessors.
        if (CecilProjector.IsFrameworkAssembly(module.Assembly.Name.Name))
            return new RewriteResult(0, flips, defers);

        var wrap = new WrapHelpers(module);
        int flipped = 0;

        foreach (TypeDefinition type in module.GetTypes())
        foreach (PropertyDefinition prop in type.Properties.ToArray())
        {
            // A flippable FIELD type: a container family (NaturalReturn — List/Dict/Tuple/Set) OR a TOP-LEVEL il2cpp array
            // (IsArrayWrapper + Naturalize — Il2CppReferenceArray<T> -> T[], the array-field half of the standalone-array
            // flip; the array METHOD half is ArrayRewriter). A leaf/proxy field is neither -> skip. A flipped property is
            // already System.* / a BCL array (not classified, not an Il2Cpp*Array) -> the idempotency gate too.
            TypeReference il2cppType = prop.PropertyType;
            TypeReference? natural = ContainerFlip.NaturalReturn(module, il2cppType)
                ?? (ContainerFlip.IsArrayWrapper(il2cppType) ? ContainerFlip.Naturalize(module, il2cppType) : null);
            if (natural is null) continue;

            MethodDefinition? getter = prop.GetMethod, setter = prop.SetMethod;
            if ((getter?.IsVirtual ?? false) || (setter?.IsVirtual ?? false))
            { defers.Add($"{type.FullName}::{prop.Name}  (container field -> DEFER: virtual accessor needs vtable lockstep)"); continue; }

            // ALL-OR-NOTHING pre-check: the getter must have a body with a ret to splice before; the setter must have a
            // single value param + a ldarg.1 to splice after. Anything else defers the whole property (never a half-flip).
            bool getterOk = getter is null || (getter.HasBody && getter.Body.Instructions.Any(i => i.OpCode == OpCodes.Ret));
            bool setterOk = setter is null || (setter.HasBody && setter.Parameters.Count == 1
                                               && setter.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldarg_1));
            if (!getterOk || !setterOk || (getter is null && setter is null))
            { defers.Add($"{type.FullName}::{prop.Name}  (container field -> DEFER: accessor shape not handleable)"); continue; }

            // GETTER: materialize the returned il2cpp container -> natural before EACH ret, then flip the return type.
            if (getter is not null)
            {
                ILProcessor il = getter.Body.GetILProcessor();
                MethodReference toManaged = wrap.MarshalToManagedClosed(natural);
                foreach (Instruction ret in getter.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
                    il.InsertBefore(ret, Instruction.Create(OpCodes.Call, toManaged));
                getter.ReturnType = natural;
            }

            // SETTER: flip the param to natural, then dematerialize it back to the il2cpp container right after EACH
            // ldarg.1 (before the existing Il2CppObjectBaseToPtr) — `call ToIl2CppTyped<natural>(natural); castclass
            // <il2cppContainer>`. The gc_wbarrier field-set is untouched; it just receives the flipped proxy.
            if (setter is not null)
            {
                ILProcessor il = setter.Body.GetILProcessor();
                MethodReference toIl2 = wrap.MarshalToIl2CppClosed(natural);
                TypeReference il2Import = module.ImportReference(il2cppType);
                foreach (Instruction ld in setter.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldarg_1).ToArray())
                {
                    il.InsertAfter(ld, Instruction.Create(OpCodes.Castclass, il2Import));   // pushed down by the next insert
                    il.InsertAfter(ld, Instruction.Create(OpCodes.Call, toIl2));            // ld -> call ToIl2 -> castclass
                }
                setter.Parameters[0].ParameterType = natural;
            }

            prop.PropertyType = natural;
            flipped++;
            flips.Add($"{type.FullName}::{prop.Name}:  {il2cppType.FullName}  ->  {natural.FullName}  (container field)");
        }

        return new RewriteResult(flipped, flips, defers);
    }
}
