using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// One directory-patch outcome — which proxy DLLs actually changed (and their per-module flip detail) and which
// were left untouched, for the human-readable patch log and for tests to assert against.
public sealed record DirectoryPatchResult(
    IReadOnlyList<(string Dll, RewriteResult Result)> Patched,   // DLLs actually rewritten (Flipped > 0)
    IReadOnlyList<string> Unchanged,                             // DLLs that flipped 0 (incl. framework proxies)
    IReadOnlyList<string> Unreadable,                            // non-.NET / corrupt DLLs Cecil could not load
    int TotalFlipped,
    string SchemaHash,                                           // the §7.2 marker stamped in the dir (SchemaMarker.Hash)
    IReadOnlyList<Residual> Residual,                    // members left naturalizable-but-unflipped AFTER every pass
    IReadOnlyList<(string Dll, string Defer)> Defers)            // every defer, from EVERY module — see below
{
    // Residuals with no known deferral reason: a pass should have covered these and did not. The number a caller
    // (CLI, test, consumer installer) should actually act on.
    public IReadOnlyList<Residual> Unexplained => Residual.Where(r => r.Unexplained).ToList();
}

// The patcher-APPLY driver: apply the IL-rewrite to the proxy DLLs of one interop directory in place.
//
// A DIRECTORY-level driver, not per-DLL, BY DESIGN — the reason the seam needs one shared engine at all. A
// cross-module virtual Task slot must flip in LOCKSTEP: flipping Assembly-CSharp's `Backend`1::OpenSession` override
// requires ToyGame.Core's `BackendBase`1::OpenSession` root to flip too, or the half-flip fails to load. Patching
// every module in one pass is what makes the planner's cross-module gate SOUND: the gate trusts the sibling module
// will be patched, and this driver keeps that promise.
//
// Two structural fixes:
//   * ATOMIC WRITE — each changed module is written to a temp file in the SAME directory and renamed over the
//     original (rename(2)); a mid-write failure never leaves a torn proxy.
//   * IDEMPOTENT re-run — only modules that actually change are written, and the Task family detects an
//     already-flipped member, so a second run over an already-patched tree writes nothing (Flipped == 0).
public static class InteropPatcher
{
    public static DirectoryPatchResult PatchDirectory(string interopDir, TextWriter? log = null)
    {
        if (!Directory.Exists(interopDir))
            throw new DirectoryNotFoundException($"interop dir not found: {interopDir}");

        using var resolver = new InMemoryInteropResolver(interopDir);   // cross-module refs (Assembly-CSharp -> ToyGame.Core, Il2Cpp*)
        var patched = new List<(string, RewriteResult)>();
        var unchanged = new List<string>();
        var unreadable = new List<string>();
        var residual = new List<Residual>();
        var defers = new List<(string, string)>();
        var exactRows = new List<ExactTypeRow>();
        int total = 0;

        foreach (string path in Directory.EnumerateFiles(interopDir, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);

            ModuleDefinition module;
            try
            {
                // InMemory: read the whole file and close the handle, so the atomic rename can replace it.
                module = ModuleDefinition.ReadModule(path,
                    new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            }
            catch
            {
                unreadable.Add(name);   // native or non-.NET DLL in the dir — skip, never a hard failure
                continue;
            }

            try
            {
                (RewriteResult result, bool normalized) = PatchModule(module);

                // Collect defers from EVERY module, not only the ones that flipped something. A module that flipped
                // 0 lands in `unchanged` below, and its defers used to be dropped on the floor — the one place a
                // wholly-deferred module could go silent.
                foreach (string d in result.Defers) defers.Add((name, d));

                // Audit AFTER every pass has run on this module, on the post-rewrite state: what is still
                // wearing a naturalizable il2cpp type (Nullable or container), and is there a known reason? (See ResidualAudit.)
                residual.AddRange(ResidualAudit.Scan(module));

                // The equality pass's POSTCONDITION, read off the post-patch module rather than off the pass's own
                // bookkeeping: any type still shadowing a System.Object virtual with a parallel newslot is one whose
                // equality the CLR cannot reach. Reported through the same channel as a naturalization hole because
                // it is the same kind of thing — a member a consumer meets in a shape that silently misbehaves.
                foreach (string t in EqualityRewriter.Shadowed(module))
                    residual.Add(new Residual(name, t, "a System.Object virtual is shadowed by a parallel newslot — the CLR never dispatches to it", true));

                // The pool-retarget pass's POSTCONDITION, read the same way: a materialization site still going
                // through Il2CppInterop's pool is one where a proxy's type is still a fact about the seam it was
                // reached through. Reported through the same channel as a naturalization hole — same kind of thing.
                foreach (string s in PoolRetargetRewriter.Remaining(module))
                    residual.Add(new Residual(name, s, "an il2cpp object is still materialized at the DECLARED type — `is`/`switch` cannot see the actual one", true));

                // Collect this module's proxy-type identities for the exact-type map. Read from the PRE-patch state
                // is irrelevant here (no pass touches a cctor's class resolution), but it is read per-module inside
                // the one directory walk so the map can never describe a different set of modules than was patched.
                exactRows.AddRange(ExactTypeExtract.Rows(module));

                if (result.Flipped > 0 || normalized)
                {
                    AtomicWrite(module, path);
                    if (result.Flipped > 0)
                    {
                        patched.Add((name, result));
                        total += result.Flipped;
                        log?.WriteLine($">> {name}: flipped {result.Flipped}");
                        foreach (string f in result.Flips) log?.WriteLine($"     FLIP  {f}");
                    }
                    if (normalized) log?.WriteLine($">> {name}: normalized System.Private.CoreLib ref to the runtime BCL version");
                }
                else
                {
                    unchanged.Add(name);
                }
            }
            finally
            {
                module.Dispose();
            }
        }

        // Stamp the content-addressed marker (docs/contribution/architecture/16-metadata.md, Generalization D).
        // ALWAYS — even on an already-patched tree (total == 0) — so the marker records "this dir was patched to
        // schema-hash X" as a structural fact the loader shim reads at Attach, not something inferred from whether THIS
        // run flipped anything. The hash is over Families.Default(), the same registry every rewriter builds from, so
        // it is the honest content-address of what this patch did (idempotent: an identical marker is not rewritten).
        // The exact-type map, beside the proxies it describes and written on the SAME schedule as the marker: always,
        // even when this run flipped nothing, because the map is a standing description of the tree rather than a
        // record of what one run did. Atomic + idempotent (an unchanged map is not rewritten). It is written from
        // the rows collected across EVERY module in this one walk, which is what makes an ambiguous il2cpp identity
        // (two proxy types claiming it) detectable at all — a per-module writer could never see the collision.
        bool mapChanged = ExactTypeMap.Write(interopDir, exactRows);
        log?.WriteLine($">> exact-type map {(mapChanged ? "written" : "current")}: {ExactTypeMap.FileName} = {exactRows.Count} proxy type(s)");

        string schemaHash = SchemaMarker.Hash(Families.Default());
        bool markerChanged = ContentMarker.Stamp(interopDir, SchemaMarker.InteropMarkerFileName, schemaHash, MarkerNote);
        log?.WriteLine($">> marker {(markerChanged ? "stamped" : "current")}: {SchemaMarker.InteropMarkerFileName} = {schemaHash}");

        // The residual report is emitted ALWAYS — including on an already-patched tree (total == 0), where it is the
        // only signal there is. Unexplained residuals are called out separately: those are holes, not deferrals.
        if (residual.Count > 0)
        {
            var unexplained = residual.Where(x => x.Unexplained).ToList();
            log?.WriteLine($"\n>> residual: {residual.Count} member(s) left wearing a naturalizable il2cpp type " +
                           $"({residual.Count - unexplained.Count} known-deferred, {unexplained.Count} unexplained)");
            foreach (Residual x in unexplained)
                log?.WriteLine($"   !! HOLE  {x.Module}: {x.Member}  ({x.Why})");
        }

        return new DirectoryPatchResult(patched, unchanged, unreadable, total, schemaHash, residual, defers);
    }

    const string MarkerNote =
        "These il2cpp proxies were natural-typed by inutil (§7.2). If this file is absent or its hash differs\n" +
        "from the running inutil build's schema, the proxies are unpatched or stale — regenerate with:\n" +
        "    inutil-interoppatch --game <gameDir>";

    // Every family pass over ONE module, in order — the per-module unit BOTH drivers share: the on-disk directory
    // loop above, and the BepInEx preloader patcher, which receives the loader's already-read AssemblyDefinitions one
    // at a time and must apply the IDENTICAL rewrite in memory (the byte-loaded copies and the later disk persist may
    // never diverge — same passes, same order, one implementation). Order: Task (return flip), container-return
    // (materialize), value/ref-bearing Nullable-return (full-body replace), the Nullable FIELD/PROPERTY accessor pass
    // (getter tail-swap + setter rebuild), arrays, then the UNIFIED param pass (ParamRewriter), and finally equality
    // pairing (EqualityRewriter — the one pass that flips on a GENERATION fact rather than a correspondence row, and
    // so the one the marker needs PatchCapabilities to see). Each pass is independently idempotent, so the whole
    // patch is.
    //
    // AFTER the flip, NormalizeCoreLibRef makes the proxy's BCL reference self-consistent: Il2CppInterop's generator
    // stamps System.Private.CoreLib at the GENERATOR host's version (net9) while the assembly TARGETS net6 (its
    // System.Runtime ref is 6.0), so a consumer that touches a proxy type sees a higher CoreLib than the runtime —
    // Roslyn rejects it (CS1705), and Roslyn *scripting* can't reconcile it at all. Aligning CoreLib to the assembly's
    // own System.Runtime version removes the skew; a no-op on a correctly-generated proxy, and correct even if a game
    // genuinely runs net9 (both would be 9.0).
    public static (RewriteResult Result, bool NormalizedCoreLib) PatchModule(ModuleDefinition module)
    {
        RewriteResult result = new TaskProxyRewriter().RewriteModule(module)
            .Merge(new ContainerReturnRewriter().RewriteModule(module))
            .Merge(new NullableReturnRewriter().RewriteModule(module))
            .Merge(new NullableFieldRewriter().RewriteModule(module))
            .Merge(new ContainerFieldRewriter().RewriteModule(module))
            .Merge(new ArrayRewriter().RewriteModule(module))
            .Merge(new ParamRewriter().RewriteModule(module))
            .Merge(new EqualityRewriter().RewriteModule(module))
            .Merge(new PoolRetargetRewriter().RewriteModule(module));
        bool normalized = NormalizeCoreLibRef(module);
        return (result, normalized);
    }

    // Align every System.Private.CoreLib assembly reference to the module's own System.Runtime reference version
    // (the runtime-consistent BCL version — System.Runtime is the TFM facade the generator gets right, while it
    // mis-stamps System.Private.CoreLib at its host's version). Returns true if any ref version changed. No-op when
    // the module has no System.Private.CoreLib ref, no System.Runtime ref, or they already agree.
    static bool NormalizeCoreLibRef(ModuleDefinition module)
    {
        AssemblyNameReference? runtime = null;
        var coreLibs = new List<AssemblyNameReference>();
        foreach (AssemblyNameReference r in module.AssemblyReferences)
        {
            if (r.Name == "System.Runtime") runtime = r;
            else if (r.Name == "System.Private.CoreLib") coreLibs.Add(r);
        }
        if (runtime is null || coreLibs.Count == 0) return false;

        bool changed = false;
        foreach (AssemblyNameReference cl in coreLibs)
            if (cl.Version != runtime.Version) { cl.Version = runtime.Version; changed = true; }
        return changed;
    }

    // The DISK-ONLY wire-attribute pass: re-attach the recovered [JsonProperty] wire names onto the proxy members as
    // System.Text.Json [JsonPropertyName], so a standard managed serializer round-trips the game's own proxy by its
    // wire names. SEPARATE from PatchDirectory (a different contract — serialization, not natural-typing) and run
    // AFTER it by the CLI; NOT part of the preloader-shared PatchModule, since the boot path never needs wire
    // attributes. Reuses the same atomic temp-write+rename and lock-free resolver. Degrades to a no-op when the
    // wiremap is absent/malformed.
    public static WireStampResult StampWireAttributesDirectory(string interopDir, string wireMapPath, TextWriter? log = null)
    {
        if (!Directory.Exists(interopDir))
            throw new DirectoryNotFoundException($"interop dir not found: {interopDir}");

        WireMap? map = WireMap.Load(wireMapPath);
        if (map is null)
        {
            log?.WriteLine($">> wire-attrs: no usable wiremap at {wireMapPath} — skipped (proxies keep member-name serialization)");
            return default;   // nothing stamped, nothing already there — the caller's own "no wiremap" line covers why
        }
        log?.WriteLine($">> wire-attrs: {map.TypeCount} recovered type(s) from {Path.GetFileName(wireMapPath)}");

        using var resolver = new InMemoryInteropResolver(interopDir);
        var rewriter = new WireAttributeRewriter();
        int total = 0, dlls = 0, already = 0;

        foreach (string path in Directory.EnumerateFiles(interopDir, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            ModuleDefinition module;
            try
            {
                module = ModuleDefinition.ReadModule(path,
                    new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            }
            catch
            {
                continue;   // native / non-.NET DLL — skip, never a hard failure (mirrors PatchDirectory)
            }

            try
            {
                WireStampResult r = rewriter.Stamp(module, map);
                already += r.AlreadyPresent;
                if (r.Stamped > 0)
                {
                    AtomicWrite(module, path);
                    total += r.Stamped;
                    dlls++;
                    log?.WriteLine($">> {Path.GetFileName(path)}: stamped {r.Stamped} wire attr(s)");
                }
            }
            finally
            {
                module.Dispose();
            }
        }

        // BOTH numbers, always: a re-run over an already-stamped tree stamps 0, and a caller that reads only the
        // first number cannot tell that from "the recovered names never landed". The second is the standing fact.
        log?.WriteLine($">> wire-attrs: stamped {total} member(s) across {dlls} DLL(s); {already} already present");
        return new WireStampResult(total, already);
    }

    // Write to a temp file in the SAME directory (so the rename stays on one filesystem and is atomic), then
    // replace the original. No torn proxy is ever observable — the original is intact until the instant it isn't.
    static void AtomicWrite(ModuleDefinition module, string targetPath)
    {
        string tmp = targetPath + ".inutil-tmp";
        try
        {
            // The BCL-identity invariant, enforced at the LAST possible moment: no module leaves this patcher carrying
            // a System.Private.CoreLib reference at a version other than its own System.Runtime's. Normalizing once in
            // PatchModule is not enough — anything that touches the module afterwards can re-introduce the skew, and
            // one did: ResidualAudit.Scan plans members (building natural types) between PatchModule and this write,
            // which used to ImportReference the tool host's net9 corlib and leave a dangling 9.0.0.0 row behind. Roslyn
            // rejects the assembly on that row alone (CS1705), so every offline consumer check of the patched tree
            // dies. BclScope removed the source; this makes a re-introduction structurally unable to reach disk.
            NormalizeCoreLibRef(module);
            module.Write(tmp);
            File.Move(tmp, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch { /* best effort — a failed write left it */ }
        }
    }
}

// A BaseAssemblyResolver whose resolutions are read IN MEMORY — the file is read fully and its handle closed at once,
// never a deferred stream. Cecil's DefaultAssemblyResolver reads resolutions DEFERRED and keeps the resolved DLL OPEN
// for the resolver's lifetime; under Windows sharing semantics (incl. wine) that denies the rename-over AtomicWrite
// needs when a later module IS one of those resolved files — the write throws UnauthorizedAccessException. The offline
// CLI never surfaced this because it has only run on POSIX, where rename-over-open is legal; in-game it fails
// immediately. Used by PatchDirectory AND as the preloader patcher's ResolveFailure bridge, so every resolution the
// patch engine causes is lock-free by construction.
public sealed class InMemoryInteropResolver : BaseAssemblyResolver
{
    readonly Dictionary<string, AssemblyDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryInteropResolver(params string[] searchDirs)
    {
        foreach (string d in searchDirs)
            if (Directory.Exists(d)) AddSearchDirectory(d);
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_cache.TryGetValue(name.Name, out AssemblyDefinition? hit)) return hit;
        var inMemory = new ReaderParameters { InMemory = true, AssemblyResolver = this };
        return _cache[name.Name] = base.Resolve(name, inMemory);
    }

    protected override void Dispose(bool disposing)
    {
        foreach (AssemblyDefinition ad in _cache.Values) { try { ad.Dispose(); } catch { } }
        _cache.Clear();
        base.Dispose(disposing);
    }
}
