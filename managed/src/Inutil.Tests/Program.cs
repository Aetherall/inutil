using Inutil.Sugar;
using Inutil.Marshal;
using Inutil.Schema;
using Inutil;                                       // mod-facing surface: Mods, ILoad/IUnload/ITick/IGui
using Inutil.Host;                                 // scheduling: MainThread + Coroutines; FrameDriver
using System.Collections;                          // IEnumerator — coroutine bodies below
using Il2CppInterop.Runtime.InteropTypes.Arrays;   // Il2CppStructArray / Il2CppStringArray — for the write-side wrapper-resolution tests
using Task = System.Threading.Tasks.Task;

// Offline unit tests for the SDK runtime seam's game-independent surface. The Il2CppSugar carrier logic (TCS + one
// shared ConditionalWeakTable, keyed by a raw nint) needs no il2cpp domain, so it is fully testable here; the
// il2cpp-touching overloads (Il2CppObjectBase) and container marshalling are validated in-game. Proves the identity
// round-trip, the Task<T>:Task shared-map contract, non-carrier discrimination, Leaf identity, null propagation,
// and that the deferred container paths fail LOUD rather than silently mis-marshal.

int fail = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"  {(ok ? "ok   " : "WRONG")} [{name}]");
    if (!ok) fail++;
}

Console.WriteLine("── Il2CppSugar carrier: identity round-trip (game-independent nint path) ──");

Check("WrapTask(0) -> null", Il2CppSugar.WrapTask((nint)0) is null);
Check("WrapTaskT<int>(0) -> null", Il2CppSugar.WrapTaskT<int>((nint)0) is null);

nint ptr = (nint)0xBEEF;
Task? t = Il2CppSugar.WrapTask(ptr);
Check("WrapTask carrier is non-null", t is not null);
Check("carrier is pre-completed (await won't hang)", t!.IsCompleted);
Check("UnwrapTask recovers the il2cpp pointer", Il2CppSugar.UnwrapTask(t) == ptr);
Check("IsCarrier true for a wrapped task", Il2CppSugar.IsCarrier(t));

nint ptrT = (nint)0xF00D;
Task<int>? tt = Il2CppSugar.WrapTaskT<int>(ptrT);
Check("WrapTaskT carrier non-null + completed", tt is { IsCompleted: true });
Check("UnwrapTask recovers Task<T> pointer via the SHARED map (Task<T> : Task)", Il2CppSugar.UnwrapTask(tt!) == ptrT);
Check("carrier result is the v1 placeholder default(T)", tt!.Result == 0);

// two distinct carriers keep distinct identities
Check("distinct carriers keep distinct pointers", Il2CppSugar.UnwrapTask(t) != Il2CppSugar.UnwrapTask(tt!));

// a mod-FABRICATED (non-carrier) CLR task is not ours -> the boundary marshaller must rebuild it, not forward it
Task fab = Task.CompletedTask;
Check("fabricated task Unwraps to 0", Il2CppSugar.UnwrapTask(fab) == 0);
Check("fabricated task IsCarrier false", !Il2CppSugar.IsCarrier(fab));
Check("UnwrapTask(null) -> 0", Il2CppSugar.UnwrapTask(null) == 0);
Check("IsCarrier(null) -> false", !Il2CppSugar.IsCarrier(null));

Console.WriteLine("\n── Il2CppConvRuntime: structure (Leaf identity, null, fail-loud deferral) ──");

var rt = new Il2CppConvRuntime();     // static init resolves the WrapTaskT<T> reflection — throws here if it drifts
var shapes = new Il2CppConvShapeSource();

Conv leaf = Conv.Build(typeof(int), shapes);
Check("leaf int is Identity", leaf.Identity);
Check("Leaf() returns value unchanged (ToIl2Cpp)", Equals(rt.Leaf(leaf, 42, ConvDirection.ToIl2Cpp), 42));
Check("Leaf() returns value unchanged (ToManaged)", Equals(rt.Leaf(leaf, "x", ConvDirection.ToManaged), "x"));

