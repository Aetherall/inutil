using Inutil.TestKit;

namespace Inutil.Battery;

// Loader-AGNOSTIC battery runner. The two loader shims (BepInEx / MelonLoader) are ~20-line entry points
// that build a Suite, register the same cases, and call RunAll — there is exactly one implementation of
// "declare the manifest, run each case, isolate its failure, report done", so the two loaders cannot drift
// (one shared driver, thin per-loader shims, never two copies of the contract).

public sealed class AssertException : Exception { public AssertException(string m) : base(m) { } }
public sealed class SkipException : Exception { public SkipException(string m) : base(m) { } }

// Case bodies signal outcome by control flow: return = pass (the returned string is the pass detail),
// Check.True(false, …) = fail, Check.Skip(…) = explicit skip. Any other exception is caught as a fail, so
// a throwing case can never take the whole battery (and the game) down or silently vanish.
public static class Check
{
    public static void True(bool cond, string message) { if (!cond) throw new AssertException(message); }
    public static void Skip(string reason) => throw new SkipException(reason);
}

public sealed class Suite
{
    // The host-visible file the battery writes and the driver reads back. Lives in the game root, which is
    // mapped to the host filesystem under wine (the same place -logFile writes), so the driver reads it
    // directly after the game exits.
    public const string SidecarName = "inutil-results.jsonl";

    readonly string _battery;
    readonly List<(string id, Func<string?> body)> _cases = new();

    public Suite(string battery) => _battery = battery;

    public void Add(string id, Func<string?> body) => _cases.Add((id, body));

    public void RunAll(IResultSink sink)
    {
        // Manifest FIRST: declare every id up front so the aggregator can tell a case that ran-and-failed
        // from one that never ran at all.
        sink.Manifest(_cases.Select(c => c.id));
        foreach (var (id, body) in _cases)
        {
            try { sink.Result(id, TestStatus.Pass, body()); }
            catch (SkipException sk) { sink.Result(id, TestStatus.Skip, sk.Message); }
            catch (Exception ex) { sink.Result(id, TestStatus.Fail, $"{ex.GetType().Name}: {ex.Message}"); }
        }
        // Done LAST: its presence is what tells the aggregator the run completed rather than being killed.
        sink.Done();
    }
}
