# 8. Concurrency — threads, frames, and lifetimes

*The chapter for everything that happens **after** your hook body returns: main-thread work, waiting,
coroutines, pinning, and what survives a hot-reload. Misusing this surface produces the crashes the rest
of the engine works hard to prevent — and until now it was documented only in source headers.*

## The one rule

**il2cpp and Unity APIs run on the main thread.** A hook body already *is* on the main thread (the game
called you). Everything else — a REPL transport, async IO, a `Task.Run`, a plain `await Task.Delay` —
is not, and an il2cpp touch from the wrong thread is a native `c0000005`, not an exception.

`MainThread` is how you get back:

```csharp
MainThread.Post(() => player.Heal(50f));            // fire-and-forget, runs on the NEXT tick
int hp = await MainThread.Post(() => player.Hp);    // awaitable from a background thread
await MainThread.NextFrame();                       // yield one frame, RESUME on the main thread
await MainThread.Until(() => Game.IsLoaded, 30_000); // poll a condition each frame (timeout faults, no hang)
```

`NextFrame`/`Until` are the awaitables to reach for: they complete *from inside* the pump, so the code
after the `await` runs on the main thread. `Task.Delay`/`Task.Yield` resume on the **thread pool** —
everything after them is off-thread.

Two loud edges, by design:

- **Pre-pump `Post<T>` throws.** At chainload (before any frame) nothing drains the queue, so awaiting
  the result would deadlock forever; the throw tells you to defer or use fire-and-forget `Post(Action)`.
- **A REPL line can't await a main-thread primitive** — the deadlock guard fails it loud
  ([the REPL guide](./06-repl.md)).

## Frame validity: `Self`, `Proceed`, and `HookContext` don't survive an `await`

The dispatch frame is **thread-local and synchronous**: `Self`, `Proceed`, and a raw `HookContext` wrap
the native stack frame of the call that's happening *right now*. After an `await`, a `Post`, or inside a
coroutine, that frame is gone — `Self` reads null/stale, `Proceed` throws, a kept `HookContext` points at
a dead stack.

**The capture pattern**: read everything you need *synchronously*, then continue with the captured values.

```csharp
public Task<IResult> GamePrepare(bool local)
{
    var app = Self!;                          // capture SYNCHRONOUSLY — Self is dead after the first await
    return DeferredPrepare(app, local);
}

static async Task<IResult> DeferredPrepare(TarkovApplication app, bool local)
{
    await MainThread.Until(() => CommonUI.Instance != null);
    return app.GamePrepare(local);            // RE-INVOKE the method — you can't Proceed from a continuation
}
```

Note the last line: a continuation can't `Proceed` (the frame is gone), so a deferred hook **re-invokes
the hooked method** and lets the second entry take its forward branch.

**The faulted-Task rule**: a hook that returns `Task`/`Task<T>` must return a *completed* task or forward
the original's by identity — never a faulted one. The write-side marshaller reads `task.Result` to hand
the game its value, and that read throws on a fault. Catch inside your async body and return a sane value.

## Coroutines: frame-stepped work without async

```csharp
Coroutine co = Coroutines.Start(MyRoutine());

IEnumerator MyRoutine()
{
    yield return null;                 // resume next tick
    yield return Wait.Frames(10);      // skip 10 ticks
    yield return Wait.Seconds(1.5);    // REAL wall time (not Time.timeScale — this is not Unity's WaitForSeconds)
    yield return Wait.Until(() => State.Raid != null);   // predicate polled on the main thread
    co2 = Coroutines.Start(Sub());     // or `yield return Sub()` to nest
}
```

Every step runs on the main thread. A step that throws faults *that* coroutine (warning-logged) and
never touches its siblings. `co.Stop()` is thread-safe and takes effect at the next tick.

## Pinning: keeping an il2cpp object alive across the gap

A proxy pointer captured on a background thread (or held past the frame that produced it) is rooted by
**nothing** — the il2cpp GC may collect it before your posted work runs. Pin it across the gap:

```csharp
nint pin = MainThread.Pin(profile);                    // or Pin(ptr)
MainThread.Post(() => { Consume(profile); MainThread.Unpin(pin); });
```

The GC is non-moving, so `Pin` prevents *collection* only — the pointer never changes. Once the game
itself references the object (you handed it into a game structure), the game's own graph keeps it alive
and the pin has done its job.

## Lifetimes: what unload / hot-reload does to all of this

Hooks and lifecycle objects were always torn down with their generation. Now the **concurrency residue is
too** — on unload (every hot-reload is one), inutil reclaims, in order:

1. **queued `Post` closures** from your generation are dropped (old code must not run on a later tick),
2. **your coroutines** are stopped (a leaked `while(true)` loop used to keep stepping unloaded code
   forever *and* rooted the "collectible" ALC),
3. **your pins** are freed (previously one leaked il2cpp GCHandle per `Pin` per reload).

If anything was reclaimed you'll see it in the log:

```
inutil unload: reclaimed 1 coroutine(s), 1 queued post(s), 2 pin(s) the mod left running …
```

Treat that line as a nudge, not a service: **`OnUnload` is the place to stop/unpin your own.** The
scoped reclaim is the backstop that keeps a forgotten loop from haunting the process.

One scope edge: ownership is derived from *your* code — your iterator type, your delegate, your `Pin`
call site. If you hand a **host-defined** enumerator that merely wraps your delegate to
`Coroutines.Start`, pass the owner explicitly (`Coroutines.Start(routine, myAlc)`);
`MainThread.Until` already scopes by its predicate, so the common case is covered.

## Checkpoint

- ✅ you know the one rule (main thread) and which awaitables respect it (`NextFrame`/`Until` — not `Task.Delay`)
- ✅ you capture `Self` synchronously and re-invoke instead of `Proceed`-ing from a continuation
- ✅ you can step work with `Coroutines` + `Wait`, and pin what crosses the post→tick gap
- ✅ you know what unload reclaims for you — and that `OnUnload` should have done it first