// the shape source still classifies BCL families correctly (the il2cpp-leaf override didn't break the base)
Conv taskConv = Conv.Build(typeof(Task<int>), shapes);
Check("Task<int> classifies as a Task container", taskConv is { Kind: ConvKind.Task, Identity: false });
Check("Task container has one child = spelled element int", taskConv.Children is [{ Managed: var m }] && m == typeof(int));
Check("int[] classifies as an Array container", Conv.Build(typeof(int[]), shapes).Kind == ConvKind.Array);

// null propagates without touching il2cpp
Check("Container(null) -> null", rt.Container(taskConv, null, ConvDirection.ToManaged) is null);

// Array ToIl2Cpp: the write-side type RESOLUTION (Il2CppTypeOf) is pure metadata (no booted runtime) so it is
// offline-tested here — including the RECURSION (int[][] -> a ReferenceArray whose element is itself a
// StructArray wrapper); the actual CONSTRUCTION is validated in-game. A List/Dict write IS wired (below); its
// il2cpp type resolves via the R1 runtime proxy resolver, which fails LOUD OFFLINE (no Il2Cppmscorlib loaded).
Check("Array ToIl2Cpp resolves int[] -> Il2CppStructArray<int>", ContainerBridge.Il2CppTypeOf(Conv.Build(typeof(int[]), shapes)) == typeof(Il2CppStructArray<int>));
Check("Array ToIl2Cpp resolves string[] -> Il2CppStringArray", ContainerBridge.Il2CppTypeOf(Conv.Build(typeof(string[]), shapes)) == typeof(Il2CppStringArray));
Check("Array ToIl2Cpp RECURSES int[][] -> Il2CppReferenceArray<Il2CppStructArray<int>>", ContainerBridge.Il2CppTypeOf(Conv.Build(typeof(int[][]), shapes)) == typeof(Il2CppReferenceArray<Il2CppStructArray<int>>));
// List write goes through the R1 runtime proxy resolver, which offline (no Il2Cppmscorlib loaded) fails LOUD;
// its real RESOLUTION (-> Il2CppSystem.List<int>) is validated in-game. Dictionary write resolves the SAME way
// (wired + validated in-game) -> offline it is also loud for the identical reason (not a missing feature).
Check("List ToIl2Cpp resolves via runtime scan -> throws offline (Il2Cppmscorlib not loaded)", Throws<NotSupportedException>(() => ContainerBridge.Il2CppTypeOf(Conv.Build(typeof(List<int>), shapes))));
Check("Dictionary ToIl2Cpp resolves via runtime scan -> throws offline (Il2Cppmscorlib not loaded)", Throws<NotSupportedException>(() => ContainerBridge.Il2CppTypeOf(Conv.Build(typeof(Dictionary<int, string>), shapes))));
// Task ToIl2Cpp now FABRICATES (DematerializeTask): a completed managed task resolves the il2cpp Task`1 proxy via the
// R1 runtime scan, which offline (no Il2Cppmscorlib) fails LOUD at resolution — its real fabricate is validated in-game.
Check("Task ToIl2Cpp fabricate resolves via runtime scan -> throws offline (Il2Cppmscorlib not loaded)",
    Throws<NotSupportedException>(() => rt.Container(taskConv, Task.FromResult(5), ConvDirection.ToIl2Cpp)));

// Delegate: a NATURAL (BCL) delegate spelled at the hook boundary classifies as ConvKind.Delegate and FAILS LOUD in
// BOTH directions — the delegate family is call-site-only (the IL seam flips an Il2CppSystem.Action/Func PARAM via
// op_Implicit for a DIRECT call); there is no reverse il2cpp<->BCL runtime bridge, so identity-passing a raw pointer
// as a CLR delegate is never allowed. A hook that wants to observe the delegate spells the raw Il2CppSystem.Action
// (a leaf). null short-circuits to null (null is null either way), so only a non-null delegate reaches the throw.
Conv delConv = Conv.Build(typeof(Action<int>), shapes);
Check("System.Action<int> classifies as ConvKind.Delegate", delConv.Kind == ConvKind.Delegate);
Check("non-generic System.Action classifies as ConvKind.Delegate", Conv.Build(typeof(Action), shapes).Kind == ConvKind.Delegate);
Check("Delegate ToManaged fails loud (no reverse il2cpp->BCL bridge)", Throws<NotSupportedException>(() => rt.Container(delConv, new Action<int>(_ => { }), ConvDirection.ToManaged)));
Check("Delegate ToIl2Cpp fails loud (call-site-only family)", Throws<NotSupportedException>(() => rt.Container(delConv, new Action<int>(_ => { }), ConvDirection.ToIl2Cpp)));
Check("Delegate null -> null (null is null either way)", rt.Container(delConv, null, ConvDirection.ToManaged) is null);

