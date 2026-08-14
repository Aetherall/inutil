// inutil's BepInEx PRELOADER patcher — the end-user natural-typing pass. The layering is forced by measured
// Windows/wine file-sharing reality.
//
// NORMAL PATH — the CONSTRUCTOR patches ON DISK, pre-read. be.755's Preloader.Run order is:
//   Il2CppInteropManager.Initialize()  (generates/restores the proxies)
//   AddPatchersFromDirectory()         (loads + INSTANTIATES patcher plugins  <-- our ctor runs here)
//   LoadAssemblyDirectories()          (opens a deferred Cecil stream on EVERY interop DLL)
//   PatchAndLoad()                     (Initialize -> patch methods -> byte-load -> Finalizers)
// The ctor is therefore the ONE in-process window where the interop files are guaranteed unlocked — even on
// the boot a game update regenerated them — so it runs the marker-guarded, atomic, idempotent on-disk
// PatchDirectory (the exact driver the offline CLI runs). The preloader then simply READS patched files, so
// CsModCompiler's disk-read references agree with the runtime by construction. After that window, in-process
// disk writes are dead: the patching context holds deferred streams for the whole preload, the chainloader
// caches more ADs through TypeLoader.CecilResolver, and Windows/wine sharing then denies rename-over for the
// process lifetime (measured: the phase-2 persist fails with UnauthorizedAccessException).
//
// FALLBACK LAYER — if the ctor's disk patch fails (a locked file, AV interference), the in-memory pass rescues
// the SESSION: patch methods rewrite the context ADs (BepInEx byte-loads what a patch method modified, before
// anything binds), a ResolveFailure bridge feeds Cecil's write-time resolution from lock-free in-memory reads,
// and the host plugin best-effort persists to disk at Load (usually denied — see above). Both layers apply the
// one deterministic per-module implementation (InteropPatcher.PatchModule), so they cannot diverge.
//
// Deploy to BepInEx/patchers/ (NOT plugins/). The engine + schema are compiled into this assembly as source, so
// it binds only BepInEx's own already-loaded assemblies + Mono.Cecil and stays out of the proxy graph it patches.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;                 // TypeLoader — the preloader's ONE shared Cecil resolver
using BepInEx.Preloader.Core.Patching;
using Mono.Cecil;
using Inutil.InteropPatch;
using Inutil.Schema;

namespace Inutil.Host.Patcher;

[PatcherPluginInfo("aetherall.inutil.patcher", "inutil interop patcher", "0.2.0")]
public sealed class PreloadInteropPatcher : BasePatcher
{
    private bool _skip;
    private int _modified;
    private int _failed;
    private int _alreadyLoaded;
    private HashSet<string>? _onlyRefused;   // non-null = rescue ONLY what the disk pass could not write (see ctor)
    private string? _ctorReport;   // Log isn't wired during construction — emitted from Initialize

