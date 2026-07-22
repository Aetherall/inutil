using System.Linq;
using Mono.Cecil;

namespace Inutil.InteropPatch;

// The DISK-ONLY wire-attribute pass: re-attach the [JsonProperty] a game author wrote and Il2CppInterop STRIPPED
// — recovered into inutil.wiremap.json — back onto the proxy members as System.Text.Json [JsonPropertyName], so
// a STANDARD managed serializer (System.Text.Json + the opt-in Inutil.Wire converter) round-trips the game's OWN
// proxy by its WIRE names, with no twin and no per-game Newtonsoft shim.
//
// WHY DISK-ONLY, not in the preloader-shared PatchModule: the boot path needs the natural-typing SIGNATURE flips to
// LOAD the game; wire attributes matter only at SERIALIZE time, never at load. Keeping this out of PatchModule leaves
// the preloader untouched and localizes the wiremap dependency to the offline CLI.
//
// Game-agnostic (keyed purely by recovered il2cpp metadata), idempotent (a member already carrying the attribute is
// skipped, so a re-run stamps 0), and safe (a member absent from the wiremap is left exactly as-is).
public sealed class WireAttributeRewriter
{
    // Both loaders host a .NET 6 CoreCLR (Inutil.csproj) — System.Text.Json ships in that shared framework at
    // assembly version 6.0.0.0 / token cc7b13ffcd2ddd51. The stamped attribute must RESOLVE there at runtime, so
    // the reference is written to that identity (JsonPropertyNameAttribute itself exists since STJ 3.0; it is the
    // contract-customization RESOLVER that is net7+, which is why Inutil.Wire uses a JsonConverter, not a resolver).
    static readonly Version StjVersion = new(6, 0, 0, 0);
    static readonly byte[] StjToken = { 0xcc, 0x7b, 0x13, 0xff, 0xcd, 0x2d, 0xdd, 0x51 };
    const string AttrNamespace = "System.Text.Json.Serialization";
    const string AttrTypeName = "JsonPropertyNameAttribute";

    // Stamp [JsonPropertyName("<wire>")] onto every proxy member the wiremap names. Returns the count stamped
    // (0 => module unchanged, so the directory driver skips the atomic write).
    public int Stamp(ModuleDefinition module, WireMap map)
    {
        MethodReference? ctor = null;       // built once, lazily — an untouched module never references System.Text.Json
        MethodReference? kindCtor = null;   // ditto for [Inutil.WireKind]
        int stamped = 0;
        foreach (TypeDefinition type in module.GetTypes())
        {
            // Converter KIND on the TYPE (typeKinds): a "string"-kind type (e.g. MongoID) serializes as a bare string
            // wherever it appears, so the mark goes on the type, not each member. Enums are covered by the runtime's
            // global JsonStringEnumConverter, so they are NOT stamped (the mark is only for the handful the BCL can't infer).
            string? kind = map.KindOf(type.FullName);
            if (kind == "string") stamped += AttachKind(module, type, kind, ref kindCtor);

            IReadOnlyDictionary<string, string>? members = map.ForType(type.FullName);
            if (members is null) continue;
            foreach (PropertyDefinition p in type.Properties)
                if (members.TryGetValue(p.Name, out string? wire)) stamped += Attach(module, p, wire, ref ctor);
            foreach (FieldDefinition f in type.Fields)
                if (members.TryGetValue(f.Name, out string? wire)) stamped += Attach(module, f, wire, ref ctor);
        }
        return stamped;
    }

    static int AttachKind(ModuleDefinition module, TypeDefinition type, string kind, ref MethodReference? ctor)
    {
        if (type.CustomAttributes.Any(a => a.AttributeType.Name == "WireKindAttribute")) return 0;   // idempotent
        ctor ??= BuildWireKindCtor(module);
        var attr = new CustomAttribute(ctor);
        attr.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, kind));
        type.CustomAttributes.Add(attr);
        return 1;
    }

    static MethodReference BuildWireKindCtor(ModuleDefinition module)
    {
        // Inutil.WireKindAttribute lives in the engine (Inutil.dll), which the patched proxies already reference
        // (natural-typing splices call Inutil.Marshal.Il2CppMarshal). Reuse that reference if present; else add one
        // by simple name — Inutil is not strong-named, so the loader binds it by name at runtime.
        AssemblyNameReference inutil = module.AssemblyReferences.FirstOrDefault(a => a.Name == "Inutil")
                                       ?? AddInutilReference(module);
        var attrType = new TypeReference("Inutil", "WireKindAttribute", module, inutil);
        var ctor = new MethodReference(".ctor", module.TypeSystem.Void, attrType) { HasThis = true };
        ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        return ctor;
    }

    static AssemblyNameReference AddInutilReference(ModuleDefinition module)
    {
        var reference = new AssemblyNameReference("Inutil", new Version(0, 0, 0, 0));
        module.AssemblyReferences.Add(reference);
        return reference;
    }

    static int Attach(ModuleDefinition module, ICustomAttributeProvider member, string wire, ref MethodReference? ctor)
    {
        if (member.CustomAttributes.Any(a => a.AttributeType.Name == AttrTypeName)) return 0;   // idempotent
        ctor ??= BuildCtor(module);
        var attr = new CustomAttribute(ctor);
        attr.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, wire));
        member.CustomAttributes.Add(attr);
        return 1;
    }

    static MethodReference BuildCtor(ModuleDefinition module)
    {
        AssemblyNameReference stj = module.AssemblyReferences.FirstOrDefault(a => a.Name == "System.Text.Json")
                                    ?? AddStjReference(module);
        var attrType = new TypeReference(AttrNamespace, AttrTypeName, module, stj);
        var ctor = new MethodReference(".ctor", module.TypeSystem.Void, attrType) { HasThis = true };
        ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        return ctor;
    }

    static AssemblyNameReference AddStjReference(ModuleDefinition module)
    {
        var reference = new AssemblyNameReference("System.Text.Json", StjVersion) { PublicKeyToken = StjToken };
        module.AssemblyReferences.Add(reference);
        return reference;
    }
}
