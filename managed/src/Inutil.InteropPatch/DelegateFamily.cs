using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// The delegate PARAM family for the IL-rewrite seam (the BclToIl2Cpp direction) — the conversion half of the delegate
// family, the mirror of NullableFamily/ContainerFlip for delegates. It answers, for one proxy method parameter: is
// this an il2cpp delegate wrapper the seam can flip, and if so what is its natural BCL type and which op_Implicit
// dematerializes it. The DETECTION fact (which il2cpp names ARE delegate wrappers — Action/Func by arity) lives in the
// registry (Families.cs); this owns only the CONVERSION (op_Implicit -> natural).
//
// Unlike the value-Nullable/container write bridges (a generic Il2CppMarshal dematerializer), a delegate's bridge is
// Il2CppInterop's OWN `implicit operator Wrapper(System.Xxx)` — shipped per wrapper, spliced at the call site. So the
// natural type and the converter both come from that op_Implicit, resolved off the wrapper here; the actual IL splice
// is the ONE shared ParamFlip.Splice.
public static class DelegateFamily
{
    static readonly CorrespondenceRegistry _families = Families.Default();

    // Resolve a flippable delegate param. Returns false for a non-delegate type OR an il2cpp delegate wrapper that
    // carries no BCL-sourced op_Implicit (a custom game delegate we cannot give a natural spelling — leave it raw).
    //   natural          — the BCL surface type (System.Action / Func<..>), closed over the wrapper's type args.
    //   opImplicitClosed — the wrapper's `op_Implicit(System.Xxx)`, closed the same way, to splice at method entry.
    public static bool TryResolve(ModuleDefinition module, TypeReference pt,
                                  out TypeReference natural, out MethodReference opImplicitClosed)
    {
        natural = null!;
        opImplicitClosed = null!;

        // Registry gate — "is this an Il2CppSystem.Action/Func of a modeled arity". A non-delegate, or an arity
        // beyond the registered bound, classifies to no Delegate row and is left wrapper-typed (safe).
        if (_families.Classify(CecilTypeRef.Of(pt)) is not { Kind: ConvKind.Delegate }) return false;

        TypeReference[] typeArgs = pt is GenericInstanceType git ? git.GenericArguments.ToArray() : Array.Empty<TypeReference>();

        // Resolve the wrapper to read its op_Implicit signature (the `!0`-based converter — a hand-synthesized concrete
        // signature would not bind at JIT, so Resolve is the correct source). Cecil's Resolve THROWS on an unresolvable
        // assembly (it does not return null), so guard it: a wrapper we cannot resolve is left wrapper-typed, never a
        // crash of the whole directory patch.
        TypeDefinition? wrapperDef;
        try { wrapperDef = (pt is GenericInstanceType g ? g.ElementType : pt).Resolve(); }
        catch (AssemblyResolutionException) { return false; }
        if (wrapperDef is null) return false;

        // The REAL bind gate: Il2CppInterop ships `public static implicit operator Wrapper(System.Xxx)`. The one whose
        // PARAMETER is the System.* delegate is the BCL->il2cpp direction we splice (the reverse op_Implicit's param is
        // the wrapper itself, Il2CppSystem.*, so the namespace check excludes it). No such operator (a custom game
        // delegate coincidentally in a modeled arity) => we cannot name a natural type => leave it raw.
        MethodDefinition? opImpl = wrapperDef.Methods.FirstOrDefault(m =>
            m.IsStatic && m.Name == "op_Implicit" && m.Parameters.Count == 1
            && m.Parameters[0].ParameterType.Namespace == "System");
        if (opImpl is null) return false;

        // Natural BCL surface = op_Implicit's parameter type, closed over the SAME type args as the wrapper. Built by
        // hand (not the Mono.Cecil.Rocks MakeGenericInstanceType extension, which the project does not reference) — the
        // same GenericInstanceType construction WrapHelpers uses for every other closed natural type.
        TypeReference bclElem = opImpl.Parameters[0].ParameterType is GenericInstanceType bg
            ? module.ImportReference(bg.ElementType)
            : module.ImportReference(opImpl.Parameters[0].ParameterType);
        if (typeArgs.Length > 0)
        {
            var closed = new GenericInstanceType(bclElem);
            foreach (TypeReference a in typeArgs) closed.GenericArguments.Add(a);
            natural = closed;
        }
        else natural = bclElem;

        // The converter to splice, closed over the same args (host-instance-generic when the wrapper is generic).
        opImplicitClosed = typeArgs.Length > 0
            ? MakeHostInstanceGeneric(module, opImpl, typeArgs)
            : module.ImportReference(opImpl);
        return true;
    }

    // A MethodReference to `m` (op_Implicit) called on the CLOSED generic instance of its declaring type (e.g.
    // Il2CppSystem.Action`1<int>::op_Implicit). `m` is defined in the WRAPPER's assembly (Il2Cppmscorlib), not the
    // module being patched, so it is imported into `module`. Import the OPEN method FIRST (Cecil resolves the
    // cross-module signature with a proper context), then graft the imported ref onto the closed declaring type — and
    // do NOT re-import the constructed reference. Building from the RAW (un-imported) `m` and then re-importing makes
    // Cecil re-walk op_Implicit's signature with an EMPTY generic context and NRE in ImportGenericContext.
    static MethodReference MakeHostInstanceGeneric(ModuleDefinition module, MethodReference m, params TypeReference[] args)
    {
        MethodReference im = module.ImportReference(m);
        var git = new GenericInstanceType(im.DeclaringType);
        foreach (TypeReference a in args) git.GenericArguments.Add(a);
        var r = new MethodReference(im.Name, im.ReturnType, git)
        {
            HasThis = im.HasThis,
            ExplicitThis = im.ExplicitThis,
            CallingConvention = im.CallingConvention,
        };
        foreach (ParameterDefinition p in im.Parameters) r.Parameters.Add(new ParameterDefinition(p.ParameterType));
        foreach (GenericParameter gp in im.GenericParameters) r.GenericParameters.Add(new GenericParameter(gp.Name, r));
        return r;
    }
}
