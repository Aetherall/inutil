using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.InteropPatch;
using Inutil.Schema;

namespace Inutil.InteropPatch.Tests;

// Offline + synthetic (no game, no wine, no provisioned proxies): the non-virtual Task pass flips GENERIC methods
// too — including the nested Task<Result<!!0>> shape where the method's own generic parameter lives INSIDE the Task
// element (EftClientBackendSession.Send<T>(..) : Task<Result<T>>). Built entirely with Cecil.
//
// Three synthetic non-virtual methods on one game (non-framework) module exercise every branch of the flip:
//   * Send<T>(string, Callback<T>) : Il2CppTask`1<GameResult`1<!!0>>  — the NEW containing case (own-param nested)
//   * LoadTyped<T>()               : Il2CppTask`1<!!0>                — the bare own-param case (regression)
//   * Commit()                     : Il2CppTask                        — the non-generic case (regression)
// The il2cpp/System Task names come from the registry (Families), never a literal.
static class NonVirtualGenericTaskFlipTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]" + (ok || detail is null ? "" : $"  -- {detail}"));
            if (!ok) failures++;
        }

        var reg = Families.Default();
        string il2cppTaskT = reg.ByConvKind(ConvKind.Task, 1)!.Il2CppFullName;      // Il2CppSystem…Task`1
        string il2cppTask  = reg.ByConvKind(ConvKind.Task, 0)!.Il2CppFullName;      // Il2CppSystem…Task
        string sysTaskT    = reg.ByConvKind(ConvKind.Task, 1)!.BclOpenType.FullName!; // System.Threading.Tasks.Task`1
        string sysTask     = reg.ByConvKind(ConvKind.Task, 0)!.BclOpenType.FullName!; // System.Threading.Tasks.Task

        static (string Ns, string Name) Split(string full)
        {
            int i = full.LastIndexOf('.');
            return (full[..i], full[(i + 1)..]);
        }
        var (taskTNs, taskTName) = Split(il2cppTaskT);
        var (taskNs, taskName)   = Split(il2cppTask);

        // A GAME (non-framework) proxy module — CecilProjector.IsFrameworkAssembly("ToyGame.Core") is false, so the
        // non-virtual pass runs. GameResult`1 lives in a SEPARATE assembly ref so "imported" is a real cross-assembly
        // import (the flipped element must be re-scoped into this module, not left dangling).
        var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("ToyGame.Core", new Version(1, 0, 0, 0)), "ToyGame.Core", ModuleKind.Dll);
        ModuleDefinition module = asm.MainModule;
        var il2cppCore = new AssemblyNameReference("Il2Cppmscorlib", new Version(1, 0, 0, 0));
        var gameData   = new AssemblyNameReference("ToyGame.Data", new Version(1, 0, 0, 0));
        module.AssemblyReferences.Add(il2cppCore);
        module.AssemblyReferences.Add(gameData);

        // ── synthetic type builders (open type refs mirror WrapHelpers.MakeSysTaskT: add one GenericParameter) ──
        GenericInstanceType Il2CppTaskTOf(TypeReference elem)
        {
            var open = new TypeReference(taskTNs, taskTName, module, il2cppCore);
            open.GenericParameters.Add(new GenericParameter(open));
            var inst = new GenericInstanceType(open);
            inst.GenericArguments.Add(elem);
            return inst;
        }
        TypeReference Il2CppTaskBare() => new TypeReference(taskNs, taskName, module, il2cppCore);
        GenericInstanceType GenericOf(string ns, string name, TypeReference arg)
        {
            var open = new TypeReference(ns, name, module, gameData);
            open.GenericParameters.Add(new GenericParameter(open));
            var inst = new GenericInstanceType(open);
            inst.GenericArguments.Add(arg);
            return inst;
        }

        var session = new TypeDefinition("ToyGame.Core", "Session",
            TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(session);

        // A body with a single ret and no exception handler -> CanApplyFlip true. ldnull leaves a reference on the
        // stack for the spliced WrapTaskT call (structural round-trip only; never JITed offline).
        MethodDefinition NonVirtual(string name)
        {
            var m = new MethodDefinition(name, MethodAttributes.Public, module.TypeSystem.Void);
            session.Methods.Add(m);
            return m;
        }
        static void FillBody(MethodDefinition m)
        {
            ILProcessor il = m.Body.GetILProcessor();
            il.Append(Instruction.Create(OpCodes.Ldnull));
            il.Append(Instruction.Create(OpCodes.Ret));
        }

        // Send<T>(string, GameCallback`1<!!0>) : Il2CppTask`1<GameResult`1<!!0>>  — the NEW containing case.
        var send = NonVirtual("Send");
        var sendT = new GenericParameter("T", send);
        send.GenericParameters.Add(sendT);
        send.Parameters.Add(new ParameterDefinition("request", ParameterAttributes.None, module.TypeSystem.String));
        send.Parameters.Add(new ParameterDefinition("callback", ParameterAttributes.None, GenericOf("ToyGame.Data", "GameCallback`1", sendT)));
        send.ReturnType = Il2CppTaskTOf(GenericOf("ToyGame.Data", "GameResult`1", sendT));
        FillBody(send);

        // LoadTyped<T>() : Il2CppTask`1<!!0>  — the bare own-param regression.
        var loadTyped = NonVirtual("LoadTyped");
        var loadT = new GenericParameter("T", loadTyped);
        loadTyped.GenericParameters.Add(loadT);
        loadTyped.ReturnType = Il2CppTaskTOf(loadT);
        FillBody(loadTyped);

        // Commit() : Il2CppTask  — the non-generic regression.
        var commit = NonVirtual("Commit");
        commit.ReturnType = Il2CppTaskBare();
        FillBody(commit);

        // ── the pass ──
        var rewriter = new TaskProxyRewriter();
        RewriteResult result = rewriter.RewriteModule(module);
        Console.WriteLine($"\n>> synthetic ToyGame.Core: non-virtual pass flipped {result.Flipped}");
        foreach (string f in result.Flips) Console.WriteLine($"     FLIP  {f}");

        // ── the NEW case: Send<T> : System.Task`1<GameResult`1<!!0>> with !!0 owned by Send + GameResult imported ──
        var ret = send.ReturnType as GenericInstanceType;
        Check("Send<T> return flips to System.Threading.Tasks.Task`1",
            ret?.ElementType.FullName == sysTaskT, send.ReturnType.FullName);
        var inner = ret is { GenericArguments.Count: 1 } ? ret.GenericArguments[0] as GenericInstanceType : null;
        Check("Send<T> Task element stays the nested GameResult`1 instance (shape not collapsed)",
            inner?.ElementType.FullName == "ToyGame.Data.GameResult`1", inner?.FullName);
        TypeReference? innerArg = inner is { GenericArguments.Count: 1 } ? inner.GenericArguments[0] : null;
        Check("Send<T> inner GameResult arg is the method's OWN generic parameter (!!0, owner == the method)",
            innerArg is GenericParameter gp && ReferenceEquals(gp.Owner, send),
            innerArg is GenericParameter g2 ? $"owner={g2.Owner}" : innerArg?.FullName);
        Check("Send<T> GameResult`1 element is IMPORTED into this module (scoped to ToyGame.Data)",
            inner?.ElementType.Scope?.Name == "ToyGame.Data", inner?.ElementType.Scope?.Name);

        // ── regression: bare own-param + non-generic still flip exactly as before ──
        var ltRet = loadTyped.ReturnType as GenericInstanceType;
        Check("bare-param LoadTyped<T> flips to System.Task`1 with !!0 preserved raw (owner == the method)",
            ltRet?.ElementType.FullName == sysTaskT
            && ltRet.GenericArguments[0] is GenericParameter lgp && ReferenceEquals(lgp.Owner, loadTyped),
            loadTyped.ReturnType.FullName);
        Check("non-generic Commit flips to System.Threading.Tasks.Task",
            commit.ReturnType.FullName == sysTask, commit.ReturnType.FullName);

        // ── idempotent: a second pass flips 0 (every member is already System.Task) ──
        Check("idempotent: re-running the non-virtual pass flips 0",
            rewriter.RewriteModule(module).Flipped == 0);

        // ── round-trip: the rewritten module must WRITE + RE-READ with the nested flip intact (sound IL) ──
        using var ms = new MemoryStream();
        module.Write(ms);
        ms.Position = 0;
        var reloaded = ModuleDefinition.ReadModule(ms);
        var rSend = reloaded.GetTypes().SelectMany(t => t.Methods).First(m => m.Name == "Send");
        var rRet = rSend.ReturnType as GenericInstanceType;
        var rInner = rRet is { GenericArguments.Count: 1 } ? rRet.GenericArguments[0] as GenericInstanceType : null;
        Check("round-trip: Send<T> flip survives Cecil write + re-read (System.Task`1<GameResult`1<!!0>>)",
            rRet?.ElementType.FullName == sysTaskT
            && rInner?.ElementType.FullName == "ToyGame.Data.GameResult`1"
            && rInner.GenericArguments[0] is GenericParameter rgp && ReferenceEquals(rgp.Owner, rSend));
        reloaded.Dispose();
        module.Dispose();

        return failures;
    }
}
