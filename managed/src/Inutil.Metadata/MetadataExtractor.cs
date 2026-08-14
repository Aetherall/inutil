using System.IO;
using System.Linq;
using Mono.Cecil;
using Inutil.Schema;
using Inutil.InteropPatch;

namespace Inutil.Metadata;

// One extract outcome — the wire-map's shape, for the log and for tests to assert against. Like InteropPatch's
// DirectoryPatchResult: a structured result, not a bool. `Changed` is false on an idempotent re-run (identical
// inputs -> byte-identical wire-map -> no write).
public sealed record MetadataExtractResult(
    int TypesScanned,
    int MembersClassified,
    IReadOnlyList<string> Warnings,
    int WireMapTypesEmitted,            // serialized types in the wire-map
    string WireMapPath,                 // the inutil.wiremap.json path
    bool Changed);                      // did the wire-map change?

// The extract STAGE (docs/contribution/architecture/16-metadata.md): produce an attributed stub (Cpp2IL) ->
// project each member (CecilMemberRef) -> classify (WireRegistry) into ONE normalized WireModel -> emit the
// recovered wire map as a single `inutil.wiremap.json` sidecar — the tooling wire-protocol map InteropPatch/
// WireMap.cs reads at patch time, keyed by REAL il2cpp class names (the Il2Cpp proxy prefix is bridged on read).
// It carries every serialized type's members (wire name + converter kind + persisted + nullability) plus
// the per-TYPE converter kinds. Same discipline as InteropPatcher.PatchDirectory: structured result, ATOMIC write
// (temp + rename), IDEMPOTENT re-run (byte-identical sidecar -> no write), fail-loud warnings (never a silent skip).
public sealed class MetadataExtractor
{
    readonly WireRegistry _registry;

    public MetadataExtractor(WireRegistry? registry = null) => _registry = registry ?? WireFamilies.Default();

