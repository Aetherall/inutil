# 1. Philosophy — how inutil stays cohesive and human-sized

*What the project optimizes for, and why it works the way it does — the canonical principles, and the
mechanical PR checklist they build to, both live on this page. Read it before your first contribution.*

## The thesis

inutil is an interop framework doing genuinely hard things — native detours, an IL rewriter, a Win64 ABI
marshaller, two loaders — against a moving target. A system like that dies one of two ways: it grows too
large to hold in one head, or its pieces drift out of agreement until no one can safely change anything.

So the project optimizes, above feature count, for one property:

> **A single person can hold any subsystem in their head, end to end, and change it without fear.**

Three disciplines keep it there — **maximal factorization**, **real testing**, and **periodic
architecture reevaluation**. Everything below is a consequence of those three. None of it is style
preference; each rule has a specific class of bug it exists to prevent, and most were written in the blood
of a real one.

## 1. If two pieces of code must agree, make them share the object that says what they agree on

*(Spec principle P1.)* The deepest form of factorization here isn't "don't repeat code" — it's **don't
repeat a fact**. When two places must agree on something, they don't agree *by discipline* (two
implementations a human keeps in sync); they consume *one object* that states the fact.

The load-bearing example is `managed/src/Inutil.Schema/Families.cs`: every fact about a natural-typing
family — its il2cpp name, its BCL counterpart, its bridge shape and direction — lives there once. Both the
offline IL-rewrite seam and the runtime marshaller build their tables from it by lookup. Adding a family is
**one registration**, not four edits kept parallel by hope. The [contribution workflow](./architecture/10-schema.md)
makes the promise concrete: *write one `IBridge`, register one `TypeCorrespondence`, nothing else changes.*

The failure mode this kills: N implementations that "happen to agree" until, one refactor later, they
don't — silently.

## 2. Factor on the third repetition, not the first

*(Spec principle P2.)* "Maximally factorized" does **not** mean "abstract early." Speculative abstraction
is its own entropy — structure the codebase hasn't earned, that everyone has to route around. The rule is
the opposite: generalize when the code has **proven three times over** that it needs to, and then *finish*
the generalization rather than leaving a fourth site to hand-roll.

inutil's own history is the evidence: shared slot-resolution appeared after the *second* virtual-return
family; the element-converter was extracted once *three* container kinds needed nesting. A PR that
introduces an abstraction for its first use will get asked "what are the other two?" A PR that hand-rolls a
sibling of something used three times already will get asked "why isn't this the shared one?"

## 3. Name, don't merge

The counter-discipline to blind DRY, and the judgment call that keeps factorization honest: **two things
with similar code but different contracts stay distinct, named types.** Merging them to save lines couples
two contracts that will need to diverge, and re-teaches the next reader a false equivalence.

The three `.cs` mod lifecycles — `Coremods` (load-once, permanent), `ModLibs` (shared source, emitted to
disk), `CsModHost` (hot-reload, collectible) — share a compiler and `Mods.Discover`, yet are three named
types, because *timing and lifetime* are different contracts. The heuristic:

- **Same knowledge** in two places → one object (rule 1: merge the fact).
- **Same shape, different contract** → distinct names (this rule: don't merge the code).

Getting this wrong in either direction is the mistake. Rule 1 without rule 3 collapses things that should
stay apart; rule 3 without rule 1 leaves facts duplicated.

## 4. Silence is a design decision, not a default

*(Spec principle P4.)* Every failure path is *deliberately* loud or *deliberately* silent — never silent
by accident. A shape the engine can't prove safe **fails loud**: a precise `NotSupportedException` at the
seam, a startup warning, a matcher diagnostic naming the real overloads. It never mis-marshals quietly.

Silence is reserved for where it's *correct*: a by-name probing primitive (`Fields.GetInt` → `default` on a
miss) is silent because probing-for-maybe-absent is its job; the API layered over it that means "the modder
asked for *this* field" throws. Both are right; the split is the point.

This isn't only a runtime-safety property — it's what makes **incremental refactoring safe**. Because an
unfinished path *shouts* instead of corrupting, you can land a generalization in stages: the wired sites
work, the unwired ones throw "not built yet." The moment a deferral goes *silent*, that safety net is gone
and staged work becomes a minefield — which is exactly the regression a course-correction (C5)
was written to reverse.

