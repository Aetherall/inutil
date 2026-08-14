# Feature request — write-through views beyond `IList<T>`, and on proxy members

**Status:** feature request / decision-menu item, for the team
**Date:** 2026-08-05
**Grounding:** a read of `Inutil.Schema/Families.cs`, `Inutil/Marshal/ContainerBridge.cs` and
`Inutil/Marshal/MutableListAdapter.cs` against `docs/guide/03-natural-typing.md`, plus one live
measurement in a downstream consumer (below — the *mechanism* is consumer-independent, the measurement
merely confirmed it).

---

## The shape of the problem

A natural-typed container crossing the boundary is **materialized**, in both directions. Reading gives a
fresh BCL container; writing builds a fresh il2cpp one. There is no shared identity between the two
sides, so **mutating what a getter handed you changes nothing the game will ever read** — and nothing
says so. No exception, no log, no diagnostic. The code type-checks, runs, and does nothing.

This is not new behaviour and it is not a bug: it is what `MaterializeDictionary` /
`DematerializeDictionary` (`ContainerBridge.cs:302`, `:317`) do by construction, and
`docs/guide/03-natural-typing.md:89` already states it for `List<T>` — *"`List<T>` hands you a snapshot
(mutating it never touches the game)"*. The engine also already has the answer for one family: `IList<T>`
is a **live write-through view** (`Families.cs:51`, `ContainerBridge.cs:281`,
`Il2CppMutableListAdapter<T>`), which copies nothing and forwards every op to the held proxy.

The request is about the two places that answer does not reach.

### Measured

Against a game type exposing a natural `Dictionary<K,V>`-typed property:

| probe | result |
|---|---|
| two consecutive reads of the property | **different instances** (`ReferenceEquals` false) |
| mutate every entry of the first read, discard it, re-read | unchanged |
| mutate the read copy, then **assign it back** through the setter | all N edits applied |
| write a scalar property directly | applied (no ceremony needed) |

So the correct idiom on a proxy member is **read → modify → assign back**, and the incorrect one — the
obvious one — is indistinguishable from working code at the call site.

---

## Gap A — the write-through family is `List`-only

`ConvKind.MutableList` is registered for exactly one correspondence: il2cpp `List\`1` ↔ `IList<>`
(`Families.cs:51`). `HashSet\`1` (`Families.cs:58`) and `Dictionary\`2` have **no view counterpart** —
they materialize into a fresh BCL container and that is the only spelling available.

So for a set or a map there is no way to say "I want the game's own one". A hook that wants to remove a
key from a game dictionary must read the copy, delete from it, and write the whole thing back — which is
not merely inconvenient: it is **semantically different**, because the write-back replaces the container
the game holds rather than editing it, and anything else holding a reference to the original keeps the
old contents.

`Il2CppMutableListAdapter<T>` is the template for closing this. An `ISet<T>` and an `IDictionary<K,V>`
adapter are the same bounded shape — hold the proxy, forward each op, convert per-op through the child
`Conv` — with the same identity round-trip on the write side
(`DematerializeMutableList`'s `IIl2CppListCarrier` check, `ContainerBridge.cs:296`).

## Gap B — a view is a hook-boundary spelling, never a proxy rewrite

`Families.cs:47` records this deliberately:

> `IsFlippableContainer` excludes `MutableList` — `IList` is a hook-boundary spelling, never a proxy
> rewrite.

That is a sound decision for what it was decided about (the interop-patch flip must stay stable), but it
has a consequence worth naming: **a property or field on a patched proxy is always the copying spelling,
for every family, `List` included.** A mod that reaches a container by hooking a method whose *parameter*
is a list can opt into a view; a mod that reads the same container off a game object cannot, ever.

Reading state off an object is the more common of the two. So in the common case, every container is
copy-only, and Gap A's "at least lists have an answer" does not apply.

Taken together the two gaps say: **on a proxy member, no container family has a write-through spelling,
and the failure mode is silent.**

---

## Options, cheapest first

**1. Document the copy on every copying row (no engine change).**
`docs/guide/03-natural-typing.md:70`'s families table carries the *(a **copy** at the boundary)* note on
the `List` row only; the `Dictionary`, `Set` and read-only rows are silent about it, and the
snapshot-vs-view paragraph below the table is written entirely in terms of `List` / `IList`. Adding the
note to every copying row, stating the read-modify-write-back idiom, and saying plainly that member
access is copy-only closes the *silent* half of the trap without touching the engine. This is the one to
do regardless of what else is chosen.

**2. Make the trap loud in a diagnostic mode.**
The read side already *mints* the managed container (`ContainerBridge.cs:302`). Minting a subclass that
warns on first mutation when it holds no carrier would turn "did nothing, said nothing" into a log line
naming the member. Opt-in only — it costs an allocation shape and a check per write, which the normal
path should not pay. This is the cheapest thing that catches the mistake at the moment it is made rather
than at the moment someone notices the game did not change.

**3. `ISet<T>` and `IDictionary<K,V>` write-through families.**
Mirrors `MutableList` exactly, including the carrier round-trip. Bounded, well-understood, and closes
Gap A. Note the dictionary adapter is the only genuinely new work: a set forwards a sequence surface much
as the list adapter does, while a dictionary is pair-shaped and needs its own enumerate/indexer forwarding
(the same distinction `MaterializeDictionary`'s own comment already draws about `EnumeratePairs`).

**4. Views on proxy members.**
The deep one, and the only one that closes Gap B. It would mean the patch flipping a member's getter to a
view type with the setter round-tripping by identity. It changes what an assignment to that property
*means*, and it reopens a decision `Families.cs:47` closed on purpose — so it wants its own design pass
rather than being folded into (3).

## What not to do

**Do not make views the default.** A view is N proxied element reads where a copy is one bulk
materialization, and `docs/guide/03-natural-typing.md:92` already makes that argument in the other
direction (*"If you only read, prefer the copy"*). The read-only spellings exist precisely so a caller can
say it is only reading. The request is for a **spelling that is available**, not for a changed default.

## Verification note

The measurement above came from a downstream consumer's game type, but nothing about the finding depends
on it: `MaterializeDictionary` constructs a new `Dictionary<,>` unconditionally, so the copy is
structural. It should reproduce on **ToyGame** with any il2cpp type exposing a `Dictionary` or `HashSet`
member — which is also what a fixture for options (2) or (3) would need, under both loaders, per
`docs/contribution/01-philosophy.md`.
