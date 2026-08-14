using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Inutil.InteropPatch;

// The STANDALONE-ARRAY pass: flip a game proxy method's TOP-LEVEL il2cpp array RETURN and/or PARAMS
// (Il2CppReferenceArray<T> / Il2CppStructArray<T> / Il2CppStringArray) to the natural BCL array (T[] / string[]).
//
// Arrays are NOT an Il2CppSystem.* family, so the container families skip them: Il2CppInterop ships a free
// bidirectional op_Implicit with T[], but it converts at CALL sites, NOT the proxy method's SIGNATURE — so a hook
// that RECEIVES a standalone array param would otherwise have to spell the raw Il2CppReferenceArray to bind. This pass
// makes standalone array signatures natural game-wide, reusing the SAME two splices the container families use (the
// marshalling rides the ConvKind.Array path, no drift):
//   RETURN: splice Il2CppMarshal.ToManaged<T[]>(object) before each ret (materialize the returned il2cpp array).
//   PARAM : flip the param to T[] + splice ToIl2CppTyped<T[]>(T[]) + castclass <il2cppArray> after each value-load of
//           that arg, so the existing Il2CppObjectBaseToPtr / argv store gets the dematerialized il2cpp array.
//
// Accessors (get_/set_) are SKIPPED — an array FIELD is a property flipping in lockstep with its accessors, owned by
// ContainerFieldRewriter. Guardrails match the field rewriter: NON-VIRTUAL only (a virtual array slot needs vtable
// lockstep — DEFER); GAME-scoped; ALL-OR-NOTHING per method (never a half-flipped signature); a value-address (ldarga)
// or exception-handled body DEFERS (can't splice a conversion at an address load / around a handler); IDEMPOTENT (a
// flipped position is a BCL array, so a re-run flips 0).
public sealed class ArrayRewriter
{
    public RewriteResult RewriteModule(ModuleDefinition module)
    {
        var flips = new List<string>();
        var defers = new List<string>();
        if (CecilProjector.IsFrameworkAssembly(module.Assembly.Name.Name))
            return new RewriteResult(0, flips, defers);

        var wrap = new WrapHelpers(module);
        int flipped = 0;

        foreach (TypeDefinition type in module.GetTypes())
        foreach (MethodDefinition m in type.Methods.ToArray())
        {
            if (m.IsGetter || m.IsSetter) continue;                                   // array FIELDS -> ContainerFieldRewriter (property lockstep)

            bool retIsArray = ContainerFlip.IsArrayWrapper(m.ReturnType);
            var arrayParams = m.Parameters.Where(p => ContainerFlip.IsArrayWrapper(p.ParameterType)).ToList();
            if (!retIsArray && arrayParams.Count == 0) continue;                       // no standalone array position

            string where = $"{type.FullName}::{m.Name}";
            if (m.IsVirtual)
            { defers.Add($"{where}  (array method -> DEFER: virtual slot needs vtable lockstep)"); continue; }
            if (!m.HasBody || m.Body.Instructions.Count == 0 || m.Body.HasExceptionHandlers)
            { defers.Add($"{where}  (array method -> DEFER: no body / exception-handled body)"); continue; }

            // ── ALL-OR-NOTHING pre-check: naturalize every array position, and confirm each array param has a
            //    value-load (ldarg) and NO address-load (ldarga) — else defer the WHOLE method (never a half-flip).
            TypeReference? naturalRet = retIsArray ? ContainerFlip.Naturalize(module, m.ReturnType) : null;
            if (retIsArray && naturalRet is null)
            { defers.Add($"{where}  (array return -> DEFER: element not naturalizable)"); continue; }

            var plan = new List<(ParameterDefinition p, int argIdx, TypeReference natural)>();
            bool bad = false;
            foreach (ParameterDefinition p in arrayParams)
            {
                TypeReference? nat = ContainerFlip.Naturalize(module, p.ParameterType);
                int argIdx = m.Parameters.IndexOf(p) + (m.IsStatic ? 0 : 1);
                bool valueLoad = m.Body.Instructions.Any(i => IsValueLoad(i, argIdx, p));
                bool addrLoad = m.Body.Instructions.Any(i => IsAddrLoad(i, p));
                if (nat is null || !valueLoad || addrLoad)
                { defers.Add($"{where}  (array param '{p.Name}' -> DEFER: {(nat is null ? "not naturalizable" : addrLoad ? "loaded by address" : "no value-load to splice")})"); bad = true; break; }
                plan.Add((p, argIdx, nat));
            }
            if (bad) continue;

            // ── APPLY (pre-checked, so this cannot half-flip) ──
            ILProcessor il = m.Body.GetILProcessor();

            if (retIsArray)
            {
                MethodReference toManaged = wrap.MarshalToManagedClosed(naturalRet!);
                foreach (Instruction ret in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
                    il.InsertBefore(ret, Instruction.Create(OpCodes.Call, toManaged));
                flips.Add($"{where}  return  {m.ReturnType.FullName} -> {naturalRet!.FullName}");
                m.ReturnType = naturalRet;
            }

            foreach (var (p, argIdx, nat) in plan)
            {
                MethodReference toIl2 = wrap.MarshalToIl2CppClosed(nat);
                TypeReference il2Import = module.ImportReference(p.ParameterType);
                foreach (Instruction ld in m.Body.Instructions.Where(i => IsValueLoad(i, argIdx, p)).ToArray())
                {
                    il.InsertAfter(ld, Instruction.Create(OpCodes.Castclass, il2Import));   // pushed down by the next insert
                    il.InsertAfter(ld, Instruction.Create(OpCodes.Call, toIl2));            // ld -> call ToIl2 -> castclass
                }
                flips.Add($"{where}  param '{p.Name}'  {p.ParameterType.FullName} -> {nat.FullName}");
                p.ParameterType = nat;
            }

            flipped++;
        }

        return new RewriteResult(flipped, flips, defers);
    }

    // A VALUE load of arg `argIndex` (param `p`): the short forms Ldarg_0..3 are positional; the long Ldarg/Ldarg_S carry
    // the ParameterDefinition. (Ldarg_0..3 for arg>=4 never occur; a >=4 arg is always the long form.)
    static bool IsValueLoad(Instruction i, int argIndex, ParameterDefinition p)
    {
        var op = i.OpCode;
        if (op == OpCodes.Ldarg_0) return argIndex == 0;
        if (op == OpCodes.Ldarg_1) return argIndex == 1;
        if (op == OpCodes.Ldarg_2) return argIndex == 2;
        if (op == OpCodes.Ldarg_3) return argIndex == 3;
        if (op == OpCodes.Ldarg || op == OpCodes.Ldarg_S) return ReferenceEquals(i.Operand, p);
        return false;
    }

    // An ADDRESS load of param `p` (ldarga / ldarga.s) — always carries the ParameterDefinition. A value conversion
    // can't be spliced at an address load, so its presence defers the method.
    static bool IsAddrLoad(Instruction i, ParameterDefinition p)
        => (i.OpCode == OpCodes.Ldarga || i.OpCode == OpCodes.Ldarga_S) && ReferenceEquals(i.Operand, p);
}
