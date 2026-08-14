using System.Text;

namespace Inutil.TestKit;

// The machine-checked verdict for one battery's sidecar. Ok is true ONLY when every reason list is empty;
// there is deliberately no path that returns green by absence of evidence.
public sealed class Verdict
{
    public bool Ok;
    public string Headline = "";
    public readonly List<string> Reasons = new();

    public int Passed, Failed, Skipped;   // reported result statuses
    public int Missing, Unexpected;       // manifest ↔ results set diffs
    public int Malformed, Manifests, Dones;

    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append(Ok ? "GREEN" : "RED").Append(" — ").Append(Headline).Append('\n');
        sb.Append($"   passed={Passed} failed={Failed} skipped={Skipped} " +
                  $"missing={Missing} unexpected={Unexpected} malformed={Malformed}\n");
        foreach (var r in Reasons) sb.Append("   ✗ ").Append(r).Append('\n');
        return sb.ToString();
    }
}

// Reads a battery's JSONL sidecar and decides pass/fail. Every failure mode a battery can hide is an explicit
// red branch here:
//   * file missing / empty            -> the battery produced no output at all (plugin never loaded)
//   * no manifest                     -> we can't know what was SUPPOSED to run, so we can't trust "all green"
//   * no done                         -> the battery didn't finish; a partial file is not a pass
//   * manifest id with no result      -> a silently skipped test (the original 49-bugs blind spot)
//   * result id not in the manifest   -> a rogue/duplicate report
//   * any malformed line              -> never silently dropped
//   * any result with status=fail     -> the obvious one the old harness still managed to ignore
public static class Aggregator
{
    public static Verdict Judge(string sidecarPath)
    {
        var v = new Verdict();

        if (!File.Exists(sidecarPath))
        {
            v.Reasons.Add($"no result file at '{sidecarPath}': the battery never produced output " +
                          "(plugin failed to load, or the game crashed before writing a single record).");
            return Finish(v);
        }

        var lines = File.ReadAllLines(sidecarPath).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0)
        {
            v.Reasons.Add($"empty result file '{sidecarPath}': the battery wrote nothing.");
            return Finish(v);
        }

        var records = new List<TestRecord>();
        foreach (var line in lines)
        {
            try { records.Add(TestRecord.Parse(line)); }
            catch (Exception ex)
            {
                v.Malformed++;
                v.Reasons.Add($"malformed record ({ex.GetType().Name}): {Trunc(line)}");
            }
        }

        var manifests = records.Where(r => r.Kind == RecordKind.Manifest).ToList();
        var dones     = records.Where(r => r.Kind == RecordKind.Done).ToList();
        var results   = records.Where(r => r.Kind == RecordKind.Result).ToList();
        v.Manifests = manifests.Count;
        v.Dones = dones.Count;

        foreach (var r in results)
        {
            switch (r.Status)
            {
                case TestStatus.Pass: v.Passed++; break;
                case TestStatus.Skip: v.Skipped++; break;
                case TestStatus.Fail:
                    v.Failed++;
                    v.Reasons.Add($"FAIL {r.Id}: {r.Detail ?? "(no detail)"}");
                    break;
                default:
                    v.Reasons.Add($"result {r.Id ?? "(no id)"} has no status");
                    break;
            }
        }

        if (manifests.Count == 0)
            v.Reasons.Add("no manifest record: cannot know which tests were expected, so results cannot be trusted as complete.");
        else if (manifests.Count > 1)
            v.Reasons.Add($"{manifests.Count} manifest records: exactly one is expected.");

        if (dones.Count == 0)
            v.Reasons.Add("no done record: the battery did not finish (crashed or was killed mid-run); a partial run is not a pass.");

        // Manifest ↔ results set reconciliation (only meaningful with exactly one manifest).
        if (manifests.Count == 1)
        {
            var expected = new HashSet<string>(manifests[0].Ids ?? Array.Empty<string>());
            var reported = new List<string>();
            foreach (var r in results)
                if (r.Id is not null) reported.Add(r.Id);

            var dupes = reported.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            foreach (var d in dupes)
                v.Reasons.Add($"duplicate result for '{d}': a test reported more than once (ambiguous).");

            var reportedSet = new HashSet<string>(reported);
            var missing = expected.Where(id => !reportedSet.Contains(id)).OrderBy(x => x).ToList();
            var unexpected = reportedSet.Where(id => !expected.Contains(id)).OrderBy(x => x).ToList();
            v.Missing = missing.Count;
            v.Unexpected = unexpected.Count;
            foreach (var id in missing)
                v.Reasons.Add($"declared but never reported: '{id}' (a silently skipped test).");
            foreach (var id in unexpected)
                v.Reasons.Add($"reported but not declared in the manifest: '{id}'.");
        }

        return Finish(v);
    }

    static Verdict Finish(Verdict v)
    {
        v.Ok = v.Reasons.Count == 0;
        v.Headline = v.Ok
            ? $"{v.Passed} passed" + (v.Skipped > 0 ? $", {v.Skipped} skipped" : "")
            : $"{v.Reasons.Count} problem(s)";
        return v;
    }

    static string Trunc(string s) => s.Length <= 160 ? s : s.Substring(0, 157) + "...";
}
