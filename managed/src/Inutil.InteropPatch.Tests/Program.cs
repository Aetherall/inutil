using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.Schema;
using Inutil.InteropPatch;
using Inutil.InteropPatch.Tests;

// Offline IL-rewrite proof: drive the pure engine (VirtualSlotPlanner + Task family) over the REAL generated
// ToyGame proxies. No game, no wine; the proxies are read (copied, never mutated) from the loader tree.
//
// The source dir MUST be PRISTINE (unpatched) — asserted below via the schema marker, not assumed. Most assertions
// are written as outcomes to be robust to the starting state (the cross-module GATE at the PLAN level; the flip
// mechanics as OUTCOMES — after the pass the root IS System.Task`1 — plus a Cecil round-trip and idempotency), but
// "robust to the starting state" was never actually true of the suite as a whole: on an already-flipped input a
// negative assertion inverts (GetCombo is NOT flipped), every idempotency case passes VACUOUSLY, and a before/after
// case like the residual audit's pre-flip half cannot be expressed at all. A wrong-state input is therefore a hard
// setup error (exit 2), never a red run that looks like a code regression.

// ── offline suites: pure path/projection logic — run with NO provisioned game ──
Console.WriteLine(">> --game locator (§5.1)");
int offlineFailures = GameLocatorTests.Run();
Console.WriteLine(">> Cecil member/attribute projection (§4.1)");
offlineFailures += CecilProjectionTests.Run();
Console.WriteLine(">> framework-vs-game assembly classifier (BepInEx/MelonLoader il2cpp-prefix divergence)");
offlineFailures += FrameworkAssemblyTests.Run();
Console.WriteLine(">> non-virtual GENERIC Task flip incl. nested Task<Result<!!0>> (v17 follow-up, synthetic Cecil)");
offlineFailures += NonVirtualGenericTaskFlipTests.Run();
Console.WriteLine(">> wire-attribute re-attachment: stamp recovered [JsonPropertyName] onto proxy members (synthetic Cecil)");
offlineFailures += WireAttributeTests.Run();

// ── the real Cecil rewrite over provisioned proxies (needs a game; gracefully skipped if unprovisioned) ──
string? interopDir = Environment.GetEnvironmentVariable("INUTIL_INTEROP_DIR")
    ?? (args.Length > 0 ? args[0] : FindInteropDir());

if (interopDir is null || !Directory.Exists(interopDir))
{
    if (offlineFailures > 0)
    {
        Console.Error.WriteLine($"\nINTEROP-PATCH TESTS RED — {offlineFailures} offline failure(s) (Cecil integration skipped: no interop dir).");
        return 1;
    }
    Console.Error.WriteLine("!! interop dir not found — provision with setup-bepinex, or pass it as arg / INUTIL_INTEROP_DIR.\n   (offline suites green; Cecil integration skipped)");
    return 2;
}
Console.WriteLine($">> proxies: {interopDir}");

// The Cecil integration below copies proxies OUT of this dir and asserts what the passes do to them — which is only
// meaningful if they arrive UNPATCHED. Patch that dir out of band (a manual inutil-interoppatch run, a consumer's
// install step) and the suite silently changes meaning: negative assertions invert, and every "re-running the pass
// flips 0" idempotency case passes VACUOUSLY while testing nothing. That is a worse outcome than a red run, so the
// state is asserted rather than assumed — using the same marker this suite already exercises further down.
if (ContentMarker.ReadHash(interopDir, SchemaMarker.InteropMarkerFileName) is { } stampedHash)
{
    Console.Error.WriteLine(
        $"\n!! the interop dir at {interopDir} is ALREADY PATCHED (marker {SchemaMarker.InteropMarkerFileName} = {stampedHash}).\n" +
        "   These tests need PRISTINE proxies — on an already-flipped input the idempotency cases pass vacuously.\n" +
        "   Regenerate with 'setup-bepinex --force', or point INUTIL_INTEROP_DIR at an unpatched copy.");
    return 2;
}

int failures = offlineFailures;   // fold the offline verdict into the overall tally
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]" + (ok || detail is null ? "" : $"  -- {detail}"));
    if (!ok) failures++;
}

