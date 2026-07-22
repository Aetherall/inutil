# 3. Natural typing — plain C# types across the boundary

*Why chapter 2's `int Tally(int n)` worked. Previous: [2. Hooking](./02-hooking.md) · Next: [4. Escape hatches](./04-escape-hatches.md).*

## The one-line model

> **Write your mod against normal .NET types — `Task<T>`, `int?`, `List<T>`, `Action<T>` — and inutil
> makes the game's proxies speak them, so the awkward `Il2CppSystem.*` wrappers never reach your code.**

Without inutil, a game method that returns `Task<int>` shows up on the Il2CppInterop proxy as
`Il2CppSystem.Threading.Tasks.Task<Il2CppSystem.Int32>`, and you can't `await` it, LINQ over it, or hand
it a plain lambda. Natural typing removes that tax.

## Before / after

```csharp
// BEFORE — raw Il2CppInterop: wrapper types everywhere, no await, no plain lambda
Il2CppSystem.Collections.Generic.List<Il2CppSystem.Int32> loot = inventory.GetLoot();
Il2CppSystem.Threading.Tasks.Task reload = weapon.ReloadAsync();
weapon.OnFire(Il2CppSystem.Action.op_Implicit(...));   // hand-build the delegate wrapper

// AFTER — natural typing: it's just C#
List<int> loot = inventory.GetLoot();       // a real List<int> — foreach/LINQ/indexer all work
Task reload   = weapon.ReloadAsync();        // a real Task — you can await it
weapon.OnFire(() => Log("fired!"));          // pass a bare lambda straight in
```

## How it works — two seams

Natural typing is two mechanisms working together, and knowing which is which explains its limits:

1. **The IL-rewrite seam (offline).** `inutil-interoppatch` (the CLI you ran in
   [chapter 1](./01-setup-first-mod.md)) rewrites the proxy DLLs *on disk* so their method signatures
   are spelled in BCL types. This is what lets your mod **compile** against `List<int>`.
2. **The runtime marshaller (in-process).** At the hook boundary and on flipped calls, inutil converts
   between the BCL value and the il2cpp representation — building a real `List<int>` from an il2cpp list,
   or dematerializing your `List<int>` back. This is what makes it **work** at runtime.

Both are driven by **one registry** of type families, so the two seams can never disagree about what's
supported.

## What's bridged

| Family | Natural type | Direction | Notes |
|---|---|---|---|
| Task | `Task`, `Task<T>` | game → you | receive and `await` a game task |
| List | `List<T>` | both | the concrete read/write container (a **copy** at the boundary) |
| Read-only sequences | `IReadOnlyList<T>`, `IEnumerable<T>` | game → you | materialize *into* a `List<T>` |
| Mutable list view | `IList<T>` | both | a **live write-through view** of the game's own list — your `Remove`/`Add` land on the game's list, and `Proceed` forwards it by identity |
| Set | `HashSet<T>` | both | its own target, not a List re-spelling |
| Dictionary | `Dictionary<K,V>` | both | |
| Nullable | `T?` (`int?`, `Vec3?`) | both | value-layout aware |
| Tuple | `ValueTuple` (`(int, string)`) | both | arities 1–7 |
| Delegate | `Action`, `Action<…>`, `Func<…>` | **you → game** | pass a lambda into a game method param |

The **direction** matters:

- **Task, sequences** come *from* the game — you receive them naturally.
- **Delegates** go *to* the game — you pass a plain `Action`/`Func`/lambda into a game method parameter.
  There is deliberately **no reverse**: a game method that *returns* a delegate, or hands you one, stays
  wrapper-typed (see limits).
- **Containers, Nullable, Tuple** flip **both** ways — they work as parameters and as returns.

One spelling choice carries **semantics**, not just shape: `List<T>` hands you a *snapshot* (mutating it
never touches the game), while `IList<T>` hands you the *game's own list* through a live adapter — spell
`IList<T>` exactly when the method contract is "the callee mutates the pool" (a `Remove`-per-pick pool, a
list the game keeps reading). If you only read, prefer the copy: one bulk materialization beats N proxied
element reads.

Everything else — strings, blittable structs (`Vec3`), enums (`Faction`), and the game's own proxy types
— already marshals correctly through Il2CppInterop; natural typing is specifically the wrapper families
above that it *didn't* handle well.

## The safety net: patched, stale, or missing

Because compilation (seam 1) and runtime (seam 2) are separate steps, they could drift — you could
compile a mod expecting flipped proxies but deploy over un-patched ones. inutil stamps the proxy
directory with a content hash of the family registry that patched it, and checks it at startup:

- **Current** — silent, all good.
- **Missing** (never patched) or **Stale** (patched by a different inutil schema) — a **loud warning** at
  attach: `inutil: interop proxies look unpatched…`

So the classic "works on my machine, silently wrong on deploy" failure is caught, not suffered. If you
see that warning, re-run `inutil-interoppatch --game <game>`.

## Honest limits (all fail loud, never silent)

A shape natural typing doesn't flip stays **wrapper-typed**. Your mod then either spells the
`Il2CppSystem.*` type (compiles and works, just less pretty) or hits a precise `NotSupportedException` at
the marshalling seam — it never mis-marshals silently. The current holes ([reference/limits.md](../reference/limits.md), Gap 2):

- **A value-`Nullable` element inside a container** — `List<Vec3?>`, `Dictionary<…, Loadout?>` — defers
  the whole container flip (an empty il2cpp value-Nullable would NRE on unbox). *Roadmap.*
- **Generic-method container returns/params** — a generic *Task* return flips; a generic-method
  *container* return/param does not yet. *Roadmap.*
- **A managed `Task` return against an *unflipped* Task proxy** (unpatched interop, or the stale-marker
  case above) is **rejected at mod load** with a directed error — it used to be the one failure deferred
  all the way to the first live call (`MissingMethodException` mid-raid). Spell the il2cpp Task, or patch
  the interop.
- **Delegates only go one way** — you can pass a lambda *into* the game, but a method returning a delegate
  stays wrapper-typed (there's no il2cpp→BCL delegate bridge). Delegate arity is bounded (Action ≤ 8,
  Func ≤ 9); beyond that a param stays wrapper-typed.

None of these corrupt data — they either compile against the wrapper or raise at the seam. When you hit
one, the exception tells you exactly which shape and where.

> A note on where a hook fits: when you write `int Tally(int n)` (chapter 2), the *hook boundary* runs
> the same runtime marshaller — reading the frame's args into your natural types and writing your natural
> return back. So "natural typing" and "hooking" share one marshalling engine; the families above are
> exactly what a hook's args and returns can be.

## Checkpoint

- ✅ you know natural typing is two seams (compile-time IL rewrite + runtime marshaller) over one registry
- ✅ you can read the supported-families table and reason about *direction*
- ✅ you know the fail-loud edges, and that the marker warns when proxies are unpatched/stale

**Next → [4. Escape hatches](./04-escape-hatches.md)** — for the cases natural typing can't reach: an
erased handle, a type you can't name at author time, or a call so suspect it might hard-crash the game.
