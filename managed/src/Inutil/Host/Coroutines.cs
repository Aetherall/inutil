// Main-thread coroutine runner — drive an IEnumerator one step per frame ON the main (il2cpp-attached) thread,
// on top of the MainThread pump. The async story's STEPPING primitive: a hook body CAPTUREs what it needs and
// Starts a coroutine; each `yield return` resumes on a later main-thread tick, where il2cpp APIs are safe to
// call again (the HookContext frame is long gone, but a captured + pinned proxy is not).
//
// A tiny PURE-MANAGED scheduler with its own yield vocabulary (null / Wait.Frames / Wait.Seconds /
// Wait.Until|While / a nested IEnumerator). It deliberately does NOT interpret il2cpp UnityEngine.YieldInstruction
// types, so — like the pump — it is loader- AND game-agnostic and validates identically on both loaders. (A
// consumer needing true Unity coroutine fidelity — WaitForSeconds respecting Time.timeScale, AsyncOperation,
// WaitForEndOfFrame — calls their loader's own facility; this is the marshal-and-step primitive, not a Unity
// coroutine reimplementation.)
//
// Substrate-AGNOSTIC like MainThread: never names a loader type. Ticked once per frame by MainThread.Drain
// (AFTER the action queue). The snapshot budget + per-step exception isolation are deliberate.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Loader;

namespace Inutil.Host;

// A yield gate the scheduler polls each tick ON the main thread. `yield return Wait.X(...)` parks the coroutine
// until Ready() returns true. The predicate forms (Until/While) are evaluated on the main thread, so they may
// safely read il2cpp state.
public abstract class Wait
{
    internal abstract bool Ready();

    // Resume after `frames` ticks (<= 0 behaves like `yield return null` — resume on the next tick).
    public static Wait Frames(int frames) => new WaitFrames(frames);

    // Resume after `seconds` of REAL wall time (monotonic Stopwatch). NOT UnityEngine.WaitForSeconds — it
    // ignores game pause / Time.timeScale (that fidelity needs the loader's own coroutines); managed clock keeps
    // the runner game-agnostic.
    public static Wait Seconds(double seconds) => new WaitSeconds(seconds);

    // Resume once `predicate` returns true (polled each tick on the main thread).
    public static Wait Until(Func<bool> predicate) =>
        new WaitUntil(predicate ?? throw new ArgumentNullException(nameof(predicate)));

    // Resume once `predicate` returns false.
    public static Wait While(Func<bool> predicate)
    {
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));
        return new WaitUntil(() => !predicate());
    }

    private sealed class WaitFrames : Wait
    {
        private int _remaining;
        public WaitFrames(int frames) => _remaining = frames;
        // Polled once per tick AFTER the yield: n=0 is ready on the first poll (next tick), n skips n ticks.
        internal override bool Ready() => _remaining-- <= 0;
    }

    private sealed class WaitSeconds : Wait
    {
        private readonly long _deadline;   // Stopwatch timestamp; captured at the yield (construction ~= yield time)
        public WaitSeconds(double seconds) =>
            _deadline = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        internal override bool Ready() => Stopwatch.GetTimestamp() >= _deadline;
    }

    private sealed class WaitUntil : Wait
    {
        private readonly Func<bool> _predicate;
        public WaitUntil(Func<bool> predicate) => _predicate = predicate;
        internal override bool Ready() => _predicate();
    }
}

// A running coroutine handle. Returned by Coroutines.Start; carries the nesting stack + current gate. Stop() is
// thread-safe (sets a flag honored at the next tick); IsRunning flips false once it finishes, faults, or stops.
public sealed class Coroutine
{
    internal readonly Stack<IEnumerator> Stack = new();   // sub-coroutine nesting; the top is the active enumerator
    internal Wait? Gate;                                  // current park gate (null => MoveNext on the next tick)
    internal AssemblyLoadContext? Owner;                  // the ALC whose unload stops this coroutine (StopAll)
    internal volatile bool StopRequested;

    public bool IsRunning { get; internal set; } = true;
    public Exception? Fault { get; internal set; }        // set if a step or a predicate threw (then IsRunning=false)

    public void Stop() => StopRequested = true;
}

public static class Coroutines
{
    private static readonly ConcurrentQueue<Coroutine> _pending = new();   // starts from ANY thread land here
    private static readonly List<Coroutine> _active = new();               // main-thread-only working set

    // Frames the scheduler has ticked (one per Drain). Monotonic; useful for frame-based waits + diagnostics.
    public static long TickCount { get; private set; }

    // Coroutines currently scheduled (excludes pending starts not yet merged). Main-thread read.
    public static int ActiveCount => _active.Count;

    // Start `routine`; the FIRST MoveNext runs on the NEXT main-thread tick — uniform regardless of caller
    // thread (no synchronous first-step like Unity's StartCoroutine). Thread-safe: this only enqueues; the body
    // executes solely on the main thread at Tick. NOTE: if the routine touches il2cpp, the value it captures
    // must be kept alive across the post->step gap (MainThread.Pin), same as a posted action.
    public static Coroutine Start(IEnumerator routine) => Start(routine, null);

