using System.Reflection;
using Inutil.Host;
using HookApi = Inutil.Hooks.Hooks;   // the ENGINE CLASS — aliased because the Inutil.Hooks namespace shadows the Hooks type by simple name

namespace Inutil.Battery;

// The native hook engine, END-TO-END in a booted IL2CPP game. Unlike the flip/container cases —
// which reach the engine reflectively through the DEPLOYED proxies to keep the battery's compile closure
// game-proxy-free — a hook is written against the typed Inutil.Hooks API (HookContext, HookCallback), so
// these cases link the SDK directly. The TARGET is still named purely by il2cpp metadata strings
// (ResolveNative: "Assembly-CSharp"/"ToyGame"/"Game"/"Tally"), so no ToyGame proxy is referenced.
//
// The engine is brought up by the FIRST case (LoaderAdapter.Attach -> inutil_core.dll init). Putting Attach
// in a case, not in the plugin's Load(), keeps the Suite's per-case isolation: an attach failure fails ONE
// case and the already-run SmokeCases/flip/container results still stand — the harness stays able to tell
// "the engine broke" from "the harness broke".
//
// Target: ToyGame.Game::Tally(int) => Score + n. A non-generic instance method with its OWN compiled body
// (the ctor stores `_tallyVia = Tally`, so IL2CPP cannot inline it away — a delegate needs a real
// methodPointer), which is exactly what MinHook patches in place. Pure (no state mutation), so cases share
// one instance and the return is a deterministic function of Score(0) + the (possibly hook-rewritten) arg.
public static class HookCases
{
    public static void Register(Suite suite)
    {
        // (1) Bring the engine up. Everything below depends on it; a failure here is loud, not a skip.
        suite.Add("hook.engine.attach", () =>
        {
            LoaderAdapter.Attach();   // [DllImport] inutil_core.dll -> MH_Initialize + wire the 4 dispatchers + install callbacks
            return "inutil_core.dll loaded, interceptor init OK, install/proceed/vtable callbacks wired";
        });

        // (2) A pre-hook SEES the incoming argument — the base proof that dispatch reaches managed keyed by
        //     the right MethodInfo* and the ABI plan reads the arg from the right register slot.
        suite.Add("hook.pre.observe", () =>
        {
            var (game, tally) = Target();
            int seen = int.MinValue;
            using var _ = HookApi.Pre("Assembly-CSharp", "ToyGame", "Game", "Tally", c => seen = c.Arg<int>(0), argc: 1);
            int r = Call(tally, game, 5);
            Check.True(seen == 5, $"pre-hook observed arg={seen}, expected 5 — dispatch or arg-read is wrong");
            return $"pre-hook observed Tally(5) (original returned {r})";
        });

        // (3) A pre-hook REWRITES the argument -> the original body computes on the new value. Proves the
        //     reload-after-pre-dispatch path (the thunk re-reads the register args the hook may have changed).
        suite.Add("hook.arg.rewrite", () =>
        {
            var (game, tally) = Target();
            int score = Score(game);
            using var _ = HookApi.Pre("Assembly-CSharp", "ToyGame", "Game", "Tally", c => c.SetArg<int>(0, 100), argc: 1);
            int r = Call(tally, game, 5);
            Check.True(r == score + 100, $"Tally returned {r}, expected Score({score})+100 — the rewritten arg did not reach the body");
            return $"pre-hook rewrote arg 5->100, Tally returned {r}";
        });

        // (4) A post-hook REWRITES the return — the RetFrame spill/reload path (RAX rewritten after the CALL).
        suite.Add("hook.return.rewrite", () =>
        {
            var (game, tally) = Target();
            using var _ = HookApi.Post("Assembly-CSharp", "ToyGame", "Game", "Tally", c => c.SetReturn<int>(4242), argc: 1);
            int r = Call(tally, game, 5);
            Check.True(r == 4242, $"post-hook set return 4242 but caller got {r} — RetFrame reload is wrong");
            return $"post-hook rewrote return -> {r}";
        });

        // (5) A pre-hook Skip()s + SetReturn()s — REPLACE the method: the original body never runs and the
        //     caller gets the hook's value (not Score+arg). The thunk's SKIP branch.
        suite.Add("hook.skip", () =>
        {
            var (game, tally) = Target();
            int score = Score(game);
            using var _ = HookApi.Pre("Assembly-CSharp", "ToyGame", "Game", "Tally", c => { c.SetReturn<int>(-7); c.Skip(); }, argc: 1);
            int r = Call(tally, game, 5);
            Check.True(r == -7 && r != score + 5, $"skip returned {r}, expected -7 with the original ({score}+5) bypassed");
            return $"pre-hook Skip()+SetReturn replaced Tally -> {r} (original not run)";
        });

        // (6) A pre-hook Proceed()s — the "around" wrap: run the original NOW, read its result, transform it,
        //     then Skip() so the thunk doesn't call the original a second time.
        suite.Add("hook.proceed", () =>
        {
            var (game, tally) = Target();
            int score = Score(game);
            using var _ = HookApi.Pre("Assembly-CSharp", "ToyGame", "Game", "Tally", c =>
            {
                if (!c.Proceed()) throw new AssertException("Proceed() returned false inside a pre-hook — no live frame");
                c.SetReturn<int>(c.Return<int>() + 1);
                c.Skip();
            }, argc: 1);
            int r = Call(tally, game, 5);
            Check.True(r == score + 5 + 1, $"proceed+transform returned {r}, expected {score + 5 + 1}");
            return $"pre-hook Proceed()->{score + 5}, +1 -> {r}";
        });

        // (7) The in-place detour fires even when the call is dispatched through a delegate BUILT IN THE CTOR
        //     before the hook existed (InvokeStoredTally -> _tallyVia(n) -> Tally). MinHook patches the body,
        //     which the delegate's method_ptr already points at — a field-swap hook would miss this; in-place
        //     patching cannot. The strongest evidence the detour is on the real body, not a proxy shim.
        suite.Add("hook.delegate.dispatch", () =>
        {
            object game = Instance();
            MethodInfo stored = Proxy("Game", "InvokeStoredTally");
            int seen = int.MinValue;
            using var _ = HookApi.Pre("Assembly-CSharp", "ToyGame", "Game", "Tally", c => seen = c.Arg<int>(0), argc: 1);
            int r = Call(stored, game, 9);
            Check.True(seen == 9, $"the Tally detour did NOT fire through the ctor-built delegate (seen={seen})");
            return $"in-place Tally detour fired through a ctor-built delegate (arg=9, InvokeStoredTally returned {r})";
        });

        // (8) ABI-completeness HEADLINE — a shared __Canon generic whose trailing MethodInfo* SPILLS past
        //     GPR slot 3. EchoWide<T>(a,b,c,d) is an instance method with 4 params, so the hidden RGCTX mi lands
        //     at arg index 5 = caller-stack Stack[1]. Hooked for TWO reference-type instantiations (Player, Boss)
        //     that share ONE __Canon body (refs>1), each direct call must route to ITS OWN chain by the spilled
        //     live mi (dispatch_key reads Stack[miSlot-4]). Pre-fix the mi capped to -1 and BOTH routed to the
        //     first registrant — so calling EchoWide<Boss> fired the Player hook. The discriminating proof.
        suite.Add("hook.canon.spill.route", () =>
        {
            object game = Instance();
            MethodInfo echoPlayer = Proxy("Game", "EchoWidePlayer");
            MethodInfo echoBoss = Proxy("Game", "EchoWideBoss");

            nint openEcho = HookApi.ResolveNative("Assembly-CSharp", "ToyGame", "Game", "EchoWide");
            nint miPlayer = HookApi.InflateNative(openEcho, HookApi.ResolveClass("Assembly-CSharp", "ToyGame", "Player"));
            nint miBoss = HookApi.InflateNative(openEcho, HookApi.ResolveClass("Assembly-CSharp", "ToyGame", "Boss"));

            int playerHits = 0, bossHits = 0;
            using var hp = HookApi.PreNative(miPlayer, _ => playerHits++);   // registered FIRST -> the mis-route sink pre-fix
            using var hb = HookApi.PreNative(miBoss, _ => bossHits++);

            InvokeIgnore(echoPlayer, game, null);   // -> EchoWide<Player>(...) : only the player hook may fire
            InvokeIgnore(echoBoss, game, null);     // -> EchoWide<Boss>(...)   : only the boss hook may fire

            Check.True(playerHits >= 1 && bossHits >= 1,
                $"a hook never fired (playerHits={playerHits} bossHits={bossHits}) — the live RGCTX mi missed the registered inflation, NOT a spill mis-route");
            Check.True(playerHits == 1 && bossHits == 1,
                $"SPILL MIS-ROUTE: playerHits={playerHits} bossHits={bossHits} — a shared __Canon call routed to the first-registered chain; the trailing MethodInfo* past GPR slot 3 was not read from Stack[miSlot-4]");
            return $"shared __Canon spill routed correctly (miPos=5 -> Stack[1]): EchoWide<Player>->player x{playerHits}, EchoWide<Boss>->boss x{bossHits}";
        });

        // (9) hook Task-FABRICATE at the boundary (the write-direction consumer of DematerializeTask): a mod
        //     pre-hook SetReturnTask's a FABRICATED Task<int>(777) and Skip()s the original FetchNumber. RoundTripNumber
        //     reads FetchNumber().Result GAME-SIDE, so the fabricated il2cpp Task's result must reach it as 777 —
        //     proving SetReturnTask dematerialized the mod's managed Task into a real il2cpp Task the game then read.
        suite.Add("hook.task.fabricate.return", () =>
        {
            object game = Instance();
            MethodInfo roundTrip = Proxy("Game", "RoundTripNumber");
            using var _ = HookApi.Pre("Assembly-CSharp", "ToyGame", "Game", "FetchNumber",
                c => { c.SetReturnTask(System.Threading.Tasks.Task.FromResult(777)); c.Skip(); }, argc: 0);
            int r;
            try { r = (int)roundTrip.Invoke(game, null)!; }
            catch (TargetInvocationException tie)
            {
                Check.True(false, $"RoundTripNumber threw {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message} — the fabricated Task return did not marshal");
                return null;
            }
            Check.True(r == 777, $"RoundTripNumber returned {r}, expected 777 — the hook's fabricated Task<int> result did not reach the game-side reader");
            return $"hook SetReturnTask(Task.FromResult(777)) + Skip() -> RoundTripNumber read the fabricated il2cpp Task result: {r}";
        });
    }

