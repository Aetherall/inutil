# TODO — the next-step candidates for inutil v2

**Status:** decision menu, for the team / next contributor
**Date:** 2026-07-09
**Grounding:** a read of the current `v2-rewrite` tip against `docs/reference/limits.md` and
`docs/contribution/architecture/16-metadata.md`, plus direct verification of marker/battery state in the tree. Each item cites
the file/line it is based on, and marks what is *verified in the tree* vs *asserted by a doc*.

---

## 0. Where things stand (both major arcs are "proven")

Two arcs have each reached a milestone where the hard part is behind us:

- **Runtime engine — done & hardened.** All seven course-corrections (`docs/contribution/01-philosophy.md`
  C1–C7) landed; both v1→v2 engine regressions are closed — delegate params and virtual
  container-param flip (`docs/reference/limits.md` CORE 1/2). Every not-yet-built path fails **loud**.
- **Metadata pillar — spine proven via its first consumer.** The Cpp2IL → Cecil → `WireRegistry` →
  `inutil.wiremap.json` pipeline is built. Its first consumer was the Lens (`Inutil.LensGen` /
  `Inutil.Lens`), which round-tripped GREEN in-game before the maintainer retired it as the wrong shape
  (see `docs/reference/natural-dx.md`); the pillar's surviving consumer is
  `Inutil.InteropPatch/WireMap.cs`'s member-name remapping at patch time
  (`docs/contribution/architecture/16-metadata.md`). The in-game battery has grown well past that
  baseline — now **77/77 core, 91/91 with the REPL cases** (`docs/reference/limits.md` is the count
  source of truth).

So the open work is no longer "make the hard thing correct" — it is a prioritization call across the
four directions below. Two of these (§1 marker, §2 wire-map) are the standing
*"decision the team owes"*; record the pick here.

---

## 1. The §7.2 content-addressed marker  ✅ DONE (2026-07-09)

Closes the **last silent-misbehavior seam** in the whole tree *and* discharges metadata-pillar
**Generalization D** — one mechanism, two payoffs.

**Landed.** `ContentMarker` + `SchemaMarker` in `Inutil.Schema` (the single assembly both the offline
patcher and runtime `Inutil.dll` share — so the marker format is single-homed, no drift). The patch
step stamps `inutil.interop-marker` = `SchemaMarker.Hash(Families.Default())`; `LoaderShim` reads it
at Attach and warns loud on Missing/Stale, silent on Current. The same mechanism stamps the
`inutil.wiremap.json` sidecar (Generalization D, both artifacts). Proven offline (`MarkerTests`, interoppatch-test marker
asserts) **and in-game** (`interop.marker.current` + `interop.marker.detects-mismatch`; battery **GREEN
77/0**). See `docs/reference/limits.md` Gap 3 (now CLOSED). The sidecar's staleness *reader* remains earned-later (§2/§6).

**What / where (verified unbuilt in the tree, 2026-07-09):**
- `managed/src/Inutil/Host/LoaderShim.cs:20` — still a no-op `TODO(§7.2 marker)`; the read-and-warn
  at Attach is a structural no-op.
- No `InteropMarker.cs` exists; no registry-hashing / content-addressing anywhere in
  `managed/src` (grep for `ContentAddress`/`RegistryHash`/`SchemaHash` returns only an unrelated
  mod-compile hash in `Inutil.Mods/CsModHost.cs`).

**Why it matters most:** `docs/reference/limits.md` Gap 3 names this as *"the one spot where a genuinely silent
misbehavior could slip past."* A mod compiled expecting flipped (natural-typed) proxies but loaded
over **un-patched** proxies is currently **undetected** — the only place in the codebase where a
mismatch does not fail loud. The project's entire discipline is fail-loud-never-silent (P4); this is
the single hole left in it.

**The dual payoff (`docs/contribution/architecture/16-metadata.md`, Generalization D):** §7.2 wants the marker to be
*content-addressed* — a hash of the schema registry that produced the patch, so "is this proxy
current" is a structural check, not two humans syncing constants (it flags the `InteropMarker.cs:25`
drift bug the doc cites). Build it **once, generally**, and stamp **both** artifacts: patched proxies
(hash of `CorrespondenceRegistry`) and metadata sidecars (hash of `{metadata build id,
WireRegistry}`). "Is this artifact current for this game build" becomes one equality check reused by
both pillars — a generalization that pays for itself twice. Now (right after the pillar landed) is the
cheapest time to build it, while both artifacts are fresh and want stamping.

**Shape of the work:** (a) stamp a content-addressed marker in the interop dir at patch time
(hash the registry that drove the patch); (b) read it in `LoaderShim` at Attach and warn — loudly,
actionably — if absent or stale; (c) extend the same marker to the sidecar-extract stage.