Console.WriteLine("\n── ContainerBridge: Array ToManaged materialization (§7.6, plain-array stand-in) ──");

// Il2CppMarshal.ToManaged drives the Conv tree + the real Il2CppConvRuntime through its PUBLIC boundary entry
// (For + the C2 validator + Convert); a plain BCL array stands in for the il2cpp one (both are IEnumerable; the
// leaf is identity). Arrays are UNANCHORED, so the validator passes them through — this is the one container kind
// whose real boundary entry is exercisable offline, so it stays on Il2CppMarshal.ToManaged (the anchored kinds
// below drive the Conv door directly, since their anchor can only match an in-game il2cpp type).
{
    int[] r = Il2CppMarshal.ToManaged<int[]>(new[] { 10, 20, 30 })!;
    Check("int[] materializes element-wise", r is [10, 20, 30]);
}
{
    string[] r = Il2CppMarshal.ToManaged<string[]>(new[] { "a", "b" })!;
    Check("string[] materializes element-wise", r is ["a", "b"]);
}
{
    // NESTED: int[][] recurses — each outer element goes through the child Array node's OWN materialization.
    int[][] r = Il2CppMarshal.ToManaged<int[][]>(new[] { new[] { 1, 2 }, new[] { 3 } })!;
    Check("int[][] recurses through the child Array Conv", r is [[1, 2], [3]]);
}
{
    // Direct ContainerBridge: the result ELEMENT TYPE is the spelled one (int), a typed int[] not object[].
    Array a = ContainerBridge.MaterializeArray(Conv.Build(typeof(int[]), shapes), new[] { 7, 8 }, ConvDirection.ToManaged, rt);
    Check("MaterializeArray yields a typed int[] (not object[])", a is int[] { Length: 2 });
}
Check("Array ToManaged of null -> null", rt.Container(Conv.Build(typeof(int[]), shapes), null, ConvDirection.ToManaged) is null);

Console.WriteLine("\n── ContainerBridge: List / IReadOnlyList / Enumerable ToManaged (§7.6) ──");
{
    List<int> r = ToManagedDirect<List<int>>(new List<int> { 1, 2, 3 })!;
    Check("List<int> materializes element-wise", r is [1, 2, 3]);
}
{
    IReadOnlyList<int> r = ToManagedDirect<IReadOnlyList<int>>(new List<int> { 4, 5 })!;
    Check("IReadOnlyList<int> materializes (a List<int> satisfies it)", r is [4, 5]);
}
{
    IEnumerable<string> r = ToManagedDirect<IEnumerable<string>>(new List<string> { "p", "q" })!;
    Check("IEnumerable<string> materializes", r is ICollection<string> { Count: 2 } && r.SequenceEqual(new[] { "p", "q" }));
}
{
    // NESTED: List<int[]> — each element recurses through the child Array node's OWN materialization.
    List<int[]> r = ToManagedDirect<List<int[]>>(new List<int[]> { new[] { 1 }, new[] { 2, 3 } })!;
    Check("List<int[]> recurses through the child Array Conv", r.Count == 2 && r[0] is [1] && r[1] is [2, 3]);
}
{
    // Enumeration FALLBACK: a source with Count+indexer but NO IEnumerable (the il2cpp-collection shape) enumerates.
    List<int> r = ToManagedDirect<List<int>>(new IndexOnly(9, 8))!;
    Check("indexer-fallback enumeration (Count+Item, no IEnumerable)", r is [9, 8]);
}