## 5. A validated claim is a machine-checked claim

*(Spec principle P5.)* "Works in-game," "both loaders green," "no hook threw" mean nothing as a sentence a
human typed after watching a log scroll by. They mean something only as the **output of an aggregator that
fails the build on a red verdict**. Real testing, at two tiers:

- **Offline unit tests** for pure logic — e.g. `HookMatch` is deliberately split out of the install path so
  its decision logic is testable over plain `System.Type`, no game boot required.
- **In-game validation** against a *real* IL2CPP build (the ToyGame fixture) under **both** loaders at
  parity — the battery. A capability is "done" when a case proves it in a live frame, not when a doc claims
  it.

A corollary runs through every doc in this repo, and should run through your PRs: **cite the file or
mechanism, not intuition.** State trackers distinguish "verified in the tree" from "asserted by a doc" on
purpose. A claim you can't point at isn't one yet. And a PR without the
mandatory test template does not merge — coverage is part of the change, not a follow-up.

## 6. Enforce the invariant, not its last instance

The hardest-won rule, learned from a real slip (course-correction C5).
When you build a check, guardrail, or test to protect an invariant, protect **the invariant itself** — not
the specific string, file, or shape that most recently broke it.

A guardrail that greps for the exact symptom is a *regression test*: it catches a re-run of the identical
bug and is blind to the same fact in a new spelling (`typeof(X)` where you grepped `"X"`), a new call site,
or a **new seam** added after you wrote the check. Phrase the check against the fact:

- not "does `ContainerFlip.cs` mention `Il2CppSystem.List`" → but **"does any family fact have more than
  one implementation?"**
- not "is this one site fixed" → but **"does every value-write site either route through the shared
  primitive or throw?"**

Two corollaries, both paid for in real bugs:

1. **Building the shared implementation does not wire its consumers.** Routing every site through the one
   primitive is a *separate* obligation that must itself be enforced — or it silently stops the moment one
   demonstrator goes green while three other sites still hand-roll it.
2. **Where a check structurally can't see a copy** (a compiler-checked `typeof` a text grep can't match),
   *tie* the copies together with a test rather than *excluding* the one you can't see. An exclusion is a
   blind spot with a comment on it.

"Done," for an invariant, is *every site it governs* — each one failing loud or routing through the single
implementation — not one case passing.

## 7. Architecture is reevaluated, not frozen

The system is periodically **re-derived**, not merely extended. v2 itself is a ground-up rewrite motivated
by a 154-agent adversarial review of v1 (49 confirmed bugs); a mid-flight course-correction stopped a
convergence from drifting before parity; [the reach-faces reframe](./architecture/17-reach-faces.md) is a
deliberate *reframe* of an API surface from v1's shape into a better one. Re-deriving the right shape is treated as first-order work,
not overhead.

The consequence for a contributor: **discovering that the current shape is wrong is part of the job, not a
detour from it.** A PR that says "adding this cleanly requires reshaping that" is welcome — surfacing the
tension beats hiding a feature inside a shape that can't hold it. Because *the code is what the next
contributor learns from*, an addition that fits badly doesn't just cost its own lines — it teaches the next
person the wrong pattern, and the entropy compounds.

## What this means when you open a PR

Before requesting review, read your own change through these five questions:

1. **Does it add a second implementation of something that already has one?** (rules 1, 3) — share the
   fact, or justify why the contract genuinely differs.
2. **Is every new failure path deliberately loud or deliberately silent?** (rule 4) — no accidental
   deferral that corrupts.
3. **Does it route through the shared engine, or hand-roll a sibling of something that exists?** (rules 1,
   2) — reuse, or show the third repetition that earns a new abstraction.
4. **Does it carry its tests — offline logic *and* an in-game case where the seam needs a live frame?**
   (rule 5) — a claim without a machine check isn't done.
5. **If it adds a guardrail, does that guardrail check the invariant or just the instance that broke?**
   (rule 6) — and does it wire *every* site, not just the demonstrator?

Where the open work is — the holes worth closing, each a well-scoped contribution — is mapped in
[reference/limits.md](../reference/limits.md). How the system fits together is the [system map](./02-system-map.md).
The user-facing side of "fail loud, never silent," so you can feel what you're protecting, is
[guide/00-orient.md](../guide/00-orient.md).
