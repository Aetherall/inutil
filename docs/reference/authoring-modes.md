# Authoring modes — no-build `.cs` vs compiled `.csproj`

*Reference. See also [1. Setup](../guide/01-setup-first-mod.md) and [7. Full leverage](../guide/07-full-leverage.md).*

There are two ways to author an inutil mod. Most work happens in the first; you reach for the second only
when you need a stable shippable artifact, IDE tooling, or NuGet references.

## Mode A — no-build `.cs` (the default)

Drop `.cs` files into a watched folder; inutil compiles them **in-process** and drives them. No `.csproj`,
no build step, hot-reload on save. This is the whole of [chapter 1](../guide/01-setup-first-mod.md).

It comes in **three named lifecycles**, each its own folder, loaded in this order at boot:

| Folder | Lifecycle | Loads | Hot-reloads? | Use it for |
|---|---|---|---|---|
| `inutil-libs/<name>/*.cs` | **shared library** (`ModLibs`) | at boot, emitted to `inutil-libs/<name>.dll`, into the host context | rebuilt when source changes | code shared across mods — a mod references it by name, one type identity |
| `inutil-coremods/<name>/*.cs` | **load-once, permanent** (`Coremods`) | synchronously, **before the menu**, into a rooted context | no | boot-time fixes whose detour must be live before menu load; a dev REPL |
| `inutil-mods/<name>/*.cs` | **hot-reload** (`CsModHost`) | polled after boot, per-mod collectible context | **yes** (~1s poll + 250ms debounce) | everyday mod development |

Load order matters: **libs → coremods → mods**, so a coremod or mod can reference a library, and a
coremod's patch is live before the hot-reload loop starts polling. All three use the same in-process
compiler and the same `Mods.Discover` (so `Hook<T>` + `ILoad`/`ITick`/`IGui`/`IUnload` work identically in
each). The distinction is **timing and lifetime**, not capability:

- A **coremod** doesn't hot-reload and never tears down — right for something that must exist for the whole
  session.
- A **hot-reload mod** is torn down and replaced on every save (its `OnUnload` fires) — right for iterating.
- A **library** is emitted to disk so both the in-process compiler *and* an offline IDE can reference it.

Deploy roots differ by loader: under BepInEx these sit at `<game>/BepInEx/<folder>`; under MelonLoader at
`<game>/MelonLoader/<folder>`.

## Mode B — compiled `.csproj`

You build the mod offline with the normal .NET toolchain, producing a DLL. Reach for this when Mode A
can't do the job:

- **You want a stable, versioned release artifact**, IDE tooling, or to reference NuGet packages the
  in-process compiler's ref-dir scan won't resolve.

### How a compiled mod loads

inutil has **no drop-folder that ingests a pre-built DLL** — the watched folders above all compile
*source*. So a compiled mod is wired in through a loader plugin entry, exactly the way inutil's own hosts
wire in `.cs` mods:

```csharp
// Your mod is its own BepInEx plugin (or MelonLoader MelonMod). In its entry point, hand your assembly
// to inutil's discovery — the same call CsModHost/Coremods make for source mods:
public override void Load()
{
    int wired = Inutil.Mods.Discover(System.Reflection.Assembly.GetExecutingAssembly());
    // now your Hook<T> classes are installed and your ILoad/ITick lifecycles are driven by FrameDriver
}
```

`Mods.Discover(assembly)` and `Mods.Add(instance)` are the public registration surface.

> A first-class "drop a built mod DLL here and inutil discovers it" loader is not in the current tree —
> compiled mods register through a plugin entry as above. If that changes, this page will say so.

## Which to use

| If you… | Use |
|---|---|
| are iterating on hook/game logic | **Mode A**, `inutil-mods/` (hot-reload) |
| have code several mods share | **Mode A**, `inutil-libs/` |
| need a patch live before the menu | **Mode A**, `inutil-coremods/` |
| want a versioned release DLL / IDE tooling / NuGet refs | **Mode B** |

The two modes aren't exclusive — a common shape is a compiled library (Mode B) referenced by
hot-reloading `.cs` mods (Mode A) during development.
