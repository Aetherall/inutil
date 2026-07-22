using System.Reflection;
using Inutil.Host;   // ModContext

namespace Inutil.Battery;

// THE CALLBACK MIRROR, end-to-end in a booted IL2CPP game (ConvKind.Callback — the INBOUND twin of the Result mirror).
//
// ⚠ REQUIRES THE ToyGame FIXTURE REBUILD: binds against ToyGame.{Roster,Callback,Result} (Assets/Scripts/Callback.cs),
// which reach the battery only after the ToyGame project is rebuilt to IL2CPP and run under wine. Without that the
// case SKIPs (fixture types / Roslyn absent) rather than failing — the offline gates are already green independently.
//
// What the offline gates can't prove: the FULL inbound round-trip in a live frame — a compiled Hook<Roster> spells
// Inutil.Bridge.Callback<Player[]> for the game's Callback<Player[]> param (HookMatch Tier 2.5 binds it, since the game
// Callback type can't be interop-patch-flipped), and cb.Ok([alpha,bravo]) drives the seam: flip Player[] ->
// Il2CppReferenceArray<Player>, wrap in a game Result, invoke the REAL callback. A miss anywhere (bind, element flip,
// Result build, invoke) shows as a wrong count/name.
public static class CallbackCases
{
    public static void Register(Suite suite)
    {
        suite.Add("callback.mirror.inbound", () =>
        {
            // (0) Register the per-game Result + Callback shims (a callback is invoked WITH a Result, so BOTH — the
            //     Callback seam reuses BuildResult). Resolved reflectively off the proxies, exactly like serverstub's
            //     MirrorShim does for the real EFT Comfort.Common.{Result,Callback}. SKIPs if the fixture isn't staged.
            RegisterToyGameMirror();

            // (1) The mod: a Hook<Roster> that spells the mirror for the game Callback<Player[]> param. Named as a real
            //     mod would; MatchMirrorParams (Tier 2.5) binds it, the seam materializes the inbound game callback.
            const string src = """
                using Inutil;
                using ToyGame;
                public sealed class InutilCallbackHook : Hook<Roster>
                {
                    // game param is Callback<Player[]> (reified Callback<Il2CppReferenceArray<Player>>); spell the mirror.
                    // Ok flips Player[] -> il2cpp array, wraps it in a game Result, invokes the real game callback.
                    void RequestRoster(Inutil.Bridge.Callback<Player[]> cb)
                        => cb.Ok(new[] { new Player("alpha"), new Player("bravo") });
                }
                """;
            if (!ModHostCases.TryCompileAndLoad("inutilcallback", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilCallbackHook") is not null, "the compiled callback hook did not load into the ModContext");

            MethodInfo request = ModHostCases.Proxy("Roster", "RequestRoster");
            Type rosterType = request.DeclaringType!;
            object roster = Activator.CreateInstance(rosterType)!;

            // Baseline (UNHOOKED): the original RequestRoster invokes the callback EMPTY -> DecodeCount == 0.
            object cbBase = ConstructGameCallback();
            ModHostCases.InvokeVoid(request, roster, cbBase);
            int baseCount = DecodeCount(rosterType, cbBase);
            Check.True(baseCount == 0, $"baseline RequestRoster gave DecodeCount={baseCount}, expected 0 (invoked, empty) — the target or the per-game shim is not what we think");

            // (2) Discover — wired==1 PROVES MatchMirrorParams bound Inutil.Bridge.Callback<Player[]> to the game
            //     Callback<Player[]> param (the interop-patch can't flip the game Callback type, so the bind is Tier 2.5).
            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook(s), expected 1 (Roster::RequestRoster) — did Inutil.Bridge.Callback<Player[]> bind to the game Callback<Player[]> param via HookMatch Tier 2.5?");

            // (3) HOOKED: cb.Ok([alpha,bravo]) marshals Player[] -> Il2CppReferenceArray<Player> (recursive child), wraps
            //     it in a game Result (BuildResult shim), and invokes the real game callback -> count 2, first "alpha".
            object cbHook = ConstructGameCallback();
            ModHostCases.InvokeVoid(request, roster, cbHook);
            int hookCount = DecodeCount(rosterType, cbHook);
            string hookFirst = DecodeFirst(rosterType, cbHook);
            Check.True(hookCount == 2 && hookFirst == "alpha",
                $"hooked RequestRoster gave count={hookCount}/first='{hookFirst}', expected 2/'alpha' — the Callback mirror did not flip Player[] -> il2cpp, wrap it in a Result, and invoke the game callback (bind / element-flip / Result-build / invoke)");

            // (4) TEARDOWN — the hook is gone; the empty baseline returns.
            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            object cbAfter = ConstructGameCallback();
            ModHostCases.InvokeVoid(request, roster, cbAfter);
            int afterCount = DecodeCount(rosterType, cbAfter);
            Check.True(afterCount == 0, $"after teardown DecodeCount={afterCount}, expected 0 — the mod's hook was not removed");

            return $"compiled Hook<Roster>.RequestRoster(Inutil.Bridge.Callback<Player[]>) bound via HookMatch Tier 2.5 + "
                 + $"the ConvKind.Callback seam: baseline count {baseCount} -> hooked {hookCount} (first '{hookFirst}'), teardown {afterCount} ({removed} sub removed)";
        });
    }

    // ── per-game Result + Callback shims (the Inutil.Bridge.Mirror.Register* contract, resolved reflectively) ────────
    static bool _registered;
    static void RegisterToyGameMirror()
    {
        if (_registered) return;
        Type resultOpen = ProxyOpenType("Result");       // ToyGame.Result`1
        Type callbackOpen = ProxyOpenType("Callback");   // ToyGame.Callback`1

        // Result: build a closed game Result<elemIl2> via its generated ctor, and read its parts back — exactly the
        // pattern serverstub's MirrorShim uses for the real EFT Comfort.Common.Result<T>.
        Inutil.Bridge.Mirror.RegisterResult(
            gameResultOpen: resultOpen,
            build: (elemIl2, valueIl2, error, code) =>
            {
                Type t = resultOpen.MakeGenericType(elemIl2);
                return error is null
                    ? t.GetConstructor(new[] { elemIl2 })!.Invoke(new[] { valueIl2 })!
                    : t.GetConstructor(new[] { elemIl2, typeof(string), typeof(int) })!.Invoke(new object?[] { valueIl2, error, code })!;
            },
            read: gameResult =>
            {
                Type t = gameResult.GetType();
                object? value = t.GetProperty("Value")!.GetValue(gameResult);
                string? error = (string?)t.GetProperty("Error")!.GetValue(gameResult);
                int code = (int)t.GetProperty("ErrorCode")!.GetValue(gameResult)!;
                return (value, error, code);
            });

        // Callback: how to INVOKE the game callback with a game Result (Callback<T>.Invoke(Result<T>)). BuildResult
        // (above) supplies the Result the seam hands here — the Callback shim adds only the invoke.
        Inutil.Bridge.Mirror.RegisterCallback(
            gameCallbackOpen: callbackOpen,
            invoke: (gameCallback, gameResult) => gameCallback.GetType().GetMethod("Invoke")!.Invoke(gameCallback, new[] { gameResult }));

        _registered = true;
    }

    // Construct the reified game callback the game would hand a mod: Callback<Il2CppReferenceArray<Player>> (the close
    // Il2CppInterop reifies for a Callback<Player[]> param). Il2CppInterop construction is version-sensitive -> SKIP if
    // unreachable, like the ValueType cases in ModHostCases.
    static object ConstructGameCallback()
    {
        try
        {
            Type playerType = ModHostCases.Proxy("Player", "GetHealth").DeclaringType!;
            Type refArrayOfPlayer = Il2CppRefArrayOpen().MakeGenericType(playerType);
            Type closed = ProxyOpenType("Callback").MakeGenericType(refArrayOfPlayer);   // Callback<Il2CppReferenceArray<Player>>
            return Activator.CreateInstance(closed)!;
        }
        catch (Exception ex) { Check.Skip($"il2cpp game Callback proxy construction not reachable: {ex.GetType().Name}: {ex.Message}"); throw; }
    }

    // The open Il2CppReferenceArray<> proxy, resolved reflectively so this case keeps the battery's game-proxy-free
    // compile closure (no direct Il2CppInterop naming) — the interop core is always loaded in a booted game.
    static Type Il2CppRefArrayOpen()
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t = asm.GetType("Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1");
            if (t is not null) return t;
        }
        Check.Skip("Il2CppInterop.Runtime (Il2CppReferenceArray<>) not loaded — cannot construct the reified game callback");
        throw new AssertException("unreachable");
    }

    static int DecodeCount(Type rosterType, object cb)
        => Convert.ToInt32(rosterType.GetMethod("DecodeCount")!.Invoke(null, new[] { cb }));

    static string DecodeFirst(Type rosterType, object cb)
        => (string)(rosterType.GetMethod("DecodeFirst")!.Invoke(null, new[] { cb }) ?? "");

    // Resolve an OPEN generic ToyGame proxy type by simple name (arity 1) — ToyGame.* under BepInEx, Il2CppToyGame.*
    // under MelonLoader. SKIPs (never a false RED) where the fixture rebuild hasn't staged it.
    static Type ProxyOpenType(string simpleName)
    {
        string want = simpleName + "`1";
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x is not null).ToArray()!; }
            catch { continue; }
            Type? t = types.FirstOrDefault(x => x.Name == want && (x.Namespace == "ToyGame" || x.Namespace == "Il2CppToyGame"));
            if (t is not null) return t;
        }
        Check.Skip($"ToyGame.{simpleName}<T> proxy not found — the ToyGame fixture rebuild (Assets/Scripts/Callback.cs -> .unity-build) has not been run");
        throw new AssertException("unreachable");
    }
}
