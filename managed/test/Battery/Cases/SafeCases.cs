using Il2CppInterop.Runtime;   // IL2CPP.* — game-agnostic il2cpp C ABI (no game proxy referenced)

namespace Inutil.Battery;

// docs/contribution/architecture/17-reach-faces.md — Inutil.Safe, the fault-safe scope, END-TO-END in a booted IL2CPP game: the FIRST managed
// consumer of inutil_core's SEH fault-guard (before this it was built, Wine-proven, and wired to nothing —
// docs/contribution/architecture/17-reach-faces.md). Two shapes, one guard:
//   • Safe.TryInvoke (Shape A) — the proven substrate over the REAL il2cpp_runtime_invoke (pure-native risky
//     window, exactly what guard_smoke validated).
//   • Safe.Run(delegate) (Shape B) — the ergonomic face; a managed delegate runs inside the guard via a
//     reverse-P/Invoke trampoline, so a caught fault's longjmp also unwinds a CLR transition frame. That extra
//     risk is de-risked natively by guard_managed_smoke.c and — for the CLR-frame case a C test cannot reach —
//     by `safe.run.faulted-survives` here (fault, then MORE managed work on the same thread returns Ok).
//
// Reflection-only + game-AGNOSTIC (Il2CppInterop C ABI + Inutil.Safe): targets are named by il2cpp metadata
// STRINGS ("ToyGame"/"Player"/"GetHealth"), no typed proxy is bound — the battery's proxy-free compile closure,
// like SmokeCases. (The typed seamless payoff — Safe.Run(() => player.TakeDamage(5)) on a real proxy — is the
// natural companion to the P2 task-2 generated live accessors, which already need a proxy-coupled consumer.)
//
// Independent of the hook engine: the guard self-initializes on first call (interceptor.h), so these do NOT
// require HookCases' Attach. Registered LAST in the Suite so the two DELIBERATE caught faults here (leaf faults,
// benign taint) cannot perturb an earlier case. Within this file the clean cases
// run before the faulting ones for the same reason.
public static class SafeCases
{
    public static void Register(Suite suite)
    {
        // (1) SUBSTRATE happy path — Safe.TryInvoke actually INVOKES a real method under the guard and returns
        //     the right boxed value. A fresh (zero-init, no-ctor) Player has Health = 0, so GetHealth() -> 0.
        suite.Add("safe.tryinvoke.ok", () =>
        {
            nint klass = ResolveClass("ToyGame", "Player");
            Check.True(klass != 0, "ToyGame.Player did not resolve — il2cpp not up or metadata missing");
            nint method = ResolveMethod(klass, "GetHealth", 0);
            Check.True(method != 0, "GetHealth(0 args) not found on ToyGame.Player or its ancestors");
            nint obj = IL2CPP.il2cpp_object_new(klass);   // zero-init, NO ctor runs -> Health defaults to 0
            Check.True(obj != 0, "il2cpp_object_new(Player) returned null");

            InvokeResult r = Inutil.Safe.TryInvoke(method, obj);
            Check.True(r.Ok, $"guarded GetHealth should be Ok, got {r}");
            int hp = r.Unbox<int>();
            Check.True(hp == 0, $"zero-init Player.GetHealth() should be 0, got {hp}");
            return $"Safe.TryInvoke(GetHealth) on a fresh Player -> Ok, health={hp}";
        });

        // (2) FACE happy path — Safe.Run runs a clean managed+il2cpp delegate through the reverse-P/Invoke
        //     trampoline + guard and reports Ok; the value-returning form carries the delegate's managed result.
        suite.Add("safe.run.ok", () =>
        {
            InvokeResult r = Inutil.Safe.Run(() =>
            {
                if (ResolveClass("ToyGame", "Player") == 0)   // a nested il2cpp P/Invoke, clean, under the guard
                    throw new Exception("Player not resolvable inside the guarded scope");
            });
            Check.True(r.Ok, $"clean Safe.Run should be Ok, got {r}");

            SafeResult<int> rv = Inutil.Safe.Run(() => 6 * 7);
            Check.True(rv.Ok && rv.Value == 42, $"Safe.Run<int>(6*7) should be Ok value=42, got {rv}");
            return $"Safe.Run(clean) -> Ok; Safe.Run<int>(6*7) -> {rv.Value}";
        });

        // ── docs/contribution/architecture/17-reach-faces.md: the raw by-name / discovery surface for the irreducible cases (escape hatch, not default) ──

        // (a) Probe (the guard_READ side, the twin of Safe's guarded invoke): a freshly-allocated object is a live
        //     object; a GARBAGE handle (0x10) is NOT — caught by inutil_guard_read, not a crash. Describe answers
        //     "what is it?" for a suspect handle. This is how by-name validates an erased base ref before reaching into it.
        suite.Add("probe.live-vs-garbage", () =>
        {
            nint klass = ResolveClass("ToyGame", "Player");
            Check.True(klass != 0, "ToyGame.Player did not resolve");
            nint live = IL2CPP.il2cpp_object_new(klass);
            Check.True(Inutil.Probe.IsLiveObject(live), "a freshly-allocated Player must read as a live object");
            Check.True(!Inutil.Probe.IsLiveObject((nint)0x10), "a garbage handle 0x10 must be NOT live — caught by the guard, not a crash");
            ProbeResult d = Inutil.Probe.Describe(live);
            Check.True(d.Valid && d.KlassName == "ToyGame.Player", $"Describe: valid={d.Valid} klass='{d.KlassName}' (expected ToyGame.Player)");
            return $"Probe: live Player=true, garbage 0x10=false (survived); Describe -> {d}";
        });

        // (b) THE headline — reach an ERASED Il2CppSystem.Object handle BY NAME. Hold a Player only as the base
        //     type (a hook arg / collection element whose concrete type you cannot name at the call site); Fields and
        //     Invoke both resolve on the RUNTIME class, so a by-name field write + a by-name guarded method call reach
        //     it and agree. This is where by-name survives because a typed face is structurally impossible.
        suite.Add("invoke.erased-handle", () =>
        {
            nint klass = ResolveClass("ToyGame", "Player");
            Check.True(klass != 0, "ToyGame.Player did not resolve");
            nint objPtr = IL2CPP.il2cpp_object_new(klass);
            Check.True(objPtr != 0, "il2cpp_object_new(Player) returned null");
            var erased = new Il2CppSystem.Object(objPtr);   // static type = the base; runtime type = Player

            Check.True(Inutil.Fields.SetInt(erased, "Health", 123), "Fields.SetInt('Health') should resolve on the erased handle's runtime class");
            int viaField = Inutil.Fields.GetInt(erased, "Health");
            Check.True(viaField == 123, $"Fields.GetInt('Health') via the erased handle got {viaField}, expected 123");

            InvokeResult r = Inutil.Invoke.Call(erased, "GetHealth");
            Check.True(r.Ok, $"Invoke.Call('GetHealth') on the erased handle should be Ok, got {r}");
            int viaMethod = r.Unbox<int>();
            Check.True(viaMethod == 123, $"GetHealth via the erased handle should read the field we set (123), got {viaMethod}");

            return $"erased Il2CppSystem.Object -> Fields.SetInt/GetInt('Health')=123 + Invoke.Call('GetHealth')=Ok {viaMethod} "
                 + "(reached by name via the runtime class — the concrete type is unnamed at the call site)";
        });

        // (c) Introspect — the discovery that makes (b) workable: you can't write `obj.Health` for a type you can't
        //     name, but you CAN Dump/Methods it to learn the names, then reach them. The REPL/dev companion.
        suite.Add("introspect.dump", () =>
        {
            nint klass = ResolveClass("ToyGame", "Player");
            Check.True(klass != 0, "ToyGame.Player did not resolve");
            var erased = new Il2CppSystem.Object(IL2CPP.il2cpp_object_new(klass));
            string dump = Inutil.Introspect.Dump(erased);
            Check.True(dump.Contains("ToyGame.Player") && dump.Contains("Health"), $"Dump must name the runtime class + list Health; got:\n{dump}");
            string methods = Inutil.Introspect.Methods(erased);
            Check.True(methods.Contains("GetHealth"), $"Methods must list GetHealth; got:\n{methods}");
            return "Introspect.Dump/Methods on an erased handle discover 'ToyGame.Player' + the Health field + the GetHealth method";
        });

        // (3) SUBSTRATE fault caught — v1's money-shot: a non-null but GARBAGE methodInfo (0x10, in the always-
        //     reserved null page) faults at il2cpp_runtime_invoke's ENTRY reading the bogus method struct. The
        //     guard turns the c0000005 CoreCLR can't catch into a Faulted verdict; the process survives.
        suite.Add("safe.tryinvoke.faulted-survives", () =>
        {
            InvokeResult r = Inutil.Safe.TryInvoke((nint)0x10, (nint)0);
            Check.True(r.Faulted, $"garbage methodInfo 0x10 should Fault, got {r}");
            Check.True(r.FaultCode == 0xC0000005, $"expected access violation 0xC0000005, got 0x{r.FaultCode:x}");
            return $"Safe.TryInvoke(garbage 0x10) -> Faulted 0x{r.FaultCode:x8} @0x{r.FaultAddr:x} (survived)";
        });

        // (4) FACE fault caught + THE CLR-frame de-risk (the part guard_managed_smoke.c structurally cannot
        //     reach). The delegate does a RAW il2cpp deref of a garbage object (reads its klass slot at 0x10) ->
        //     a hardware fault INSIDE il2cpp, under a real managed frame + the reverse-P/Invoke trampoline. The
        //     guard's longjmp unwinds that CLR transition frame; if that corrupts the thread's CLR frame chain,
        //     the SECOND Safe.Run below — which re-enters the same reverse-P/Invoke path — crashes or misbehaves.
        //     Its Ok is the proof the frame chain survived. Registered last; a leaf read fault holds no il2cpp
        //     lock (benign taint), and the recovery is proven THROUGH the guard, not by touching raw il2cpp.
        suite.Add("safe.run.faulted-survives", () =>
        {
            InvokeResult r = Inutil.Safe.Run(() => { IL2CPP.il2cpp_object_get_class((nint)0x10); });   // faults reading klass @0x10 (Action: return discarded)
            Check.True(r.Faulted, $"Safe.Run over a faulting il2cpp call should Fault, got {r}");
            Check.True(r.FaultCode == 0xC0000005, $"expected access violation 0xC0000005, got 0x{r.FaultCode:x}");

            SafeResult<int> after = Inutil.Safe.Run(() => 21 + 21);   // MORE managed work, same thread, through Safe.Run again
            Check.True(after.Ok && after.Value == 42,
                $"post-fault Safe.Run should be Ok value=42 (CLR frame chain survived the longjmp), got {after}");
            return $"Safe.Run(fault) -> Faulted 0x{r.FaultCode:x8}; post-fault Safe.Run<int> -> Ok value={after.Value} (frame chain intact)";
        });
    }

    // Resolve a game class by its il2cpp (not managed-proxy) name; try both image spellings (version-sensitive),
    // mirroring SmokeCases. Returns 0 if unresolved.
    static nint ResolveClass(string @namespace, string name)
    {
        foreach (var image in new[] { "Assembly-CSharp.dll", "Assembly-CSharp" })
        {
            nint k = IL2CPP.GetIl2CppClass(image, @namespace, name);
            if (k != 0) return k;
        }
        return 0;
    }

    // Resolve a method by name + arg count on `klass` or any ancestor (il2cpp_class_get_method_from_name keys on
    // name+argc only). 0 if no class in the chain has it.
    static nint ResolveMethod(nint klass, string method, int argc)
    {
        for (nint k = klass; k != 0; k = IL2CPP.il2cpp_class_get_parent(k))
        {
            nint m = IL2CPP.il2cpp_class_get_method_from_name(k, method, argc);
            if (m != 0) return m;
        }
        return 0;
    }
}
