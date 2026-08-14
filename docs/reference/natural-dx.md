# Natural DX — plain C# over the wire

**Status:** design target / north-star, **not yet shipped.** A prior attempt at this DX shipped as *the
Lens* — a `JsonNode`-view generator over recovered wire names — but was retired as the wrong shape (see
*Grounding* below); nothing currently in the tree offers typed wire/serialized-data access. This doc
specifies the DX being designed for that gap and the constraints that shape it, so "is this natural?" has
one written answer. Marked a target on purpose (P5): there is no in-tree implementation to point at today.

**Grounding:** the serverstub lens retrospective (the motivating write-up: *"the modder manipulates ordinary
objects and never types the word Lens"*), a read of serverstub's real usage (`ProfileMint`, `ItemDb`,
`Backend`) against the now-retired `Inutil.LensGen` / `Inutil/Lens/Json.cs` and the surviving
`inutil.wiremap.json` sidecar ([architecture/16](../contribution/architecture/16-metadata.md)), and the
field shapes in serverstub's own wiremap. Each claim below cites the mechanism, not intuition.

---

## The one-line model

> **Serialize and edit the game's wire shapes as ordinary C# — plain classes, real `List<T>`/`Dictionary`,
> `.Field` access, `foreach`/LINQ — with the wire names, converters, absence, and unknown fields handled by
> the type, so no lens, `Opaque`, `.Node`, or wire string ever reaches your code.**

Natural DX is the serialization twin of **natural typing** ([guide/03](../guide/03-natural-typing.md)). Natural
typing makes a proxy's *method signatures* speak BCL types (`List<int>`, `Task<T>`, `int?`) instead of
`Il2CppSystem.*`. Natural DX makes a game type's *serialized shape* speak a plain POCO instead of a
JsonNode-view keyed by recovered wire names. Same philosophy — write normal .NET, let the engine absorb the
tax — one layer up: from **types** to **shapes**.

We call a value that achieves it a **natural DTO**, the concept **natural wire**, and the review test
**"is this natural?"**.

## Before / after

The same `ProfileMint`-shaped work — patch a field, walk an inventory, re-serialize — under the now-retired
Lens vs. the target (nothing in the current tree offers a wire-editing API; this contrasts the retired
approach against what should replace it):

```csharp
// BEFORE — the retired lens: three vocabularies in one function
using Descriptor = Inutil.Lenses.ProfileDescriptorLens;

var pd = Descriptor.Of(node);                              // a typed view over a detached JsonNode
pd.Health.UpdateTime = Inutil.Opaque.Of(Clock.NowSec);    // wire-shape escape hatch, in your face
foreach (var el in Inutil.LensList<ItemLens>.Lenient(     // a bespoke list — no LINQ, no IReadOnlyList
             pd.Inventory.items.Node, ItemLens.Elem))
    Regen(el);
var wire = pd.Node.ToJsonString();                         // .Node — drop back to raw to serialize

// AFTER — natural: it's just C#
var pd = Wire.Deserialize<ProfileDescriptor>(json);        // a plain POCO, lossless (see below)
pd.Health.UpdateTime = Clock.NowSec;                       // an ordinary field; the converter hides the wire shape
foreach (var it in pd.Inventory.Items) Regen(it);          // a real List<Item> — foreach / LINQ / indexer all work
var wire = Wire.Serialize(pd);                             // unknown keys + field absence preserved
```

`Wire` is a thin facade over `System.Text.Json` pre-configured with the natural conventions (extension-data
overflow, the recovered wire-name map, the converter kit) — the managed-domain serializer serverstub already
reaches for (`ProfileMint`: *"the mod's OWN System.Text.Json"*). Crossing into the game's il2cpp Newtonsoft
stays exactly where it is: `Inutil.Json.To`/`From` at the serve boundary, and *only* there.

## The test: "is this natural?"

A handler is natural when **all** of these hold — the review checklist:

1. **No lens vocabulary reaches the modder.** No `LensList`/`LensDict`/`Opaque`/`WireLens`, no `.Node`, no
   `Inutil.Lenses.*` type. The type they hold is a plain class.
2. **Collections are real BCL collections.** `List<T>`/`Dictionary<K,V>` — `foreach`, LINQ, indexers, and
   `IReadOnly*` consumers all work, not a member-for-member re-spelling that implements no interface.
3. **Wire names are invisible.** The modder writes `pd.Id`, never `node["_id"]` and never learns the
   mapping — it lives in an attribute the recovered metadata filled in.
4. **It is lossless by default.** Round-tripping preserves SPT-extra keys and the absent-vs-null-vs-value
   distinction without the modder thinking about it (see *the shape*, below).
5. **The proxy is behind the boundary.** The il2cpp proxy appears only inside `Inutil.Json.To`/`From` at the
   serve seam — never as the modder's working surface.

Fail any one and it isn't natural yet.

## Why not "improve the proxy" — the data-model wall

The tempting shortcut is to skip a new type and make the **il2cpp interop proxy itself** the natural object:
re-attach the recovered `[JsonProperty]`, add wire getters/setters, serialize it directly. It cannot work,
and the reason is **not** the native-backing detail — it is a *data-model* fact, provable from serverstub's
own comments:

- **The wire is a superset of the type.** SPT's on-disk/wire JSON carries fields the game type does not model.
  A proxy (or any type shaped like the game type) can only ever hold the game-type's subset, so re-serializing
  through it **drops** the extra — `ProfileMint`: *"rebuilding risks dropping a sub-structure like
  BodyPartsDamageHistory the client's deserializer expects."*