    // --- target resolution + reflective invoke (the TARGET stays proxy-typed only via reflection) ----------

    static object? _game;
    // The shared Game proxy instance. Il2CppInterop construction is version-sensitive -> an honest SKIP (the
    // engine cases that don't need an instance still ran). Tally is pure, so one shared instance is safe.
    static object Instance()
    {
        if (_game is not null) return _game;
        Type t = Proxy("Game", "Tally").DeclaringType!;
        try { return _game = Construct(t); }
        catch (Exception ex) { Check.Skip($"il2cpp Game proxy construction not reachable: {ex.GetType().Name}: {ex.Message}"); throw; }
    }

    static (object game, MethodInfo tally) Target() => (Instance(), Proxy("Game", "Tally"));

    static int Score(object game)
    {
        PropertyInfo? p = game.GetType().GetProperty("Score");
        return p is not null ? (int)p.GetValue(game)! : 0;   // il2cpp projects the public field as a property
    }

    // Invoke a proxy method whose return is irrelevant (the hook fires on the body regardless of args, so a
    // null reference arg is fine — the __Canon body just echoes it). A throw is a LOUD fail.
    static void InvokeIgnore(MethodInfo mi, object game, object? arg)
    {
        try { mi.Invoke(game, new object?[] { arg }); }
        catch (TargetInvocationException tie)
        { Check.True(false, $"invoking {mi.Name} threw {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}"); }
    }

