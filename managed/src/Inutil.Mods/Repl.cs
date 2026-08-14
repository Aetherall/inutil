// Inutil.Repl — the in-game C# REPL engine (§7.12), built on the live hook host. One ReplSession is a long-lived
// Roslyn CSharp.Scripting interpreter wired to the RUNNING game: a submission is compiled by Roslyn against the
// live loaded world and executed in-process, so it can call the typed SDK (Inutil.Hooks) and the Il2CppInterop
// proxies (Player, …) against the real runtime. Successive submissions CHAIN through a ScriptState, so submission
// N sees the `var`s submission N-1 declared (the REPL semantic).
//
// Lives in Inutil.Mods.dll (not the SDK) because it drags in Roslyn — the same reason CsModCompiler does.
//
// THREADING (the §7.12 contract). Eval is SYNCHRONOUS on the calling thread — a REPL line with no `await` runs to
// completion inline, which is CORRECT: it must be the loader's main (il2cpp-attached) thread so the line's proxy
// touches happen on the right thread. The one hazard: a line that AWAITS a main-thread-completed primitive
// (MainThread.NextFrame()/Post<T>/Until) produces a Task that only Drain — on the main thread — can complete;
// blocking on it here would hang the game forever. The guard detects that (on the main thread, an incomplete task)
// and fails LOUD instead of hanging. A live transport (ReplServer) keeps Eval on the main thread by DISPATCHING
// each line through MainThread.Post, so the guard is the single place that shape is handled.
//
// LIFECYCLE: Roslyn submission assemblies are NOT collectible — a session is long-lived by design. The HOOKS a
// session registers ARE removable (each Hooks.Pre/Post returns an IDisposable); only the submission assemblies
// persist. A collectible REPL (compile-to-bytes into a collectible ALC for `:reset`) is future hardening.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Inutil.Repl;

// The outcome of one submission: the value of its last expression (boxed), or an error. A compile error or a
// runtime throw leaves the session state UNADVANCED — the next submission continues from the last good state.
public readonly struct ReplResult
{
    public readonly bool Ok;
    public readonly object? Value;
    public readonly string? Error;
    private ReplResult(bool ok, object? value, string? error) { Ok = ok; Value = value; Error = error; }
    public static ReplResult Good(object? value) => new(true, value, null);
    public static ReplResult Bad(string error) => new(false, null, error);

    public override string ToString() => Ok ? (Value?.ToString() ?? "null") : "error: " + Error;
}

public sealed class ReplSession
{
    private readonly ScriptOptions _options;
    private ScriptState<object>? _state;

    // Build a session against the live loaded world. References, deduped by simple assembly name:
    //   1. The full RUNNING-runtime BCL directory — so a submission can use ANY BCL type, not just what happens to
    //      be loaded. A native PE (coreclr/clrjit) has no ECMA metadata and CreateFromFile is lazy, so it must be
    //      filtered here (else a compile-time CS0009), which IsManaged does.
    //   2. Then every non-dynamic LOADED assembly not already covered — the interop proxies (Player), Il2CppInterop,
    //      Inutil.dll (Hooks) — so a submission names them with no manual wiring.
    // Every reference then shares the RUNNING runtime's BCL identity, so a submission's `int` and its boxed `object`
    // return come from the same CoreLib the scripting host uses (no skew). Imports seed the usings every line gets.
    public ReplSession(string proxyNamespace, IEnumerable<string>? extraImports = null)
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // dedup by simple assembly name

