using Inutil;
using Inutil.Host;
using Inutil.Marshal;
using Inutil.Repl;
using Inutil.Schema;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

// Offline end-to-end test of the no-build mod host: compile a REAL lifecycle mod from source, load it into a
// collectible ModContext, Discover it, drive a frame, and tear it down — asserting each lifecycle seam fired (via
// a marker file, since the mod lives in its own ALC). A lifecycle mod touches no il2cpp, so the whole compile ->
// load -> Discover -> tick -> unload path runs offline. The Hook<T> half (needs a live il2cpp frame) is in-game.

// ── serve-mcp mode: the standalone host for the MCP E2E test (not a check) ──────────────────────────────────
// `dotnet inutil-mods-tests.dll serve-mcp [port]` stands the SSE MCP server up over a ReplSession and BECOMES the
// main thread (Drain in a loop), so an agent can repl_eval pure C# with no game booted — the same offline pump
// the ReplServer socket check below uses. Prints `MCP_PORT=<port>` for the E2E script
// (mods/mcp-repl/test/e2e-claude.sh) and runs until killed.
if (args.Length > 0 && args[0] == "serve-mcp")
{
    int servePort = args.Length > 1 && int.TryParse(args[1], out int pp) ? pp : 0;
    MainThread.Drain();                                    // become the main thread (sets IsPumping)
    using var mcp = ReplMcpServer.Start(servePort, "", Console.Error.WriteLine);
    Console.WriteLine($"MCP_PORT={mcp.Port}"); Console.Out.Flush();
    while (true) { MainThread.Drain(); System.Threading.Thread.Sleep(5); }
}

int fail = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"  {(ok ? "ok   " : "WRONG")} [{name}]");
    if (!ok) fail++;
}

// ── build the reference set + a scratch mod dir ─────────────────────────────────────────────────────────────
// The test output dir holds Inutil.dll (+ Inutil.Schema.dll, Roslyn). Il2CppInterop is Private=false in the SDK
// so it is NOT copied here — but a lifecycle mod references only Inutil's ILoad/ITick/IUnload, which carry no
// il2cpp in their signatures, so the compile needs just the SDK + the BCL (RuntimeDir, auto-appended).
string baseDir = AppContext.BaseDirectory;
var refDirs = new List<string> { baseDir };
// If a staged Unity tree is present, add its interop + core dirs too (harmless, and robust if the SDK surface
// a mod touches ever pulls an il2cpp type transitively).
string unity = Environment.GetEnvironmentVariable("INUTIL_UNITY_DIR")
    ?? Path.Combine(baseDir, "../../../../../.unity-build");
foreach (string sub in new[] { "loaders/bepinex/BepInEx/core", "loaders/bepinex/BepInEx/interop" })
{
    string d = Path.GetFullPath(Path.Combine(unity, sub));
    if (Directory.Exists(d)) refDirs.Add(d);
}

// RUNTIME resolution from the same staged dirs: the SDK references Il2CppInterop with Private=false (never copied
// beside the tests), and some engine paths the suite exercises FORCE that assembly to load (e.g. HookMatch's
// natural-array map naming Il2CppReferenceArray<T>). Without this hook those paths throw FileNotFoundException
// offline — in-game the loader has them loaded already, so this is purely the offline-host equivalent.
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    foreach (string d in refDirs)
    {
        string cand = Path.Combine(d, name.Name + ".dll");
        if (File.Exists(cand)) return ctx.LoadFromAssemblyPath(cand);
    }
    return null;
};

string work = Path.Combine(Path.GetTempPath(), "inutil-mods-test");
try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
Directory.CreateDirectory(work);
string marker = Path.Combine(work, "marker.txt");
string modDir = Path.Combine(work, "SampleMod");
Directory.CreateDirectory(modDir);

// A real mod: implements every lifecycle seam and records each firing to the marker file (observable across the
// mod's own ALC). The marker path is baked into the source (the mod has no other channel back to the test).
string src = """
using Inutil;
using System.IO;

public sealed class SampleLifecycleMod : ILoad, ITick, IUnload
{
    const string Marker = @"__MARKER__";
    public void OnLoad()   => File.AppendAllText(Marker, "load\n");
    public void Tick()     => File.AppendAllText(Marker, "tick\n");
    public void OnUnload() => File.AppendAllText(Marker, "unload\n");
}
""".Replace("__MARKER__", marker);
File.WriteAllText(Path.Combine(modDir, "SampleMod.cs"), src);

Console.WriteLine("── CsModCompiler + ModContext + Discover + FrameDriver + teardown (§7.9, offline) ──");

// (1) COMPILE from source.
var r = CsModCompiler.Compile("samplemod", new[] { Path.Combine(modDir, "SampleMod.cs") }, refDirs);
if (!r.Ok) foreach (string e in r.Errors) Console.WriteLine($"     compile error: {e}");
Check($"CsModCompiler compiles the mod (refs={r.RefCount})", r.Ok && r.Pe is { Length: > 0 });

if (r.Ok)
{
    // (2) LOAD the compiled bytes into a fresh collectible ALC (sharing the host's Inutil.dll identity).
    var alc = new ModContext("test:SampleMod");
    var asm = r.Pdb is { Length: > 0 }
        ? alc.LoadFromStream(new MemoryStream(r.Pe!), new MemoryStream(r.Pdb))
        : alc.LoadFromStream(new MemoryStream(r.Pe!));
    Check("compiled assembly loads into the collectible ModContext", asm.GetType("SampleLifecycleMod") is not null);

    // (3) DISCOVER — instantiates the lifecycle type and fires OnLoad.
    int before = Mods.Count;
    Mods.Discover(asm);
    Check("Discover registers the lifecycle mod (Mods.Count +1)", Mods.Count == before + 1);
    Check("OnLoad fired at Discover", ReadMarker(marker) == "load");

    // (4) FRAME — FrameDriver.Tick drives the mod's ITick.
    FrameDriver.Tick();
    Check("FrameDriver.Tick drove the compiled mod's ITick", ReadMarker(marker) == "load|tick");

    // (5) TEARDOWN — RemoveAll(alc) fires OnUnload + drops the mod; the ALC is then unloadable.
    int removed = Mods.RemoveAll(alc);
    Check("RemoveAll(alc) fired OnUnload + dropped the mod", removed == 1 && ReadMarker(marker) == "load|tick|unload");
    Check("Mods registry is empty again after teardown", Mods.Count == before);
    alc.Unload();
    Check("the mod's ALC unloads (collectible; host root dropped)", true);   // Unload() returned without throwing
}