    // Invoke the proxy method; a spliced/hook failure surfaces as a LOUD fail, never a swallowed skip.
    static int Call(MethodInfo mi, object game, int arg)
    {
        try { return (int)mi.Invoke(game, new object[] { arg })!; }
        catch (TargetInvocationException tie)
        {
            Check.True(false, $"invoking {mi.Name} threw {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}");
            throw;   // unreachable (Check.True already threw)
        }
    }

    // Find a loaded proxy method by simple type name + method name, resilient to the interop namespace prefix.
    static MethodInfo Proxy(string typeSimpleName, string methodName)
    {
        Assembly? acs = null;
        try { acs = Assembly.Load(new AssemblyName("Assembly-CSharp")); } catch { /* fall through to the domain scan */ }
        foreach (Assembly asm in (acs is not null ? new[] { acs } : Array.Empty<Assembly>()).Concat(AppDomain.CurrentDomain.GetAssemblies()))
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x is not null).ToArray()!; }
            catch { continue; }
            Type? t = types.FirstOrDefault(x => x.Name == typeSimpleName && x.GetMethod(methodName) is not null);
            if (t is not null) return t.GetMethod(methodName)!;
        }
        throw new AssertException($"{typeSimpleName}::{methodName} proxy not found in any loaded assembly — the Assembly-CSharp interop proxy did not load");
    }

    static object Construct(Type proxyType)
    {
        ConstructorInfo? ctor = proxyType.GetConstructor(Type.EmptyTypes);
        if (ctor is not null) return ctor.Invoke(null);
        return Activator.CreateInstance(proxyType) ?? throw new MissingMethodException(proxyType.FullName, ".ctor()");
    }
}
