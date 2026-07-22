using System.Text.Json;
using Mono.Cecil;
using Inutil.Schema;
using Inutil.InteropPatch;
using Inutil.Metadata;
using Inutil.Metadata.Tests;

// Metadata pillar tests. The OFFLINE emit test (MetadataEmitTests) proves the inutil.wiremap.json shape over a
// synthetic attributed module (no game). The INTEGRATION test runs inutil's OWN Cpp2IL pass over the REAL ToyGame
// (metadata v31): produce an attributed stub, project + classify + emit the wire map, assert the produce->read->
// extract chain. Skipped gracefully when no game is provisioned.

Console.WriteLine(">> offline wire-map emit (§5.2/§6)");
int failures = MetadataEmitTests.Run();

string? gameDir = FindGameDir();
GameLayout? layout = gameDir is null ? null : GameLocator.Locate(gameDir);
if (layout?.GameAssemblyDll is null || layout.GlobalMetadataDat is null)
{
    if (failures > 0) { Console.Error.WriteLine($"\nMETADATA TESTS RED — {failures} offline failure(s) (integration skipped: no game)."); return 1; }
    Console.WriteLine("\n>> no provisioned game — integration SKIPPED (offline emit test green; run setup-bepinex to gate the Cpp2IL pipeline)");
    return 0;
}

Console.WriteLine($"\n>> game: {layout.GameDir} ({layout.Loader})");
string work = Path.Combine(Path.GetTempPath(), "inutil-metadata-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]" + (ok || detail is null ? "" : $"  -- {detail}"));
    if (!ok) failures++;
}
try
{
    string stubDir = Path.Combine(work, "stub");
    new Cpp2IlStubProducer().Produce(layout.GameAssemblyDll, layout.GlobalMetadataDat, stubDir, Console.Out);

    string stub = Path.Combine(stubDir, "Assembly-CSharp.dll");
    Check("Cpp2IL produced Assembly-CSharp.dll stub from v31 metadata", File.Exists(stub));

    // produce -> Cecil re-read -> projection reads ToyGame.Loadout.Gold + its Cpp2IL-injected attributes
    ModuleDefinition module = AssemblyDefinition.ReadAssembly(stub).MainModule;
    FieldDefinition? gold = module.GetTypes().FirstOrDefault(t => t.FullName == "ToyGame.Loadout")?.Fields.FirstOrDefault(f => f.Name == "Gold");
    Check("stub carries ToyGame.Loadout.Gold (produce -> re-read)", gold is not null);
    if (gold is not null)
    {
        IMemberRef mr = CecilMemberRef.OfField(gold);
        Check("projection reads name/type (Gold : System.Int32)", mr.Name == "Gold" && mr.MemberType.FullName == "System.Int32");
        Check("Cpp2IL-injected attributes present (attribute pipeline intact)", mr.Attributes.Count > 0);
    }

    // the extract stage: stub -> classify all -> inutil.wiremap.json. ToyGame carries the attributed WireProfile
    // fixture: Handle -> "Nickname" (wire name) and Side -> "faction" (wire name + enum converter). The extract must
    // recover exactly those authored names the il2cpp proxy STRIPS — the pillar's core payoff.
    string wireMap = Path.Combine(work, "inutil.wiremap.json");
    MetadataExtractResult r = new MetadataExtractor().ExtractFromStub(stubDir, wireMap, Console.Out);
    Check("wire-map written", File.Exists(wireMap));
    Check("scanned ToyGame types (walk reached the game assembly)", r.TypesScanned > 0);
    Check("wire-map has >= 1 serialized type (the WireProfile fixture)", r.WireMapTypesEmitted >= 1);
    Check("no warnings (nothing malformed)", r.Warnings.Count == 0);
    Check("first extract reports Changed", r.Changed);

    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(wireMap));
    JsonElement types = doc.RootElement.GetProperty("types");
    Check("wire-map includes ToyGame.WireProfile", types.TryGetProperty("ToyGame.WireProfile", out JsonElement wp), wireMap);
    if (types.TryGetProperty("ToyGame.WireProfile", out wp))
    {
        JsonElement mems = wp.GetProperty("members");
        Check("recovers WireProfile.Handle -> Nickname (wire name the proxy strips)", Wire(mems, "Handle") == "Nickname");
        Check("recovers WireProfile.Side -> faction + enum converter (wire name + converter kind)",
            Wire(mems, "Side") == "faction" && Kind(mems, "Side") == "enum");
    }

    // idempotent: a second extract over the same stub writes a byte-identical wire-map -> nothing changed.
    MetadataExtractResult r2 = new MetadataExtractor().ExtractFromStub(stubDir, wireMap, Console.Out);
    Check("idempotent re-run -> Changed == false (byte-identical wire-map)", !r2.Changed);

    Console.WriteLine(failures == 0
        ? "\nMETADATA TESTS GREEN — inutil's own Cpp2IL pass -> attributed stub -> classified inutil.wiremap.json, idempotent."
        : $"\nMETADATA TESTS RED — {failures} wrong.");
    return failures == 0 ? 0 : 1;
}
finally
{
    try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
}

// A member's "wire" / converter "kind" from a type's "members" array (keyed by "name").
static string? Wire(JsonElement members, string name) => Find(members, name)?.GetProperty("wire").GetString();
static string? Kind(JsonElement members, string name) =>
    Find(members, name) is { } m && m.TryGetProperty("converter", out JsonElement c) ? c.GetProperty("kind").GetString() : null;
static JsonElement? Find(JsonElement members, string name)
{
    foreach (JsonElement m in members.EnumerateArray())
        if (m.GetProperty("name").GetString() == name) return m;
    return null;
}

// Walk up to .unity-build, then the bepinex loader tree (the provisioned game dir --game would target).
static string? FindGameDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".unity-build"))) dir = dir.Parent;
    if (dir is null) return null;
    string game = Path.Combine(dir.FullName, ".unity-build", "loaders", "bepinex");
    return Directory.Exists(game) ? game : null;
}
