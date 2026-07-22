# inutil docs

**inutil** is a C#/native framework for modding IL2CPP/Unity games. It lets a mod:

- **hook** game methods (pre/post/vtable, via a native interceptor),
- write mod code in **natural C# types** (`Task<T>`, `int?`, `List<T>`, `Action<T>`) instead of
  Il2CppInterop's generated `Il2CppSystem.*` wrappers, and
- run as a **plain `.cs` file with no build step** — dropped into a folder, compiled in-process, hot-reloaded on save.

It ships over two loaders — **BepInEx** and **MelonLoader** — on one shared engine.

---

## Three doors

**→ I want to write a mod.** Start at the guide and climb. Each chapter ends at something you can run.

- [`guide/00-orient.md`](./guide/00-orient.md) — what inutil is, the three capabilities, when *not* to use it
- [`guide/01-setup-first-mod.md`](./guide/01-setup-first-mod.md) — install, patch the proxies once, drop your first `.cs` mod, watch it hot-reload
- [`guide/02-hooking.md`](./guide/02-hooking.md) — `Hook<Game>`: name a method like the target, call `Proceed<T>()`
- [`guide/03-natural-typing.md`](./guide/03-natural-typing.md) — why plain BCL types just work, and what fails loud when they can't
- [`guide/04-escape-hatches.md`](./guide/04-escape-hatches.md) — `Safe`, `Invoke`, `Probe`, `Introspect`, `Fields` for the raw cases
- [`guide/06-repl.md`](./guide/06-repl.md) — the in-game C# REPL over `telnet 127.0.0.1`
- [`guide/07-full-leverage.md`](./guide/07-full-leverage.md) — composing it all, and the honest limits map

Reference pages back the guide:

- [`reference/authoring-modes.md`](./reference/authoring-modes.md) — no-build `.cs` hot-reload vs compiled `.csproj`, and the three mod lifecycles
- [`reference/limits.md`](./reference/limits.md) — the sourced map of what's green, what fails loud, what's roadmap
- [`reference/roadmap.md`](./reference/roadmap.md) — the next-step candidates (decision menu for the next contributor)
- [`reference/packaging.md`](./reference/packaging.md) — the distributable engine bundle + the consumption contract (plan)
- [`reference/deferred-zero-il2cpp.md`](./reference/deferred-zero-il2cpp.md) — a capability the design deliberately defers, recorded on purpose

**→ I want to understand how it works.** The architecture reference — every component, and the *why*
behind each decision — is written for engineers, not mod authors. Start at the system map:

- [`contribution/02-system-map.md`](./contribution/02-system-map.md) — the topology + one page per component (`architecture/10`–`20`)

**→ I want to contribute.** Start with how the project thinks, then the mechanics:

- [`contribution/01-philosophy.md`](./contribution/01-philosophy.md) — cohesion, factorization, real testing, and the rules that keep the codebase human-sized *(read first)*
- [`contribution/02-system-map.md`](./contribution/02-system-map.md) — the component map; e.g. adding a type family starts at [`architecture/10-schema.md`](./contribution/architecture/10-schema.md)
- [`reference/limits.md`](./reference/limits.md) — what's open and worth closing

---

## Status (honest)

This branch (`v2-rewrite`) is a ground-up rewrite. The engine is complete and validated in-game under
**both loaders** — BepInEx and MelonLoader, at full parity. Everything the guide documents as *built*
is proven by the in-game battery; anything not yet re-ported from v1 is called out explicitly (see
[`reference/limits.md`](./reference/limits.md)) and marked *roadmap* where it appears.

A distinguishing property to keep in mind throughout: **inutil fails loud, never silent.** A shape the
engine can't bridge yet raises a precise exception (or simply doesn't hook) — it never mis-marshals your
data quietly. The guide points out each such edge as you reach it.