Console.WriteLine("\n── ContainerBridge: Dictionary ToManaged (§7.6, 2-child key+value recursion) ──");
{
    Dictionary<int, string> r = ToManagedDirect<Dictionary<int, string>>(
        new Dictionary<int, string> { [1] = "a", [2] = "b" })!;
    Check("Dictionary<int,string> materializes both children", r is { Count: 2 } && r[1] == "a" && r[2] == "b");
}
{
    // NESTED value: Dictionary<int,int[]> — each VALUE recurses through the child Array Conv (2-child node whose
    // value child is itself a container).
    Dictionary<int, int[]> r = ToManagedDirect<Dictionary<int, int[]>>(
        new Dictionary<int, int[]> { [1] = new[] { 7, 8 } })!;
    Check("Dictionary<int,int[]> recurses value through child Array Conv", r is { Count: 1 } && r[1] is [7, 8]);
}
{
    // PAIR-enumeration FALLBACK: a dict-shaped source with GetEnumerator/MoveNext/Current but NO IEnumerable
    // (the il2cpp-dict shape) enumerates through the universal protocol.
    Dictionary<int, string> r = ToManagedDirect<Dictionary<int, string>>(new PairOnly((5, "x"), (6, "y")))!;
    Check("pair-fallback enumeration (GetEnumerator/MoveNext/Current, no IEnumerable)", r is { Count: 2 } && r[5] == "x" && r[6] == "y");
}
Check("Dictionary ToManaged of null -> null", rt.Container(Conv.Build(typeof(Dictionary<int, string>), shapes), null, ConvDirection.ToManaged) is null);
Check("Dictionary ToIl2Cpp (full Dematerialize path) throws offline at resolution (validated in-game)", Throws<NotSupportedException>(() => rt.Container(Conv.Build(typeof(Dictionary<int, string>), shapes), new Dictionary<int, string>(), ConvDirection.ToIl2Cpp)));

Console.WriteLine("\n── ContainerBridge: Nullable / Tuple ToManaged (§7.6, value-type read side) ──");
{
    // Nullable reads HasValue/Value off the proxy; a NullableProxy stand-in mirrors the il2cpp Nullable<T> shape
    // (a boxed BCL int? is just a boxed int — no HasValue property — so the il2cpp shape needs a real stand-in).
    int? r = ToManagedDirect<int?>(new NullableProxy(true, 42));
    Check("Nullable<int> HasValue reads inner value", r == 42);
}
{
    int? r = ToManagedDirect<int?>(new NullableProxy(false, 0));
    Check("Nullable<int> !HasValue -> null", r is null);
}
{
    // property path: an il2cpp ValueTuple exposes its fields AS properties (TupleProxy mirrors that shape).
    (int, string) r = ToManagedDirect<(int, string)>(new TupleProxy<int, string>(9, "z"));
    Check("ValueTuple<int,string> reads Item1/Item2 (property path)", r == (9, "z"));
}
{
    // field path: a BCL ValueTuple's Item1..N are FIELDS — the ReadItem property-then-field fallback covers both.
    (int, int) r = ToManagedDirect<(int, int)>((3, 4));
    Check("ValueTuple field path (BCL tuple stand-in)", r == (3, 4));
}
{
    // 3-arity: proves the N-child construction beyond a pair.
    (int, string, bool) r = ToManagedDirect<(int, string, bool)>(new TupleProxy3<int, string, bool>(1, "a", true));
    Check("ValueTuple<int,string,bool> constructs 3-arity", r == (1, "a", true));
}
{
    // NESTED element: (int, int[]) — the second element recurses through the child Array Conv.
    (int, int[]) r = ToManagedDirect<(int, int[])>(new TupleProxy<int, int[]>(1, new[] { 5, 6 }));
    Check("ValueTuple recurses element through child Array Conv", r.Item1 == 1 && r.Item2 is [5, 6]);
}
Check("Nullable ToIl2Cpp (Dematerialize path) throws offline at resolution (validated in-game)", Throws<NotSupportedException>(() => rt.Container(Conv.Build(typeof(int?), shapes), 5, ConvDirection.ToIl2Cpp)));
Check("Tuple ToIl2Cpp (Dematerialize path) throws offline at resolution (validated in-game)", Throws<NotSupportedException>(() => rt.Container(Conv.Build(typeof((int, string)), shapes), (1, "x"), ConvDirection.ToIl2Cpp)));

