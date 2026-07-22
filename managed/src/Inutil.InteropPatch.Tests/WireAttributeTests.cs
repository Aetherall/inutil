using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.InteropPatch;

namespace Inutil.InteropPatch.Tests;

// Proves the disk-only wire-attribute pass end-to-end over a SYNTHETIC proxy-shaped Cecil module (no game, no
// Cpp2IL): parse a wiremap, stamp [JsonPropertyName] onto the named members, leave the rest (interop bookkeeping)
// alone, idempotent, and survive a Cecil write+re-read. The in-game battery is the gate that a REAL patched proxy
// actually serializes.
static class WireAttributeTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]");
            if (!ok) failures++;
        }

        var module = ModuleDefinition.CreateModule("SyntheticWire", ModuleKind.Dll);
        var dto = new TypeDefinition("EFT", "ProfileDescriptor",
            TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(dto);

        // proxy-shaped string properties (real proxies expose members as properties) — member name != wire name
        PropertyDefinition Prop(string name)
        {
            var getter = new MethodDefinition("get_" + name,
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, module.TypeSystem.String);
            getter.Body = new MethodBody(getter);
            ILProcessor il = getter.Body.GetILProcessor();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            dto.Methods.Add(getter);
            var p = new PropertyDefinition(name, PropertyAttributes.None, module.TypeSystem.String) { GetMethod = getter };
            dto.Properties.Add(p);
            return p;
        }
        Prop("Id");        // wiremap: -> "_id"    (the classic rename)
        Prop("Nickname");  // wiremap: -> "Nickname"
        Prop("Pointer");   // NOT in the wiremap — interop bookkeeping, must be left untouched

        // A "string"-kind type (typeKinds) — the converter-kind pass stamps [WireKind("string")] on the TYPE.
        var mongo = new TypeDefinition("EFT", "MongoID", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(mongo);

        string wireJson =
            "{\"types\":{\"EFT.ProfileDescriptor\":{\"members\":[" +
            "{\"name\":\"Id\",\"type\":\"EFT.MongoID\",\"wire\":\"_id\"}," +
            "{\"name\":\"Nickname\",\"type\":\"System.String\",\"wire\":\"Nickname\"}]}}," +
            "\"typeKinds\":{\"EFT.MongoID\":\"string\",\"EFT.EPlayerSide\":\"enum\"}}";
        string wirePath = Path.Combine(Path.GetTempPath(), "inutil-wiremap-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(wirePath, wireJson);

        string? outPath = null;
        try
        {
            WireMap? map = WireMap.Load(wirePath);
            Check("wiremap parses to 1 recovered type", map is { TypeCount: 1 });

            int stamped = new WireAttributeRewriter().Stamp(module, map!);
            Check("stamped 3 (2 member wire names + 1 type kind)", stamped == 3);

            static string? Wire(TypeDefinition t, string prop) =>
                t.Properties.First(p => p.Name == prop).CustomAttributes
                    .FirstOrDefault(a => a.AttributeType.Name == "JsonPropertyNameAttribute")
                    ?.ConstructorArguments[0].Value as string;

            Check("Id member carries [JsonPropertyName(\"_id\")]", Wire(dto, "Id") == "_id");
            Check("Nickname member carries [JsonPropertyName(\"Nickname\")]", Wire(dto, "Nickname") == "Nickname");
            Check("Pointer (absent from wiremap) is NOT stamped", Wire(dto, "Pointer") is null);

            static string? Kind(TypeDefinition t) =>
                t.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "WireKindAttribute")
                    ?.ConstructorArguments[0].Value as string;
            Check("EFT.MongoID (typeKinds=string) carries [WireKind(\"string\")]", Kind(mongo) == "string");
            Check("EFT.ProfileDescriptor (a DTO, no kind) is NOT kind-stamped", Kind(dto) is null);

            int again = new WireAttributeRewriter().Stamp(module, map!);
            Check("idempotent: re-running the pass stamps 0", again == 0);

            outPath = Path.Combine(Path.GetTempPath(), "inutil-wireattr-" + Guid.NewGuid().ToString("N") + ".dll");
            module.Write(outPath);
            using var reloaded = ModuleDefinition.ReadModule(outPath);
            var reType = reloaded.GetTypes().First(t => t.Name == "ProfileDescriptor");
            Check("attribute survives Cecil write + re-read", Wire(reType, "Id") == "_id");
        }
        finally
        {
            module.Dispose();
            try { File.Delete(wirePath); } catch { }
            if (outPath is not null) try { File.Delete(outPath); } catch { }
        }
        return failures;
    }
}
