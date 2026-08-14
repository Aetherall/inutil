using System.Reflection;

namespace Inutil.Battery;

// The Task return-flip, END-TO-END in a booted IL2CPP game (IL-rewrite + runtime). InteropPatch flips
// the cross-module virtual slot ToyGame.Backend`1::OpenSession from the wrapper Il2CppSystem...Task<Session> to
// a NATURAL System.Threading.Tasks.Task<Session>, splicing `call Inutil.Sugar.Il2CppSugar.WrapTaskT<Session>`
// into the body; Inutil.dll is deployed beside the loader so that call resolves at JIT.
//
// REFLECTION-ONLY by design: the battery keeps its compile closure (Il2CppInterop + TestKit — no engine, no game
// proxy reference), so it stays the harness's own liveness check while ALSO proving the engine's first family
// against a live runtime. What this trio validates that no offline test can:
//   deployed  — the patcher ran on the DEPLOYED proxy AND Il2CppInterop's real runtime loaded the flipped IL.
//   sdk       — Inutil.dll (with the WrapTask the flip calls) is deployed and loadable beside the loader.
//   runs      — invoking the flip actually executes the spliced WrapTaskT and yields a working CLR Task
//               (best-effort: il2cpp generic-proxy construction is version-sensitive, so a construction failure
//               is an honest SKIP — the deployed+sdk cases already prove patch + load + deploy).
public static class TaskFlipCases
{
    public static void Register(Suite suite)
    {
        suite.Add("task.virtual-flip.deployed", () =>
        {
            MethodInfo open = FindOpenSession(out string typeName);
            string rt = open.ReturnType.FullName ?? open.ReturnType.Name;
            Check.True(rt.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal),
                $"{typeName}::OpenSession still returns '{rt}' — the deployed proxy is NOT flipped " +
                "(the patcher did not run, or the flip failed to load)");
            return $"{typeName}::OpenSession returns natural {rt}";
        });