// ── scheduling: MainThread pump + Coroutines runner (pure-managed — no il2cpp domain needed) ──────────
// The pump/coroutine logic is game-agnostic (only MainThread.Pin touches il2cpp, exercised in-game), so its
// snapshot budget, exception isolation, next-tick timing, and the NEW Post<T> pre-pump guard all validate here.
Console.WriteLine("\n── MainThread + Coroutines (§7.11 scheduling — game-independent) ──");

// Coroutines.Tick() is internal (driven only through the public MainThread.Drain seam); the action queue is
// empty here, so each Drain() is a clean coroutine tick. Fire-and-forget posts use block-body lambdas so they
// bind Post(Action), not Post<T> (a `() => expr` returning a value would pick the guarded result overload).

// (a) The pre-pump guard MUST be asserted FIRST: the Drain() below latches IsPumping true for the process.
Check("pre-pump: IsPumping is false before the first Drain", !MainThread.IsPumping);
Check("pre-pump: Post<T> fails loud rather than deadlocking (nothing drains the queue yet)",
    Throws<InvalidOperationException>(() => { _ = MainThread.Post(() => 1); }));

// (b) Post(Action) is fire-and-forget: it enqueues pre-pump without throwing and runs on the first tick.
int ran = 0;
MainThread.Post(() => { ran++; });                  // enqueued while still pre-pump — no guard, no throw
Check("pre-pump: Post(Action) enqueues without throwing (deferred to the first tick)", ran == 0);
MainThread.Drain();                                 // first tick — captures THIS thread as the main thread
Check("first Drain runs the pre-pump-enqueued action", ran == 1);
Check("IsPumping latches true after the first Drain", MainThread.IsPumping);

// (c) Post<T> post-pump on the (captured) main thread runs INLINE and completes with the value.
Check("Post<T> on the main thread runs inline + completes with the value",
    MainThread.Post(() => 7) is { IsCompleted: true, Result: 7 });

// (d) Snapshot budget: an action that itself Posts runs on the NEXT tick, not this one (can't starve the frame).
int reentrant = 0;
MainThread.Post(() => { MainThread.Post(() => { reentrant++; }); });
MainThread.Drain();                                 // runs the outer post; the inner is queued for next tick
Check("self-posting action defers its post to the next tick (snapshot budget)", reentrant == 0);
MainThread.Drain();
Check("...and the deferred post runs on the following tick", reentrant == 1);

// (e) Exception isolation: a throwing posted action never stops its siblings or the pump.
int after = 0;
MainThread.Post(() => { throw new InvalidOperationException("boom"); });
MainThread.Post(() => { after++; });
MainThread.Drain();
Check("a throwing posted action is isolated — its sibling still runs", after == 1);

// (f) Coroutine: first MoveNext runs on the NEXT tick; then one step per tick; then reaped when done.
int steps = 0;
IEnumerator Routine() { steps++; yield return null; steps++; yield return null; steps++; }
var co = Coroutines.Start(Routine());
Check("coroutine: no step before the first tick (uniform next-tick first step)", steps == 0 && co.IsRunning);
MainThread.Drain(); Check("coroutine: tick 1 runs to the first yield", steps == 1 && co.IsRunning);
MainThread.Drain(); Check("coroutine: tick 2 advances one step", steps == 2 && co.IsRunning);
MainThread.Drain(); Check("coroutine: tick 3 runs the tail + completes (reaped)", steps == 3 && !co.IsRunning && co.Fault is null);

