# Deferred capability: genuine zero-`Il2Cpp` mod code

**Status:** **decided 2026-07-13 — genuine zero IS the goal, and it holds** (see §5 for the recorded answers). *(Reference; question recorded 2026-07-06, re-homed here from the v1 corpus; answered by OpenTarkov, the first production consumer.)*
**Where it sits:** the architecture is described across [`docs/contribution/`](../contribution/02-system-map.md); this page records **one capability the design deliberately does not include**, so it's a conscious decision rather than a silent gap. It is explicitly **out of the v2 parity milestone** (see [`limits.md`](./limits.md)) — it does not block anything; decide, on the record, whether it's ever wanted. Also tracked in the [roadmap](./roadmap.md) (§5).

---

## 1. The question

> Will v2 let modders write mods with **no `Il2Cpp` occurrence anywhere** in mod code?

Short answer: **no — and the design is right not to try, but the reason splits into two categories that must not be conflated.** "il2cpp occurrence" is two different things:

| Category | Example spelling in mod code | Does natural typing remove it? |
|---|---|---|
| **Framework wrappers** — `Il2CppSystem.*`, `Il2CppInterop.*` | `Il2CppSystem.Collections.Generic.List<int>`, `Il2CppReferenceArray<T>` | **Yes.** This is natural typing's whole job. The mod writes `List<int>`, `T[]`, `Task<T>`, `int?`, tuples. |
| **The game's own types** — the game's classes, generated under an `Il2Cpp`-prefixed namespace | `Il2CppToyGame.Player`, `Il2CppEFT.Profile` | **No, by design.** These stay verbatim; the mod names them directly. |

Category 1 is the promise natural typing makes ([`guide/03-natural-typing.md`](../guide/03-natural-typing.md)): a mod never has to name an `Il2CppSystem.*` / `Il2CppInterop.*` **wrapper** — it writes `List<int>`, `T[]`, `Task<T>`, `int?`, tuples. v2 delivers and extends it.

Category 2 is **not** a natural-typing target and cannot be, because natural typing maps a type only where a *faithful BCL twin exists* ([`guide/03-natural-typing.md`](../guide/03-natural-typing.md), "the direction matters"; the architecture is [marshal (12)](../contribution/architecture/12-marshal.md)). `List<T>` has a twin; a bespoke game class `Player` does not — it *is* the game's type. v2 makes this explicit: in `managed/src/Inutil/Marshal/Il2CppConvShapeSource.cs:17-18`, any `Il2CppObjectBase`-derived (game reference) or ref-bearing value proxy is an **identity leaf** — passed through unchanged, the mod handles the raw proxy. The `Il2Cpp` prefix on game types is inherent to referencing the game at all; the schema tests spell it literally (`Il2CppToyGame.SomeEnum`).

So: **any real mod that hooks game code will contain `Il2Cpp<Game>.*` occurrences.** What v2 removes is the wrapper noise and the value-layout hazards around game types — not the game types' own identity.

## 2. What "genuine zero" would additionally require

Making Category 2 disappear is a **separate capability** from natural typing: strip (or alias away) the `Il2Cpp` namespace prefix on the game's *own* generated proxies, so a modder writes `ToyGame.Player` instead of `Il2CppToyGame.Player`. This is purely ergonomic — it changes how a type is *spelled*, not how it's *marshalled*. It touches none of the schema/bridge/planner machinery the rewrite is about, which is exactly why it's a follow-on and not a rewrite concern.

### The one hard constraint: collision safety

