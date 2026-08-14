// Mod diagnostics — the human-facing channel for "your hot-reload edit didn't take".
//
// When a hot-reload compile fails, CsModHost keeps the PREVIOUS generation running and logs the errors — but a
// human editing a `.cs` and playing sees nothing change and can't tell their edit failed vs applied-with-no-
// visible-effect (the agent side is already covered by `game status DEGRADED` / mod-verify). This is the engine
// half of the fix: a game-FREE store of recent compile/load outcomes that a game-specific renderer polls and
// surfaces (OT's dev `moderrors` coremod toasts them). The engine names no game UI type — a generic on-screen
// renderer for other inutil hosts is a follow-on.
//
// PULL model, deliberately: compile errors are recorded on the COMPILE WORKER thread (CsModHost.ReloadOnBg),
// load outcomes on the MAIN thread (LoadOnMain). A push event would fire game-touching handlers off-thread
// (c0000005). Instead the renderer POLLS `Since(seq)` from its own per-frame ITick — already on the main thread
// — so every game call it makes is main-thread by construction. No il2cpp, no UnityEngine — game-free.
using System.Collections.Generic;

namespace Inutil;

public static class Diagnostics
{
    // One recorded mod compile/load outcome. Ok=false: a FAILURE — the new code did NOT take; the previous
    // generation (if any) still runs. Ok=true: SUCCESS — clears any prior failure for that mod. Seq is a
    // per-process monotonic id the renderer tracks to poll only what's new. Summary is a one-line human string
    // (the first CSid diagnostic, or the exception message).
    public readonly record struct ModEvent(long Seq, string Mod, bool Ok, string Kind, string Summary);

    private static readonly object _gate = new();
    private static readonly List<ModEvent> _recent = new();
    private static long _seq;
    private const int Cap = 64;   // a bounded tail — the renderer only cares about the newest few

    public static void RecordFailure(string mod, string kind, string summary) => Append(mod, false, kind, summary);
    public static void RecordSuccess(string mod) => Append(mod, true, "compiled", "");

    private static void Append(string mod, bool ok, string kind, string summary)
    {
        lock (_gate)
        {
            _recent.Add(new ModEvent(++_seq, mod, ok, kind, summary ?? ""));
            if (_recent.Count > Cap) _recent.RemoveRange(0, _recent.Count - Cap);
        }
    }

    // Events strictly after `afterSeq`, oldest-first — the renderer's per-frame pull. Track the returned events'
    // max Seq (or `Sequence`) as the next `afterSeq`. A renderer starting mid-session baselines on `Sequence` so
    // it doesn't replay history as fresh toasts.
    public static IReadOnlyList<ModEvent> Since(long afterSeq)
    {
        lock (_gate)
        {
            var outp = new List<ModEvent>();
            foreach (ModEvent e in _recent) if (e.Seq > afterSeq) outp.Add(e);
            return outp;
        }
    }

    // The current high-water sequence — the baseline a renderer captures at load.
    public static long Sequence { get { lock (_gate) return _seq; } }

    // The mods whose LATEST outcome is a failure (a later success for the same mod clears it) — the set a
    // renderer shows as "currently broken". Newest-first.
    public static IReadOnlyList<ModEvent> ActiveFailures()
    {
        lock (_gate)
        {
            var latest = new Dictionary<string, ModEvent>();
            foreach (ModEvent e in _recent) latest[e.Mod] = e;   // _recent is in Seq order -> last wins
            var outp = new List<ModEvent>();
            foreach (ModEvent e in latest.Values) if (!e.Ok) outp.Add(e);
            outp.Sort((a, b) => b.Seq.CompareTo(a.Seq));
            return outp;
        }
    }
}