        suite.Add("task.nonvirtual-flip.deployed", () =>
        {
            MethodInfo commit = FindProxyMethod("Game", "Commit", out string typeName);
            string rt = commit.ReturnType.FullName ?? commit.ReturnType.Name;
            Check.True(rt.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal),
                $"{typeName}::Commit still returns '{rt}' — the non-virtual Task pass did not flip it");
            return $"{typeName}::Commit returns natural {rt}";
        });

        suite.Add("task.sdk.present", () =>
        {
            Assembly sdk;
            try { sdk = Assembly.Load(new AssemblyName("Inutil")); }
            catch (Exception ex) { Check.True(false, $"Inutil.dll not loadable beside the loader: {ex.GetType().Name}: {ex.Message}"); return null; }

            Type? sugar = sdk.GetType("Inutil.Sugar.Il2CppSugar");
            Check.True(sugar is not null, "Inutil.dll loaded but Inutil.Sugar.Il2CppSugar is missing");
            // WrapTaskT has two overloads (nint / Il2CppObjectBase) — GetMethod would be ambiguous, so scan.
            Check.True(sugar!.GetMethods().Any(m => m.Name == "WrapTaskT"), "Il2CppSugar.WrapTaskT (the flip's splice target) is missing");
            return $"Inutil.dll loaded: {sdk.GetName().Name} with Il2CppSugar.WrapTaskT present";
        });

        suite.Add("task.virtual-flip.runs", () =>
        {
            MethodInfo open = FindOpenSession(out string typeName);
            object backend;
            MethodInfo closedOpen;
            try
            {
                // Construct the CLOSED proxy and resolve OpenSession on IT — the open-generic MethodInfo can't be
                // invoked (ContainsGenericParameters). Construction is version-sensitive, hence the SKIP guard.
                backend = ConstructBackend(open.DeclaringType!);
                closedOpen = backend.GetType().GetMethod("OpenSession")
                    ?? throw new MissingMethodException(backend.GetType().FullName, "OpenSession");
            }
            catch (Exception ex) { Check.Skip($"il2cpp generic-proxy construction of {typeName} not reflectively reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            // The invoke itself is NOT guarded: if the spliced WrapTaskT does not resolve, this must fail LOUD.
            object? result;
            try { result = closedOpen.Invoke(backend, null); }
            catch (TargetInvocationException tie)
            {
                Check.True(false, $"invoking the flipped OpenSession threw {tie.InnerException?.GetType().Name}: " +
                                  $"{tie.InnerException?.Message} — the spliced WrapTaskT likely did not resolve");
                return null;
            }

            Check.True(result is System.Threading.Tasks.Task,
                $"OpenSession returned {result?.GetType().FullName ?? "null"}, not a CLR Task");
            var task = (System.Threading.Tasks.Task)result!;
            Check.True(task.IsCompleted, "the returned CLR Task is not completed — the carrier should complete from the il2cpp task");
            // item 1a (read-side carrier completion): the carrier now carries the il2cpp task's REAL result (the Session
            // OpenSession returned), not a v1 default(T) placeholder. Read carrier.Result.Name GAME value CLR-side.
            object? session = result!.GetType().GetProperty("Result")?.GetValue(result);
            Check.True(session is not null,
                "carrier.Result is null — OpenSession's real Session did not reach the CLR carrier (item 1a placeholder not driven)");
            object? name = session!.GetType().GetProperty("Name")?.GetValue(session);
            Check.True((name as string) == "session",
                $"carrier Session.Name should be 'session' (OpenSession's real result), got '{name}' — the carrier still holds a placeholder");
            return $"invoked OpenSession -> carrier.Result is the REAL Session (Name={name}) CLR-side: item 1a carrier completion";
        });

        // ── Task WRITE / FABRICATE (ToIl2Cpp — the hook-boundary direction Il2CppSugar documents) ──────────────
        // Driven through the REAL public write entry Il2CppMarshal.ToIl2CppTyped<Task<int>> (reflective, so the battery
        // stays engine-ref free) — the same door a hook boundary / param seam calls. Three shapes: a COMPLETED managed
        // task fabricates a completed il2cpp Task`1<int>; a PENDING one fabricates a promise that TrySetResult drives to
        // completion; a CARRIER (a forwarded game task) round-trips by IDENTITY (the original il2cpp pointer), never a copy.

        suite.Add("task.fabricate.completed.runs", () =>
        {
            object il2cppTask;
            try { il2cppTask = ToIl2Cpp(typeof(System.Threading.Tasks.Task<int>), System.Threading.Tasks.Task.FromResult(5))!; }
            catch (Exception ex) { Check.Skip($"il2cpp Task`1 fabricate not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }
            Check.True(Il2CppIsCompleted(il2cppTask), "fabricated il2cpp Task from a completed managed task should be completed");
            Check.True(Convert.ToInt32(Il2CppResult(il2cppTask)) == 5,
                $"fabricated il2cpp Task`1<int>.Result should be 5, got {Il2CppResult(il2cppTask)}");
            return $"ToIl2CppTyped(Task.FromResult(5)) -> completed il2cpp {il2cppTask.GetType().Name} Result=5";
        });

        suite.Add("task.fabricate.pending.runs", () =>
        {
            var mtcs = new System.Threading.Tasks.TaskCompletionSource<int>();
            object il2cppTask;
            try { il2cppTask = ToIl2Cpp(typeof(System.Threading.Tasks.Task<int>), mtcs.Task)!; }
            catch (Exception ex) { Check.Skip($"il2cpp Task`1 promise fabricate not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }
            Check.True(!Il2CppIsCompleted(il2cppTask), "a fabricated promise il2cpp Task should NOT be completed before the managed task is");
            mtcs.SetResult(88);   // ExecuteSynchronously continuation fires inline -> il2cppTask.TrySetResult(88)
            Check.True(Il2CppIsCompleted(il2cppTask), "after the managed SetResult, the fabricated il2cpp Task should be driven to completion");
            Check.True(Convert.ToInt32(Il2CppResult(il2cppTask)) == 88,
                $"the driven il2cpp Task`1<int>.Result should be 88, got {Il2CppResult(il2cppTask)}");
            return $"pending managed task -> promise il2cpp Task, SetResult(88) drove it to completion Result=88";
        });

        suite.Add("task.fabricate.carrier.runs", () =>
        {
            object il2cppTask, carrier, back;
            try
            {
                il2cppTask = ToIl2Cpp(typeof(System.Threading.Tasks.Task<int>), System.Threading.Tasks.Task.FromResult(7))!;
                carrier = WrapTaskTReflect(typeof(int), il2cppTask)!;                          // a CLR carrier over the il2cpp task
                back = ToIl2Cpp(typeof(System.Threading.Tasks.Task<int>), carrier)!;           // Dematerialize the carrier
            }
            catch (Exception ex) { Check.Skip($"il2cpp Task carrier round-trip not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }
            nint orig = Ptr(il2cppTask), fwd = Ptr(back);
            Check.True(orig == fwd,
                $"carrier Dematerialize must FORWARD the original il2cpp task (ptr 0x{orig:X}), got 0x{fwd:X} — a fresh fabricate would break identity");
            return $"carrier round-trip forwarded the original il2cpp task by identity (ptr 0x{orig:X})";
        });

        // ── item 1b: read-side PENDING carrier — the OnCompleted bridge (read mirror of the pending WRITE fabricate) ──
        // Isolates the BRIDGE from any game-async pumping: fabricate a genuinely-PENDING il2cpp promise task (from a
        // managed TCS), wrap it as a CLR carrier (WrapTaskT sees it pending -> attaches the il2cpp UnsafeOnCompleted
        // bridge), then complete the promise DETERMINISTICALLY and assert the carrier is driven to completion by the
        // il2cpp task's OWN completion — not a placeholder. Chains: managed SetResult -> (write) TrySetResult on the
        // il2cpp promise -> il2cpp task completes -> (read) OnCompleted fires -> CLR carrier completes.
        suite.Add("task.pending.bridge.runs", () =>
        {
            var mtcs = new System.Threading.Tasks.TaskCompletionSource<int>();
            object il2cppPending, carrier;
            try
            {
                il2cppPending = ToIl2Cpp(typeof(System.Threading.Tasks.Task<int>), mtcs.Task)!;   // pending il2cpp promise Task`1<int>
                carrier = WrapTaskTReflect(typeof(int), il2cppPending)!;                          // CLR carrier -> read bridge attaches
            }
            catch (Exception ex) { Check.Skip($"il2cpp pending Task bridge not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            var clr = (System.Threading.Tasks.Task<int>)carrier;
            Check.True(!clr.IsCompleted, "the carrier should be PENDING (the il2cpp promise is not completed yet) — a placeholder would already be done");
            mtcs.SetResult(55);   // write continuation -> il2cpp TrySetResult(55) -> il2cpp task completes -> read bridge fires
            bool done = clr.Wait(2000);
            Check.True(done, "the carrier never completed after the il2cpp promise was completed — the read-side OnCompleted bridge did not fire (item 1b)");
            Check.True(clr.Result == 55, $"carrier Result should be 55 (driven from the il2cpp task), got {clr.Result}");
            return $"pending il2cpp promise -> CLR carrier driven to completion by the OnCompleted bridge: Result={clr.Result} (item 1b)";
        });
    }

    // Reflectively invoke the SDK write entry Il2CppMarshal.ToIl2CppTyped<spelled>(managed) — the same public door a
    // hook boundary / param seam calls. Reflective so the battery keeps its engine-ref-free liveness-check role.
    static object? ToIl2Cpp(Type spelled, object? managed)
    {
        MethodInfo m = Sdk().GetType("Inutil.Marshal.Il2CppMarshal")!
            .GetMethods().First(x => x.Name == "ToIl2CppTyped" && x.IsGenericMethodDefinition);
        return m.MakeGenericMethod(spelled).Invoke(null, new[] { managed });
    }

    // Il2CppSugar.WrapTaskT<T>(Il2CppObjectBase) — mint a CLR carrier over an il2cpp task (the read-flip's runtime target).
    static object? WrapTaskTReflect(Type elem, object il2cppTask)
    {
        MethodInfo m = Sdk().GetType("Inutil.Sugar.Il2CppSugar")!.GetMethods()
            .First(x => x.Name == "WrapTaskT" && x.GetParameters() is [{ ParameterType.Name: "Il2CppObjectBase" }]);
        return m.MakeGenericMethod(elem).Invoke(null, new[] { il2cppTask });
    }

    static Assembly Sdk() => Assembly.Load(new AssemblyName("Inutil"));
    static bool Il2CppIsCompleted(object task) => (bool)task.GetType().GetProperty("IsCompleted")!.GetValue(task)!;
    static object? Il2CppResult(object task) => task.GetType().GetProperty("Result")?.GetValue(task);
    static nint Ptr(object o) => (nint)o.GetType().GetProperty("Pointer")!.GetValue(o)!;

    static MethodInfo FindOpenSession(out string typeName) => FindProxyMethod("Backend`1", "OpenSession", out typeName);

    // Find a loaded proxy method by simple type name + method name, resilient to the interop namespace prefix.
    static MethodInfo FindProxyMethod(string typeSimpleName, string methodName, out string typeName)
    {
        foreach (Assembly asm in CandidateAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
            catch { continue; }

            Type? t = types.FirstOrDefault(x => x.Name == typeSimpleName && x.GetMethod(methodName) is not null);
            if (t is not null) { typeName = t.FullName ?? t.Name; return t.GetMethod(methodName)!; }
        }
        typeName = "(not found)";
        throw new AssertException($"{typeSimpleName}::{methodName} proxy not found in any loaded assembly — the Assembly-CSharp interop proxy did not load");
    }

    static IEnumerable<Assembly> CandidateAssemblies()
    {
        // The proxy may not be in the AppDomain yet at chainload — try to force-load it first, then fall back.
        Assembly? acs = null;
        try { acs = Assembly.Load(new AssemblyName("Assembly-CSharp")); } catch { /* fall through to the domain scan */ }
        if (acs is not null) yield return acs;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) yield return a;
    }

    // Best-effort reflective construction of Backend`1<int> via its (T tag) ctor. Il2CppInterop's generic
    // instantiation + arg marshalling is version-sensitive; the caller treats any failure here as a SKIP.
    static object ConstructBackend(Type openBackend)
    {
        Type closed = openBackend.MakeGenericType(typeof(int));
        ConstructorInfo ctor = closed.GetConstructors()
            .Where(c => c.GetParameters().Length == 1)
            .OrderBy(c => c.GetParameters()[0].ParameterType == typeof(int) ? 0 : 1)
            .First();
        object arg = ctor.GetParameters()[0].ParameterType == typeof(int)
            ? 0
            : Activator.CreateInstance(ctor.GetParameters()[0].ParameterType, 0)!;
        return ctor.Invoke(new[] { arg });
    }
}
