using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Inutil.InteropPatch;

// The ONE param-flip IL mechanism, shared by every write-direction PARAM family (container, value-type Nullable, and
// delegate params). A flipped param wears the NATURAL type in the public signature while the proxy body still reads
// the il2cpp value — so at method entry the incoming natural value is DEMATERIALIZED into a local of the original
// il2cpp type, and every body load of the param is redirected to that local.
//
// Driven by the unified ParamFamily (via ParamFlipResolver) for ALL three param families, virtual + non-virtual
// (docs/contribution/01-philosophy.md): a param family is a type-map + this call, not a re-implemented splice. The
// converter is closed over the natural type: ToIl2CppTyped<Natural>(Natural) -> object for container / Nullable (a
// plain `ldarg` satisfies its T param for both a reference natural and a value natural System.Nullable<T>), or the
// delegate wrapper's own op_Implicit (returns the exact il2cpp type). The trailing castclass is a real narrowing for
// the `object`-returning helper and a verifiable no-op for op_Implicit.
public static class ParamFlip
{
    // Splice one param's entry dematerialization (see class comment). The caller has already resolved the natural
    // type, the original il2cpp type, and the closed ToIl2CppTyped helper; it sets p.ParameterType = natural after.
    //   add local of <il2cppType>;  redirect body ldarg<param> -> ldloc<local>;
    //   prepend: ldarg <param>; call ToIl2CppTyped<Natural>; castclass <il2cppType>; stloc <local>.
    public static void Splice(ModuleDefinition module, MethodDefinition method, ParameterDefinition param,
                              TypeReference il2cppType, MethodReference toIl2Cpp)
    {
        MethodBody body = method.Body;
        body.InitLocals = true;
        var local = new VariableDefinition(module.ImportReference(il2cppType));
        body.Variables.Add(local);

        // Redirect the ORIGINAL loads of the param to the (soon-to-be-filled) local. Done BEFORE inserting the
        // entry conversion, so the conversion's own `ldarg` is not itself redirected.
        int argIndex = param.Index + (method.HasThis ? 1 : 0);
        foreach (Instruction ins in body.Instructions)
            if (LoadsArg(ins, argIndex, param)) { ins.OpCode = OpCodes.Ldloc; ins.Operand = local; }

        // Prepend: ldarg <param>; call ToIl2CppTyped<Natural>; castclass <il2cppType>; stloc <local>.
        ILProcessor il = body.GetILProcessor();
        Instruction first = body.Instructions[0];
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg, param));
        il.InsertBefore(first, Instruction.Create(OpCodes.Call, module.ImportReference(toIl2Cpp)));
        il.InsertBefore(first, Instruction.Create(OpCodes.Castclass, module.ImportReference(il2cppType)));
        il.InsertBefore(first, Instruction.Create(OpCodes.Stloc, local));
    }

    // Does this instruction LOAD the argument at `argIndex` (positional ldarg.0-3, or ldarg/ldarg.s by param)?
    // A reference param (container proxy) and a ref-bearing value proxy (Il2CppSystem.Nullable is a CLASS) are both
    // loaded by ldarg, never ldarga — so this redirect covers every use.
    static bool LoadsArg(Instruction ins, int argIndex, ParameterDefinition param) => ins.OpCode.Code switch
    {
        Code.Ldarg_0 => argIndex == 0,
        Code.Ldarg_1 => argIndex == 1,
        Code.Ldarg_2 => argIndex == 2,
        Code.Ldarg_3 => argIndex == 3,
        Code.Ldarg or Code.Ldarg_S => ReferenceEquals(ins.Operand, param),
        _ => false,
    };
}