The `Il2Cpp` prefix is not decoration — Il2CppInterop adds it so its generated types cannot collide with the **real** BCL/Unity assemblies loaded in the same runtime. `Il2CppSystem.String` and `System.String` are different types living in one process; drop the prefix on `System` and you get ambiguous or wrong binds. **Therefore any de-prefixing must be *selective*: game-specific namespaces only (`ToyGame.*`, the game's own assemblies), never `Il2CppSystem` / `Il2CppInterop` / the il2cpp-Unity projections.** A real `ToyGame.*` .NET assembly won't exist in the runtime, so *those* prefixes are safe to shed; the framework ones are not. Any option below that can't honor this selectivity is disqualified.

A second constraint: whatever the mechanism, the aliased spelling and the proxy's real type must be **the same type**, not a new one. The two seams (compile-time flip and runtime marshal) match hooks by name + parameter *type identity* ([marshal (12)](../contribution/architecture/12-marshal.md)); if `ToyGame.Player` were a distinct new type rather than an alias/forward to `Il2CppToyGame.Player`, every hook and marshaller would miss. This rules out "generate a parallel type hierarchy" and favors alias/forward approaches.

## 3. Options (if the team decides it's wanted)

Argued, with the constraint above as the filter.

**A — Mod-compile-time alias injection (recommended if we do this at all).**
inutil already owns the mod compile step (`CsModHost` in `Inutil.Mods`, Roslyn, no build). Inject a generated `global using ToyGame = Il2CppToyGame;` (namespace alias) set, or a Roslyn syntax rewrite, so the modder writes `ToyGame.Player` and it compiles to `Il2CppToyGame.Player` — the *same* type, so the seams are unaffected. Scoped to mod compilation; touches no shared proxy DLL; trivially reversible; naturally selective (we choose which namespaces to alias). Cost: enumerate the game's namespaces at patch time and emit the alias set; keep it regenerated per game-build like `InteropPatch` already is. Weakness: namespace aliases in C# don't cover every construct cleanly (nested/generic edge cases), so it needs the same test rigor as a family.

**B — Type-forwarding facade assembly.**
Emit a companion assembly of `[TypeForwardedTo]` (or thin alias types) mapping `ToyGame.*` → `Il2CppToyGame.*`, referenced at mod-compile and resolved to the real proxy at runtime. Additive, doesn't mutate Il2CppInterop's output. Weakness: forwarding generics/nested types is fiddly, and it's a second generated artifact to ship and version.

**C — Configure Il2CppInterop's namespace generation directly.**
If the pinned Il2CppInterop version exposes namespace-prefix control, generate the game assemblies without the prefix in the first place. Cleanest spelling, no extra layer — but it bakes the choice into the shared proxy DLLs (harder to make selective, and it's the option most exposed to Il2CppInterop version drift — a standing risk of the interop layer). **Verify the actual config surface of the pinned version before costing this — do not assume it exists.**

**D — Do nothing (the honest default).**
Keep the `Il2Cpp` prefix on game types. It arguably *helps*: it signals "this is a proxy, not the real .NET type," and every modder in the Il2CppInterop ecosystem already reads it that way. The ergonomic win of dropping it is real but small; the collision-safety and version-drift surface it opens is not.

## 4. Recommendation

**Ship v2 as Option D. Treat A as the follow-on if user feedback shows the prefix is a genuine friction point.** Rationale:

- It is 100% orthogonal to the rewrite's actual thesis (correctness + generalization). Bundling it in dilutes the parity gate with a change that has nothing to do with why v1 had 49 bugs.
- The only defensible mechanism (A) lives in the mod-compile layer (`Inutil.Mods` / [mod host (15)](../contribution/architecture/15-mod-host.md)) — a late, self-contained seam — so there's no sequencing reason to touch it early.
- It carries its own risk class (collision safety, §2) that deserves its own validation, not a rider on the cutover.

## 5. The decision the team owes (record the answer here)

**Answered 2026-07-13, by OpenTarkov — the first production consumer (EFT):**

1. **Genuine zero-`Il2Cpp`-in-mod-code IS the product goal — Category-1 elimination was judged NOT sufficient — and
   the goal holds.** Every shipped or shippable OpenTarkov mod, coremod, and dev tool is at zero `Il2Cpp` spellings
   in code (comments aside); the raw reach lives inside the engine, where it belongs. The last holdouts were the
   consumer's own *agent tooling* (two hand-rolled eval channels predating the engine's REPL and reach faces), and
   they were retired in favor of the engine's REPL transports rather than grandfathered — the goal was enforced,
   not narrowed to fit the leftovers.
2. **No de-prefixing mechanism (Option A/B/C) was needed.** This page's Category-2 premise — that the game's own
   proxies arrive `Il2Cpp`-prefixed — does not hold for EFT: Il2CppInterop leaves *game* namespaces unprefixed
   (mods spell `EFT.Profile`, never `Il2CppEFT.Profile`); only the framework projections carry the prefix, and
   those are exactly what natural typing already removes. So for this consumer, Category 2 is **empty** and
   Category-1 elimination *equals* genuine zero. **Option D stands for the framework prefixes** (they are the
   collision guard, and mods never spell them). Revisit A/B/C only for a future game whose own namespaces arrive
   prefixed — and verify the pinned Il2CppInterop's config surface then, not now.
3. **Confirmed: it never gated the v2 cutover.** Genuine zero was reached *after* the cutover, through the engine's
   own capabilities — natural typing, the additive binds, the reach faces, and the REPL consolidation — not through
   a spelling shim.

The standing truth, updated: **v2 removes the wrapper layer entirely; for a game whose proxies keep their natural
namespaces (EFT), that is genuine zero in mod code — reached and held. The framework prefixes remain, intended,
inside the engine only.**
