# 2. The system map — how the parts fit

*Read after [1. Philosophy](./01-philosophy.md), before any component page. This is the orientation: what
the modules are, how they depend on each other, and the one structural idea the whole system turns on.*

## What the system is

inutil turns "mod an IL2CPP game" into "write plain C#." It does three things — **hook** game methods,
let you write against **natural BCL types** instead of `Il2CppSystem.*` wrappers, and **host** no-build
`.cs` mods — over two loaders (BepInEx, MelonLoader) on one shared core. Everything below is in service of
those three.

> **State:** v2 is the shipped engine. The ground-up rewrite is complete, cut over, and validated in-game
> under both loaders at full parity. (If you find a doc describing the rewrite/migration in future tense,
> that's history; it's done.)

## The one idea everything turns on

If you internalize one thing before reading the component pages, make it this:

> **Two seams must agree on "how does il2cpp type X correspond to BCL type Y" — the offline IL rewriter
> and the runtime marshaller. They don't agree by discipline. They consume one object that states the
> correspondence: `Inutil.Schema`. Neither seam owns the notion of a type family; both read it.**

That's why `Inutil.Schema` sits at the bottom of the diagram with arrows *out* to everything: it's the
single source of truth, and the reason the two seams can't drift apart. Adding a natural-typing family is
one registration there, and both seams pick it up. Hold onto this; half the design decisions in the tree
are downstream of it.

## The dependency map

```
                          ┌─────────────────────────────────────────────┐
                          │  Inutil.Schema  — the correspondence registry │  ← the heart (10)
                          │  TypeCorrespondence · Conv tree · planner ·   │
                          │  content markers                              │
                          └───────┬───────────────────────────┬──────────┘
              consumes it ┌───────▼────────┐         ┌─────────▼──────────┐ consumes it
                          │ Inutil.Interop │         │  Inutil.Marshal    │
                          │ Patch — IL     │         │  runtime           │  (12)
                          │ rewrite (11)   │         │  marshaller (12)   │
                          │ OFFLINE, Cecil │         │  at the boundary   │
                          └────────────────┘         └─────────┬──────────┘
                                                               │ marshals args/returns for
                          ┌────────────────────────────────────▼──────────┐
                          │  Inutil.Hooks (managed)  →  inutil_core (native)│  ← interception (13)
                          │  Pre/Post/PreNative · HookContext · ABI plan    │
                          │  MinHook detours + SEH fault guard              │
                          └───────┬─────────────────────────────┬──────────┘
             ergonomics ┌─────────▼──────────┐        reach ┌────▼───────────────────┐
                        │ Hook<T> · HookMatch │             │ Safe · Invoke · Probe · │  (17)
                        │ Mods discovery (14) │             │ Introspect · Fields     │
                        └─────────┬───────────┘             └─────────────────────────┘
                                  │ driven by
                        ┌─────────▼────────────────────────────────────┐
                        │  Inutil.Mods host + Inutil.Host integration    │  ← execution (15)
                        │  CsModHost/Coremods/ModLibs · compiler ·       │
                        │  FrameDriver · MainThread · Coroutines         │
                        └─────────┬──────────────────────────┬──────────┘
              wire layer ┌────────▼─────────┐          repl ┌▼──────────────┐
                         │ Inutil.Metadata   │               │ Inutil.Repl   │  (18)
                         │ (16)              │               │               │
                         └──────────────────┘               └───────────────┘

  ┌───────────────────────────────────────────────────────────────────────────┐
  │ Inutil.Host.{BepInEx,MelonLoader} — the ONLY per-loader code (19)          │
  │ Testing: offline unit projects + the in-game Battery + validate.sh (20)    │
  └───────────────────────────────────────────────────────────────────────────┘
```

## The component index

Read them in this order — it's dependency order, bottom-up. Each page follows the same seven-section
template (Job · In the tree · Design · Invariants · Limits/defers/TODOs · Tests · Why).

| # | Page | Job | In the tree |
|---|---|---|---|
| 10 | [schema](./architecture/10-schema.md) | the correspondence registry — the single source of truth | `managed/src/Inutil.Schema` |
| 11 | [interop-patch](./architecture/11-interop-patch.md) | the offline IL-rewrite seam (proxies → natural types) | `managed/src/Inutil.InteropPatch[.Cli]` |
| 12 | [marshal](./architecture/12-marshal.md) | the runtime marshaller seam (the boundary conversion) | `managed/src/Inutil/Marshal` |
| 13 | [hook-engine](./architecture/13-hook-engine.md) | managed hook engine + native interceptor & fault guard | `managed/src/Inutil/Hooks`, `native/core` |
| 14 | [hook-ergonomics](./architecture/14-hook-ergonomics.md) | `Hook<T>`, the `HookMatch` tiers, mod discovery | `managed/src/Inutil/Mods` |
| 15 | [mod-host](./architecture/15-mod-host.md) | the three `.cs` lifecycles, compiler, loader integration | `managed/src/Inutil.Mods`, `managed/src/Inutil/Host` |
| 16 | [metadata](./architecture/16-metadata.md) | wire-name recovery for member-name remapping at patch time | `managed/src/Inutil.Metadata[.Cli]` |
| 17 | [reach-faces](./architecture/17-reach-faces.md) | by-name / fault-safe escape hatches | `managed/src/Inutil/{Safe,Invoke,Introspect,Fields}` |
| 18 | [repl](./architecture/18-repl.md) | the in-game C# REPL | `managed/src/Inutil.Mods/Repl*.cs` |
| 19 | [loaders](./architecture/19-loaders.md) | the BepInEx/MelonLoader shims | `managed/src/Inutil.{BepInEx,MelonLoader}` |
| 20 | [testing](./architecture/20-testing.md) | the two test tiers + the pass/fail gate | `managed/src/*.Tests`, `managed/test/Battery`, `tools/wine` |

## How to use a component page

Each page is written to be the *complete* current picture of one part — you shouldn't need a separate
spec or gap-tracker alongside it; the architecture pages plus [reference/limits.md](../reference/limits.md)
are the whole picture. Where a page's **Limits, defers & TODOs** section
marks something roadmap, that is the honest state of the tree, not an aspiration. Where its **Why** section
names a bug-class, that's the failure the shape exists to prevent — change the shape and you re-open it, so
read it before you refactor.

The mod-*author* side of the same system (how to *use* these components, not build them) is the
[guide](../guide/00-orient.md); the user-facing limits map is [reference/limits.md](../reference/limits.md).
