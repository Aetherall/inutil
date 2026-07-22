// Roots every ToyGame member so IL2CPP's managed stripping keeps them in GameAssembly.dll, and runs them in a loop
// so the methods are live call targets the interceptor can hook at runtime; logs results so hook effects are
// observable in the player log.
using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using ToyGame;

public class Bootstrap : MonoBehaviour
{
    Player _p;
    Game _g;
    int _tick;

    void Start()
    {
        StartWatchdog();
        _p = Player.Create("Toymaker");
        _g = new Game();
        Debug.Log("[ToyGame] Bootstrap.Start — inutil fixture alive");
        Exercise();
    }

    // Self-destruct timer: under Proton/wine the player window can wedge (a guest crash leaves wine hung), so a
    // validation launch must never hang a CI/agent forever. A background thread force-exits after N seconds —
    // default 3, override via INUTIL_WATCHDOG_SECONDS (<=0 disables, e.g. for interactive play).
    static void StartWatchdog()
    {
        int secs = 3;
        string env = System.Environment.GetEnvironmentVariable("INUTIL_WATCHDOG_SECONDS");
        if (!string.IsNullOrEmpty(env) && int.TryParse(env, out int v)) secs = v;
        if (secs <= 0) { Debug.Log("[ToyGame] watchdog disabled"); return; }
        Debug.Log($"[ToyGame] watchdog armed: force-exit after {secs}s");
        var t = new Thread(() =>
        {
            Thread.Sleep(secs * 1000);
            Debug.Log($"[ToyGame] watchdog: {secs}s elapsed — force-exiting");
            System.Environment.Exit(0);
        }) { IsBackground = true, Name = "inutil-watchdog" };
        t.Start();
    }

    // Called every second so each method is a live, repeatedly-invoked target.
    void Update()
    {
        if (Time.frameCount % 60 != 0) return;
        _tick++;
        Exercise();
    }