// ── ALC-scoped concurrency teardown: coroutines + queued posts die with their generation ────────────────────
// A second real mod whose OnLoad leaks on purpose: an infinite coroutine (each step appends "co") and a queued
// MainThread post ("post"). Unloading the mod must stop the coroutine (it used to keep stepping old-generation
// code forever AND rooted the collectible ALC), drop its not-yet-drained post, and leave coroutines owned by
// OTHER contexts untouched. (Pins are the third scoped resource — il2cpp-native, validated in-game.)
Console.WriteLine("── ALC-scoped teardown (coroutines + posts die with the mod) ──");
{
    string marker2 = Path.Combine(work, "marker2.txt");
    string leakDir = Path.Combine(work, "LeakyMod");
    Directory.CreateDirectory(leakDir);
    string leakSrc = """
using Inutil;
using Inutil.Host;
using System.Collections;
using System.IO;

public sealed class LeakyMod : ILoad
{
    const string Marker = @"__MARKER2__";
    public void OnLoad()
    {
        Coroutines.Start(Loop());                                            // never stopped by the mod — the leak
        MainThread.Post(() => File.AppendAllText(Marker, "post\n"));
    }
    static IEnumerator Loop() { while (true) { File.AppendAllText(Marker, "co\n"); yield return null; } }
    // Called via reflection right before unload — a post queued by the dying generation must be DROPPED.
    public static void PostAgain() => MainThread.Post(() => File.AppendAllText(Marker, "late\n"));
}
""".Replace("__MARKER2__", marker2);
    File.WriteAllText(Path.Combine(leakDir, "LeakyMod.cs"), leakSrc);

    var r2 = CsModCompiler.Compile("leakymod", new[] { Path.Combine(leakDir, "LeakyMod.cs") }, refDirs);
    if (!r2.Ok) foreach (string e in r2.Errors) Console.WriteLine($"     compile error: {e}");
    Check($"leaky mod compiles (refs={r2.RefCount})", r2.Ok && r2.Pe is { Length: > 0 });
    if (r2.Ok)
    {
        var alc2 = new ModContext("test:LeakyMod");
        var asm2 = alc2.LoadFromStream(new MemoryStream(r2.Pe!));
        Mods.Discover(asm2);

        // A host-owned (default-ALC) coroutine that must SURVIVE the mod's teardown — proves the scope.
        int hostSteps = 0;
        System.Collections.IEnumerator HostLoop() { while (true) { hostSteps++; yield return null; } }
        Coroutine hostCo = Coroutines.Start(HostLoop());

        FrameDriver.Tick();                                   // drains the post; merges + first-steps both coroutines
        FrameDriver.Tick();
        string whileLoaded = ReadMarker(marker2);
        Check("mod's posted action ran + its coroutine steps while loaded",
              whileLoaded.StartsWith("post|co") && whileLoaded.Contains("co|co"));

        asm2.GetType("LeakyMod")!.GetMethod("PostAgain")!.Invoke(null, null);   // queue from the dying generation
        Mods.RemoveAll(alc2);
        string atUnload = ReadMarker(marker2);
        FrameDriver.Tick();
        FrameDriver.Tick();
        Check("unload STOPPED the mod's coroutine (zero steps after RemoveAll)", ReadMarker(marker2) == atUnload);
        Check("unload DROPPED the mod's queued post (no 'late' ever runs)", !ReadMarker(marker2).Contains("late"));
        Check("a host-owned coroutine keeps running (teardown is ALC-scoped, not global)",
              hostSteps >= 3 && hostCo.IsRunning);
        hostCo.Stop();
        FrameDriver.Tick();                                   // reap it so later blocks see a clean scheduler
        alc2.Unload();
        Check("the leaky mod's ALC unloads after the scoped teardown", true);
    }
}

