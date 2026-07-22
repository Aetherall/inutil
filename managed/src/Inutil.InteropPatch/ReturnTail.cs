using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Inutil.InteropPatch;

// The shared return-tail rewrite: route every `ret` in a body through `call <helper>` so the value on the stack rides
// into the helper and the helper's result is what the (flipped) method returns. Append one `WRAP: call helper; ret`
// tail and turn each existing `ret` into `br WRAP` — a branch target that pointed at a `ret` still reaches the wrap.
// ONE implementation, shared by every return family (the Task carrier's WrapTask, the container flip's
// Il2CppMarshal.ToManaged), instead of a copy per seam.
public static class ReturnTail
{
    public static void RouteThroughHelper(ModuleDefinition module, MethodDefinition method, MethodReference wrapHelper)
    {
        MethodBody body = method.Body;
        MethodReference helper = module.ImportReference(wrapHelper);
        var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();
        ILProcessor il = body.GetILProcessor();
        var call = Instruction.Create(OpCodes.Call, helper);
        var newRet = Instruction.Create(OpCodes.Ret);
        il.Append(call);
        il.Append(newRet);
        foreach (Instruction ret in rets) { ret.OpCode = OpCodes.Br; ret.Operand = call; }
    }
}
