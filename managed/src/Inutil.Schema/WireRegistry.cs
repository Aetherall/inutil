namespace Inutil.Schema;

// The single registry the pillar's consumers (wire-map, version-diff) consult via Classify — the
// member-aspect analogue of CorrespondenceRegistry (docs/contribution/architecture/16-metadata.md). A recognizer is registered once
// in WireFamilies.cs; nothing else spells an attribute name (the mirror of the C4 single-site guardrail).
//
// Deliberately NOT merged with CorrespondenceRegistry into a generic Registry<TSubject,TFact>: the two share only
// the register/classify SHAPE. Type-classify is anchor-name + arity + open-parameter rejection with a single
// shape verdict; wire-classify is attribute-name match + a value EXTRACTION yielding zero-or-many facts per
// member. Same idiom and module, different predicate.
public sealed class WireRegistry
{
    readonly List<WireCorrespondence> _all = new();

    public WireRegistry Register(WireCorrespondence correspondence)
    {
        _all.Add(correspondence);
        return this;
    }

    public IReadOnlyList<WireCorrespondence> All => _all;

    // Classify a member: walk its attributes, match each against the recognizers by attribute type name, run the
    // extractor, collect the facts. The analogue of CorrespondenceRegistry.Classify — but yielding a structured
    // WireClassification (facts + warnings), because a member can carry several facts and the fail-loud path must
    // surface.
    //
    // Three outcomes per attribute — the distinction IS the graceful-degrade-vs-fail-loud contract:
    //   - no recognizer matches -> silently ignored (an unrecognized attribute is not our concern; the per-game
    //     degrade path).
    //   - a recognizer matches AND Extract yields a fact -> recorded.
    //   - a recognizer matches BUT Extract returns null -> the attribute is OURS yet its blob is malformed; a
    //     WARNING, never a silent skip.
    public WireClassification Classify(IMemberRef member)
    {
        var facts = new List<WireFact>();
        var warnings = new List<string>();

        foreach (IAttributeRef attr in member.Attributes)
        {
            bool matchedAny = false;
            foreach (WireCorrespondence c in _all)
            {
                if (c.AttributeTypeFullName != attr.AttributeTypeFullName) continue;
                matchedAny = true;
                WireFact? fact = c.Extract(attr);
                if (fact is not null)
                    facts.Add(fact);
                else
                    warnings.Add(
                        $"attribute {attr.AttributeTypeFullName} on {member.DeclaringType.FullName}.{member.Name} " +
                        $"matched the {c.Kind} recognizer but its blob was malformed — no {c.Kind} fact extracted");
            }
            _ = matchedAny; // an unmatched attribute is deliberately NOT a warning: graceful per-game degrade.
        }

        return new WireClassification(facts, warnings);
    }

    // The recovered facts for a member, discarding warnings — the ergonomic path for consumers that have already
    // gated on Classify(...).Warnings (or for the offline test's happy-path assertions).
    public IReadOnlyList<WireFact> Facts(IMemberRef member) => Classify(member).Facts;
}
