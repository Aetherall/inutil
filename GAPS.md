# GAPS.md — inutil defects surfaced by its first real consumer

*Found by reading OpenTarkov's `offline/` (124 files, ~16.5k lines) against a live, patched EFT interop and probing
each claim with the compiler. The consumer-facing half — what to write instead — is [GUIDANCE.md](./GUIDANCE.md).*

Each entry states **what was observed**, **how it was observed**, and **what was inferred versus read**. The
distinction earned its place here: two entries in the first draft of this file were wrong, both because a
plausible-looking signature dump or summary line was trusted over the compiler. See "How the wrong answers happened"
at the end — the failure modes are reusable.

**Status:** G3, G4 and G10 are closed and proven (G4 in-game under both loaders). G2 was mis-stated and is restated
below as a design question needing a decision before any code. Remaining: **G11** (a container that compiles and
throws — the one that makes the compiler a false oracle), G5's wiremap onboarding gap for CONSUMERS (G9 fixed
inutil's own harness ordering, not a consumer's pipeline), then the ergonomic items (G6-G8).

**G10 and G11 were found by ACTING on [GUIDANCE.md](./GUIDANCE.md), not by reading more code** — G10 by running §0
(re-patch) on the consumer's real overlay, G11 by taking §4's advice and then probing it in a booted game. Both were
invisible to the pass that produced this file, and for the same reason each time: a step nobody had executed
end-to-end.

---

## G3 — `surface-query --methods` cannot distinguish flipped from unflipped — **CLOSED**

**Severity: high.** It is the tool authors are told to consult, and it silently erases the one distinction that
decides whether natural typing works.

`managed/src/Inutil.Check.Cli/Naming.cs:14-38` — `CleanTypeName` maps `Il2CppSystem.X` → `System.X`
(`MapBcl`, line 37) and renders `Il2CppReferenceArray<T>` / `Il2CppStructArray<T>` / **`Il2CppArrayBase<T>`** as
`T[]` (`IsIl2CppArrayWrapper`, line 31). So:

| Real Cecil type | What the tool prints | What C# accepts |
|---|---|---|
| `System.Nullable<EFT.MongoID>` (flipped) | `System.Nullable<EFT.MongoID>` | `EFT.MongoID` ✅ |
| `Il2CppSystem.Nullable<EFT.MongoID>` (unflipped) | `System.Nullable<EFT.MongoID>` | `EFT.MongoID` ❌ |
| `EFT.Hideout.ScavCaseScheme[]` (flipped) | `ScavCaseScheme[]` | `Array.Empty<…>()` ✅ |
| `Il2CppArrayBase<TraderDialogTemplate>` (unflipped) | `TraderDialogTemplate[]` | `Array.Empty<…>()` ❌ |

**How found:** the dump said `ItemTemplate.get_ParentId() : System.Nullable<EFT.MongoID>`; a probe mod assigning
`new EFT.MongoID(x)` failed with `CS0029 … to 'Il2CppSystem.Nullable<EFT.MongoID>'`. Same again for
`TraderDialogsDTO.Elements`.

The header (`Naming.cs:1-4`) explains why it is like this, and is not wrong about its own history: it was ported
verbatim from the codegen era, where the display name had to match `il2cpp_type_get_name` output so the reverse
index would key correctly. That is a coherent purpose. It is simply no longer the *only* purpose — the same output
is now the mod author's spelling oracle, and the two requirements are in direct conflict.

**FIXED.** Printing raw Cecil names everywhere would have traded one wrong answer for another — the index still
needs its il2cpp-shaped key — so the renderings are split rather than replaced: `CleanTypeName` stays the index key,
a new `AuthorTypeName` renders what C# must literally be written, and every author-facing signature (params,
returns, properties, fields, ctors, in `methods`/`query`/`dump`) uses the latter. Any signature still wearing an
il2cpp spelling is tagged `[raw il2cpp]`, with a legend separating a stale patch from a by-design refusal
(`IReadOnlyList<T>`) from a real gap.