// ── IConfigure + ModConfig (cfg dialect, live re-apply, ALC-scoped teardown) ──────────────────────
// A real compiled mod reads typed keys with code defaults + descriptions. Asserted: Configure fires BEFORE
// OnLoad; the file materializes self-documented in the BepInEx cfg dialect (the launcher's structured editor
// contract); an on-disk edit re-fires Configure LIVE through the FrameDriver poll — no recompile, no ALC
// churn; an unparsable value falls back to the code default while the file keeps the user's text; unknown
// keys survive re-materialization; and an unloaded generation's Configure never re-fires.
Console.WriteLine("── IConfigure + ModConfig (cfg dialect, live re-apply, ALC-scoped) ──");
{
    ModConfigStore.Root = Path.Combine(work, "config");
    ModConfigStore.PollIntervalMs = 0;                        // tests poll on every Tick

    string marker3 = Path.Combine(work, "marker3.txt");
    string cfgModDir = Path.Combine(work, "CfgMod");
    Directory.CreateDirectory(cfgModDir);
    string cfgSrc = """
using Inutil;
using System.Globalization;
using System.IO;

public sealed class CfgMod : IConfigure, ILoad
{
    const string Marker = @"__M3__";
    public void Configure(ModConfig cfg)
    {
        string greeting = cfg.GetString("greeting", "hello", "the greeting line");
        bool loud = cfg.GetBool("loud", false, "shout it");
        int count = cfg.GetInt("count", 3);
        float speed = cfg.GetFloat("speed", 1.5f);
        File.AppendAllText(Marker, $"cfg:{greeting}:{loud}:{count}:{speed.ToString(CultureInfo.InvariantCulture)}\n");
    }
    public void OnLoad() => File.AppendAllText(Marker, "load\n");
}
""".Replace("__M3__", marker3);
    File.WriteAllText(Path.Combine(cfgModDir, "CfgMod.cs"), cfgSrc);

    var r3 = CsModCompiler.Compile("cfgmod", new[] { Path.Combine(cfgModDir, "CfgMod.cs") }, refDirs);
    if (!r3.Ok) foreach (string e in r3.Errors) Console.WriteLine($"     compile error: {e}");
    Check("config mod compiles", r3.Ok && r3.Pe is { Length: > 0 });
    if (r3.Ok)
    {
        var alc3 = new ModContext("test:CfgMod");
        var asm3 = alc3.LoadFromStream(new MemoryStream(r3.Pe!));
        Mods.Discover(asm3);
        Check("Configure fires BEFORE OnLoad, with the code defaults", ReadMarker(marker3) == "cfg:hello:False:3:1.5|load");

        string cfgPath = Path.Combine(ModConfigStore.Root!, "cfgmod.cfg");
        string file = File.ReadAllText(cfgPath);
        Check("file materializes self-documented in the cfg dialect",
              file.Contains("[cfgmod]") && file.Contains("## the greeting line")
              && file.Contains("# Setting type: String") && file.Contains("# Default value: hello")
              && file.Contains("greeting = hello") && file.Contains("# Setting type: Boolean")
              && file.Contains("speed = 1.5"));

        // Live edit: change a value, break one, add an unknown key -> ONE FrameDriver.Tick re-fires Configure.
        File.WriteAllText(cfgPath, "[cfgmod]\ngreeting = bonjour\nloud = not-a-bool\ncount = 7\nmystery = 42\n");
        FrameDriver.Tick();
        Check("an on-disk edit re-fires Configure LIVE (new values, unparsable -> default, no ALC churn)",
              ReadMarker(marker3).EndsWith("cfg:bonjour:False:7:1.5"));
        string file2 = File.ReadAllText(cfgPath);
        Check("re-materialize preserves the user's unknown key", file2.Contains("mystery = 42"));
        Check("re-materialize keeps the user's unparsable text for fixing", file2.Contains("loud = not-a-bool"));

        Mods.RemoveAll(alc3);
        string atUnload3 = ReadMarker(marker3);
        File.WriteAllText(cfgPath, "[cfgmod]\ngreeting = ghost\n");
        FrameDriver.Tick();
        Check("unload retires the watcher (no Configure into a dead ALC)", ReadMarker(marker3) == atUnload3);
        alc3.Unload();

        // Friendly-name derivation for the FALLBACK path (no explicit host name): the mod host mints
        // assemblies as `inutilmod_<name>` / `inutilcore_<name>`, and the config filename must read as the
        // name the USER knows, not the mangled compile name. Compile under a prefixed assembly name and
        // Discover WITHOUT a friendly name — the file must be the stripped <name>.cfg.
        string cfgDirNow = ModConfigStore.Root!;
        string pfxDir = Path.Combine(work, "PfxMod");
        Directory.CreateDirectory(pfxDir);
        File.WriteAllText(Path.Combine(pfxDir, "PfxMod.cs"),
            "public sealed class PfxMod : Inutil.IConfigure { public void Configure(Inutil.ModConfig c) { c.GetInt(\"n\", 1); } }");
        var rp = CsModCompiler.Compile("inutilmod_zebra", new[] { Path.Combine(pfxDir, "PfxMod.cs") }, refDirs);
        if (rp.Ok)
        {
            var alcp = new ModContext("test:PfxMod");
            var pasm = alcp.LoadFromStream(new MemoryStream(rp.Pe!));
            Mods.Discover(pasm);   // no friendly name -> fallback strips the prefix
            Check("fallback strips the framework compile prefix (inutilmod_zebra -> zebra.cfg)",
                  File.Exists(Path.Combine(cfgDirNow, "zebra.cfg")) && !File.Exists(Path.Combine(cfgDirNow, "inutilmod_zebra.cfg")));
            Mods.RemoveAll(AssemblyLoadContext.GetLoadContext(pasm)!);
            alcp.Unload();
        }
    }
    ModConfigStore.PollIntervalMs = 1000;                     // restore for later blocks
}