string work = Path.Combine(Path.GetTempPath(), "inutil-interoppatch-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(work);
    resolver.AddSearchDirectory(interopDir);
    var rewriter = new TaskProxyRewriter();

    static string Root(SlotPlan p) => $"{((CecilSlotMethod)p.Root).Definition.DeclaringType.Name}::{p.Root.Name}";

    // ── Assembly-CSharp.dll: the cross-module GATE — asserted at the plan level ─────────────
    {
        var module = ModuleDefinition.ReadModule(Copy("Assembly-CSharp.dll"),
            new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
        var plans = rewriter.Plan(module);

        Console.WriteLine($"\n>> Assembly-CSharp: {plans.Count} virtual Task slot(s)");
        foreach (var p in plans)
            Console.WriteLine($"     {(p.IsDeferred ? "DEFER " + p.WholeSlotDefer : "flip x" + p.Members.Count),-24}  root {Root(p)}");

        // Backend`1::OpenSession's slot must root to the CROSS-MODULE BackendBase`1 (in ToyGame.Core) and be
        // flippable (a NewSlot in another GAME proxy module flips in lockstep).
        var cross = plans.FirstOrDefault(p =>
        {
            var rd = ((CecilSlotMethod)p.Root).Definition;
            return rd.Name == "OpenSession" && rd.DeclaringType.Name == "BackendBase`1";
        });
        Check("cross-module slot present: Backend`1::OpenSession roots to BackendBase`1 (another module)", cross is not null);
        Check("cross-module slot NOT deferred — flippable via the v15 gate", cross is { IsDeferred: false },
            cross?.WholeSlotDefer?.ToString());
        Check("...and its member is the Assembly-CSharp override Backend`1::OpenSession",
            cross is not null && cross.Members.Any(m => ((CecilSlotMethod)m).Definition.DeclaringType.Name == "Backend`1"));
        module.Dispose();
    }

    // ── ToyGame.Core.dll: flip OUTCOME + round-trip + idempotency ─────────────────────────────────
    {
        var module = ModuleDefinition.ReadModule(Copy("ToyGame.Core.dll"),
            new ReaderParameters { InMemory = true, AssemblyResolver = resolver });

        var result = rewriter.RewriteModule(module);
        Console.WriteLine($"\n>> ToyGame.Core: flipped {result.Flipped}");
        foreach (var f in result.Flips) Console.WriteLine($"     FLIP  {f}");

        var root = FindMethod(module, "BackendBase`1", "OpenSession");
        Check("after pass, BackendBase`1::OpenSession is System.Threading.Tasks.Task`1",
            root?.ReturnType.FullName.StartsWith("System.Threading.Tasks.Task`1") == true, root?.ReturnType.FullName);

        // Round-trip: the rewritten module must WRITE and RE-READ with the flip intact (structurally-sound IL).
        string outPath = Path.Combine(work, "ToyGame.Core.patched.dll");
        module.Write(outPath);
        module.Dispose();
        var reloaded = ModuleDefinition.ReadModule(outPath, new ReaderParameters { AssemblyResolver = resolver });
        var reRoot = FindMethod(reloaded, "BackendBase`1", "OpenSession");
        Check("round-trip: flip survives Cecil write + re-read",
            reRoot?.ReturnType.FullName.StartsWith("System.Threading.Tasks.Task`1") == true);

        // Idempotency: a second pass over the flipped module flips nothing (every member already System.Task).
        var again = rewriter.RewriteModule(reloaded);
        Check("idempotent: re-running the pass flips 0", again.Flipped == 0, $"flipped {again.Flipped}");
        reloaded.Dispose();
    }

    // ── non-virtual Task flip (separate pass): Game::Commit (plain non-virtual Task) flips alone ──
    {
        var module = ModuleDefinition.ReadModule(Copy("Assembly-CSharp.dll"),
            new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
        rewriter.RewriteModule(module);
        var commit = FindMethod(module, "Game", "Commit");
        Check("non-virtual Game::Commit flips to System.Threading.Tasks.Task",
            commit?.ReturnType.FullName == "System.Threading.Tasks.Task", commit?.ReturnType.FullName);
        // A generic METHOD (LoadTyped<T>: Task<!!0>) flips too: its bare own-param !!0 is preserved raw as
        // System.Threading.Tasks.Task`1<!!0>. Regression guard: the non-virtual generic path keeps the method's
        // OWN generic-parameter context (owner stays the method, not stripped by import).
        var loadTyped = FindMethod(module, "Game", "LoadTyped");
        if (loadTyped is not null)
        {
            var lt = loadTyped.ReturnType as GenericInstanceType;
            Check("generic-method Game::LoadTyped flips to System.Threading.Tasks.Task`1 (v17 non-virtual generic)",
                lt?.ElementType.FullName == "System.Threading.Tasks.Task`1", loadTyped.ReturnType.FullName);
            Check("...and LoadTyped's Task element is still the method's OWN generic parameter (!!0 preserved raw)",
                lt?.GenericArguments[0].IsGenericParameter == true
                && ((GenericParameter)lt.GenericArguments[0]).Owner == loadTyped);
        }
        module.Dispose();
    }

    // ── container-return flip (at the IL-rewrite boundary): Game::GetHighScores (Il2CppSystem.List<int>) ──
    {
        var module = ModuleDefinition.ReadModule(Copy("Assembly-CSharp.dll"),
            new ReaderParameters { InMemory = true, AssemblyResolver = resolver });

        var result = new ContainerReturnRewriter().RewriteModule(module);
        Console.WriteLine($"\n>> container pass on Assembly-CSharp: flipped {result.Flipped}");
        foreach (var f in result.Flips) Console.WriteLine($"     FLIP  {f}");

        var ghs = FindMethod(module, "Game", "GetHighScores");
        Check("Game::GetHighScores flips to System.Collections.Generic.List`1",
            ghs?.ReturnType.FullName.StartsWith("System.Collections.Generic.List`1") == true, ghs?.ReturnType.FullName);
        // The spliced call is present in the body — the flip materializes via Il2CppMarshal.ToManaged, not a bare
        // signature change (a signature-only flip would return the raw il2cpp List typed as System.List — a lie).
        Check("...and its body calls Inutil.Marshal.Il2CppMarshal::ToManaged",
            ghs?.Body?.Instructions.Any(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "Inutil.Marshal.Il2CppMarshal" && mr.Name == "ToManaged") == true);

        // Dictionary + ValueTuple flip the same way (reference-type proxies, no box).
        var gt = FindMethod(module, "Game", "GetTallies");
        Check("Game::GetTallies flips to System.Collections.Generic.Dictionary`2",
            gt?.ReturnType.FullName.StartsWith("System.Collections.Generic.Dictionary`2") == true, gt?.ReturnType.FullName);
        var gm = FindMethod(module, "Game", "GetMetrics");
        Check("Game::GetMetrics flips to System.ValueTuple`2",
            gm?.ReturnType.FullName.StartsWith("System.ValueTuple`2") == true, gm?.ReturnType.FullName);
        // VIRTUAL container slot (Entity/Boss::Ranks): flipped in LOCKSTEP through the SAME VirtualSlotPlanner the Task
        // and Nullable families use — the base decl AND the override, or the vtable slot's return is inconsistent (the
        // override no longer matches the base). The container family's virtual half through the shared slot engine.
        var entRk = FindMethod(module, "Entity", "Ranks");
        var bossRk = FindMethod(module, "Boss", "Ranks");
        Check("virtual Entity::Ranks + Boss::Ranks flip to System.Collections.Generic.List`1 in LOCKSTEP",
            entRk?.ReturnType.FullName.StartsWith("System.Collections.Generic.List`1") == true
            && bossRk?.ReturnType.FullName.StartsWith("System.Collections.Generic.List`1") == true,
            $"Entity={entRk?.ReturnType.FullName}, Boss={bossRk?.ReturnType.FullName}");
        // The override's body materializes via ToManaged too (a per-member ret-tail splice, not a bare signature change
        // — a signature-only flip would return the raw il2cpp list typed as System.List, a lie).
        Check("...and Boss::Ranks body calls Inutil.Marshal.Il2CppMarshal::ToManaged (per-member ret-tail splice)",
            bossRk?.Body?.Instructions.Any(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "Inutil.Marshal.Il2CppMarshal" && mr.Name == "ToManaged") == true);
        // Arrays are intentionally left alone (free via Il2CppInterop op_Implicit + regression targets).
        var gc = FindMethod(module, "Game", "GetCombo");
        Check("Game::GetCombo is NOT flipped (arrays stay Il2CppStructArray — free)",
            gc?.ReturnType.FullName.Contains("Il2CppStructArray") == true, gc?.ReturnType.FullName);

        // UNIFIED param pass (ParamRewriter): ONE pass flips ALL param families — container / value-Nullable /
        // delegate, virtual + non-virtual — through ONE detection (ParamFlipResolver) + ONE splice (ParamFlip):
        //   * Game::SetInventory(List<string>)            — container param, ToIl2CppTyped dematerialize
        //   * Game::GrantGold(Il2CppSystem.Nullable<int>)  — value-Nullable param, ToIl2CppTyped dematerialize
        //   * Game::ForEachScore(Il2CppSystem.Action<int>) — delegate param, the wrapper's OWN op_Implicit
        var pres = new ParamRewriter().RewriteModule(module);
        foreach (var f in pres.Flips) Console.WriteLine($"     FLIP  {f}");

        var si = FindMethod(module, "Game", "SetInventory");
        Check("Game::SetInventory param flips to System.Collections.Generic.List`1 (container, unified pass)",
            si?.Parameters[0].ParameterType.FullName.StartsWith("System.Collections.Generic.List`1") == true, si?.Parameters[0].ParameterType.FullName);
        Check("...and SetInventory body calls Il2CppMarshal::ToIl2CppTyped (container dematerialize)",
            si?.Body?.Instructions.Any(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "Inutil.Marshal.Il2CppMarshal" && mr.Name == "ToIl2CppTyped") == true);

        var gg = FindMethod(module, "Game", "GrantGold");
        Check("Game::GrantGold param flips to System.Nullable`1 (value-Nullable, unified pass)",
            gg?.Parameters[0].ParameterType.FullName.StartsWith("System.Nullable`1") == true, gg?.Parameters[0].ParameterType.FullName);
        Check("...and GrantGold body calls Il2CppMarshal::ToIl2CppTyped (Nullable dematerialize)",
            gg?.Body?.Instructions.Any(i => i.Operand is MethodReference nmr
                && nmr.DeclaringType.FullName == "Inutil.Marshal.Il2CppMarshal" && nmr.Name == "ToIl2CppTyped") == true);

        var fe = FindMethod(module, "Game", "ForEachScore");
        Check("Game::ForEachScore param flips to System.Action`1 (delegate, unified pass)",
            fe?.Parameters[0].ParameterType.FullName.StartsWith("System.Action`1") == true, fe?.Parameters[0].ParameterType.FullName);
        Check("...and ForEachScore body calls the wrapper's op_Implicit (delegate dematerialize)",
            fe?.Body?.Instructions.Any(i => i.Operand is MethodReference mr
                && mr.Name == "op_Implicit" && mr.DeclaringType is GenericInstanceType) == true);

        // Idempotent: a flipped param is System.* (no longer a candidate), so a second unified pass flips 0.
        Check("idempotent: re-running the unified param pass flips 0", new ParamRewriter().RewriteModule(module).Flipped == 0);

        // VIRTUAL container-PARAM lockstep (Entity/Boss::Muster) — the unified param pass flips a vtable slot's PARAM
        // through the SAME VirtualSlotPlanner the return families use, base + override together (a half-flip fails to
        // load). Guarded on presence: Muster ships only once ToyGame is rebuilt with the fixture; the in-game battery
        // (param.container.virtual.flip.*) is the hard gate that it flipped + runs.
        var entM = FindMethod(module, "Entity", "Muster");
        var bossM = FindMethod(module, "Boss", "Muster");
        if (entM is not null && bossM is not null)
            Check("virtual Entity::Muster + Boss::Muster PARAM flip to System.List in LOCKSTEP",
                entM.Parameters[0].ParameterType.FullName.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal)
                && bossM.Parameters[0].ParameterType.FullName.StartsWith("System.Collections.Generic.List`1", StringComparison.Ordinal),
                $"Entity={entM.Parameters[0].ParameterType.FullName}, Boss={bossM.Parameters[0].ParameterType.FullName}");

        // value-type Nullable RETURN (full-body replace): Game::FindSpawn (Il2CppSystem.Nullable<Vec3>) ->
        // System.Nullable<Vec3>, its body ABANDONED for a call to the null-aware SDK invoke (Il2CppInterop's own
        // value-type Nullable return is broken both ways, so a ret-tail splice can't salvage it).
        var nres = new NullableReturnRewriter().RewriteModule(module);
        foreach (var f in nres.Flips) Console.WriteLine($"     FLIP  {f}");
        var fspawn = FindMethod(module, "Game", "FindSpawn");
        Check("Game::FindSpawn flips to System.Nullable`1",
            fspawn?.ReturnType.FullName.StartsWith("System.Nullable`1") == true, fspawn?.ReturnType.FullName);
        Check("...and its body calls Inutil.Marshal.ValueTypeBridge::InvokeNullableStructReturn (body-replace)",
            fspawn?.Body?.Instructions.Any(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "Inutil.Marshal.ValueTypeBridge" && mr.Name == "InvokeNullableStructReturn") == true);
        // VIRTUAL value-Nullable slot (Entity/Boss::Waypoint): flipped in LOCKSTEP through the SAME VirtualSlotPlanner
        // the Task family uses — the base decl AND the override, or the vtable slot's signature is inconsistent.
        var entWp = FindMethod(module, "Entity", "Waypoint");
        var bossWp = FindMethod(module, "Boss", "Waypoint");
        Check("virtual Entity::Waypoint + Boss::Waypoint flip to System.Nullable`1 in LOCKSTEP",
            entWp?.ReturnType.FullName.StartsWith("System.Nullable`1") == true
            && bossWp?.ReturnType.FullName.StartsWith("System.Nullable`1") == true,
            $"Entity={entWp?.ReturnType.FullName}, Boss={bossWp?.ReturnType.FullName}");
        Check("idempotent: re-running the nullable pass flips 0", new NullableReturnRewriter().RewriteModule(module).Flipped == 0);

        // Nullable ACCESSOR pass, METHOD-backed arm (Player::Waypoint / Player::Backpack). An auto-property yields
        // TWO proxy members over one storage: a FIELD-backed pair over <X>k__BackingField and a METHOD-backed pair
        // for the property itself. Only the first used to flip — the pre-check demanded a NativeFieldInfoPtr a real
        // property's setter never has, so the whole property deferred (getter included, via all-or-nothing).
        //
        // AUDIT FIRST, while the module is still unflipped for these members: the residual audit must SEE them as
        // unexplained holes. A check that only ever runs against the fixed state cannot tell "covered" from "never
        // looked" — which is precisely the failure the audit exists to catch.
        var preAudit = ResidualNullableAudit.Scan(module);
        Check("audit SEES the unflipped method-backed accessors as unexplained holes (pre-flip)",
            preAudit.Any(x => x.Member.EndsWith("::Waypoint", StringComparison.Ordinal) && x.Unexplained)
            && preAudit.Any(x => x.Member.EndsWith("::Backpack", StringComparison.Ordinal) && x.Unexplained),
            $"pre-flip unexplained: [{string.Join(", ", preAudit.Where(x => x.Unexplained).Select(x => x.Member))}]");

        var nfres = new NullableFieldRewriter().RewriteModule(module);
        foreach (var f in nfres.Flips) Console.WriteLine($"     FLIP  {f}");

        var playerT = module.GetTypes().First(t => t.Name == "Player");
        PropertyDefinition? wp = playerT.Properties.FirstOrDefault(p => p.Name == "Waypoint");
        PropertyDefinition? bp = playerT.Properties.FirstOrDefault(p => p.Name == "Backpack");
        // VALUE rung -> System.Nullable<Vec3>; REF-BEARING rung -> the BARE proxy (System.Nullable<class> is illegal).
        Check("Player::Waypoint (method-backed accessor) flips to System.Nullable`1",
            wp?.PropertyType.FullName.StartsWith("System.Nullable`1", StringComparison.Ordinal) == true, wp?.PropertyType.FullName);
        Check("Player::Backpack (method-backed, ref-bearing) flips to the bare Loadout proxy",
            bp?.PropertyType.FullName.EndsWith("Loadout", StringComparison.Ordinal) == true, bp?.PropertyType.FullName);
        // get/set/property must agree — a half-flip still LOADS, so only an explicit check catches it.
        Check("...and Waypoint's accessors agree with the property type (no half-flip)",
            wp?.GetMethod?.ReturnType.FullName == wp?.PropertyType.FullName
            && wp?.SetMethod?.Parameters[0].ParameterType.FullName == wp?.PropertyType.FullName,
            $"get={wp?.GetMethod?.ReturnType.FullName}, set={wp?.SetMethod?.Parameters[0].ParameterType.FullName}");
        Check("...and Backpack's accessors agree with the property type (no half-flip)",
            bp?.GetMethod?.ReturnType.FullName == bp?.PropertyType.FullName
            && bp?.SetMethod?.Parameters[0].ParameterType.FullName == bp?.PropertyType.FullName,
            $"get={bp?.GetMethod?.ReturnType.FullName}, set={bp?.SetMethod?.Parameters[0].ParameterType.FullName}");
        // The setter keeps its invoke body and gains the entry dematerialization — a signature-only flip would hand
        // the natural value straight to il2cpp_object_unbox. The ref-bearing rung must use the DEDICATED helper.
        Check("...and set_Waypoint body calls Il2CppMarshal::ToIl2CppTyped (spliced param dematerialize)",
            wp?.SetMethod?.Body?.Instructions.Any(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "Inutil.Marshal.Il2CppMarshal" && mr.Name == "ToIl2CppTyped") == true);
        Check("...and set_Backpack body calls ValueTypeBridge::RefToNullable (ref-bearing box rebuild)",
            bp?.SetMethod?.Body?.Instructions.Any(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "Inutil.Marshal.ValueTypeBridge" && mr.Name == "RefToNullable") == true);
        // The getter is a TAIL-SWAP over the same invoke body: the broken `newobj Nullable<T>(ptr)` becomes the
        // null-aware read (runtime_invoke boxes a Nullable return exactly as the field box does).
        Check("...and get_Backpack body calls ValueTypeBridge::BoxedToRefNullable (getter tail-swap)",
            bp?.GetMethod?.Body?.Instructions.Any(i => i.Operand is MethodReference mr
                && mr.DeclaringType.FullName == "Inutil.Marshal.ValueTypeBridge" && mr.Name == "BoxedToRefNullable") == true);
        Check("idempotent: re-running the nullable ACCESSOR pass flips 0",
            new NullableFieldRewriter().RewriteModule(module).Flipped == 0);

        // ...and now the audit must report them GONE. Both directions asserted, so neither a blind audit nor an
        // unflipped member can pass silently.
        var postAudit = ResidualNullableAudit.Scan(module);
        Check("audit reports NO unexplained residual after the accessor pass",
            !postAudit.Any(x => x.Unexplained),
            $"unexplained: [{string.Join(", ", postAudit.Where(x => x.Unexplained).Select(x => x.Member + " — " + x.Why))}]");

        // STATIC field-backed setter must DEFER, not miscompile. The rebuild is instance-only (it emits `ldarg.0` as
        // `this` and calls WriteNullableField(Il2CppObjectBase, …)), but a static setter's arg0 is the VALUE and a
        // static il2cpp field has no object — so emitting it produces INVALID IL that throws InvalidProgramException
        // the moment a mod calls it. It shipped that way: three static setters in EFT's proxies carry such a body
        // (e.g. EFT.AudioUtils::set__cachedDspScheduleLookaheadSec). ToyGame has no static Nullable field, so the
        // shape is manufactured from a REAL Il2CppInterop accessor body — the fixture cannot supply it, and a
        // hand-built body would prove nothing about the real generator's output.
        {
            var statModule = ModuleDefinition.ReadModule(Copy("Assembly-CSharp.dll"),
                new ReaderParameters { InMemory = true, AssemblyResolver = resolver });
            var stPlayer = statModule.GetTypes().First(t => t.Name == "Player");
            var stProp = stPlayer.Properties.First(p => p.Name.Contains("Waypoint", StringComparison.Ordinal)
                                                        && p.Name.Contains("BackingField", StringComparison.Ordinal));
            stProp.SetMethod!.IsStatic = true;      // same body, now a static accessor — the shape the pass must decline

            var stRes = new NullableFieldRewriter().RewriteModule(statModule);
            Check("static field-backed Nullable setter DEFERS (never a mis-emitted instance body)",
                stRes.Defers.Any(d => d.Contains("static field-backed setter", StringComparison.Ordinal)),
                $"defers: [{string.Join(" | ", stRes.Defers)}]");
            Check("...and the static property is left UNFLIPPED rather than half-rewritten",
                stProp.PropertyType.FullName.Contains("Il2CppSystem.Nullable", StringComparison.Ordinal)
                && stProp.SetMethod.Body.Instructions.All(i => i.Operand is not MethodReference smr
                    || smr.Name is not ("WriteNullableField" or "WriteNullableRefField")),
                stProp.PropertyType.FullName);
            // The audit must call it a KNOWN deferral, not a hole — the reason is real and recorded.
            var stAudit = ResidualNullableAudit.Scan(statModule);
            Check("...and the audit reports it known-deferred (static), not unexplained",
                stAudit.Any(x => x.Member.Contains("Waypoint", StringComparison.Ordinal) && !x.Unexplained
                                 && x.Why.Contains("static", StringComparison.Ordinal)),
                $"[{string.Join(" | ", stAudit.Where(x => x.Member.Contains("Waypoint", StringComparison.Ordinal)).Select(x => x.Member + " => " + x.Why))}]");
            statModule.Dispose();
        }

        // Round-trip: the rewritten module must WRITE and RE-READ with the flip intact (structurally-sound IL).
        string outPath = Path.Combine(work, "acs-container.patched.dll");
        module.Write(outPath);
        module.Dispose();
        var reloaded = ModuleDefinition.ReadModule(outPath, new ReaderParameters { AssemblyResolver = resolver });
        var reGhs = FindMethod(reloaded, "Game", "GetHighScores");
        Check("container flip survives Cecil write + re-read",
            reGhs?.ReturnType.FullName.StartsWith("System.Collections.Generic.List`1") == true);

        // Idempotency: a second container pass over the flipped module flips nothing (the return is now System.*).
        var again = new ContainerReturnRewriter().RewriteModule(reloaded);
        Check("idempotent: re-running the container pass flips 0", again.Flipped == 0, $"flipped {again.Flipped}");
        reloaded.Dispose();
    }

    // ── PatchDirectory: the directory driver — atomic in-place write + lockstep + idempotency ──
    {
        // A fresh dir holding BOTH game modules (the cross-module pair must be patched together, or a half-flip).
        string dir = Path.Combine(work, "interop");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(interopDir, "Assembly-CSharp.dll"), Path.Combine(dir, "Assembly-CSharp.dll"), true);
        File.Copy(Path.Combine(interopDir, "ToyGame.Core.dll"), Path.Combine(dir, "ToyGame.Core.dll"), true);

        var r = InteropPatcher.PatchDirectory(dir);
        Console.WriteLine($"\n>> PatchDirectory: {r.TotalFlipped} flipped across {r.Patched.Count} DLL(s)");
        foreach (var (dll, res) in r.Patched) Console.WriteLine($"     {dll}: {res.Flipped}");

        Check("no .inutil-tmp left behind (atomic write cleaned up)",
            !Directory.EnumerateFiles(dir, "*.inutil-tmp").Any());

        // Robust OUTCOME (no assumption about the source tree's starting flip-state): after the pass BOTH game
        // modules are flipped ON DISK in lockstep, and the atomic rename landed structurally-sound, re-readable proxies.
        var acs = ModuleDefinition.ReadModule(Path.Combine(dir, "Assembly-CSharp.dll"), new ReaderParameters { AssemblyResolver = resolver });
        var core = ModuleDefinition.ReadModule(Path.Combine(dir, "ToyGame.Core.dll"), new ReaderParameters { AssemblyResolver = resolver });
        bool coreFlipped = FindMethod(core, "BackendBase`1", "OpenSession")?.ReturnType.FullName?.StartsWith("System.Threading.Tasks.Task`1") == true;
        bool acsFlipped = FindMethod(acs, "Backend`1", "OpenSession")?.ReturnType.FullName?.StartsWith("System.Threading.Tasks.Task`1") == true;
        acs.Dispose(); core.Dispose();
        Check("on-disk ToyGame.Core BackendBase`1 root is System.Task`1 after PatchDirectory", coreFlipped);
        Check("on-disk Assembly-CSharp Backend`1 override is System.Task`1 (cross-module lockstep on disk)", acsFlipped);

        // Idempotent: a second directory-patch over the now-patched tree writes nothing.
        var r2 = InteropPatcher.PatchDirectory(dir);
        Check("idempotent: re-patching the directory flips 0", r2.TotalFlipped == 0, $"flipped {r2.TotalFlipped}");

        // ── marker (docs/contribution/architecture/16-metadata.md): the patch stamps the content-addressed marker the loader shim reads.
        // The offline half of "the silent unpatched-proxy hole is closed"; the in-game battery (interop.marker.*) is
        // the end-to-end gate that the SAME hash the net9 CLI stamped verifies Current in the net6 runtime.
        string expected = SchemaMarker.Hash(Families.Default());
        Check("PatchDirectory result carries the schema hash", r.SchemaHash == expected, r.SchemaHash);
        Check("marker file present in the interop dir after patch",
            File.Exists(Path.Combine(dir, SchemaMarker.InteropMarkerFileName)));
        Check("marker Verify -> Current for this build's registry",
            ContentMarker.Verify(dir, SchemaMarker.InteropMarkerFileName, expected) == MarkerVerdict.Current);
        Check("re-patch keeps the marker Current (idempotent stamp)",
            ContentMarker.Verify(dir, SchemaMarker.InteropMarkerFileName, r2.SchemaHash) == MarkerVerdict.Current);

        // A dir that was never patched reads Missing — the "unpatched proxies" fail-loud the shim warns on.
        string unpatched = Path.Combine(work, "unpatched");
        Directory.CreateDirectory(unpatched);
        Check("unpatched dir marker Verify -> Missing",
            ContentMarker.Verify(unpatched, SchemaMarker.InteropMarkerFileName, expected) == MarkerVerdict.Missing);

        // A marker stamped by a DIFFERENT schema reads Stale — the "patched by another inutil" fail-loud.
        string stale = Path.Combine(work, "stale");
        ContentMarker.Stamp(stale, SchemaMarker.InteropMarkerFileName, "deadbeef", "foreign schema");
        Check("foreign-hash marker Verify -> Stale",
            ContentMarker.Verify(stale, SchemaMarker.InteropMarkerFileName, expected) == MarkerVerdict.Stale);
    }

    Console.WriteLine(failures == 0
        ? "\nINTEROP-PATCH TESTS GREEN — the pure engine drives a real Cecil rewrite: cross-module gate (v15), flip + round-trip, idempotent."
        : $"\nINTEROP-PATCH TESTS RED — {failures} wrong.");
    return failures == 0 ? 0 : 1;
}
finally
{
    try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
}