    // Overload with an explicit OWNER — the ALC whose unload stops this coroutine (Mods.RemoveAll →
    // StopAll(owner)). Default (null) derives it from the enumerator's own assembly (right for the common case:
    // a mod's iterator method compiles into the mod's assembly). Pass it explicitly when the enumerator is
    // host-defined but drives a caller-supplied delegate (MainThread.Until) — else the routine outlives the
    // delegate's ALC and keeps running unloaded code.
    public static Coroutine Start(IEnumerator routine, AssemblyLoadContext? owner)
    {
        if (routine is null) throw new ArgumentNullException(nameof(routine));
        var co = new Coroutine
        {
            Owner = owner ?? AssemblyLoadContext.GetLoadContext(routine.GetType().Assembly),
        };
        co.Stack.Push(routine);
        _pending.Enqueue(co);
        return co;
    }

    // Request that `coroutine` stop (idempotent, thread-safe). It makes no further progress and is reaped on
    // the next tick.
    public static void Stop(Coroutine coroutine)
    {
        if (coroutine is null) throw new ArgumentNullException(nameof(coroutine));
        coroutine.Stop();
    }

    // Stop every coroutine owned by `owner` — the ALC-scoped teardown Mods.RemoveAll drives on mod unload.
    // Without this, an unloaded generation's `while(true)` coroutine kept stepping OLD-generation code forever
    // AND rooted the "collectible" ALC (the enumerator state machine is an ALC-defined type held by _active).
    // Thread-safe like Stop: only the StopRequested flag is written; the main-thread Tick reaps. Returns how
    // many were flagged (pending starts included — reaped at merge, before any step).
    public static int StopAll(AssemblyLoadContext owner)
    {
        if (owner is null) throw new ArgumentNullException(nameof(owner));
        int n = 0;
        foreach (Coroutine co in _pending)                       // ConcurrentQueue enumeration is a snapshot
            if (co.Owner == owner && !co.StopRequested) { co.Stop(); n++; }
        Coroutine[] active;
        lock (_active) { active = _active.ToArray(); }           // snapshot vs a concurrent main-thread Tick
        foreach (Coroutine co in active)
            if (co.Owner == owner && co.IsRunning && !co.StopRequested) { co.Stop(); n++; }
        return n;
    }

    // Driven once per frame by MainThread.Drain, ON the main thread, AFTER the action queue. Merges pending
    // starts, advances each active coroutine by AT MOST one MoveNext (bounded per tick, like the pump's snapshot
    // budget — a self-restarting coroutine can't starve the frame), then reaps finished/faulted/stopped. A start
    // issued during this tick is merged NEXT tick (uniform first-step timing).
    internal static void Tick()
    {
        TickCount++;
        // _active is guarded by its own lock: Tick owns it for the frame (uncontended in the steady state —
        // ~nothing), and StopAll snapshots under it from whatever thread a mod unload runs on. Reentrant on
        // the main thread, so a step that itself calls StopAll/Start cannot deadlock.
        lock (_active)
        {
            while (_pending.TryDequeue(out Coroutine? co)) _active.Add(co);

            // Snapshot the count: coroutines added by a step this tick are in _pending, not _active, so they
            // begin next tick — never re-entered within the same Tick.
            int count = _active.Count;
            for (int i = 0; i < count; i++) Step(_active[i]);

            _active.RemoveAll(static c => !c.IsRunning);
        }
    }

    private static void Step(Coroutine co)
    {
        if (co.StopRequested) { co.IsRunning = false; return; }

        if (co.Gate is not null)
        {
            bool ready;
            try { ready = co.Gate.Ready(); }
            catch (Exception ex) { Fault(co, ex); return; }
            if (!ready) return;
            co.Gate = null;   // gate opened — fall through and advance THIS tick (no wasted idle tick)
        }

        IEnumerator top = co.Stack.Peek();
        bool moved;
        try { moved = top.MoveNext(); }
        catch (Exception ex) { Fault(co, ex); return; }

        if (!moved)
        {
            co.Stack.Pop();                                  // this enumerator finished
            if (co.Stack.Count == 0) co.IsRunning = false;   // whole coroutine done (a parent, if any, resumes next tick)
            return;
        }

        switch (top.Current)
        {
            case Wait w:        co.Gate = w; break;          // park on the gate
            case IEnumerator e: co.Stack.Push(e); break;     // nested sub-coroutine; driven from the next tick
            default:            co.Gate = null; break;       // null / unrecognized -> resume next tick
        }
    }

    private static void Fault(Coroutine co, Exception ex)
    {
        co.Fault = ex;
        co.IsRunning = false;   // a faulted coroutine is dropped; the rest of the frame's coroutines are unaffected
        Hooks.Hooks.OnWarning?.Invoke($"inutil coroutine: step threw {ex.GetType().Name}: {ex.Message}");
    }
}
