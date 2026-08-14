using Mono.Cecil;
using Inutil.Schema;
using Inutil.InteropPatch;

namespace Inutil.InteropPatch.Tests;

// Proves the projection half end-to-end (docs/contribution/architecture/16-metadata.md): a real Mono.Cecil member ->
// CecilMemberRef/CecilAttributeRef -> the real WireFamilies.Default() registry -> facts. Builds a SYNTHETIC
// attributed Cecil assembly IN MEMORY (no game, no Cpp2IL), so the projection's arg-decoding (typeof -> string,
// byte nullability flag, named args) runs for real rather than through fakes.
static class CecilProjectionTests
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
        TypeReference str = module.TypeSystem.String;
        TypeReference i32 = module.TypeSystem.Int32;
        TypeReference u8 = module.TypeSystem.Byte;
        TypeReference systemType = module.ImportReference(typeof(Type));

        // Recognized attribute types (ctors as needed) + a converter type to reference via typeof(...).
        TypeDefinition jsonPropT = DefineType(module, "Newtonsoft.Json", "JsonPropertyAttribute");
        MethodReference jsonPropCtor0 = AddCtor(jsonPropT);          // ()  -> named-arg / malformed forms
        MethodReference jsonPropCtor1 = AddCtor(jsonPropT, str);     // (string wireName)
        TypeDefinition jsonConvT = DefineType(module, "Newtonsoft.Json", "JsonConverterAttribute");
        MethodReference jsonConvCtor = AddCtor(jsonConvT, systemType);
        TypeDefinition serializeT = DefineType(module, "UnityEngine", "SerializeField");
        MethodReference serializeCtor = AddCtor(serializeT);
        TypeDefinition nullableT = DefineType(module, "System.Runtime.CompilerServices", "NullableAttribute");
        MethodReference nullableCtor = AddCtor(nullableT, u8);
        TypeDefinition stringEnumConv = DefineType(module, "Newtonsoft.Json.Converters", "StringEnumConverter");

        // The DTO whose fields/properties bear the attributes Il2CppInterop would have stripped.
        var dto = new TypeDefinition("Game", "Profile", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(dto);

        FieldDefinition Field(string name, TypeReference ft, params CustomAttribute[] attrs)
        {
            var f = new FieldDefinition(name, FieldAttributes.Public, ft);
            foreach (CustomAttribute a in attrs) f.CustomAttributes.Add(a);
            dto.Fields.Add(f);
            return f;
        }

        CustomAttribute WireNameCtor(string wire)
        {
            var ca = new CustomAttribute(jsonPropCtor1);
            ca.ConstructorArguments.Add(new CustomAttributeArgument(str, wire));
            return ca;
        }
        CustomAttribute WireNameNamed(string wire)
        {
            var ca = new CustomAttribute(jsonPropCtor0);
            ca.Properties.Add(new CustomAttributeNamedArgument("PropertyName", new CustomAttributeArgument(str, wire)));
            return ca;
        }
        CustomAttribute ConverterCtor(TypeReference convType)
        {
            var ca = new CustomAttribute(jsonConvCtor);
            ca.ConstructorArguments.Add(new CustomAttributeArgument(systemType, convType));
            return ca;
        }
        CustomAttribute NullableCtor(byte flag)
        {
            var ca = new CustomAttribute(nullableCtor);
            ca.ConstructorArguments.Add(new CustomAttributeArgument(u8, flag));
            return ca;
        }

        FieldDefinition fNick = Field("_f7", str, WireNameCtor("Nickname"));
        FieldDefinition fLevel = Field("_f8", i32, WireNameNamed("Level"));
        FieldDefinition fSide = Field("side", i32, ConverterCtor(stringEnumConv));
        FieldDefinition fHp = Field("_hp", i32, new CustomAttribute(serializeCtor));
        FieldDefinition fMaybe = Field("maybe", str, NullableCtor(2));
        FieldDefinition fBad = Field("_fX", str, new CustomAttribute(jsonPropCtor0));   // JsonProperty, no name -> malformed

        var reg = WireFamilies.Default();

        // --- the projection surfaces the pure-view identity ---
        IMemberRef nickRef = CecilMemberRef.OfField(fNick);
        Check("projected member name (_f7)", nickRef.Name == "_f7");
        Check("projected member sort Field", nickRef.Sort == MemberSort.Field);
        Check("projected declaring type (Game.Profile)", nickRef.DeclaringType.FullName == "Game.Profile");
        Check("projected member type (System.String)", nickRef.MemberType.FullName == "System.String");

        // --- facts recovered through the REAL registry via the Cecil projection ---
        Check("Cecil JsonProperty(\"Nickname\") -> WireName \"Nickname\"",
            First(reg.Classify(nickRef), FactKind.WireName)?.Value == "Nickname");
        Check("Cecil JsonProperty(PropertyName=\"Level\") named-arg -> WireName \"Level\"",
            First(reg.Classify(CecilMemberRef.OfField(fLevel)), FactKind.WireName)?.Value == "Level");

        WireFact? conv = First(reg.Classify(CecilMemberRef.OfField(fSide)), FactKind.Converter);
        Check("Cecil JsonConverter typeof(...) decoded to the converter type NAME",
            conv?.Value == "Newtonsoft.Json.Converters.StringEnumConverter");
        Check("Cecil converter classified Enum (§7.13 taxonomy)", conv?.ConverterKind == ConverterKind.Enum);

        Check("Cecil SerializeField -> Persisted",
            First(reg.Classify(CecilMemberRef.OfField(fHp)), FactKind.Persisted) is not null);
        Check("Cecil Nullable(2) -> Nullability \"2\"",
            First(reg.Classify(CecilMemberRef.OfField(fMaybe)), FactKind.Nullability)?.Value == "2");

        // --- fail-loud flows through the real projection: malformed matched attribute -> warning, not silent ---
        WireClassification bad = reg.Classify(CecilMemberRef.OfField(fBad));
        Check("Cecil malformed JsonProperty -> no fact", First(bad, FactKind.WireName) is null);
        Check("Cecil malformed JsonProperty -> a WARNING (fail loud through real Cecil)", bad.Warnings.Count == 1);

        // --- property projection (MemberSort.Property) ---
        var prop = new PropertyDefinition("Shield", PropertyAttributes.None, i32);
        prop.CustomAttributes.Add(new CustomAttribute(serializeCtor));
        dto.Properties.Add(prop);
        IMemberRef propRef = CecilMemberRef.OfProperty(prop);
        Check("projected property sort Property", propRef.Sort == MemberSort.Property);
        Check("Cecil property SerializeField -> Persisted", First(reg.Classify(propRef), FactKind.Persisted) is not null);

        // --- type projection is safe (top-level DeclaringType resolves to itself, no NRE) ---
        IMemberRef typeRef = CecilMemberRef.OfType(dto);
        Check("projected type sort Type + declaring resolves (no NRE)",
            typeRef.Sort == MemberSort.Type && typeRef.DeclaringType.FullName == "Game.Profile");

        return failures;
    }

    static TypeDefinition DefineType(ModuleDefinition m, string ns, string name)
    {
        var td = new TypeDefinition(ns, name, TypeAttributes.Public | TypeAttributes.Class, m.TypeSystem.Object);
        m.Types.Add(td);
        return td;
    }

    static MethodReference AddCtor(TypeDefinition attrType, params TypeReference[] paramTypes)
    {
        var ctor = new MethodDefinition(".ctor",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            attrType.Module.TypeSystem.Void);
        foreach (TypeReference pt in paramTypes) ctor.Parameters.Add(new ParameterDefinition(pt));
        attrType.Methods.Add(ctor);
        return ctor;
    }

    static WireFact? First(WireClassification c, FactKind kind)
    {
        foreach (WireFact f in c.Facts)
            if (f.Kind == kind) return f;
        return null;
    }
}
