using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Inutil.InteropPatch;

// The POOL-RETARGET pass: every IMPLICIT "materialize this pointer as the declared type" primitive a generated
// proxy calls becomes a call to inutil's own materializer, so a proxy is built at the object's ACTUAL il2cpp class
// rather than at the type of the seam it was read through (docs/reference/exact-proxy-types.md).
//
// THE DEFECT, read off Il2CppInterop's source rather than inferred. Il2CppObjectPool.Get<T> reads the object's real
// class — and uses it ONLY to test RuntimeSpecificsStore.IsInjected. It then builds T, the DECLARED static type,
// via InitializerStore<T>. So a Boss read through an Entity-typed property arrives as an Entity proxy, `is Boss` is
// FALSE, and a C# switch over subtypes matches nothing. Nothing throws; code that constructs its own objects sees
// `is`/`switch` work perfectly, right up until it is handed an object that came from the game.
//
// TWO PRIMITIVES, NOT ONE — and finding that out cost an in-game run. The pool is what a CONCRETELY-typed read
// calls (a property returning Entity). A read whose element type is a generic PARAMETER — `List`1::get_Item`, an
// array indexer — calls IL2CPP.PointerToValueGeneric<T> instead, which reaches the pool one frame deeper, INSIDE
// Il2CppInterop.Runtime, which we do not patch. Retargeting only the pool fixed every property/field seam and left
// every container ELEMENT base-typed: the property case went green while the thing consumers actually complain
// about did not. Hence a TABLE of primitives rather than one hard-coded name, and a postcondition (`Remaining`)
// phrased over the whole table — a third primitive shows up as a residual instead of as a silent half-fix.
//
// NOT RETARGETED, deliberately: `Il2CppObjectBase.Cast<T>` / `TryCast<T>`. Those are EXPLICIT — the caller named
// the type it wants — and they are the documented escape hatch a mod uses to recover a derived type by hand. This
// pass exists to fix the IMPLICIT materializations, where nobody chose the type at all. (Both spellings keep
// working either way: exact typing only ever hands back a subtype of what was asked for.)
//
// WHY THIS SEAM. Every generated proxy — a property body, a field wrapper, a method return, a container's element
// read — materializes through these primitives, so this is a single choke point rather than a per-seam rewrite.
// Mechanically it is the simplest pass here: no signature changes, no bodies rebuilt, no slot lockstep — the
// operand's declaring type is swapped and the generic argument, the parameters and the return type stay verbatim.
//
// SCOPE — deliberately NOT game-scoped, like EqualityRewriter and unlike the naturalizing families. Those flip a
// consumer's own spellings, so "a framework proxy's BCL types are not ours" is right for them. This one has to
// cover Il2Cppmscorlib in particular: the element read of `List<Entity>` runs inside Il2CppSystem.List's OWN proxy.
//
// IDEMPOTENT by construction: a retargeted call no longer names an Il2CppInterop primitive, so a re-run matches
// nothing.
public sealed class PoolRetargetRewriter
{
    public const string PoolFullName = "Il2CppInterop.Runtime.Runtime.Il2CppObjectPool";
    public const string GetName = "Get";
    public const string ApiFullName = "Il2CppInterop.Runtime.IL2CPP";
    public const string PointerToValueName = "PointerToValueGeneric";

    // The IMPLICIT pointer -> T primitives, with the exact parameter shapes they are matched by. The shapes are
    // checked rather than assumed: this is the one pass that keys on Il2CppInterop INTERNALS rather than on a
    // schema row, so if a future Il2CppInterop changes a primitive it must stop matching (leaving today's
    // behaviour) instead of retargeting something that no longer means what we think it does.
    static readonly (string Type, string Name, string[] Params)[] Primitives =
    {
        (PoolFullName, GetName, new[] { "System.IntPtr" }),
        (ApiFullName, PointerToValueName, new[] { "System.IntPtr", "System.Boolean", "System.Boolean" }),
    };

    public RewriteResult RewriteModule(ModuleDefinition module)
    {
        var flips = new List<string>();
        var defers = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        WrapHelpers? wrap = null;   // built lazily: a module with no materialization site must not gain an Inutil reference
        foreach (MethodDefinition m in module.GetTypes().SelectMany(t => t.Methods))
        {
            if (!m.HasBody) continue;
            foreach (Instruction i in m.Body.Instructions)
            {
                if (i.Operand is not GenericInstanceMethod call || Match(call) is not { } which) continue;
                wrap ??= new WrapHelpers(module);
                TypeReference arg = call.GenericArguments[0];
                i.Operand = which.Name == GetName ? wrap.ExactGetClosed(arg) : wrap.ExactPointerToValueClosed(arg);
                counts[which.Name] = counts.TryGetValue(which.Name, out int c) ? c + 1 : 1;
            }
        }

        int sites = counts.Values.Sum();
        if (sites > 0)
            flips.Add($"{string.Join(" + ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}<T> x{kv.Value}"))}  ->  " +
                      "Inutil.Marshal.Il2CppObjects (exact-type materialization)");

        return new RewriteResult(sites, flips, defers);
    }

    // Which primitive this call is, or null. Public because the tests assert over the same rule the pass applies —
    // never a second copy of "what counts as a materialization site".
    public static (string Type, string Name)? Match(GenericInstanceMethod call)
    {
        MethodReference em = call.ElementMethod;
        if (call.GenericArguments.Count != 1 || em.HasThis) return null;
        foreach ((string type, string name, string[] ps) in Primitives)
        {
            if (em.Name != name || em.DeclaringType?.FullName != type) continue;
            if (em.Parameters.Count != ps.Length) continue;
            bool ok = true;
            for (int k = 0; k < ps.Length; k++)
                if (em.Parameters[k].ParameterType.FullName != ps[k]) { ok = false; break; }
            if (ok) return (type, name);
        }
        return null;
    }

    // Every remaining implicit materialization in the module — the pass's POSTCONDITION, read off the post-patch
    // module rather than off its own bookkeeping (the same shape as EqualityRewriter.Shadowed). A site this pass
    // could not see is a seam where type identity is still a fact about how you reached the object, so it is
    // reported rather than assumed absent. Matched by NAME + declaring type only (not by parameter shape), so a
    // primitive whose signature drifted out of the table above still shows up here instead of vanishing.
    public static IEnumerable<string> Remaining(ModuleDefinition module)
    {
        foreach (TypeDefinition t in module.GetTypes())
            foreach (MethodDefinition m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (Instruction i in m.Body.Instructions)
                {
                    if (i.Operand is not MethodReference mr) continue;
                    foreach ((string type, string name, _) in Primitives)
                        if (mr.Name == name && mr.DeclaringType?.FullName == type)
                            yield return $"{t.FullName}::{m.Name} still calls {type}::{name}";
                }
            }
    }
}
