# Metadata — recovering wire names

*The author-time pillar. Read [the system map](../02-system-map.md) first, and [schema (10)](./10-schema.md)
for the wire-registry's twin.*

## Job

A game author spells a serializable type with wire names that differ from the C# member names —
`[JsonProperty("_id")]`, `[JsonConverter(typeof(StringEnumConverter))]`, `[SerializeField] int _level`.
Il2CppInterop **strips every attribute** off the generated proxy, so at runtime the member is `Handle` and
the wire key `Nickname`, and *nothing connects them*. This pillar recovers that lost mapping: it reads a
game's il2cpp metadata **offline** into one `inutil.wiremap.json` sidecar. It is inutil's **second pillar**
— author-time, optional, and structurally quarantined from the shipped runtime (`Inutil.dll` never
references it or Cpp2IL).

## In the tree

Two parts — the offline **extract**, and the **runtime consumer**:

| File | Holds |
|---|---|
| **extract** — `managed/src/Inutil.Metadata/` | *the offline `Inutil.Metadata` assembly (the Cpp2IL quarantine)* |
| `Cpp2IlStubProducer.cs` | the ONE Cpp2IL call site — `GameAssembly.dll` + `global-metadata.dat` → an attributed "DummyDll" stub |
| `MetadataExtractor.cs` | the extract stage: stub → Cecil → classify → emit `inutil.wiremap.json`; atomic, idempotent, marker-stamped |
| `WireModel.cs` | the normalized model (`WireModel`: types × members × facts), produced once, many consumers |
| `WireMapEmitter.cs` | `EmitJson` — the deterministic `types` + `typeKinds` sidecar |
| `Inutil.Metadata.Cli/Program.cs` | the `inutil-metadata-extract` CLI (`--game <dir>` or direct `dll dat out`) |
| `Inutil.InteropPatch/CecilMemberRef.cs` | the Cecil→pure-view projection (member + attribute blobs); lives with the reader it reuses |
| **schema** — `Inutil.Schema/{MemberRef,WireCorrespondence,WireRegistry,WireFamilies}.cs` | the pure `IMemberRef`/`IAttributeRef` views, the `WireRegistry`, and the single-site `WireFamilies.cs` |
| **consumer** — `Inutil.InteropPatch/WireMap.cs` | the disk-pass reader: loads the sidecar as plain data (no project edge to `Inutil.Metadata`), member-name remapping at patch time |

## Design

**The offline extract is a single pipeline.** `GameAssembly.dll` + `global-metadata.dat` → **Cpp2IL**
(`AttributeInjectorProcessingLayer` re-injects the residue Il2CppInterop strips; `AsmResolverDllOutputFormat`
writes the stub to disk) → **Mono.Cecil** re-reads it (`CecilMemberRef`) → **`WireRegistry.Classify`** →
one normalized `WireModel` → `WireMapEmitter` → `inutil.wiremap.json`. The stub crosses a *filesystem*
boundary between Cpp2IL and Cecil, so Cpp2IL's AsmResolver object model never enters the reader (and there's
no reader-version clash with InteropPatch's Cecil 0.11.5). Cpp2IL fails **loud** on an unsupported metadata
version rather than emitting a partial stub.

**Facts are data, exactly as families are** ([schema (10)](./10-schema.md)). `WireRegistry` is the
member-aspect analogue of `CorrespondenceRegistry`: one recognized attribute is one `Register` row
(`WireCorrespondence` = an attribute anchor + a `FactKind` + an `Extract` closure that pulls the value out of
the decoded blob). The four kinds — `WireName`, `Converter`, `Persisted`, `Nullability` — key consumers, and
`Converter` reuses the schema's `String`/`Enum`/`Opaque` taxonomy verbatim. This is deliberately **not**
merged with `CorrespondenceRegistry` into a generic `Registry<,>`: the two share only the register/classify
*shape*, not the predicate — type-classify is anchor + arity + open-parameter rejection yielding one verdict;
wire-classify is attribute-name match + a value *extraction* yielding zero-or-many facts. Same idiom and
module, different predicate.

**The pure views keep the classifier game-free.** `IMemberRef`/`IAttributeRef` (in `Inutil.Schema`, no
Cecil) are the member-side mirror of `ITypeRef`. `CecilMemberRef`/`CecilAttributeRef` project a stub's
member/attribute onto them offline; synthetic Cecil fakes project onto them in tests — so
`WireRegistry.Classify` is provable with no game, exactly as `ITypeRef` makes the type classifier provable.

