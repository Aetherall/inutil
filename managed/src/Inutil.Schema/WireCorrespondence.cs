namespace Inutil.Schema;

// The kind of authored fact one recognized attribute yields (docs/contribution/architecture/16-metadata.md). The member-aspect analogue
// of ConvKind (Conv.cs) for the type aspect: the join key a consumer keys on, never per-attribute code.
public enum FactKind { WireName, Converter, Persisted, Nullability }

// The converter taxonomy, reused as a registry column (docs/contribution/architecture/16-metadata.md): how a recovered JSON
// converter re-serializes a value — a string form, an enum-as-string, or an opaque custom converter the pillar
// records but does not model. Pre-value-recovery we classify by the converter TYPE (the attribute reliably
// carries the type; its ctor VALUES are the version-dependent part), so this is a type-keyed verdict.
public enum ConverterKind { String, Enum, Opaque }

// One recovered authored fact about a member — the member-aspect analogue of a classified ConvShape. DATA, not
// behaviour: WireName carries the wire string, Converter the converter type name + taxonomy verdict, Persisted
// only its presence, Nullability the decoded flag. Static factories keep each construction honest about which
// columns it populates.
public sealed record WireFact
{
    public FactKind Kind { get; }

    // WireName: the wire/serialized name. Converter: the converter type full name. Nullability: the decoded flag
    // ("0"/"1"/"2" oblivious/not-null/nullable). Persisted: null (presence is the fact).
    public string? Value { get; }

    // Meaningful only for FactKind.Converter — the taxonomy verdict for Value. Opaque otherwise.
    public ConverterKind ConverterKind { get; }

    WireFact(FactKind kind, string? value, ConverterKind converterKind)
    {
        Kind = kind;
        Value = value;
        ConverterKind = converterKind;
    }

    public static WireFact WireName(string name)                     => new(FactKind.WireName, name, ConverterKind.Opaque);
    public static WireFact Converter(string typeName, ConverterKind kind) => new(FactKind.Converter, typeName, kind);
    public static WireFact Persisted()                              => new(FactKind.Persisted, null, ConverterKind.Opaque);
    public static WireFact Nullability(string flag)                => new(FactKind.Nullability, flag, ConverterKind.Opaque);
}

// One recognized attribute -> the fact it yields. The member-aspect analogue of TypeCorrespondence: a new
// recognizer is one ROW in WireFamilies.cs, never a switch arm. The Extract closure is the deliberate difference
// from TypeCorrespondence's shape verdict — wire-classify pulls a VALUE out of the blob (no arity, no
// open-parameter rejection), which is why the two registries are not merged: they share the register/classify
// SHAPE, not the predicate.
public sealed record WireCorrespondence(
    // The attribute type full name — the anchor WireRegistry matches on, single-site-spelled in WireFamilies.cs.
    string AttributeTypeFullName,
    FactKind Kind,
    // Pull the fact from the blob. Returns null ONLY when the attribute matched but its blob is malformed/
    // unextractable — the classifier turns that into a fail-loud WARNING, never a silent skip. A Persisted
    // recognizer whose fact is mere presence never returns null.
    Func<IAttributeRef, WireFact?> Extract);

// The structured result of classifying one member. Carries BOTH the recovered facts and any warnings, so the
// fail-loud-never-silent invariant is structural: a matched-but-malformed attribute cannot vanish — it lands in
// Warnings.
public sealed record WireClassification(
    IReadOnlyList<WireFact> Facts,
    IReadOnlyList<string> Warnings);
