# GUIDANCE.md — what a consumer should write today

*Findings from reading inutil's first real consumer (OpenTarkov's `offline/` — 124 files, ~16.5k lines) against
a live, patched EFT interop. Everything here is **consumer-facing**: what to write, what to stop writing, and how
to tell the difference. The inutil-side defects the same pass surfaced are in [GAPS.md](./GAPS.md).*

> **Read this first — how these claims were checked.** Every "this compiles / this doesn't" below was verified by
> compiling a probe mod against a real interop (`inutil-check check <bepDir> <modDir>`), **not** by reading a
> signature dump. `surface-query --methods` cannot answer the flipped-vs-unflipped question — it renders il2cpp
> types under their natural names, so both states print identically ([GAPS.md](./GAPS.md) G3). Until that is fixed,
> **the compiler is the only oracle.** Nothing here is runtime-verified: a compiling assignment can still marshal
> wrong, and this consumer's own comments record exactly that happening. Confirm behaviour with `mod-verify` before
> trusting a change in a shipped path.

## 0. First, re-patch — the consumer's interop is stale

Before changing a line of consumer code, re-run `inutil-interoppatch` over the overlay's `BepInEx/interop`. On the
overlay as found, the current build flips **783 additional members across 19 DLLs**. Much of the awkward spelling
below is not a limitation the consumer is working around — it is a limitation that was already lifted on disk
nowhere.

**Verify it this way, not by re-running the patcher and reading the summary.** The patcher is idempotent, so a
second run over an already-patched tree reports `patched 0 DLL(s), 0 member(s) flipped` — which reads exactly like
"nothing left to do" and is how I first talked myself into the opposite conclusion. Patch a *copy* and diff the
behaviour: compile the same probe against the old tree and the new one.

**One thing breaks on re-patch, and it is the good kind.** `offline/src/kernel/Items.Fixtures.cs:135` stops
compiling, because the workaround it contains is no longer legal against the naturalized property (§5). That is the
entire breakage — the rest of `offline/` compiles clean against the re-patched tree.

## The rule that generalizes

> **Do not carry a workaround forward on the strength of the comment that introduced it.**

Nearly every finding below is the same shape: the consumer hit a real limit, wrote a correct workaround, documented
it carefully — and the limit later closed without the workaround being revisited. The comments are honest records of
a state of the world that has moved. When a workaround costs type-safety (a by-name write, a cast, a raw wrapper
spelling), re-probe it against the current interop; the check costs one `mod-check` run.

## 1. Array-typed properties are natural — mostly

`Il2CppReferenceArray<T>` construction for an array-typed **property** is obsolete. Both of these compile against
the consumer's overlay *even before* re-patching:

```csharp
collection.ScavCaseSchemes = Array.Empty<EFT.Hideout.ScavCaseScheme>();   // was: new Il2CppReferenceArray<…>(0)
scheme.requirements        = Array.Empty<EFT.Hideout.Requirement>();
```

In `offline/` that pattern is ~30 sites: `hideout/Hideout.Fixtures.cs:205-252`, `kernel/Backend.cs:113-114,324-333`,
`kernel/Items.Fixtures.cs:75-108`, `profile/Profile.Fixtures.cs:195-204`, `kernel/Handbook.Fixtures.cs:28-33`,
`customization/Customization.Fixtures.cs:92`.

**The exception matters more than the rule.** `EFT.Dialogs.TraderDialogsDTO.Elements` (`kernel/Backend.cs:365`) is
typed `Il2CppArrayBase<T>` — a *third* array wrapper inutil does not flip, before or after re-patching
(GAPS.md G2). It still needs the explicit spelling. So this is a **per-member** fact, not a per-codebase one: probe
each site, don't sweep.

## 2. A "getter-only" property often isn't

`hideout/Hideout.Fixtures.cs:220-229` writes nine members by name:

```csharp
// the comment says: "areaType/continuous/productionTime/count/requirements expose GETTERS ONLY"
Inutil.Fields.SetInt(s, "areaType", (int)area);
Inutil.Fields.SetBool(s, "continuous", true);
…
```

They are not getter-only. `EFT.Hideout.BaseHideoutScheme` declares real setters for all eight members plus
`requirements`. Written typed, the block is compile-checked and a game-side rename breaks the build instead of
silently no-opping:

```csharp
s._id = id;  s.areaType = (int)area;  s.continuous = true;
s.requirements = Array.Empty<EFT.Hideout.Requirement>();
```

`Fields.Set*` returns `false` on a miss, and that return is discarded at every one of these call sites — which is
the failure mode the typed form removes entirely. When you *must* stay by-name, check the return
(see [guide 4](./docs/guide/04-escape-hatches.md)).

## 3. Getter-only for real? Look for the backing-field twin

`session/MenuData.Hooks.cs:95-96`:

```csharp
Inutil.Fields.SetString(Self, "<LocationTime>k__BackingField", wt.Time);
Inutil.Fields.SetFloat (Self, "<Acceleration>k__BackingField", wt.Acceleration);
```

An auto-property's backing field surfaces as its **own proxy property, with both accessors** — so this is a typed
write, angle brackets and all:

```csharp
Self._LocationTime_k__BackingField = wt.Time;
Self._Acceleration_k__BackingField = wt.Acceleration;
```

Confirm the spelling per type before relying on it — the `<X>k__BackingField` → `_X_k__BackingField` sanitization is
a convention, not a guarantee. Full treatment, including why this is *safer to author* rather than more *legitimate*:
[guide 4 → "Getter-only property? Look for its backing field first"](./docs/guide/04-escape-hatches.md).

## 4. Natural dictionaries: assign, don't mutate

`profile/Profile.Fixtures.cs:283-297` reaches the `Customization` field by name and writes entries through
`ValueTypeBridge.InvokeUnboxed(dict, "set_Item", …)`. `EFT.ProfileDescriptor.Customization` is a natural
`Dictionary<EBodyModelPart, MongoID>`, so the dictionary can be built in plain C# and assigned.

**Keep the insight in that comment.** It records that the *getter* returns a freshly marshalled copy, so mutating
what the getter hands back is lost. That is still true, and it is exactly why the fix is to **assign a complete
dictionary through the setter**, not to fetch-and-mutate.

This one deserves a runtime check before shipping: the same comment records a live `ArgumentException`
("Object contains non-primitive or non-blittable data") from the property path. Compiling is not evidence that the
marshalling is now correct, and `MongoID` is ref-bearing — historically the exact shape that went wrong here.

## 5. Ref-bearing `Nullable`: delete the cast, after re-patching

`kernel/Items.Fixtures.cs:131-136`:

```csharp
// today, against the stale overlay:
it.ParentId = (Il2CppSystem.Nullable<EFT.MongoID>)
    Inutil.Marshal.ValueTypeBridge.RefToNullable(new EFT.MongoID(parent));

// after re-patching (§0) — ParentId is a real EFT.MongoID?:
it.ParentId = new EFT.MongoID(parent);
```

Verified both directions: the natural form fails on the overlay as found and compiles against the re-patched tree;
the cast does the reverse. `it.ParentId = null` also compiles, so "no parent" stays expressible — the property
naturalized to `EFT.MongoID?`, not to a bare `EFT.MongoID`.

This is the case that started the whole thread, and it is the reason §0 comes first: the fix shipped, the consumer
never saw it.

## 6. Wire JSON: the checked shape needs a wiremap first

`Json.To<T>(new { … })` (the checked object-literal form, [guide 5](./docs/guide/05-wire-json.md)) is the right
answer for hand-minting a wire DTO — and the natural replacement for §2's by-name block. But on this consumer's
overlay the patcher reports:

```
>> wire-attrs: no usable wiremap at …/inutil.wiremap.json — skipped (proxies keep member-name serialization)
```

Two consequences. Neither is silent — this is a capability being switched off, not a correctness bug:

- **The checked object-literal API is refused outright.** `Json.ToNode` throws on a type with no recovered members
  (`managed/src/Inutil/Wire/Json.cs:94-99`), naming both possible causes. So `Json.To<T>(new { … })` cannot be used
  for any EFT type until a wiremap exists — it does not silently degrade to unchecked.
- **`Wire.Serialize` still produces correct JSON, via the game's serializer.** With no recovered members every value
  takes the opaque path and is delegated to `Json.From` (`Wire.cs:84,125-137`), which reads the intact native
  attributes. Right answers — but no advantage over the `Json.From` this code already calls everywhere.

The prerequisite is running `inutil-metadata-extract`; the pipeline currently stages only `inutil-interoppatch` and
`inutil-check` (`dev-attach.sh:56`). Until then, prefer §2's typed setters for these types — the shape API is not
available to them at all.

## 7. Helpers worth deleting once inutil grows them

Consumer-local reimplementations of things that belong upstream (tracked as GAPS.md G6):

| Consumer code | What it is |
|---|---|
| `kernel/Serve.cs:17-18` — `Json.To<T>(Json.From(v).ToJsonString())` | a deep clone; wants `Json.Clone<T>` |
| `customization/Customization.Service.cs:15` | a `JsonNode` string-round-trip purely to reparent (net6 has no `DeepClone`) |
| `kernel/WireUpd.cs:11,14,19,27,30` | proxy⇄POCO bridging, hand-rolled in both directions |
| `…ToArray<T>(…)?.ToList() ?? new()` (~8 sites) | a null-to-empty convenience |
| `kernel/MirrorShim.cs:21-43` | the `Mirror` registration shim, raw reflection re-resolved per call |

Until those land, keep them where they are — one named helper per repo beats the same idiom inlined at 30 sites.

## 8. When you have to drop to the raw hook tier

`clientfix/ForceLocalRaid.cs:11-17` documents the boundary accurately and is worth reading as the model for how to
record one: a `Task`-returning method cannot be *replaced* through `Hook<T>` + `Proceed` (it throws
`MissingMethodException` on the `Task` constructor), so a `Hooks.Pre` that only rewrites an input argument and never
touches the return slot is the correct shape. Note what makes it safe — the original's `Task` flows back untouched
because the hook never writes the return. A raw hook that *does* need to transform the result has no such escape.

This is inutil's most consumer-visible ergonomic limit (GAPS.md G8).