// ── HookMatch: the offline matcher + fail-loud diagnostic (GAPS Gap 1 / P4) ─────────────────────────────────
// Pure reflection over plain stand-in classes, so the Discover DECISION logic is driven WITHOUT an il2cpp frame
// (the install half needs a game; the match half does not). Each fallback-matcher case adds asserts here first
// (red), then the matcher (green); the in-game ModHostCases confirms the rescued hook actually fires.
Console.WriteLine("── HookMatch (offline matcher + diagnostic) ──");
{
    Type game = typeof(FakeGame);
    MethodInfo hookTallyInt    = typeof(FakeHook).GetMethod("Tally", new[] { typeof(int) })!;
    MethodInfo hookTallyString = typeof(FakeHook).GetMethod("Tally", new[] { typeof(string) })!;
    MethodInfo hookHelper      = typeof(FakeHook).GetMethod("HelperNotOnGame", BindingFlags.NonPublic | BindingFlags.Instance)!;
    MethodInfo hookTypo        = typeof(FakeHook).GetMethod("Taly", new[] { typeof(int) })!;

    // Tier 0 — exact name + parameter types binds (proves the extraction preserved Discover's behavior).
    Check("Resolve binds an exact name+type match", HookMatch.Resolve(game, hookTallyInt)?.Name == "Tally");
    // A same-name method with a DIFFERENT signature does not bind — the silent gap the diagnostic covers.
    Check("Resolve returns null on a signature mismatch", HookMatch.Resolve(game, hookTallyString) is null);

    // Tier 1 — container interface widening (GAPS Gap 1 "nested/concrete-container"): a hook may spell a READ-ONLY
    // supertype (IReadOnlyList/IEnumerable) of the flipped proxy's concrete container (List), and the engine
    // marshals the il2cpp list into that spelling (Families.cs registers both as read targets). Bound ONLY when
    // the element types are IDENTICAL and the hook spelling is a container the engine actually knows — never a
    // loose IsAssignableFrom (which would wrongly match IEnumerable<object> to a List<string>, or bind a container
    // the marshaller can't produce like ICollection<T>).
    MethodInfo musterRO   = typeof(FakeHook).GetMethod("Muster", new[] { typeof(IReadOnlyList<int>) })!;
    MethodInfo musterEnum = typeof(FakeHook).GetMethod("Muster", new[] { typeof(IEnumerable<int>) })!;
    MethodInfo musterMut  = typeof(FakeHook).GetMethod("Muster", new[] { typeof(IList<int>) })!;
    MethodInfo musterColl = typeof(FakeHook).GetMethod("Muster", new[] { typeof(ICollection<int>) })!;
    MethodInfo setInvRO   = typeof(FakeHook).GetMethod("SetInventory", new[] { typeof(IReadOnlyList<string>) })!;
    MethodInfo setInvObj  = typeof(FakeHook).GetMethod("SetInventory", new[] { typeof(IEnumerable<object>) })!;

    Check("Resolve widens List<int> -> IReadOnlyList<int> hook", HookMatch.Resolve(game, musterRO)?.Name == "Muster");
    Check("Resolve widens List<int> -> IEnumerable<int> hook", HookMatch.Resolve(game, musterEnum)?.Name == "Muster");
    Check("Resolve widens List<string> -> IReadOnlyList<string> hook", HookMatch.Resolve(game, setInvRO)?.Name == "SetInventory");
    Check("Resolve binds the write-through IList<int> spelling to List<int> (MutableList)", HookMatch.Resolve(game, musterMut)?.Name == "Muster");
    Check("Resolve does NOT widen to an unregistered container (ICollection<int>)", HookMatch.Resolve(game, musterColl) is null);
    Check("Resolve does NOT widen across element types (IEnumerable<object> vs List<string>)", HookMatch.Resolve(game, setInvObj) is null);

    // Tier 2 — explicit interface impl (GAPS Gap 1 "interface-implementor"): Il2CppInterop FLATTENS interfaces, so
    // an explicit impl `Ns.IFoo.Bar` lands on the proxy as a mangled public method `Ns_IFoo_Bar` (verified against
    // the real ToyGame proxy: ToyGame_ITicker_Tick). A hook names the PLAIN method `Bar`; the matcher maps it by
    // the Il2CppInterop mangling SHAPE (ends `_Bar`, preceding `_`-segment is an interface name I+Upper) + exact
    // signature. Only the shape binds — a non-interface `_Bar` does not — and two candidates never silently guess.
    MethodInfo hookTick    = typeof(FakeHook).GetMethod("Tick", Type.EmptyTypes)!;
    MethodInfo hookTickInt = typeof(FakeHook).GetMethod("Tick", new[] { typeof(int) })!;
    Check("Resolve maps plain Tick() -> the mangled explicit impl", HookMatch.Resolve(game, hookTick)?.Name == "FakeNs_IFakeTicker_Tick");
    Check("interface-impl needs the I<Name> mangling shape (Helper_Tick does not bind)", HookMatch.Resolve(typeof(FakeNoIface), hookTick) is null);
    Check("interface-impl needs an exact signature (Tick(int) does not map to the 0-arg impl)", HookMatch.Resolve(game, hookTickInt) is null);
    Check("interface-impl is unique-or-null (two mangled _Tick candidates -> no silent guess)", HookMatch.Resolve(typeof(FakeAmbigProxy), hookTick) is null);

    // Tier 3 — generic-method instantiation inference (GAPS Gap 1 "generic-inference"): a CONCRETE hook
    // `int Echo(int)` names a specific instantiation of the game's generic `Echo<T>(T)`; the matcher infers the
    // type args from the concrete signature (T=int), inflates `Echo<int>`, and binds that closed instantiation
    // (the install then resolves its native mi + hooks it). Inference is per-instantiation (int vs long), respects
    // arity, and is verified by re-inflating and exact-matching the signature.
    MethodInfo hookEchoInt  = typeof(FakeHook).GetMethod("Echo", new[] { typeof(int) })!;
    MethodInfo hookEchoLong = typeof(FakeHook).GetMethod("Echo", new[] { typeof(long) })!;
    MethodInfo hookEchoBad  = typeof(FakeHook).GetMethod("Echo", new[] { typeof(int), typeof(int) })!;
    MethodInfo? echoIntM  = HookMatch.Resolve(game, hookEchoInt);
    MethodInfo? echoLongM = HookMatch.Resolve(game, hookEchoLong);
    Check("Resolve infers Echo<int> from concrete Echo(int)",
          echoIntM is { IsGenericMethod: true, Name: "Echo" } && echoIntM.GetGenericArguments()[0] == typeof(int));
    Check("generic-inference is per-instantiation (Echo(long) -> Echo<long>, not Echo<int>)",
          echoLongM is { IsGenericMethod: true } && echoLongM.GetGenericArguments()[0] == typeof(long));
    Check("generic-inference respects arity (Echo(int,int) does not match Echo<T>(T))", HookMatch.Resolve(game, hookEchoBad) is null);
    // Reference-type instantiations bind too — the install resolves the SAME native mi the raw InflateNative tier
    // uses (proven identical in-game), and a non-inlined call routes to it (the only caveat is the universal
    // inlining boundary that applies to every hook, not a generic-specific silent failure).
    MethodInfo hookEchoStr = typeof(FakeHook).GetMethod("Echo", new[] { typeof(string) })!;
    MethodInfo? echoStrM = HookMatch.Resolve(game, hookEchoStr);
    Check("generic-inference binds a reference-type instantiation (Echo<string> -> Echo<string>)",
          echoStrM is { IsGenericMethod: true } && echoStrM.GetGenericArguments()[0] == typeof(string));

    // Tier 2.5 — mirror params (Inutil.Bridge.Callback<T>): a hook RECEIVES a game reference-wrapper (Comfort.Common
    // .Callback<T'>) it can't spell (nor bind by the tiers above); it spells inutil's mirror Inutil.Bridge.Callback<T>
    // and the seam MATERIALIZES the game callback into it (validated in-game). The mirror<->game correspondence is
    // recognized HERE — the game callback param can't be interop-patch-flipped (it's a GAME type). Gated on the per-game
    // Mirror.RegisterCallback open + >=1 mirror param + element correspondence; an IDENTITY element keeps this il2cpp-free.
    MethodInfo hookFetch = typeof(FakeHook).GetMethod("Fetch")!;
    Check("mirror-param does NOT bind before RegisterCallback (no game open registered)", HookMatch.Resolve(game, hookFetch) is null);
    Inutil.Bridge.Mirror.RegisterCallback(typeof(FakeCallback<>), (_, _) => { });
    Check("mirror-param binds Inutil.Bridge.Callback<T> hook -> game FakeCallback<T'> after RegisterCallback",
          HookMatch.Resolve(game, hookFetch)?.Name == "Fetch");

    // DelegateIn — the inbound-delegate mirror (the GENERAL twin of Callback): a hook RECEIVES a game System.Action/Func
    // param (the interop proxy exposes a delegate param as a NATURAL System.Action<Il2CppElem>) it can't receive as a
    // System.Action (no reverse bridge — ConvKind.Delegate fails loud). It spells Inutil.Bridge.Action/Func<..> and binds
    // HERE — UNLIKE Callback there is NO per-game shim (the game open IS the BCL System delegate), so it binds with no
    // registration, on same-arity + element correspondence (identity here). A Func mirror's LAST arg is the return.
    MethodInfo hookSubscribe = typeof(FakeHook).GetMethod("Subscribe")!;
    Check("DelegateIn: Inutil.Bridge.Action<T> hook binds game System.Action<T> param (no shim needed)",
          HookMatch.Resolve(game, hookSubscribe)?.Name == "Subscribe");
    MethodInfo hookCompute = typeof(FakeHook).GetMethod("Compute")!;
    Check("DelegateIn: Inutil.Bridge.Func<T,R> hook binds game System.Func<T,R> param",
          HookMatch.Resolve(game, hookCompute)?.Name == "Compute");

    // Mirror-element NESTING: a generic arg inside a game type's close is never interop-patch-flipped, so a game
    // Callback<Dictionary<...>> reflects with the RAW il2cpp container as its element (serverstub's insurance cost
    // quote: Callback<Dictionary<string, Dictionary<string, int>>>). The mirror spells the natural BCL containers and
    // ElementCorresponds recurses through NaturalContainerBindsRaw (registry anchor per level) — and REJECTS a nested
    // element mismatch (inner int vs string) rather than widening loosely.
    MethodInfo hookQuote = typeof(FakeHook).GetMethod("Quote")!;
    Check("mirror-param binds the FULL cost-quote shape (string[] x2 widened + Callback<Dictionary<string, Dictionary<string,int>>> nested mirror, ONE signature)",
          HookMatch.Resolve(game, hookQuote)?.Name == "Quote");
    MethodInfo hookQuoteBad = typeof(FakeHook).GetMethod("QuoteBad")!;
    Check("mirror-param REJECTS a nested element mismatch (inner Dictionary<string,string> vs <string,int>)",
          HookMatch.Resolve(game, hookQuoteBad) is null);

    // raw-proxy (GAPS Gap 1): "name a proxy method the expression API can't reference". That limitation was the
    // EXPRESSION selector's (public-only); Discover matches BY NAME and Tier 0 already searches Public|NonPublic,
    // so a non-public game method binds by name+signature with no separate matcher — the raw-proxy case IS Tier 0.
    MethodInfo hookHidden = typeof(FakeHook).GetMethod("Hidden", new[] { typeof(int) })!;
    Check("Tier 0 binds a NON-PUBLIC game method by name+signature (raw-proxy is Tier-0 territory)",
          HookMatch.Resolve(game, hookHidden)?.Name == "Hidden");

    // THE DIAGNOSTIC. A signature mismatch (the name IS on the proxy) is an intended hook that mis-typed its
    // parameters: warn, and name both the hook method and the available game signature so it is fixable.
    // Visibility declares intent for the name-absent case: a PUBLIC method that matched nothing is an intended
    // hook (typo, or the game renamed it) — warn with the fix; a private/internal method is a genuine helper —
    // silent. And a hook that DID bind never warns.
    string? warn = HookMatch.Diagnose(game, hookTallyString);
    Check("Diagnose WARNS on a name-collision signature mismatch", warn is not null);
    Check("Diagnose names the hook method + the available game signature",
          warn is not null && warn.Contains("Tally") && warn.Contains("Int32") && warn.Contains(nameof(FakeGame)));
    Check("Diagnose stays SILENT for a PRIVATE helper whose name is absent on the proxy", HookMatch.Diagnose(game, hookHelper) is null);
    string? typoWarn = HookMatch.Diagnose(game, hookTypo);
    Check("Diagnose WARNS for a PUBLIC method whose name is absent (typo / game rename — the silent-miss killer)",
          typoWarn is not null && typoWarn.Contains("matched NOTHING") && typoWarn.Contains("private"));
    Check("Diagnose stays SILENT when the hook DID bind (no false alarm)", HookMatch.Diagnose(game, hookTallyInt) is null);

    // BIND-TIME RETURN VALIDATION — the Proceed<Task> compile-clean/runtime-throw seam. A managed Task/Task<T>
    // declared against an UNFLIPPED il2cpp Task proxy must be rejected at Discover with the fix in the message;
    // a flipped (managed-identical) Task return and a faithfully il2cpp-spelled return both pass; and non-Task
    // returns are out of this validator's scope (the matcher's correspondence tiers own those shapes).
    MethodInfo gLoadRaw     = typeof(FakeGame).GetMethod("LoadRaw")!;         // returns FakeIl2CppTask (unflipped stand-in)
    MethodInfo gLoadFlipped = typeof(FakeGame).GetMethod("LoadFlipped")!;     // returns managed Task (flipped stand-in)
    MethodInfo hLoadManaged = typeof(FakeHook).GetMethod("LoadRaw")!;         // declares managed Task
    MethodInfo hLoadGenT    = typeof(FakeHook).GetMethod("LoadRawT")!;        // declares managed Task<int>
    MethodInfo hLoadIl2     = typeof(FakeHook).GetMethod("LoadRawIl2")!;      // declares the il2cpp spelling
    string? taskErr = HookMatch.ValidateReturn(game, gLoadRaw, hLoadManaged);
    Check("ValidateReturn REJECTS managed Task vs an unflipped il2cpp Task return",
          taskErr is not null && taskErr.Contains("MissingMethodException") && taskErr.Contains("il2cpp Task"));
    Check("ValidateReturn REJECTS managed Task<T> vs an unflipped il2cpp Task return",
          HookMatch.ValidateReturn(game, gLoadRaw, hLoadGenT) is not null);
    Check("ValidateReturn passes a FLIPPED (managed) Task return", HookMatch.ValidateReturn(game, gLoadFlipped, hLoadManaged) is null);
    Check("ValidateReturn passes an il2cpp-spelled Task return (faithful spelling)", HookMatch.ValidateReturn(game, gLoadRaw, hLoadIl2) is null);
    Check("ValidateReturn ignores non-Task returns (out of scope)",
          HookMatch.ValidateReturn(game, typeof(FakeGame).GetMethod("GetHealth")!, hookTallyInt) is null);

    // INTEGRATION — drive Discover end-to-end on a real Hook<T> whose one method mis-matches. The no-match branch
    // does NO il2cpp install (it warns and continues), so this runs offline and is the honest gate for the
    // diagnostic: it proves WireHookClass actually routes Diagnose -> Hooks.OnWarning, not just that Diagnose can
    // build a string. (A rescued hook that must FIRE needs the game — that is ModHostCases, per matcher.)
    var warnings = new List<string>();
    Action<string>? prev = Inutil.Hooks.Hooks.OnWarning;
    Inutil.Hooks.Hooks.OnWarning = warnings.Add;
    try
    {
        int w = Mods.Discover(Assembly.GetExecutingAssembly(), t => t == typeof(FakeGameHook));
        Check("Discover wires 0 for a hook whose methods all mis-match", w == 0);
        Check("Discover routes the diagnostic through Hooks.OnWarning", warnings.Any(x => x.Contains("Tally")));
        Check("Discover routes the public-name-absent (typo/rename) warning too", warnings.Any(x => x.Contains("matched NOTHING") && x.Contains("Tallyy")));

        // A lifecycle method (ITick.Tick) must NEVER be hook-matched — even though FakeGame has a mangled
        // FakeNs_IFakeTicker_Tick the interface-impl tier would otherwise bind. Without the WireHookClass exclusion
        // Discover would try to INSTALL that hook (il2cpp) and throw here in the offline gate; wiring 0 cleanly
        // proves the exclusion (the class is `Hook<FakeGame>, ITick`, so a false Tier-2 match is the failure mode).
        int wLife = Mods.Discover(Assembly.GetExecutingAssembly(), t => t == typeof(FakeLifecycleHook));
        Check("Discover does not hook an ITick lifecycle Tick() (no false interface-impl match)", wLife == 0);
        Mods.RemoveAll(AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly()) ?? AssemblyLoadContext.Default);
    }
    finally { Inutil.Hooks.Hooks.OnWarning = prev; }
}

