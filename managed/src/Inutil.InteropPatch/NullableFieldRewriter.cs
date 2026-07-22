using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The Nullable FIELD/PROPERTY accessor pass. Il2CppInterop renders a Nullable<T> FIELD as a property with get_/set_
// accessors — which the return/param families deliberately EXCLUDE (they skip IsGetter/IsSetter) AND which the method
// arm's invoke-replace can't serve (a field-backed accessor has no native get_X to invoke; it reads the field via
// il2cpp_field_get_value). So this pass owns them: it flips each il2cpp Nullable<T> property + its get/set accessors,
// in LOCKSTEP, to the natural spelling —
//   • VALUE-type T (int, Vec3)  -> System.Nullable<T>
//   • REF-bearing T (MongoID …) -> the bare proxy T (a nullable REFERENCE, null == empty; System.Nullable<class> is illegal)
// via a GETTER TAIL-SWAP (leave the field-read/box body, swap ONLY the broken `newobj Nullable<T>(ptr)` tail for a
// call to BoxedToNullable / BoxedToRefNullable — same IntPtr-in-stack shape) and a SETTER body REBUILD (forward
// `this, NativeFieldInfoPtr_X, value` to WriteNullableField / WriteNullableRefField). The bug it fixes: a field-backed
// Nullable<ref> PROPERTY accessor's raw Il2CppInterop getter NREs.
//
// Guardrails: NON-VIRTUAL only (a virtual accessor is a vtable/interface slot needing the same lockstep as virtual
// returns — deferred); ALL-OR-NOTHING per property (every present accessor is pre-checked handleable, or the whole
// property defers — a half-flip is invalid IL); GAME-scoped. IDEMPOTENT: a flipped property is no longer typed il2cpp
// Nullable, so the top filter skips it — a re-run flips 0.
public sealed class NullableFieldRewriter
{
    static readonly CorrespondenceRegistry _families = Families.Default();

