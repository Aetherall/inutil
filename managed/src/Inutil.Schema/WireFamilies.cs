namespace Inutil.Schema;

// The SINGLE registration site for the WIRE aspect — the mirror of Families.cs (docs/contribution/architecture/16-metadata.md).
// As Families.cs is the ONLY file allowed to spell an `Il2CppSystem.*` family name (C4), WireFamilies.Default()
// is the ONLY file allowed to spell an ATTRIBUTE type name. WireRegistryDriftTest derives the anchor set FROM
// this registry and fails if any anchor leaks elsewhere — enforcing the invariant, not a frozen list of today's
// strings.
//
// Every recognized attribute is one Register row: attribute type name, its FactKind, and an Extract closure that
// pulls the value out of the decoded blob. Adding a recognizer is one row here, no new mechanism.
public static class WireFamilies
{
    public static WireRegistry Default()
    {
        var r = new WireRegistry();

        // JSON wire name — Newtonsoft's [JsonProperty("wire")]. The wire name is positional ctor arg 0; some usages
        // set it as named arg PropertyName instead. Same fact either way — check both, positional first. A
        // [JsonProperty] with neither is legitimate (may carry only Order/Required) and yields no WIRE-NAME fact:
        // Extract returns null ONLY when the attribute is present but we cannot read the intended name.
        r.Register(new WireCorrespondence(
            "Newtonsoft.Json.JsonPropertyAttribute", FactKind.WireName,
            a => AsString(Positional(a, 0)) is { } s ? WireFact.WireName(s)
               : AsString(Named(a, "PropertyName")) is { } n ? WireFact.WireName(n)
               : null));

        // JSON converter — [JsonConverter(typeof(SomeConverter))]. Ctor arg 0 is the converter type (projected to
        // its full name string at the pure-view boundary), classified into the taxonomy by the converter type.
        r.Register(new WireCorrespondence(
            "Newtonsoft.Json.JsonConverterAttribute", FactKind.Converter,
            a => AsString(Positional(a, 0)) is { } t ? WireFact.Converter(t, ClassifyConverter(t)) : null));

        // Unity persistence marker — [SerializeField]. Presence IS the fact (the field is serialized by Unity even
        // when non-public); there is no value to extract, so this never fails loud.
        r.Register(new WireCorrespondence(
            "UnityEngine.SerializeField", FactKind.Persisted,
            _ => WireFact.Persisted()));

        // Nullability — the compiler-emitted [Nullable(b)] flag (0 oblivious / 1 not-null / 2 nullable). The flag is
        // ctor arg 0 (a byte, or a byte[] for generic members — we record the leading byte). Absent arg => malformed.
        r.Register(new WireCorrespondence(
            "System.Runtime.CompilerServices.NullableAttribute", FactKind.Nullability,
            a => NullableFlag(a) is { } f ? WireFact.Nullability(f) : null));

        // ToyGame's CUSTOM wire attributes (docs/contribution/architecture/16-metadata.md fixture) — the demonstration that a new
        // recognizer is ONE ROW: recovery on these (defined in the toy, no Newtonsoft) proves the engine.
        // [WireName("n")] -> ctor arg 0 (the wire name). [WireConverter(WireKind)] -> ctor arg 0 = the enum's
        // underlying int, whose ordering matches ConverterKind (String/Enum/Opaque) by construction.
        r.Register(new WireCorrespondence(
            "ToyGame.WireNameAttribute", FactKind.WireName,
            a => AsString(Positional(a, 0)) is { } s ? WireFact.WireName(s) : null));
        r.Register(new WireCorrespondence(
            "ToyGame.WireConverterAttribute", FactKind.Converter,
            a => WireKindConverter(Positional(a, 0))));

        return r;
    }

    // --- blob readers: pure, dependency-free helpers over the decoded pure-view args (no Cecil/reflection) ---

    static object? Positional(IAttributeRef a, int i) => i >= 0 && i < a.ConstructorArgs.Count ? a.ConstructorArgs[i] : null;

    static object? Named(IAttributeRef a, string name)
    {
        foreach ((string n, object? v) in a.NamedArgs)
            if (n == name) return v;
        return null;
    }

    // A projected typeof-arg is a full-name STRING at the pure-view boundary (see IAttributeRef.ConstructorArgs);
    // a wire-name arg is a plain string. Anything else (or null) is not a readable string value.
    static string? AsString(object? v) => v as string is { Length: > 0 } s ? s : null;

    // The [Nullable] flag: a single byte, or an array whose leading element is the member's own flag. Rendered as
    // its decimal string (a stable, reader-agnostic token). Accepts the array as a real byte[] (synthetic fakes)
    // or an object?[] of boxed bytes (how Cecil decodes an array ctor arg) — the SOURCE must not change the fact,
    // so both resolve identically.
    static string? NullableFlag(IAttributeRef a)
    {
        object? arg0 = Positional(a, 0);
        return arg0 switch
        {
            byte b                                                  => b.ToString(),
            byte[] { Length: > 0 } bs                               => bs[0].ToString(),
            object?[] { Length: > 0 } os when os[0] is byte ob      => ob.ToString(),
            _                                                       => null,
        };
    }

    // ToyGame's [WireConverter(WireKind)] carries the taxonomy verdict DIRECTLY as an enum ctor arg (Cecil decodes
    // it as its boxed underlying integer). WireKind's order matches ConverterKind (String=0/Enum=1/Opaque=2) by
    // construction, so the int maps straight across. Out-of-range / non-integer => malformed (fail loud).
    static WireFact? WireKindConverter(object? arg0)
    {
        if (arg0 is null) return null;
        int k;
        try { k = Convert.ToInt32(arg0); } catch { return null; }
        if (k < 0 || k > 2) return null;
        var kind = (ConverterKind)k;
        return WireFact.Converter("WireKind." + kind, kind);
    }

    // Taxonomy verdict keyed on the converter TYPE name (its ctor VALUES are the version-dependent part we do not
    // decode pre-recovery). A heuristic on the well-known Newtonsoft converters; unknown converters are Opaque
    // (recorded faithfully, not modeled) — never a wrong guess dressed as certainty.
    static ConverterKind ClassifyConverter(string converterTypeName) =>
          converterTypeName.Contains("StringEnum")                                   ? ConverterKind.Enum
        : converterTypeName.EndsWith("StringConverter") || converterTypeName.Contains("ToString") ? ConverterKind.String
        : ConverterKind.Opaque;
}
