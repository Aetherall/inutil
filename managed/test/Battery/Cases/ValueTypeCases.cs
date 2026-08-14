using System.Collections;
using System.Reflection;

namespace Inutil.Battery;

// ValueTypeBridge, END-TO-END: a ref-bearing value type (ToyGame.Loadout {int Gold; string Owner}) crossing
// the ToIl2Cpp boundary as a tuple ELEMENT and a dict VALUE. The interop-patch flips Game::DescribeStat's
// (int,Loadout) param and Game::SumBook's Dictionary<int,Loadout> param to natural; invoking them with a natural
// container built from a REAL Loadout proxy drives DematerializeTuple / DematerializeDictionary's value-type write.
// The GAME reads the element back (DescribeStat -> "n:Owner#Gold"; SumBook -> a sum over Gold+Owner.Length), so a
// mis-marshaled write shows as a WRONG result — the hazard (Il2CppInterop's generic ctor/set_Item hands the
// wrong representation for a value type with a reference field), caught by the game's own read of the element.
//
// Reflection-only over the proxies (no game-type compile ref), like the flip cases. Il2CppInterop construction is
// version-sensitive -> a construction failure is an honest SKIP; the marshalling write itself fails LOUD.
public static class ValueTypeCases
{
    public static void Register(Suite suite)
    {
        // Nullable-return PRIMITIVE, validated DIRECTLY (bypassing the patcher flip): the SDK's null-aware
        // value-type Nullable invoke on the RAW, unflipped FindSpawn/PreferredSide. Il2CppInterop's own value-type
        // Nullable return is broken BOTH ways (verified: FindSpawn(0) NREs, FindSpawn(3) reads HasValue=False), so
        // ValueTypeBridge.InvokeNullableStructReturn does the invoke itself. This proves the primitive the full-body
        // replace will splice, before the invasive body-rewrite exists: FindSpawn(0) -> null, FindSpawn(3) ->
        // Vec3(3,3,3); PreferredSide(false) -> null, PreferredSide(true) -> a present enum.
        suite.Add("nullable.return.helper.struct", () =>
        {
            MethodInfo fs = FindProxy("Game", "FindSpawn");
            object game = TryConstruct(fs.DeclaringType!, out string why) ?? Skip<object>(why);
            Type vec3T = fs.ReturnType.GetGenericArguments()[0];              // Il2CppSystem.Nullable<Vec3> -> Vec3

            object? empty = InvokeNullableHelper(vec3T, game, "FindSpawn", 0);
            Check.True(empty is null, $"FindSpawn(0) should be an empty Nullable (null), got {empty?.GetType().FullName}");

            object? present = InvokeNullableHelper(vec3T, game, "FindSpawn", 3);   // present -> boxed Vec3
            Check.True(present is not null, "FindSpawn(3) should be a present Nullable, got null");
            object? x = present!.GetType().GetField("X")?.GetValue(present)
                        ?? present.GetType().GetProperty("X")?.GetValue(present);
            Check.True(x is not null && Math.Abs(Convert.ToSingle(x) - 3f) < 0.001f,
                $"FindSpawn(3).X should be 3, got {x?.ToString() ?? "null"} — the value-type Nullable return marshaled wrong");
            return $"null-aware value-type Nullable invoke: FindSpawn(0)=null (empty), FindSpawn(3)=Vec3(X={x}) (present)";
        });

        suite.Add("nullable.return.helper.enum", () =>
        {
            MethodInfo ps = FindProxy("Game", "PreferredSide");
            object game = TryConstruct(ps.DeclaringType!, out string why) ?? Skip<object>(why);
            Type factionT = ps.ReturnType.GetGenericArguments()[0];          // Il2CppSystem.Nullable<Faction> -> Faction

            object? none = InvokeNullableHelper(factionT, game, "PreferredSide", false);
            Check.True(none is null, $"PreferredSide(false) should be an empty Nullable (null), got {none?.GetType().FullName}");
            object? some = InvokeNullableHelper(factionT, game, "PreferredSide", true);   // present -> boxed Faction (Bear)
            Check.True(some is not null, "PreferredSide(true) should be a present Nullable, got null");
            return $"null-aware enum Nullable invoke: PreferredSide(false)=null (empty), PreferredSide(true)={some} (present)";
        });

        suite.Add("valuetype.tuple-element.write", () =>
        {
            MethodInfo describe = FindProxy("Game", "DescribeStat");
            object game = TryConstruct(describe.DeclaringType!, out string why) ?? Skip<object>(why);
            object loadout = MakeLoadout("hero", 5);                         // a real Loadout proxy {Gold=5, Owner="hero"}
            Type tupleT = typeof(ValueTuple<,>).MakeGenericType(typeof(int), loadout.GetType());
            object tuple = Activator.CreateInstance(tupleT, new object[] { 7, loadout })!;   // natural (int, Loadout)
            string desc = Invoke<string>(describe, game, tuple);
            Check.True(desc == "7:hero#5", $"DescribeStat read a garbled ref-bearing VT after the write: got '{desc}', expected '7:hero#5'");
            return $"(int,Loadout) tuple write round-tripped through the game's own read: '{desc}'";
        });

        suite.Add("valuetype.dict-value.write", () =>
        {
            // dict-VALUE ref-bearing VT, the follow-up the tuple case deferred: unlike a tuple's named Item
            // fields (patched via il2cpp_field_set_value), a Dictionary stores values in internal entry storage
            // reached only through set_Item — whose Il2CppInterop wrapper mis-marshals a value type with a reference
            // field (verified earlier: SumBook NRE'd / summed garbage). ValueTypeBridge.InvokeUnboxed now raw-invokes
            // set_Item with the value passed UNBOXED. Build a natural Dictionary<int,Loadout> {3: {Gold=5, Owner=
            // "hero"}}; the flipped SumBook drives DematerializeDictionary's value-type write; the GAME sums
            // key + Gold + Owner.Length = 3 + 5 + 4 = 12, so a mis-marshaled value shows as a wrong sum or an NRE.
            MethodInfo sumBook = FindProxy("Game", "SumBook");
            object game = TryConstruct(sumBook.DeclaringType!, out string why) ?? Skip<object>(why);
            object loadout = MakeLoadout("hero", 5);                          // Loadout {Gold=5, Owner="hero"}
            Type dictT = typeof(Dictionary<,>).MakeGenericType(typeof(int), loadout.GetType());
            var book = (IDictionary)Activator.CreateInstance(dictT)!;         // natural Dictionary<int, Loadout>
            book.Add(3, loadout);
            int sum = Invoke<int>(sumBook, game, book);
            Check.True(sum == 12, $"SumBook read a garbled ref-bearing VT dict value: got {sum}, expected 12 (3 + Gold 5 + len(\"hero\"))");
            return $"Dictionary<int,Loadout> value write round-tripped through the game's own read: sum={sum}";
        });

        suite.Add("valuetype.list-element.write", () =>
        {
            // list-ELEMENT ref-bearing VT — the C5 List<Loadout> capability, sibling to the dict-value case.
            // Add(T')'s Il2CppInterop wrapper mis-marshals a value type with a reference field exactly as set_Item
            // did (the embedded string dropped, the value garbled). DematerializeList now routes Add through
            // ValueTypeBridge.InvokeUnboxed with the element passed UNBOXED. Build a natural List<Loadout>
            // {{Gold=5,Owner="hero"},{Gold=3,Owner="ally"}}; the flipped SumLoadouts drives DematerializeList's
            // value-type write; the GAME sums Gold+Owner.Length = (5+4)+(3+4) = 16, so a mis-marshaled element shows
            // as a wrong sum or an NRE.
            MethodInfo sum = FindProxy("Game", "SumLoadouts");
            object game = TryConstruct(sum.DeclaringType!, out string why) ?? Skip<object>(why);
            object hero = MakeLoadout("hero", 5);                             // Loadout {Gold=5, Owner="hero"}
            object ally = MakeLoadout("ally", 3);                             // Loadout {Gold=3, Owner="ally"}
            Type listT = typeof(List<>).MakeGenericType(hero.GetType());
            var gear = (IList)Activator.CreateInstance(listT)!;               // natural List<Loadout>
            gear.Add(hero); gear.Add(ally);
            int s = Invoke<int>(sum, game, gear);
            Check.True(s == 16, $"SumLoadouts read a garbled ref-bearing VT list element: got {s}, expected 16 ((5+4)+(3+4))");
            return $"List<Loadout> element write round-tripped through the game's own read: sum={s}";
        });
    }

