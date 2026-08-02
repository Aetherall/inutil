using System.Collections;
using System.Reflection;

namespace Inutil.Battery;

// EqualityRewriter, END-TO-END: a patched proxy's Equals(object) actually reaches the source its GetHashCode came
// from, in a real il2cpp runtime, through a real managed hash container.
//
// THE DEFECT. Il2CppInterop emits an override of GetHashCode() (its signature matches System.Object's) but never one
// of Equals(object) (the game's Equals takes il2cpp's Object — a sibling overload, not an override), and
// Il2CppObjectBase overrides neither. So EqualityComparer<T>.Default resolves to ObjectEqualityComparer, which hashes
// by the GAME's rule and compares by WRAPPER identity. Every managed Dictionary/HashSet/Contains over a proxy finds
// the right bucket and then refuses the entry: the lookup compiles, runs, throws nothing, and can never hit.
//
// WHAT IS ASSERTED, and why it is the container and not just the method: the fix is only worth anything if
// `dict[key]` starts hitting, so each case drives a real Dictionary<T,int> — the shape a consumer actually writes.
// NON-VACUITY is asserted first in every case: the two proxies must be DISTINCT managed objects, or an
// Equals that returned reference-identity would pass having proven nothing.
//
// FIXTURE NOTE. The content-equality case (a type whose typed Equals compares VALUES — the shape that motivated the
// pass) needs ToyGame.ItemId, which exists only after a ToyGame rebuild; it SKIPs on an older fixture, exactly as the
// Callback cases do. The two cases that run on any build cover the other half — a typed Equals forwarded to, and the
// pointer floor — using proxies the generated tree already contains.
public static class EqualityCases
{
    public static void Register(Suite suite)
    {
        // A typed Equals(T) exists -> the emitted Equals(object) forwards to it, so two proxies over the SAME il2cpp
        // object compare equal and a dictionary keyed by one is found by the other. Before the pass this is false for
        // every proxy in the tree, however the game itself defines equality.
        suite.Add("equality.typed.forwards", () =>
        {
            Type t = FindProxyType("UnityEngine.AnimationCurve");
            (object a, object b) = TwoWrappers(t);
            Check.True(!ReferenceEquals(a, b), "non-vacuity: the two wrappers must be distinct managed objects");

            Check.True(a.Equals(b), "Equals(object) did not forward to the type's own typed Equals — the proxy is unpaired");
            Check.True(DictionaryRoundTrip(t, a, b, out string detail),
                $"a Dictionary keyed by one wrapper was not found by the other ({detail}) — the hash/equals pair is still split");
            return $"AnimationCurve: distinct wrappers over one object compare equal and share a dict entry ({detail})";
        });

        // No equality member to forward to -> the pointer floor. Sound with ANY GetHashCode (pointer-equal implies
        // the same object implies the same hash), and strictly better than the wrapper identity it replaces.
        suite.Add("equality.pointer.floor", () =>
        {
            Type t = FindProxyType("UnityEngine.Event");
            (object a, object b) = TwoWrappers(t);
            Check.True(!ReferenceEquals(a, b), "non-vacuity: the two wrappers must be distinct managed objects");

            Check.True(a.Equals(b), "Equals(object) did not fall back to Pointer identity — the proxy is unpaired");
            Check.True(DictionaryRoundTrip(t, a, b, out string detail),
                $"a Dictionary keyed by one wrapper was not found by the other ({detail})");
            return $"Event: pointer identity pairs with the game's hash ({detail})";
        });

        // The shape the pass was written for: a ref-bearing value type whose typed Equals compares CONTENT. Two
        // SEPARATELY MINTED proxies (not two wrappers over one object) must compare equal, which only the forwarded
        // typed Equals can deliver — the pointer floor would say false here.
        suite.Add("equality.content.distinct-objects", () =>
        {
            Type? t = TryFindProxyType("ToyGame.ItemId") ?? TryFindProxyType("Il2CppToyGame.ItemId");
            if (t is null) Check.Skip("ToyGame.ItemId absent — fixture predates the equality types (rebuild ToyGame)");

            MethodInfo mint = FindProxy("Game", "MintItem");
            object game = Construct(mint.DeclaringType!);
            object a = mint.Invoke(game, new object[] { "54cb50c76803fa8b248b4571" })!;
            object b = mint.Invoke(game, new object[] { "54cb50c76803fa8b248b4571" })!;
            Check.True(!ReferenceEquals(a, b), "non-vacuity: the two ids must be distinct managed objects");
            Check.True(a.GetHashCode() == b.GetHashCode(),
                "precondition: equal content must already hash equal (that half was never broken)");

            Check.True(a.Equals(b),
                "two separately minted ItemIds with equal content compare UNEQUAL — Equals(object) is not forwarding to the content Equals");
            Check.True(DictionaryRoundTrip(t!, a, b, out string detail),
                $"a Dictionary keyed by one id was not found by an equal-content id ({detail}) — the lookup a consumer writes still misses");
            return $"ItemId: equal content, distinct objects, one dict entry ({detail})";
        });
    }