**Acceptance:** a mod loaded over un-patched (or schema-stale) proxies produces a precise, actionable
warning at Attach — not a silent mismatch; the same marker check gates both the proxy dir and the
`inutil.wiremap.json` sidecars.

**Counter-argument (honest):** it is *diagnostic-only* — adds a warning, not capability. A modder on
the standard `validate.sh`/patch flow never trips the mismatch, so live exposure is low. If the
priority is user-facing value, §2 or §4 deliver more.

---

## 2. Wire-protocol map — the next earned pillar consumer  ✅ DONE (2026-07-09)

**What / where:** `docs/contribution/architecture/16-metadata.md`. A thin consumer over the normalized model that emits the
**full attributed DTO graph**.

**Landed.** The extract walk was refactored to a shared normalized `WireModel` (`BuildModel`). At the time
two consumers hung off it: `EmitLenswire` (consumed by the Lens, since retired along with it) and
`WireMapEmitter.EmitJson` → `inutil.wiremap.json` (byte-identical — guarded by `MetadataEmitTests`), the
full DTO graph (every serialized type; every member with wire name defaulting to the member name,
converter+kind, persisted, nullability). `WireMapEmitter.EmitJson` is now the sole emitter; `Extract`
stamps its marker. Chosen via a design decision: **JSON** (tooling contract) + **all members** (the
complete wire shape, not just annotated fields).

