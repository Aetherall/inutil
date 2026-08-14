using System.Text.Json;
using Mono.Cecil;
using Inutil.Schema;
using Inutil.Metadata;

namespace Inutil.Metadata.Tests;

// Offline proof of the wire-map EMIT (docs/contribution/architecture/16-metadata.md): build a SYNTHETIC attributed Cecil module in
// memory and drive MetadataExtractor.BuildModel + WireMapEmitter over it, asserting the exact inutil.wiremap.json
// shape WireMap.cs will parse — no game, no Cpp2IL. The integration test (Program.cs) proves the full Cpp2IL
// pipeline on the real ToyGame.
static class MetadataEmitTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]");
            if (!ok) failures++;
        }

        var module = ModuleDefinition.CreateModule("Synthetic", ModuleKind.Dll);
        TypeReference str = module.TypeSystem.String, i32 = module.TypeSystem.Int32, sysType = module.ImportReference(typeof(Type));

        TypeDefinition jsonPropT = DefineType(module, "Newtonsoft.Json", "JsonPropertyAttribute");
        MethodReference jpCtor0 = AddCtor(jsonPropT);
        MethodReference jpCtor1 = AddCtor(jsonPropT, str);
        TypeDefinition jsonConvT = DefineType(module, "Newtonsoft.Json", "JsonConverterAttribute");
        MethodReference jcCtor = AddCtor(jsonConvT, sysType);
        TypeDefinition serializeT = DefineType(module, "UnityEngine", "SerializeField");
        MethodReference sfCtor = AddCtor(serializeT);
        TypeDefinition enumConv = DefineType(module, "Newtonsoft.Json.Converters", "StringEnumConverter");

        var dto = new TypeDefinition("Game", "Profile", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(dto);
        Field(dto, "_f7", str, WireName(jpCtor1, str, "Nickname"));                               // wire name only
        Field(dto, "side", i32, Converter(jcCtor, sysType, enumConv));                             // converter only
        Field(dto, "_fS", str, WireName(jpCtor1, str, "Faction"), Converter(jcCtor, sysType, enumConv)); // both
        Field(dto, "_hp", i32, new CustomAttribute(sfCtor));                                       // SerializeField only
        Field(dto, "raw", i32);                                                                    // no attributes
        Field(dto, "_bad", str, new CustomAttribute(jpCtor0));                                     // JsonProperty, no name -> malformed

        // A TYPE-level [JsonConverter] (a converter leaf like EFT's MongoID) — NO members, so it is not a `types`
        // entry, but it DOES contribute a `typeKinds` row (the `@type` capability). StringEnumConverter -> "enum".
        var tagged = new TypeDefinition("Toy", "Tagged", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        tagged.CustomAttributes.Add(Converter(jcCtor, sysType, enumConv));
        module.Types.Add(tagged);

        // A MARKER-LESS type the game serializes BY DEFAULT (no [JsonProperty]/[SerializeField] on any member) —
        // the EFT.ProfileSettings case. Not IsSerialized, but REACHABLE via Game.Profile.settings, so the closure
        // must recover it into `types` (its members emitting under their own names).
        var settings = new TypeDefinition("Game", "Settings", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(settings);
        Field(settings, "Volume", i32);
        Field(settings, "Sensitivity", i32);
        Field(dto, "settings", settings);   // Game.Profile references it -> reachability

        // A marker-less type NOT reachable from any serialized type — must stay OUT (the closure is reachability,
        // not "include every type").
        var orphan = new TypeDefinition("Game", "Orphan", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(orphan);
        Field(orphan, "x", i32);

        WireModel model = new MetadataExtractor().BuildModel(new[] { module });
        string json = WireMapEmitter.EmitJson(model);
        Check("wire-map is deterministic (byte-identical re-emit -> idempotent)", json == WireMapEmitter.EmitJson(model));

        // Fail-loud flows through the extractor: the malformed JsonProperty on _bad surfaces a WARNING, never silent.
        Check("malformed JsonProperty -> a warning surfaced (fail loud, not silent)", model.Warnings.Count == 1);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement types = doc.RootElement.GetProperty("types");
        Check("wire-map includes the serialized DTO (Game.Profile)", types.TryGetProperty("Game.Profile", out JsonElement profile));
        Check("wire-map EXCLUDES an unserialized type (the JsonProperty attribute defn has no serialized members)",
            !types.TryGetProperty("Newtonsoft.Json.JsonPropertyAttribute", out _));
        Check("wire-map EXCLUDES a converter-leaf type with no members from `types` (Toy.Tagged)",
            !types.TryGetProperty("Toy.Tagged", out _));

        // The closure: a marker-less type reachable from a serialized DTO is RECOVERED; an unreachable one is not.
        Check("wire-map RECOVERS a marker-less type reachable via a member (Game.Settings — the ProfileSettings case)",
            types.TryGetProperty("Game.Settings", out JsonElement settingsT));
        if (types.TryGetProperty("Game.Settings", out settingsT))
        {
            JsonElement sm = settingsT.GetProperty("members");
            Check("...and its marker-less members emit under their own names (Volume, Sensitivity)",
                sm.GetArrayLength() == 2 && MemberWire(sm, "Volume") == "Volume" && MemberWire(sm, "Sensitivity") == "Sensitivity");
        }
        Check("wire-map EXCLUDES a marker-less type NOT reachable from any serialized type (Game.Orphan)",
            !types.TryGetProperty("Game.Orphan", out _));

        if (types.TryGetProperty("Game.Profile", out profile))
        {
            JsonElement members = profile.GetProperty("members");
            Check("wire-map lists ALL 7 members (6 + the `settings` reference)", members.GetArrayLength() == 7);
            Check("wire-map: _f7 -> wire \"Nickname\" (recovered)", MemberWire(members, "_f7") == "Nickname");
            Check("wire-map: plain field `raw` -> wire defaults to the member name", MemberWire(members, "raw") == "raw");
            Check("wire-map: `side` -> converter kind enum", MemberConverterKind(members, "side") == "enum");
            Check("wire-map: `_fS` -> wire \"Faction\" + converter enum (both)",
                MemberWire(members, "_fS") == "Faction" && MemberConverterKind(members, "_fS") == "enum");
            Check("wire-map: SerializeField `_hp` -> persisted true", MemberPersisted(members, "_hp"));
            Check("wire-map: malformed `_bad` -> wire defaults to name, no converter",
                MemberWire(members, "_bad") == "_bad" && MemberConverterKind(members, "_bad") is null);
        }

        // typeKinds (the ported @type capability): the type-level [JsonConverter] on Toy.Tagged -> "enum".
        JsonElement typeKinds = doc.RootElement.GetProperty("typeKinds");
        Check("wire-map typeKinds carries the type-level converter kind (Toy.Tagged -> enum)",
            typeKinds.TryGetProperty("Toy.Tagged", out JsonElement tk) && tk.GetString() == "enum");

        return failures;
    }

    // --- wire-map JSON member lookups (a member is an object in the type's "members" array, keyed by "name") ---
    static JsonElement? Member(JsonElement members, string name)
    {
        foreach (JsonElement m in members.EnumerateArray())
            if (m.GetProperty("name").GetString() == name) return m;
        return null;
    }

    static string? MemberWire(JsonElement members, string name) => Member(members, name)?.GetProperty("wire").GetString();

    static string? MemberConverterKind(JsonElement members, string name) =>
        Member(members, name) is { } m && m.TryGetProperty("converter", out JsonElement c) ? c.GetProperty("kind").GetString() : null;

    static bool MemberPersisted(JsonElement members, string name) =>
        Member(members, name) is { } m && m.TryGetProperty("persisted", out JsonElement p) && p.GetBoolean();

    static TypeDefinition DefineType(ModuleDefinition m, string ns, string name)
    {
        var td = new TypeDefinition(ns, name, TypeAttributes.Public | TypeAttributes.Class, m.TypeSystem.Object);
        m.Types.Add(td);
        return td;
    }

    static MethodReference AddCtor(TypeDefinition t, params TypeReference[] ps)
    {
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, t.Module.TypeSystem.Void);
        foreach (TypeReference p in ps) ctor.Parameters.Add(new ParameterDefinition(p));
        t.Methods.Add(ctor);
        return ctor;
    }

    static void Field(TypeDefinition dto, string name, TypeReference ft, params CustomAttribute[] attrs)
    {
        var f = new FieldDefinition(name, FieldAttributes.Public, ft);
        foreach (CustomAttribute a in attrs) f.CustomAttributes.Add(a);
        dto.Fields.Add(f);
    }

    static CustomAttribute WireName(MethodReference ctor, TypeReference str, string wire)
    {
        var ca = new CustomAttribute(ctor);
        ca.ConstructorArguments.Add(new CustomAttributeArgument(str, wire));
        return ca;
    }

    static CustomAttribute Converter(MethodReference ctor, TypeReference sysType, TypeReference convType)
    {
        var ca = new CustomAttribute(ctor);
        ca.ConstructorArguments.Add(new CustomAttributeArgument(sysType, convType));
        return ca;
    }
}
