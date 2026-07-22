# 0. Orient — what inutil is, and when to reach for it

*The 5-minute picture, before you install anything. Next: [1. Setup & your first mod](./01-setup-first-mod.md).*

## The one-line model

> **inutil lets you write a mod for an IL2CPP game in plain C# — hooking the game's own methods and
> passing normal .NET types across the boundary — as a `.cs` file you drop in a folder and edit live.**

The problem it exists to solve: IL2CPP games ship no managed code. A loader (BepInEx, MelonLoader) runs
Il2CppInterop, which generates *proxy* assemblies so you can call the game from C# — but those proxies
are awkward. Method arguments and returns are `Il2CppSystem.*` wrapper types, not `Task<T>`/`int?`/
`List<T>`; hooking is low-level; and there's no fast edit loop. inutil is the layer that makes modding
such a game feel like writing ordinary C#.

## The three capabilities

Everything in inutil is one of these three, or supports them.

| # | Capability | What you write | Covered in |
|---|---|---|---|
| 1 | **Hook** a game method | a class `: Hook<Game>` with a method named like the target; call `Proceed<T>()` to run the original | [2. Hooking](./02-hooking.md) |
| 2 | **Natural typing** | plain BCL types — `Task<T>`, `int?`, `List<T>`, `Action<T>` — where the raw proxy demands `Il2CppSystem.*` | [3. Natural typing](./03-natural-typing.md) |
| 3 | **Mod hosting** | a plain `.cs` file; no `.csproj`, no build — inutil compiles and hot-reloads it in-process | [1. Setup](./01-setup-first-mod.md) |

On top of those three sit the tools you reach for less often, but which unlock the hard cases:

| Face | For | Covered in |
|---|---|---|
| `Safe` | calling into the game without a crash taking down the process (catches hardware faults, not just managed exceptions) | [4. Escape hatches](./04-escape-hatches.md) |
| `Invoke` · `Probe` · `Introspect` · `Fields` | reaching a method/field **by name** when you don't have a typed proxy for it (erased handles, unknown-at-author-time types) | [4. Escape hatches](./04-escape-hatches.md) |
| the **REPL** | poking at the live game from a `telnet` prompt while it runs | [6. REPL](./06-repl.md) |

## The mental model

```
   your .cs mod  ──writes──▶  Hook<Game> / Task<T> / int? / List<T>      (plain C#)
        │                                   │
        │                          inutil marshalling seam                (BCL ⇄ il2cpp, at the boundary)
        │                                   │
   inutil host  ──compiles+loads──▶  patched Il2CppInterop proxies         (natural types, flipped in place)
        │                                   │
   native engine (inutil_core.dll) ──intercepts──▶  the real game method   (MinHook detour)
```

Two of these seams are set up **once, offline**, before the game runs:

- **The interop patch** rewrites the proxy DLLs on disk so their method signatures speak BCL types — this
  is what makes natural typing (capability 2) real. You run one CLI, `inutil-interoppatch`, after installing.
- **The wire-map extract** (optional) recovers a game's real serialized field names into a sidecar file that
  `inutil-interoppatch` reads to re-attach them to the patched proxies — one CLI, `inutil-metadata-extract`.

Everything else happens **at runtime, in-process**: your `.cs` mod is compiled, loaded into a collectible
context, its hooks installed into the native engine, and its per-frame `Tick()` driven by the loader.

## When to use inutil — and when not

| You want to… | Reach for |
|---|---|
| Change what a game method does, or observe its calls | inutil — a `Hook<Game>` (capability 1) |
| Call game methods / read game state from normal C# | inutil — natural typing (capability 2) |
| Iterate fast without a build step, or experiment live | inutil — the `.cs` mod loop + the REPL |
| Ship a plugin that only needs BepInEx's own APIs, no game-method hooking | plain BepInEx — you may not need inutil at all |
| Write against the game with the raw Il2CppInterop proxies, wrappers and all | plain Il2CppInterop — inutil is the ergonomics *over* it, not a replacement |

inutil sits **on top of** a loader and Il2CppInterop; it doesn't replace them. You still install BepInEx
or MelonLoader the normal way. inutil is what you add so the modding is pleasant.

## The fail-loud promise

One property runs through the whole framework and is worth internalizing before you start:

> **inutil never mis-marshals silently.** When it hits a type shape it can't yet bridge, or a method it
> can't match, it raises a precise exception (a `NotSupportedException` at the seam) or simply declines to
> hook — it never hands your mod a quietly-wrong value.

So when something *doesn't* work, you get told exactly where and why. The guide flags each such edge as
you reach it, and [7. Full leverage](./07-full-leverage.md) collects them into one map.

## Status you should know going in

- The engine is validated in-game under **both** BepInEx and MelonLoader, at full parity.
- This is the `v2-rewrite` branch: a few v1 capabilities aren't re-ported yet. They're listed in
  [reference/limits.md](../reference/limits.md); the guide marks anything roadmap where it comes up.
- There's **no packaged release yet** — today you build inutil from this repo and deploy the DLLs beside
  your loader. The next chapter walks that, step by step. (A packaged distribution is roadmap.)

**Next → [1. Setup & your first mod](./01-setup-first-mod.md).**
