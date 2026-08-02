using System.Reflection;

namespace Inutil.Battery;

// The container-return flip, END-TO-END in a booted IL2CPP game (at the IL-rewrite boundary + runtime).
// InteropPatch's container pass flips the non-virtual ToyGame.Game::GetHighScores from the wrapper
// Il2CppSystem.Collections.Generic.List<int> to a NATURAL System.Collections.Generic.List<int>, splicing
// `call Inutil.Marshal.Il2CppMarshal::ToManaged<List<int>>` into the body; Inutil.dll is deployed beside the
// loader so that call resolves at JIT and drives the (already in-game-proven) MaterializeList.
//
// REFLECTION-ONLY by design, like TaskFlipCases: the battery keeps its compile closure (no game-proxy reference),
// so it stays the harness's own liveness check while proving the SECOND seam (IL-rewrite) consumes the marshaller.
//   deployed — the patcher's container pass ran on the DEPLOYED proxy AND the flipped IL loaded.
//   runs     — invoking the flip actually executes the spliced ToManaged and yields a REAL BCL List<int>
//              materialized from the il2cpp list (best-effort: il2cpp proxy construction is version-sensitive, so
//              a construction failure is an honest SKIP — `deployed` already proves patch + load + deploy).
public static class ContainerFlipCases
{
    public static void Register(Suite suite)
    {
        // deployed = the return type flipped to natural; runs = invoking it returns a REAL BCL value the spliced
        // ToManaged materialized (not the il2cpp proxy wearing a System.* label — a signature-only lie).
        AddDeployed(suite, "container.list-return.flip.deployed", "GetHighScores", "System.Collections.Generic.List");
        AddRuns(suite, "container.list-return.flip.runs", "GetHighScores",
            r => r is List<int>, r => $"natural List<int> [{string.Join(",", (List<int>)r)}]");

        // DECISIVE new-family experiment: HashSet<T> — a genuinely NEW container SHAPE, not a List re-spelling.
        // GetTagSet's return flips to natural System.HashSet and the SHARED collection materializer produces a real
        // HashSet<int> (a List would fail `r is HashSet<int>` — proving the Set stays a Set through the new ConvKind).
        AddDeployed(suite, "container.set-return.flip.deployed", "GetTagSet", "System.Collections.Generic.HashSet");
        // READ-BACK now GREEN via the CopyTo array read (ContainerBridge.TryCopyToArray): the game strips a set's direct
        // struct GetEnumerator, and its explicit-impl enumerator throws a spurious "Collection was modified" version-
        // check under Il2CppInterop's reflective MoveNext — so we read the version-FREE way ICollection<T> guarantees
        // (CopyTo into a fresh il2cpp array, then enumerate the array). `r is HashSet<int>` proves the Set stays a Set
        // (a List would fail it); the set always contains the literal 7 (Score is 0 on a fresh proxy, so the other two
        // elements may collapse — 7 is the version-independent oracle that the elements really materialized).
        AddRuns(suite, "container.set-return.flip.runs", "GetTagSet",
            r => r is HashSet<int> hs && hs.Contains(7),
            r => { var hs = (HashSet<int>)r; return $"natural HashSet<int> [{string.Join(",", hs)}] (contains literal 7 — CopyTo read-back)"; });

        AddDeployed(suite, "container.dict-return.flip.deployed", "GetTallies", "System.Collections.Generic.Dictionary");
        AddRuns(suite, "container.dict-return.flip.runs", "GetTallies",
            r => r is Dictionary<string, int>, r => $"natural Dictionary<string,int> ({((Dictionary<string, int>)r).Count} entries)");

        AddDeployed(suite, "container.tuple-return.flip.deployed", "GetMetrics", "System.ValueTuple");
        AddRuns(suite, "container.tuple-return.flip.runs", "GetMetrics",
            r => r is ValueTuple<string, string>, r => { var (a, b) = ((string, string))r; return $"natural (string,string) ({a},{b})"; });

        // NESTED-RETURN recursion: Game::SplitSquad returns a tuple whose FIRST element is itself an il2cpp
        // wrapper — Il2CppSystem.ValueTuple<Il2CppReferenceArray<Player>, string>. The container pass now naturalizes
        // the element too, flipping it to a FULLY natural (Player[], string) — never a shallow flip that keeps the
        // inner Il2CppReferenceArray (a half-correct lie). Reflection-only: Player is a game proxy, so the array's
        // element type / member are read reflectively.
        suite.Add("container.nested-return.flip.deployed", () =>
        {
            MethodInfo mi = FindProxyMethod("Game", "SplitSquad", out string typeName);
            string rt = mi.ReturnType.FullName ?? mi.ReturnType.Name;
            Check.True(rt.StartsWith("System.ValueTuple", StringComparison.Ordinal),
                $"{typeName}::SplitSquad still returns '{rt}' — the container pass did not flip the nested-return tuple");
            // The recursion's whole point: Item1 is a NATURAL BCL array (Player[]), not the leaked
            // Il2CppReferenceArray<Player> a shallow flip would keep.
            Type item1 = mi.ReturnType.GetGenericArguments()[0];
            Check.True(item1.IsArray && !item1.FullName!.Contains("Il2CppReferenceArray", StringComparison.Ordinal),
                $"{typeName}::SplitSquad Item1 is '{item1.FullName}', not a natural BCL array — the nested element was not naturalized (half-flip)");
            return $"{typeName}::SplitSquad returns natural ({item1.Name}, string) — nested array element naturalized, not a leaked wrapper";
        });

        suite.Add("container.nested-return.flip.runs", () =>
        {
            MethodInfo mi = FindProxyMethod("Game", "SplitSquad", out string typeName);
            object game;
            try { game = Construct(mi.DeclaringType!); }
            catch (Exception ex) { Check.Skip($"il2cpp Game proxy construction not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            object? result;
            try { result = mi.Invoke(game, null); }             // the spliced nested ToManaged<(Player[],string)> runs here
            catch (TargetInvocationException tie)
            {
                Check.True(false, $"invoking the flipped SplitSquad threw {tie.InnerException?.GetType().Name}: " +
                                  $"{tie.InnerException?.Message} — the spliced nested ToManaged likely did not resolve/materialize");
                return null;
            }

            Check.True(result is not null, "SplitSquad returned null");
            Type rt = result!.GetType();
            object? item1 = rt.GetField("Item1")?.GetValue(result);
            object? item2 = rt.GetField("Item2")?.GetValue(result);
            Check.True(item1 is Array { Length: 1 },
                $"SplitSquad Item1 is '{item1?.GetType().FullName ?? "null"}' (expected a natural Player[] of length 1) — the nested array did not materialize");
            object player = ((Array)item1!).GetValue(0)!;
            // The array element is a natural game proxy (Player); read Name reflectively (property or field).
            object? name = player.GetType().GetProperty("Name")?.GetValue(player)
                           ?? player.GetType().GetField("Name")?.GetValue(player);
            Check.True((name as string) == "base" && (item2 as string) == "base",
                $"SplitSquad content is ({name},{item2}), expected (base,base) — the nested (Player[],string) round-trip garbled");
            return $"invoked SplitSquad -> natural (Player[]{{{name}}}, \"{item2}\"): nested-return ToManaged recursed through the array element";
        });

        // VIRTUAL container RETURN (via the shared VirtualSlotPlanner — the container family's virtual half): Entity::Ranks
        // (base) and Boss::Ranks (override) must flip their return in LOCKSTEP or the vtable slot's signature is
        // inconsistent (the override no longer matches the base). Both ret-tail-spliced through ToManaged (unlike the
        // value-Nullable slot's full-body replace — a container flip rides Il2CppInterop's own return value).
        suite.Add("container-return.virtual.flip.deployed", () =>
        {
            MethodInfo baseR = FindProxyMethod("Entity", "Ranks", out string baseT);
            MethodInfo ovrR = FindProxyMethod("Boss", "Ranks", out string bossT);
            string brt = baseR.ReturnType.FullName ?? "", ort = ovrR.ReturnType.FullName ?? "";
            Check.True(brt.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal)
                && ort.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal),
                $"virtual Ranks slot not flipped in lockstep: {baseT}={brt}, {bossT}={ort}");
            return $"virtual Ranks slot flipped in lockstep: {baseT} + {bossT} -> System.Collections.Generic.List<int>";
        });

        suite.Add("container-return.virtual.flip.runs", () =>
        {
            MethodInfo ovrR = FindProxyMethod("Boss", "Ranks", out string bossT);
            object boss;
            try { boss = Construct(ovrR.DeclaringType!); }
            catch (Exception ex) { Check.Skip($"il2cpp Boss proxy construction not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            object? result;
            try { result = ovrR.Invoke(boss, new object[] { 2 }); }   // the spliced ToManaged<List<int>> runs here
            catch (TargetInvocationException tie)
            {
                Check.True(false, $"invoking the flipped Boss::Ranks threw {tie.InnerException?.GetType().Name}: " +
                                  $"{tie.InnerException?.Message} — the spliced Il2CppMarshal.ToManaged likely did not resolve/materialize");
                return null;
            }
            Check.True(result is List<int>, $"Boss::Ranks(2) returned {result?.GetType().FullName ?? "null"}, not a natural List<int>");
            var list = (List<int>)result!;
            // Boss overrides with n*2 (Tag=1000), so Ranks(2) = [4, 1000] — list[0]==4 proves the OVERRIDE dispatched
            // through the flipped virtual slot (the base body is n, which would give 2, not 4).
            Check.True(list.Count == 2 && list[0] == 4,
                $"Boss::Ranks(2) = [{string.Join(",", list)}], expected [4,1000] (override n*2, Tag=1000) — the flipped virtual container return marshaled wrong or dispatched to the base");
            return $"flipped virtual Boss::Ranks(2) -> natural List<int> [{string.Join(",", list)}] (override n*2): container slot flip + ToManaged materialized";
        });

        // VIRTUAL container PARAM (WRITE direction via the shared VirtualSlotPlanner — the unified param family's
        // virtual half): Entity::Muster (base) and Boss::Muster (override) must
        // flip their PARAM in LOCKSTEP or the vtable slot's signature is inconsistent (the override's param no longer
        // matches the base's). Both dematerialize the incoming natural List at entry via ToIl2CppTyped — the param twin
        // of Ranks (docs/reference/limits.md, CORE 2 / GAPS #2: container returns already flipped virtual; params were non-virtual only).
        suite.Add("param.container.virtual.flip.deployed", () =>
        {
            MethodInfo baseM = FindProxyMethod("Entity", "Muster", out string baseT);
            MethodInfo ovrM = FindProxyMethod("Boss", "Muster", out string bossT);
            string bpt = baseM.GetParameters()[0].ParameterType.FullName ?? "", opt = ovrM.GetParameters()[0].ParameterType.FullName ?? "";
            Check.True(bpt.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal)
                && opt.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal),
                $"virtual Muster param not flipped in lockstep: {baseT}={bpt}, {bossT}={opt}");
            return $"virtual Muster PARAM flipped in lockstep: {baseT} + {bossT} -> System.Collections.Generic.List<int>";
        });

        suite.Add("param.container.virtual.flip.runs", () =>
        {
            MethodInfo ovrM = FindProxyMethod("Boss", "Muster", out string bossT);
            object boss;
            try { boss = Construct(ovrM.DeclaringType!); }
            catch (Exception ex) { Check.Skip($"il2cpp Boss proxy construction not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            var squad = new List<int> { 5, 6, 7 };
            object? result;
            try { result = ovrM.Invoke(boss, new object[] { squad }); }   // natural List -> spliced ToIl2CppTyped -> il2cpp List; body reads Count
            catch (TargetInvocationException tie)
            {
                Check.True(false, $"invoking the flipped Boss::Muster threw {tie.InnerException?.GetType().Name}: " +
                                  $"{tie.InnerException?.Message} — the spliced Il2CppMarshal.ToIl2CppTyped likely did not resolve/dematerialize");
                return null;
            }
            // Boss overrides with Count*2 (Tag=1000), so Muster([5,6,7]) = 3*2 + 1000 = 1006 — the value proves the
            // OVERRIDE dispatched through the flipped virtual slot (the base body is Count+Tag = 3, not 1006) AND the
            // dematerialized il2cpp list arrived with the right length (Count=3, not 0/garbled).
            Check.True(result is int r && r == 1006,
                $"Boss::Muster([5,6,7]) = {result}, expected 1006 (override Count*2 + Tag=1000) — the flipped virtual container PARAM dematerialized wrong or dispatched to the base");
            return $"flipped virtual Boss::Muster([5,6,7]) -> {result} (override Count*2 + Tag): container PARAM slot flip + ToIl2Cpp dematerialized";
        });

        // value-type Nullable RETURN (full-body replace): Game::FindSpawn's return flips from the broken
        // Il2CppSystem.Nullable<Vec3> to a natural System.Nullable<Vec3>, its body ABANDONED for the null-aware SDK
        // invoke. End-to-end through the FLIPPED proxy (the helper cases prove the primitive in isolation): the
        // empty case (FindSpawn(0)) that NRE'd raw now returns null, the present case a real Vec3.
        suite.Add("nullable-return.flip.deployed", () =>
        {
            MethodInfo mi = FindProxyMethod("Game", "FindSpawn", out string typeName);
            string rt = mi.ReturnType.FullName ?? mi.ReturnType.Name;
            Check.True(rt.StartsWith("System.Nullable", StringComparison.Ordinal),
                $"{typeName}::FindSpawn still returns '{rt}' — the value-type Nullable-return pass did not flip it");
            return $"{typeName}::FindSpawn returns natural {rt}";
        });

        suite.Add("nullable-return.flip.runs", () =>
        {
            MethodInfo mi = FindProxyMethod("Game", "FindSpawn", out string typeName);
            object game;
            try { game = Construct(mi.DeclaringType!); }
            catch (Exception ex) { Check.Skip($"il2cpp Game proxy construction not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            object? empty, present;
            try { empty = mi.Invoke(game, new object[] { 0 }); present = mi.Invoke(game, new object[] { 3 }); }
            catch (TargetInvocationException tie)
            {
                Check.True(false, $"invoking the flipped FindSpawn threw {tie.InnerException?.GetType().Name}: " +
                                  $"{tie.InnerException?.Message} — the spliced null-aware invoke did not resolve");
                return null;
            }
            Check.True(empty is null, $"FindSpawn(0) should be an empty Nullable (null), got {empty?.GetType().FullName}");
            Check.True(present is not null, "FindSpawn(3) should be a present Nullable, got null");
            object? x = present!.GetType().GetField("X")?.GetValue(present)     // present boxes to a boxed Vec3
                        ?? present.GetType().GetProperty("X")?.GetValue(present);
            Check.True(x is not null && Math.Abs(Convert.ToSingle(x) - 3f) < 0.001f,
                $"FindSpawn(3).X should be 3, got {x?.ToString() ?? "null"} — the flipped value-type Nullable return marshaled wrong");
            return $"flipped FindSpawn: (0)=null (empty, no NRE), (3)=Vec3(X={x}) (present) — full-body-replace value-type Nullable return";
        });

        // VIRTUAL value-type Nullable RETURN (via the shared VirtualSlotPlanner): Entity::Waypoint (base) and
        // Boss::Waypoint (override) must flip their return in LOCKSTEP or
        // the vtable slot's signature is inconsistent (the override no longer matches the base). Both body-replaced.
        suite.Add("nullable-return.virtual.flip.deployed", () =>
        {
            MethodInfo baseW = FindProxyMethod("Entity", "Waypoint", out string baseT);
            MethodInfo ovrW = FindProxyMethod("Boss", "Waypoint", out string bossT);
            string brt = baseW.ReturnType.FullName ?? "", ort = ovrW.ReturnType.FullName ?? "";
            Check.True(brt.StartsWith("System.Nullable", StringComparison.Ordinal) && ort.StartsWith("System.Nullable", StringComparison.Ordinal),
                $"virtual Waypoint slot not flipped in lockstep: {baseT}={brt}, {bossT}={ort}");
            return $"virtual Waypoint slot flipped in lockstep: {baseT} + {bossT} -> System.Nullable<Vec3>";
        });

        suite.Add("nullable-return.virtual.flip.runs", () =>
        {
            MethodInfo ovrW = FindProxyMethod("Boss", "Waypoint", out string bossT);
            object boss;
            try { boss = Construct(ovrW.DeclaringType!); }
            catch (Exception ex) { Check.Skip($"il2cpp Boss proxy construction not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            object? empty, present;
            try { empty = ovrW.Invoke(boss, new object[] { 0 }); present = ovrW.Invoke(boss, new object[] { 2 }); }
            catch (TargetInvocationException tie)
            {
                Check.True(false, $"invoking the flipped Boss::Waypoint threw {tie.InnerException?.GetType().Name}: " +
                                  $"{tie.InnerException?.Message} — the spliced null-aware invoke did not resolve");
                return null;
            }
            Check.True(empty is null, $"Boss::Waypoint(0) should be an empty Nullable (null), got {empty?.GetType().FullName}");
            Check.True(present is not null, "Boss::Waypoint(2) should be present, got null");
            object? x = present!.GetType().GetField("X")?.GetValue(present)
                        ?? present.GetType().GetProperty("X")?.GetValue(present);
            // Boss overrides with i*2, so Waypoint(2)=Vec3(4,4,4) — X==4 proves the OVERRIDE dispatched (not the base's i).
            Check.True(x is not null && Math.Abs(Convert.ToSingle(x) - 4f) < 0.001f,
                $"Boss::Waypoint(2).X should be 4 (override i*2), got {x?.ToString() ?? "null"} — the flipped virtual value-Nullable return marshaled wrong");
            return $"flipped virtual Boss::Waypoint: (0)=null (empty), (2)=Vec3(X={x}) (present, override i*2) — value-Nullable slot flip";
        });

        // value-type Nullable PARAM (write mirror of the return pass, sharing ParamFlip with the container-param
        // pass): GrantGold(int?) flips to natural System.Nullable<int>. Unlike the broken Nullable RETURN, the WRITE
        // direction is sound — DematerializeNullable builds the il2cpp Nullable<int'> through its ctor.
        suite.Add("nullable-param.flip.deployed", () =>
        {
            MethodInfo gg = FindProxyMethod("Game", "GrantGold", out string typeName);
            string pt = gg.GetParameters()[0].ParameterType.FullName ?? gg.GetParameters()[0].ParameterType.Name;
            Check.True(pt.StartsWith("System.Nullable", StringComparison.Ordinal),
                $"{typeName}::GrantGold param still '{pt}' — the value-type Nullable-param pass did not flip it");
            return $"{typeName}::GrantGold takes natural {pt}";
        });

        suite.Add("nullable-param.flip.runs", () =>
        {
            MethodInfo gg = FindProxyMethod("Game", "GrantGold", out string typeName);
            object game;
            try { game = Construct(gg.DeclaringType!); }
            catch (Exception ex) { Check.Skip($"il2cpp proxy construction of {typeName} not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            // Score is a public int field; Il2CppInterop renders it as a property (fall back to a field just in case).
            PropertyInfo? scoreP = gg.DeclaringType!.GetProperty("Score");
            FieldInfo? scoreF = scoreP is null ? gg.DeclaringType!.GetField("Score") : null;
            Func<int> readScore = () => Convert.ToInt32(scoreP is not null ? scoreP.GetValue(game) : scoreF!.GetValue(game));

            int before = readScore();
            try { gg.Invoke(game, new object?[] { (int?)5 }); }      // present -> Score += 5
            catch (TargetInvocationException tie)
            { Check.True(false, $"GrantGold(int? 5) [PRESENT] threw: {tie.InnerException}"); return null; }
            int mid = readScore();
            try { gg.Invoke(game, new object?[] { (int?)null }); }   // empty -> Score += 0
            catch (TargetInvocationException tie)
            { Check.True(false, $"GrantGold(int? null) [EMPTY] threw: {tie.InnerException}"); return null; }
            int after = readScore();
            Check.True(mid - before == 5, $"PRESENT: GrantGold(5) should +5, got {mid - before} (before={before}, mid={mid})");
            Check.True(after - mid == 0, $"EMPTY: GrantGold(null) should +0, got {after - mid} (mid={mid}, after={after})");
            Check.True(after - before == 5,
                $"GrantGold(5) then GrantGold(null) should raise Score by 5, got {after - before} (before={before}, after={after}) — the ToIl2Cpp Nullable-param write is wrong");
            return $"GrantGold(int? 5) then (int? null): Score {before} -> {after} (+5) — value-type Nullable param dematerialized present AND empty";
        });

        // STATIC Nullable field (Game::Rally). A static il2cpp field has no object and no instance offset — its
        // storage is the class's static-fields block — so the instance write helper cannot serve it; the pass emits
        // WriteNullableStaticField, which rebuilds the box through metadata and stores it with
        // il2cpp_field_static_set_value. Offline proves the IL shape; only a booted game proves the bytes land
        // where the game's own read finds them, present AND empty.
        suite.Add("nullable-accessor.static-field.flip.runs", () =>
        {
            Type gameT = FindProxyType("Game", out string typeName);
            PropertyInfo rally = RequireProperty(gameT, "Rally");
            MethodInfo peek = gameT.GetMethod("PeekRallyX")!;
            string pt = rally.PropertyType.FullName ?? rally.PropertyType.Name;
            Check.True(pt.StartsWith("System.Nullable", StringComparison.Ordinal),
                $"{typeName}::Rally is '{pt}' — the static Nullable field did not flip");

            Type vec3T = Nullable.GetUnderlyingType(rally.PropertyType)!;
            object vec3 = Activator.CreateInstance(vec3T)!;
            SetXyz(vec3T, ref vec3, 5f);
            rally.SetValue(null, vec3);                                   // static write, present
            float x = Convert.ToSingle(peek.Invoke(null, null));
            Check.True(Math.Abs(x - 5f) < 0.001f,
                $"Rally=Vec3(5) then PeekRallyX()={x}, expected 5 — the static Nullable write did not reach the static slot");
            Check.True(rally.GetValue(null) is not null, "get_Rally returned null for a PRESENT static Nullable");

            rally.SetValue(null, null);                                   // static write, empty
            float none = Convert.ToSingle(peek.Invoke(null, null));
            Check.True(Math.Abs(none - (-1f)) < 0.001f,
                $"Rally=null then PeekRallyX()={none}, expected -1 — the EMPTY static write did not clear hasValue");
            Check.True(rally.GetValue(null) is null, "get_Rally should be null after an empty static write");
            return $"static Game::Rally round-trip: present(X=5) and empty(-1) through il2cpp_field_static_set_value";
        });

        // VIRTUAL Nullable accessors (Entity/Boss::Beacon, ::Cache). get_X and set_X are separate vtable slots, so
        // the property, its getter and its setter must all flip across the whole override graph or the property is
        // a half-flip that LOADS and only misbehaves when touched. Boss's overrides deliberately store a DIFFERENT
        // value than they are handed (X+1, Gold+1), so reading the value back proves the call dispatched through the
        // OVERRIDE's flipped accessor rather than the base's — which a signature check alone cannot show.
        suite.Add("nullable-accessor.virtual.flip.runs", () =>
        {
            // The type by NAME, never `FindProxyMethod(...).DeclaringType`: PeekBeaconX is declared on Entity and
            // merely inherited by Boss, so DeclaringType would hand back Entity — and the case would then construct
            // an Entity and exercise the BASE accessors while claiming to test the override.
            Type bossT = FindProxyType("Boss", out string typeName);
            object boss;
            try { boss = Construct(bossT); }
            catch (Exception ex) { Check.Skip($"il2cpp proxy construction of {typeName} not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            PropertyInfo beacon = bossT.GetProperty("Beacon")!, cache = bossT.GetProperty("Cache")!;
            Check.True(!(beacon.PropertyType.FullName ?? "").Contains("Il2CppSystem.Nullable", StringComparison.Ordinal)
                       && !(cache.PropertyType.FullName ?? "").Contains("Il2CppSystem.Nullable", StringComparison.Ordinal),
                $"virtual accessors did not flip: Beacon={beacon.PropertyType.FullName}, Cache={cache.PropertyType.FullName}");

            // VALUE rung through the override: hand it 7, the override stores 8.
            Type vec3T = Nullable.GetUnderlyingType(beacon.PropertyType)!;
            object vec3 = Activator.CreateInstance(vec3T)!;
            SetXyz(vec3T, ref vec3, 7f);
            beacon.SetValue(boss, vec3);
            float x = Convert.ToSingle(bossT.GetMethod("PeekBeaconX")!.Invoke(boss, null));
            object? viaGetter = beacon.GetValue(boss);
            float gx = viaGetter is null ? -99f
                : Convert.ToSingle(vec3T.GetField("X")?.GetValue(viaGetter) ?? vec3T.GetProperty("X")!.GetValue(viaGetter));
            string where = $"(setter declared on {beacon.SetMethod?.DeclaringType?.Name}, instance is {boss.GetType().Name})";
            Check.True(Math.Abs(x - 8f) < 0.001f,
                $"Beacon=Vec3(7): PeekBeaconX()={x}, get_Beacon().X={gx}, expected 8 — the write did not go through Boss's override {where}");

            // REF-BEARING rung through the override: hand it 100, the override stores 101 — and the embedded string
            // must survive the box rebuild on the way in.
            object player = ConstructPlayer(FindProxyMethod("Player", "MakeLoadout", out _).DeclaringType!);
            object loadout = player.GetType().GetMethod("MakeLoadout")!.Invoke(player, new object?[] { 100 })!;
            cache.SetValue(boss, loadout);
            int gold = Convert.ToInt32(bossT.GetMethod("PeekCacheGold")!.Invoke(boss, null));
            Check.True(gold == 101,
                $"Cache=Loadout(100) then PeekCacheGold()={gold}, expected 101 (override stores Gold+1) — ref-bearing write did not reach the override");

            // Empty on both rungs: null must clear, not throw.
            beacon.SetValue(boss, null);
            cache.SetValue(boss, null);
            Check.True(Math.Abs(Convert.ToSingle(bossT.GetMethod("PeekBeaconX")!.Invoke(boss, null)) - (-1f)) < 0.001f
                       && Convert.ToInt32(bossT.GetMethod("PeekCacheGold")!.Invoke(boss, null)) == -1,
                "writing null through the virtual accessors did not clear both Nullables");
            return $"virtual accessor lockstep: Beacon 7->{x} (override +1), Cache 100->{gold} (override +1), both clear on null";
        });

        // STATIC container field (Game::Squad). A static setter's value is ldarg.0 — the slot that holds `this` on an
        // instance method — so the pre-check's literal ldarg.1 never matched and every static container property
        // deferred (529 in one real game). Offline proves it flips; only a booted game proves the WRITE lands, since
        // the static path stores through il2cpp_field_static_set_value rather than the instance field write.
        suite.Add("container.static-field.flip.runs", () =>
        {
            MethodInfo peek = FindProxyMethod("Game", "PeekSquadN", out string typeName);
            Type gameT = peek.DeclaringType!;
            PropertyInfo squad = gameT.GetProperty("Squad")
                ?? throw new AssertException($"{typeName}::Squad not found — the static container fixture is missing");
            string pt = squad.PropertyType.FullName ?? squad.PropertyType.Name;
            Check.True(pt.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal),
                $"{typeName}::Squad is '{pt}' — the static container property did not flip");

            var written = new List<int> { 4, 5, 6, 7 };
            squad.SetValue(null, written);                                   // static write: no instance
            int n = Convert.ToInt32(peek.Invoke(null, null));                // the GAME's own read of the same storage
            Check.True(n == written.Count,
                $"Squad = List[{written.Count}] then PeekSquadN() = {n} — the static field write dematerialized wrong");
            object? read = squad.GetValue(null);
            Check.True(read is List<int> back && back.Count == written.Count,
                $"get_Squad returned {read?.GetType().FullName ?? "null"} — the static getter did not materialize a natural List");
            return $"static Game::Squad round-trip: wrote List[{written.Count}], game read {n} back (natural {pt})";
        });

        // Nullable AUTO-PROPERTY (METHOD-backed accessors): Player::Waypoint (value element) and Player::Backpack
        // (ref-bearing element). An auto-property yields TWO proxy members over one storage — a FIELD-backed pair
        // over <X>k__BackingField and a METHOD-backed pair for the property itself (get_/set_ dispatch through
        // il2cpp_runtime_invoke). Only the first was ever flippable; the second deferred as "accessor not
        // handleable", because the pre-check demanded a NativeFieldInfoPtr a real property's setter never has. These
        // cases pin the METHOD-backed pair: the getter via the shared tail-swap, the setter via the ordinary param
        // flip (ToIl2CppTyped for the value rung, ValueTypeBridge.RefToNullable for the ref-bearing one).
        suite.Add("nullable-accessor.flip.deployed", () =>
        {
            Type playerT = FindProxyMethod("Player", "PeekWaypointX", out string typeName).DeclaringType!;
            PropertyInfo wp = RequireProperty(playerT, "Waypoint"), bp = RequireProperty(playerT, "Backpack");
            string wpT = wp.PropertyType.FullName ?? wp.PropertyType.Name;
            string bpT = bp.PropertyType.FullName ?? bp.PropertyType.Name;

            // The value rung goes to System.Nullable<Vec3>; the ref-bearing rung CANNOT (System.Nullable<class> is
            // illegal) and goes to the BARE proxy used as a nullable reference — so asserting "not il2cpp Nullable"
            // is the honest shared check, plus each rung's own target.
            Check.True(wpT.StartsWith("System.Nullable", StringComparison.Ordinal),
                $"{typeName}::Waypoint is '{wpT}' — the method-backed Nullable accessor did not flip to System.Nullable");
            Check.True(!bpT.Contains("Nullable", StringComparison.Ordinal) && bpT.EndsWith("Loadout", StringComparison.Ordinal),
                $"{typeName}::Backpack is '{bpT}' — the ref-bearing method-backed accessor should flip to the bare Loadout proxy");
            // get/set must agree with the property, or the flip was a half-flip that still loads.
            Check.True(wp.GetMethod!.ReturnType == wp.PropertyType && wp.SetMethod!.GetParameters()[0].ParameterType == wp.PropertyType,
                $"{typeName}::Waypoint accessors disagree with the property type ({wpT})");
            Check.True(bp.GetMethod!.ReturnType == bp.PropertyType && bp.SetMethod!.GetParameters()[0].ParameterType == bp.PropertyType,
                $"{typeName}::Backpack accessors disagree with the property type ({bpT})");
            return $"{typeName}: Waypoint -> {wpT}, Backpack -> {bpT} (method-backed accessors, get/set in lockstep)";
        });

        suite.Add("nullable-accessor.flip.runs", () =>
        {
            MethodInfo peekX = FindProxyMethod("Player", "PeekWaypointX", out string typeName);
            Type playerT = peekX.DeclaringType!;
            object player;
            try { player = ConstructPlayer(playerT); }
            catch (Exception ex) { Check.Skip($"il2cpp proxy construction of {typeName} not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            MethodInfo peekGold = playerT.GetMethod("PeekBackpackGold")!, peekOwner = playerT.GetMethod("PeekBackpackOwner")!;
            PropertyInfo wp = RequireProperty(playerT, "Waypoint"), bp = RequireProperty(playerT, "Backpack");

            // VALUE rung. Write a natural Nullable<Vec3> through the flipped setter; the GAME reads the same storage
            // back (PeekWaypointX), so a mis-dematerialized write shows as a wrong X rather than a silent pass.
            Type vec3T = Nullable.GetUnderlyingType(wp.PropertyType)!;
            object vec3 = Activator.CreateInstance(vec3T)!;
            SetXyz(vec3T, ref vec3, 7f);
            wp.SetValue(player, vec3);                                   // present
            float x = Convert.ToSingle(peekX.Invoke(player, null));
            Check.True(Math.Abs(x - 7f) < 0.001f, $"Waypoint=Vec3(7,..) then PeekWaypointX()={x}, expected 7 — the spliced Nullable write is wrong");
            object? readBack = wp.GetValue(player);
            Check.True(readBack is not null, "get_Waypoint returned null for a PRESENT value — the getter tail-swap read the box wrong");

            wp.SetValue(player, null);                                   // empty
            float none = Convert.ToSingle(peekX.Invoke(player, null));
            Check.True(Math.Abs(none - (-1f)) < 0.001f, $"Waypoint=null then PeekWaypointX()={none}, expected -1 (HasValue=false) — the EMPTY write did not clear hasValue");
            Check.True(wp.GetValue(player) is null, "get_Waypoint should be null after writing an empty Nullable");

            // REF-BEARING rung — the one with no hand-writable natural spelling. The Loadout comes from the game's
            // own factory (no il2cpp construction guesswork), and Owner is a real string reference INSIDE the value
            // type: reading it back intact is what proves RefToNullable's GC-aware copy, not a raw byte blit.
            object loadout = playerT.GetMethod("MakeLoadout")!.Invoke(player, new object?[] { 999 })!;
            bp.SetValue(player, loadout);                                // present
            int gold = Convert.ToInt32(peekGold.Invoke(player, null));
            string? owner = (string?)peekOwner.Invoke(player, null);
            Check.True(gold == 999, $"Backpack=Loadout(999) then PeekBackpackGold()={gold}, expected 999 — RefToNullable wrote the wrong inner value");
            Check.True(!string.IsNullOrEmpty(owner),
                $"PeekBackpackOwner()='{owner ?? "null"}' — the embedded string reference did not survive the Nullable box (GC-aware copy failed)");
            Check.True(bp.GetValue(player) is not null, "get_Backpack returned null for a PRESENT ref-bearing value");

            bp.SetValue(player, null);                                   // empty == a null reference
            int noGold = Convert.ToInt32(peekGold.Invoke(player, null));
            Check.True(noGold == -1, $"Backpack=null then PeekBackpackGold()={noGold}, expected -1 (empty Nullable)");
            Check.True(bp.GetValue(player) is null, "get_Backpack should be null after writing an empty (null) ref-bearing Nullable");

            return $"method-backed accessors round-trip: Waypoint present(X=7)/empty(-1), Backpack present(Gold=999, Owner='{owner}')/empty(-1)";
        });

        // PARAM direction (ToIl2Cpp): SetInventory(List<string>) — the param flips to natural, and invoking it with
        // a natural List dematerializes to an il2cpp List the real method consumes (Score += items.Count).
        suite.Add("container.list-param.flip.deployed", () =>
        {
            MethodInfo si = FindProxyMethod("Game", "SetInventory", out string typeName);
            ParameterInfo p = si.GetParameters()[0];
            string pt = p.ParameterType.FullName ?? p.ParameterType.Name;
            Check.True(pt.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal),
                $"{typeName}::SetInventory param still '{pt}' — the container-param pass did not flip it");
            return $"{typeName}::SetInventory takes natural {pt}";
        });

        suite.Add("container.list-param.flip.runs", () =>
        {
            MethodInfo si = FindProxyMethod("Game", "SetInventory", out string typeName);
            object game;
            try { game = Construct(si.DeclaringType!); }
            catch (Exception ex) { Check.Skip($"il2cpp proxy construction of {typeName} not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            PropertyInfo? score = game.GetType().GetProperty("Score");
            int before = score is not null ? (int)score.GetValue(game)! : 0;
            var items = new List<string> { "a", "b", "c" };
            try { si.Invoke(game, new object[] { items }); }   // natural List -> spliced ToIl2CppTyped -> il2cpp List
            catch (TargetInvocationException tie)
            {
                Check.True(false, $"invoking the flipped SetInventory threw {tie.InnerException?.GetType().Name}: " +
                                  $"{tie.InnerException?.Message} — the spliced Il2CppMarshal.ToIl2CppTyped likely did not resolve");
                return null;
            }

            // Score += items.Count inside the game proves the il2cpp list arrived with the RIGHT length — i.e.
            // ToIl2Cpp dematerialized all elements, not an empty/garbled list.
            if (score is null) return $"SetInventory(List<string> [{items.Count}]) invoked without throwing (Score not reflectable): ToIl2Cpp resolved";
            int after = (int)score.GetValue(game)!;
            Check.True(after - before == items.Count,
                $"Score delta {after - before}, expected {items.Count} (the dematerialized list's Count) — ToIl2Cpp built a wrong-length il2cpp list");
            return $"SetInventory(natural List<string> [{items.Count}]) -> Score +={after - before}: ToIl2Cpp dematerialized correctly";
        });

        // Set PARAM: CountTags(HashSet<int>) flips to natural; invoking with a real HashSet dematerializes
        // to an il2cpp HashSet the game counts. A wrong count = the ToIl2Cpp Set write built a wrong/garbled set.
        suite.Add("container.set-param.flip.deployed", () =>
        {
            MethodInfo ct = FindProxyMethod("Game", "CountTags", out string typeName);
            ParameterInfo p = ct.GetParameters()[0];
            string pt = p.ParameterType.FullName ?? p.ParameterType.Name;
            Check.True(pt.StartsWith("System.Collections.Generic.HashSet", StringComparison.Ordinal),
                $"{typeName}::CountTags param still '{pt}' — the container-param pass did not flip the Set");
            return $"{typeName}::CountTags takes natural {pt}";
        });

        suite.Add("container.set-param.flip.runs", () =>
        {
            MethodInfo ct = FindProxyMethod("Game", "CountTags", out string typeName);
            object game;
            try { game = Construct(ct.DeclaringType!); }
            catch (Exception ex) { Check.Skip($"il2cpp proxy construction of {typeName} not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            var tags = new HashSet<int> { 5, 6, 7, 8 };
            object? ret;
            try { ret = ct.Invoke(game, new object[] { tags }); }   // natural HashSet -> spliced ToIl2CppTyped -> il2cpp HashSet
            catch (TargetInvocationException tie)
            {
                Check.True(false, $"invoking the flipped CountTags threw {tie.InnerException?.GetType().Name}: " +
                                  $"{tie.InnerException?.Message} — the spliced Il2CppMarshal.ToIl2CppTyped likely did not resolve the Set");
                return null;
            }
            Check.True(ret is int c && c == tags.Count,
                $"CountTags returned {ret}, expected {tags.Count} — the ToIl2Cpp Set write built a wrong-size il2cpp HashSet");
            return $"CountTags(natural HashSet<int> [{tags.Count}]) -> {ret}: ToIl2Cpp dematerialized the Set correctly";
        });

        // delegate PARAM (BclToIl2Cpp) — the docs/reference/limits.md, CORE-1 capability restored: Game::ForEachScore(Action<int>) flips
        // from the wrapper Il2CppSystem.Action<int> to the natural System.Action<int>, so a BARE LAMBDA binds. The proxy
        // body splices the wrapper's OWN op_Implicit at entry (wraps the managed delegate as the il2cpp Action) and the
        // game invokes it (cb(Score)) — reverse-pinvoking back into the managed lambda. Reflection-only like the rest.
        suite.Add("delegate.action-param.flip.deployed", () =>
        {
            MethodInfo fe = FindProxyMethod("Game", "ForEachScore", out string typeName);
            ParameterInfo p = fe.GetParameters()[0];
            string pt = p.ParameterType.FullName ?? p.ParameterType.Name;
            Check.True(pt.StartsWith("System.Action", StringComparison.Ordinal),
                $"{typeName}::ForEachScore param still '{pt}' — the delegate-param pass did not flip it");
            return $"{typeName}::ForEachScore takes natural {pt}";
        });

        suite.Add("delegate.action-param.flip.runs", () =>
        {
            MethodInfo fe = FindProxyMethod("Game", "ForEachScore", out string typeName);
            object game;
            try { game = Construct(fe.DeclaringType!); }
            catch (Exception ex) { Check.Skip($"il2cpp proxy construction of {typeName} not reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

            PropertyInfo? scoreP = game.GetType().GetProperty("Score");
            int score = scoreP is not null ? Convert.ToInt32(scoreP.GetValue(game)) : 0;

            // A bare System.Action<int> binds ONLY because the flip made the param natural — an Il2CppSystem.Action<int>
            // param would reject it (ArgumentException). The spliced op_Implicit wraps cb as the il2cpp delegate; the
            // body's cb(Score) invokes it, which reverse-pinvokes back into THIS lambda.
            var captured = new List<int>();
            Action<int> cb = x => captured.Add(x);
            try { fe.Invoke(game, new object[] { cb }); }
            catch (TargetInvocationException tie)
            {
                Check.True(false, $"invoking the flipped ForEachScore threw {tie.InnerException?.GetType().Name}: " +
                                  $"{tie.InnerException?.Message} — the spliced op_Implicit likely did not resolve/bind");
                return null;
            }
            Check.True(captured.Count == 1 && captured[0] == score,
                $"ForEachScore(cb) invoked the managed lambda {captured.Count}x with [{string.Join(",", captured)}], expected 1x [{score}] — the delegate did not round-trip through op_Implicit");
            return $"ForEachScore(bare System.Action<int>) -> game called cb({score}): managed lambda fired {captured.Count}x, captured [{string.Join(",", captured)}] (op_Implicit round-trip)";
        });
    }

    // Find a loaded proxy TYPE by simple name. Distinct from FindProxyMethod(...).DeclaringType, which resolves to
    // wherever the METHOD was declared — a base type for anything inherited, which silently retargets a case meant
    // for the derived type.
    static Type FindProxyType(string typeSimpleName, out string typeName)
    {
        foreach (Assembly asm in CandidateAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
            catch { continue; }

            Type? t = types.FirstOrDefault(x => x.Name == typeSimpleName);
            if (t is not null) { typeName = t.FullName ?? t.Name; return t; }
        }
        throw new AssertException($"{typeSimpleName} proxy type not found in any loaded assembly");
    }

    // The property a case names must EXIST — a rename/removal in the fixture is a loud failure, never a silent skip.
    static PropertyInfo RequireProperty(Type proxyType, string name)
        => proxyType.GetProperty(name)
           ?? throw new AssertException($"{proxyType.FullName}::{name} property not found on the proxy — the fixture member is missing or renamed");

    // ToyGame.Player has no parameterless ctor (Player(string name)), so Construct's path does not serve it.
    static object ConstructPlayer(Type proxyType)
    {
        ConstructorInfo? ctor = proxyType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(string));
        if (ctor is null) throw new MissingMethodException($"{proxyType.FullName} has no (string) ctor");
        return ctor.Invoke(new object?[] { "battery" });
    }

    // Set X/Y/Z on a Vec3 value proxy (fields on the il2cpp struct proxy; property fallback for a rendering that
    // surfaces them as properties). Boxed-by-ref so the mutation lands on the box the caller then writes.
    static void SetXyz(Type vec3T, ref object boxed, float v)
    {
        foreach (string n in new[] { "X", "Y", "Z" })
        {
            if (vec3T.GetField(n) is { } f) f.SetValue(boxed, v);
            else vec3T.GetProperty(n)?.SetValue(boxed, v);
        }
    }

    // A "<method> return type flipped to natural" case (no game instance needed — pure reflection over the proxy).
    static void AddDeployed(Suite suite, string id, string method, string naturalPrefix) => suite.Add(id, () =>
    {
        MethodInfo mi = FindProxyMethod("Game", method, out string typeName);
        string rt = mi.ReturnType.FullName ?? mi.ReturnType.Name;
        Check.True(rt.StartsWith(naturalPrefix, StringComparison.Ordinal),
            $"{typeName}::{method} still returns '{rt}' — the container pass did not flip it");
        return $"{typeName}::{method} returns natural {rt}";
    });

    // A "invoking <method> yields the natural value ToManaged materialized" case. Game construction is version-
    // sensitive -> SKIP; the invoke itself is NOT guarded (a spliced ToManaged that did not resolve fails LOUD).
    static void AddRuns(Suite suite, string id, string method, Func<object, bool> ok, Func<object, string> detail) => suite.Add(id, () =>
    {
        MethodInfo mi = FindProxyMethod("Game", method, out string typeName);
        object game;
        try { game = Construct(mi.DeclaringType!); }
        catch (Exception ex) { Check.Skip($"il2cpp proxy construction of {typeName} not reflectively reachable: {ex.GetType().Name}: {ex.Message}"); return null; }

        object? result;
        try { result = mi.Invoke(game, null); }
        catch (TargetInvocationException tie)
        {
            Check.True(false, $"invoking the flipped {method} threw {tie.InnerException?.GetType().Name}: " +
                              $"{tie.InnerException?.Message} — the spliced Il2CppMarshal.ToManaged likely did not resolve");
            return null;
        }

        Check.True(result is not null && ok(result),
            $"{method} returned {result?.GetType().FullName ?? "null"}, not the expected natural BCL type");
        return $"invoked {method} -> {detail(result!)}: ToManaged resolved and materialized";
    });

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
        Assembly? acs = null;
        try { acs = Assembly.Load(new AssemblyName("Assembly-CSharp")); } catch { /* fall through to the domain scan */ }
        if (acs is not null) yield return acs;
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) yield return a;
    }

    // Best-effort reflective construction of a plain (non-generic) il2cpp proxy via its parameterless ctor. Any
    // failure is a SKIP at the caller (Il2CppInterop construction is version-sensitive).
    static object Construct(Type proxyType)
    {
        ConstructorInfo? ctor = proxyType.GetConstructor(Type.EmptyTypes);
        if (ctor is not null) return ctor.Invoke(null);
        return Activator.CreateInstance(proxyType)
            ?? throw new MissingMethodException(proxyType.FullName, ".ctor()");
    }
}
