using Inutil.TestKit;

namespace Inutil.TestKit.Cli;

// Tests the tester. Each scenario writes a crafted sidecar and asserts the aggregator's verdict. The point
// is to prove — before we ever trust it against a real game — that the aggregator goes RED on every way a
// battery can fail OR hide a failure, and GREEN only on a genuinely complete, all-passing run. This is the
// structural replacement for the old log-scraper that exited SUCCESS while ignoring most of its verdicts.
public static class SelfTest
{
    static int _failures;

    public static int Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inutil-testkit-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // --- the ONLY green path: manifest + a result per declared id, all pass, then done ---
            Expect("complete all-pass", green: true, dir, new[]
            {
                Manifest("b", "a", "b"),
                Result("b", "a", TestStatus.Pass),
                Result("b", "b", TestStatus.Pass),
                Done("b"),
            });

            // explicit skip is a REPORTED outcome, not a silent gap — still green, but visible.
            Expect("complete with explicit skip", green: true, dir, new[]
            {
                Manifest("b", "a", "b"),
                Result("b", "a", TestStatus.Pass),
                Result("b", "b", TestStatus.Skip),
                Done("b"),
            }, wantSkipped: 1);

            // --- the failures the harness MUST catch ---

            Expect("a failing test", green: false, dir, new[]
            {
                Manifest("b", "a", "b"),
                Result("b", "a", TestStatus.Pass),
                Result("b", "b", TestStatus.Fail, "expected 42 got 0"),
                Done("b"),
            });

            // the original blind spot: a test declared but never reported.
            Expect("silently skipped test", green: false, dir, new[]
            {
                Manifest("b", "a", "b", "c"),
                Result("b", "a", TestStatus.Pass),
                Result("b", "b", TestStatus.Pass),
                Done("b"),
            });

            // crash/kill mid-run: results present, but no done marker.
            Expect("no done marker (killed mid-run)", green: false, dir, new[]
            {
                Manifest("b", "a", "b"),
                Result("b", "a", TestStatus.Pass),
                Result("b", "b", TestStatus.Pass),
            });

            // plugin never loaded: no manifest at all.
            Expect("no manifest", green: false, dir, new[]
            {
                Result("b", "a", TestStatus.Pass),
                Done("b"),
            });

            // a rogue result nobody declared.
            Expect("undeclared result", green: false, dir, new[]
            {
                Manifest("b", "a"),
                Result("b", "a", TestStatus.Pass),
                Result("b", "rogue", TestStatus.Pass),
                Done("b"),
            });

            // same test reported twice.
            Expect("duplicate result", green: false, dir, new[]
            {
                Manifest("b", "a"),
                Result("b", "a", TestStatus.Pass),
                Result("b", "a", TestStatus.Pass),
                Done("b"),
            });

            // --- structural corruption never passes silently ---
            ExpectRaw("empty file", green: false, dir, "");
            ExpectRaw("whitespace-only file", green: false, dir, "   \n\n  \n");
            ExpectRaw("malformed json line", green: false, dir,
                Manifest("b", "a").ToJsonLine() + "\n" +
                "{ this is not json\n" +
                Result("b", "a", TestStatus.Pass).ToJsonLine() + "\n" +
                Done("b").ToJsonLine() + "\n");

            // missing file: judged by a path that was never written.
            {
                var v = Aggregator.Judge(Path.Combine(dir, "does-not-exist.jsonl"));
                Assert("missing file", !v.Ok, v);
            }

            Console.WriteLine(_failures == 0
                ? "SELFTEST GREEN — the aggregator caught every failure mode and passed only the complete run."
                : $"SELFTEST RED — {_failures} aggregator behaviour(s) were wrong.");
            return _failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    static void Expect(string name, bool green, string dir, TestRecord[] records, int wantSkipped = -1)
    {
        var text = string.Concat(records.Select(r => r.ToJsonLine() + "\n"));
        ExpectRaw(name, green, dir, text, wantSkipped);
    }

    static void ExpectRaw(string name, bool green, string dir, string text, int wantSkipped = -1)
    {
        var path = Path.Combine(dir, "case-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, text);
        var v = Aggregator.Judge(path);
        Assert(name, v.Ok == green, v);
        if (wantSkipped >= 0 && v.Skipped != wantSkipped)
        {
            _failures++;
            Console.WriteLine($"  MISMATCH [{name}]: expected skipped={wantSkipped}, got {v.Skipped}");
        }
    }

    static void Assert(string name, bool ok, Verdict v)
    {
        if (ok)
        {
            Console.WriteLine($"  ok   [{name}] -> {(v.Ok ? "GREEN" : "RED")}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"  WRONG[{name}] -> {(v.Ok ? "GREEN" : "RED")} (unexpected)");
            foreach (var r in v.Reasons) Console.WriteLine($"        · {r}");
        }
    }

    static TestRecord Manifest(string battery, params string[] ids) => TestRecord.Manifest(battery, ids);
    static TestRecord Result(string battery, string id, TestStatus s, string? detail = null) =>
        TestRecord.Result(battery, id, s, detail);
    static TestRecord Done(string battery) => TestRecord.Done(battery);
}
