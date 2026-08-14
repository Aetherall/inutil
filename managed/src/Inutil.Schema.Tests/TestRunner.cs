namespace Inutil.Schema.Tests;

// Dependency-free assert harness shared by the planner and classifier suites (the same offline, no-NuGet style
// as the validation harness's own self-test). Failures accrue on a static counter; Program exits non-zero if any.
static class T
{
    public static int Failures;

    public static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]" + (ok || detail is null ? "" : $"  -- {detail}"));
        if (!ok) Failures++;
    }
}
