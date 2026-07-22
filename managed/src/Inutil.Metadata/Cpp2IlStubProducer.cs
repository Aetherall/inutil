using System.IO;
using Cpp2IL.Core;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;

namespace Inutil.Metadata;

// Produces an attributed "DummyDll" stub from a game's GameAssembly.dll + global-metadata.dat, via Cpp2IL
// (docs/contribution/architecture/16-metadata.md). The ONE place Cpp2IL is invoked (the §7 quarantine). The
// loader's own first-launch Cpp2IL run feeds Il2CppInterop, which STRIPS the attributes into the shipped proxies;
// this is inutil's OWN pass, capturing the attributed stub the loader throws away. The stub is written to disk and
// re-read with Mono.Cecil across a filesystem boundary, so Cpp2IL's AsmResolver object model never enters the reader.
public sealed class Cpp2IlStubProducer
{
    static bool _pluginsInitialized;

    // Produce the stub assemblies under outDir and return it. The Unity version is read from
    // <Game>_Data/globalgamemanagers (derived from the metadata path). Fails LOUD (throws) on a parse failure —
    // e.g. an unsupported metadata version — rather than emitting a partial/empty stub; the caller surfaces it.
    public string Produce(string gameAssemblyDll, string globalMetadataDat, string outDir, TextWriter? log = null)
    {
        log ??= TextWriter.Null;

        // Init registers the built-in instruction sets / output formats / processing layers. Once per process.
        if (!_pluginsInitialized)
        {
            Cpp2IlApi.Init();
            _pluginsInitialized = true;
        }

        string ggm = GlobalGameManagersPath(globalMetadataDat);
        var unityVersion = Cpp2IlApi.GetVersionFromGlobalGameManagers(File.ReadAllBytes(ggm));
        log.WriteLine($"   cpp2il: unity {unityVersion}, reading {Path.GetFileName(globalMetadataDat)}");

        Cpp2IlApi.InitializeLibCpp2Il(gameAssemblyDll, globalMetadataDat, unityVersion, allowUserToInputAddresses: false);
        try
        {
            ApplicationAnalysisContext ctx = Cpp2IlApi.CurrentAppContext
                ?? throw new InvalidOperationException("Cpp2IL produced no application context");

            // Recover + inject the authored attributes onto the model (the residue Il2CppInterop strips).
            new AttributeInjectorProcessingLayer().Process(ctx, null!);

            Directory.CreateDirectory(outDir);
            new AsmResolverDllOutputFormatDefault().DoOutput(ctx, outDir);

            int dllCount = Directory.GetFiles(outDir, "*.dll", SearchOption.AllDirectories).Length;
            log.WriteLine($"   cpp2il: {ctx.Assemblies.Count} assemblies -> {dllCount} stub DLL(s) under {outDir}");
            return outDir;
        }
        finally
        {
            // Clear LibCpp2Il's per-game state so a subsequent Produce (another game / re-run) starts clean.
            Cpp2IlApi.ResetInternalState();
        }
    }

    // <Game>_Data/globalgamemanagers, derived from the located metadata path:
    //   <Game>_Data/il2cpp_data/Metadata/global-metadata.dat  ->  <Game>_Data/globalgamemanagers
    static string GlobalGameManagersPath(string globalMetadataDat)
    {
        string metadataDir = Path.GetDirectoryName(globalMetadataDat) ?? "";   // .../Metadata
        string il2cppData = Path.GetDirectoryName(metadataDir) ?? "";          // .../il2cpp_data
        string dataDir = Path.GetDirectoryName(il2cppData)
            ?? throw new DirectoryNotFoundException($"cannot derive <Game>_Data from metadata path {globalMetadataDat}");
        return Path.Combine(dataDir, "globalgamemanagers");
    }
}