    // Full pipeline: produce the stub via Cpp2IL into a temp dir, extract the wire map, stamp its marker, clean up.
    public MetadataExtractResult Extract(string gameAssemblyDll, string globalMetadataDat, string wireMapPath, TextWriter? log = null)
    {
        string stubDir = Path.Combine(Path.GetTempPath(), "inutil-stub-" + Guid.NewGuid().ToString("N"));
        try
        {
            new Cpp2IlStubProducer().Produce(gameAssemblyDll, globalMetadataDat, stubDir, log);
            MetadataExtractResult result = ExtractFromStub(stubDir, wireMapPath, log);
            StampMarker(gameAssemblyDll, globalMetadataDat, result.WireMapPath, log);
            return result;
        }
        finally
        {
            try { Directory.Delete(stubDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // Stamp the wire-map's content-addressed marker beside it — the SAME ContentMarker the interop patcher stamps
    // on the proxies (docs/contribution/architecture/16-metadata.md). The address folds the WIRE-registry
    // structure (which recognizers ran) with the two inputs' content hashes (which game build), so "is this sidecar
    // current for this build?" is a structural check. No runtime CONSUMER of the verdict yet; we stamp now so the
    // artifact carries its provenance. Only the full `Extract` stamps; `ExtractFromStub` (test/reuse path) does not.
    void StampMarker(string gameAssemblyDll, string globalMetadataDat, string artifactPath, TextWriter? log)
    {
        string address = SchemaMarker.Sha256Hex(
            SchemaMarker.WireHash(_registry) + "\n" +
            SchemaMarker.Sha256HexOfFile(gameAssemblyDll) + "\n" +
            SchemaMarker.Sha256HexOfFile(globalMetadataDat));
        string dir = Path.GetDirectoryName(artifactPath) ?? ".";
        string markerName = Path.GetFileName(artifactPath) + SchemaMarker.SidecarMarkerSuffix;
        bool changed = ContentMarker.Stamp(dir, markerName, address, MarkerNote);
        log?.WriteLine($"   marker {(changed ? "stamped" : "current")}: {markerName} = {address}");
    }

    const string MarkerNote =
        "This wire-map was extracted by inutil from the game's il2cpp metadata (docs/contribution/architecture/16-metadata.md). The hash\n" +
        "content-addresses the wire recognizers + the GameAssembly.dll/global-metadata.dat that produced it;\n" +
        "regenerate with: inutil-metadata-extract --game <gameDir>.";

    // Extract from an already-produced stub dir (the Cecil + classify + emit half; no Cpp2IL). Separated so it is
    // testable against a synthetic attributed assembly with no game, and so a caller can reuse one produced stub.
    public MetadataExtractResult ExtractFromStub(string stubDir, string wireMapPath, TextWriter? log = null)
    {
        // ONE shared resolver with the WHOLE stub dir on its search path. A Cpp2IL stub's attribute blob routinely
        // references a type in ANOTHER stub assembly (a [JsonConverter]/enum ctor arg whose enum lives in a different
        // DLL, a [Nullable] byte[] element in the BCL); reading that ctor arg makes Mono.Cecil Resolve() the type.
        // With no shared resolver each module's default resolver did NOT know stubDir, so a cross-assembly enum threw
        // ResolutionException and aborted the whole extraction — green on single-assembly ToyGame, red at EFT scale.
        // Framework stubs are skipped from the WALK (BuildModel) but stay on disk, so cross-assembly refs into
        // mscorlib/System/Il2Cpp* still resolve here.
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(stubDir);
        var readerParameters = new ReaderParameters { InMemory = true, AssemblyResolver = resolver };

        var modules = new List<ModuleDefinition>();
        try
        {
            foreach (string dll in Directory.EnumerateFiles(stubDir, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (IsFramework(Path.GetFileNameWithoutExtension(dll))) continue;
                try { modules.Add(ModuleDefinition.ReadModule(dll, readerParameters)); }
                catch { /* a non-.NET / corrupt stub DLL — skip, never a hard failure */ }
            }
            return WriteWireMap(BuildModel(modules), wireMapPath, log);
        }
        finally
        {
            foreach (ModuleDefinition m in modules) m.Dispose();
            resolver.Dispose();
        }
    }

    // The testable core: classify every field/property (and each type's OWN converter) across the modules into the
    // normalized WireModel. Pure over the projected views — no I/O, no Cpp2IL — so it is provable against a synthetic
    // in-memory Cecil module with no game. Retains EVERY game member (the emitter filters): faithful normalization.
    public WireModel BuildModel(IEnumerable<ModuleDefinition> modules)
    {
        var types = new List<WireModelType>();
        var warnings = new List<string>();
        int typesScanned = 0, membersClassified = 0;

        foreach (ModuleDefinition module in modules)
            foreach (TypeDefinition type in module.GetTypes())
            {
                if (type.Name == "<Module>") continue;
                typesScanned++;

                var members = new List<WireModelMember>();
                foreach (FieldDefinition f in type.Fields)
                    members.Add(ClassifyMember(CecilMemberRef.OfField(f), warnings, ref membersClassified));
                foreach (PropertyDefinition p in type.Properties)
                    members.Add(ClassifyMember(CecilMemberRef.OfProperty(p), warnings, ref membersClassified));

                // The type's OWN wire KIND -> its per-type `typeKinds` row (WireMap.KindOf). The type-level
                // [JsonConverter] verdict ALONE misses the two families that dominate a real game, so fold in the
                // STRUCTURAL signals the converter name can't carry — see ResolveTypeKind.
                WireClassification tc = _registry.Classify(CecilMemberRef.OfType(type));
                warnings.AddRange(tc.Warnings);
                string? typeKind = ResolveTypeKind(type, tc.Facts.FirstOrDefault(f => f.Kind == FactKind.Converter));

                types.Add(new WireModelType(type.FullName, members, typeKind));
            }

        return new WireModel(types, warnings, typesScanned, membersClassified);
    }

    WireModelMember ClassifyMember(IMemberRef member, List<string> warnings, ref int membersClassified)
    {
        membersClassified++;
        WireClassification wc = _registry.Classify(member);
        warnings.AddRange(wc.Warnings);   // a matched-but-malformed attribute surfaces here, never silently dropped
        return new WireModelMember(member.Name, member.MemberType.FullName, wc.Facts);
    }

    internal static string KindStr(ConverterKind k) => k switch
    {
        ConverterKind.String => "string",
        ConverterKind.Enum   => "enum",
        _                    => "opaque",
    };

    // The per-TYPE wire kind emitted as `typeKinds` (the `@type` capability). A type-level [JsonConverter] classified
    // by its converter NAME (StringEnumConverter -> enum, *StringConverter -> string) is the tested baseline, but on a
    // real game two families carry NO recognizable converter attribute (the converter name alone recovered ZERO EFT
    // enum kinds and mis-typed MongoID), so fold in the STRUCTURAL signals:
    //   - a non-[Flags] ENUM serializes BY NAME via a GLOBAL StringEnumConverter — no per-type attribute, so the
    //     enum-ness of the TYPE is the signal. [Flags] enums serialize as an int and stay structural (null).
    //   - a value-type STRUCT carrying a [JsonConverter] we didn't name-recognize is the MongoID family: a bare wire
    //     STRING, not a nested object — so an otherwise-`opaque` value type degrades to `string`.
    // Null = no converter kind (the structural proxy shape — the ToyGame case, where member name == JSON key).
    static string? ResolveTypeKind(TypeDefinition type, WireFact? typeConverter)
    {
        if (type.IsEnum) return HasFlags(type) ? null : KindStr(ConverterKind.Enum);
        if (typeConverter is null) return null;
        ConverterKind kind = typeConverter.ConverterKind;
        if (kind == ConverterKind.Opaque && type.IsValueType) kind = ConverterKind.String;   // value-type struct + [JsonConverter] = MongoID -> bare string
        return KindStr(kind);
    }

    // [Flags] enums serialize as an int, not by name, so they are NOT tagged `enum`. The one spot outside WireFamilies
    // that reads an attribute name, deliberately: [Flags] is a STRUCTURAL discriminator (bitfield?), not a WIRE
    // recognizer — it never belongs in the wire registry. A plain no-arg marker: presence is the whole fact.
    static bool HasFlags(TypeDefinition type) =>
        type.HasCustomAttributes && type.CustomAttributes.Any(a => a.AttributeType.FullName == "System.FlagsAttribute");

    MetadataExtractResult WriteWireMap(WireModel model, string wireMapPath, TextWriter? log)
    {
        string content = WireMapEmitter.EmitJson(model);
        bool changed = !File.Exists(wireMapPath) || File.ReadAllText(wireMapPath) != content;
        if (changed) AtomicWriteText(wireMapPath, content);
        int wmTypes = model.SerializedClosure().Count;   // marker-bearing DTOs + the marker-less types reachable from them

        foreach (string w in model.Warnings) log?.WriteLine($"   !! {w}");
        log?.WriteLine($"   extract: {model.TypesScanned} types, {model.MembersClassified} members -> "
            + $"{wmTypes} mapped type(s), {model.Warnings.Count} warning(s){(changed ? "" : " (unchanged)")}");

        return new MetadataExtractResult(model.TypesScanned, model.MembersClassified, model.Warnings, wmTypes, wireMapPath, changed);
    }

    // Write to a temp file beside the target, then rename over it (atomic on one filesystem) — mirrors
    // InteropPatcher.AtomicWrite: a mid-write failure never leaves a torn sidecar.
    static void AtomicWriteText(string targetPath, string content)
    {
        string dir = Path.GetDirectoryName(targetPath) ?? ".";
        Directory.CreateDirectory(dir);
        string tmp = targetPath + ".inutil-tmp";
        try
        {
            File.WriteAllText(tmp, content);
            File.Move(tmp, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    // Framework stub assemblies carry no authored wire attributes and are large (mscorlib alone is ~2MB); skip them
    // so the walk stays fast and the sidecar stays game-focused. Game DTOs live in Assembly-CSharp + game-named DLLs.
    static bool IsFramework(string assemblyName) =>
        assemblyName is "mscorlib" or "netstandard" or "Mono.Security"
        || assemblyName.StartsWith("System", StringComparison.Ordinal)
        || assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal)
        || assemblyName.StartsWith("Unity.", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal)
        || assemblyName.StartsWith("Il2Cpp", StringComparison.Ordinal);
}