// ── MutableList: the write-through IList<T> adapter ───────────────────────────
// The adapter forwards every op reflectively to the held list through the SAME member surface an il2cpp List
// proxy exposes (Count/Item/Add/Insert/RemoveAt/Clear/IndexOf/Remove/Contains) — so a BCL List<int> stands in
// offline and the whole classify -> wrap -> mutate-through -> identity-return path runs in the fast gate; only
// the real il2cpp element conversion is in-game (leaf int is identity here, exactly as a game proxy element is).
Console.WriteLine("── MutableList (write-through IList<T> adapter) ──");
{
    var runtime = new Il2CppConvRuntime();
    Conv conv = Conv.Build(typeof(IList<int>), new Il2CppConvShapeSource());
    Check("IList<int> classifies MutableList with one leaf child",
        conv.Kind == ConvKind.MutableList && conv.Children.Count == 1 && conv.Children[0].Identity);

    var game = new List<int> { 1, 2, 3 };                    // stands in for the game's il2cpp List proxy
    object view = conv.Convert(game, ConvDirection.ToManaged, runtime)!;
    Check("ToManaged mints the live adapter (an IList<int> carrier, not a copy)",
        view is IList<int> && view is IIl2CppListCarrier);

    var list = (IList<int>)view;
    Check("reads go through the held list (Count / indexer)", list.Count == 3 && list[1] == 2);
    list.Add(4); list.Remove(2); list[0] = 9; list.Insert(1, 7);
    Check("writes go THROUGH to the held list (Add/Remove/set/Insert land on the game's list)",
        game.SequenceEqual(new[] { 9, 7, 3, 4 }));
    Check("membership asks the held list (IndexOf/Contains)",
        list.IndexOf(3) == 2 && list.Contains(4) && !list.Contains(2));
    Check("enumeration reads the live list", list.ToArray().SequenceEqual(new[] { 9, 7, 3, 4 }));

    object back = conv.Convert(view, ConvDirection.ToIl2Cpp, runtime)!;
    Check("ToIl2Cpp of the adapter is IDENTITY (Proceed hands the game its OWN list)", ReferenceEquals(back, game));

    // A mod-FABRICATED IList (no carrier) takes the dematerialize-as-List`1 fallback, which must resolve the
    // il2cpp List proxy — absent offline, so it fails LOUD at resolution (the documented R1 behavior), never
    // silently passing a managed list where an il2cpp one belongs.
    try
    {
        conv.Convert(new List<int> { 5 }, ConvDirection.ToIl2Cpp, runtime);
        Check("fabricated IList offline fails LOUD at il2cpp proxy resolution", false);
    }
    catch (NotSupportedException) { Check("fabricated IList offline fails LOUD at il2cpp proxy resolution", true); }
}