string Copy(string name)
{
    string dst = Path.Combine(work, name);
    File.Copy(Path.Combine(interopDir, name), dst, overwrite: true);
    return dst;
}

static MethodDefinition? FindMethod(ModuleDefinition module, string typeName, string methodName)
    => module.GetTypes().FirstOrDefault(t => t.Name == typeName)?.Methods.FirstOrDefault(m => m.Name == methodName);

// null (not a throw) when .unity-build is absent — an unprovisioned box still runs the pure locator tests above.
//
// Prefers interop.pristine — the unpatched snapshot setup-bepinex takes at generation time. The live interop/ is
// patched in place by the first bepinex-validate (or any manual patch), which would make this suite refuse to run
// on any box that has ever validated; the snapshot is what keeps a pristine input available without a reprovision.
// Falls back to interop/ so a tree provisioned before the snapshot existed still runs (and the marker gate above
// tells it what to do if that one is already patched).
static string? FindInteropDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".unity-build"))) dir = dir.Parent;
    if (dir is null) return null;
    string bep = Path.Combine(dir.FullName, ".unity-build", "loaders", "bepinex", "BepInEx");
    string pristine = Path.Combine(bep, "interop.pristine");
    return Directory.Exists(pristine) ? pristine : Path.Combine(bep, "interop");
}
