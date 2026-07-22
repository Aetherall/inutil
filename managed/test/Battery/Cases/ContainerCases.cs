using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Inutil.Battery;

// ContainerBridge, END-TO-END in a booted IL2CPP game: marshal REAL il2cpp arrays (Il2CppStructArray /
// Il2CppStringArray) to natural BCL arrays through Il2CppMarshal.ToManaged (the Conv tree + Il2CppConvRuntime).
// Offline the SAME materialization loop is proven over plain-array stand-ins (Inutil.Tests); here the element
// source is a genuine il2cpp array allocated in the live runtime.
//
// Reflection-only over Il2CppMarshal, so the battery stays engine-decoupled at compile time (like the Task
// cases); the il2cpp array TYPES come from Il2CppInterop, which the battery already references.
public static class ContainerCases
{
    public static void Register(Suite suite)
    {
        suite.Add("container.array.struct.tomanaged", () =>
        {
            Il2CppStructArray<int> il2cpp = new[] { 5, 6, 7 };     // op_Implicit -> a real il2cpp value array
            int[] r = ToNatural<int[]>(il2cpp);
            Check.True(r is [5, 6, 7], $"got [{string.Join(",", r)}], expected [5,6,7]");
            return $"Il2CppStructArray<int> -> natural int[] [{string.Join(",", r)}]";
        });

        suite.Add("container.array.string.tomanaged", () =>
        {
            Il2CppStringArray il2cpp = new[] { "alpha", "beta" };  // op_Implicit -> a real il2cpp string array
            string[] r = ToNatural<string[]>(il2cpp);
            Check.True(r is ["alpha", "beta"], $"got [{string.Join(",", r)}]");
            return $"Il2CppStringArray -> natural string[] [{string.Join(",", r)}]";
        });

        suite.Add("container.list.tomanaged", () =>
        {
            // A real il2cpp collection proxy (List<T> — NOT a BCL IEnumerable, so this exercises the
            // ContainerBridge Count+indexer enumeration fallback against the live runtime).
            var il2cpp = new Il2CppSystem.Collections.Generic.List<int>();
            il2cpp.Add(11); il2cpp.Add(22); il2cpp.Add(33);
            List<int> r = ToNatural<List<int>>(il2cpp);
            Check.True(r is [11, 22, 33], $"got [{string.Join(",", r)}], expected [11,22,33]");
            return $"Il2CppSystem.List<int> -> natural List<int> [{string.Join(",", r)}]";
        });

        suite.Add("container.dict.tomanaged", () =>
        {
            // A real il2cpp Dictionary proxy -> natural BCL Dictionary<int,string> (the 2-child key+value
            // recursion), enumerated through the GetEnumerator/MoveNext/Current pair protocol against the live
            // runtime — the il2cpp dict has no positional int indexer, so this exercises EnumeratePairs, not the
            // Count+indexer sequence fallback.
            var il2cpp = new Il2CppSystem.Collections.Generic.Dictionary<int, string>();
            il2cpp[1] = "one"; il2cpp[2] = "two";
            Dictionary<int, string> r = ToNatural<Dictionary<int, string>>(il2cpp);
            Check.True(r is { Count: 2 } && r[1] == "one" && r[2] == "two", $"got {r.Count} entries");
            return $"Il2CppSystem.Dictionary<int,string> -> natural Dictionary<int,string> ({r.Count} entries: 1={r.GetValueOrDefault(1)}, 2={r.GetValueOrDefault(2)})";
        });

        suite.Add("container.nullable.tomanaged", () =>
        {
            // A real il2cpp Nullable<int> proxy -> natural int? (HasValue/Value read against the live runtime).
            // Construct via the proxy ctor(T) — the explicit int?->Nullable operator boxes to Il2CppSystem.Object
            // and won't cast back to the typed proxy (the value-type boxing hazard).
            var some = new Il2CppSystem.Nullable<int>(7);
            int? r = ToNatural<int?>(some);
            Check.True(r == 7, $"expected 7, got {(r.HasValue ? r.Value.ToString() : "null")}");
            return $"Il2CppSystem.Nullable<int> -> int? ({r})";
        });

        suite.Add("container.tuple.tomanaged", () =>
        {
            // A real il2cpp ValueTuple<string,string> proxy -> natural (string,string) (Item1/Item2 read, N-child
            // construction against the live runtime).
            var il2cpp = new Il2CppSystem.ValueTuple<string, string>("alpha", "beta");
            (string a, string b) r = ToNatural<(string, string)>(il2cpp);
            Check.True(r.a == "alpha" && r.b == "beta", $"got ({r.a},{r.b})");
            return $"Il2CppSystem.ValueTuple<string,string> -> (string,string) ({r.a},{r.b})";
        });

        // ── ToIl2Cpp (write/dematerialize) direction: natural BCL array -> il2cpp wrapper, proven by ROUND-TRIP
        // (marshal out then read back through ToManaged, so a corrupt write is caught by a wrong read-back). ──
        suite.Add("container.array.struct.toil2cpp", () =>
        {
            int[] natural = { 3, 4, 5 };
            object il2cpp = ToIl2Cpp(natural, typeof(int[]));           // -> Il2CppStructArray<int>
            Check.True(il2cpp is Il2CppStructArray<int>, $"expected Il2CppStructArray<int>, got {il2cpp.GetType().Name}");
            int[] back = ToNatural<int[]>(il2cpp);                      // round-trip proves the il2cpp array holds [3,4,5]
            Check.True(back is [3, 4, 5], $"round-trip got [{string.Join(",", back)}]");
            return $"int[] -> Il2CppStructArray<int> -> int[] round-trip [{string.Join(",", back)}]";
        });

        suite.Add("container.array.string.toil2cpp", () =>
        {
            string[] natural = { "x", "y" };
            object il2cpp = ToIl2Cpp(natural, typeof(string[]));        // -> Il2CppStringArray
            Check.True(il2cpp is Il2CppStringArray, $"expected Il2CppStringArray, got {il2cpp.GetType().Name}");
            string[] back = ToNatural<string[]>(il2cpp);
            Check.True(back is ["x", "y"], $"round-trip got [{string.Join(",", back)}]");
            return $"string[] -> Il2CppStringArray -> string[] round-trip [{string.Join(",", back)}]";
        });

        suite.Add("container.array.nested.toil2cpp", () =>
        {
            // WRITE-SIDE RECURSION: jagged int[][] -> Il2CppReferenceArray<Il2CppStructArray<int>>, each inner
            // int[] recursing through the child Array Conv into its own Il2CppStructArray<int>. Round-trip back.
            int[][] natural = { new[] { 1, 2 }, new[] { 3 } };
            object il2cpp = ToIl2Cpp(natural, typeof(int[][]));         // -> Il2CppReferenceArray<Il2CppStructArray<int>>
            Check.True(il2cpp is Il2CppReferenceArray<Il2CppStructArray<int>>, $"expected Il2CppReferenceArray<Il2CppStructArray<int>>, got {il2cpp.GetType().Name}");
            int[][] back = ToNatural<int[][]>(il2cpp);
            Check.True(back is [[1, 2], [3]], $"round-trip got [{string.Join("|", back.Select(a => string.Join(",", a)))}]");
            return $"int[][] -> Il2CppReferenceArray<Il2CppStructArray<int>> -> int[][] round-trip [{string.Join("|", back.Select(a => string.Join(",", a)))}]";
        });

        suite.Add("container.list.toil2cpp", () =>
        {
            // WRITE via the R1 runtime proxy resolver: natural List<int> -> a concrete
            // Il2CppSystem.Collections.Generic.List<int> resolved by name from the loaded Il2Cppmscorlib, populated
            // through the proxy's Add. Round-trip back through ToManaged.
            List<int> natural = new() { 7, 8, 9 };
            object il2cpp = ToIl2Cpp(natural, typeof(List<int>));       // -> Il2CppSystem.Collections.Generic.List<int>
            Check.True(il2cpp is Il2CppSystem.Collections.Generic.List<int>, $"expected Il2CppSystem.List<int>, got {il2cpp.GetType().Name}");
            List<int> back = ToNatural<List<int>>(il2cpp);
            Check.True(back is [7, 8, 9], $"round-trip got [{string.Join(",", back)}]");
            return $"List<int> -> Il2CppSystem.List<int> -> List<int> round-trip [{string.Join(",", back)}]";
        });

        suite.Add("container.dict.toil2cpp", () =>
        {
            // WRITE the 2-child dict via R1: natural Dictionary<int,string> -> a concrete
            // Il2CppSystem.Collections.Generic.Dictionary<int,string>, key + value each recursing, populated
            // through the proxy's set_Item indexer. Round-trip back through ToManaged.
            Dictionary<int, string> natural = new() { [1] = "one", [2] = "two" };
            object il2cpp = ToIl2Cpp(natural, typeof(Dictionary<int, string>));   // -> Il2CppSystem.Dictionary<int,string>
            Check.True(il2cpp is Il2CppSystem.Collections.Generic.Dictionary<int, string>, $"expected Il2CppSystem.Dictionary<int,string>, got {il2cpp.GetType().Name}");
            Dictionary<int, string> back = ToNatural<Dictionary<int, string>>(il2cpp);
            Check.True(back is { Count: 2 } && back[1] == "one" && back[2] == "two", $"round-trip got {back.Count} entries");
            return $"Dictionary<int,string> -> Il2CppSystem.Dictionary<int,string> -> back ({back.Count} entries: 1={back.GetValueOrDefault(1)}, 2={back.GetValueOrDefault(2)})";
        });

        suite.Add("container.nullable.toil2cpp", () =>
        {
            // WRITE a value-type kind: natural int? -> il2cpp Il2CppSystem.Nullable<int> via the R1-resolved proxy
            // ctor(T'). Round-trip back through ToManaged.
            int? natural = 7;
            object il2cpp = ToIl2Cpp(natural, typeof(int?));            // -> Il2CppSystem.Nullable<int>
            Check.True(il2cpp is Il2CppSystem.Nullable<int>, $"expected Il2CppSystem.Nullable<int>, got {il2cpp.GetType().Name}");
            int? back = ToNatural<int?>(il2cpp);
            Check.True(back == 7, $"round-trip got {(back.HasValue ? back.Value.ToString() : "null")}");
            return $"int? -> Il2CppSystem.Nullable<int> -> int? round-trip ({back})";
        });

        suite.Add("container.tuple.toil2cpp", () =>
        {
            // WRITE the N-child value-type kind: natural (string,string) -> il2cpp ValueTuple<string,string> via
            // the R1-resolved proxy ctor(T1',T2'), each Item recursing. Round-trip back.
            (string, string) natural = ("alpha", "beta");
            object il2cpp = ToIl2Cpp(natural, typeof((string, string)));  // -> Il2CppSystem.ValueTuple<string,string>
            Check.True(il2cpp is Il2CppSystem.ValueTuple<string, string>, $"expected Il2CppSystem.ValueTuple<string,string>, got {il2cpp.GetType().Name}");
            (string a, string b) back = ToNatural<(string, string)>(il2cpp);
            Check.True(back.a == "alpha" && back.b == "beta", $"round-trip got ({back.a},{back.b})");
            return $"(string,string) -> Il2CppSystem.ValueTuple<string,string> -> back ({back.a},{back.b})";
        });

        // ── C2 correspondence guard: a spelled shape that DISAGREES with the game's actual il2cpp type fails LOUD
        // at the boundary, not with a downstream cast crash. Spell List<int>, hand the marshaller a game Dictionary. ─
        suite.Add("container.correspondence.mismatch.fails-loud", () =>
        {
            var gameDict = new Il2CppSystem.Collections.Generic.Dictionary<int, string>();
            gameDict[1] = "one";
            try
            {
                List<int> wrong = ToNatural<List<int>>(gameDict);   // List spelling over a Dictionary value
                Check.True(false, $"expected a correspondence mismatch, but ToManaged<List<int>> returned {wrong?.GetType().FullName ?? "null"}");
                return null;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("correspondence mismatch"))
            {
                return $"correspondence guard rejected List<int> over a game Dictionary (fail-loud, not a downstream crash)";
            }
        });
    }

