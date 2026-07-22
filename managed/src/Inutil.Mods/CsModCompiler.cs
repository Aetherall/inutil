// CsModCompiler — the file-set, in-process C# compiler behind inutil's no-build .cs mod loop (§7.9).
//
// Takes a mod's *.cs sources + the on-disk reference DIRECTORIES and emits a real assembly (PE + PDB bytes) the
// collectible ModContext loads. Pure managed CPU work — no il2cpp call — so it runs OFF the game thread
// (CsModHost compiles on a worker, posts only the load/wire to the main thread).
//
// Reference strategy points at the same on-disk dirs the loader has loaded, so a clean compile here equals a
// clean load:
//   <gameroot>/dotnet      the .NET framework impl assemblies (the BCL)
//   BepInEx/interop        the loader's Il2CppInterop game proxies (Assembly-CSharp.*, UnityEngine.*, Il2Cpp*)
//   BepInEx/core           Il2CppInterop + BepInEx
//   plugins (Inutil.dll)   the SDK: Hook<T>, Hooks, the lifecycle interfaces
// De-duped by simple name (first dir wins), native PEs skipped (no ECMA metadata), RuntimeDir appended last as
// a guaranteed BCL fallback.
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Inutil;

public static class CsModCompiler
{
    public sealed class Result
    {
        public byte[]? Pe;
        public byte[]? Pdb;
        public string[] Errors = Array.Empty<string>();
        public string[] Warnings = Array.Empty<string>();
        public int RefCount;
        public bool Ok => Pe != null && Errors.Length == 0;
    }

    // Compile `csFiles` into an assembly named `asmName`, referencing every managed DLL found across `refDirs`
    // (scanned in order; first occurrence of a simple name wins). The reference set is cached on the dir list +
    // each dir's last-write time, so a steady-state recompile reuses the MetadataReferences.
    public static Result Compile(string asmName, IReadOnlyList<string> csFiles, IReadOnlyList<string> refDirs)
    {
        if (csFiles is null || csFiles.Count == 0)
            return new Result { Errors = new[] { "no .cs source files" } };

        List<MetadataReference> refs = References(refDirs);

        var trees = new List<SyntaxTree>(csFiles.Count);
        var parseOpts = new CSharpParseOptions(LanguageVersion.Latest);
        foreach (string f in csFiles)
        {
            string text;
            try { text = File.ReadAllText(f); }
            catch (Exception ex) { return new Result { Errors = new[] { $"read {Path.GetFileName(f)}: {ex.Message}" }, RefCount = refs.Count }; }
            // Encode the SourceText (UTF-8): emitting a PDB writes a per-file source checksum, which Roslyn
            // refuses for a text with no encoding (CS8055 "Cannot emit debug information…without encoding").
            trees.Add(CSharpSyntaxTree.ParseText(SourceText.From(text, System.Text.Encoding.UTF8), parseOpts, path: f));
        }

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,   // faithful hot-reload stack traces + a PDB
            allowUnsafe: true,
            // il2cpp interop proxies are built against a NEWER System.Private.CoreLib than the game's runtime, which
            // the .NET loader unifies at load (proxies + engine hooks bind fine at runtime). Roslyn's strict
            // compile-time ref-version check would otherwise reject any mod that touches a proxy (CS1705) or warn on
            // the unification (CS1701/CS1702). Suppress the three — runtime binding is correct.
            specificDiagnosticOptions: new[]
            {
                new KeyValuePair<string, ReportDiagnostic>("CS1701", ReportDiagnostic.Suppress),
                new KeyValuePair<string, ReportDiagnostic>("CS1702", ReportDiagnostic.Suppress),
                new KeyValuePair<string, ReportDiagnostic>("CS1705", ReportDiagnostic.Suppress),
            });

        CSharpCompilation comp = CSharpCompilation.Create(asmName, trees, refs, options);

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        EmitResult emit = comp.Emit(peStream, pdbStream);

        string[] errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(Fmt).ToArray();
        string[] warnings = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).Select(Fmt).Take(20).ToArray();

        if (!emit.Success || errors.Length > 0)
            return new Result
            {
                Errors = errors.Length > 0 ? errors : new[] { "emit failed with no error diagnostics" },
                Warnings = warnings, RefCount = refs.Count,
            };

        return new Result { Pe = peStream.ToArray(), Pdb = pdbStream.ToArray(), Warnings = warnings, RefCount = refs.Count };
    }

    static readonly object _gate = new();
    static List<MetadataReference>? _refs;
    static string _refKey = "";

    static List<MetadataReference> References(IReadOnlyList<string> refDirs)
    {
        var dirs = new List<string>(refDirs ?? Array.Empty<string>());
        string? rt = RuntimeDir();
        if (rt != null) dirs.Add(rt);   // guaranteed BCL source, appended LAST (a present dotnet/ still wins by name)

        string key = string.Join("|", dirs.Select(d =>
        {
            try { return d + ":" + Directory.GetLastWriteTimeUtc(d).Ticks; } catch { return d + ":0"; }
        }));

        lock (_gate)
        {
            if (_refs != null && key == _refKey) return _refs;
            var refs = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (string dll in Directory.EnumerateFiles(dir, "*.dll"))
                {
                    string name = Path.GetFileNameWithoutExtension(dll);
                    if (!seen.Add(name)) continue;        // first dir scanned wins
                    if (!IsManaged(dll)) continue;         // skip a native PE (no ECMA metadata) — errors at add otherwise
                    try { refs.Add(MetadataReference.CreateFromFile(dll)); } catch { }
                }
            }
            _refs = refs; _refKey = key;
            return refs;
        }
    }

    static bool IsManaged(string dll)
    {
        try { using var fs = File.OpenRead(dll); using var pe = new PEReader(fs); return pe.HasMetadata; }
        catch { return false; }
    }

    static string? RuntimeDir()
    {
        try { string loc = typeof(object).Assembly.Location; if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc); }
        catch { }
        try { return System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(); }
        catch { return null; }
    }

    // "CSid message @ file:line" — the canonical, locale-independent diagnostic display.
    static string Fmt(Diagnostic d)
    {
        FileLinePositionSpan span = d.Location.GetLineSpan();
        string loc = span.IsValid ? $"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}" : "";
        return $"{d.Id} {d.GetMessage()} @ {loc}";
    }
}