    // A real Loadout proxy from the game: construct a Player(name) and call Player.MakeLoadout(gold) -> Loadout{gold, name}.
    static object MakeLoadout(string name, int gold)
    {
        MethodInfo mk = FindProxy("Player", "MakeLoadout");
        Type playerT = mk.DeclaringType!;
        ConstructorInfo? ctor = playerT.GetConstructors()
            .FirstOrDefault(c => c.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(string));
        if (ctor is null) Check.Skip($"Player(string) proxy ctor not found on {playerT.FullName}");
        object player;
        try { player = ctor!.Invoke(new object[] { name }); }
        catch (Exception ex) { Check.Skip($"Player proxy construction not reachable: {ex.GetType().Name}: {ex.Message}"); throw; }
        return mk.Invoke(player, new object[] { gold })!;
    }

    static MethodInfo FindProxy(string typeSimpleName, string methodName)
    {
        Assembly? acs = null;
        try { acs = Assembly.Load(new AssemblyName("Assembly-CSharp")); } catch { }
        foreach (Assembly asm in (acs is not null ? new[] { acs } : Array.Empty<Assembly>()).Concat(AppDomain.CurrentDomain.GetAssemblies()))
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
            catch { continue; }
            Type? t = types.FirstOrDefault(x => x.Name == typeSimpleName && x.GetMethod(methodName) is not null);
            if (t is not null) return t.GetMethod(methodName)!;
        }
        throw new AssertException($"{typeSimpleName}::{methodName} proxy not found — the Assembly-CSharp interop proxy did not load");
    }

    static object? TryConstruct(Type proxyType, out string why)
    {
        why = "";
        try { return proxyType.GetConstructor(Type.EmptyTypes)?.Invoke(null) ?? Activator.CreateInstance(proxyType); }
        catch (Exception ex) { why = $"il2cpp proxy construction of {proxyType.FullName} not reachable: {ex.GetType().Name}: {ex.Message}"; return null; }
    }

    // Invoke a flipped proxy method; a marshalling failure surfaces LOUD (not a swallowed skip).
    static T Invoke<T>(MethodInfo mi, object target, object arg)
    {
        try { return (T)mi.Invoke(target, new[] { arg })!; }
        catch (TargetInvocationException tie)
        {
            Check.True(false, $"invoking {mi.Name} threw {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message} — the ToIl2Cpp value-type write likely mis-marshaled");
            throw;
        }
    }

    static T Skip<T>(string why) { Check.Skip(why); throw new SkipException(why); }

    // Reflectively call the deployed SDK's ValueTypeBridge.InvokeNullableStructReturn<valType>(game, method, args) —
    // the null-aware value-type Nullable invoke. Unwraps TargetInvocationException so a real marshalling failure
    // surfaces its own exception. Returns the boxed result (a boxed underlying value for present, null for empty).
    static object? InvokeNullableHelper(Type valType, object game, string method, object arg)
    {
        Assembly sdk = Assembly.Load(new AssemblyName("Inutil"));
        Type vtb = sdk.GetType("Inutil.Marshal.ValueTypeBridge")
            ?? throw new AssertException("Inutil.Marshal.ValueTypeBridge missing from Inutil.dll");
        MethodInfo m = (vtb.GetMethod("InvokeNullableStructReturn")
            ?? throw new AssertException("ValueTypeBridge.InvokeNullableStructReturn missing")).MakeGenericMethod(valType);
        try { return m.Invoke(null, new object?[] { game, method, new object?[] { arg } }); }
        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
    }
}
