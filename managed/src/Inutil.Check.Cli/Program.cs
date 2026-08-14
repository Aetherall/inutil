// inutil-check — the offline inutil dev-check CLI (ships in the bundle tools/). Three verbs:
//
//   check <bepDir> <modDir> [asmName]   compile a mod's .cs EXACTLY as inutil's in-process CsModCompiler does —
//                                       not a port of it: it loads Inutil.Mods.dll from the CHECKED
//                                       target tree's own plugins/ and calls the one Compile the game host calls,
//                                       so the offline gate and the hot-reload loop cannot drift (they are the
//                                       same code, same Roslyn closure, same options — the old hand-port had
//                                       already drifted on CS1701/02/05 suppression and CS8785 elevation).
//                                       Prints "CSid message @ file:line" per diagnostic.
//                                       Exit 0 = clean · 1 = compile error(s) · 2 = setup.
//   query <interopDir> <Type> [max]     reverse-index one type (ctors/producers/consumers). Prints the index +
//                                       a "RESULT: surface-query ..." line. Exit 0 = found · 1 = notfound/ambiguous.
//   methods <interopDir> <Type> [max]   the type's OWN declared surface — the exact method names + parameter
//                                       types a Hook<T> must spell (incl. get_/set_ accessors + mangled
//                                       interface impls under their real hookable names) + the base chain.
//                                       "RESULT: surface-methods ..." line. Exit 0 = found · 1 = notfound/ambiguous.
//   dump  <interopDir> <outPath>        write the whole grep catalog (reverse-index.txt). Exit 0.
//
// (The `wire`/`lens`/`lens-usage` verbs that used to live here — the wire-mutation debt ledger and the
// "wire JSON only through Lens" gates — retired with Lens/LensGen; see git history if you need them back.)

using System.Reflection;
using System.Reflection.PortableExecutable;
using Mono.Cecil;
using Inutil.Check;

return args.Length == 0 ? Usage() : args[0] switch
{
    "check"   => Check(args),
    "query"   => Query(args),
    "methods" => Methods(args),
    "dump"    => Dump(args),
    _         => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: inutil-check check <bepDir> <modDir> [asmName]");
    Console.Error.WriteLine("       inutil-check query <interopDir> <Type> [max]");
    Console.Error.WriteLine("       inutil-check methods <interopDir> <Type> [max]");
    Console.Error.WriteLine("       inutil-check dump  <interopDir> <outPath>");
    return 2;
}

// ---- check: compile a mod THROUGH the target tree's own CsModCompiler (the single implementation) -----------

