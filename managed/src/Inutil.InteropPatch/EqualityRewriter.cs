using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Inutil.InteropPatch;

// The EQUALITY-PAIRING pass: reconnect the proxy hierarchy's equality members to the System.Object slots the CLR
// actually dispatches through.
//
// THE DEFECT, read off the generated metadata rather than inferred. Il2CppInterop gives Il2CppSystem.Object its own
// GetHashCode() / Equals(Il2CppSystem.Object) / ToString(), and emits them **NEWSLOT** — brand-new virtual slots that
// do not override System.Object's. Every proxy below inherits from that root, so the whole tree's equality hangs off
// a parallel slot the CLR never reaches through `object`:
//
//     Il2CppSystem.Object::GetHashCode()              virt=True newslot=TRUE   <- a NEW slot, not an override
//     UnityEngine.AnimationCurve::GetHashCode()       virt=True newslot=False  <- overrides the NEW slot, not Object's
//
// Measured in a booted game, one Event object wrapped by two proxies:
//
//     a.GetHashCode() / b.GetHashCode()          -> 28991153 / 59593788   (System.Object's — WRAPPER identity)
//     GetHashCode.Invoke(a) / .Invoke(b)         -> 37 / 37               (the game's, via the generated body)
//
// The generated override is real and correct; nothing calls it. So EqualityComparer<T>.Default, Dictionary, HashSet,
// List.Contains and object.Equals all see wrapper identity for every proxy — `dict[new MongoID(id)]` cannot hit an
// entry the game put there, and two proxies over the SAME il2cpp object land in a HashSet twice. Nothing throws; the
// lookup is simply always wrong. (A statically-typed `mongoId.GetHashCode()` compiles to a direct `call` and DOES
// reach the game's, which is how this hid: probe it that way and the hash looks perfect.)
//
// THE FIX IS AT THE ROOT, not per type. Two edits to Il2CppSystem.Object and the entire hierarchy reconnects, because
// every derived member already overrides ITS slots:
//   * GetHashCode() — clear NEWSLOT. Its signature already matches System.Object::GetHashCode, so as a ReuseSlot it
//     becomes the override the generator should have emitted, and every derived GetHashCode chains into Object's slot.
//   * Equals(object) — ADD one. It cannot be fixed by a flag: the generated Equals takes Il2CppSystem.Object, a
//     different signature, so it can never fill Object::Equals(object)'s slot. The added override forwards to the
//     virtual Equals(Il2CppSystem.Object), which dispatches to whatever the runtime type declares — the game's own
//     equality, content and all.
//
// The pair stays CONSISTENT by construction: both members now resolve to the same game object through the same
// dispatch, so equal implies same hash. Pairing only one of them is worse than pairing neither — a real Equals over
// a wrapper-identity hash puts equal keys in different buckets, which is precisely what the in-game battery caught
// when this pass was first written per-type.
//
// SCOPE — deliberately NOT game-scoped, unlike every other pass here. The rest naturalize a consumer's spellings, so
// "a framework proxy's own BCL types are not ours to flip" is right for them. This is a property of Il2CppInterop's
// GENERATION and it lives in Il2Cppmscorlib, which is exactly the module those passes skip. A Dictionary keyed by
// UnityEngine.AnimationCurve is broken the same way one keyed by EFT.MongoID is.
//
// NOT FIXED, same root cause: ToString() is newslot too, so `$"{proxy}"` prints the proxy's type name rather than the
// game's string. Reconnecting it would change the text of every log line in every consumer — a behaviour change worth
// making deliberately, not as a side effect of an equality fix. It is reported by Shadowed(), never silently.
public sealed class EqualityRewriter
{
    const string Il2CppObjectFullName = "Il2CppSystem.Object";
    const string Il2CppObjectBaseFullName = "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase";