// (g) Wait.Until gate: parks until the predicate flips true (polled each tick on the main thread).
bool open = false; int afterGate = 0;
IEnumerator Gated() { yield return Wait.Until(() => open); afterGate = 1; }
var gc = Coroutines.Start(Gated());
MainThread.Drain();                                 // t1: MoveNext parks on the gate
MainThread.Drain();                                 // t2: gate polled, still closed
Check("Wait.Until parks while the predicate is false", afterGate == 0 && gc.IsRunning);
open = true;
MainThread.Drain();                                 // gate opens -> resumes THIS tick (no wasted idle tick)
Check("Wait.Until resumes when the predicate flips true", afterGate == 1 && !gc.IsRunning);

// (h) A faulting coroutine is dropped with its Fault set; a sibling coroutine is unaffected.
int sib = 0;
IEnumerator Boom() { yield return null; throw new InvalidOperationException("co-boom"); }
IEnumerator Sib()  { yield return null; sib = 1; }
var bc = Coroutines.Start(Boom());
var sc = Coroutines.Start(Sib());
MainThread.Drain();                                 // both take their first step (yield null)
MainThread.Drain();                                 // bc throws -> faulted; sc runs sib=1, unaffected
Check("faulted coroutine carries its Fault + stops", bc is { IsRunning: false, Fault: InvalidOperationException });
Check("a sibling coroutine is unaffected by a fault", sib == 1 && !sc.IsRunning);

// ── mod lifecycle registry + FrameDriver (per-frame driving + teardown — game-independent) ────────────
// The registry + FrameDriver are pure-managed (RemoveAll routes by AssemblyLoadContext, which resolves offline),
// so the load/tick/gui/unload lifecycle, exception isolation, and the FrameDriver contract all validate here.
Console.WriteLine("\n── Mods lifecycle + FrameDriver (§7.10 — game-independent) ──");
var lifeMod = new LifecycleMod();
Mods.Add(lifeMod);
Check("Mods.Add fires OnLoad once", lifeMod.Loads == 1);
Check("Mods retains the registered object (Count reflects it)", Mods.Count == 1);
FrameDriver.Tick();
Check("FrameDriver.Tick drives ITick.Tick (via Mods.Tick, after MainThread.Drain)", lifeMod.Ticks == 1);
FrameDriver.Gui();
Check("FrameDriver.Gui drives IGui.OnGui", lifeMod.Guis == 1);

// exception isolation: a throwing ITick never stops a sibling mod's tick.
var badTick = new ThrowingTickMod();
var goodTick = new LifecycleMod();
Mods.Add(badTick); Mods.Add(goodTick);
FrameDriver.Tick();
Check("a throwing ITick is isolated — a sibling mod still ticks", goodTick.Ticks == 1);

// RemoveAll(alc) fires OnUnload and drops every object owned by that ALC (the mod-teardown un-root).
var thisAlc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(LifecycleMod).Assembly)!;
int countBefore = Mods.Count;
int removed = Mods.RemoveAll(thisAlc);
Check("RemoveAll(alc) fires OnUnload on a registered mod", lifeMod.Unloads == 1);
Check("RemoveAll(alc) drops all objects owned by that ALC", removed == countBefore && Mods.Count == 0);

// Json.From(object) — the object-typed serialize twin (a hook's flipped System.Object param). The il2cpp path
// needs a game (validated there); the game-free contract is null propagation + the fail-loud non-il2cpp guard,
// which must reject BEFORE consulting the serializer seam (so it trips even with no serializer registered).
Console.WriteLine("\n── Json.From(object) — object-typed serialize twin (game-independent guards) ──");
Check("From((object?)null) -> null", Inutil.Json.From((object?)null) is null);
Check("From(object) rejects a managed object loud (before the serializer seam)",
    Throws<ArgumentException>(() => Inutil.Json.From(new object())));
Check("From(object) rejects a managed string loud", Throws<ArgumentException>(() => Inutil.Json.From((object)"plain")));

Console.WriteLine(fail == 0
    ? "\nINUTIL SDK TESTS GREEN — carrier identity holds, Leaf is identity, Array materializes, deferred paths fail loud."
    : $"\nINUTIL SDK TESTS RED — {fail} wrong.");
return fail == 0 ? 0 : 1;

