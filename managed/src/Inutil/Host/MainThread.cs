// The main-thread dispatch pump — marshal a delegate onto the loader's main (il2cpp-attached) thread, where
// Unity and most il2cpp APIs MUST run. The keystone the async/coroutine work builds on: a hook body's
// HookContext frame is valid only for the SYNCHRONOUS call (it wraps the native stack frame), so you cannot
// await-then-touch it; the sound pattern is to CAPTURE what you need and Post a closure that does the il2cpp
// work on the next main-thread tick.
//
// Substrate-AGNOSTIC: never names a loader type. A host shim PUSHES a per-frame tick in by calling
// FrameDriver.Tick() on the main thread — MelonLoader from MelonMod.OnUpdate (free), BepInEx from an injected
// InutilPump.Update (its BasePlugin has no per-frame callback; see Inutil.BepInEx). The SDK side is identical
// for both; only the tick SOURCE differs, in the shim. Lives in Inutil.dll so any consumer reuses it. The
// snapshot-budget drain and per-callback exception isolation are sound; Post<T> fails loud in the pre-pump
// window rather than enqueueing into a queue nothing can drain yet.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;

namespace Inutil.Host;

public static class MainThread
{
    private static readonly ConcurrentQueue<Action> _q = new();
    private static volatile int _mainThreadId = -1;   // captured on the first Drain (the tick runs on the main thread)

    // True once the pump has ticked at least once (a tick source is wired AND the player loop is running).
    public static bool IsPumping => _mainThreadId != -1;

    // Are we on the main thread right now? -1 before the first tick, so this is false pre-pump and Post always
    // enqueues (a hook firing before the loop resumes still runs its posted work on the first tick).
    public static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