    // Runs the ON-DISK patch here because the ctor is the one unlocked pre-read window (see class header).
    // Everything below — the in-memory patch methods, the resolve bridge, the host plugin's disk persist —
    // is the FALLBACK layer for a failed disk patch. Fully try-wrapped: a ctor throw would make BepInEx drop
    // the whole patcher, losing the fallback too.
    public PreloadInteropPatcher()
    {
        try { File.Delete(TracePath); } catch { }
        Trace("ctor (pre-stream window)");
        try
        {
            if (!Directory.Exists(InteropDir)) { Trace("ctor: no interop dir yet"); return; }
            string schema = SchemaMarker.Hash(Families.Default());
            if (ContentMarker.Verify(InteropDir, SchemaMarker.InteropMarkerFileName, schema) == MarkerVerdict.Current)
            {
                _skip = true;
                Trace("ctor: marker current — nothing to do");
                return;
            }
            // No marker, but a previous run may have left a note saying it got as far as an in-process run CAN get.
            // Without this the patcher re-walks all ~140 modules on EVERY launch (measured 4.3s, after the two boots
            // that actually did the work) to rediscover that the one module the loader binds before us is still
            // bound. The note only licenses skipping when nothing about it could have changed — same schema, same
            // proxy files, a walk that already flipped nothing, and every refused module still bound right now.
            if (PartialPatchNote.Read(InteropDir) is { } note && note.NothingLeftToDo(InteropDir, schema, IsLoaded))
            {
                _skip = true;
                _ctorReport = "inutil: the interop proxies are as natural-typed as an in-game pass can make them — " +
                              string.Join(", ", note.Refused.Select(x => x.Dll)) +
                              " is bound by the loader before any patcher runs and cannot be replaced from inside " +
                              "the game. Run `inutil-interoppatch` with the game closed to finish the job.";
                Trace($"ctor: partial note is current and converged — skipping the walk " +
                      $"(still bound: {string.Join(", ", note.Refused.Select(x => x.Dll))})");
                return;
            }
            DirectoryPatchResult r = Inutil.InteropPatch.InteropPatcher.PatchDirectory(InteropDir, null);
            // The residual audit is otherwise invisible on this path (PatchDirectory reports it to a log, and we pass
            // null): a boot could leave members wearing a raw il2cpp spelling and say nothing. Carry the COUNT; the
            // names come from running inutil-interoppatch, which prints the same audit in full.
            string holes = r.Unexplained.Count > 0
                ? $" {r.Unexplained.Count} member(s) left raw with no known deferral reason — run inutil-interoppatch to list them."
                : "";
            if (r.Complete)
            {
                _skip = true;   // disk is now patched + marker stamped; the read below picks it up — no in-memory pass
                _ctorReport = $"inutil: natural-typed the interop proxies ON DISK in the pre-read window — " +
                              $"{r.TotalFlipped} member(s) across {r.Patched.Count} assembl{(r.Patched.Count == 1 ? "y" : "ies")} " +
                              $"(schema {r.SchemaHash}); the preloader loads the patched files directly.{holes}";
                Trace($"ctor: disk-patched {r.TotalFlipped} member(s) across {r.Patched.Count} assemblies");
            }
            else
            {
                // PARTIAL. Everything writable DID reach disk; arm the in-memory pass for exactly what did not.
                //
                // Two different refusals hide here, and calling them the same thing is how this reported a fiction.
                // A module ALREADY BOUND in this AppDomain is unwritable for the process lifetime, and — measured —
                // the loader binds Il2Cppmscorlib before ANY patcher is constructed, on every boot, not just the one
                // that generates the proxies. So "the next launch completes them" was false: the next launch refuses
                // it again, and the only thing that ever patches it is an OUT-OF-PROCESS run (inutil-interoppatch),
                // where nothing has it mapped. Say that, because it is the only sentence a reader can act on.
                // Anything else refused (an antivirus, a stale handle) genuinely may clear by the next launch.
                _onlyRefused = new HashSet<string>(
                    r.Refused.Select(x => Path.GetFileNameWithoutExtension(x.Dll)), StringComparer.OrdinalIgnoreCase);
                string[] bound = r.Refused.Where(x => IsLoaded(Path.GetFileNameWithoutExtension(x.Dll)))
                                          .Select(x => x.Dll).ToArray();
                string[] transient = r.Refused.Where(x => !IsLoaded(Path.GetFileNameWithoutExtension(x.Dll)))
                                              .Select(x => x.Dll).ToArray();
                string why = bound.Length > 0
                    ? $"{string.Join(", ", bound)} {(bound.Length == 1 ? "is" : "are")} bound by the loader before any " +
                      "patcher runs, so no in-game pass can ever replace them — their members keep raw il2cpp " +
                      "spellings until an OUT-OF-PROCESS run (inutil-interoppatch) patches them."
                    : "";
                if (transient.Length > 0)
                    why += (why.Length > 0 ? " " : "") +
                           $"{string.Join(", ", transient)} {(transient.Length == 1 ? "was" : "were")} refused by " +
                           "something outside this process; the next launch may complete them.";
                _ctorReport = $"inutil: natural-typed {r.Patched.Count} interop assembl{(r.Patched.Count == 1 ? "y" : "ies")} " +
                              $"ON DISK ({r.TotalFlipped} member(s)); {r.Refused.Count} could not be written. {why}{holes}";
                Trace($"ctor: disk-patched {r.Patched.Count} assemblies, REFUSED {r.Refused.Count} " +
                      $"(bound: {(bound.Length == 0 ? "-" : string.Join(", ", bound))}; " +
                      $"transient: {(transient.Length == 0 ? "-" : string.Join(", ", transient))})");
            }
        }
        catch (Exception ex)
        {
            Trace($"ctor: disk patch FAILED {ex.GetType().Name}: {ex.Message} — falling back to the in-memory pass");
            _ctorReport = $"inutil: pre-read disk patch failed ({ex.GetType().Name}: {ex.Message}) — " +
                          "falling back to the in-memory session patch (mod compiles may not see natural types this boot).";
        }
    }

