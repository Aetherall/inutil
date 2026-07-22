# 7. Full leverage — composing it, and knowing the edges

*The last rung. Previous: [6. REPL](./06-repl.md) · Reference: [authoring modes](../reference/authoring-modes.md) · [limits & roadmap](../reference/limits.md).*

You've climbed the whole ladder. This chapter ties the capabilities together, gives you the working loop
that experienced inutil modders actually use, and hands you the honest map of where the edges are — so
"full leverage" means *knowing what the tool can and can't do*, not just its happy path.

## The whole toolkit, one glance

| You want to… | Reach for | Chapter |
|---|---|---|
| Change / observe a game method | `Hook<T>` + `Proceed<R>` | [2](./02-hooking.md) |
| Pass normal C# types across the boundary | natural typing (`Task<T>`, `List<T>`, `int?`, `Action`) | [3](./03-natural-typing.md) |
| Call a method / read a field you can't name at author time | `Invoke` / `Fields` (by name) | [4](./04-escape-hatches.md) |
| Touch a suspect handle without crashing | `Safe.Run` / `Probe` | [4](./04-escape-hatches.md) |
| Experiment live, no rebuild | the REPL | [6](./06-repl.md) |
| Iterate a mod with no build step | the `.cs` hot-reload loop | [1](./01-setup-first-mod.md) |

## A worked composition

A single realistic feature — "make a boss drop double loot, and log what dropped" — touches most of the
stack:

```csharp
using Inutil;
using ToyGame;

public sealed class DoubleLoot : Hook<Boss>, ILoad
{
    public void OnLoad() => Log("DoubleLoot armed");

    // Hook (ch.2) a method whose param is a natural List<T> (ch.3 — the container flip):
    void GrantLoot(List<Item> drops)
    {
        foreach (var d in drops.ToList())          // it's a real List<T>: LINQ, indexer, ToList all work
            drops.Add(d);                          // double every drop

        // Escape hatch (ch.4): read a field we don't have a typed accessor for, fault-safely
        foreach (var d in drops)
            Log($"dropped {Fields.GetString(d, "_id")}");

        Proceed(drops);                            // run the original with the doubled list
    }
}
```

The **working loop** most modders settle into:

1. **Explore in the REPL** ([ch.6](./06-repl.md)) — `Introspect.Dump` an object, confirm a method name,
   try a hook inline until it fires.
2. **Codify as a `.cs` hook** ([ch.1](./01-setup-first-mod.md)–[2](./02-hooking.md)) — drop it in
   `inutil-mods/`, iterate with hot-reload.
3. **Reach for an escape hatch** ([ch.4](./04-escape-hatches.md)) only where a typed face doesn't exist.
4. **Graduate to a compiled mod** ([authoring modes](../reference/authoring-modes.md)) when you want a
   stable release artifact.

## The two setup steps you do *outside* the game

Everything above runs in-process, but two seams are prepared offline and are worth re-stating because
they're the ones people forget after a game update:

- `inutil-interoppatch --game <game>` — makes natural typing real (re-run after any game update
  regenerates the proxies). If you skip it, inutil warns loud at startup.
- `inutil-metadata-extract --game <game>` — recovers a game's real serialized field names into a sidecar
  that `inutil-interoppatch` re-attaches to the patched proxies (optional; most mods don't need it).

## Both loaders, one mod — almost

inutil runs at full parity under BepInEx and MelonLoader, but a mod that names game types crosses one
loader difference: the **proxy namespace**. BepInEx strips the `Il2Cpp` prefix (`ToyGame.Player`);
MelonLoader keeps it (`Il2CppToyGame.Player`). A loader-agnostic mod aliases it:

```csharp
#if MELONLOADER
using Game = Il2CppToyGame;
#else
using Game = ToyGame;
#endif
```

Deploy paths differ too (`BepInEx/plugins` vs `Mods/`, `BepInEx/inutil-mods` vs `MelonLoader/inutil-mods`)
— see [chapter 1](./01-setup-first-mod.md).

## Know the edges

This is what separates "using inutil" from "having full leverage": knowing where it stops. Every edge
**fails loud** — a precise exception or a startup warning, never a silent mis-marshal — but you should
know them before you design a mod around one:

- **Natural typing has holes** — a value-`Nullable` inside a container, generic-method container
  returns/params, and the reverse (il2cpp→BCL) delegate direction are not bridged yet. A mod that hits
  one spells the wrapper type or gets a `NotSupportedException`. ([details](../reference/limits.md))
- **`Probe` proves *plausibly* live, not use-after-free.** ([ch.4](./04-escape-hatches.md))
- **`Safe` tolerates a fault, but the runtime may be tainted after** — diagnose and restart, don't
  loop-retry. ([ch.4](./04-escape-hatches.md))
- **The REPL session isn't collectible**, and you can't `await` a main-thread primitive from a line.
  ([ch.6](./06-repl.md))
- **There's no packaged release yet** — you build from source. ([ch.1](./01-setup-first-mod.md))

The full, sourced map — what's green, what's roadmap — is [reference/limits.md](../reference/limits.md).

## Where to go next

- **Threads, frames, and lifetimes** — [8. Concurrency](./08-concurrency.md): main-thread work
  (`MainThread.Post`/`NextFrame`/`Until`), the `Self`-capture rule for async hooks, coroutines, pinning,
  and what a hot-reload reclaims for you. Read it before your first `await` in a hook.
- **Typed settings that apply live** — [9. Config](./09-config.md): `IConfigure` + `ModConfig`, one
  self-documented cfg per mod, edits re-fire `Configure` with no reload.
- **Deeper on authoring** — [authoring modes](../reference/authoring-modes.md): the `.cs` hot-reload vs
  compiled-`.csproj` fork, and the three mod lifecycles (libs / coremods / hot-reload mods).
- **How it actually works** — the [architecture reference](../contribution/02-system-map.md) (the system
  map + a page per component), written for engineers.
- **Contributing a type family** — [the contribution guide](../contribution/01-philosophy.md). Every natural-typing
  family is one registration in `Families.cs`; closing a [limits](../reference/limits.md) hole is a
  well-scoped contribution.

You now have the whole tool. Go make the game do something it wasn't supposed to.