**Proven:** offline over a synthetic module + the **real ToyGame v31** build (`inutil-metadata-tests`),
which is **now in the `check` gate** — closing the gating gap that let §1's stale test rot. The wire-map
is an author-time artifact with no runtime consumer, so the honest gate is offline-against-the-real-build,
not an in-game battery case (a game boot wouldn't exercise it). Real artifact verified:
`ToyGame.WireProfile` → `Gold`/`Handle`(Nickname)/`Side`(faction, enum)/`_level`(persisted).

**Next (earned third, §6.3):** version diff — now cheap, `WireModel` is the shared model to diff.

---

## 3. MelonLoader battery parity — validation debt  ⚠️ likely closed (verify)

**What / where:** `docs/reference/limits.md` P4. **Update (2026-07-10):** `docs/reference/limits.md` now
records **both loaders at full parity** ("under BepInEx and MelonLoader identically", 77/77 core / 91/91
with the REPL cases) — the P2 and REPL work that landed after this entry drove MelonLoader past the old
smoke-only baseline (3 cases: `plumbing.alive` / `il2cpp.runtime` / `canary.fail`). The
`Inutil.Battery.MelonLoader` shim and `validate.sh melon` path exist and are exercised. Treat this as
**closed pending a fresh `validate.sh melon` full run** that re-confirms parity and records any
Melon-specific skips with a reason, not silent.

**Why it matters:** v1 shipped the full demo suite validated under **both** loaders; v2 has not
re-closed that. FrameDriver unification (`docs/contribution/architecture/19-loaders.md`) means the loader-divergence bug
class v1 hit is already designed out — so this is *validation* debt, not an architecture gap. Cheapest
of the four; closes a real v1→v2 regression.

**Acceptance:** the full battery runs GREEN under MelonLoader at (or near) BepInEx parity; any Melon-
specific skips are recorded with a reason, not silent.

---

## 4. Fields / Introspect / Invoke — modder-facing runtime APIs

> **Reframed — see [`docs/contribution/architecture/17-reach-faces.md`](../contribution/architecture/17-reach-faces.md).** The shape below ("re-port the three v1 *modder-facing* APIs")
> is the one to *not* re-port verbatim: `docs/contribution/architecture/17-reach-faces.md` argues these are the *substrate* the seamless experience
> should stand on, with the raw by-name surface exposed only where a typed face is structurally impossible.
> Read docs/contribution/architecture/17-reach-faces.md before starting.
>
> **✅ P2 COMPLETE (2026-07-09), both loaders GREEN (86/86).** §7.1 the fault-safe scope — `Inutil.Safe`
> (`Safe.TryInvoke` + `Safe.Run(delegate)`) + `InvokeResult`; §7.2 the `Fields` by-name substrate, at the
> time paired with a GENERATED typed live-object accessor (`Inutil.Lenses.<T>Live`, the `LensGenerator`
> twin over Fields), since retired along with the Lens; §7.3 the raw by-name / discovery escape hatch —
> `Inutil.Invoke` (guarded via Safe), `Inutil.Probe`, `Inutil.Introspect` — for the irreducible §5 cases
> (erased handle / unknown type / REPL). The whole `Fields`/`Introspect`/`Invoke` row is DONE and remains
> the by-name substrate; see `docs/contribution/architecture/17-reach-faces.md`.

**What / where:** `docs/reference/limits.md` P2. The v1 modder-facing surface not yet re-ported:
- `Fields` — field get/set by name (`GetValue<T>`/`SetValue<T>`, `GetNullable`, `GetString`, …), the
  deliberate silent-probe convention (miss → false/default; `docs/contribution/architecture/17-reach-faces.md`).
- `Introspect` — `Dump`/`FieldList`/`Methods`/`Props`/`TypeInfo` reflection dumping.
- `Invoke` — general game-method call with an `InvokeResult`, the **managed face of the fault-safe
  probe** (hardware-fault code/addr caught, not just managed exceptions; `docs/contribution/architecture/13-hook-engine.md`).

**Why it matters:** the biggest ergonomic surface still missing. The native fault-safe **substrate
already exists** (the interceptor is present; only internal `ValueTypeBridge.InvokeUnboxed` uses it) —
so this is building the managed API over an existing substrate, not new native work.

**Acceptance:** a mod can get/set a game field by name, dump a type's members, and invoke a game method
with a fault-safe result — each covered by an in-game battery case.

---

## 5. Lower / later — not lost, just not next

Named here so they do not fall out of the roadmap:

- **Version diff** (`docs/contribution/architecture/16-metadata.md`) — the third earned pillar consumer; ingest two
  normalized models, diff types/members/attributes, emit the wire-contract delta. Attacks the re-port
  tax modders pay every game update. Earned *after* the wire-map (§2).
- **Interactive REPL** (`docs/reference/limits.md` P1 / `docs/contribution/architecture/18-repl.md`) — ✅ DONE (2026-07-10). `Inutil.Repl`:
  `ReplSession.Eval(string)` (Roslyn scripting, state chaining) + the §7.12 deadlock guard (synchronous Eval
  fails loud, never hangs, when a submission awaits a main-thread primitive — option (a), decided before the
  transport) + `ReplServer` (loopback TCP: telnet in, each line dispatched to the main thread via Post). Proven
  offline (eval/chaining/error/guard/socket round-trip) + in-game (`repl.eval.live-hook`, both loaders 91/91:
  a live submission registers a hook on Player.GetHealth() that fires + is removable).
- **`Discover` fallback method-matchers** (`docs/reference/limits.md` Gap 1) — ✅ DONE (2026-07-10). The matcher was extracted
  to a pure, offline-testable `HookMatch` (`MatchExact → MatchWidenedContainer → MatchInterfaceImpl →
  MatchGenericInference`). Landed (offline red→green + in-game, both loaders 90/90): the **fail-loud diagnostic**
  (an intended-but-mis-spelled hook warns, a private helper stays silent), **Tier 1 container interface widening**
  (`IReadOnlyList<T>`/`IEnumerable<T>` over the proxy's `List<T>` — `modhost.hook.container-widen`), **Tier 2
  explicit interface impl** (plain `Tick()` → mangled `ToyGame_ITicker_Tick`, + lifecycle-method exclusion —
  `modhost.hook.interface-impl`), and **Tier 3 generic-method instantiation inference** (`int Echo(int)` →
  `Echo<int>`, both value-type and reference-type `__Canon` — `modhost.hook.generic-inference[-reftype]`).
  raw-proxy (non-public names) turned out to be Tier 0 already (name+signature search is `Public|NonPublic`),
  locked by an offline test. The only Tier-3 caveat is the universal inlining boundary (a JIT-inlined call site
  has nothing to detour — true of every hook). Nothing in Discover is silent.
- **Interop-patch flip scope holes** (`docs/reference/limits.md` Gap 2) — value-Nullable-inside-a-container defers the
  whole flip; generic-method container flip is the deferred "v17" case. Both fail loud.
- **The distributable engine bundle** (`docs/reference/packaging.md`) — make inutil ship a versioned bundle so
  consumers (the OpenTarkov engine) stop rebuilding it from source. Discharges the `docs/reference/limits.md`
  "Packaging & build" gap; unblocks the engine's migration off the last pre-rewrite pin. **In progress:**
  `tools/pack.sh` produces the bundle (both loader trees + runnable patch CLI); what's left is the schema-marker
  version tie and a publish channel (`packaging.md` → "Remaining work" 4–5).
- **Genuine zero-`Il2Cpp` mod code** (`docs/reference/deferred-zero-il2cpp.md`) — de-prefixing the game's own proxies
  (`ToyGame.Player` vs `Il2CppToyGame.Player`). Explicitly out of the v2 parity milestone; recommended
  posture is Option D (do nothing) unless user feedback shows the prefix is real friction.

---

## Recommendation

**§1 (the marker)** is the strongest single next step: it is the only remaining seam where silent
corruption is possible, closing it finishes the project's deepest thesis, and the same content-
addressed mechanism discharges pillar Generalization D — dual-purpose and cheapest to build now. If
the priority is instead user-facing value or keeping the just-warm pillar context going, **§2
(wire-map)** is the alternative; **§3 (MelonLoader)** is the cheapest debt paydown. Record the pick.
