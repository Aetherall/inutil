namespace Inutil.TestKit;

// The emitter side of the protocol, used from inside the loader plugin. A battery drives it as:
//   sink.Manifest(ids);  foreach case: sink.Result(...);  sink.Done();
public interface IResultSink
{
    void Manifest(IEnumerable<string> ids);
    void Result(string id, TestStatus status, string? detail = null);
    void Done();
}

// Writes JSONL to a host-visible sidecar file, FLUSHING every record (append-and-close per line). The game
// runs headless under Proton and is force-killed a few seconds in, so robustness-under-kill is the design
// constraint: every record already returned from Result() is on disk, and a kill can at worst drop the
// trailing `done` — which the aggregator reads as "did not finish", i.e. red, never a false green.
public sealed class FileResultSink : IResultSink
{
    readonly string _path;
    readonly string _battery;

    public FileResultSink(string path, string battery)
    {
        _path = path;
        _battery = battery;
    }

    // Manifest is line 1 AND truncates any stale file left by a prior run, so a run always starts clean
    // even if the driver forgot to remove it.
    public void Manifest(IEnumerable<string> ids)
        => File.WriteAllText(_path, TestRecord.Manifest(_battery, ids).ToJsonLine() + "\n");

    public void Result(string id, TestStatus status, string? detail = null)
        => Append(TestRecord.Result(_battery, id, status, detail));

    public void Done()
        => Append(TestRecord.Done(_battery));

    void Append(TestRecord r) => File.AppendAllText(_path, r.ToJsonLine() + "\n");
}
