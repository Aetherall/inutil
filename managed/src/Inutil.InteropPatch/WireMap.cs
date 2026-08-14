using System.Text.Json;

namespace Inutil.InteropPatch;

// The recovered wire map (inutil.wiremap.json) as the interop-patch DISK pass consumes it: a proxy type's il2cpp
// name -> (member name -> wire name). The metadata pillar (Inutil.Metadata,
// docs/contribution/architecture/16-metadata.md) PRODUCES this sidecar offline via Cpp2IL; this reads it as a
// plain DATA artifact with System.Text.Json — there is NO project edge to Inutil.Metadata or Cpp2IL, so the
// runtime-isolation invariant is untouched. A missing/malformed file returns null -> the pass is a clean no-op (a
// game with no wiremap keeps member-name serialization; degrade, never fail).
public sealed class WireMap
{
    readonly Dictionary<string, Dictionary<string, string>> _members;   // type il2cpp-name -> (member -> wire)
    readonly Dictionary<string, string> _typeKinds;                     // type il2cpp-name -> converter kind

    WireMap(Dictionary<string, Dictionary<string, string>> members, Dictionary<string, string> typeKinds)
    {
        _members = members;
        _typeKinds = typeKinds;
    }

    public int TypeCount => _members.Count;

    // A type's converter KIND ("string" / "enum" / "opaque"), or null. This is the per-TYPE serialization shape
    // the game author's [JsonConverter] implied (a MongoID is always a bare string) — recovered into typeKinds.
    // Il2Cpp-prefix stripped, same bridge as ForType.
    public string? KindOf(string typeFullName)
    {
        if (_typeKinds.TryGetValue(typeFullName, out string? k)) return k;
        const string prefix = "Il2Cpp";
        if (typeFullName.StartsWith(prefix, StringComparison.Ordinal)
            && _typeKinds.TryGetValue(typeFullName.Substring(prefix.Length), out string? k2)) return k2;
        return null;
    }

    // A proxy type's member->wire table, or null if the type is not a recovered serialized DTO. Strips an
    // optional leading Il2Cpp (MelonLoader prefixes the game proxy namespace; BepInEx does not) so BOTH loader
    // spellings key the SAME recovered map — the one reconciled bridge.
    public IReadOnlyDictionary<string, string>? ForType(string typeFullName)
    {
        if (_members.TryGetValue(typeFullName, out var m)) return m;
        const string prefix = "Il2Cpp";
        if (typeFullName.StartsWith(prefix, StringComparison.Ordinal)
            && _members.TryGetValue(typeFullName.Substring(prefix.Length), out var m2)) return m2;
        return null;
    }

    // Parse inutil.wiremap.json; null if absent or shape-invalid (the pass then no-ops, never throws).
    public static WireMap? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("types", out JsonElement types) || types.ValueKind != JsonValueKind.Object)
                return null;

            var members = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (JsonProperty t in types.EnumerateObject())
            {
                if (!t.Value.TryGetProperty("members", out JsonElement mem) || mem.ValueKind != JsonValueKind.Array)
                    continue;
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (JsonElement m in mem.EnumerateArray())
                {
                    string? name = m.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    string? wire = m.TryGetProperty("wire", out JsonElement w) ? w.GetString() : null;
                    if (name is not null && wire is not null) map[name] = wire;
                }
                members[t.Name] = map;
            }

            var typeKinds = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("typeKinds", out JsonElement kinds) && kinds.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty k in kinds.EnumerateObject())
                    if (k.Value.ValueKind == JsonValueKind.String) typeKinds[k.Name] = k.Value.GetString()!;

            return new WireMap(members, typeKinds);
        }
        catch (JsonException)
        {
            return null;   // a malformed sidecar degrades to member-name serialization, not a hard failure
        }
    }
}