        string? rt = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        if (!string.IsNullOrEmpty(rt) && Directory.Exists(rt))
            foreach (string dll in Directory.EnumerateFiles(rt, "*.dll"))
            {
                if (!IsManaged(dll) || !seen.Add(Path.GetFileNameWithoutExtension(dll))) continue;
                try { refs.Add(MetadataReference.CreateFromFile(dll)); } catch { /* unreadable -> skip */ }
            }

        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.IsDynamic) continue;
            string loc;
            try { loc = a.Location; } catch { continue; }   // some hosts throw on Location for in-memory asms
            if (string.IsNullOrEmpty(loc)) continue;
            if (!seen.Add(a.GetName().Name ?? Path.GetFileNameWithoutExtension(loc))) continue;
            try { refs.Add(MetadataReference.CreateFromFile(loc)); } catch { /* unreadable -> skip */ }
        }

        var imports = new List<string> { "System", "System.Linq", "Inutil.Hooks", "Inutil.Sugar" };
        if (!string.IsNullOrEmpty(proxyNamespace)) imports.Add(proxyNamespace);
        if (extraImports != null) imports.AddRange(extraImports);

        _options = ScriptOptions.Default.WithReferences(refs).WithImports(imports);
    }

    // Does `dll` carry managed metadata? CreateFromFile is lazy and defers a native PE to a compile-time CS0009,
    // so pre-filter by reading the PE header (the same check CsModCompiler uses).
    static bool IsManaged(string dll)
    {
        try
        {
            using var fs = File.OpenRead(dll);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
            return pe.HasMetadata;
        }
        catch { return false; }
    }

    // Compile + run one submission against the running game, chaining onto prior session state. Synchronous by
    // design (see the file header); errors are captured (not thrown) and do not advance the session.
    public ReplResult Eval(string code)
    {
        try
        {
            System.Threading.Tasks.Task<ScriptState<object>> task = _state is null
                ? CSharpScript.RunAsync(code, _options)
                : _state.ContinueWithAsync(code);

            // §7.12 deadlock guard. A no-await submission completes SYNCHRONOUSLY (the deliberate contract); one
            // that awaits does not. On the main thread, blocking on an incomplete task hangs forever when it awaits
            // a main-thread-completed primitive (they complete only from Drain, which the blocked main thread can
            // never reach). Fail LOUD instead of hanging — a REPL line must run to completion synchronously.
            if (!task.IsCompleted && Inutil.Host.MainThread.OnMainThread)
                return ReplResult.Bad(
                    "this REPL line did not complete synchronously — it is awaiting (e.g. a main-thread primitive " +
                    "like MainThread.NextFrame()). A synchronous Eval on the main thread cannot await such a " +
                    "primitive without deadlocking the game. Do async work from a hook or coroutine, not a REPL line.");

            ScriptState<object> next = task.GetAwaiter().GetResult();
            _state = next;                                    // advance only on success (error leaves state intact)
            return ReplResult.Good(_state.ReturnValue);
        }
        catch (CompilationErrorException ce)
        {
            return ReplResult.Bad(string.Join("; ", ce.Diagnostics.Select(d => d.ToString())));
        }
        catch (Exception ex)
        {
            return ReplResult.Bad($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

// Preload the Roslyn scripting closure (deployed beside the plugin) into the plugin's OWN load context BEFORE any
// Roslyn type is touched. BepInEx default-probes plugins/ so it binds our copies; MelonLoader routes every resolve
// through its own resolver, which can find the WRONG System.Collections.Immutable and throw — so we pre-populate
// the ALC with the correct files by path. This class references NO Roslyn type, so calling it cannot trigger the
// very resolve we are getting ahead of (call it before `new ReplSession`).
public static class ReplBootstrap
{
    private static bool _done;
    private static bool _available;
    private const string TopAsm = "Microsoft.CodeAnalysis.CSharp.Scripting";

    // Leaves first (Immutable/Metadata before the CodeAnalysis assemblies that need them), scripting on top.
    private static readonly string[] Closure =
    {
        "System.Collections.Immutable.dll", "System.Reflection.Metadata.dll",
        "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll",
        "Microsoft.CodeAnalysis.Scripting.dll", "Microsoft.CodeAnalysis.CSharp.Scripting.dll",
    };

    // Whether the scripting engine is AVAILABLE here (its closure is deployed beside the plugin, or already
    // loaded). False when only Inutil.dll + the host were deployed — a consumer then SKIPs rather than throwing a
    // FileNotFoundException constructing a ReplSession.
    public static bool PreloadEngine(Action<string>? log = null)
    {
        if (_done) return _available;
        _done = true;

        var self = typeof(ReplBootstrap).Assembly;
        string? dir = null;
        try { dir = Path.GetDirectoryName(self.Location); } catch { }

        bool alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => string.Equals(a.GetName().Name, TopAsm, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(dir)) { _available = alreadyLoaded; return _available; }

        _available = alreadyLoaded || File.Exists(Path.Combine(dir, TopAsm + ".dll"));
        if (!_available) return false;

        var alc = AssemblyLoadContext.GetLoadContext(self) ?? AssemblyLoadContext.Default;
        var loaded = new HashSet<string>(
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "").Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        foreach (string name in Closure)
        {
            string simple = Path.GetFileNameWithoutExtension(name);
            if (loaded.Contains(simple)) continue;                       // framework / loader already provides it
            string p = Path.Combine(dir, name);
            if (!File.Exists(p)) continue;                               // not every leaf ships on every loader
            try { alc.LoadFromAssemblyPath(p); }
            catch (Exception ex) { log?.Invoke($"[repl] preload {name}: {ex.GetType().Name} {ex.Message}"); }
        }
        return true;
    }
}
