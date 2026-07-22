using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inutil.TestKit;

// A battery reports over three record kinds, all written as one flat JSON object per line (JSONL):
//   manifest — the FULL set of test ids this battery intends to run, written FIRST. Lets the aggregator
//              detect a test that was declared but never reported (a SILENTLY skipped test — the exact
//              blind spot docs/contribution/architecture/20-testing.md blames for 49 bugs going unseen).
//   result   — one per test actually run: pass / fail / skip + a human detail.
//   done     — written LAST, after every case. Its ABSENCE means the battery crashed or was killed
//              mid-run, so a partial file can never be mistaken for a complete green one.
public enum RecordKind { Manifest, Result, Done }

// skip is an EXPLICIT, reported non-result (e.g. "not applicable on this loader") — visible, never silent.
// A test that simply never emits a record is MISSING, which the aggregator treats as red. (P4: silence is
// a design decision, not a default.)
public enum TestStatus { Pass, Fail, Skip }

public sealed class TestRecord
{
    public RecordKind Kind { get; set; }
    public string Battery { get; set; } = "";

    // result-only
    public string? Id { get; set; }
    public TestStatus? Status { get; set; }
    public string? Detail { get; set; }

    // manifest-only
    public string[]? Ids { get; set; }

    // The SINGLE (de)serializer both the emitter and the aggregator use — the whole point of putting this
    // in a shared assembly is that neither side can hand-roll a JSON shape the other doesn't understand.
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    public string ToJsonLine() => JsonSerializer.Serialize(this, Json);

    public static TestRecord Parse(string line) =>
        JsonSerializer.Deserialize<TestRecord>(line, Json)
        ?? throw new FormatException("deserialized to null");

    public static TestRecord Manifest(string battery, IEnumerable<string> ids) =>
        new() { Kind = RecordKind.Manifest, Battery = battery, Ids = ids.ToArray() };

    public static TestRecord Result(string battery, string id, TestStatus status, string? detail) =>
        new() { Kind = RecordKind.Result, Battery = battery, Id = id, Status = status, Detail = detail };

    public static TestRecord Done(string battery) =>
        new() { Kind = RecordKind.Done, Battery = battery };
}