    // Queue `a` to run on the next main-thread tick. Always enqueues (even when called ON the main thread):
    // running inline mid-frame would re-enter in surprising order; next-tick is the clean contract. Any exception
    // `a` throws is isolated at Drain. Fire-and-forget: no one awaits a result, so a pre-pump enqueue is fine.
    public static void Post(Action a)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));
        _q.Enqueue(a);
    }

    // Post work that produces a result, awaitable from a BACKGROUND thread (a REPL transport, async IO). If
    // called ON the main thread it runs inline — awaiting our own tick would deadlock, since the main thread is
    // the only thing that drains the queue.
    //
    // Pre-pump guard (fail loud beats hang forever). Before the first Drain, `_mainThreadId` is unset, so
    // OnMainThread is false for EVERY thread: we can neither run `f` inline NOR rely on a tick to drain the queue
    // (no tick source has fired yet — chainload, before any frame). A caller that synchronously waits on the
    // returned Task in this window would deadlock permanently, contradicting the "awaitable" guarantee. Throw a
    // clear error instead. (Post(Action) has no such guard: nobody awaits it, so it just runs later.)
    public static Task<T> Post<T>(Func<T> f)
    {
        if (f is null) throw new ArgumentNullException(nameof(f));
        if (!IsPumping)
            throw new InvalidOperationException(
                "MainThread.Post<T> was called before the first pump tick; this cannot complete. No tick source " +
                "has run yet (chainload, before any frame), so nothing can drain the queue and a synchronous wait " +
                "on the returned Task would deadlock forever. Post once the pump is live (IsPumping), or use " +
                "MainThread.Post(Action) for fire-and-forget work that does not need a result.");
        if (OnMainThread)
        {
            try { return Task.FromResult(f()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _q.Enqueue(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }

    // The seam: a host shim calls this every frame, ON the main thread (via FrameDriver.Tick). Drains a SNAPSHOT
    // of the queue — an action that itself Posts runs on the NEXT tick, so a self-posting action can't starve the
    // frame. Each action is exception-isolated (routes to Hooks.OnWarning, never stops the pump). Then advances
    // the main-thread coroutine runner on this same per-frame tick (Coroutines.Tick), AFTER the action queue (a
    // coroutine started by a posted action this tick begins next tick).
    public static void Drain()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;   // by contract we are on the main thread here
        int budget = _q.Count;                                  // only what was queued BEFORE this tick
        while (budget-- > 0 && _q.TryDequeue(out Action? a))
        {
            try { a(); }
            catch (Exception ex)
            {
                Hooks.Hooks.OnWarning?.Invoke($"inutil pump: posted action threw {ex.GetType().Name}: {ex.Message}");
            }
        }
        Coroutines.Tick();   // step main-thread coroutines after the action queue (shares this one per-frame seam)
    }

    // ── frame-yield awaitables (the async story's *waiting* primitive) ──────────────────────────────────────
    // `await MainThread.NextFrame()` / `await MainThread.Until(cond)` let a hook body yield Unity frames and RESUME
    // ON THE MAIN THREAD. Plain Task.Yield()/Task.Delay resume on the thread pool, so any il2cpp/Unity touch after
    // the await is off-thread (c0000005); these complete their TCS from INSIDE Drain/Tick (on the main thread), so
    // — with no RunContinuationsAsynchronously and no captured sync context — the continuation runs inline on the
    // main thread.

    // Completes on the next main-thread tick, on the main thread.
    public static Task NextFrame()
    {
        var tcs = new TaskCompletionSource();     // inline continuation on the completing (main) thread
        Post(() => tcs.TrySetResult());
        return tcs.Task;
    }

    // Polls `condition` each frame ON the main thread (so it may read il2cpp state); completes when it returns true.
    // Past `timeoutMs` (>= 0) the Task faults with TimeoutException — no hang. A throwing predicate faults the Task.
    public static Task Until(Func<bool> condition, int timeoutMs = -1)
    {
        if (condition is null) throw new ArgumentNullException(nameof(condition));
        var tcs = new TaskCompletionSource();
        // Owner = the CONDITION's ALC, not UntilRoutine's (which is host-defined — Inutil.dll — and would make
        // the poll outlive the mod that supplied the predicate). Mods.RemoveAll → Coroutines.StopAll then
        // reaps a still-polling Until when its mod generation unloads.
        Coroutines.Start(UntilRoutine(condition, timeoutMs, tcs), OwnerOf(condition));
        return tcs.Task;
    }

    private static IEnumerator UntilRoutine(Func<bool> condition, int timeoutMs, TaskCompletionSource tcs)
    {
        Stopwatch? sw = timeoutMs >= 0 ? Stopwatch.StartNew() : null;
        while (true)
        {
            bool ok;
            try { ok = condition(); }
            catch (Exception ex) { tcs.TrySetException(ex); yield break; }
            if (ok) { tcs.TrySetResult(); yield break; }
            if (sw is not null && sw.ElapsedMilliseconds >= timeoutMs)
            { tcs.TrySetException(new TimeoutException($"MainThread.Until: condition not met within {timeoutMs}ms")); yield break; }
            yield return null;   // re-poll next tick, on the main thread
        }
    }

    // Keep an il2cpp object alive across the post->tick gap. A background thread that captured an Il2CppObject*
    // (e.g. a proxy's .Pointer) and Posts a closure using it must Pin it first: once the value leaves the
    // synchronous hook frame nothing else roots it, and the incremental GC may free it before the tick runs.
    // Returns an il2cpp GCHandle; free it with Unpin once done. Boehm is non-moving, so Pin prevents COLLECTION
    // only — the pointer itself stays valid either way.
    //
    // OWNERSHIP: each pin is registered to the CALLING assembly's ALC (NoInlining so GetCallingAssembly is the
    // mod, not a JIT-inlined frame), and Mods.RemoveAll → UnpinAll(owner) frees a generation's leftover pins on
    // unload/hot-reload — else they leaked an il2cpp GCHandle per Pin per reload, forever. A pin whose pointee the
    // game itself now references stays alive through the game's own graph after the unpin; the handle was only
    // ever the bridge across the hand-off gap.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nint Pin(nint il2cppObject) => PinCore(il2cppObject, Assembly.GetCallingAssembly());
    // Overload taking the managed proxy directly, so a mod pins without spelling `.Pointer` (an Il2CppObjectBase member).
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nint Pin(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase il2cppObject)
        => PinCore(il2cppObject.Pointer, Assembly.GetCallingAssembly());

    public static void Unpin(nint gcHandle)
    {
        _pins.TryRemove(gcHandle, out _);
        IL2CPP.il2cpp_gchandle_free(gcHandle);
    }

    private static readonly ConcurrentDictionary<nint, AssemblyLoadContext?> _pins = new();

    private static nint PinCore(nint il2cppObject, Assembly caller)
    {
        nint handle = IL2CPP.il2cpp_gchandle_new(il2cppObject, false);
        _pins[handle] = AssemblyLoadContext.GetLoadContext(caller);
        return handle;
    }

    // ── ALC-scoped teardown (driven by Mods.RemoveAll on mod unload/hot-reload) ─────────────────────────────

    // Drop every queued posted action owned by `owner` — a closure from an unloading generation must not run
    // old-generation code on a later tick (nor root the collectible ALC from the queue). Snapshot-bounded like
    // Drain. Returns how many were dropped.
    public static int DropPosted(AssemblyLoadContext owner)
    {
        if (owner is null) throw new ArgumentNullException(nameof(owner));
        int dropped = 0;
        int budget = _q.Count;
        while (budget-- > 0 && _q.TryDequeue(out Action? a))
        {
            if (OwnerOf(a) == owner) { dropped++; continue; }
            _q.Enqueue(a);   // not ours — back of the queue (relative order among survivors is preserved)
        }
        return dropped;
    }

    // Free every pin registered to `owner` (see Pin). Returns how many handles were freed.
    public static int UnpinAll(AssemblyLoadContext owner)
    {
        if (owner is null) throw new ArgumentNullException(nameof(owner));
        int freed = 0;
        foreach (var kv in _pins)
            if (kv.Value == owner && _pins.TryRemove(kv.Key, out _))
            {
                IL2CPP.il2cpp_gchandle_free(kv.Key);
                freed++;
            }
        return freed;
    }

    // The ALC a delegate's code lives in: its target instance's type, or (static lambdas) its declaring type.
    private static AssemblyLoadContext? OwnerOf(Delegate d)
    {
        Type? t = d.Target?.GetType() ?? d.Method.DeclaringType;
        return t is null ? null : AssemblyLoadContext.GetLoadContext(t.Assembly);
    }
}