    private static string InteropDir => Path.Combine(Paths.BepInExRootPath, "interop");

    // Direct-to-file progress trace. Preloader log messages are only FLUSHED to LogOutput.log by the
    // chainloader — a preloader hang leaves them invisible — so every phase transition is also appended here,
    // where an outside observer can see exactly where a stuck boot stopped. Truncated in Initialize.
    private static string TracePath => Path.Combine(Paths.BepInExRootPath, ".inutil-preload-patch.trace");
    private static void Trace(string msg)
    {
        try { File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
    }

    // The phase-2 handshake: the host plugin persists to disk ONLY when this boot's in-memory pass reported
    // "ok" (or didn't run — marker already current). A PARTIAL in-memory patch with a full disk persist would
    // make the loaded assemblies and the compile references disagree — the split this design must never produce.
    internal static string BreadcrumbPath => Path.Combine(Paths.BepInExRootPath, ".inutil-preload-patch");

    public override void Initialize()
    {
        Trace("initialize");
        try { File.Delete(BreadcrumbPath); } catch { }   // a stale verdict from a prior boot must never gate this one
        if (_ctorReport is not null) Log.LogInfo(_ctorReport);   // the ctor ran before Log was wired

        if (_skip)
            Log.LogInfo("inutil: interop proxies are at the current patch schema on disk — in-memory pass not needed.");
        else
        {
            // Cecil RESOLVES assembly refs at WRITE time (serializing a signature needs a typeref's
            // IsValueType → Resolve), and the interop dir is NOT on TypeLoader's shared resolver search path in
            // the preloader window — so serializing a MODIFIED interop AD threw AssemblyResolutionException on
            // the proxies' unversioned sibling refs ('UnityEngine.CoreModule, Version=0.0.0.0'), surfacing as a
            // silent dead boot inside BepInEx's own post-patch byte-load write. Bridge the failure event to an
            // IN-MEMORY interop resolver — one shared event fixes both our serialize-check below AND BepInEx's
            // own write of the ADs we mark modified. Deliberately NOT AddSearchDirectory: those hold the resolved
            // DLL open for the process life, so the phase-2 disk persist then dies on rename-over-open
            // (UnauthorizedAccessException — the exact bug this replaced).
            _bridge = new InMemoryInteropResolver(InteropDir);
            TypeLoader.CecilResolver.ResolveFailure += BridgeResolve;
            Trace("resolve bridge attached (in-memory interop resolver)");
        }
        Trace(_skip ? "initialize done (marker current — skipping)" : "initialize done (unpatched — passes armed)");
    }

    private InMemoryInteropResolver? _bridge;

    // Is an assembly of this simple name ALREADY in the AppDomain? Read live rather than snapshotted at Initialize,
    // because the preloader binds assemblies as it goes — the set this question is about grows during the pass.
    private static bool IsLoaded(string simpleName)
    {
        foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? n = null;
            try { n = a.GetName().Name; } catch { }   // a dynamic/reflection-only assembly may refuse its name
            if (string.Equals(n, simpleName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private AssemblyDefinition? BridgeResolve(object sender, AssemblyNameReference reference)
    {
        try { return _bridge?.Resolve(reference); }
        catch { return null; }   // not ours to resolve — let the next handler / the normal failure path decide
    }

    // Runs on EVERY assembly in the preloader context before BepInEx loads it. Rewrite the AD in place; return
    // true when modified so BepInEx byte-loads THIS copy (leaving the on-disk file unlocked for the host
    // plugin's persist). False leaves it to load from disk; a non-proxy assembly flips nothing and falls through.
    [TargetAssembly(TargetAssemblyAttribute.AllAssemblies)]
    public bool Patch(AssemblyDefinition assembly)
    {
        if (_skip) return false;
        string name = assembly.Name.Name;

        // The in-memory pass rescues exactly what the disk pass could not write. When the disk pass reported which
        // modules those were, patching the rest again is pure cost — they are already patched ON DISK and the passes
        // are idempotent, so they would flip 0 and be dropped anyway, after a full re-walk of every module.
        if (_onlyRefused is not null && !_onlyRefused.Contains(name)) return false;

        // THE INVARIANT THIS PASS MUST NOT BREAK: never report an assembly modified when a copy of that identity is
        // ALREADY loaded in this AppDomain. Returning true makes BepInEx byte-load our rewritten copy, which mints a
        // SECOND assembly with the same simple name — and Il2CppInterop resolves an assembly by name with Single(),
        // so the duplicate is a hard boot failure ("Sequence contains more than one matching element" out of
        // Il2CppClassPointerStore`1..cctor, surfacing as "Unable to execute IL2CPP chainloader"). Measured on the
        // boot that generates the proxies, where Il2Cppmscorlib is loaded before any patcher is constructed.
        // Phrased against the fact — an identity already present — rather than against that one module: any proxy
        // the loader happens to have bound early is the same hazard, and a later BepInEx may bind a different one.
        if (IsLoaded(name))
        {
            _alreadyLoaded++;
            Trace($"patch {name} SKIPPED — already loaded in this AppDomain (byte-loading would duplicate the identity)");
            return false;
        }

        Trace($"patch {name}");
        try
        {
            (RewriteResult result, bool normalized) = Inutil.InteropPatch.InteropPatcher.PatchModule(assembly.MainModule);
            Trace($"patch {name} done (flipped={result.Flipped} norm={normalized})");
            if (result.Flipped == 0 && !normalized) return false;
            // Serialize-check (fail-loud): BepInEx writes every modified AD in its post-patch byte-load, where a
            // Cecil serialization failure is UNATTRIBUTED and stalls the boot silently. Writing here first pins
            // any failure to its assembly (catch → error log + trace + the breadcrumb 'fail' verdict that makes
            // the plugin skip the disk persist). NB a per-assembly failure still leaves the OTHER already-returned
            // assemblies patched in memory (mixed state) — acceptable because a serialization failure is a bug
            // the offline CLI run would also hit, not a per-machine condition; the loud error names the assembly.
            using (var probe = new MemoryStream())
            {
                assembly.Write(probe);
                Trace($"write-probe {name} ok ({probe.Length}B)");
            }
            _modified++;
            return true;
        }
        catch (Exception ex)
        {
            _failed++;
            Trace($"patch {name} FAILED {ex.GetType().Name}: {ex.Message}");
            Log.LogError($"inutil: in-memory patch of '{name}' failed ({ex.GetType().Name}: {ex.Message}) — " +
                         "natural-typed mods may not bind this session. Run inutil-interoppatch offline and relaunch.");
            return false;
        }
    }

    public override void Finalizer()
    {
        Trace($"finalizer (skip={_skip} modified={_modified} failed={_failed} alreadyLoaded={_alreadyLoaded})");
        // Detach the resolve bridge — preloading is over. The ADs it minted are cached inside
        // TypeLoader.CecilResolver (may serve later chainloader scans); they hold NO file handles, so leaving
        // them cached is deliberate — do not Dispose the bridge.
        if (_bridge is not null) { TypeLoader.CecilResolver.ResolveFailure -= BridgeResolve; _bridge = null; }
        if (_skip) return;   // no breadcrumb: the plugin's own marker check handles the steady state

        // The 'mapped' verdict: modules this process can never write, because they are loaded into it. The plugin's
        // Load-time persist would re-walk the whole directory (~25s) only to be refused on exactly these again, so
        // tell it to skip. Distinct from 'fail' — nothing went wrong here, the work simply belongs to the next launch.
        if (_alreadyLoaded > 0 && _failed == 0)
        {
            try { File.WriteAllText(BreadcrumbPath, $"mapped {_alreadyLoaded}"); } catch { }
            Log.LogInfo($"inutil: {_alreadyLoaded} interop assembl{(_alreadyLoaded == 1 ? "y is" : "ies are")} already " +
                        "bound in this process and cannot be rewritten — they stay raw for this session and the next " +
                        "launch patches them on disk. Everything else is natural-typed.");
            return;
        }
        try { File.WriteAllText(BreadcrumbPath, _failed == 0 ? $"ok {_modified}" : $"fail {_failed}/{_modified}"); } catch { }
        Log.LogInfo(_failed == 0
            ? $"inutil: rewrote {_modified} interop assembl{(_modified == 1 ? "y" : "ies")} in memory before load — " +
              "natural types bind this session; the host plugin persists them to disk for the mod compiler."
            : $"inutil: {_failed} interop assembl(ies) FAILED the in-memory patch — the host plugin will SKIP the disk " +
              "persist (memory and disk must never disagree). Run inutil-interoppatch offline and relaunch.");
    }
}