static int Check(string[] args)
{
    if (args.Length < 3) return Usage();
    string bep = Path.GetFullPath(args[1]);
    string modDir = Path.GetFullPath(args[2]);
    string asmName = args.Length > 3 ? args[3] : new DirectoryInfo(modDir).Name;

    if (!Directory.Exists(modDir)) { Console.Error.WriteLine($"!! no mod dir: {modDir}"); return 2; }
    string interop = Path.Combine(bep, "interop");
    if (!Directory.Exists(interop)) { Console.Error.WriteLine($"!! no interop: {interop} — boot once with bepinex installed"); return 2; }

    // The SAME on-disk dirs the loader has loaded — so a clean compile equals a clean load (CsModHost).
    string gameRoot = Path.GetDirectoryName(bep.TrimEnd(Path.DirectorySeparatorChar))!;
    var refDirs = new[]
    {
        Path.Combine(gameRoot, "dotnet"),   // the .NET 6 BCL
        interop,                            // the loader's il2cpp game proxies
        Path.Combine(bep, "core"),          // Il2CppInterop + BepInEx
        Path.Combine(bep, "plugins"),       // Inutil.dll (Hook<T>/Hooks/lifecycle) + consumer.Sdk.dll
        Path.Combine(bep, "inutil-libs"),   // boot-emitted shared libraries (ModLibs)
    };
    // THE LIST ABOVE MUST MIRROR THE HOST'S, IN ORDER. "A clean check == a clean hot-reload" is only
    // structural if this CLI compiles against the same assemblies, resolved the same way, as
    // BepInExPlugin.StartCsModLoop hands CsModHost — CsModCompiler takes the FIRST occurrence of a
    // simple name, so a divergence in membership or in order changes which assembly wins.
    //
    // inutil-libs was missing, and that is not a cosmetic gap: a mod that references a shared library
    // (the ModLibs lifecycle exists precisely so mods CAN) compiles in-game and fails here with CS0246
    // on a type that is demonstrably loaded in the running process. The check was reporting a defect in
    // the mod when the defect was in the check.

    var csFiles = Directory.EnumerateFiles(modDir, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .OrderBy(f => f, StringComparer.Ordinal).ToList();
    if (csFiles.Count == 0) { Console.Error.WriteLine($"!! no .cs sources under {modDir}"); return 2; }

    // Bind the checked tree's OWN compiler. LoadFrom-context probing resolves Inutil.Mods.dll's deps
    // (Inutil.dll, Inutil.Schema.dll, the Microsoft.CodeAnalysis 4.3.0 closure) from the SAME plugins dir —
    // the exact assemblies the in-game host loads — so "a clean check == a clean hot-reload" is structural,
    // not a maintained promise. A missing dll / API skew is a loud setup error, never a silent fallback.
    CompileFn? compile = BindCsModCompiler(Path.Combine(bep, "plugins"), out string? bindErr);
    if (compile is null) { Console.Error.WriteLine(bindErr); return 2; }

    CompileOutcome r;
    try { r = compile(asmName, csFiles, refDirs); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"!! CsModCompiler.Compile threw {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
        return 2;
    }

    foreach (string w in r.Warnings) Console.WriteLine(w);   // already "CSid message @ file:line" (CsModCompiler.Fmt)
    foreach (string e in r.Errors) Console.WriteLine(e);

    if (r.Ok)
    {
        Console.Error.WriteLine($">> {asmName}: compiled clean (refs={r.RefCount}, {csFiles.Count} file(s))");
        return 0;
    }
    Console.Error.WriteLine($">> {asmName}: {r.Errors.Length} compile error(s) (refs={r.RefCount})");
    return 1;
}

// Late-bound bridge to Inutil.CsModCompiler.Compile — reflection rather than a compile-time reference so
// inutil-check builds anywhere (no target needed) yet always RUNS the target-staged engine build it is checking.
static CompileFn? BindCsModCompiler(string pluginsDir, out string? err)
{
    err = null;
    string dll = Path.Combine(pluginsDir, "Inutil.Mods.dll");
    if (!File.Exists(dll))
    {
        err = $"!! no {dll} — is this a booted inutil install? (check runs its own CsModCompiler, never a private copy)";
        return null;
    }
    Assembly asm;
    try { asm = Assembly.LoadFrom(dll); }
    catch (Exception ex) { err = $"!! load {dll}: {ex.GetType().Name}: {ex.Message}"; return null; }

    MethodInfo? m;
    try { m = asm.GetType("Inutil.CsModCompiler")?.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static); }
    catch (Exception ex) { err = $"!! resolve Inutil.CsModCompiler.Compile: {ex.GetType().Name}: {ex.Message}"; return null; }
    if (m is null)
    {
        err = "!! Inutil.CsModCompiler.Compile not found in the staged Inutil.Mods.dll — engine/otcheck contract skew (update tools/otcheck)";
        return null;
    }

    return (asmName, cs, refs) =>
    {
        object result = m.Invoke(null, new object?[] { asmName, cs.ToList(), refs.ToList() })!;
        Type rt = result.GetType();
        static T Field<T>(Type t, object o, string name) => (T)t.GetField(name)!.GetValue(o)!;
        return new CompileOutcome(
            (bool)rt.GetProperty("Ok")!.GetValue(result)!,
            Field<string[]>(rt, result, "Errors"),
            Field<string[]>(rt, result, "Warnings"),
            Field<int>(rt, result, "RefCount"));
    };
}

// ---- query / dump: the Cecil reverse-index over interop -------------------------------------------------