    // Reflectively drive Il2CppMarshal.ToIl2Cpp (the write direction) from the deployed Inutil.dll — mirror of
    // ToNatural, unwrapping the invoke so a real marshalling failure surfaces, not TargetInvocationException.
    static object ToIl2Cpp(object managedValue, Type managedType)
    {
        Assembly sdk = Assembly.Load(new AssemblyName("Inutil"));
        Type m = sdk.GetType("Inutil.Marshal.Il2CppMarshal")
            ?? throw new AssertException("Inutil.Marshal.Il2CppMarshal missing from Inutil.dll");
        MethodInfo toIl2 = m.GetMethod("ToIl2Cpp")
            ?? throw new AssertException("Il2CppMarshal.ToIl2Cpp missing");
        try { return toIl2.Invoke(null, new object?[] { managedValue, managedType })!; }
        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
    }

    // Reflectively drive Il2CppMarshal.ToManaged<T> from the deployed Inutil.dll (unwrapping the invoke so a
    // marshalling failure surfaces its real exception, not TargetInvocationException).
    static T ToNatural<T>(object il2cppValue)
    {
        Assembly sdk = Assembly.Load(new AssemblyName("Inutil"));
        Type m = sdk.GetType("Inutil.Marshal.Il2CppMarshal")
            ?? throw new AssertException("Inutil.Marshal.Il2CppMarshal missing from Inutil.dll");
        MethodInfo toManaged = m.GetMethod("ToManaged")
            ?? throw new AssertException("Il2CppMarshal.ToManaged missing");
        try { return (T)toManaged.MakeGenericMethod(typeof(T)).Invoke(null, new[] { il2cppValue })!; }
        catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
    }
}