- **The wire is absence-sensitive; the type is not.** SPT distinguishes an *absent* field from a
  *present-default* one (`StackObjectsCount` absent = "no cap" vs `0` = "sold out"; the template *omits*
  gameplay lists on purpose). A materialized proxy has every field at some default and cannot represent
  "absent" — `ProfileMint`: *"adding wrong-typed empties would be a net risk."*
- **The primary path never holds a live object at all.** serverstub serves the JSON **raw**
  (`ProfileMint`: *"LoadProfiles serves it raw"*); it materializes only at the one seam the game's own code
  demands a live object. Making the proxy the working surface would force materialization on every step that
  today is pure JSON.

So a proxy — however hardened or re-annotated — is a **lossy projection** of the wire. Optimizing for DX
*rejects* it: a lossy-and-invisible object silently corrupts profiles, and the modder spends their days
chasing a missing `BodyPartsDamageHistory` or an `absent`-turned-`0`. Lossy-invisible is the opposite of
seamless. The same objection rules out a naïve POCO that models only the known fields — which is why the
natural DTO has three field flavors, not one.

## The shape — one type, three field flavors

A natural DTO is lossless *without ceremony* because its fields come in three kinds, all ordinary C#:

| Flavor | For | Spelling |
|---|---|---|
| **typed** | clean known fields | `string Nickname`, `List<Item> Items`, `int Level` |
| **escape-hatch** | the irreducible dirty fields | a coercing `[JsonConverter]` (polymorphic `armorClass` string-or-int → the modder just reads `int`); `Optional<T>` for three-state absence (`x.StackObjectsCount.IsPresent`) |
| **overflow** | every unmodeled SPT key | one `[JsonExtensionData]` member — caught and round-tripped untouched, invisible unless the modder asks for a key by name |

The overflow member is the standard .NET pattern for "typed access to a superset JSON," and it is what makes
a POCO lossless where the retrospective assumed only a DOM could be. The escape-hatch flavor is the
[guide/04](../guide/04-escape-hatches.md) discipline applied per-field: localize the one honest exception,
don't force the whole shape through a DOM so a few fields can be dirty. This is also where the recent lens
work on **absence / `Opaque` / `Lenient`** re-homes — as a small converter + `Optional<T>` kit, not a parallel
type system.

## Two populations — the corrected axis

The split is **not** game-type vs owned-shape. It is *how much of the shape you own*, and it cross-cuts:

- **Authored configs** — you own the entire shape, no unknown keys, not absence-sensitive (`RagfairConfig`,
  `RepairCfg`, most of serverstub's ~60 opted-in shapes). → a **plain authored POCO**, lossless with STJ,
  **inutil uninvolved** (nothing was recovered; you author the names). This is most of the win.
- **Pass-through wire blobs** — a fat external JSON you edit-in-place and must preserve the rest of
  (`EFT.ProfileDescriptor` from `profiles.json`, the item DB, `PmcProfile`). → a natural DTO with recovered
  wire names + the overflow/escape-hatch flavors. `ProfileDescriptor` is a *game* type that is pass-through;
  the item DB is *owned* but pass-through — the axis genuinely cuts across the game/owned line.

## Where the work lives

Consistent with the one-directional dependency (engine → consumer; **inutil never knows a consumer's
shapes**):

- **inutil generates the natural DTO for a game type only** — `[…(typeof(EFT.ProfileDescriptor))]`. The
  `inutil.wiremap.json` sidecar (Cpp2IL → Cecil → `WireRegistry`, [architecture/16](../contribution/architecture/16-metadata.md))
  fills in `[JsonProperty]` + converter-kind + nesting, so a game DTO costs the modder **one attribute** and
  zero hand-transcription, and stays drift-proof across game updates. The generated DTO is *managed data*
  (real `string`/enum/`List<T>`), not the il2cpp proxy — so a managed serializer round-trips it cleanly and
  the "can't traverse a native proxy" wall never applies.
- **The consumer authors its own configs** — ordinary POCOs, the consumer's repo, no inutil codegen. The
  string-keyed `GenerateLens("serverstub.X")` (inutil generating for a *named non-game shape*) is exactly the
  cross-line coupling this removes.
- **Neutral primitives** — `Optional<T>` and the coercing converters are game-agnostic; they may live in
  inutil as neutral utilities or in the consumer, but they are never *about* a specific non-game shape.

## Honest limits & open questions

Fail-loud, never silent (P4), and the parts not yet resolved:

- **STJ vs. the game's Newtonsoft fidelity.** A natural DTO is round-tripped by managed STJ; the JSON it
  produces must satisfy the game deserializer at the boundary. This is **mitigated** because the boundary is
  always `Inutil.Json.To` — the game's own converters (`EftJsonConverters.SerializerSettings`) do the value
  coercion, so the DTO needs only correct **keys and structure**, the same property that made the retired
  Lens work. It is not yet proven end-to-end.
- **The dirty-field converter count is unknown.** How many of the ~60 shapes carry a polymorphic /
  three-state field decides how big the converter kit is. Count before committing scope (P2 — earn the
  abstraction on evidence).
- **Deep partial modeling.** `[JsonExtensionData]` is per-object; a subtree you deliberately leave unmodeled
  is typed as `JsonNode`/`JsonElement` and not descended. That is a per-branch choice, not a default.

## Graduation path

When natural wire ships and is battery-green under both loaders, this spec splits into its permanent homes,
retiring itself:

- a **guide** chapter beside `03-natural-typing.md` (the modder how-to), and
- an **architecture** section in / beside `16-metadata.md` (the generator + recovery mechanism), and
- a raw by-name escape hatch, if the genuinely irreducible construct-from-nothing niche still needs one,
  in `04-escape-hatches.md` rather than a revived lens.

The end state is the one the retrospective asked for: the modder manipulates ordinary objects and never
types the word *lens*.
