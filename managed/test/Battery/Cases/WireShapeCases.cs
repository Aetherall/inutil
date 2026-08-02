using System.Reflection;
using System.Text.Json.Serialization;

namespace Inutil.Battery;

// Json.ToNode / Json.To<T>(shape) — the CHECKED object-literal builder, END-TO-END in a booted game.
//
// The offline suite proves the builder's logic against a stand-in DTO. What only a real game can prove is the part
// that matters: the keys are checked against wire names RECOVERED BY THE METADATA PILLAR from the game's own
// stripped attributes — ToyGame's WireProfile spells them [WireName("Nickname")] / [WireName("faction")] in source,
// Il2CppInterop strips them off the proxy, inutil's Cpp2IL pass recovers them, and InteropPatch re-attaches them as
// [JsonPropertyName]. So "does `new { Nickname = ... }` validate?" is a question about that whole chain, not about
// reflection over an attribute someone hand-wrote.
//
// ToyGame bundles no Newtonsoft, so there is no game deserializer to register — the seam is exercised with a FAKE
// one that captures the JSON the builder produced. That is exactly the split the seam was designed for: the
// game-specific half is a delegate, the game-agnostic half is inutil's.
public static class WireShapeCases
{
    public static void Register(Suite suite)
    {
        suite.Add("wire-shape.recovered-names", () =>
        {
            Type wp = FindProxyType("WireProfile", out string typeName);
            string[] wire = WireNamesOf(wp);
            // The renamed members are the interesting ones: a proxy-only tool cannot know Handle serializes as
            // "Nickname". If these are missing the shape builder has nothing to check against, so say so plainly.
            Check.True(wire.Contains("Nickname") && wire.Contains("faction"),
                $"{typeName} carries wire names [{string.Join(",", wire)}] — expected the RECOVERED 'Nickname'/'faction'. " +
                "Without them the interop is unpatched or the wiremap was not stamped, and no shape key can be validated.");
            return $"{typeName} recovered wire names: [{string.Join(",", wire)}]";
        });

        suite.Add("wire-shape.checked-build.runs", () =>
        {
            Type wp = FindProxyType("WireProfile", out string typeName);

            // Capture what the builder hands the game's deserializer. ToyGame has none registered, so this also
            // proves the delegate seam itself (register -> To<T> routes through it).
            string? captured = null;
            Inutil.Json.UseDeserializer((json, _) => { captured = json; return null; });

            MethodInfo to = typeof(Inutil.Json).GetMethods()
                .First(m => m.Name == "To" && m.IsGenericMethodDefinition
                            && m.GetParameters() is [{ ParameterType.FullName: "System.Object" }])
                .MakeGenericMethod(wp);
            to.Invoke(null, new object?[] { new { Nickname = "toy", Gold = 7 } });

            Check.True(captured is not null, "the shape never reached the deserializer seam");
            Check.True(captured!.Contains("\"Nickname\":\"toy\"") && captured.Contains("\"Gold\":7"),
                $"built JSON was {captured} — expected the recovered wire names with their coerced values");
            return $"checked shape -> {captured} (validated against the metadata pillar's recovered names)";
        });

        suite.Add("wire-shape.unknown-key.fails-loud", () =>
        {
            Type wp = FindProxyType("WireProfile", out string typeName);

            // The whole reason the object form is allowed to exist: an identifier that LOOKS like a member but
            // is not must never sail through as a silent default.
            string message = "";
            try { Inutil.Json.ToNode(wp, new { Nicknam = "toy" }); }
            catch (Exception ex) { message = ex.Message; }

            Check.True(message.Contains("unknown wire member 'Nicknam'"),
                $"a misspelled key did not fail loud — got '{message}'. A silent default here is the failure this API exists to prevent.");
            Check.True(message.Contains("Did you mean: 'Nickname'"),
                $"the failure did not name the near-miss — got '{message}'");
            return $"misspelled key rejected against {typeName}: {message.Split('\n')[0]}";
        });
    }

    // The wire names actually stamped on a proxy — the same [JsonPropertyName]s Json.ToNode validates against and
    // Wire.Serialize writes from (one recovered list, not two).
    static string[] WireNamesOf(Type t)
    {
        var names = new List<string>();
        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetCustomAttribute<JsonPropertyNameAttribute>() is { } a) names.Add(a.Name);
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (f.GetCustomAttribute<JsonPropertyNameAttribute>() is { } a) names.Add(a.Name);
        return names.ToArray();
    }

    static Type FindProxyType(string simpleName, out string typeName)
    {
        foreach (Assembly asm in CandidateAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
            catch { continue; }

            Type? t = types.FirstOrDefault(x => x.Name == simpleName);
            if (t is not null) { typeName = t.FullName ?? t.Name; return t; }
        }
        throw new AssertException($"{simpleName} proxy type not found in any loaded assembly");
    }

    static IEnumerable<Assembly> CandidateAssemblies()
    {
        Assembly? acs = null;
        try { acs = Assembly.Load(new AssemblyName("Assembly-CSharp")); } catch { /* fall through */ }
        if (acs is not null) yield return acs;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) yield return a;
    }
}