Guarded by `SurfaceRenderingTests` over the real pristine proxies, phrased against the fact ("the author rendering
never equals the index rendering for an il2cpp type") rather than against the two families that caused the
incident. Non-vacuity is asserted first: collapse the two renderings and nothing classifies as raw, which is where
a regression lands — verified by sabotage.

**Docs follow-up (open):** `docs/guide/04-escape-hatches.md` points authors at `surface-query --methods` for
backing-field spellings. That advice was always sound for *names*; it can now safely be extended to type spellings,
and the guide should say so.

---

## G2 — arrays of an OPEN generic parameter are unreachable (~1,100 members)

**Restated.** The first draft called this "an unhandled third array spelling" and implied a one-line fix in
`ContainerFlip`. That was wrong about what ``Il2CppArrayBase`1`` *is*.

**Measured.** Across the whole EFT interop, ``Il2CppArrayBase`1`` appears **1,178 times and not once with a closed
argument** — every instantiation's argument is a generic *parameter*: `T`, `TKey`, `TValue`, `TElement`, `TSource`,
`TResult`, `K`, `V`, … So this is not a wrapper Il2CppInterop uses interchangeably with the concrete two; it is what
it must emit for `T[]` when `T` is open, because it cannot choose between ``Il2CppReferenceArray`1`` and
``Il2CppStructArray`1`` without knowing whether `T` is a value type.

So this is the **open-generic element case**, which every other family in the patch already declines by design
("open-generic element `<T>` — not a concrete type to flip"), seen from the consumer's side rather than the
rewriter's. Adding ``Il2CppArrayBase`1`` to `ContainerFlip`'s wrapper list would be wrong on its own: the target
type `!T[]` is expressible in IL, but the spliced runtime bridge would have to pick the concrete wrapper per
instantiation.

**Consumer impact is real even so.** `EFT.Dialogs.TraderDialogsDTO.Elements` — inherited from
``EFT.BackendArrayDto`1`` — cannot take an `Array.Empty<TraderDialogTemplate>()`, and the closed type has a
perfectly concrete `T`. The naturalization is blocked at the *declaration*, which is open, even though every use is
closed.

**This is a design question, not a fix**, and it is not small:

- **(a) Do nothing.** Consumers spell the wrapper at ~1,100 members. Cheapest; the status quo.
- **(b) Flip the open declaration to `!T[]`** and add a bridge generic in `T` that selects the concrete wrapper at
  runtime (`typeof(T).IsValueType`). Feasible — but it puts an open-generic member through the rewriter for the
  first time, which the planner's deferral machinery currently refuses wholesale, so it is a change to that policy
  and not just to `ContainerFlip`.
- **(c) Flip only where a closed use exists** — i.e. naturalize per-instantiation rather than at the declaration.
  Almost certainly not expressible: the member has one declaration, and C# binds against it.

Worth deciding deliberately. Nothing here should be implemented before (b)'s effect on the planner is argued
through.

---

## G4 — residual holes on a real game — **CLOSED**

A clean single-pass patch of the EFT interop now reports:

```
>> residual: 104 member(s) left wearing a naturalizable il2cpp type (104 known-deferred, 0 unexplained)
== patched 19 DLL(s), 843 member(s) flipped; 124 unchanged, 0 non-.NET ==
```

Was 34 unexplained and 783 flipped. Three reporting defects and one real gap, in that order:

**Reporting (fixed).** The first draft of this entry claimed the audit contradicted itself — 68 printed holes
against a summary of 34. It never did:

1. **Printed twice** — once by `PatchDirectory` into the log handed to it, once again by the CLI from the returned
   result. 34 holes, 68 lines. One emitter now.
2. **Not identified by signature** — the member string was `Type::Method(paramName)`, so three distinct overloads of
   `Sirenix … DeserializeValue` rendered identically, indistinguishable from one hole reported three times. Keyed on
   the full parameter list now.
3. **Silent on every in-game path** — the report only goes to a `log`, and *both* in-game callers pass `null`, so a
   boot that left members raw said nothing at all. This is how the consumer's overlay came to carry holes nobody
   knew about. Both callers now log the count and name `inutil-interoppatch` for the list.

**The real gap (fixed): genericity was gated on the wrong unit.** All three candidacy gates read
`!m.HasGenericParameters`, excluding a generic *method* outright — where the actual condition belongs to the *type
being flipped*. `AddEffect<TEffect>(EBodyPart, Nullable<float> delayTime, …, Action<TEffect> initCallback)` has four
params whose natural type is plainly `float?` and one that genuinely depends on `TEffect`; excluding the method left
all five raw. Worse, it left them **invisible rather than deferred** — a non-candidate produces no flip, no defer,
no log line — which is why they surfaced only through the audit. 23 of the 34 were this shape; the last 4 were
`CollisionWithGeneratedSibling` defers the audit's hand-kept reason list did not know about, now resolved by asking
`ParamFamily`'s own collision predicate rather than re-deriving one.

**Verification status — proven in-game, both loaders.** `Game::Infuse<T>(float?, float?, Action<T>, T) : T` and
`Game::Ledger<T>(T) : List<int>` reproduce the measured EFT shape member-for-member, with the `Player`
instantiation rooted in `Bootstrap.Exercise` so IL2CPP keeps a real methodPointer. Both directions are asserted on
one method — too timid (closed params left raw) and too eager (touching `Action<T>` or the `T` return) are both
regressions. `bepinex-validate` GREEN 97, `melon-validate` GREEN 97, with the decisive case running in a booted
IL2CPP game under each:

```
Infuse<Player>(1f, 2f, null, player) -> game computed LastInfuse=12
Ledger<Player>                       -> natural List<int> [1,2,7]
```

so the spliced entry dematerialization and the return tail-swap both execute correctly inside a generic method
body.

**Still open:** the coarse `virtual` arm (`ResidualAudit.cs:110-117`), which cannot distinguish a legitimate
slot-root defer from a real hole. With unexplained now at zero, the 100 known-deferred — dominated by that arm — are
what is left to tighten.

---

## G5 — the metadata pillar is never wired into a consumer's pipeline

Not "silently inert" — an earlier draft of this entry said the degraded paths fail quietly, and that was wrong in
both directions. Nothing here is silent. The gap is that a whole pillar is simply switched off for the only real
consumer, and nothing in the pipeline ever turns it on.

**What is missing.** `inutil-metadata-extract` recovers the authored JSON wire names that the IL2CPP proxy strips
(Cpp2IL over `GameAssembly.dll` + `global-metadata.dat`) into `inutil.wiremap.json`; `WireAttributeRewriter` then
re-attaches them as `[JsonPropertyName]` during the patch. On the consumer's overlay the patcher reports
`no usable wiremap … — skipped (proxies keep member-name serialization)`, so **zero** attributes are stamped and
every EFT type has zero recovered wire members.

The cause is in the consumer's pipeline, and it is a single line: `dev-attach.sh:56` stages exactly two of the
bundle's three CLIs —

```sh
cp -a "$bundle/tools/inutil-interoppatch" "$bundle/tools/inutil-check" "$tools/"
```

`inutil-metadata-extract` is never copied and never run; the string "wiremap" does not appear anywhere in that
repo's scripts or CI.

**What that costs, precisely — both verified against the code, not inferred:**

- **The checked object-literal API is UNAVAILABLE, loudly.** `Json.ToNode` throws on a type with no recovered
  members (`Json.cs:94-99`), naming both causes and telling the caller to use a JSON string instead. So
  `Json.To<T>(new { … })` — the API [guide 5](./docs/guide/05-wire-json.md) recommends — cannot be used for any EFT
  type at all. Not "unchecked": refused.
- **`Wire.Serialize` degrades to the game's own serializer, correctly.** With zero recovered members every value
  takes the opaque path (`Wire.cs:84`), which delegates the subtree to `Json.From` — the game's registered
  Newtonsoft, reading the intact NATIVE attributes, so the output is fully wire-correct (`Wire.cs:125-137`). It
  throws only if no serializer is registered, which this consumer does register (`MirrorShim.cs:18`). So
  `Wire.Serialize` produces right answers here — it just has no advantage over the `Json.From` the consumer already
  calls everywhere, because the engine-side path it exists to provide never engages.

**So the failure is one of leverage, not correctness.** Two capabilities the pillar exists to deliver — checked
shape keys, and serialization that does not route through the game's Newtonsoft — are unreachable for the consumer,
and the only reason is that a tool inutil ships is not run.

**Fix direction, and it is partly ours.** The extract is optional in the bundle and the patcher's "skipped" line is
informational rather than a warning, so a consumer can complete a correct-looking install and never learn a pillar
is dark. Options: have `inutil-interoppatch --game` run the extract itself when a wiremap is absent and the game
artifacts are present (making it the default rather than a second step); or keep it separate but make the skip a
loud warning naming the CLI. The consumer-side half is adding it to `dev-attach.sh`'s copy line and its patch step.

---

## G6 — API surface consumers hand-roll

Each observed in more than one place in `offline/`:

- **`Json.Clone<T>`** — `Json.To<T>(Json.From(v).ToJsonString())` (`kernel/Serve.cs:17-18`).
- **`JsonNode` reparenting** — a string round-trip purely to detach a node from its parent, because net6's
  `System.Text.Json` has no `DeepClone` (`customization/Customization.Service.cs:15`).
- **proxy ⇄ POCO** — `Json.From(x)?.Deserialize<TPoco>(opts)` and `Json.To<TProxy>(SerializeToNode(poco, opts))`,
  both directions hand-written (`kernel/WireUpd.cs:11,14,19,27,30`).
- **null-to-empty** — `ToArray<T>(…)?.ToList() ?? new()` at ~8 sites.

None is hard; the value is that each is a place a consumer currently has to know an inutil implementation detail.

---

## G7 — `Mirror` registration is reflection boilerplate, re-resolved per call

`offline/src/kernel/MirrorShim.cs:21-43` is the whole shim: `RegisterResult` needs a `build` and a `read` lambda,
both written with raw `Type.GetConstructor` / `GetProperty` / `GetMethod` — **re-resolved on every invocation**, on
the backend result path.

inutil's own error messages (`managed/src/Inutil/Marshal/ContainerBridge.cs:472-530`) prescribe this exact shape, so
the boilerplate is by design rather than by accident. But the shim it prescribes is mechanical: the game's
`Result<T>` exposes `Value`/`Error`/`ErrorCode` and two constructors. A convention-based overload
(`Mirror.RegisterResult(typeof(Result<>))`, resolving those members once and caching the handles) would delete the
consumer's shim and the per-call reflection, keeping the explicit-lambda form for games whose `Result` does not
follow the convention.

---

## G8 — a `Task`-returning method cannot be replaced ergonomically

The most consumer-visible ergonomic limit. `Hook<T>` + `Proceed<Task>` throws
`MissingMethodException: Constructor on type 'System.Threading.Tasks.Task' not found`, so replacing such a method
means dropping to raw `Hooks.Pre`/`HookContext`.

`offline/src/clientfix/ForceLocalRaid.cs:11-17` documents the workaround well, and it is sound *for that case*: a
pre-hook that only rewrites an input argument never touches the return slot, so the original's `Task` flows back
untouched. The limit bites when a consumer needs to **transform** the result — no ergonomic path, and the raw tier
gives no help either.

Already in `docs/reference/limits.md`; recorded here because it is the limit this consumer hit most visibly, which
is a data point about priority.

---

## G9 — the first `*-validate` run after a reprovision was always RED — **CLOSED**

Found by running the long loop for G4. `setup-bepinex --force` / `setup-melon --force` regenerate `interop/` from
scratch, which also removes `inutil.wiremap.json`. `validate.sh` then ran the interop patch **before** the wire-map
extract, so the patch had no sidecar to stamp (`== stamped 0 wire attribute(s) ==`) and every `wire-shape.*` case
failed on a type carrying no recovered wire names. The extract ran moments later, so the *second* run was green
with nothing changed — the failure shape that teaches people to re-run and shrug.

Observed identically on both loaders before the fix: BepInEx RED 4, then GREEN 97; Melon RED 3, then GREEN 97.

**Fixed by ordering, guarded by an assertion.** The extract moved ahead of the patch (it reads
`GameAssembly.dll` + `global-metadata.dat`, never the proxies, so it has no dependency in the other direction), and
the patch step now asserts the fact that ordering exists to guarantee: by that point a wire map must exist, so
finding none — or stamping zero from one — fails loud, in one line, right there. A future reordering or a silently
failing extract cannot come back as several cryptic battery failures on a type that mysteriously has no wire names.

Proven on the exact scenario that was always red — a full `--force` reprovision followed by a **single** validate
run, on both loaders:

```
>> wire-attrs: 1 recovered type(s) from inutil.wiremap.json
>> wire-attrs: stamped 4 member(s) across 1 DLL(s)
GREEN — 97 passed          (bepinex-validate, and melon-validate, first run)
```

---

## G10 — the patcher stamped the TOOL HOST's BCL identity into every module it wrote — **CLOSED**

**Severity: high.** It made a freshly patched tree uncompilable-against for every offline consumer — the exact
operation GUIDANCE.md §0 tells a consumer to perform first.

**How found:** running §0 for real on the consumer's overlay. After `inutil-interoppatch`, `mod-check` over
`offline/src` went from a working tree to **5013 errors**, nearly all the same one:

```
CS1705 Assembly 'Assembly-CSharp' … uses 'System.Private.CoreLib, Version=9.0.0.0' which has a higher version
       than referenced assembly 'System.Private.CoreLib, Version=6.0.0.0'
```

The patcher already knew about this class of skew — `NormalizeCoreLibRef` (`PatchDirectory.cs`) exists to align
every `System.Private.CoreLib` reference to the module's own `System.Runtime` version, and it *ran* (it logged
`normalized …` for four DLLs). It just could not win, because it ran too early:

1. `PatchModule` rewrites, then normalizes — at this point the module is consistent.
2. `ResidualAudit.Scan` runs next, and it *plans* members (`ParamFamily.PlanMember` → `WrapHelpers`) to ask each
   family whether a member was naturalizable. Planning builds natural types, and `SysNullableOf` /
   `ContainerFlip.BclGeneric` built theirs with `module.ImportReference(typeof(System.Nullable<>))` — which
   resolves the open type from **the tool's own runtime** and adds a fresh `System.Private.CoreLib 9.0.0.0`
   row to `module.AssemblyReferences`.
3. `AtomicWrite` then persisted that row. Nothing was scoped to it — Roslyn rejects the assembly on the row's
   mere presence.

Every run re-added one (the consumer's `Assembly-CSharp` had accumulated ~26 duplicate corlib rows), and every run
re-reported `normalized`, so the log looked like the fix was working while the output never converged.

**Fixed at the source, plus a structural guard.** `BclScope` (new) is the single place a BCL open type is named:
it *builds* the reference — same namespace/name/arity, `IsValueType` carried over — scoped to the module's own
`System.Private.CoreLib` row, instead of importing the tool's. Both former import sites route through it.
Separately, `NormalizeCoreLibRef` now also runs inside `AtomicWrite`, immediately before `module.Write`, so a
future pass that re-introduces the skew after `PatchModule` cannot reach disk with it — the invariant is enforced
where the bytes leave, not where the last known offender ran.

Verified by patching a copy of the consumer's interop and reading the emitted refs: before, exactly one
`System.Private.CoreLib 9.0.0.0` row survived every run; after, none, and `mod-check` over the same 125-file
consumer went from 5013 errors to clean.

---

## G11 — a naturalized dictionary of a REF-BEARING value type compiles but throws on assignment

**Severity: high**, and worse than a plain gap: the compiler now says yes to something the runtime refuses, so
the oracle GUIDANCE.md relies on ("probe it with `inutil-check`") returns a false green for this shape.

`EFT.ProfileDescriptor.Customization` presents as a natural `Dictionary<EBodyModelPart, MongoID>` — it type-checks,
and a 125-file consumer compiles clean around it. Assigning it in a **booted game** throws:

```csharp
var pd = new EFT.ProfileDescriptor();
var d  = new Dictionary<EFT.EBodyModelPart, EFT.MongoID>();
d[EFT.EBodyModelPart.Head] = new EFT.MongoID("5cc085d214c02e000c6bea67");
pd.Customization = d;
// ArgumentException: Object contains non-primitive or non-blittable data. (Parameter 'value')
```

**How found:** taking GUIDANCE.md §4's advice (assign a complete dictionary through the setter), then — because §4
itself flags that compiling is not evidence for this one — probing it live over the slot's `/eval` endpoint. The
throw reproduces on a bare descriptor with a single entry, so it is the property path itself, not anything about
the consumer's data.

The value type is the whole story: `MongoID` is ref-bearing, so the marshaller's blittable-copy path rejects the
dictionary. inutil's own marshaller runs at **hook seams**, not on a direct proxy property call, so nothing
re-flips this one at the point of assignment. Note the asymmetry with G-`ParentId`: a ref-bearing *`Nullable`* was
made to work end-to-end; a ref-bearing dictionary *value* was not, and there is currently no signal that says so
short of running it.

Two things worth deciding: whether the container flip should refuse to naturalize a member it cannot marshal (so
the compiler goes back to being the oracle), and — either way — whether `surface-query` should mark such a member,
since today nothing distinguishes it from a container that genuinely round-trips.

The consumer keeps the by-name write (`Fields.GetObject` + `ValueTypeBridge.InvokeUnboxed("set_Item", …)`), now
with the re-probe recorded in the comment so the next reader does not re-derive it.

---

## Not a gap: ref-bearing `Nullable` (the `ParentId` case)

Recorded because the first draft of this file listed it as an open high-severity defect, with a mechanism worked out
in detail. **It is fixed.** Against a freshly patched interop, `it.ParentId = new EFT.MongoID(parent)` compiles and
`it.ParentId = null` compiles — the property naturalizes to `EFT.MongoID?`. The accessor work landed.

What is real is the *consumer-side* consequence: their overlay is patched by an older build, so they still need the
`RefToNullable` cast, and re-patching will break that one line
([GUIDANCE.md](./GUIDANCE.md) §0, §5). A stale on-disk patch is invisible from the consumer's side — worth
considering whether the marker should let a consumer detect "patched, but by an older rewriter build".

---

## How the wrong answers happened

Both retracted findings came from trusting a summary over the compiler. Worth keeping, because both traps are
built into the tooling and will catch the next person:

1. **The idempotent second run.** The patcher was run twice on the same copy. The *first* run did the work; the
   *second* printed `patched 0 DLL(s), 0 member(s) flipped` — read as "the current build has nothing more to give
   on this interop", concluded from it that the ref-bearing nullable fix had not landed. The true number was 783
   members across 19 DLLs. **Always patch a fresh copy, and compare compiler behaviour before and after — never
   read a second run's summary as a statement about the first.**
2. **The lying dump (G3).** `surface-query --methods` printed `System.Nullable<EFT.MongoID>` for an *unflipped*
   member, which read as confirmation. It took a `CS0029` to break the story.

The general shape: an artifact that renders the *intent* of a transformation is not evidence the transformation
happened. Only something that consumes the output for real — here, the C# compiler — is.

---

## Verification status

- **Compile-verified** (probe mods via `inutil-check check`, against both the consumer's overlay and a freshly
  patched copy): G2, G3, and the retracted `ParentId` entry.
- **Tool-output-verified** (single clean patch run over a copy of the consumer's interop): G4, G5.
- **Read from source**: G6, G7, G8, and the `Naming.cs` / `ContainerFlip.cs` / `ResidualAudit.cs` mechanisms cited.
- **Runtime-verified in a booted game** (the consumer's live EFT slot, via its `/eval` endpoint): **G11** — the
  throwing setter, isolated on a bare descriptor. **G10**'s fix is compile-verified (5013 errors → clean over the
  consumer's 125 files) and the patched tree boots: menu reached, the consumer's mod compiled and wired 109 hooks.
- Everything else here remains unchecked in a booted game. G11 is the reason that line used to matter more than it
  looked: the two oracles disagree, and only one of them was being consulted.