    // Build Dictionary<T,int>, store under `key`, look up under `probe` — the operation a consumer actually writes,
    // and the one that stayed broken while Equals looked fine. Also asserts the pair is CONSISTENT (one entry, not
    // two), which catches an Equals that says true while the hash disagrees.
    static bool DictionaryRoundTrip(Type t, object key, object probe, out string detail)
    {
        var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(t, typeof(int)))!;
        dict[key] = 7;
        dict[probe] = 9;                                        // an equal key must OVERWRITE, never add a second entry
        bool found = dict.Contains(probe);
        int count = dict.Count;
        detail = $"count={count}, contains={found}";
        return found && count == 1;
    }

    // A second proxy over the SAME il2cpp object: read the first's Pointer and rebuild the typed wrapper from it.
    // Il2CppInterop generates a .ctor(IntPtr) for every proxy; construction failure is an honest SKIP.
    static (object, object) TwoWrappers(Type t)
    {
        object a = Construct(t);
        PropertyInfo ptr = t.GetProperty("Pointer")
            ?? throw new AssertException($"{t.FullName} has no Pointer — not an Il2CppObjectBase proxy");
        object raw = ptr.GetValue(a)!;
        ConstructorInfo ctor = t.GetConstructor(new[] { typeof(IntPtr) })
            ?? throw new AssertException($"{t.FullName} has no .ctor(IntPtr) — cannot build a second wrapper");
        object b;
        try { b = ctor.Invoke(new[] { raw }); }
        catch (Exception ex) { Check.Skip($"second wrapper over {t.FullName} not constructible: {ex.GetType().Name}: {ex.Message}"); throw; }
        return (a, b);
    }

    static object Construct(Type t)
    {
        try { return t.GetConstructor(Type.EmptyTypes)?.Invoke(null) ?? Activator.CreateInstance(t)!; }
        catch (Exception ex) { Check.Skip($"il2cpp proxy construction of {t.FullName} not reachable: {ex.GetType().Name}: {ex.Message}"); throw; }
    }

    static Type FindProxyType(string fullName)
        => TryFindProxyType(fullName) ?? throw new AssertException($"{fullName} proxy not found — its interop assembly did not load");

    static Type? TryFindProxyType(string fullName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t;
            try { t = asm.GetType(fullName, throwOnError: false); }
            catch { continue; }
            if (t is not null) return t;
        }
        return null;
    }

    static MethodInfo FindProxy(string typeSimpleName, string methodName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x is not null).ToArray()!; }
            catch { continue; }
            Type? t = types.FirstOrDefault(x => x.Name == typeSimpleName && x.GetMethod(methodName) is not null);
            if (t is not null) return t.GetMethod(methodName)!;
        }
        throw new AssertException($"{typeSimpleName}::{methodName} proxy not found");
    }
}
