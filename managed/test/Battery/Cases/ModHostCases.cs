using System.Reflection;
using Inutil.Host;   // ModContext, FrameDriver

namespace Inutil.Battery;

// THE CAPSTONE (mod-execution layer end-to-end in a booted IL2CPP game): a mod COMPILED FROM
// SOURCE whose ergonomic Hook<Game> fires against the real game method, whose ITick is driven by FrameDriver,
// and which tears down cleanly. This is the one proof that ties the whole layer together in the actual game —
// the piece the offline gate cannot reach (it needs a live il2cpp frame + the real Game proxy).
//
// The mod SOURCE names ToyGame.Game directly (exactly as a real mod would); CsModCompiler resolves it against
// the deployed interop proxies. CsModCompiler lives in the optional Inutil.Mods.dll (it carries Roslyn), so it
// is reached by REFLECTION and the case SKIPs cleanly where that + Roslyn are not deployed (e.g. the MelonLoader
// battery layout) — everything else (ModContext, Mods.Discover, Hook<T>, FrameDriver) is the referenced SDK.
public static class ModHostCases
{
    public static void Register(Suite suite)
    {
        suite.Add("modhost.capstone.compiled-hook", () =>
        {
            // Marker file the mod's ITick writes to (its statics live in the mod's own ALC — the marker is the
            // cross-ALC channel back to the battery, as the offline mods-test does).
            string marker = Path.Combine(Path.GetTempPath(), "inutil-capstone-marker.txt");
            try { File.Delete(marker); } catch { }

            // The mod: an ergonomic Hook<Game> that DOUBLES Tally's arg by proceeding to the original, plus an
            // ITick that records each frame. Named exactly as a real .cs mod would name game types.
            string src = """
                using Inutil;
                using ToyGame;
                using System.IO;
                public sealed class InutilCapstoneHook : Hook<Game>, ITick
                {
                    int Tally(int n) => Proceed<int>(n * 2);     // run the original with a doubled arg
                    public void Tick() => File.AppendAllText(@"__MARKER__", "tick\n");
                }
                """.Replace("__MARKER__", marker);

            // Baseline BEFORE the hook: Tally(5) == Score + 5 (the target is unhooked here).
            var (game, tally) = Target();
            int score = Score(game);
            int baseline = Call(tally, game, 5);
            Check.True(baseline == score + 5, $"pre-hook Tally(5)={baseline}, expected Score({score})+5 — the target is not what we think");

            // (1) COMPILE from source + (2) LOAD into a collectible ModContext (SKIPs where Roslyn absent).
            if (!TryCompileAndLoad("inutilcapstone", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilCapstoneHook") is not null, "the compiled mod type did not load into the ModContext");
            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Game::Tally)");

            // (3) THE ERGONOMIC HOOK FIRES against the real il2cpp method: Tally(5) -> Proceed(10) -> Score+10.
            int hooked = Call(tally, game, 5);
            Check.True(hooked == score + 10, $"ergonomic Hook<Game> did not fire: Tally(5)={hooked}, expected Score({score})+10 (arg doubled via Proceed)");

            // (4) FrameDriver DRIVES the mod's ITick (the seam the loader shim pumps every frame).
            FrameDriver.Tick();
            Check.True(File.Exists(marker) && File.ReadAllText(marker).Contains("tick"), "FrameDriver.Tick did not drive the compiled mod's ITick");

            // (5) TEARDOWN — RemoveAll drops the mod's hook + lifecycle; the ALC unloads; the hook is gone.
            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            int afterUnload = Call(tally, game, 5);
            Check.True(afterUnload == score + 5, $"after teardown Tally(5)={afterUnload}, expected Score({score})+5 — the mod's hook was not removed");

            return $"compiled Hook<Game> mod: Tally(5) {baseline}->{hooked} (arg doubled), FrameDriver drove ITick, teardown restored {afterUnload} ({removed} sub removed)";
        });

        // CONTAINER-ARG through the ergonomic tier (the Hook<T> boundary composing the frame ABI with the SHARED
        // marshalling engine): a compiled Hook<Game> whose method takes a NATURAL BCL container
        // (List<string>). The boundary reads the incoming il2cpp list as a natural List<string> THROUGH
        // Il2CppMarshal (NOT a second marshaller), the mod appends an element, and Proceed writes the grown list
        // back — the game's SetInventory does `Score += items.Count`, so the +1 the hook added is observable as a
        // Score DELTA (a pure scalar oracle, independent of how the proxy surfaces the list). This is the proof
        // that the container `else` now routes to the engine instead of failing loud, end-to-end in a booted game.
        suite.Add("modhost.hook.container-arg", () =>
        {
            string src = """
                using Inutil;
                using ToyGame;
                using System.Collections.Generic;
                public sealed class InutilContainerArgHook : Hook<Game>
                {
                    // read the incoming list as a natural List<string> (engine), append, run the original on it
                    void SetInventory(List<string> items) { items.Add("extra"); Proceed(items); }
                }
                """;
            if (!TryCompileAndLoad("inutilcontainerarg", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilContainerArgHook") is not null, "the compiled container-arg hook did not load into the ModContext");

            object game = Instance();
            MethodInfo setInv = Proxy("Game", "SetInventory");
            var two = new List<string> { "a", "b" };

            // Baseline: unhooked SetInventory(["a","b"]) raises Score by exactly 2 (items.Count) — the oracle.
            int s0 = Score(game); InvokeVoid(setInv, game, two);
            int dBase = Score(game) - s0;
            Check.True(dBase == 2, $"baseline SetInventory raised Score by {dBase}, expected 2 — the target is not what we think");

            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Game::SetInventory)");

            // Hooked: the hook could only Add() because it received the list as a NATURAL List<string> (engine
            // read il2cpp->natural); Proceed marshalled the 3-element list back to il2cpp (natural->il2cpp), so the
            // original counts 3 -> Score rises by 3. A delta of 2 would mean the arg never reached the mod naturally.
            int s1 = Score(game); InvokeVoid(setInv, game, two);
            int dHook = Score(game) - s1;
            Check.True(dHook == 3, $"hooked SetInventory raised Score by {dHook}, expected 3 — the container arg did not round-trip through the engine (read il2cpp->natural, append, write natural->il2cpp)");

            // Teardown: the hook is gone; the delta returns to the baseline 2.
            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            int s2 = Score(game); InvokeVoid(setInv, game, two);
            int dAfter = Score(game) - s2;
            Check.True(dAfter == 2, $"after teardown SetInventory raised Score by {dAfter}, expected 2 — the mod's hook was not removed");

            return $"compiled Hook<Game>.SetInventory(List<string>): baseline +{dBase}, hooked +{dHook} (engine read+append+write), teardown +{dAfter} ({removed} sub removed)";
        });

        // CONTAINER INTERFACE WIDENING at the ergonomic tier (HookMatch Tier 1, the FIRST fallback matcher end-to-end):
        // a hook may spell a READ-ONLY supertype (IReadOnlyList<int>) of the flipped proxy's concrete container param
        // (List<int>) and STILL bind — the matcher widens List -> IReadOnlyList (same element type, an engine-registered
        // read target), and the boundary reads the incoming il2cpp list INTO that spelling through the shared marshaller.
        // Entity.Muster(List<int>) => squad.Count + Tag (Tag=0 on base Entity), so the hook REPLACES with squad.Count*100
        // — a pure int oracle. Independent from container-arg (which spelled the CONCRETE List<string>, an exact match);
        // this only binds because Tier 1 widened it.
        suite.Add("modhost.hook.container-widen", () =>
        {
            string src = """
                using Inutil;
                using ToyGame;
                using System.Collections.Generic;
                public sealed class InutilWidenHook : Hook<Entity>
                {
                    // spell the read-only supertype of the proxy's List<int> param; Tier 1 widens it. Replace: Count*100.
                    int Muster(IReadOnlyList<int> squad) => squad.Count * 100;
                }
                """;
            if (!TryCompileAndLoad("inutilwiden", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilWidenHook") is not null, "the compiled widen hook did not load into the ModContext");

            MethodInfo muster = Proxy("Entity", "Muster");
            Type entityType = muster.DeclaringType!;
            object entity = entityType.GetConstructor(Type.EmptyTypes)?.Invoke(null) ?? Activator.CreateInstance(entityType)!;
            var squad = new List<int> { 1, 2, 3 };

            // Baseline (base Entity, Tag=0): Muster([1,2,3]) == Count + Tag == 3.
            int baseline = (int)InvokeObj(muster, entity, new object[] { squad });
            Check.True(baseline == 3, $"baseline Entity.Muster([1,2,3])={baseline}, expected 3 (Count + Tag0) — the target is not what we think");

            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Entity::Muster) — did IReadOnlyList<int> widen to the List<int> param?");

            // Hooked (replace): squad read AS IReadOnlyList<int> -> Count*100 == 300. A miss here means the widened
            // hook never bound, or the il2cpp list did not read into the read-only spelling through the engine.
            int hooked = (int)InvokeObj(muster, entity, new object[] { squad });
            Check.True(hooked == 300, $"hooked Entity.Muster([1,2,3])={hooked}, expected 300 (Count*100 via replace over the widened IReadOnlyList<int>)");

            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            int after = (int)InvokeObj(muster, entity, new object[] { squad });
            Check.True(after == 3, $"after teardown Entity.Muster([1,2,3])={after}, expected 3 — the mod's hook was not removed");

            return $"compiled Hook<Entity>.Muster(IReadOnlyList<int>) bound to Muster(List<int>) via container widening: "
                 + $"baseline {baseline} -> hooked {hooked} (Count*100 over the read-only spelling), teardown {after} ({removed} sub removed)";
        });

        // EXPLICIT INTERFACE IMPL at the ergonomic tier (HookMatch Tier 2, the SECOND fallback matcher): Il2CppInterop
        // FLATTENS interfaces, so ToyGame's `void ITicker.Tick()` on Game lands on the proxy as a mangled PUBLIC method
        // `ToyGame_ITicker_Tick` with no interface map. A modder names the PLAIN `Tick()`; Tier 2 recognizes the mangled
        // counterpart by the Il2CppInterop shape (ends `_Tick`, preceding segment `ITicker` = I+Upper) and binds it.
        // Game.ITicker.Tick() does Score++, so the hook Proceeds TWICE -> Score rises by 2: proof it bound to the mangled
        // method AND that Proceed reaches the real body.
        suite.Add("modhost.hook.interface-impl", () =>
        {
            string src = """
                using Inutil;
                using ToyGame;
                public sealed class InutilTickHook : Hook<Game>
                {
                    // name the PLAIN interface method; Tier 2 maps it to the mangled ToyGame_ITicker_Tick. Proceed twice.
                    void Tick() { Proceed(); Proceed(); }
                }
                """;
            if (!TryCompileAndLoad("inutiltick", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilTickHook") is not null, "the compiled tick hook did not load into the ModContext");

            object game = Instance();
            MethodInfo tick = Proxy("Game", "ToyGame_ITicker_Tick");   // the mangled explicit-impl method (no plain 'Tick' exists)

            // Baseline: Tick() does Score++ -> a Score delta of 1.
            int s0 = Score(game); InvokeObj(tick, game, null); int dBase = Score(game) - s0;
            Check.True(dBase == 1, $"baseline Tick() raised Score by {dBase}, expected 1 (Score++) — the target is not what we think");

            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Game::Tick) — did the plain 'Tick' map to the mangled ToyGame_ITicker_Tick?");

            // Hooked: the plain-named hook bound to the mangled method and Proceeds twice -> Score rises by 2.
            int s1 = Score(game); InvokeObj(tick, game, null); int dHook = Score(game) - s1;
            Check.True(dHook == 2, $"hooked Tick() raised Score by {dHook}, expected 2 (Proceed x2) — 'Tick' did not bind to the mangled explicit impl, or Proceed did not reach the original");

            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            int s2 = Score(game); InvokeObj(tick, game, null); int dAfter = Score(game) - s2;
            Check.True(dAfter == 1, $"after teardown Tick() raised Score by {dAfter}, expected 1 — the mod's hook was not removed");

            return $"compiled Hook<Game>.Tick() bound to the mangled explicit impl ToyGame_ITicker_Tick: "
                 + $"baseline +{dBase}, hooked +{dHook} (Proceed x2), teardown +{dAfter} ({removed} sub removed)";
        });

        // GENERIC-METHOD INSTANTIATION INFERENCE at the ergonomic tier (HookMatch Tier 3, the THIRD fallback matcher):
        // a generic game method `Echo<T>(T)` has no single body to hook — one per instantiation. A concrete hook
        // `Player Echo(Player p)` lets Tier 3 infer T=Player, inflate `Echo<Player>`, resolve ITS native mi, and hook
        // that closed instantiation (the same install path the raw PreNative/InflateNative tier uses). We hook the
        // VALUE-TYPE instantiation Echo<int> (its own dedicated body, no shared __Canon RGCTX routing — the simplest
        // generic to hook) and trigger via reflection-invoke (il2cpp_runtime_invoke -> the inflated methodPointer where
        // the detour lives, regardless of inlining). Echo<T> returns its input, so the hook REPLACES with input+1000.
        suite.Add("modhost.hook.generic-inference", () =>
        {
            string src = """
                using Inutil;
                using ToyGame;
                public sealed class InutilEchoHook : Hook<Game>
                {
                    // concrete Echo(int) -> Tier 3 infers + hooks Echo<int>; replace with input+1000 to prove it fired
                    int Echo(int n) => n + 1000;
                }
                """;
            if (!TryCompileAndLoad("inutilecho", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilEchoHook") is not null, "the compiled echo hook did not load into the ModContext");

            object game = Instance();
            MethodInfo echoInt = Proxy("Game", "Echo").MakeGenericMethod(typeof(int));   // Echo<int> — value-type instantiation, own body

            // Baseline: Echo<int>(5) returns its input, 5.
            int baseline = (int)InvokeObj(echoInt, game, new object[] { 5 });
            Check.True(baseline == 5, $"baseline Echo<int>(5)={baseline}, expected 5 (identity) — the target is not what we think");

            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Game::Echo<int> inferred from concrete Echo(int))");

            // Hooked: Echo<int>(5) now fires the inferred hook -> replace with 5+1000.
            int hooked = (int)InvokeObj(echoInt, game, new object[] { 5 });
            Check.True(hooked == 1005, $"hooked Echo<int>(5)={hooked}, expected 1005 (input+1000 via replace) — the concrete Echo(int) hook did not bind to the inferred generic Echo<int> instantiation");

            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            int after = (int)InvokeObj(echoInt, game, new object[] { 5 });
            Check.True(after == 5, $"after teardown Echo<int>(5)={after}, expected 5 — the mod's hook was not removed");

            return $"compiled Hook<Game>.Echo(int) inferred + bound to the generic Echo<int> instantiation: "
                 + $"baseline {baseline} -> hooked {hooked} (input+1000), teardown {after} ({removed} sub removed)";
        });

        // GENERIC-INFERENCE over a REFERENCE-TYPE instantiation (Tier 3, the __Canon half). Echo<int> above proved
        // a value-type instantiation (own body); this proves a reference-type one — EchoWide<Player>, whose shared
        // __Canon body is routed by a runtime RGCTX MethodInfo* (HookCases.hook.canon.spill.route proves that
        // routing for the raw tier; a probe confirmed the ergonomic install resolves the IDENTICAL native mi). We
        // use EchoWide<T> because it carries [NoInlining] and a direct trampoline Game.EchoWidePlayer(p) =>
        // EchoWide<Player>(p,p,p,p) — a real, non-inlined compiled call (unlike Echo<Player>, which the JIT inlines,
        // so nothing to detour). The extra params are Il2CppSystem.Object (a proxy type — marshals via the proxy
        // path). The concrete hook `Player EchoWide(Player, Il2CppSystem.Object x3)` infers T=Player; the hook bumps
        // the arg's Health, a scalar oracle proving the inferred reference-type instantiation bound AND fired.
        suite.Add("modhost.hook.generic-inference-reftype", () =>
        {
            string src = """
                using Inutil;
                using ToyGame;
                public sealed class InutilEchoWideHook : Hook<Game>
                {
                    // concrete EchoWide(Player, ...) -> Tier 3 infers + hooks EchoWide<Player> (a __Canon ref-type body)
                    Player EchoWide(Player a, Il2CppSystem.Object b, Il2CppSystem.Object c, Il2CppSystem.Object d)
                    { a.Health += 1000; return a; }
                }
                """;
            if (!TryCompileAndLoad("inutilechowide", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilEchoWideHook") is not null, "the compiled echowide hook did not load into the ModContext");

            object game = Instance();
            Type playerType = Proxy("Player", "GetHealth").DeclaringType!;
            MethodInfo echoWidePlayer = Proxy("Game", "EchoWidePlayer");   // EchoWidePlayer(p) => EchoWide<Player>(p,p,p,p), NoInlining
            object player = PlayerInstance(playerType);                    // fresh Player, Health = 100

            // Baseline: EchoWidePlayer(player) -> EchoWide<Player> returns its first arg, Health unchanged.
            int h0 = Health(player);
            InvokeObj(echoWidePlayer, game, new object[] { player });
            int hBase = Health(player);
            Check.True(hBase == h0, $"baseline EchoWidePlayer left Health {h0}->{hBase}, expected unchanged — the target is not what we think");

            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Game::EchoWide<Player> inferred from concrete EchoWide(Player, ...))");

            // Hooked: EchoWidePlayer -> EchoWide<Player> fires the inferred reference-type hook -> a.Health += 1000.
            InvokeObj(echoWidePlayer, game, new object[] { player });
            int hHook = Health(player);
            Check.True(hHook == hBase + 1000, $"hooked EchoWidePlayer left Health {hBase}->{hHook}, expected +1000 — the concrete EchoWide(Player,...) hook did not bind to / fire on the inferred __Canon EchoWide<Player> instantiation");

            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            InvokeObj(echoWidePlayer, game, new object[] { player });
            int hAfter = Health(player);
            Check.True(hAfter == hHook, $"after teardown EchoWidePlayer changed Health {hHook}->{hAfter}, expected unchanged — the mod's hook was not removed");

            return $"compiled Hook<Game>.EchoWide(Player, Il2CppSystem.Object x3) inferred + bound to the __Canon "
                 + $"reference-type instantiation EchoWide<Player>: Health {hBase} -> {hHook} (+1000 when hooked), teardown {hAfter} ({removed} sub removed)";
        });

        // VALUE-TYPE CONTAINER arg — the box/unbox dance for a Nullable<T> passed BY VALUE (its bytes AT the
        // frame slot, not behind a pointer). A compiled Hook<Game>.GrantGold(int?) reads the incoming int? (box the
        // frame Nullable<int> -> engine MaterializeNullable -> natural int?), adds 100, and Proceeds (engine
        // DematerializeNullable -> unbox back into the frame slot). The game does Score += amount ?? 0, so the +100
        // is a Score DELTA oracle. Proves the value-type frame bridge round-trips a Nullable both directions.
        suite.Add("modhost.hook.valuetype-nullable-arg", () =>
        {
            string src = """
                using Inutil;
                using ToyGame;
                public sealed class InutilNullableArgHook : Hook<Game>
                {
                    void GrantGold(int? amount) => Proceed(amount + 100);   // box int? -> +100 -> unbox to the frame
                }
                """;
            if (!TryCompileAndLoad("inutilnullablearg", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilNullableArgHook") is not null, "the compiled nullable-arg hook did not load into the ModContext");

            object game = Instance();
            MethodInfo grant = Proxy("Game", "GrantGold");
            object arg5 = 5;   // reflection accepts a boxed int for a Nullable<int> param

            int s0 = Score(game); InvokeVoid(grant, game, arg5);
            int dBase = Score(game) - s0;
            Check.True(dBase == 5, $"baseline GrantGold(5) raised Score by {dBase}, expected 5 — the target is not what we think");

            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Game::GrantGold) — is the Nullable<int> param flipped to natural?");

            int s1 = Score(game); InvokeVoid(grant, game, arg5);
            int dHook = Score(game) - s1;
            Check.True(dHook == 105, $"hooked GrantGold(5) raised Score by {dHook}, expected 105 — the int? did not box->engine(+100)->unbox round-trip through the by-value frame slot");

            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            int s2 = Score(game); InvokeVoid(grant, game, arg5);
            int dAfter = Score(game) - s2;
            Check.True(dAfter == 5, $"after teardown GrantGold(5) raised Score by {dAfter}, expected 5 — the mod's hook was not removed");

            return $"compiled Hook<Game>.GrantGold(int?): baseline +{dBase}, hooked +{dHook} (box int? -> engine +100 -> unbox), teardown +{dAfter} ({removed} sub removed)";
        });

        // REF-BEARING VALUE-TYPE CONTAINER return — the full box/unbox dance's hardest case: an il2cpp ValueTuple
        // <string,string> is a value RETURNED via the sret buffer whose two slots are il2cpp STRING POINTERS (not CLR
        // refs). A compiled Hook<Game>.GetMetrics() Proceeds (box the sret bytes THROUGH the GC write barrier ->
        // engine MaterializeTuple -> natural (string,string)), replaces Item2, and returns it (engine
        // DematerializeTuple -> unbox back into the sret buffer). The game-side reader gets the flipped natural tuple,
        // so a scrambled element (a raw byte-copy putting il2cpp ptrs where CLR refs belong) shows as a wrong string.
        // Item1 is preserved (proving Proceed read the original) and Item2 replaced (proving the write-back).
        suite.Add("modhost.hook.valuetype-tuple-return", () =>
        {
            string src = """
                using Inutil;
                using ToyGame;
                public sealed class InutilTupleReturnHook : Hook<Game>
                {
                    (string, string) GetMetrics()
                    {
                        var m = Proceed<(string, string)>();   // box the sret ValueTuple -> natural (string,string)
                        return (m.Item1, "side=Scav");          // keep Item1, replace Item2 -> unbox to the sret buffer
                    }
                }
                """;
            if (!TryCompileAndLoad("inutiltuplereturn", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilTupleReturnHook") is not null, "the compiled tuple-return hook did not load into the ModContext");

            object game = Instance();
            MethodInfo getMetrics = Proxy("Game", "GetMetrics");

            (string a, string b) baseTuple = ((string, string))InvokeObj(getMetrics, game, null);
            Check.True(baseTuple.b == "side=Bear", $"baseline GetMetrics().Item2={baseTuple.b}, expected side=Bear — the target is not what we think");

            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Game::GetMetrics)");

            (string a, string b) hookTuple = ((string, string))InvokeObj(getMetrics, game, null);
            Check.True(hookTuple.b == "side=Scav" && hookTuple.a == baseTuple.a,
                $"hooked GetMetrics()=({hookTuple.a},{hookTuple.b}), expected (Item1 unchanged '{baseTuple.a}', Item2 'side=Scav') — the ref-bearing ValueTuple did not box->engine->unbox at the sret frame");

            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            (string a, string b) afterTuple = ((string, string))InvokeObj(getMetrics, game, null);
            Check.True(afterTuple.b == "side=Bear", $"after teardown GetMetrics().Item2={afterTuple.b}, expected side=Bear — the mod's hook was not removed");

            return $"compiled Hook<Game>.GetMetrics()->(string,string): baseline Item2={baseTuple.b}, hooked ({hookTuple.a},{hookTuple.b}) [Item1 preserved via Proceed, Item2 replaced via unbox], teardown {afterTuple.b} ({removed} sub removed)";
        });

        // REF-BEARING GAME VALUE STRUCT spelled DIRECTLY (the last by-value frame case): ToyGame.Loadout {int Gold;
        // string Owner} is a value type with a REFERENCE field, returned by value via sret. The engine treats it as
        // a leaf, so the frame bridge boxes it WHOLE — barriered, because it carries a ref — the hook reads the
        // original Loadout, bumps Gold, and returns it; the unbox writes the full struct back to the sret buffer.
        // Oracle: Gold changes (box read + mutate + unbox write) AND Owner is PRESERVED (the embedded string survived
        // the barriered box/unbox — the hazard a raw byte-blit would corrupt into a dangling/garbage ref).
        suite.Add("modhost.hook.valuetype-refbearing-return", () =>
        {
            string src = """
                using Inutil;
                using ToyGame;
                public sealed class InutilLoadoutHook : Hook<Player>
                {
                    Loadout MakeLoadout(int gold)
                    {
                        var lo = Proceed<Loadout>(gold);   // box the sret Loadout -> the game proxy (leaf identity)
                        lo.Gold += 1000;                    // mutate the boxed value
                        return lo;                          // unbox back to the sret buffer (Owner must survive)
                    }
                }
                """;
            if (!TryCompileAndLoad("inutilloadout", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilLoadoutHook") is not null, "the compiled loadout hook did not load into the ModContext");

            MethodInfo make = Proxy("Player", "MakeLoadout");
            object player = PlayerInstance(make.DeclaringType!);

            (int gold, string owner) baseVals = ReadLoadout(InvokeObj(make, player, new object[] { 5 }));
            Check.True(baseVals.gold == 5, $"baseline MakeLoadout(5).Gold={baseVals.gold}, expected 5 — the target is not what we think");

            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Player::MakeLoadout)");

            (int gold, string owner) hookVals = ReadLoadout(InvokeObj(make, player, new object[] { 5 }));
            Check.True(hookVals.gold == 1005 && hookVals.owner == baseVals.owner,
                $"hooked MakeLoadout(5)=Gold {hookVals.gold}/Owner '{hookVals.owner}', expected Gold 1005 with Owner '{baseVals.owner}' preserved — the ref-bearing value struct did not box/unbox with its embedded reference intact at the sret frame");

            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            (int gold, string owner) afterVals = ReadLoadout(InvokeObj(make, player, new object[] { 5 }));
            Check.True(afterVals.gold == 5, $"after teardown MakeLoadout(5).Gold={afterVals.gold}, expected 5 — the mod's hook was not removed");

            return $"compiled Hook<Player>.MakeLoadout(int)->Loadout: baseline Gold={baseVals.gold}, hooked Gold={hookVals.gold}/Owner='{hookVals.owner}' (box+mutate+unbox, ref intact), teardown Gold={afterVals.gold} ({removed} sub removed)";
        });

        // REF/OUT PARAM at the ergonomic tier: a hook body may DECLARE a game method's `ref`/`out` params and
        // they marshal like any arg — the frame slot is a POINTER to the value (Loc.Ind), which ctx.ArgPointer already
        // deref's, so the boundary reads/writes the element exactly like a by-value one. A ref/out param is a METHOD
        // OUTPUT: the body's final value is written back to the caller after it returns (WriteRefOutArgs). This case
        // takes Player.TryConsume(int cost, out int remaining) and REPLACES it — sets the out sentinel, DOESN'T touch
        // Health. Two oracles: `remaining` == 777 (the out writeback landed) AND Health stays 100 (the original never
        // ran — the body IS the method). Primitive out via StructureToPtr; no GC barrier (an int carries no refs).
        suite.Add("modhost.hook.ref-out-replace", () =>
        {
            string src = """
                using Inutil;
                using ToyGame;
                public sealed class InutilTryConsumeHook : Hook<Player>
                {
                    // replace: produce a sentinel `out remaining`, leave Health untouched (do NOT Proceed to the original)
                    bool TryConsume(int cost, out int remaining) { remaining = 777; return true; }
                }
                """;
            if (!TryCompileAndLoad("inutiltryconsume", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilTryConsumeHook") is not null, "the compiled try-consume hook did not load into the ModContext");

            MethodInfo tryConsume = Proxy("Player", "TryConsume");
            Type playerType = tryConsume.DeclaringType!;

            // Baseline (fresh Player, Health=100): TryConsume(5,out) -> Health 95, remaining 95, returns true.
            object pB = PlayerInstance(playerType);
            object[] aB = { 5, 0 };
            bool rB = (bool)InvokeObj(tryConsume, pB, aB);
            int remB = Convert.ToInt32(aB[1]); int hpB = Health(pB);
            Check.True(rB && remB == 95 && hpB == 95, $"baseline TryConsume(5,out)=ret {rB}/rem {remB}/Health {hpB}, expected true/95/95 — the target is not what we think");

            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Player::TryConsume) — did the `out int` param match?");

            // Hooked (fresh Player): the body REPLACES -> out remaining == 777 (writeback landed) AND Health == 100
            // (the original never ran, so it did not decrement — proving the body IS the method, not an add-on).
            object pH = PlayerInstance(playerType);
            object[] aH = { 5, 0 };
            bool rH = (bool)InvokeObj(tryConsume, pH, aH);
            int remH = Convert.ToInt32(aH[1]); int hpH = Health(pH);
            Check.True(rH && remH == 777 && hpH == 100, $"hooked TryConsume(5,out)=ret {rH}/rem {remH}/Health {hpH}, expected true/777/100 — the out param did not write back (777) or the replace did not suppress the original (Health should stay 100)");

            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            object pA = PlayerInstance(playerType);
            object[] aA = { 5, 0 };
            InvokeObj(tryConsume, pA, aA);
            int remA = Convert.ToInt32(aA[1]); int hpA = Health(pA);
            Check.True(remA == 95 && hpA == 95, $"after teardown TryConsume(5,out)=rem {remA}/Health {hpA}, expected 95/95 — the mod's hook was not removed");

            return $"compiled Hook<Player>.TryConsume(int, out int): baseline rem={remB}/hp={hpB}, hooked rem={remH}/hp={hpH} (out writeback + replace suppressed the original), teardown rem={remA}/hp={hpA} ({removed} sub removed)";
        });

        // REF-BEARING ref/out — the barriered write, the genuinely-new mechanism for ref/out at the ergonomic tier:
        // Player.Reforge(ref Loadout lo) takes a value type WITH a reference field by ref. Writing it back stores the
        // embedded string ref into the caller's (possibly-live-heap) ref/out storage, so it must go THROUGH the il2cpp
        // GC write barrier (ctx.ArgWritesBarriered -> ManagedToFrameValue barriered -> UnboxToFrame barriered) — a raw
        // byte-blit would drop/garble the ref under this incremental GC. The hook REPLACES: bump Gold by 100, keep
        // Owner. Oracle: Gold 7 -> 107 (write landed) AND Owner PRESERVED "Tester" (the embedded string survived the
        // barriered write — and it's the untouched MakeLoadout owner, NOT the original's "Tester*", proving replace).
        //
        // We drive Reforge through OUR runtime_invoke marshalling (ValueTypeBridge.InvokeUnboxed), NOT the Il2CppInterop
        // proxy — the proxy's generated glue can't carry a ref-bearing value BY REF, but runtime_invoke passes a value
        // type's UNBOXED data pointer as the byref arg (a value type by ref -> pointer to its bytes), so Reforge writes
        // through it and the SAME boxed Loadout reflects the change. The hook fires either way (both enter the detoured
        // methodPointer), and here the destination is a heap box's data region — a genuine barriered-write target, so
        // this ACTUALLY EXERCISES the barriered UnboxToFrame in-game rather than reasoning it works offline. Baseline-
        // gated only as a real safety net: if runtime_invoke itself can't round-trip `ref Loadout` in this build, SKIP.
        suite.Add("modhost.hook.ref-bearing-barrier", () =>
        {
            string src = """
                using Inutil;
                using ToyGame;
                public sealed class InutilReforgeHook : Hook<Player>
                {
                    // replace: bump Gold, keep Owner — the ref-bearing write-back is barriered so the string survives
                    void Reforge(ref Loadout lo) { lo.Gold += 100; }
                }
                """;
            if (!TryCompileAndLoad("inutilreforge", src, out Assembly modAsm, out ModContext alc)) return null;
            Check.True(modAsm.GetType("InutilReforgeHook") is not null, "the compiled reforge hook did not load into the ModContext");

            MethodInfo make = Proxy("Player", "MakeLoadout");
            object player = PlayerInstance(make.DeclaringType!);   // Name "Tester" -> MakeLoadout owner "Tester"

            // Baseline: unhooked Reforge(ref {7,"Tester"}) via runtime_invoke -> {8,"Tester*"}. If runtime_invoke can't
            // carry a ref-bearing value by ref in this build, SKIP honestly (never a false RED) — but it should.
            object loB = InvokeObj(make, player, new object[] { 7 });
            Inutil.Marshal.ValueTypeBridge.InvokeUnboxed(player, "Reforge", loB);
            (int gold, string owner) baseVals = ReadLoadout(loB);
            if (!(baseVals.gold == 8 && baseVals.owner == "Tester*"))
                Check.Skip($"runtime_invoke does not round-trip `ref Loadout` in this build (baseline Reforge gave Gold={baseVals.gold}/Owner='{baseVals.owner}', expected 8/'Tester*') — the barriered ref/out write is wired + offline-verified but not observable here");

            int wired = global::Inutil.Mods.Discover(modAsm);
            Check.True(wired == 1, $"Discover wired {wired} hook method(s), expected 1 (Player::Reforge) — did the `ref Loadout` param match?");

            // Hooked (replace): the barriered UnboxToFrame writes {107, "Tester"-ptr} into the boxed Loadout's HEAP
            // data through the GC barrier. Gold 7 -> 107 (write landed) AND Owner PRESERVED "Tester" (the embedded
            // string survived — a missing barrier corrupts/drops it) AND != "Tester*" (the original never ran: replace).
            object loH = InvokeObj(make, player, new object[] { 7 });
            Inutil.Marshal.ValueTypeBridge.InvokeUnboxed(player, "Reforge", loH);
            (int gold, string owner) hookVals = ReadLoadout(loH);
            Check.True(hookVals.gold == 107 && hookVals.owner == "Tester",
                $"hooked Reforge(ref)=Gold {hookVals.gold}/Owner '{hookVals.owner}', expected Gold 107 with Owner 'Tester' preserved — the barriered ref-bearing ref/out write did not land Gold or dropped the embedded string (a missing GC barrier corrupts the ref)");

            int removed = global::Inutil.Mods.RemoveAll(alc);
            alc.Unload();
            object loA = InvokeObj(make, player, new object[] { 7 });
            Inutil.Marshal.ValueTypeBridge.InvokeUnboxed(player, "Reforge", loA);
            (int gold, string owner) afterVals = ReadLoadout(loA);
            Check.True(afterVals.gold == 8 && afterVals.owner == "Tester*", $"after teardown Reforge(ref)=Gold {afterVals.gold}/Owner '{afterVals.owner}', expected 8/'Tester*' — the mod's hook was not removed");

            return $"compiled Hook<Player>.Reforge(ref Loadout) via runtime_invoke: baseline Gold={baseVals.gold}/Owner='{baseVals.owner}', hooked Gold={hookVals.gold}/Owner='{hookVals.owner}' (barriered ref-bearing write into a heap box, string intact), teardown Gold={afterVals.gold} ({removed} sub removed)";
        });
    }

    // Compile `src` in-game against the DEPLOYED proxies, and load the result into a fresh collectible ModContext.
    // Shared by the capstone (a scalar Hook) and the container-arg case, so the in-game compile+load recipe lives
    // in ONE place. Returns false — after an honest Check.Skip — where Inutil.Mods /
    // Roslyn isn't deployed (e.g. the MelonLoader battery layout); on success yields the loaded
    // assembly + its ALC. NB Check.Skip throws, so the `return false` tail is only for the compiler's benefit.
    internal static bool TryCompileAndLoad(string asmName, string src, out Assembly modAsm, out ModContext alc)
    {
        modAsm = null!; alc = null!;

        // Inutil.Mods (Roslyn) present? If not, this environment can't compile a .cs mod — honest SKIP.
        Assembly mods;
        try { mods = Assembly.Load(new AssemblyName("Inutil.Mods")); }
        catch { Check.Skip("Inutil.Mods.dll (the no-build mod host + Roslyn) not deployed here"); return false; }

#if MELONLOADER
        // The fixture's mod source is authored BepInEx-style (`using ToyGame;`), because BepInEx's Il2CppInterop
        // STRIPS the Il2Cpp prefix from game namespaces. MelonLoader KEEPS it (Il2CppToyGame.*), so a real melon
        // modder writes `using Il2CppToyGame;`. Adapt the source to the loader's actual proxy namespace — the same
        // spelling difference IsFrameworkAssembly handles at the assembly level.
        src = src.Replace("using ToyGame;", "using Il2CppToyGame;");
#endif

        // Reference dirs for the in-game compile: this plugin's own dir (Inutil.dll), the interop proxies
        // (ToyGame.Game + Il2Cpp*), and Il2CppInterop (core). CsModCompiler appends the running net6 runtime BCL
        // as the guaranteed BCL source. Because the interop-patch normalizes each proxy's System.Private.CoreLib
        // reference to the runtime BCL version (PatchDirectory.NormalizeCoreLibRef), the compile is BCL-consistent
        // with no version skew. The two loaders lay these dirs out DIFFERENTLY — resolved per-loader here, matching
        // the production shims' own .cs mod loop (BepInExPlugin vs InutilMelonMod StartCsModLoop):
        //   BepInEx : plugins/ + ../interop + ../core
        //   Melon   : Mods/    + ../MelonLoader/Il2CppAssemblies + ../MelonLoader/net6
        // Absent -> SKIP.
        string selfDir = Path.GetDirectoryName(typeof(ModHostCases).Assembly.Location)!;   // the deploy dir
#if MELONLOADER
        string gameRoot = Path.GetDirectoryName(selfDir)!;               // .../<loader>  (Mods sits directly under it)
        string ml = Path.Combine(gameRoot, "MelonLoader");
        string interop = Path.Combine(ml, "Il2CppAssemblies");          // melon interop proxies
        string core = Path.Combine(ml, "net6");                         // MelonLoader + Il2CppInterop
#else
        string root = Path.GetDirectoryName(selfDir)!;                   // .../BepInEx
        string interop = Path.Combine(root, "interop");
        string core = Path.Combine(root, "core");
#endif
        if (!Directory.Exists(interop) || !Directory.Exists(core))
            { Check.Skip($"interop/core proxy dirs not present ({interop} / {core})"); return false; }
        var refDirs = new[] { selfDir, interop, core };

        string srcDir = Path.Combine(Path.GetTempPath(), "inutil-modhost-src");
        Directory.CreateDirectory(srcDir);
        string srcFile = Path.Combine(srcDir, asmName + ".cs");
        File.WriteAllText(srcFile, src);

        // COMPILE (reflection into Inutil.CsModCompiler — it lives in the optional Roslyn-carrying Inutil.Mods.dll).
        Type compilerT = mods.GetType("Inutil.CsModCompiler") ?? throw new AssertException("Inutil.CsModCompiler type missing from Inutil.Mods");
        MethodInfo compile = compilerT.GetMethod("Compile", new[] { typeof(string), typeof(IReadOnlyList<string>), typeof(IReadOnlyList<string>) })
            ?? throw new AssertException("CsModCompiler.Compile(string, IReadOnlyList<string>, IReadOnlyList<string>) not found");
        object result = compile.Invoke(null, new object[] { asmName, new[] { srcFile }, refDirs })!;
        Type rt = result.GetType();
        bool ok = (bool)rt.GetProperty("Ok")!.GetValue(result)!;
        byte[]? pe = (byte[]?)rt.GetField("Pe")!.GetValue(result);
        string[] errors = (string[])rt.GetField("Errors")!.GetValue(result)!;
        Check.True(ok && pe is { Length: > 0 }, $"mod compile failed: {string.Join(" | ", errors)}");

        // LOAD into a collectible ModContext (the mod's own ALC; RemoveAll + Unload un-roots its hooks + statics).
        alc = new ModContext("inutil:" + asmName);
        modAsm = alc.LoadFromStream(new MemoryStream(pe!));
        return true;
    }

    // Invoke a void proxy method with one argument; a hook/marshalling failure surfaces LOUD, never a swallowed skip.
    internal static void InvokeVoid(MethodInfo mi, object target, object? arg)
    {
        try { mi.Invoke(target, new[] { arg }); }
        catch (TargetInvocationException tie)
        { Check.True(false, $"invoking {mi.Name} threw {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}"); }
    }

    // Invoke a value-returning proxy method, unwrapping the reflection wrapper so a real hook/marshalling failure
    // surfaces its own exception (a LOUD fail), not an opaque TargetInvocationException.
    static object InvokeObj(MethodInfo mi, object target, object?[]? args)
    {
        try { return mi.Invoke(target, args)!; }
        catch (TargetInvocationException tie)
        { Check.True(false, $"invoking {mi.Name} threw {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}"); throw; }
    }

    // Construct a Player proxy (ctor(string) has no parameterless twin, so Game's Instance() path can't reach it).
    // Il2CppInterop construction is version-sensitive -> an honest SKIP if unreachable, like the ValueType cases.
    static object PlayerInstance(Type playerType)
    {
        try
        {
            MethodInfo? create = playerType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (create is not null) return create.Invoke(null, new object[] { "Tester" })!;
            return Activator.CreateInstance(playerType, "Tester")!;
        }
        catch (Exception ex) { Check.Skip($"il2cpp Player proxy construction not reachable: {ex.GetType().Name}: {ex.Message}"); throw; }
    }

    // Player.GetHealth() — the side-channel oracle for "did the original run?" (the ref/out replace case checks Health
    // is unchanged because the hook replaced TryConsume rather than proceeding to the health-decrementing original).
    static int Health(object player)
    {
        MethodInfo? gh = player.GetType().GetMethod("GetHealth", Type.EmptyTypes);
        Check.True(gh is not null, "Player.GetHealth() not found on the proxy — cannot read Health for the replace oracle");
        return Convert.ToInt32(gh!.Invoke(player, null));
    }

    // Read a Loadout proxy's Gold (int) + Owner (string) — Il2CppInterop projects the struct's fields as properties
    // (fallback to fields). The cross-ALC-safe read of the ref-bearing value the hook round-tripped at the frame.
    static (int gold, string owner) ReadLoadout(object lo)
    {
        Type t = lo.GetType();
        object? goldObj = t.GetProperty("Gold")?.GetValue(lo) ?? t.GetField("Gold")?.GetValue(lo);
        object? ownerObj = t.GetProperty("Owner")?.GetValue(lo) ?? t.GetField("Owner")?.GetValue(lo);
        Check.True(goldObj is not null, "Loadout.Gold not readable on the returned proxy — is this really a Loadout?");
        return (Convert.ToInt32(goldObj), ownerObj?.ToString() ?? "");
    }

    // ── minimal target resolution (mirrors HookCases; kept local so this case is self-contained) ─────────────
    static object? _game;
    static object Instance()
    {
        if (_game is not null) return _game;
        Type t = Proxy("Game", "Tally").DeclaringType!;
        try { return _game = t.GetConstructor(Type.EmptyTypes)?.Invoke(null) ?? Activator.CreateInstance(t)!; }
        catch (Exception ex) { Check.Skip($"il2cpp Game proxy construction not reachable: {ex.GetType().Name}: {ex.Message}"); throw; }
    }

    static (object game, MethodInfo tally) Target() => (Instance(), Proxy("Game", "Tally"));

    static int Score(object game)
    {
        PropertyInfo? p = game.GetType().GetProperty("Score");
        return p is not null ? (int)p.GetValue(game)! : 0;
    }

    static int Call(MethodInfo mi, object game, int arg)
    {
        try { return (int)mi.Invoke(game, new object[] { arg })!; }
        catch (TargetInvocationException tie)
        { Check.True(false, $"invoking {mi.Name} threw {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}"); throw; }
    }

    internal static MethodInfo Proxy(string typeSimpleName, string methodName)
    {
        Assembly? acs = null;
        try { acs = Assembly.Load(new AssemblyName("Assembly-CSharp")); } catch { }
        foreach (Assembly asm in (acs is not null ? new[] { acs } : Array.Empty<Assembly>()).Concat(AppDomain.CurrentDomain.GetAssemblies()))
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x is not null).ToArray()!; }
            catch { continue; }
            Type? t = types.FirstOrDefault(x => x.Name == typeSimpleName && x.GetMethod(methodName) is not null);
            if (t is not null) return t.GetMethod(methodName)!;
        }
        throw new AssertException($"{typeSimpleName}::{methodName} proxy not found — the Assembly-CSharp interop proxy did not load");
    }
}
