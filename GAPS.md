# GAPS.md — inutil defects surfaced by its first real consumer

*Found by reading OpenTarkov's `offline/` (124 files, ~16.5k lines) against a live, patched EFT interop and probing
each claim with the compiler. The consumer-facing half — what to write instead — is [GUIDANCE.md](./GUIDANCE.md).*

Each entry states **what was observed**, **how it was observed**, and **what was inferred versus read**. The
distinction earned its place here: two entries in the first draft of this file were wrong, both because a
plausible-looking signature dump or summary line was trusted over the compiler. See "How the wrong answers happened"
at the end — the failure modes are reusable.

**Status:** G3 and G4's reporting defects are fixed. G2 was mis-stated and is restated below as a design question.
Remaining work, in order: the 34 residual holes (G4), the wiremap onboarding gap (G5), then the ergonomic items
(G6–G8). G2 needs a decision before any code.

---

## G3 — `surface-query --methods` cannot distinguish flipped from unflipped

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

## G4 — residual holes on a real game (reporting fixed; the holes remain)

A clean single-pass patch of the EFT interop reports:

```
>> residual: 134 member(s) left wearing a naturalizable il2cpp type (100 known-deferred, 34 unexplained)
== patched 19 DLL(s), 783 member(s) flipped; 124 unchanged, 0 non-.NET ==
```

alongside **694 pass-level `defer` lines** (dominated by `array method → virtual slot needs vtable lockstep` and
`container field → virtual accessor needs vtable lockstep`) and **34 printed `!! HOLE` lines**:

| Count | Printed hole reason |
|---|---|
| 23 | nullable param: NO KNOWN DEFERRAL REASON |
| 8 | container param: NO KNOWN DEFERRAL REASON |
| 2 | container return: NO KNOWN DEFERRAL REASON |
| 1 | nullable return: NO KNOWN DEFERRAL REASON |

**The reporting defects are FIXED** (commit below). The first draft of this entry claimed the audit's headline
count contradicted its own output — 68 printed holes against 34 reported. It did not; three separate reporting
bugs made it look that way, and each was worth fixing on its own:

1. **The report was printed twice** — once by `PatchDirectory` into the log handed to it, once again by the CLI
   from the returned result. 34 holes, 68 lines. Now one emitter, in the library.
2. **Holes were not identified by signature.** The member string was `Type::Method(paramName)`, so three distinct
   overloads of `Sirenix … DeserializeValue` rendered as three identical lines — indistinguishable from one hole
   reported three times, which is exactly how a report gets dismissed as noisy. Now keyed on the full parameter
   list: **34 lines, 34 distinct**, reconciling with the summary.
3. **Every in-game path was silent.** `PatchDirectory` only writes the report to a `log`, and *both* in-game
   callers (`Inutil.BepInEx.Patcher`, `Inutil.BepInEx`) pass `null` — so a boot that left members wearing a raw
   il2cpp spelling said nothing at all, and the author met them later as an unexplained compile error. This is how
   the consumer's overlay came to have holes nobody knew about. Both now log the count and point at
   `inutil-interoppatch` for the names.

**What remains is the holes themselves** — 34 members a pass should have covered and didn't, with a representative
cluster in every `EFT.HealthSystem.ActiveHealthController::AddEffect` nullable parameter. Untouched.

This also compounds with the still-outstanding coarse `virtual` arm (`ResidualAudit.cs:89-96`), which the file
already documents as unable to distinguish a legitimate slot-root defer from a real hole.

---

## G5 — no wiremap in the consumer's pipeline, so two features are silently inert

The patcher reports `no usable wiremap … — skipped (proxies keep member-name serialization)` on the consumer's
overlay. Consequences, neither of which announces itself:

- **`Wire.Serialize` writes nothing** — it is opt-in on recovered names by construction
  (`managed/src/Inutil/Wire/Wire.cs:1-5`).
- **The checked object-literal shape is unchecked** — with no recovered members there is nothing to validate keys
  against, so `Json.To<T>(new { … })` builds the object but cannot reject a typo. Documented as a limit in
  `docs/guide/05-wire-json.md`; the gap is that a consumer reaches for that API *for* the check and gets it silently
  disabled.

**Partly ours.** `inutil-metadata-extract` is optional in the bundle and this consumer never runs it — an
onboarding/packaging gap, not merely their oversight. Worth making the degraded state loud at the call site
("type X has no recovered wire members — the key check is disabled; run inutil-metadata-extract") rather than a
quiet passthrough, consistent with the fail-loud promise.

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
- **Not runtime-verified — nothing here was checked in a booted game.**