// ── REPL engine + TCP transport ─────────────────────────────────────────────────────────────────────
// Pure-managed, so the whole Roslyn scripting + main-thread-dispatch path runs offline: a submission with no
// il2cpp touch (40+2) compiles + runs under Roslyn, and MainThread's queue is a plain queue we drive by Drain.
// The IL2CPP payoff (a REPL line hooking a live game method) is the in-game battery's job; here we lock the
// mechanics: evaluation, state chaining, error capture, the deadlock guard, and the socket round-trip.
Console.WriteLine("── REPL engine + transport (§7.12) ──");
{
    // Mark THIS thread as the main thread (Drain records it) so the guard's OnMainThread check is live and
    // IsPumping is true — the same state the game establishes on its first frame tick.
    MainThread.Drain();

    var repl = new ReplSession(proxyNamespace: "");   // no game proxy offline — pure C# submissions
    ReplResult a = repl.Eval("40 + 2");
    Check("Eval evaluates arbitrary C# (40+2 -> 42)", a.Ok && a.Value is int av && av == 42);

    repl.Eval("var bonus = 4242;");
    ReplResult b = repl.Eval("bonus + 1");
    Check("Eval chains session state across submissions (bonus+1 -> 4243)", b.Ok && b.Value is int bv && bv == 4243);

    ReplResult bad = repl.Eval("this is not valid c#");
    Check("Eval captures a compile error (Ok=false, Error set)", !bad.Ok && !string.IsNullOrEmpty(bad.Error));
    ReplResult afterBad = repl.Eval("bonus + 2");
    Check("a failed submission does not advance state (bonus still visible -> 4244)", afterBad.Ok && afterBad.Value is int cv && cv == 4244);

    // deadlock guard: a submission that does not complete SYNCHRONOUSLY on the main thread (it awaits an incomplete
    // task) must fail LOUD, not block the thread forever — the deadlock shape a real `await MainThread.NextFrame()`
    // would take in-game. A never-completing task is the offline-reliable stand-in: the guard's condition (incomplete
    // task + main thread) is identical, so this exercises exactly the code path that prevents the in-game hang.
    ReplResult awaited = repl.Eval("await new System.Threading.Tasks.TaskCompletionSource<int>().Task; 99");
    Check("deadlock guard: an unfinished submission on the main thread fails loud (not hangs)",
          !awaited.Ok && awaited.Error is string ge && ge.Contains("synchronous") && ge.Contains("await"));

    // TCP transport: a line sent over a loopback socket is dispatched to the main thread, evaluated, and its
    // result comes back. We ARE the main thread here, so we Drain to run the dispatched Eval while polling the socket.
    using var server = ReplServer.Start(0, "");
    Check("ReplServer binds a loopback port", server.Port > 0);
    using var client = new TcpClient();
    client.Connect(IPAddress.Loopback, server.Port);
    NetworkStream ns = client.GetStream();
    var w = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    w.WriteLine("40 + 2");
    string got = PumpForNeedle(ns, "42", () => MainThread.Drain(), 5000);
    Check("TCP transport: '40 + 2' round-trips to '42' via main-thread dispatch", got.Contains("42"));

    // HTTP transport: the same submission POSTed to /eval answers with the {"ok":..,"result":..} JSON body —
    // the script-facing curl/jq contract. Same pump dance (we ARE the main thread). Also proves the session is
    // LAZY (created on first eval, not Start) and chains state across independent POSTs like one telnet session.
    using var http = ReplHttpServer.Start(0, "");
    Check("ReplHttpServer binds a loopback port", http.Port > 0);
    string h1 = HttpEvalPumped(http.Port, "var stateChain = 40; stateChain + 2", () => MainThread.Drain());
    Check("HTTP transport: POST /eval '40 + 2' answers {\"ok\":true,\"result\":\"42\"}",
          h1.Contains("\"ok\":true") && h1.Contains("\"result\":\"42\""));
    string h2 = HttpEvalPumped(http.Port, "stateChain + 3", () => MainThread.Drain());
    Check("HTTP transport: state chains across POSTs (one session for the server)",
          h2.Contains("\"ok\":true") && h2.Contains("\"result\":\"43\""));
    string h3 = HttpEvalPumped(http.Port, "not valid c#", () => MainThread.Drain());
    Check("HTTP transport: a bad submission answers ok:false with the compile error",
          h3.Contains("\"ok\":false") && h3.Contains("error"));
}

