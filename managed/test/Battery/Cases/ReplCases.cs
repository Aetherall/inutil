using System.Reflection;

namespace Inutil.Battery;

// The in-game REPL proof: drive ReplSession.Eval — the EXACT per-line unit the TCP transport dispatches —
// against the LIVE game, so Roslyn compiles + runs arbitrary C# submissions that touch the real il2cpp runtime.
// This is the one thing the offline Inutil.Mods.Tests cannot cover (it has no game): a submission that registers a
// WORKING hook through the shared Inutil.Hooks table, closing over a value typed an instant earlier (session-state
// chaining feeding a live hook that fires on the real Player.GetHealth()).
//
// The submission registers the hook through the EXPRESSION API — Hooks.Post((Player p) => p.GetHealth(), …) —
// TOUCHING the Player proxy type, exactly as a modder types it. That compiles here because the interop-patch now
// normalizes each proxy's System.Private.CoreLib reference to the runtime BCL version (PatchDirectory). The oracle
// constructs the Player + reads GetHealth
// by REFLECTION here (so the CASE stays compile-time proxy-free, like ModHostCases — only the SUBMISSION, compiled
// by Roslyn at runtime, names the proxy).
//
// ReplSession lives in the optional Inutil.Mods.dll (Roslyn), reached by REFLECTION so the case SKIPs cleanly where
// scripting is not deployed. All submissions run ON the battery's (main, il2cpp-attached) thread; none awaits, so
// the deadlock guard never trips here.
public static class ReplCases
{
    public static void Register(Suite suite)
    {
        suite.Add("repl.eval.live-hook", () =>
        {
            Assembly mods;
            try { mods = Assembly.Load(new AssemblyName("Inutil.Mods")); }
            catch { Check.Skip("Inutil.Mods.dll (Roslyn host) not deployed here"); return null; }

            Type? sessionType = mods.GetType("Inutil.Repl.ReplSession");
            Type? bootstrapType = mods.GetType("Inutil.Repl.ReplBootstrap");
            Check.True(sessionType is not null && bootstrapType is not null, "Inutil.Repl.ReplSession/ReplBootstrap not found in Inutil.Mods");

            bool available = (bool)bootstrapType!.GetMethod("PreloadEngine")!.Invoke(null, new object?[] { null })!;
            if (!available) { Check.Skip("Roslyn scripting closure (Microsoft.CodeAnalysis.CSharp.Scripting) not deployed here"); return null; }

            // The proxy namespace the submissions get as a free `using`, and where the oracle finds Player —
            // loader-specific (BepInEx strips the Il2Cpp prefix; MelonLoader keeps it).
#if MELONLOADER
            const string proxyNs = "Il2CppToyGame";
#else
            const string proxyNs = "ToyGame";
#endif
            object session = Activator.CreateInstance(sessionType!, new object?[] { proxyNs, null })!;
            MethodInfo evalMi = sessionType!.GetMethod("Eval")!;

            // The oracle's live Player, reached by reflection (compile-time proxy-free). GetHealth() reads Health=100.
            Type playerType = Assembly.Load(new AssemblyName("Assembly-CSharp")).GetType($"{proxyNs}.Player")
                              ?? throw new AssertException($"{proxyNs}.Player proxy not found");
            object player = Activator.CreateInstance(playerType, "ReplProbe")!;
            MethodInfo getHealth = playerType.GetMethod("GetHealth", Type.EmptyTypes)!;
            int baseline = (int)getHealth.Invoke(player, null)!;
            Check.True(baseline == 100, $"baseline Player.GetHealth()={baseline}, expected 100 — the target is not what we think");

            // (0) arbitrary C# compiles + runs at runtime against the live game -> 42. The plainest proof.
            var (ok0, val0, err0) = Eval(evalMi, session, "40 + 2");
            Check.True(ok0 && val0 is int n0 && n0 == 42, $"Eval('40 + 2') -> ok={ok0} val={val0} err={err0}, expected 42");

            // (1) a session variable — proves nothing alone, but (2) closes over it (state chaining).
            var (ok1, _, err1) = Eval(evalMi, session, "var bonus = 4242;");
            Check.True(ok1, $"Eval('var bonus = 4242;') failed: {err1}");

            // (2) THE HEADLINE: one submission, compiled at runtime, that closes over `bonus` from (1) — session
            // chaining — and registers a typed hook through the SHARED Inutil.Hooks table against the LIVE runtime
            // via the EXPRESSION API (naming the Player proxy directly, as a modder would), yielding the IDisposable
            // handle. A POST hook (the return exists only after the original ran): every GetHealth() returns +bonus.
            const string headline =
                "Hooks.Post((Player p) => p.GetHealth(), c => c.SetReturn(c.Return<int>() + bonus))";
            var (ok2, handle, err2) = Eval(evalMi, session, headline);
            Check.True(ok2 && handle is IDisposable, $"the live-hook submission failed: ok={ok2} err={err2} (expected an IDisposable hook handle)");

            // The REPL-registered hook FIRES on the real Player.GetHealth() -> baseline + bonus.
            int hooked = (int)getHealth.Invoke(player, null)!;
            Check.True(hooked == baseline + 4242, $"hooked Player.GetHealth()={hooked}, expected {baseline + 4242} — the REPL-registered hook did not fire, or `bonus` did not chain from the prior submission");

            // The hook is REMOVABLE — disposing the REPL-yielded handle un-roots it; GetHealth() restores.
            ((IDisposable)handle!).Dispose();
            int restored = (int)getHealth.Invoke(player, null)!;
            Check.True(restored == baseline, $"after disposing the REPL hook handle, GetHealth()={restored}, expected {baseline} — the REPL-registered hook was not removable");

            return $"in-game REPL: Roslyn ran live C# (40+2=42), chained `bonus` across submissions, registered a hook on "
                 + $"Player.GetHealth() that fired ({baseline}->{hooked}) and was removable ({restored}) — compiled at runtime against the live game";
        });
    }

    // Call Eval by reflection and unpack the ReplResult struct (public fields Ok/Value/Error).
    static (bool ok, object? value, string? error) Eval(MethodInfo evalMi, object session, string code)
    {
        object result = evalMi.Invoke(session, new object?[] { code })!;
        Type rt = result.GetType();
        bool ok = (bool)rt.GetField("Ok")!.GetValue(result)!;
        object? value = rt.GetField("Value")!.GetValue(result);
        string? error = (string?)rt.GetField("Error")!.GetValue(result);
        return (ok, value, error);
    }
}
