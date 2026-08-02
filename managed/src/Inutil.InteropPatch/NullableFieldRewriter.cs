using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The Nullable ACCESSOR pass — EVERY il2cpp Nullable<T> property, whichever way Il2CppInterop backed its accessors.
// It owns accessors because the return/param families deliberately exclude them (they skip IsGetter/IsSetter); this
// pass is therefore the ONLY thing standing between a Nullable property and a silently-unflipped member, so it must
// cover BOTH backings an accessor can have — the boundary is the accessor's BODY SHAPE, not "field vs property":
//
//   • FIELD-backed  (a Nullable<T> FIELD; bodies load NativeFieldInfoPtr_X and read via il2cpp_field_get_value)
//     There is no native get_X/set_X to invoke, so: GETTER TAIL-SWAP (keep the field-read/box body, swap ONLY the
//     broken `newobj Nullable<T>(ptr)` tail for BoxedToNullable / BoxedToRefNullable — same IntPtr-on-stack shape)
//     and SETTER body REBUILD (forward `this, NativeFieldInfoPtr_X, value` to WriteNullableField/WriteNullableRefField).
//   • METHOD-backed (a real property — notably an AUTO-property, which yields BOTH members over one storage; bodies
//     load NativeMethodInfoPtr_X and il2cpp_runtime_invoke). The getter tail-swap applies UNCHANGED: runtime_invoke
//     applies the same Nullable boxing to a return as the field box does (empty -> null object, present -> the boxed
//     INNER value), so the tail sees the identical IntPtr. The SETTER cannot be rebuilt (no field info to write
//     through), so it takes the ordinary PARAM FLIP instead — ParamFlipResolver picks the converter and
//     ParamFlip.Splice dematerializes the natural value at entry, exactly as a Nullable param on any other method.
//
// Either way the natural spelling is the same:
//   • VALUE-type T (int, Vec3)  -> System.Nullable<T>
//   • REF-bearing T (MongoID …) -> the bare proxy T (a nullable REFERENCE, null == empty; System.Nullable<class> is illegal)
//
// The method-backed arm was the hole this pass shipped with: its pre-check demanded a NativeFieldInfoPtr the setter of
// a real property never has, so every get+set Nullable property DEFERRED ("accessor not handleable") — including its
// perfectly flippable getter, via all-or-nothing. It surfaced as EFT's ItemTemplate::ParentId, where a mod had to
// hand-build the boxed Nullable through ValueTypeBridge.RefToNullable — i.e. hand-perform the very splice below.
// (Get-ONLY method-backed properties flipped fine all along: with no setter, the pre-check passed vacuously.)
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
            // A setter is handleable EITHER way it can be backed: field-backed (a NativeFieldInfoPtr to rebuild the
            // body around) or method-backed (an invoke body whose param takes the ordinary flip — which requires the
            // resolver to actually produce a converter, so an unresolvable one defers rather than half-flips).
            bool getterOk = getter is null || (getter.HasBody && (FindNullableNewobjTail(getter) is not null || ReturnsSysNullable(getter)));
            FieldReference? setterFieldInfo = setter is not null && setter.HasBody ? HarvestFieldInfoLdsfld(setter) : null;
            (TypeReference natural, MethodReference converter)? setterParamFlip =
                setter is not null && setter.HasBody && setterFieldInfo is null && IsMethodBacked(setter)
                    ? ParamFlipResolver.Resolve(module, wrap, setter.Parameters[0].ParameterType)
                    : null;
            bool setterOk = setter is null || (setter.HasBody && setter.Parameters.Count == 1
                                               && (setterFieldInfo is not null || setterParamFlip is not null));
            if (!getterOk || !setterOk || (getter is null && setter is null))
            { defers.Add($"{type.FullName}::{prop.Name}  (nullable field -> DEFER: accessor not handleable)"); continue; }

            // STATIC + field-backed setter: the rebuild below is instance-only — it emits `ldarg.0` as `this` and
            // calls WriteNullableField(Il2CppObjectBase obj, …), but a STATIC setter's arg0 is the VALUE and there is
            // no object at all (a static il2cpp field needs il2cpp_field_static_set_value, which no helper exposes
            // yet). Emitting it anyway produced INVALID IL — verified on EFT proxies, where three static setters
            // (e.g. EFT.AudioUtils::set__cachedDspScheduleLookaheadSec) carry a body that pushes a Nullable<T> where
            // an Il2CppObjectBase is expected; it would throw InvalidProgramException the moment a mod called one.
            // Defer LOUDLY instead: a member left unflipped is a known limit, a mis-emitted body is a landmine.
            // (The METHOD-backed arm is unaffected — ParamFlip.Splice derives the arg index from HasThis.)
            if (setter is not null && setterFieldInfo is not null && setter.IsStatic)
            { defers.Add($"{type.FullName}::{prop.Name}  (nullable field -> DEFER: static field-backed setter, no static-field write helper)"); continue; }

            // The two derivations of the natural type — this pass's own (targetType, from the property's element) and
            // the resolver's (for the spliced param) — must agree, or getter and setter would flip to different types
            // and the property would be a half-flip that still compiles. Disagreement defers, loudly.
            if (setterParamFlip is { } spf && spf.natural.FullName != targetType.FullName)
            { defers.Add($"{type.FullName}::{prop.Name}  (nullable field -> DEFER: natural type disagreement {targetType.FullName} vs {spf.natural.FullName})"); continue; }

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

            // SETTER, field-backed: rebuild the body to `<Write*>(this, NativeFieldInfoPtr_X, value); ret`, then flip
            // the param type. The rebuild exists because the generated body would raw-copy the Nullable bytes,
            // storing an embedded managed reference with no GC write barrier.
            if (setter is not null && setterFieldInfo is not null)
            {
                MethodReference writeClosed = refBearing ? wrap.WriteNullableRefFieldClosed(t) : wrap.WriteNullableFieldClosed(t);
                MethodBody body = setter.Body;
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                body.Instructions.Clear();
                body.InitLocals = false;
                ILProcessor il = body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);                    // this (the proxy — an Il2CppObjectBase)
                il.Emit(OpCodes.Ldsfld, setterFieldInfo);    // nint fieldInfo (NativeFieldInfoPtr_<Field>)
                il.Emit(OpCodes.Ldarg_1);                    // value: System.Nullable<T> (value-type) or T (ref-bearing)
                il.Emit(OpCodes.Call, writeClosed);
                il.Emit(OpCodes.Ret);
                setter.Parameters[0].ParameterType = targetType;
            }
            // SETTER, method-backed: the invoke body is CORRECT as written (it unboxes the il2cpp Nullable it is
            // handed and passes it to set_X) — only its param spelling is wrong. So keep the body and splice the
            // ordinary entry dematerialization in front of it, the same mechanism every other Nullable param gets.
            else if (setter is not null)
            {
                var (_, converter) = setterParamFlip!.Value;
                ParameterDefinition p = setter.Parameters[0];
                TypeReference il2cppType = p.ParameterType;
                ParamFlip.Splice(module, setter, p, il2cppType, converter);
                p.ParameterType = targetType;
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

    // The METHOD-backed twin of HarvestFieldInfoLdsfld: the accessor dispatches to a real native get_X/set_X, which
    // Il2CppInterop locates through an `ldsfld <Proxy>::NativeMethodInfoPtr_<name>` before il2cpp_runtime_invoke.
    // Disjoint from the field-backed shape by construction (one body reads a field, the other invokes a method), so
    // the two arms can never both claim an accessor.
    static bool IsMethodBacked(MethodDefinition accessor)
    {
        foreach (Instruction instr in accessor.Body.Instructions)
            if (instr.OpCode == OpCodes.Ldsfld && instr.Operand is FieldReference fr
                && fr.Name.StartsWith("NativeMethodInfoPtr", StringComparison.Ordinal))
                return true;
        return false;
    }
}
