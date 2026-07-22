// ToyGame — a deliberately nasty-but-small type set, built to IL2CPP (win-x64) so the inutil interceptor/marshaller
// can be validated against a REAL il2cpp runtime instead of the mock. Every member targets a specific marshalling
// case (see comments).
//
// NOTE: methods are intentionally left to IL2CPP's natural inlining (NO blanket NoInlining). The trivial bodies
// (GetHealth, TakeDamage, Get/Set) WILL be inlined in Release — and hooking *through* that inlining, via the runtime
// metadata markers IL2CPP preserves at the inlined site (the logical method's MethodInfo* survives even when the body
// is spliced into a caller), is the whole reason inutil is a runtime interceptor, not a static-rewrite tool. So this
// fixture must KEEP its inlined methods.

using System;
using System.Collections.Generic;

namespace ToyGame
{
    // --- value types: the register-vs-pointer-vs-sret split ---
    public struct Vec2 { public float X, Y; public Vec2(float x, float y) { X = x; Y = y; } }            // 8B  -> by value in a GPR
    public struct Vec3 { public float X, Y, Z; public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; } } // 12B -> by hidden pointer
    public struct Loadout { public int Gold; public string Owner; }                                      // ref-bearing VT -> GC write-barrier case

    public enum Faction { Usec, Bear, Scav }

    // --- interfaces: vtable/interface dispatch + the explicit-impl addressing case ---
    // Hooking an interface method exercises the virtualMethodPointer detour path. An EXPLICIT impl (Game.ITicker.Tick)
    // is the shape Il2CppInterop renders specially and the expression API can't cleanly name — a fixture for
    // metadata-driven addressing.
    public interface IDamageable { void Damage(int amount); int Shield { get; } }
    public interface ITicker { void Tick(); }

    // Virtual base + override: a call through an Entity reference dispatches via the vtable into Boss.Evaluate's
    // virtualMethodPointer — the entry compiled/virtual call sites take.
    public class Entity
    {
        public int Tag;
        public virtual int Evaluate(int x) => x + Tag;
        // Rung-A fixture: a VIRTUAL method returning Nullable<T>. The interop-patch must flip the base decl AND
        // every override in LOCKSTEP — a half-flip leaves the vtable slot's signature inconsistent (the override's
        // return type no longer matches the base's), which is why the patch deferred virtual Nullable returns.
        public virtual Vec3? Waypoint(int i) => i == 0 ? (Vec3?)null : new Vec3(i, i, i);
        // Virtual CONTAINER return (§7.6, via the shared VirtualSlotPlanner): a VIRTUAL method returning a natural BCL
        // container (List<int>). The interop-patch must flip the base decl AND every override in LOCKSTEP — a half-flip
        // leaves the vtable slot's return inconsistent. The override returns DIFFERENT content (n*2 vs n) so a battery
        // read proves the OVERRIDE dispatched through the flipped virtual slot.
        public virtual List<int> Ranks(int n) => new List<int> { n, Tag };
        // Virtual CONTAINER PARAM (§7.6 WRITE direction, twin of Ranks): a VIRTUAL method whose PARAM is a natural BCL
        // container (List<int>). The interop-patch must flip the base decl AND every override in LOCKSTEP — a half-flip
        // leaves the vtable slot's PARAM signature inconsistent. The override computes DIFFERENT content (Count*2 vs
        // Count) so a battery read proves the OVERRIDE dispatched through the flipped slot AND the dematerialized
        // il2cpp list arrived with the right length.
        public virtual int Muster(List<int> squad) => squad.Count + Tag;
    }
    public class Boss : Entity
    {
        public Boss() { Tag = 1000; }
        public override int Evaluate(int x) => x * 2 + Tag;
        public override Vec3? Waypoint(int i) => i == 0 ? (Vec3?)null : new Vec3(i * 2, i * 2, i * 2);   // override in the same slot
        public override List<int> Ranks(int n) => new List<int> { n * 2, Tag };                          // override in the same slot (n*2, Tag=1000)
        public override int Muster(List<int> squad) => squad.Count * 2 + Tag;                            // override in the same slot (Count*2, Tag=1000)
    }

    // Generic class: closed instantiations over a *reference* T share one __Canon native body/methodPointer
    // (Container<Player> & Container<string>), while Container<int> gets its own — the generics-dispatch case.
    public class Container<T>
    {
        public T Value;
        public T Get() => Value;
        public void Set(T v) { Value = v; }
    }

    // CROSS-MODULE virtual Task<T> return. Backend<T> OVERRIDES the abstract BackendBase<T>::OpenSession declared in the
    // SEPARATE ToyGame.Core assembly-definition — so this override's vtable slot ROOTS to another proxy module
    // (ToyGame.Core.dll), not Assembly-CSharp. Measured: a single-assembly generic interface-impl is a NewSlot that
    // flips fine — the bug needs the slot ROOT in a DIFFERENT module. PatchVirtualTaskReturns' ResolveSlot then computes
    // root.module != this module and buckets the slot "external", leaving Backend`1::OpenSession the wrapper
    // Il2CppSystem.Threading.Tasks.Task<Session>; the cross-module slot fix flips it. Rooted in Bootstrap.Exercise (via
    // a BackendBase<int> ref) so IL2CPP keeps both the base slot and this override with real methodPointers.
    public class Backend<T> : BackendBase<T>
    {
        public Backend(T tag) { Tag = tag; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public override System.Threading.Tasks.Task<Session> OpenSession() => System.Threading.Tasks.Task.FromResult(new Session("session"));
    }

    public class Player : IDamageable
    {
        public int Health;
        public string Name;
        public Vec3 Position;
        public Faction Side;
        public int Shield { get; private set; } = 50;                                     // IDamageable.Shield

        // --- by-name field-access fixture (Inutil.Fields) — cases interop's typed proxy.Field can't cover ---
        public Loadout  Gear;                                                             // ref-bearing VT field (string ref) -> SetStruct (GC-aware store)
        public Loadout? Stash;                                                            // ref-bearing Nullable VT field    -> SetNullableStruct
        public Player   Rival;                                                            // settable reference field          -> Get/SetObject

        public Player(string name) { Name = name; Health = 100; Gear = new Loadout { Gold = 5, Owner = name }; Stash = null; Rival = null; }

        // Read-backs so the Fields demo can verify a by-name WRITE landed INDEPENDENTLY of Fields (the game's
        // own typed read of the same storage). Rooted in Bootstrap.Exercise so IL2CPP keeps real methodPointers.
        public int    PeekGearGold()   => Gear.Gold;
        public string PeekGearOwner()  => Gear.Owner;
        public int    PeekStashGold()  => Stash.HasValue ? Stash.Value.Gold : -1;
        public string PeekStashOwner() => Stash.HasValue ? Stash.Value.Owner : null;
        public string PeekRivalName()  => Rival != null ? Rival.Name : null;

        public void   Damage(int amount) { Health -= amount; }                            // interface-dispatched target (IDamageable)
        public void   Grow(ref Vec3 v) { v.X *= 2; v.Y *= 2; v.Z *= 2; }                  // ref of a 12B BLITTABLE struct (raw write-back safe)
        public double Scale(double f) => Health * f;                                       // double arg + double return (full-width XMM)
        // ref of a REF-BEARING value type (Loadout{int;string}): writing it back through the pointer stores a string
        // ref into il2cpp storage -> needs a GC write barrier under incremental GC. NOT hooked by the Demo yet (a raw write corrupts).
        public void   Reforge(ref Loadout lo) { lo.Gold += 1; lo.Owner = Name + "*"; }
        // static + a by-value ref-bearing VT param (il2cpp passes it by hidden pointer; Il2CppInterop mis-marshals it
        // as the boxed ptr) alongside a ref of the same — the *DiffUsing codec shape. Fixture target; NOT hooked by the Demo yet.
        public static void Encode(ref Loadout cur, Loadout prev) { cur.Gold -= prev.Gold; }

        public void   TakeDamage(float amount) { Health -= (int)amount; }                 // float arg -> XMM
        public int    GetHealth() => Health;                                              // int return
        public void   Move(Vec2 delta) { Position.X += delta.X; Position.Y += delta.Y; }  // 8B struct arg
        public void   Teleport(Vec3 pos) { Position = pos; }                              // 12B struct arg (by ptr)
        public Vec3   GetPosition() => Position;                                          // struct return (sret)
        public string Greet(string who) => "Hi " + who + ", I am " + Name;                // string arg + return
        public bool   TryConsume(int cost, out int remaining)                             // out param
        { remaining = Health; if (cost <= Health) { Health -= cost; remaining = Health; return true; } return false; }
        public void   Configure(int a, int b, int c, int d, int e, int f)                 // >4 args (stack spill)
        { Health = a + b + c + d + e + f; }
        public Loadout MakeLoadout(int gold) { return new Loadout { Gold = gold, Owner = Name }; } // ref-bearing VT return

        public static Player Create(string name) => new Player(name);                     // static + reference return
    }

    // Vtable-hook fixture: BaseEntity declares an empty virtual (IL2CPP collapses all empty virtual bodies
    // to a shared stub in Release — a methodPointer detour on that stub fires for every other empty virtual
    // and crashes). LeafEntity inherits the slot without overriding. The correct path is a vtable-slot patch
    // on the specific class, which scopes dispatch to its instances and never touches the shared stub.
    public class BaseEntity
    {
        public int Id;
        public virtual void Notify(int code) { }     // empty body -> shared stub in IL2CPP Release
        // Static trampoline: IL2CPP compiles `e.Notify(code)` as a vtable dispatch
        // (obj->klass->vtable[slot].methodPtr), which hits our patched slot. Without this,
        // proxy calls go through il2cpp_runtime_invoke -> il2cpp_object_get_virtual_method ->
        // vtable[slot].method->methodPointer — the method object's pointer, not what we patched.
        public static void TriggerNotify(BaseEntity e, int code) { e.Notify(code); }
    }
    public class LeafEntity : BaseEntity
    {
        public LeafEntity(int id) { Id = id; }
        // no override — inherits the shared-stub slot
    }

    public class Game : ITicker
    {
        public int Score;

        // --- by-name field-access fixture (Inutil.Fields): a STATIC field + a Nullable value field ---
        public static int HighScore;                                                       // static field        -> GetValue/SetValue static routing
        public int?       Bonus;                                                            // Nullable<int> field -> Get/SetNullable
        public int PeekHighScore() => HighScore;                                            // independent read-back (static storage, shared across instances)
        public int PeekBonus()     => Bonus.HasValue ? Bonus.Value : -1;

        // delegate-dispatched calls: the game stores a Func over its OWN method, BUILT IN THE CTOR (before any hook can
        // install), then dispatches THROUGH it. il2cpp's Delegate.Invoke routes through the delegate's method_ptr —
        // cached at construction, equal to the target's methodPointer body — the exact address MinHook patches IN PLACE.
        // So a methodPointer detour MUST still fire regardless of when the delegate was built (a field-swap detour would
        // miss a pre-built delegate; in-place patching does not). Strongest case for the detour: a delegate Invoke is a
        // guaranteed indirect call, so the body can never be inlined at the Invoke site. InvokeDirectTally is the DIRECT-call
        // control. EchoPlayer* probe the ONE gap — a delegate over a fully-shared __Canon generic (all ref-type args):
        // il2cpp runs that via MethodInfo::invoker_method (what inutil overwrites to hook), but Delegate.Invoke dispatches
        // through method_ptr, so the hook is bypassed.
        private readonly Func<int, int> _tallyVia;                                         // delegate over Tally, built in the ctor — BEFORE any hook
        public Game() { _tallyVia = Tally; Bonus = 7; }
        public int    Tally(int n) => Score + n;                                           // non-generic delegate target (own body)
        public int    InvokeDirectTally(int n) => Tally(n);                                // in-game compiled DIRECT call (control)
        public int    InvokeStoredTally(int n) => _tallyVia(n);                            // dispatch Tally THROUGH the pre-built delegate
        public Player EchoPlayerDirect(Player p)      => Echo<Player>(p);                   // in-game DIRECT call to a __Canon generic
        public Player EchoPlayerViaDelegate(Player p) { Func<Player, Player> f = Echo<Player>; return f(p); } // via delegate (the gap)

        public void AddScore(int n) { Score += n; }                                       // overload (1 arg)
        public void AddScore(int n, float mult) { Score += (int)(n * mult); }              // overload (2 args)
        public T    Echo<T>(T input) => input;                                            // generic method (miPos=2, mi in a GPR — no spill)
        // §7.7 ABI-completeness fixture: a WIDE generic whose hidden trailing MethodInfo* (the RGCTX the shared __Canon
        // body receives) SPILLS past GPR slot 3 onto the caller stack. Layout for an instance method with 4 params:
        // this(0) a(1) b(2) c(3) d(4) MethodInfo*(5) -> the mi lands at arg index 5 = caller-stack Stack[1] (Stack[0]
        // holds arg d, so a stack[i-4] off-by-one would read d, not the mi). Hooked for TWO ref-type instantiations
        // (Player, Boss) sharing this __Canon body, the interceptor MUST read the spilled mi to route each call to its
        // own chain; capping at slot 3 mis-routes both to the first registrant. NoInlining keeps the body a real, detourable call.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public T    EchoWide<T>(T a, object b, object c, object d) => a;                   // shared __Canon body for ref-type T
        public Player EchoWidePlayer(Player p) => EchoWide<Player>(p, p, p, p);            // direct compiled __Canon call (Player RGCTX)
        public Boss   EchoWideBoss(Boss bo)    => EchoWide<Boss>(bo, bo, bo, bo);          // direct compiled __Canon call (Boss RGCTX, shares the body)

        // §7.5 ValueTypeBridge fixture: a REF-BEARING value type (Loadout {int Gold; string Owner}) as a tuple
        // ELEMENT and a dict VALUE. The il2cpp container stores Loadout BY VALUE — its `string Owner` field needs
        // the GC write barrier — so a naive ToIl2Cpp write (Il2CppInterop's generic ctor/set_Item) hands over the
        // wrong representation and the game reads a garbled Owner/Gold. These are PARAMS (the write direction the
        // hazard lives in): the game reads the element back, so a mis-marshaled write shows as a wrong description.
        public string DescribeStat((int n, Loadout lo) stat) => $"{stat.n}:{stat.lo.Owner}#{stat.lo.Gold}";
        public int    SumBook(System.Collections.Generic.Dictionary<int, Loadout> book)
        {
            int s = 0;
            foreach (var kv in book) s += kv.Key + kv.Value.Gold + kv.Value.Owner.Length;
            return s;
        }
        // §7.5 list-ELEMENT ref-bearing VT (List<Loadout>): the interop-patch flips this List<Loadout> PARAM to natural;
        // invoking it with a natural List<Loadout> drives DematerializeList's value-type write (Add(T') via
        // ValueTypeBridge.InvokeUnboxed). The game reads each element's Gold+Owner back, so a mis-marshaled element
        // (Il2CppInterop's Add wrapper dropping the embedded string) shows as a wrong sum or NRE.
        public int    SumLoadouts(System.Collections.Generic.List<Loadout> gear)
        {
            int s = 0;
            foreach (var lo in gear) s += lo.Gold + lo.Owner.Length;
            return s;
        }
        public Container<int>    MakeIntBox(int v)        { var c = new Container<int>();    c.Set(v); return c; } // value-T generic (own body)
        public Container<Player> MakePlayerBox(Player p)  { var c = new Container<Player>(); c.Set(p); return c; } // ref-T generic (__Canon)
        public Container<string> MakeStrBox(string s)     { var c = new Container<string>(); c.Set(s); return c; } // ref-T generic (shares __Canon)

        // Same-name, same-ARITY overloads distinguished only by parameter TYPE — what
        // il2cpp_class_get_method_from_name(name, argc) cannot disambiguate, so the metadata-driven resolver needs
        // param-type matching.
        public int Combine(int a)   => a + Score;
        public int Combine(float a) => (int)a + Score;

        // type-friction surface (modder-facing CONVERSIONS): members whose Il2CppInterop PROXY signature surfaces an
        // Il2Cpp WRAPPER type where natural C# would do — the GAME-side targets for the SDK's type-friction sugar. The
        // probe managed/probes/TypeFrictionProbe MEASURES which the modder is forced to hand-write (a CS error at the
        // natural call site) vs which Il2CppInterop already bridges (compiles clean = free). Rooted in Bootstrap.Exercise;
        // a "free" case going red = an interop regression.
        //
        // Nullable<T> — `T?` proxies to Il2CppSystem.Nullable<T>. A struct/enum T gets NO conversion operator (CS0029) —
        // the real residue an L2 shim must bridge.
        public Vec3?    FindSpawn(int id)        => id == 0 ? (Vec3?)null : new Vec3(id, id, id);    // Nullable<Vec3> RETURN (struct)
        public Faction? PreferredSide(bool any)  => any ? Faction.Bear : (Faction?)null;             // Nullable<Faction> RETURN (enum)
        public void     GrantGold(int? amount)   { Score += amount ?? 0; }                           // Nullable<int> PARAM (int? has an explicit op — CS0266)

        // BCL collections — List<T>/Dictionary<,> proxy to Il2CppSystem.Collections.Generic.* with NO conversion
        // (CS0029) — the other residue family (an L2 shim copies element-wise).
        public List<int>              GetHighScores() => new List<int> { Score, Score * 2 };         // List<int> RETURN
        public void                   SetInventory(List<string> items) { Score += items.Count; }     // List<string> PARAM
        public Dictionary<string,int> GetTallies()    => new Dictionary<string,int> { ["score"] = Score }; // Dictionary RETURN

        // §step-2 HashSet<T> new-family experiment: a Set RETURN + a Set PARAM, so BOTH flip seams (ContainerFamily
        // return + ContainerParamRewriter param) and the shared collection materializer are driven for a genuinely new
        // container SHAPE. The game reads the param back (Count) so a mis-flipped set shows as a wrong count.
        public HashSet<int>           GetTagSet()               => new HashSet<int> { Score, Score * 2, 7 };  // HashSet<int> RETURN
        public int                    CountTags(HashSet<int> t) => t.Count;                                   // HashSet<int> PARAM

        // Arrays + delegates — proxy to Il2CppReferenceArray/Il2CppStructArray + Il2CppSystem.Action. The ARRAYS are
        // free (Il2CppInterop ships implicit operators; kept as REGRESSION targets — a red probe = an interop bump
        // regressed them). The Action PARAM is NOT free for a bare lambda.
        public int[]    GetCombo()               => new[] { 1, 2, Score };                           // Il2CppStructArray<int> RETURN (free)
        public string[] GetNames()               => new[] { "alpha", "beta" };                       // Il2CppReferenceArray<string> RETURN (free)
        // Action<int> proxies to Il2CppSystem.Action<int>, which a BARE LAMBDA can't bind to (CS1660): op_Implicit only
        // fires for an already-typed System.Action. inutil's interop-patch FLIPS this param to System.Action<int> so
        // g.ForEachScore(x => ...) is natural.
        public void     ForEachScore(Action<int> cb) { cb(Score); }                                  // Il2CppSystem.Action<int> PARAM (flipped by interop-patch)

        // ValueTuple<string,string> RETURN (sret). Il2CppInterop proxies it as Il2CppSystem.ValueTuple<string,string>
        // (a ref-bearing value proxy: the Item slots are il2cpp string POINTERS, not CLR refs). inutil's interop-patch
        // FLIPS the return to the natural System.ValueTuple and the hook boundary marshals it element-aware (TupleM) —
        // a raw byte-copy would put CLR string refs where il2cpp pointers belong.
        public (string, string) GetMetrics() => ("score=" + Score, "side=Bear");                     // Il2CppSystem.ValueTuple<string,string> RETURN (flipped by interop-patch)

        // Task RETURN (reference proxy). Il2CppInterop proxies these as the reference Il2CppSystem.Threading.Tasks.Task,
        // which natural C# can't spell as System.Threading.Tasks.Task. inutil's interop-patch FLIPS the return: the proxy
        // body wraps the il2cpp task as a CLR carrier (WrapTask) and the hook boundary (TaskM) round-trips a FORWARDED
        // task by identity or builds a fresh completed task for a FABRICATED one. Each carries an observable side effect
        // (Score) so suppress-vs-forward is visible at runtime.
        public System.Threading.Tasks.Task Commit()   { Score += 7;  return System.Threading.Tasks.Task.CompletedTask; }  // a forwarding hook lets the +7 apply
        public System.Threading.Tasks.Task Suppress() { Score += 99; return System.Threading.Tasks.Task.CompletedTask; }  // a suppressing hook skips the +99

        // Task<T> RETURN where T is a REF-BEARING value type (Loadout{int;string} -> Il2CppSystem.ValueType VProxy) — a
        // struct with a managed reference INSIDE a Task result. Il2CppInterop's generic Task.FromResult<T> MIS-MARSHALS
        // such a value-type arg (it hands the boxed proxy POINTER where the UNBOXED value belongs), so the stored result
        // is read at the wrong offset and the embedded reference (Owner) becomes garbage. inutil's TaskM uses FromResult
        // as a completed SHELL and rewrites m_result with the correctly-unboxed bytes, BARRIERED (this player runs
        // INCREMENTAL GC — a raw store would drop the reference). A hook FABRICATES the Task<Loadout>; RoundTripLoadout
        // reads its .Result GAME-SIDE, so a scramble shows as a bad Owner. NoInlining (targeted): the hook must fire at
        // the in-game RoundTripLoadout call site, and an inlined body can't be detoured. Unlike the GetHealth/Tally
        // leaves (kept inlined ON PURPOSE), this must stay a real call.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<Loadout> FetchLoadout() => System.Threading.Tasks.Task.FromResult(new Loadout { Gold = 1, Owner = "base" });
        public string RoundTripLoadout() { Loadout lo = FetchLoadout().Result; return lo.Owner + "#" + lo.Gold; }

        // §item-2 hook Task-FABRICATE: a clean, non-value-type Task<int> whose result RoundTripNumber reads GAME-SIDE. A
        // hook that SetReturnTask's a FABRICATED Task<int> (the ToIl2Cpp write direction, from mod code) shows up as a
        // changed RoundTripNumber — no ref-bearing box-vs-bytes hazard (unlike FetchLoadout), so a clean fabricate oracle.
        // NoInlining so the hook can detour FetchNumber at RoundTripNumber's call site.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<int> FetchNumber() => System.Threading.Tasks.Task.FromResult(3);
        public int RoundTripNumber() => FetchNumber().Result;

        // §item-1b read-side PENDING carrier: a genuinely-pending il2cpp Task<int> (async + a real delay). When the flipped
        // proxy is awaited CLR-side, the carrier must be driven by THIS task's OWN completion (~30ms later), not a
        // pre-completed placeholder — the read mirror of the pending WRITE fabricate.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public async System.Threading.Tasks.Task<int> SlowValue() { await System.Threading.Tasks.Task.Delay(30); return 99; }

        // GENERIC-method Task<T> return, T ONLY in the return. Il2CppInterop's proxy return stays OPEN
        // Il2CppSystem.Task`1<T> (interop-patch TaskFlipPlan can't close an open element), so a hook must INFER T from
        // the return. A hook spelling the FLIPPED System.Threading.Tasks.Task<Concrete> binds (the matcher accepts the
        // il2cpp-Task ↔ BCL-Task pair). Unhooked, LoadTyped<Player>() returns default(T) = null, so RoundTripTyped reads
        // "null"; a bound hook fabricates a Player and TaskM bridges it back. NoInlining: the hook must fire at the
        // in-game LoadTyped<Player>() call site inside RoundTripTyped.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<T> LoadTyped<T>() where T : class => System.Threading.Tasks.Task.FromResult<T>(null);
        public string RoundTripTyped() { Player p = LoadTyped<Player>().Result; return p != null ? p.Name : "null"; }

        // NESTED BCL result: Task<Player[]> (a Task whose result is an ARRAY of proxies). The interop-patch flips the
        // Task; the ELEMENT stays Il2CppReferenceArray<Player>, so a hook may spell the fully-natural System.Task<Player[]>
        // and the recursive boundary converts the managed Player[] back to the il2cpp array before FromResult.
        // FetchFormations nests one deeper (Task<Player[][]>, jagged) to exercise the ArrayConv recursion. RoundTrip* read
        // GAME-SIDE (NoInlining so the hook fires in-game).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<Player[]> FetchSquad() => System.Threading.Tasks.Task.FromResult(new[] { new Player("base") });
        public string RoundTripSquad() { Player[] sq = FetchSquad().Result; string s = ""; foreach (var p in sq) s += p.Name + ";"; return s; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<Player[][]> FetchFormations() => System.Threading.Tasks.Task.FromResult(new Player[][] { new[] { new Player("base") } });
        public string RoundTripFormations() { var f = FetchFormations().Result; string s = ""; foreach (var row in f) { foreach (var p in row) s += p.Name; s += "|"; } return s; }

        // recursive marshalling PHASE 2/3 — nesting a NON-array container inside a Task. Each proxy Task element is an
        // il2cpp CONTAINER (IReadOnlyList / ValueTuple / Nullable), so a hook may spell the fully-natural nested BCL
        // shape; TaskM reads the hook's result and the matching ElemConv converts it to the il2cpp element before
        // FromResult. RoundTrip* read GAME-SIDE (NoInlining).
        //   - FetchRoster: Task<IReadOnlyList<Player>>  (ListConv, reference element)
        //   - FetchPair:   Task<(Player,Player)>        (TupleConv, reference items — the il2cpp tuple is a VALUE result, so
        //                                                the FixValueResult m_result path unboxes the built proxy)
        //   - FetchMaybe:  Task<int?>                   (NullableConv, phase-3 value-in-value)
        //   - FetchStats:  Task<(int,string)>           (TupleConv, phase-3 value-item tuple)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Player>> FetchRoster()
            => System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IReadOnlyList<Player>>(new System.Collections.Generic.List<Player> { new Player("base") });
        public string RoundTripFetchRoster() { var r = FetchRoster().Result; string s = ""; foreach (var p in r) s += p.Name + ";"; return s; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<(Player, Player)> FetchPair() => System.Threading.Tasks.Task.FromResult((new Player("a"), new Player("b")));
        public string RoundTripPair() { var (a, b) = FetchPair().Result; return a.Name + "/" + b.Name; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<int?> FetchMaybe() => System.Threading.Tasks.Task.FromResult<int?>(null);
        public string RoundTripMaybe() { int? v = FetchMaybe().Result; return v.HasValue ? v.Value.ToString() : "n"; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<(int, string)> FetchStats() => System.Threading.Tasks.Task.FromResult((0, "base"));
        public string RoundTripStats() { var (n, tag) = FetchStats().Result; return n + ":" + tag; }

        // recursive marshalling Tier 2 (§Composes) — a GENERIC method whose T the game closes with an ARRAY:
        // FetchTyped<Player[]> reifies FetchTyped<Il2CppReferenceArray<Player>>, whose proxy return stays the un-flipped
        // Il2CppSystem.Task<Il2CppReferenceArray<Player>>. A hook spelling the fully-natural System.Task<Player[]> must
        // BIND (matcher binds T to the il2cpp counterpart, TaskReturnCorresponds accepts the il2cpp<->BCL element pair)
        // AND DELIVER (TaskM converts the Player[] result). Unhooked, FromResult<T>(default) is null; a bound hook fabricates a Player[].
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<T> FetchTyped<T>() where T : class => System.Threading.Tasks.Task.FromResult<T>(null);
        public string RoundTripTypedSquad() { Player[] sq = FetchTyped<Player[]>().Result; if (sq == null) return "null"; string s = ""; foreach (var p in sq) s += p.Name + ";"; return s; }

        // recursive marshalling phase B — a VALUE / STRING array nested in a Task (the ElemConv struct/string array flavors, the
        // phase-1 restriction to reference arrays lifted). Unhooked, FromResult(empty) -> RoundTrip reads ""; a hook fabricates
        // a plain int[] / string[] and the recursive boundary converts it to the il2cpp struct/string array before FromResult.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<int[]> FetchScores() => System.Threading.Tasks.Task.FromResult(new int[0]);
        public string RoundTripScores() { int[] xs = FetchScores().Result; string s = ""; foreach (var x in xs) s += x + ";"; return s; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<string[]> FetchTags() => System.Threading.Tasks.Task.FromResult(new string[0]);
        public string RoundTripTags() { string[] ts = FetchTags().Result; string s = ""; foreach (var t in ts) s += t + ";"; return s; }

        // recursive marshalling A — STANDALONE top-level nested container param/return (not inside a Task). The
        // interop-patch flips the OUTER container one level, keeping the il2cpp element; a hook may spell the fully-natural
        // nested BCL shape, and the hook-aware boundary (ArgM/RetM built from BOTH game + hook types) bridges hook<->game
        // element types. Exercised GAME-SIDE via RoundTrip callers (NoInlining).
        //   - SumGroups(IReadOnlyList<Player[]>)  : CollectionM PARAM Read      - SumJagged(Player[][])         : ArrayM PARAM Read
        //   - SumSquads(IEnumerable<Player[]>)     : EnumerableM PARAM Read      - Squads() : IEnumerable<Player[]> : EnumerableM RETURN Write
        //   - SplitSquad() : (Player[], string)    : TupleM RETURN Write (a nested-array tuple item)
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public int SumGroups(System.Collections.Generic.IReadOnlyList<Player[]> groups) { int n = 0; foreach (var g in groups) foreach (var p in g) n += p.Name.Length; return n; }
        public int RoundTripGroups() { var groups = new System.Collections.Generic.List<Player[]> { new[] { new Player("aa") }, new[] { new Player("bbb") } }; return SumGroups(groups); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public int SumJagged(Player[][] rows) { int n = 0; foreach (var row in rows) foreach (var p in row) n += p.Name.Length; return n; }
        public int RoundTripJagged() { var rows = new Player[][] { new[] { new Player("aa") }, new[] { new Player("bbb") } }; return SumJagged(rows); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public int SumSquads(System.Collections.Generic.IEnumerable<Player[]> squads) { int n = 0; foreach (var g in squads) foreach (var p in g) n += p.Name.Length; return n; }
        public int RoundTripSumSquads() { var squads = new System.Collections.Generic.List<Player[]> { new[] { new Player("aa") }, new[] { new Player("bbb") } }; return SumSquads(squads); }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Collections.Generic.IEnumerable<Player[]> Squads() => new System.Collections.Generic.List<Player[]> { new[] { new Player("base") } };
        public string RoundTripSquads() { string s = ""; foreach (var g in Squads()) { foreach (var p in g) s += p.Name; s += "|"; } return s; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public (Player[], string) SplitSquad() => (new[] { new Player("base") }, "base");
        public string RoundTripSplit() { var (sq, tag) = SplitSquad(); string s = ""; foreach (var p in sq) s += p.Name; return s + "/" + tag; }

        // PENDING Task seam — a hook must WAIT before it returns. Unhooked, FetchLoadoutSlow completes instantly; a hook
        // FLIPs it to a genuinely pending Task<Loadout> (completes N frames later). RunSlowLoadout AWAITs it GAME-SIDE
        // (async void: fire-and-forget, result observed via SlowLoadoutResult), so the demo can prove the game resumed
        // only AFTER completion, on the main thread, with the ref-bearing value-type result (Owner#Gold) intact.
        public string SlowLoadoutResult = "";
        // NoInlining (targeted): the seam is awaited from an async state machine, a call site IL2CPP would otherwise
        // inline a trivial leaf into — and an inlined body can't be detoured. It must stay a real call so the hook that
        // flips it to a pending Task fires.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task<Loadout> FetchLoadoutSlow() => System.Threading.Tasks.Task.FromResult(new Loadout { Gold = 1, Owner = "base" });
        public async void RunSlowLoadout() { Loadout lo = await FetchLoadoutSlow(); SlowLoadoutResult = lo.Owner + "#" + lo.Gold; }

        // Non-generic pending twin (Task, no result) — routes through Task`1<VoidTaskResult> on the bridge side. Unhooked
        // WarmUp completes instantly; a hook makes it pending, and RunWarmUp awaits it, flipping WarmedUp only after it lands.
        public bool WarmedUp = false;
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public System.Threading.Tasks.Task WarmUp() => System.Threading.Tasks.Task.CompletedTask;
        public async void RunWarmUp() { await WarmUp(); WarmedUp = true; }

        // IEnumerable<T> RETURN + PARAM — the "give me / take a sequence" shape. Il2CppInterop proxies IEnumerable<Player>
        // as the reference Il2CppSystem...IEnumerable`1<Player>, whose enumerator is awkward to foreach. The interop-patch
        // flips the RETURN to natural IEnumerable<Player> (routing each ret through WrapEnumerable) and the PARAM to natural
        // IEnumerable<Player> (splicing ToIl2CppEnumerable at entry); the hook boundary (EnumerableM) wraps the native
        // pointer / converts a sequence back — a container bridge, no element marshalling (Player is identical in both
        // worlds). RoundTripRoster reads the hook's sequence GAME-SIDE; CountRoster sums element state so a scrambled element shows.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]  // marshalling-test target: hook must fire at the in-game RoundTripRoster call site (not inlined away)
        public System.Collections.Generic.IEnumerable<Player> Roster() => new System.Collections.Generic.List<Player> { new Player("base") };
        public string RoundTripRoster() { string s = ""; foreach (var p in Roster()) s += p.Name + ";"; return s; }        // reads the returned IEnumerable game-side
        public int CountRoster(System.Collections.Generic.IEnumerable<Player> roster) { int n = 0; foreach (var p in roster) n += p.Name.Length; return n; }  // consumes an IEnumerable param

        void ITicker.Tick() { Score++; }                                                  // EXPLICIT interface impl (awkward to name; fixture)
    }
}
