# 2. Hooking — change what a game method does

*The core of modding. Previous: [1. Setup](./01-setup-first-mod.md) · Next: [3. Natural typing](./03-natural-typing.md).*

## The one-line model

> **Subclass `Hook<T>` for the game type you want to hook, and declare a method with the *same name and
> parameter types* as the game method. Your body becomes the method. Call `Proceed<R>(...)` to run the
> original.**

No attributes, no registration, no `Harmony.PatchAll()`. inutil's discovery (from
[chapter 1](./01-setup-first-mod.md)) finds your `Hook<T>` subclass, matches each method to the game
proxy, and installs it as a single native detour.

## Anatomy of a hook

Say the game has `class Game { int Tally(int n) {...} }`. To hook `Tally`:

```csharp
using Inutil;
using ToyGame;   // the game's proxy namespace (Il2CppToyGame under MelonLoader — see chapter 1)

public sealed class TallyHook : Hook<Game>   // T = the proxy type to hook
{
    // Same name (Tally) + same parameter types (int) as Game.Tally. This IS Game.Tally now.
    int Tally(int n) => Proceed<int>(n * 2);   // run the ORIGINAL with the arg doubled; return what it returns
}
```

Drop that in `inutil-mods/tally/TallyHook.cs`, save, and `Game.Tally(5)` now computes on `10`. The
console confirms the wire:

```
[mods] tally: compiled (88ms) + loaded — 1 hook/lifecycle wired (gen 1)
```

`1 hook/lifecycle wired` — the matcher bound your `Tally(int)` to `Game::Tally(int)`.

## The one rule that surprises people: your body *replaces* the method

By default the game's original method **does not run** — your body is the whole method, and whatever it
returns is the result. If you want the original to run, you must call `Proceed`:

```csharp
int Tally(int n) => Proceed<int>(n);   // run the original UNCHANGED — a pure observer
int Tally(int n) => 42;                // NEVER run the original — always return 42
int Tally(int n) => Proceed<int>(n) + 1;   // run the original, then adjust its result
void Save()      { Log("about to save"); Proceed(); }   // pre-work, then the original (void form)
```

This is deliberate and loud-by-contract: forgetting `Proceed` doesn't silently change behavior mid-frame
— it means *you chose* to replace the method. The two shapes:

```csharp
protected static R    Proceed<R>(params object?[] args);   // run the original, get its typed return
protected static void Proceed   (params object?[] args);   // run the original, no return
```

Pass the incoming args to run it unchanged, or **new** args to change what it computes on.

## `Self` — the receiver

For an instance method, `Self` is the live object being called, typed as `T`:

```csharp
public sealed class HealthHook : Hook<Player>
{
    int GetHealth() => Self!.IsInvincible ? 9999 : Proceed<int>();   // read other state off the receiver
}
```

`Self` is `null` for a static game method (a static hook method has no receiver). A hook method's
static-ness must match the game method's — static hooks static, instance hooks instance.

## Combining a hook with per-frame logic

A single class can be a hook **and** a lifecycle object — the interfaces from
[chapter 1](./01-setup-first-mod.md) compose:

```csharp
public sealed class Cheat : Hook<Player>, ITick, ILoad
{
    bool _god;
    public void OnLoad() { }
    public void Tick()   { if (Input.GetKeyDown(KeyCode.F1)) _god = !_god; }   // toggle each frame
    int GetHealth()      => _god ? 9999 : Proceed<int>();                       // the hook reads the toggle
}
```

Discovery instantiates the class once; the hook method and the `Tick`/`OnLoad` share that instance.
(inutil excludes your `Tick`/`OnLoad`/`OnGui`/`OnUnload` from hook-matching, so they never bind to a
coincidentally-named game method.)

## How inutil finds the target (the matcher)

Discovery matches your method to a game method in tiers, first match wins:

| Tier | Matches when your method… | Example |
|---|---|---|
| 0 — exact | has the same name + exact parameter types | `int Tally(int)` → `Game::Tally(int)` |
| 1 — container widening | spells a read-only supertype of a flipped container param | `void Take(IEnumerable<int>)` → `Take(List<int>)` |
| 2 — interface impl | names a method the game implements as an explicit interface impl (mangled `I…_Method` on the proxy) | `void Tick()` → `ToyGame_ITicker_Tick` |
| 3 — generic inference | is a concrete instantiation of a generic game method | `int Echo(int)` → `Echo<T>` as `Echo<int>` |

Tiers 1–3 only ever bind a **unique** candidate — an ambiguous match falls through rather than guess.

**When nothing matches, you get told.** If the proxy has a method of that name but your signature is
wrong, inutil warns loud, naming the real overloads:

```
inutil hook: 'TallyHook.Tally(String)' matched no method on 'Game' — 'Game' has [Tally(Int32)].
Hooks bind by exact name + parameter types; fix the parameter types (or rename the method if it is a
private helper, not a hook).
```

When the name isn't on the proxy at all, **visibility declares intent**: a *public* method that matched
nothing warns too (a typo'd hook — or one the game update renamed out from under you — must never vanish
silently), while a `private`/`internal` method is a helper and stays silent. So write helpers in a hook
class freely — just don't make them public. (This is the
[fail-loud promise](./00-orient.md#the-fail-loud-promise): a mis-spelled hook never silently does nothing.)

**Returns are validated at bind time too.** The one shape whose failure used to be deferred to the first
live call is a managed `Task`/`Task<T>` declared against an *unflipped* il2cpp Task proxy (an unpatched
interop, or a generic-method Task return — those are never flipped): it compiles, binds, then throws
`MissingMethodException` mid-raid. Discover now rejects that hook loudly at load, with the fix in the
message (spell the il2cpp Task, or run the natural-typing patch; see
[natural typing](./03-natural-typing.md)).

## ref/out parameters

You can declare `ref`/`out` params; they're matched and marshalled like the game method, and their final
values become the method's outputs (replace semantics). One documented boundary: `Proceed` passes args
**by value**, so it doesn't flow the *original's* `ref`/`out` mutations back into your locals. If you
need to *transform* (not replace) what the original writes to an out-param, drop to the raw tier below.

## When you need the frame: the raw `HookContext` tier

The `Hook<T>` ergonomics cover the common cases. For frame-level control — transforming an original's
`ref`/`out` output, reading raw slots, or hooking something the ergonomic tier can't spell — use the
engine directly:

```csharp
using Inutil.Hooks;

// Pre-hook by name; the callback gets the raw HookContext (frame slots, Proceed, Skip).
IDisposable sub = Hooks.Pre(typeof(Game), "Tally", new[] { typeof(int) }, ctx =>
{
    int n = ctx.Arg<int>(0);        // read a slot
    ctx.SetArg(0, n * 2);           // write a slot
    ctx.Proceed();                  // run the original into the frame
    ctx.SetReturn(ctx.Return<int>() + 1);   // adjust the raw return
});
```

`HookContext` gives you `Arg<T>`/`SetArg<T>`, `Return<T>`/`SetReturn<T>`, `ArgString`/`ReturnString`,
`ThisPtr`, `Proceed()`, and `Skip()`. The ergonomic `Hook<T>` is built *on* exactly this — it's not a
separate mechanism, so mixing tiers is safe.

## Safety

A hook body that throws doesn't crash the game — the exception is caught and routed to the loader's
warning log (`inutil hook Game::Tally threw …`), and the frame continues. One misbehaving mod never
takes down its siblings or the process.

What happens to the *original* when your body throws depends on where: **before** you called `Proceed`,
the original auto-runs (a throwing hook degrades to "unhooked"); **after** you called `Proceed`, the
original has already run under your control and is never run a second time — the caller gets the
original's result. (A side-effecting game method can therefore never double-fire because your post-Proceed
code threw.)

One more validity rule with teeth: `Self`, `Proceed`, and a raw `HookContext` are **frame-scoped** — they
do not survive an `await`, a `MainThread.Post`, or a coroutine step. Capture what you need synchronously;
[8. Concurrency](./08-concurrency.md) is the chapter on doing async work from a hook safely.

`Proceed` itself is contract-checked at the call, and each check throws a directed error instead of
corrupting the frame:

- **Arity is none-or-all**: `Proceed()` forwards the incoming args unchanged; `Proceed(a, b, …)` must
  supply *every* parameter. A partial list used to silently mix your new values with stale incoming slots.
- **Value-typed args must be the exact type**: an `int` box for a `float` slot used to be written as raw
  int bits (garbage at the callee); now it's refused — write `0f`, not `0`. (An enum slot accepts its
  underlying primitive; that conversion is deliberate.)
- **`Proceed<R>`'s R is validated** against the binding: it must be your declared return type, the game
  method's return type, or a proxy base of it. `Proceed<int>` on a float-returning method used to
  reinterpret the return register's raw bytes.

## Checkpoint

- ✅ a `Hook<T>` whose body changes a real game method
- ✅ you understand replace-by-default and when to call `Proceed`
- ✅ you can read `Self`, combine a hook with `ITick`, and read the matcher's fail-loud diagnostic

Notice what you *didn't* do: you passed `int`, not `Il2CppSystem.Int32`. That's the next chapter.

**Next → [3. Natural typing](./03-natural-typing.md)** — why plain `Task<T>`/`int?`/`List<T>`/`Action`
cross the boundary for free, and exactly where that stops.