    public RewriteResult RewriteModule(ModuleDefinition module)
    {
        var flips = new List<string>();
        var defers = new List<string>();

        // Game-scoped, like the non-virtual return/param arms: never flip a framework module's BCL Nullable accessors.
        if (CecilProjector.IsFrameworkAssembly(module.Assembly.Name.Name))
            return new RewriteResult(0, flips, defers);

        var wrap = new WrapHelpers(module);
        int flipped = 0;

        foreach (TypeDefinition type in module.GetTypes())
        foreach (PropertyDefinition prop in type.Properties.ToArray())
        {
            // Only a property STILL typed il2cpp Nullable<T> — a flipped one is System.Nullable<T> / bare proxy T,
            // which no longer classifies here (the idempotency gate). Element must be concrete (open generic -> skip).
            if (prop.PropertyType is not GenericInstanceType g) continue;
            if (_families.Classify(CecilTypeRef.Of(prop.PropertyType)) is not { Kind: ConvKind.Nullable }) continue;
            TypeReference t = g.GenericArguments[0];
            if (t.IsGenericParameter) continue;

            // Value-type element -> System.Nullable<T>; ref-bearing (a class proxy, !IsValueType) -> the bare proxy T.
            // The same drift-safe discriminator the method arm uses: within a confirmed il2cpp Nullable, !IsValueType
            // IS a ref-bearing value proxy (derived without re-spelling it).
            bool refBearing = !t.IsValueType;
            TypeReference targetType = refBearing ? t : wrap.SysNullableOf(t);

            MethodDefinition? getter = prop.GetMethod, setter = prop.SetMethod;
            if ((getter?.IsVirtual ?? false) || (setter?.IsVirtual ?? false))
            { defers.Add($"{type.FullName}::{prop.Name}  (nullable field -> DEFER: virtual accessor)"); continue; }

            // ALL-OR-NOTHING pre-check: every present accessor must be handleable, or the whole property defers (a
            // half-flipped property leaves inconsistent get/set/property types — invalid IL). A getter is handle-able
            // if it has the broken newobj tail (needs the swap) OR is ALREADY System.Nullable<T> (a prior tool version
            // flipped the getter but not the setter/property — a half-patched state to REPAIR). A setter is handle-able
            // if it is a single-param body that loads the NativeFieldInfoPtr static (harvested for the rebuild).
            bool getterOk = getter is null || (getter.HasBody && (FindNullableNewobjTail(getter) is not null || ReturnsSysNullable(getter)));
            bool setterOk = setter is null || (setter.HasBody && setter.Parameters.Count == 1 && HarvestFieldInfoLdsfld(setter) is not null);
            if (!getterOk || !setterOk || (getter is null && setter is null))
            { defers.Add($"{type.FullName}::{prop.Name}  (nullable field -> DEFER: accessor not handleable)"); continue; }

            // GETTER: swap the broken `newobj Nullable<T>(ptr)` tail to the chosen helper (skipped when already flipped —
            // no newobj to find), then set the natural return type.
            if (getter is not null)
            {
                if (FindNullableNewobjTail(getter) is { } newobj)
                {
                    newobj.OpCode = OpCodes.Call;
                    newobj.Operand = refBearing ? wrap.BoxedToRefNullableClosed(t) : wrap.BoxedToNullableClosed(t);
                }
                getter.ReturnType = targetType;
            }

            // SETTER: rebuild the body to `<Write*>(this, NativeFieldInfoPtr_X, value); ret`, then flip the param type.
            if (setter is not null)
            {
                FieldReference fieldInfo = HarvestFieldInfoLdsfld(setter)!;
                MethodReference writeClosed = refBearing ? wrap.WriteNullableRefFieldClosed(t) : wrap.WriteNullableFieldClosed(t);
                MethodBody body = setter.Body;
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                body.Instructions.Clear();
                body.InitLocals = false;
                ILProcessor il = body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);             // this (the proxy — an Il2CppObjectBase)
                il.Emit(OpCodes.Ldsfld, fieldInfo);   // nint fieldInfo (NativeFieldInfoPtr_<Field>)
                il.Emit(OpCodes.Ldarg_1);             // value: System.Nullable<T> (value-type) or T (ref-bearing)
                il.Emit(OpCodes.Call, writeClosed);
                il.Emit(OpCodes.Ret);
                setter.Parameters[0].ParameterType = targetType;
            }

            prop.PropertyType = targetType;
            flipped++;
            flips.Add($"{type.FullName}::{prop.Name}:  {g.ElementType.Name}<{t.Name}>  ->  "
                + (refBearing ? t.Name + " (nullable field, ref-bearing)" : "System.Nullable<" + t.Name + "> (nullable field)"));
        }

        return new RewriteResult(flipped, flips, defers);
    }

    // The broken `newobj <il2cpp Nullable`1<T>>::.ctor(System.IntPtr)` instruction in a getter body, or null. The
    // Nullable family is matched via the registry (Classify) — no il2cpp Nullable name literal.
    static Instruction? FindNullableNewobjTail(MethodDefinition method)
    {
        foreach (Instruction instr in method.Body.Instructions)
        {
            if (instr.OpCode != OpCodes.Newobj || instr.Operand is not MethodReference ctor) continue;
            if (ctor.Name != ".ctor" || ctor.Parameters.Count != 1) continue;
            if (ctor.Parameters[0].ParameterType.FullName != "System.IntPtr") continue;
            if (_families.Classify(CecilTypeRef.Of(ctor.DeclaringType)) is { Kind: ConvKind.Nullable }) return instr;
        }
        return null;
    }

    // A getter a prior tool version already flipped to System.Nullable<T> (the half-patched state this pass REPAIRS) —
    // lets the pre-check accept it without a broken newobj tail to swap.
    static bool ReturnsSysNullable(MethodDefinition method)
        => method.ReturnType is GenericInstanceType g && g.ElementType.FullName == "System.Nullable`1";

    // The `ldsfld <Proxy>::NativeFieldInfoPtr_<Field>` the generated accessor loads to locate the field — harvested so
    // the rebuilt setter reuses the SAME field-info static (no re-derivation). Null if the body loads none.
    static FieldReference? HarvestFieldInfoLdsfld(MethodDefinition accessor)
    {
        foreach (Instruction instr in accessor.Body.Instructions)
            if (instr.OpCode == OpCodes.Ldsfld && instr.Operand is FieldReference fr
                && fr.Name.StartsWith("NativeFieldInfoPtr", StringComparison.Ordinal))
                return fr;
        return null;
    }
}