    void Exercise()
    {
        _p.TakeDamage(10f);
        _p.Move(new Vec2(1, 2));
        _p.Teleport(new Vec3(3, 4, 5));
        var pos = _p.GetPosition();
        _p.TryConsume(5, out int rem);
        _p.Configure(1, 2, 3, 4, 5, 6);
        var lo = _p.MakeLoadout(999);

        _g.AddScore(10);
        _g.AddScore(10, 1.5f);
        var ib = _g.MakeIntBox(7);
        var pb = _g.MakePlayerBox(_p);
        var sb = _g.MakeStrBox("hello");
        // §7.7 ABI-completeness: root the wide generic's two ref-type __Canon instantiations (Player, Boss) via
        // direct call sites so IL2CPP compiles the shared body + both RGCTX inflations the spill-routing hook binds.
        Player ewp = _g.EchoWidePlayer(_p); Boss ewb = _g.EchoWideBoss(new Boss());
        // §7.5 ValueTypeBridge: root the ref-bearing-VT-in-container params so IL2CPP keeps them (the interop-patch
        // flips the (int,Loadout) tuple + Dictionary<int,Loadout> params to natural; the ToIl2Cpp write must route
        // the Loadout element through ValueTypeBridge or the game reads a garbled Owner/Gold).
        string vd = _g.DescribeStat((7, _p.MakeLoadout(5)));
        var vbook = new System.Collections.Generic.Dictionary<int, Loadout> { [1] = _p.MakeLoadout(3), [2] = _p.MakeLoadout(4) };
        int vsum = _g.SumBook(vbook);
        // §7.5 list-ELEMENT ref-bearing VT (C5 List<Loadout>): root SumLoadouts with a natural List<Loadout> so the
        // patch flips its param and the DematerializeList value-type write (Add via ValueTypeBridge) is a live target.
        var vgear = new System.Collections.Generic.List<Loadout> { _p.MakeLoadout(5), _p.MakeLoadout(3) };
        int vlsum = _g.SumLoadouts(vgear);

        // realism shapes: interface dispatch, ref-of-struct, double, same-arity overloads, ref-bearing-VT-by-ref +
        // static codec, explicit interface impl, virtual override. Rooted here so each is a live target for the interceptor.
        IDamageable dmg = _p; dmg.Damage(1);
        Vec3 gv = new Vec3(1, 1, 1); _p.Grow(ref gv);
        double scaled = _p.Scale(1.5);
        Loadout rl = _p.MakeLoadout(7); _p.Reforge(ref rl);
        Loadout pe = _p.MakeLoadout(2), ce = _p.MakeLoadout(9); Player.Encode(ref ce, pe);
        int cmb = _g.Combine(3) + _g.Combine(3.5f);
        ((ITicker)_g).Tick();
        Entity ent = new Boss(); int ev = ent.Evaluate(7);
        // Rung-A fixture: virtual Nullable<T> return, rooted on BOTH the override (via vtable dispatch) and the
        // base body (direct Entity instance) so IL2CPP keeps both methodPointers the lockstep-flip patch targets.
        Vec3? wpBoss = ent.Waypoint(_tick); Vec3? wpBase = new Entity().Waypoint(_tick);
        // §7.6 virtual container return: root Boss::Ranks (override, via vtable dispatch through `ent`) AND Entity::Ranks
        // (base body, direct Entity instance) so IL2CPP keeps both methodPointers the lockstep container-flip patch targets.
        List<int> rkBoss = ent.Ranks(_tick); List<int> rkBase = new Entity().Ranks(_tick);
        // §7.6 virtual container PARAM (the WRITE twin of Ranks): root Boss::Muster (override, vtable dispatch through
        // `ent`) AND Entity::Muster (base body) so IL2CPP keeps both methodPointers the lockstep param-flip targets.
        int msBoss = ent.Muster(new List<int> { 1, 2, _tick }); int msBase = new Entity().Muster(new List<int> { _tick });

        // type-friction surface — root the conversion-case members (Nullable / List / Dictionary / array /
        // delegate) so IL2CPP keeps their proxies and they're live targets for the TypeFrictionProbe.
        Vec3? spawn = _g.FindSpawn(_tick); Faction? side = _g.PreferredSide(_tick % 2 == 0); _g.GrantGold(5);
        List<int> hi = _g.GetHighScores(); _g.SetInventory(new List<string> { "x", "y" });
        Dictionary<string, int> tallies = _g.GetTallies();
        // §step-2 HashSet<T> new-family experiment: root the Set RETURN + Set PARAM so both proxies are kept and flip.
        HashSet<int> tagset = _g.GetTagSet(); int tagcount = _g.CountTags(new HashSet<int> { 1, 2, 3 });
        int[] combo = _g.GetCombo(); string[] names = _g.GetNames(); _g.ForEachScore(_ => { });
        (string, string) metrics = _g.GetMetrics();   // ValueTuple<string,string> RETURN (interop-patch flips it)
        System.Threading.Tasks.Task commit = _g.Commit(); System.Threading.Tasks.Task suppress = _g.Suppress();   // Task RETURN (interop-patch flips it)
        // CROSS-MODULE virtual Task<T> return: root the Backend<T> override and — via a BackendBase<int> reference —
        // its cross-module base slot in ToyGame.Core.dll, so IL2CPP keeps both (the shape whose slot roots to another
        // proxy module, which the cross-module slot fix must flip).
        var backend = new Backend<int>(_tick);
        System.Threading.Tasks.Task<Session> sess = backend.OpenSession();
        BackendBase<int> bb = backend; System.Threading.Tasks.Task<Session> sess2 = bb.OpenSession();
        // generic-method Task<T> return, T only in the return: root LoadTyped<Player> + the RoundTripTyped read-back
        // so IL2CPP keeps the __Canon instantiation the flipped-Task generic-method hook binds to.
        System.Threading.Tasks.Task<Player> typed = _g.LoadTyped<Player>(); string rtt = _g.RoundTripTyped();
        // nested BCL result: root Task<Player[]> + Task<Player[][]> + their read-backs so IL2CPP keeps
        // FetchSquad/FetchFormations (the shapes whose Task result-array a recursive hook fabricates natural).
        string rts = _g.RoundTripSquad(); string rtf = _g.RoundTripFormations();
        // recursive marshalling phase 2/3 + Tier 2: root the nested-container-in-Task fixtures (IReadOnlyList / tuple / nullable)
        // and the generic Tier-2 shape so IL2CPP keeps FetchRoster/FetchPair/FetchMaybe/FetchStats + FetchTyped<Player[]>.
        string rtr = _g.RoundTripFetchRoster(); string rtp = _g.RoundTripPair(); string rtm = _g.RoundTripMaybe();
        string rtst = _g.RoundTripStats(); string rtts = _g.RoundTripTypedSquad();
        Debug.Log($"[ToyGame] recursive2: roster={rtr} pair={rtp} maybe={rtm} stats={rtst} typedsquad={rtts}");
        // recursive marshalling phase B (struct/string arrays in a Task) + A (standalone top-level nested container param/return):
        // root the read-backs so IL2CPP keeps FetchScores/FetchTags + SumGroups/SumJagged/SumSquads/Squads/SplitSquad live.
        string rsc = _g.RoundTripScores(); string rtg = _g.RoundTripTags();
        int rgp = _g.RoundTripGroups(); int rjg = _g.RoundTripJagged(); int rss = _g.RoundTripSumSquads();
        string rsq = _g.RoundTripSquads(); string rsp = _g.RoundTripSplit();
        Debug.Log($"[ToyGame] recursiveBA: scores={rsc} tags={rtg} groups={rgp} jagged={rjg} sumsquads={rss} squads={rsq} split={rsp}");
        // §item-2 hook Task-fabricate (RoundTripNumber reads FetchNumber().Result game-side) + §item-1b pending carrier
        // (SlowValue is a genuinely-pending Task<int>): root both so IL2CPP keeps them as live hook/flip targets.
        int rn = _g.RoundTripNumber(); System.Threading.Tasks.Task<int> slow = _g.SlowValue();
        Debug.Log($"[ToyGame] taskfab: rn={rn} slow={(slow != null ? "task" : "n")}");

        Debug.Log($"[ToyGame] friction: spawn={(spawn.HasValue ? "y" : "n")} side={side} hi={hi.Count} " +
                  $"tallies={tallies.Count} set={tagset.Count}/{tagcount} combo={combo.Length} names={names.Length} metrics={metrics.Item1} " +
                  $"task={(commit != null && suppress != null)} sess={(sess != null && sess2 != null ? sess2.Result.Name : "n")} typed={(typed != null)}/{rtt} squad={rts}form={rtf} " +
                  $"wp={(wpBoss.HasValue ? wpBoss.Value.X : -1)}/{(wpBase.HasValue ? wpBase.Value.X : -1)} rk={rkBoss[0]}/{rkBase[0]} ms={msBoss}/{msBase} " +
                  $"echowide={(ewp != null ? ewp.Name : "n")}/{ewb.Tag} valuetype={vd}/{vsum}/{vlsum}");

        // metadata-pillar fixture (../../../../docs/contribution/architecture/16-metadata.md): root the attributed WireProfile so IL2CPP keeps it and
        // its custom [WireName]/[WireConverter] metadata (which inutil's Cpp2IL pass recovers and the proxy strips).
        var wp = new WireProfile { Handle = "Toymaker", Side = Faction.Bear, Gold = _tick };
        Debug.Log($"[ToyGame] wire: {wp.Describe()}");

        // by-name field-access fixture (Inutil.Fields): keep the static / Nullable / ref-bearing-VT / settable-ref
        // fields + their read-back peeks live so IL2CPP retains them with real methodPointers the demo verifies against.
        Game.HighScore++; _p.Rival = null; _p.Stash = null;
        Debug.Log($"[ToyGame] fields: hs={_g.PeekHighScore()} bonus={_g.PeekBonus()} " +
                  $"gear={_p.PeekGearGold()}/{_p.PeekGearOwner()} stash={_p.PeekStashGold()}/{_p.PeekStashOwner()} rival={_p.PeekRivalName()}");

        Debug.Log($"[ToyGame] tick={_tick} hp={_p.GetHealth()} pos=({pos.X},{pos.Y},{pos.Z}) " +
                  $"rem={rem} loadout={lo.Gold}/{lo.Owner} greet=\"{_p.Greet("world")}\" " +
                  $"echo={_g.Echo(42)} score={_g.Score} boxes={ib.Get()}/{pb.Get().Name}/{sb.Get()} " +
                  $"dmg.shield={dmg.Shield} grow=({gv.X},{gv.Y},{gv.Z}) scale={scaled} " +
                  $"reforge={rl.Gold}/{rl.Owner} enc={ce.Gold} cmb={cmb} ev={ev}");
    }
}
