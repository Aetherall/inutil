using System.Collections;
using System.Reflection;
using Inutil.Marshal;

namespace Inutil.Battery;

// EXACT PROXY TYPES, END-TO-END (docs/reference/exact-proxy-types.md): an object read through a BASE-declared seam
// arrives as a proxy of its ACTUAL il2cpp type, in a real il2cpp runtime, under both loaders.
//
// THE DEFECT. Il2CppInterop materializes at the DECLARED type of the seam: Il2CppObjectPool.Get<T> reads the
// object's real class only to test whether it was injected, then builds T. So a Boss read through an Entity-typed
// property was an Entity proxy, `is Boss` was FALSE, and a `switch` over subtypes matched nothing — with no error
// anywhere. The failure is asymmetric and that is why it hid: code that constructs its own objects gets an exact
// proxy and looks correct, until the same code is handed one that came from the game.
//
// WHAT IS ASSERTED, and why each case asserts THREE things. `is Boss` alone could pass for the wrong reason — a
// seam that was never base-declared to begin with, or a fixture that never held a derived object. So every case
// states: (1) the seam's DECLARED type really is the base (read off the proxy's own signature — non-vacuity),
// (2) the game's OWN typed read-back confirms the object behind it is the derived one, and only then (3) the
// proxy the modder receives satisfies `is Derived`.
//
// THE RUNGS ARE THE MATERIALIZATION SITES, not a list of seams for its own sake: a property/field read (the
// generated proxy body), a container ELEMENT (which materializes inside Il2Cppmscorlib's OWN List/Dictionary proxy
// — the module every other pass deliberately skips), the System.Object seam (the widest declared type there is), a
// CROSS-MODULE derived type, and the non-generic materializer the hook boundary uses for `Self` and its args.
// Fixing only the first would leave a mod's own hook receiving a base-typed `this` — the same bug, another door.
public static class ExactTypeCases
{
    public static void Register(Suite suite)
    {
        // A PROPERTY returning the base. The plainest rung, and the one every consumer meets first.
        suite.Add("exact.property.derived", () =>
        {
            (object game, Type boss) = Fixture();
            AssertDeclaredIs(game, "get_Champion", "Entity");
            Check.True(PeekInt(game, "PeekChampionTag") == 1000,
                "non-vacuity: the game's own read says Champion is not a Boss — the fixture is wrong, not the seam");

            object champ = Call(game, "get_Champion")!;
            Check.True(boss.IsInstanceOfType(champ),
                $"a Boss read through an Entity-declared property arrived as {champ.GetType().Name} — `is Boss` is still false");
            return $"Champion: declared Entity, is Boss ({champ.GetType().Name})";
        });

        // A FIELD typed the base. Il2CppInterop renders a field as a property whose body reads through the field
        // wrapper — a different generated body than the one above, materializing through the same primitive.
        suite.Add("exact.field.derived", () =>
        {
            (object game, Type boss) = Fixture();
            AssertDeclaredIs(game, "get_Warden", "Entity");
            object warden = Call(game, "get_Warden")!;
            Check.True(boss.IsInstanceOfType(warden),
                $"a Boss read through an Entity-declared FIELD arrived as {warden.GetType().Name}");
            return $"Warden: declared Entity, is Boss ({warden.GetType().Name})";
        });

        // A container ELEMENT. This is the rung that decides whether the pass may be game-scoped: the element read
        // runs inside Il2CppSystem.List's own proxy, in Il2Cppmscorlib — the framework module the naturalizing
        // families skip by design. A game-scoped retarget passes every case above and fails this one.
        suite.Add("exact.container.element", () =>
        {
            (object game, Type boss) = Fixture();
            Check.True(PeekInt(game, "PeekLineupTag", 0) == 1000,
                "non-vacuity: the game's own read says Lineup[0] is not a Boss");

            object first = ElementAt(Call(game, "get_Lineup")!, 0)
                ?? throw new AssertException("Lineup[0] came back null");
            Check.True(boss.IsInstanceOfType(first),
                $"a Boss read as an element of List<Entity> arrived as {first.GetType().Name} — the framework proxies " +
                "still materialize at the declared element type");
            return $"Lineup[0]: element of List<Entity>, is Boss ({first.GetType().Name})";
        });

        // A Dictionary VALUE — a different bridge path than the list element (pairs, not a sequence).
        suite.Add("exact.dictionary.value", () =>
        {
            (object game, Type boss) = Fixture();
            Check.True(PeekInt(game, "PeekWarbandTag", "boss") == 1000,
                "non-vacuity: the game's own read says Warband[\"boss\"] is not a Boss");

            object value = DictValue(Call(game, "get_Warband")!, "boss")
                ?? throw new AssertException("Warband[\"boss\"] came back null");
            Check.True(boss.IsInstanceOfType(value),
                $"a Boss read as a Dictionary<string,Entity> VALUE arrived as {value.GetType().Name}");
            return $"Warband[boss]: dictionary value, is Boss ({value.GetType().Name})";
        });

        // The System.Object seam — the widest declared type in the runtime, and the one an erased handle arrives
        // through. Declared Il2CppSystem.Object; nothing about the seam narrows it at all.
        suite.Add("exact.object.seam", () =>
        {
            (object game, Type boss) = Fixture();
            AssertDeclaredIs(game, "Loot", "Object");
            object loot = Call(game, "Loot")!;
            Check.True(boss.IsInstanceOfType(loot),
                $"a Boss returned through a System.Object seam arrived as {loot.GetType().Name}");
            return $"Loot(): declared Il2CppSystem.Object, is Boss ({loot.GetType().Name})";
        });

        // CROSS-MODULE: the declared type's proxy lives in ToyGame.Core.dll, the actual type's in Assembly-CSharp.
        // The map is written across the whole interop directory in one walk precisely so this resolves.
        suite.Add("exact.crossmodule.derived", () =>
        {
            (object game, _) = Fixture();
            Type sx = FindProxyType("ToyGame.SessionEx") ?? throw new AssertException(
                "ToyGame.SessionEx proxy absent — the fixture predates the exact-type types (rebuild ToyGame)");
            AssertDeclaredIs(game, "OpenSessionEx", "Session");
            object s = Call(game, "OpenSessionEx")!;
            Check.True(sx.IsInstanceOfType(s),
                $"a SessionEx (Assembly-CSharp) read through a Session-declared seam (ToyGame.Core) arrived as {s.GetType().Name}");
            return $"OpenSessionEx: declared ToyGame.Core Session, is SessionEx ({s.GetType().Name})";
        });

        // The NON-GENERIC materializer — the entry the hook boundary builds `Self`, its reference args and its
        // return through. Exercised directly (rather than by installing a hook) so a failure here is unambiguous:
        // the hook tier has its own cases, and this one is about type identity alone.
        suite.Add("exact.materialize.declared-base", () =>
        {
            (object game, Type boss) = Fixture();
            Type entity = FindProxyType("ToyGame.Entity") ?? throw new AssertException("ToyGame.Entity proxy absent");
            object champ = Call(game, "get_Champion")!;
            nint ptr = Pointer(champ);

            object again = Il2CppObjects.Materialize(entity, ptr)
                ?? throw new AssertException("Materialize returned null for a live object pointer");
            Check.True(boss.IsInstanceOfType(again),
                $"Materialize(declared: Entity, ptr) built a {again.GetType().Name} — the hook boundary would hand a " +
                "mod a base-typed Self for a derived receiver");
            Check.True(Il2CppObjects.ExactTypeOf(entity, ptr) == boss,
                "ExactTypeOf did not resolve the object's class to the Boss proxy");
            return $"Materialize(Entity, ptr) -> {again.GetType().Name}";
        });

        // The ESCAPE HATCH still works. TryCast/Cast were the documented way to recover the real type and mods are
        // full of them; exact typing makes them redundant, never wrong. (A proxy is still an Il2CppObjectBase over
        // a pointer — casting is unaffected by which CLR type wraps it.)
        suite.Add("exact.trycast.still-works", () =>
        {
            (object game, Type boss) = Fixture();
            object champ = Call(game, "get_Champion")!;
            MethodInfo tryCast = champ.GetType().GetMethods()
                .First(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            object? cast = tryCast.MakeGenericMethod(boss).Invoke(champ, null);
            Check.True(cast is not null, "TryCast<Boss>() returned null on an object that IS a Boss");
            return "TryCast<Boss>() still recovers the derived type";
        });

        // The FALLBACK, asserted as a behaviour rather than assumed. A generic instantiation has no row in the map
        // (its il2cpp identity is shared with the open definition, so a row would be a trap) — it must materialize
        // at the DECLARED type and work, exactly as it did before this pass existed. The failure this rules out is
        // an unresolvable class throwing, or resolving to something wrong, on the hottest path in interop.
        suite.Add("exact.fallback.unmapped", () =>
        {
            (object game, _) = Fixture();
            object lineup = Call(game, "get_Lineup")!;                       // natural List<Entity> — a BCL object
            Check.True(lineup is not null, "the flipped container read came back null");

            Type il2cppObject = FindProxyType("Il2CppSystem.Object") ?? throw new AssertException("Il2CppSystem.Object absent");
            object champ = Call(game, "get_Champion")!;
            // An unmapped class must not throw and must not be rejected — it simply yields the declared type.
            Type? none = Il2CppObjects.ExactTypeOf(il2cppObject, Pointer(champ));
            Check.True(none is null || il2cppObject.IsAssignableFrom(none),
                "a resolution escaped the assignability guard — that is the one way this pass could be silently wrong");

            var (exact, declared, rejected, mapSize) = Il2CppObjects.Stats;
            Check.True(mapSize > 0, "no exact-type map was loaded — exact typing is OFF for this tree (unpatched proxies?)");
            Check.True(rejected == 0,
                $"{rejected} resolution(s) were REJECTED by the assignability guard — a map row names a type that is not " +
                $"a subtype of the seam it was resolved for: [{string.Join("; ", Il2CppObjects.Rejections)}]");
            return $"map={mapSize} types, exact={exact}, declared={declared}, rejected={rejected}";
        });
    }

    // The Game proxy (freshly constructed, so its field initializers ran in-game) + the Boss proxy type.
    static (object Game, Type Boss) Fixture()
    {
        Type gameT = FindProxyType("ToyGame.Game") ?? throw new AssertException(
            "ToyGame.Game proxy not found — its interop assembly did not load");
        Type boss = FindProxyType("ToyGame.Boss") ?? throw new AssertException("ToyGame.Boss proxy not found");
        if (gameT.GetMethod("get_Champion") is null)
            Check.Skip("ToyGame.Game::Champion absent — fixture predates the exact-type seams (rebuild ToyGame)");
        object game;
        try { game = Activator.CreateInstance(gameT)!; }
        catch (Exception ex) { Check.Skip($"il2cpp proxy construction of {gameT.FullName} not reachable: {ex.GetType().Name}: {ex.Message}"); throw; }
        return (game, boss);
    }

    // NON-VACUITY, read off the proxy's own signature: the seam must really be declared as `expectedDeclared`.
    // Without this every case above could pass on a fixture whose seam was narrowed to the derived type, proving
    // nothing at all.
    static void AssertDeclaredIs(object game, string method, string expectedDeclared)
    {
        MethodInfo m = game.GetType().GetMethod(method)
            ?? throw new AssertException($"{game.GetType().Name}::{method} absent");
        Check.True(m.ReturnType.Name == expectedDeclared,
            $"non-vacuity: {method} is declared to return {m.ReturnType.Name}, not {expectedDeclared} — the seam is not base-declared");
    }

    static object? Call(object target, string method, params object?[] args)
        => target.GetType().GetMethod(method)?.Invoke(target, args)
           ?? throw new AssertException($"{target.GetType().Name}::{method} returned null or is absent");

    static int PeekInt(object target, string method, params object?[] args)
        => (int)(target.GetType().GetMethod(method)?.Invoke(target, args)
                 ?? throw new AssertException($"{target.GetType().Name}::{method} absent"));

    static nint Pointer(object proxy)
        => (nint)(IntPtr)proxy.GetType().GetProperty("Pointer")!.GetValue(proxy)!;

    // The flipped container is a natural BCL List<T>/Dictionary<K,V], so it is read as one — through the
    // non-generic interfaces, since the element type is a proxy this assembly cannot name at compile time.
    static object? ElementAt(object list, int index)
    {
        var l = (IList)list;
        return l.Count > index ? l[index] : throw new AssertException($"the container has {l.Count} element(s)");
    }

    static object? DictValue(object dict, string key)
    {
        var d = (IDictionary)dict;
        return d.Contains(key) ? d[key] : throw new AssertException($"no '{key}' entry (count={d.Count})");
    }

    // Both loader spellings. MelonLoader's generator prefixes a GAME namespace with `Il2Cpp`
    // (`Il2CppToyGame.Game`) where BepInEx's does not — the same divergence `CecilProjector.IsFrameworkAssembly`
    // strips on the patch side. Nothing in the map cares (it keys by the ORIGINAL il2cpp identity and carries the
    // proxy's own spelling per module); only a test naming a type by hand has to know.
    static Type? FindProxyType(string fullName)
        => Resolve(fullName) ?? Resolve("Il2Cpp" + fullName);

    static Type? Resolve(string fullName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t;
            try { t = asm.GetType(fullName, throwOnError: false); }
            catch { continue; }
            if (t is not null) return t;
        }
        return null;
    }
}