**One artifact, one consumer.** `inutil.wiremap.json` is the `Inutil.InteropPatch/WireMap.cs` disk-pass
input: a `types` map of every *serialized* DTO (a member carries a JSON/Unity marker) with all its members
(wire name — recovered or defaulting to the member's own name — plus converter kind, persisted,
nullability), and a `typeKinds` map of each type's own converter kind (a `MongoID` always serializes as a
string). It is keyed by the game's **real il2cpp class names**.

**The marker is the same mechanism page 11 stamps on the proxies.** `MetadataExtractor.StampMarker` folds
`SchemaMarker.WireHash(registry)` with the two input files' content hashes into a `<sidecar>.marker`
(`SchemaMarker`/`ContentMarker` live in `Inutil.Schema`, single-homed so a stamp and a read can't drift).
Note the deliberate asymmetry: the proxy marker has a runtime *reader*, the sidecar one does not yet — a
staleness check is the earned-later consumer, so we stamp now only to carry provenance.

## Invariants

- **Wire facts are single-homed in the sidecar (P1).** One `WireModel`, one `inutil.wiremap.json`; a second
  consumer (version diff) would be another projection off the model, not a second walk.
- **`WireFamilies.cs` is the only file that may spell an attribute anchor** (the C4 guardrail, wire side).
  `WireRegistryDriftTest` derives the anchor set *from the live registry* and greps the rest of the tree —
  a recognizer added tomorrow is covered without touching the test.
- **The marker ties the sidecar to its schema + build.** `SchemaMarker.WireHash` folded with the input
  files' hashes content-addresses "was this produced by these recognizers over this build."
- **Degrade to member-name keys, never a hard failure.** A member with no recognized attribute yields no
  facts; a type/member absent from the sidecar falls back to its member name. A stripped-attribute game
  leaves inutil exactly as capable as today.
- **Both loaders drive the SAME recovered map** — the `Il2Cpp`-prefix bridge is the one place that's reconciled.
- **Fail loud, never silent.** A matched-but-malformed attribute surfaces a `WireRegistry` warning (never a
  silent skip); a malformed sidecar is a build error.
- **Runtime isolation is structural.** `Inutil.dll`'s project closure has no edge to `Inutil.Metadata` or
  Cpp2IL — asserted by `RuntimeIsolationTest` walking the graph, so a future leak trips the moment its csproj
  joins the closure.

## Limits, defers & TODOs

- **It depends on the game not stripping attribute metadata** — outside anyone's control once a game ships.
  Attribute *value* survival under IL2CPP managed stripping is the first in-game hurdle.
- **The Cpp2IL version tax is real and accepted.** Pinned to `2022.1.0-pre-release.21` — the only line that
  reads modern metadata (ToyGame ships **v31**; stable 2022.0.7.2 supports only 24–29). Tracking Cpp2IL's
  release line is the cost of depending on a maintained reader rather than a hand-rolled `.dat` parser.
- **The sidecar marker can't see an `Extract`-only change.** `WireHash` hashes anchor + kind, not the
  closure, so a recognizer that changed only its extraction logic isn't caught — accepted, because the
  sidecar is cheaply re-derived (unlike a patched proxy at load time).
- **The version-diff consumer isn't built** — earned third, no silent scope creep.

## Tests

- **Offline** — `managed/src/Inutil.Metadata.Tests`: `MetadataEmitTests` drives `BuildModel` +
  `WireMapEmitter` over a **synthetic attributed Cecil module** (no game, no Cpp2IL), asserting the exact
  `inutil.wiremap.json` shape — recovery, the malformed-attribute warning, idempotence, `typeKinds`.
  `Program.cs` then runs inutil's *own* Cpp2IL pass over the **real ToyGame v31** (produce stub → Cecil re-read
  → extract → `Handle`→`Nickname`, `Side`→`faction`+enum → idempotent), skipped gracefully when no game is
  provisioned. In `Inutil.Schema.Tests`: `WireRegistryDriftTest` (C4 wire), `RuntimeIsolationTest` (the §7
  hard line), `MarkerTests`.

## Why it's shaped this way

In v1, wire-name/converter recovery lived in an **external, out-of-repo tool** — OpenTarkov's
`tools/lenswire` — the only cross-repo dependency the whole design carried. It read the same DummyDll-style
metadata inutil could read itself, one step earlier than the proxies InteropPatch operates on. v2 owns it
in-tree: `Inutil.Metadata` reads `GameAssembly.dll`/`global-metadata.dat` directly — the same two files
`--game <dir>` already locates for the patcher — and the external tool is gone. Owning it in-tree is also
what lets the pillar be a *native citizen* of the schema engine rather than a fork: a second aspect
(member → facts) alongside the type aspect, reusing the four moves — project, classify-as-data, thin
consumers, single spelling site. The full rationale, and the generalizations A–E it required, are captured
in the **Design** section above.
