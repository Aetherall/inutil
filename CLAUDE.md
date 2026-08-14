# CLAUDE.md — inutil

## What this is

inutil is a **game-agnostic il2cpp interop + hook engine**. Its only inputs are **il2cpp metadata and
Il2CppInterop proxies**. It ships `inutil_core.dll` (MinHook interceptor) + `Inutil.dll` (the `Hook<T>` /
`Proceed<R>` facade, the natural-typing marshaller, the `ILoad`/`IUnload`/`ITick`/`IGui` lifecycle seams,
`MainThread`, the REPL) + `Inutil.Mods.dll` (`CsModCompiler`/`CsModHost`, the no-build `.cs` loop) +
`Inutil.BepInEx.dll` (host plugin), and carries the game-free **refstubs**. Downstream projects consume it
(OpenTarkov's engine tier is the current one), but inutil **knows nothing about any specific game or
consumer** — not EFT, not SPT, not serverstub, not OpenTarkov.

Orientation — cite the file/mechanism, not memory: `docs/contribution/01-philosophy.md` (the principles +
PR checklist), `docs/contribution/02-system-map.md` (how the pieces fit), `docs/guide/` (modder how-to),
`docs/reference/` (limits, roadmap, specs).

## Prime directive: inutil is generic — keep the consumer's domain out

inutil operates on the **il2cpp / game-type contract**, never on a consumer's data shapes. A downstream
project's concepts — an SPT `RagfairConfig`, a `ProfileDescriptor`'s SPT-extra fields, a "profile.json", any
EFT/SPT/OpenTarkov type or wire assumption — must **never** leak into inutil's design, types, tests, or
reasoning. That inutil has only one consumer today does **not** make that consumer's needs the spec; the
general case is the spec.

Before adding *anything* to inutil — a type, a field, an assumption, a design, a "requirement" — run the
one-line test:

> **"What would this mean for a different il2cpp game with no SPT?"**

If it only makes sense for one game/consumer (EFT / SPT / OpenTarkov / serverstub), it does not belong in
inutil. Two corollaries, both learned the hard way:

- **Ground in the contract, not the consumer.** When you reach for "real data" to verify against, the source
  of truth is the game-type contract — the recovered il2cpp metadata (`inutil.wiremap.json`), the interop
  proxies, the game's own serializer — **not** a consumer's minted artifact (that "profile.json" is *their*
  SPT-derived output, not ground truth about anything inutil owns).
- **Genericity is machine-checked, never against a real game.** inutil is validated against the **ToyGame**
  il2cpp fixture under **both loaders** (BepInEx + MelonLoader). A capability is "done" when it's proven on
  ToyGame under both loaders — not when it works for EFT.