// Drive the Conv tree DIRECTLY (unanchored Build + Convert) for the offline marshalling-LOGIC tests, bypassing
// Il2CppMarshal.ToManaged's C2 correspondence validator. That validator checks the game's ACTUAL il2cpp type
// against the spelled anchor and can only PASS in-game — an offline BCL stand-in (a System.* List standing in for
// an Il2CppSystem.* List) is precisely the mismatch it exists to reject, which is what crashed this suite once C2
// turned the validator on. The LOGIC these tests prove (element recursion, enumeration fallbacks, value-type reads)
// is unchanged; the validator has its OWN coverage — Schema.Tests offline + the battery's
// container.correspondence.mismatch.fails-loud in-game. Arrays stay on the real Il2CppMarshal.ToManaged entry
// (unanchored -> the validator passes them through, so that entry IS exercisable offline for that kind).
T? ToManagedDirect<T>(object? il2cppValue)
    => (T?)Conv.Build(typeof(T), shapes).Convert(il2cppValue, ConvDirection.ToManaged, rt);

static bool Throws<TEx>(Action a) where TEx : Exception
{
    try { a(); return false; } catch (TEx) { return true; } catch { return false; }
}

// Count + indexer but deliberately NOT IEnumerable — stands in for an il2cpp collection proxy so the
// enumeration FALLBACK path is exercised offline.
sealed class IndexOnly
{
    readonly int[] _a;
    public IndexOnly(params int[] a) => _a = a;
    public int Count => _a.Length;
    public int this[int i] => _a[i];
}

// GetEnumerator/MoveNext/Current with a public Current pair, but deliberately NOT IEnumerable — stands in for an
// il2cpp Dictionary proxy so EnumeratePairs' universal-protocol fallback is exercised offline. Current is a plain
// KeyValuePair (its Key/Value read exactly like an il2cpp KeyValuePair proxy's).
sealed class PairOnly
{
    readonly (int, string)[] _p;
    public PairOnly(params (int, string)[] p) => _p = p;
    public Cursor GetEnumerator() => new(_p);

    public sealed class Cursor
    {
        readonly (int, string)[] _p;
        int _i = -1;
        public Cursor((int, string)[] p) => _p = p;
        public bool MoveNext() => ++_i < _p.Length;
        public KeyValuePair<int, string> Current => new(_p[_i].Item1, _p[_i].Item2);
    }
}

// A test mod implementing every lifecycle seam — counts each callback so the registry + FrameDriver driving is
// observable offline. Its assembly is the test assembly, so RemoveAll(thisAlc) owns and drops it.
sealed class LifecycleMod : ILoad, IUnload, ITick, IGui
{
    public int Loads, Unloads, Ticks, Guis;
    public void OnLoad() => Loads++;
    public void OnUnload() => Unloads++;
    public void Tick() => Ticks++;
    public void OnGui() => Guis++;
}

// A mod whose ITick throws — proves the registry exception-isolates one bad mod from its siblings' ticks.
sealed class ThrowingTickMod : ITick
{
    public void Tick() => throw new InvalidOperationException("tick-boom");
}

// HasValue/Value proxy — stands in for an il2cpp Nullable<T> (a boxed BCL int? is just a boxed int with no
// HasValue property, so the il2cpp read path needs a shaped stand-in to exercise offline).
sealed class NullableProxy
{
    public bool HasValue { get; }
    public int Value { get; }
    public NullableProxy(bool has, int val) { HasValue = has; Value = val; }
}

// Item1..N exposed as PROPERTIES — stands in for an il2cpp ValueTuple proxy (whose il2cpp fields surface as
// properties), so ReadItem's property branch is exercised offline; the BCL ValueTuple (fields) covers the other.
sealed class TupleProxy<T1, T2>
{
    public T1 Item1 { get; }
    public T2 Item2 { get; }
    public TupleProxy(T1 a, T2 b) { Item1 = a; Item2 = b; }
}

sealed class TupleProxy3<T1, T2, T3>
{
    public T1 Item1 { get; }
    public T2 Item2 { get; }
    public T3 Item3 { get; }
    public TupleProxy3(T1 a, T2 b, T3 c) { Item1 = a; Item2 = b; Item3 = c; }
}
