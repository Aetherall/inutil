using Inutil.Schema;

namespace Inutil.Schema.Tests;

// Wire classifier unit tests (docs/contribution/architecture/16-metadata.md): WireRegistry.Classify recovers the authored fact from
// each recognized attribute, reuses the converter taxonomy, degrades gracefully on an UNrecognized attribute
// (yields nothing, no warning), and FAILS LOUD on a recognized-but-malformed one (a warning, never a silent skip).
// All against synthetic members — no game, no Cpp2IL.
static class WireClassifyTests
{
    public static void Run()
    {
        var reg = WireFamilies.Default();

        // --- WireName: [JsonProperty("Nickname")] on an obfuscated field recovers the wire name. ---
        var jsonProp = FakeMemberRef.Field("_f7",
            FakeAttributeRef.Of("Newtonsoft.Json.JsonPropertyAttribute", "Nickname"));
        var c1 = reg.Classify(jsonProp);
        WireFact? wire = First(c1, FactKind.WireName);
        T.Check("JsonProperty(\"Nickname\") -> WireName fact", wire is not null);
        T.Check("WireName value is \"Nickname\"", wire?.Value == "Nickname");
        T.Check("well-formed WireName yields no warning", c1.Warnings.Count == 0);

        // The named-arg form [JsonProperty(PropertyName = "Level")] is the SAME fact.
        var jsonPropNamed = FakeMemberRef.Field("_f8",
            FakeAttributeRef.Of("Newtonsoft.Json.JsonPropertyAttribute").WithNamed(("PropertyName", "Level")));
        T.Check("JsonProperty(PropertyName=\"Level\") -> WireName \"Level\"",
            First(reg.Classify(jsonPropNamed), FactKind.WireName)?.Value == "Level");

        // --- Converter: taxonomy verdict keyed on the converter type (String/Enum/Opaque). ---
        var enumConv = reg.Classify(FakeMemberRef.Field("side",
            FakeAttributeRef.Of("Newtonsoft.Json.JsonConverterAttribute", "Newtonsoft.Json.Converters.StringEnumConverter")));
        WireFact? conv = First(enumConv, FactKind.Converter);
        T.Check("JsonConverter(StringEnumConverter) -> Converter fact", conv is not null);
        T.Check("StringEnumConverter classified Enum", conv?.ConverterKind == ConverterKind.Enum);
        T.Check("Converter records the converter type name",
            conv?.Value == "Newtonsoft.Json.Converters.StringEnumConverter");

        var opaqueConv = reg.Classify(FakeMemberRef.Field("blob",
            FakeAttributeRef.Of("Newtonsoft.Json.JsonConverterAttribute", "Game.Serialization.CustomBinaryConverter")));
        T.Check("unknown converter classified Opaque (no false certainty)",
            First(opaqueConv, FactKind.Converter)?.ConverterKind == ConverterKind.Opaque);

        // --- Persisted: [SerializeField] presence is the fact; never fails loud. ---
        var serialized = reg.Classify(FakeMemberRef.Field("_hp", FakeAttributeRef.Of("UnityEngine.SerializeField")));
        T.Check("SerializeField -> Persisted fact", First(serialized, FactKind.Persisted) is not null);
        T.Check("SerializeField yields no warning", serialized.Warnings.Count == 0);

        // --- Nullability: [Nullable(2)] decodes the flag; byte[] form takes the leading byte. ---
        var nullable = reg.Classify(FakeMemberRef.Field("maybe",
            FakeAttributeRef.Of("System.Runtime.CompilerServices.NullableAttribute", (byte)2)));
        T.Check("Nullable(2) -> Nullability \"2\"", First(nullable, FactKind.Nullability)?.Value == "2");
        var nullableArr = reg.Classify(FakeMemberRef.Field("maybeGeneric",
            FakeAttributeRef.Of("System.Runtime.CompilerServices.NullableAttribute", new byte[] { 1, 2 })));
        T.Check("Nullable([1,2]) -> Nullability \"1\" (leading byte)",
            First(nullableArr, FactKind.Nullability)?.Value == "1");

        // --- Several facts on one member: JsonProperty + JsonConverter + SerializeField all recovered. ---
        var rich = reg.Classify(FakeMemberRef.Field("_f9",
            FakeAttributeRef.Of("Newtonsoft.Json.JsonPropertyAttribute", "Faction"),
            FakeAttributeRef.Of("Newtonsoft.Json.JsonConverterAttribute", "Newtonsoft.Json.Converters.StringEnumConverter"),
            FakeAttributeRef.Of("UnityEngine.SerializeField")));
        T.Check("member with 3 recognized attributes -> 3 facts", rich.Facts.Count == 3);

        // --- Graceful degrade: an UNrecognized attribute yields nothing and NO warning (per-game path). ---
        var unknown = reg.Classify(FakeMemberRef.Field("plain",
            FakeAttributeRef.Of("Some.Vendor.WhateverAttribute", "x")));
        T.Check("unrecognized attribute -> no facts", unknown.Facts.Count == 0);
        T.Check("unrecognized attribute -> no warning (graceful degrade, not a skip)", unknown.Warnings.Count == 0);
        T.Check("member with no attributes -> nothing", reg.Classify(FakeMemberRef.Field("bare")).Facts.Count == 0);

        // --- FAIL LOUD: a recognized attribute whose blob is malformed surfaces a WARNING, not a silent skip. ---
        var malformed = reg.Classify(FakeMemberRef.Field("_fX",
            FakeAttributeRef.Of("Newtonsoft.Json.JsonPropertyAttribute")));   // no ctor arg, no PropertyName
        T.Check("malformed JsonProperty -> no fact", First(malformed, FactKind.WireName) is null);
        T.Check("malformed JsonProperty -> a WARNING (fail loud, not silent)", malformed.Warnings.Count == 1);
        T.Check("warning names the member and the recognizer",
            malformed.Warnings.Count == 1
            && malformed.Warnings[0].Contains("_fX") && malformed.Warnings[0].Contains("WireName"));

        var malformedNullable = reg.Classify(FakeMemberRef.Field("_fY",
            FakeAttributeRef.Of("System.Runtime.CompilerServices.NullableAttribute", "not-a-byte")));
        T.Check("malformed Nullable (non-byte arg) -> a warning", malformedNullable.Warnings.Count == 1);

        // --- ToyGame's custom fixture attributes: the "a new recognizer is one row" demonstration. ---
        var wpHandle = reg.Classify(FakeMemberRef.Field("Handle",
            FakeAttributeRef.Of("ToyGame.WireNameAttribute", "Nickname")));
        T.Check("ToyGame [WireName(\"Nickname\")] -> WireName \"Nickname\"", First(wpHandle, FactKind.WireName)?.Value == "Nickname");

        // [WireConverter(WireKind.Enum)] arrives as the enum's underlying int (1) — maps to ConverterKind.Enum.
        var wpSide = reg.Classify(FakeMemberRef.Field("Side",
            FakeAttributeRef.Of("ToyGame.WireConverterAttribute", 1)));
        T.Check("ToyGame [WireConverter(Enum)] -> Converter kind Enum", First(wpSide, FactKind.Converter)?.ConverterKind == ConverterKind.Enum);

        var wpBadConv = reg.Classify(FakeMemberRef.Field("Bad",
            FakeAttributeRef.Of("ToyGame.WireConverterAttribute", 9)));   // out-of-range enum value
        T.Check("ToyGame [WireConverter] out-of-range -> a warning (fail loud)", wpBadConv.Warnings.Count == 1);
    }

    static WireFact? First(WireClassification c, FactKind kind)
    {
        foreach (WireFact f in c.Facts)
            if (f.Kind == kind) return f;
        return null;
    }
}