// ── Diagnostics: the game-free compile/load outcome store the human-facing renderer polls ───────────────────
// Pull semantics (Since/Sequence), latest-wins ActiveFailures (a success clears a mod's failure), and the
// bounded tail. The game-side toast renderer (moderrors coremod) is in-game only; here we pin the store's
// contract the renderer stands on.
Console.WriteLine("── Diagnostics (compile/load outcome store) ──");
{
    long baseline = Diagnostics.Sequence;
    Diagnostics.RecordFailure("alpha", "compile-error", "CS0103 the name 'x' does not exist");
    Diagnostics.RecordFailure("beta", "load-failed", "InvalidOperationException: boom");
    var fresh = Diagnostics.Since(baseline);
    Check("Since(seq) returns the new events oldest-first", fresh.Count == 2 && fresh[0].Mod == "alpha" && !fresh[0].Ok);
    Check("Sequence advances monotonically", Diagnostics.Sequence == baseline + 2);
    Check("Since is exclusive of already-seen (a renderer polls only new)", Diagnostics.Since(Diagnostics.Sequence).Count == 0);

    Check("ActiveFailures lists both broken mods", Diagnostics.ActiveFailures().Count(e => e.Mod is "alpha" or "beta") == 2);
    Diagnostics.RecordSuccess("alpha");                 // alpha's edit now takes
    var active = Diagnostics.ActiveFailures();
    Check("a later success clears that mod's failure (latest-wins)",
          active.All(e => e.Mod != "alpha") && active.Any(e => e.Mod == "beta"));
    Check("the success is observable via Since (renderer clears its banner)",
          Diagnostics.Since(baseline + 2).Any(e => e.Mod == "alpha" && e.Ok));
}

// ── the three named lifecycles + host surface exist and are distinct types (name, don't merge) ───────
Check("Coremods is a distinct load-once lifecycle type", typeof(Coremods).GetMethod("LoadSync") is not null);
Check("ModLibs is a distinct shared-source lifecycle type", typeof(ModLibs).GetMethod("LoadSync") is not null);
Check("CsModHost is the distinct hot-reload lifecycle type", typeof(CsModHost).GetMethod("Start") is not null);

try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }

Console.WriteLine(fail == 0
    ? "\nINUTIL MODS TESTS GREEN — compile -> collectible-ALC load -> Discover -> FrameDriver -> teardown, on a real compiled mod."
    : $"\nINUTIL MODS TESTS RED — {fail} wrong.");
return fail == 0 ? 0 : 1;

// The marker file's lines joined with '|' (trimmed) — the mod's cross-ALC record of which seams fired, in order.
static string ReadMarker(string path)
{
    try { return string.Join("|", File.ReadAllLines(path).Where(l => l.Length > 0)); }
    catch { return ""; }
}

// One POST /eval round-trip against the HTTP transport, driving `pump` while waiting (the dispatched eval runs
// on THIS thread). Returns everything received (headers + JSON body) for the caller to assert on.
static string HttpEvalPumped(int port, string code, Action pump)
{
    using var hc = new TcpClient();
    hc.Connect(IPAddress.Loopback, port);
    NetworkStream hs = hc.GetStream();
    byte[] payload = new UTF8Encoding(false).GetBytes(code);
    byte[] head = System.Text.Encoding.ASCII.GetBytes($"POST /eval HTTP/1.1\r\nHost: repl\r\nContent-Length: {payload.Length}\r\n\r\n");
    hs.Write(head, 0, head.Length); hs.Write(payload, 0, payload.Length); hs.Flush();
    return PumpForNeedle(hs, "}", pump, 5000);   // the body's closing brace — headers carry none, so this is body-complete
}

// Poll a socket stream for `needle`, driving `pump` each iteration (so a main-thread-dispatched eval actually
// runs on this thread) and accumulating received bytes, until the buffer contains it or the timeout elapses.
static string PumpForNeedle(NetworkStream ns, string needle, Action pump, int timeoutMs)
{
    ns.ReadTimeout = 30;
    var sb = new StringBuilder();
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs && !sb.ToString().Contains(needle))
    {
        pump();
        try { int c = ns.ReadByte(); if (c >= 0) sb.Append((char)c); }
        catch (IOException) { /* read timeout — nothing available yet, keep pumping */ }
        catch (SocketException) { break; }
        System.Threading.Thread.Sleep(3);
    }
    return sb.ToString();
}

// ── offline matcher fixtures ────────────────────────────────────────────────────────────────────────────────
// Plain stand-ins for a game proxy + a hook class. HookMatch takes raw Types/MethodInfos, so no Hook<T> and no
// il2cpp are needed to exercise the decision logic — the point of splitting the matcher out of the install.
class FakeGame
{
    public void Tally(int n) { }
    public int GetHealth() => 0;
    public void SetInventory(List<string> items) { }        // concrete container param (as the flip leaves it)
    public int Muster(List<int> squad) => squad.Count;      // concrete container param, int (Count) return
    public void FakeNs_IFakeTicker_Tick() { }               // stands in for Il2CppInterop's explicit-impl mangling
    public T Echo<T>(T input) => input;                     // generic method — instantiations inferred from concrete hooks
    public FakeIl2CppTask LoadRaw() => null!;               // UNFLIPPED il2cpp Task return stand-in (ValidateReturn rejects a managed-Task hook)
    public System.Threading.Tasks.Task LoadFlipped() => null!;   // FLIPPED Task return stand-in (managed spelling — bridged, passes)
    internal void Hidden(int n) { }                         // NON-PUBLIC — the raw-proxy case, bound by Tier 0's NonPublic search
    public void Fetch(bool refresh, FakeCallback<FakeItem> cb) { }   // game callback param — the mirror-param (Tier 2.5) case
    public void Subscribe(int id, System.Action<FakeItem> onItem) { }   // natural System.Action delegate param — the DelegateIn (Tier 2.5) case (serverstub's System.Action<Callback>)
    public void Compute(System.Func<int, FakeItem> make) { }            // natural System.Func delegate param — the Func-mirror DelegateIn case
    // game callback whose ELEMENT is a raw il2cpp container close (nested), BESIDE raw il2cpp string-array params —
    // the FULL insurance cost-quote shape: the widened-array params and the mirror param must bind in ONE signature
    // (they straddled the tiers and bound nowhere before MatchMirrorParams accepted ParamBinds params).
    public void Quote(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray traders,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray items,
        FakeCallback<Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.Dictionary<string, int>>> cb) { }
    public void QuoteBad(FakeCallback<Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Collections.Generic.Dictionary<string, string>>> cb) { }
}