static int Query(string[] args)
{
    if (args.Length < 3) return Usage();
    string interop = Path.GetFullPath(args[1]);
    string type = args[2];
    int max = args.Length > 3 && int.TryParse(args[3], out var m) ? m : 80;

    var ix = BuildIndex(interop, out string? err);
    if (err != null) { Console.Error.WriteLine(err); return 2; }

    var q = ReverseIndex.Query(ix!, type, max);
    Console.Write(q.Text);
    if (q.Status == "ok")
    {
        Console.WriteLine($"RESULT: surface-query ok {Naming.SimpleName(q.Resolved!)} — {q.Ctors} ctor, {q.Prod} prod, {q.Cons} cons");
        return 0;
    }
    Console.WriteLine($"RESULT: surface-query {q.Status} {type}");
    return 1;
}

static int Dump(string[] args)
{
    if (args.Length < 3) return Usage();
    string interop = Path.GetFullPath(args[1]);
    string outPath = Path.GetFullPath(args[2]);

    var ix = BuildIndex(interop, out string? err);
    if (err != null) { Console.Error.WriteLine(err); return 2; }

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    ReverseIndex.DumpAll(ix!, outPath);
    Console.Error.WriteLine($">> wrote reverse index -> {outPath} ({ix!.Types:N0} types, {ix.Members:N0} members, {ix.KeyCount:N0} keys)");
    return 0;
}

static int Methods(string[] args)
{
    if (args.Length < 3) return Usage();
    string interop = Path.GetFullPath(args[1]);
    string type = args[2];
    int max = args.Length > 3 && int.TryParse(args[3], out var m) ? m : 200;

    var modules = LoadInteropModules(interop, out string? err);
    if (err != null) { Console.Error.WriteLine(err); return 2; }

    var r = ReverseIndex.TypeSurface(modules!, type, max);
    Console.Write(r.Text);
    if (r.Status == "ok")
    {
        Console.WriteLine($"RESULT: surface-methods ok {r.Resolved} — {r.Methods} declared method(s)");
        return 0;
    }
    Console.WriteLine($"RESULT: surface-methods {r.Status} {type}");
    return 1;
}

static ReverseIndex.Index? BuildIndex(string interop, out string? err)
{
    var modules = LoadInteropModules(interop, out err);
    return modules is null ? null : ReverseIndex.Build(modules);
}

// The one interop module loader `query`/`methods`/`dump` share. Resolver search paths: interop (the
// proxies reference each other) + core (Il2CppInterop) + dotnet (BCL), so base-class Resolve() in the
// rollup/base-chain walks can follow chains out of the proxy set. Misses are caught.
static List<ModuleDefinition>? LoadInteropModules(string interop, out string? err)
{
    err = null;
    if (!Directory.Exists(interop)) { err = $"!! no interop ({interop}) — boot the game once with bepinex installed"; return null; }

    string bep = Path.GetDirectoryName(interop.TrimEnd(Path.DirectorySeparatorChar))!;
    string gameRoot = Path.GetDirectoryName(bep)!;
    var resolver = new DefaultAssemblyResolver();
    foreach (var d in new[] { interop, Path.Combine(bep, "core"), Path.Combine(gameRoot, "dotnet") })
        if (Directory.Exists(d)) resolver.AddSearchDirectory(d);
    var rp = new ReaderParameters { AssemblyResolver = resolver };

    var modules = new List<ModuleDefinition>();
    foreach (var dll in Directory.EnumerateFiles(interop, "*.dll").OrderBy(x => x, StringComparer.Ordinal))
    {
        if (!IsManaged(dll)) continue;
        try { modules.Add(ModuleDefinition.ReadModule(dll, rp)); } catch { }
    }
    if (modules.Count == 0) { err = $"!! no readable interop assemblies in {interop}"; return null; }
    return modules;
}

static bool IsManaged(string dll)
{
    try { using var fs = File.OpenRead(dll); using var pe = new PEReader(fs); return pe.HasMetadata; }
    catch { return false; }
}

// The late-bound Compile call surface (mirrors CsModCompiler.Compile / .Result — bound by reflection above;
// a shape change on the engine side surfaces as the loud contract-skew setup error, never wrong results).
internal delegate CompileOutcome CompileFn(
    string asmName, IReadOnlyList<string> csFiles, IReadOnlyList<string> refDirs);

internal sealed record CompileOutcome(bool Ok, string[] Errors, string[] Warnings, int RefCount);
