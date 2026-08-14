using System;
using System.Collections.Generic;
using System.Linq;
using Inutil.Schema;

namespace Inutil.Metadata;

// The NORMALIZED metadata model (docs/contribution/architecture/16-metadata.md): types × members × facts,
// produced ONCE by the extract walk and consumed by the wire-map emitter — the "one model, many consumers" shape.
// Pure data — no Cecil, no I/O, no Cpp2IL: the facts are already projected off the pure member views (IMemberRef)
// and classified by the WireRegistry.
public sealed record WireModel(
    IReadOnlyList<WireModelType> Types,      // every GAME type walked (framework modules already filtered out upstream)
    IReadOnlyList<string> Warnings,          // fail-loud: a matched-but-malformed attribute, never silently dropped
    int TypesScanned,
    int MembersClassified)
{
    // The wire-map's emitted-type set, single-homed so the emitter's filter and any count can't drift: the
    // marker-bearing DTOs (IsSerialized) PLUS every game type REACHABLE from them through a member type. That
    // closure recovers a MARKER-LESS type the game serializes BY DEFAULT (no [JsonProperty]/[SerializeField] on any
    // member — e.g. EFT.ProfileSettings, reached via ProfileInfoDescriptor.Settings): the seed predicate alone drops
    // it, but its wire is real. A converter-KIND leaf (its OWN TypeConverterKind, e.g. MongoID) is deliberately NOT
    // followed into and NOT added — it serializes as its kind (a bare string) via typeKinds, so its members are not
    // wire. Framework types are absent from Types upstream, so a member of such a type is not found and not followed.
    public IReadOnlyList<WireModelType> SerializedClosure()
    {
        var byName = new Dictionary<string, WireModelType>(Types.Count);
        foreach (WireModelType t in Types) byName[t.FullName] = t;

        var included = new HashSet<string>();
        var queue = new Queue<WireModelType>();
        foreach (WireModelType t in Types)
            if (t.IsSerialized && included.Add(t.FullName)) queue.Enqueue(t);

        while (queue.Count > 0)
        {
            WireModelType t = queue.Dequeue();
            foreach (WireModelMember m in t.Members)
                foreach (string refName in ReferencedTypeNames(m.MemberType))
                    if (byName.TryGetValue(refName, out WireModelType? rt)
                        && rt.TypeConverterKind is null      // a scalar leaf lives in typeKinds, not types — don't follow into it
                        && included.Add(refName))
                        queue.Enqueue(rt);
        }
        return Types.Where(t => included.Contains(t.FullName)).ToList();
    }

    // The game type names a member's type REFERENCES: unwrap generic args and arrays to their constituent tokens
    // (Dictionary`2<EFT.MongoID,EFT.ProfileSettings> -> three tokens; EFT.BonusDescriptor[] -> EFT.BonusDescriptor).
    // The generic-arity backtick is kept, so a game generic (EFT.Foo`1) still matches the model's Cecil full name;
    // a framework container simply isn't in the model and is ignored.
    static IEnumerable<string> ReferencedTypeNames(string memberType)
    {
        foreach (string part in memberType.Split(new[] { '<', '>', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = part.Trim();
            while (t.EndsWith("[]", StringComparison.Ordinal)) t = t.Substring(0, t.Length - 2);
            if (t.Length > 0) yield return t;
        }
    }
}

public sealed record WireModelType(
    string FullName,
    IReadOnlyList<WireModelMember> Members,
    // The TYPE's OWN converter kind (a type-level [JsonConverter] — e.g. MongoID always serializes as a string), or
    // null. Emitted as the wire-map's `typeKinds` (WireMap.KindOf) — the per-type-kind capability. Independent of
    // IsSerialized: a converter leaf type may have no serialized members of its own yet still supply a kind to its
    // users.
    string? TypeConverterKind = null)
{
    // The wire-map inclusion criterion (§6.2), spelled ONCE here so the emitter's filter and any count can't drift:
    // a type is "serialized" iff some member carries a serialization MARKER (a JSON/Unity marker — see below).
    public bool IsSerialized => Members.Any(m => m.IsSerializationMarker);
}

// One member and the authored facts recovered on it — empty when the member carries no recognized attribute (the
// graceful per-game degrade path). The wire-map emitter reads every fact; WireMap.ForType keys on WireName/Converter.
public sealed record WireModelMember(
    string Name,
    string MemberType,
    IReadOnlyList<WireFact> Facts)
{
    public WireFact? Fact(FactKind kind) => Facts.FirstOrDefault(f => f.Kind == kind);
    public bool Has(FactKind kind) => Facts.Any(f => f.Kind == kind);

    // A serialization MARKER = a wire name, a converter, or a persistence flag: the signal that this member's
    // declaring type participates in serialization. Nullability is deliberately NOT a marker — the compiler emits
    // [Nullable] on nearly every reference member, so keying inclusion on it would pull in the whole game.
    public bool IsSerializationMarker => Has(FactKind.WireName) || Has(FactKind.Converter) || Has(FactKind.Persisted);
}