class FakeHook
{
    public void Tally(int n) { }              // exact match to FakeGame.Tally(int)
    public void Tally(string wrong) { }       // name collides with FakeGame.Tally, signature differs -> diagnostic
    private void HelperNotOnGame(int n) { }   // name absent + PRIVATE -> a genuine helper, stays silent (the convention)
    public void Taly(int n) { }               // name absent + PUBLIC -> an intended hook that typo'd (or the game renamed) -> warns

    // ValidateReturn spellings against FakeGame.LoadRaw/LoadFlipped (the Proceed<Task> bind-time seam)
    public System.Threading.Tasks.Task LoadRaw() => null!;          // managed Task vs unflipped il2cpp return -> rejected
    public System.Threading.Tasks.Task<int> LoadRawT() => null!;    // managed Task<T> vs unflipped il2cpp return -> rejected
    public FakeIl2CppTask LoadRawIl2() => null!;                    // faithful il2cpp spelling -> passes

    // container-widening spellings — read-only supertypes of the concrete proxy container
    public int Muster(IReadOnlyList<int> squad) => 0;       // widen List<int> -> IReadOnlyList<int>   (bind)
    public int Muster(IEnumerable<int> squad) => 0;         // widen List<int> -> IEnumerable<int>      (bind)
    public int Muster(IList<int> squad) => 0;               // the WRITE-THROUGH spelling (MutableList) (bind)
    public int Muster(ICollection<int> squad) => 0;         // ICollection<> is not an engine container (no bind)
    public void SetInventory(IReadOnlyList<string> items) { }   // widen List<string> -> IReadOnlyList<string> (bind)
    public void SetInventory(IEnumerable<object> items) { }     // element types differ (string vs object)      (no bind)

    // interface-impl spellings — the plain interface method name (maps to the mangled proxy method)
    public void Tick() { }                    // -> FakeNs_IFakeTicker_Tick   (bind)
    public void Tick(int n) { }               // signature mismatch vs the 0-arg mangled impl (no bind)

    // generic-inference spellings — concrete signatures that name an instantiation of the game's Echo<T>(T)
    public int Echo(int input) => 0;          // -> Echo<int>    (infer T=int, value type -> bind)
    public long Echo(long input) => 0;        // -> Echo<long>   (infer T=long, value type -> bind)
    public string Echo(string input) => input;// -> Echo<string> (ref-type instantiation, also bound)
    public int Echo(int a, int b) => 0;       // wrong arity vs Echo<T>(T)  (no bind)
    public void Fetch(bool refresh, Inutil.Bridge.Callback<FakeItem> cb) { }   // spells the mirror for the game FakeCallback<FakeItem> param
    public void Subscribe(int id, Inutil.Bridge.Action<FakeItem> onItem) { }   // spells the inbound-delegate mirror for the game's System.Action<FakeItem>
    public void Compute(Inutil.Bridge.Func<int, FakeItem> make) { }            // spells the Func mirror for the game's System.Func<int,FakeItem>
    // spells the NATURAL everything for FakeGame.Quote: string[] for the raw Il2CppStringArrays (the widened-param
    // arm INSIDE the mirror tier) + the nested-container mirror element
    public void Quote(string[] traders, string[] items, Inutil.Bridge.Callback<Dictionary<string, Dictionary<string, int>>> cb) { }
    public void QuoteBad(Inutil.Bridge.Callback<Dictionary<string, Dictionary<string, int>>> cb) { }   // vs game inner <string,string> -> must NOT bind

    public void Hidden(int n) { }             // matches FakeGame's non-public Hidden(int) via Tier 0 (raw-proxy)
}

// A proxy whose only `_Tick` method is NOT the interface-mangling shape (segment before `_Tick` is `Helper`, not
// I+Upper) — the Tier-2 shape guard must reject it.
class FakeItem { }
class FakeIl2CppTask { }                                    // stands in for the UNFLIPPED Il2CppSystem.Threading.Tasks.Task proxy
class FakeCallback<T> { }                                   // stands in for the game's Comfort.Common.Callback<T'> (the mirror-param target)
class FakeNoIface { public void Helper_Tick() { } }

// A proxy with TWO mangled interface-impl `_Tick` methods — Tier 2 must return null (never silently guess one).
class FakeAmbigProxy { public void A_IFoo_Tick() { } public void B_IBar_Tick() { } }

// A real Hook<T> over the stand-in proxy, with ONE deliberately-mismatched method (right name, wrong arg type):
// drives Discover's no-match branch offline (no game method binds, so nothing installs) to prove the OnWarning
// routing. Keep it to a single non-matching method — a matching one would try to install a detour (needs a game).
sealed class FakeGameHook : Hook<FakeGame>
{
    public void Tally(string wrong) { }       // name collides, signature differs -> the detailed diagnostic
    public void Tallyy(int n) { }             // name absent + public -> the typo/rename warning (both route via OnWarning)
}

// A hook class that ALSO implements a mod lifecycle interface: its Tick() is the ITick lifecycle (FrameDriver-
// driven), NOT a game hook — Discover must not bind it to FakeGame's mangled FakeNs_IFakeTicker_Tick.
sealed class FakeLifecycleHook : Hook<FakeGame>, ITick
{
    public void Tick() { }
}

// Stands in for the RAW il2cpp Dictionary proxy inside a game close (Il2CppSystem.Collections.Generic.Dictionary`2 —
// the registry anchor name is what ElementCorresponds/NaturalContainerBindsRaw compare, so the FullName must be exact;
// the real interop assembly is game-generated and absent from the offline gate, same convention as FakeIl2CppTask).
namespace Il2CppSystem.Collections.Generic
{
    class Dictionary<TKey, TValue> { }
}