    public RewriteResult RewriteModule(ModuleDefinition module)
    {
        var flips = new List<string>();
        var defers = new List<string>();

        TypeDefinition? root = module.GetTypes().FirstOrDefault(t => t.FullName == Il2CppObjectFullName);
        if (root is null) return new RewriteResult(0, flips, defers);   // not the module that declares the proxy root

        int flipped = 0;

        MethodDefinition? hash = root.Methods.FirstOrDefault(IsGetHashCode);
        if (hash is null)
            defers.Add($"{Il2CppObjectFullName}  (equality pairing -> DEFER: no GetHashCode() to reconnect)");
        else if (hash.IsNewSlot)
        {
            hash.IsNewSlot = false;   // ReuseSlot: the signature already matches System.Object::GetHashCode
            flipped++;
            flips.Add($"{Il2CppObjectFullName}::GetHashCode()  (NEWSLOT cleared -> overrides System.Object::GetHashCode; the whole proxy tree chains)");
        }

        if (!root.Methods.Any(IsEqualsObject))
        {
            MethodDefinition? typed = root.Methods.FirstOrDefault(m => IsEqualsOf(m, Il2CppObjectFullName));
            if (typed is null)
                defers.Add($"{Il2CppObjectFullName}  (equality pairing -> DEFER: no Equals({Il2CppObjectFullName}) to forward to)");
            else
            {
                root.Methods.Add(ForwardEquals(module, root, typed));
                flipped++;
                flips.Add($"{Il2CppObjectFullName}::Equals(object)  (added -> forwards to the virtual Equals({Il2CppObjectFullName}))");
            }
        }

        return new RewriteResult(flipped, flips, defers);
    }

    // Every type still SHADOWING a System.Object virtual with a parallel newslot of the same signature — the fact this
    // pass exists to remove, stated over the post-patch tree rather than over the pass's own bookkeeping, so a
    // generator that starts shadowing somewhere new is caught without editing this check. ToString is reported and
    // deliberately not fixed (see the header); it is named so, not omitted.
    public static IEnumerable<string> Shadowed(ModuleDefinition module)
    {
        foreach (TypeDefinition type in module.GetTypes())
        {
            if (!DerivesFromIl2CppObjectBase(type)) continue;
            foreach (MethodDefinition m in type.Methods)
            {
                if (!m.IsVirtual || !m.IsNewSlot) continue;
                if (IsGetHashCode(m) || IsEqualsObject(m))
                    yield return $"{type.FullName}::{m.Name} shadows System.Object with a parallel newslot";
            }
        }
    }

    // public override bool Equals(object obj) => obj is Il2CppSystem.Object o && this.Equals(o);
    //
    // `isinst` rather than a wrapper allocation: every game proxy already derives from Il2CppSystem.Object, so the
    // argument IS one when it is comparable at all, and anything else (a string, a boxed int, null) is correctly not
    // equal. The call is CALLVIRT on purpose — it must dispatch to the runtime type's own Equals, which is the whole
    // point of forwarding rather than comparing pointers here.
    static MethodDefinition ForwardEquals(ModuleDefinition module, TypeDefinition root, MethodDefinition typed)
    {
        var m = new MethodDefinition("Equals",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.ReuseSlot,
            module.TypeSystem.Boolean);
        m.Parameters.Add(new ParameterDefinition("obj", ParameterAttributes.None, module.TypeSystem.Object));
        m.Body = new MethodBody(m) { InitLocals = true };
        m.Body.Variables.Add(new VariableDefinition(root));

        ILProcessor il = m.Body.GetILProcessor();
        Instruction notAProxy = Instruction.Create(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, root);
        il.Emit(OpCodes.Stloc_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Brfalse_S, notAProxy);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Callvirt, typed);
        il.Emit(OpCodes.Ret);
        il.Append(notAProxy);
        il.Emit(OpCodes.Ret);
        return m;
    }

    static bool IsGetHashCode(MethodDefinition m)
        => m.Name == "GetHashCode" && m.Parameters.Count == 0 && m.ReturnType.FullName == "System.Int32" && m.IsVirtual;

    static bool IsEqualsObject(MethodDefinition m) => IsEqualsOf(m, "System.Object");

    static bool IsEqualsOf(MethodDefinition m, string paramFullName)
        => m.Name == "Equals" && m.Parameters.Count == 1 && m.IsVirtual
           && m.ReturnType.FullName == "System.Boolean"
           && m.Parameters[0].ParameterType.FullName == paramFullName;

    static bool DerivesFromIl2CppObjectBase(TypeDefinition type)
    {
        TypeReference? b = type.BaseType;
        for (int depth = 0; b is not null && depth < 16; depth++)
        {
            if (b.FullName == Il2CppObjectBaseFullName) return true;
            TypeDefinition? bd;
            try { bd = b.Resolve(); } catch { return false; }
            if (bd is null) return false;
            b = bd.BaseType;
        }
        return false;
    }
}
