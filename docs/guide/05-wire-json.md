# 5. Wire JSON — building game objects the way the game builds them

*Many game types are **wire DTOs**: the game never constructs them by hand, it deserializes them. This chapter
is about building those the same way — and about the checked object-literal form that keeps it honest.*

## When you're here

You need a live game object that the game itself normally receives as JSON — a template, a descriptor, a
config record. The obvious moves both go wrong in the same way:

```csharp
var scheme = new Game.ProductionScheme();     // zero-init: no converters, no defaults
Fields.SetInt(scheme, "areaType", 4);         // stringly-typed; a rename silently no-ops
Fields.SetBool(scheme, "continuous", true);
```

Two problems. The obvious one is that `Fields.Set*` is by-name — chapter 4 already warns that its misses are
silent by design. The subtler one: half these members expose **getters only**, because the type's contract is
"I am produced by deserialization". Poking the backing storage reaches around that contract, so nothing that
the deserializer would normally do — converters, defaults, callbacks — happens.

If the type has a wire form, **the deserializer is its constructor**.

## The seam

`Inutil.Json` is a delegate seam: a per-game shim registers the game's own (de)serializer once at startup, and
the engine stays Newtonsoft-free (many il2cpp games bundle no Newtonsoft at all).

```csharp
// once, at startup — the per-game shim, which may reference the game's Newtonsoft proxy
Inutil.Json.UseDeserializer((json, il2cppType) =>
    JsonConvert.DeserializeObject(json, il2cppType, Game.JsonConverters.SerializerSettings));
Inutil.Json.UseSerializer(obj =>
    JsonConvert.SerializeObject(obj.Cast<Il2CppSystem.Object>(), Game.JsonConverters.SerializerSettings));
```

With that registered, everything below runs through the game's **own** converters — so polymorphic graphs,
enums and custom wire types materialize exactly as they do when the game talks to its server. A call with no
serializer registered fails loud rather than guessing.

```csharp
T?   Json.To<T>(string json)          // wire JSON      -> live proxy
T?   Json.To<T>(JsonNode node)        // built DOM      -> live proxy
T[]? Json.ToArray<T>(string|JsonNode) // JSON array     -> natural T[]   (no Il2CppReferenceArray spelling)
Dictionary<K,V>? Json.ToDict<K,V>(string)  // JSON object -> natural BCL dictionary
JsonNode? Json.From<T>(T obj)         // live proxy     -> wire JSON
```

## The checked shape

Writing JSON string literals in C# is miserable, so there's an object form:

```csharp
var scheme = Json.To<Game.ProductionScheme>(new
{
    _id            = id,
    areaType       = EAreaType.WaterCollector,   // enum -> underlying number
    endProduct      = tpl,                       // string -> MongoID, by the game's converter
    continuous     = true,
    productionTime = 3600,
    requirements   = Array.Empty<object>(),
});
```

**The keys are checked.** This is the whole reason the object form exists. Anonymous-object keys are just
identifiers — nothing in C# binds them to `ProductionScheme` — so without a check, `areaTyp` would compile,
serialize, deserialize, and leave the member at its default. Silently. That is *worse* than a JSON string,
which at least announces itself as untyped wire data. So every key is resolved against the type's recovered
wire members, and a miss fails at the call site:

```
InvalidOperationException: Inutil.Json.ToNode(EFT.Hideout.ProductionScheme): unknown wire member 'areaTyp'.
  Did you mean: 'areaType'?
  Known members: _id, areaType, requirements, productionTime, endProduct, count, continuous, …
```

Those names come from the [metadata pillar](../contribution/architecture/16-metadata.md): the game's own
`[JsonProperty]`-style attributes are stripped off the generated proxies, inutil recovers them from il2cpp
metadata and re-attaches them as `[JsonPropertyName]`. It is the **same list** `Inutil.Wire.Serialize` writes
from — one recovered set, so the read and write directions cannot drift apart.

**Values are coerced to the member's declared type.** Not cosmetic: a `120.0` landing in an `int`-declared
member makes a strict reader throw *mid-graph*, aborting the whole enclosing object rather than the leaf. So
`new { count = 120.0 }` is written as `120`.

Need the DOM for one member? A `JsonNode` value passes through untouched:

```csharp
Json.To<T>(new { settings = JsonNode.Parse(rawFromDisk) });
```

And `Json.ToNode<T>(shape)` gives you the built node without deserializing — useful when you want to inspect
or serve the JSON rather than materialize a proxy.

## Honest limits

- **A polymorphic member whose subtype is chosen by a converter that only engages on the BASE static type**
  comes back as data-less base instances. No spelling of the outer object fixes this — deserialize such
  elements *as their concrete subtype* and assign them:
  ```csharp
  Game.Requirement req = Json.To<Game.ItemRequirement>(elementJson);
  ```
- **A type absent from the wiremap has no recovered members**, so nothing can be checked. `ToNode` says
  exactly that rather than rejecting every key as a typo — build such a type from a JSON string instead.
- **Enums are written as their underlying number**, while `Wire.Serialize` writes enum *names*. Deliberately
  asymmetric: a numeric enum is accepted by any reader, a name needs a string-enum converter the game may not
  register. Pass a string explicitly when the wire wants the name.
- **The check is one-directional.** It proves every key you wrote *exists*; it does not prove you wrote every
  key the game needs. A member you omit stays at its default.
- **Nested shapes are checked** when the nested member's declared type itself has recovered wire members;
  otherwise the value is serialized as-is.

## Prefer this to by-name writes

When a type has a wire form, reach for `Json.To<T>` before `Fields.Set*`. The rule that generalizes:

> If you find yourself writing read-only members by name on a type the game only ever deserializes, you are
> hand-minting a wire object. Build it on the wire side instead.

`Fields` stays the right tool for live game objects with no wire form — see [chapter 4](./04-escape-hatches.md).

## Checkpoint

- ✅ you know wire DTOs are constructed by the game's deserializer, not by `new` + field pokes
- ✅ you can register the per-game (de)serializer seam and use `To<T>` / `ToArray<T>` / `ToDict<K,V>` / `From<T>`
- ✅ you can write checked object literals, and you know a misspelled key fails loud at the call site
- ✅ you know what the check does *not* cover: polymorphic converter gates, omitted members, unmapped types

**Next → [6. The REPL](./06-repl.md)** — experimenting against the running game with no compile-deploy loop.
