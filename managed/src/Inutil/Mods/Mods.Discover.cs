// Discovery — scan a mod assembly for Hook<T> subclasses + lifecycle implementers and wire them (the ergonomic
// tier's registration path). A Hook<T>'s methods are matched to the game proxy's methods by name + parameter
// types and each installed as ONE engine pre-hook running the around adapter (HookDispatch.Around).
//
// OWNERSHIP + TEARDOWN. The engine derives a hook's owner from the callback delegate's assembly — but the around
// callback is a closure defined HERE, in the host (Inutil.dll), so the engine's own Hooks.RemoveAll(modAlc)
// could never reach it (and that host-static closure is exactly what roots the mod's collectible ALC). So Mods
// tracks each subscription under the MOD's ALC and disposes them itself in RemoveAll — un-rooting the mod is
// what lets its collectible context actually collect.
using System.Reflection;
using System.Runtime.Loader;
using Inutil.Hooks;

namespace Inutil;

public static partial class Mods
{
    // Mod ALC -> the engine hook subscriptions Discover installed for it (host-owned closures the engine's own
    // RemoveAll can't reach). Disposed in RemoveAll(owner) — the mod's un-root.
    private static readonly Dictionary<AssemblyLoadContext, List<IDisposable>> _subs = new();

    // Scan `asm` for Hook<T> subclasses + lifecycle implementers and wire them. Returns the number of hook
    // methods wired. ATOMIC: either the whole assembly wires, or — if any method's wiring throws — every engine
    // hook this call installed is disposed and nothing is committed, so no partially-wired generation is ever
    // observable. OnLoad fires only AFTER a successful wire, so a mod's OnLoad sees its hooks already live.
    public static int Discover(Assembly asm) => Discover(asm, null, null);
    public static int Discover(Assembly asm, Func<Type, bool>? keep) => Discover(asm, keep, null);

    // Overload: wire only the classes `keep` accepts (`keep == null` wires all), and name the mod for
    // user-facing artifacts (its config file) — `friendlyName == null` derives it from the assembly name,
    // stripping the framework's compile prefix so `inutilmod_mymod` reads as `mymod`. The mod host passes the
    // folder name explicitly; the fallback covers the REPL / a direct Discover.
    public static int Discover(Assembly asm, Func<Type, bool>? keep, string? friendlyName)
    {
        if (asm is null) throw new ArgumentNullException(nameof(asm));
        AssemblyLoadContext owner = AssemblyLoadContext.GetLoadContext(asm) ?? AssemblyLoadContext.Default;

        var newSubs = new List<IDisposable>();          // uncommitted — disposed on failure, tracked on success
        var newLifecycle = new List<object>();          // uncommitted — Added (fires OnLoad) only on success
        int wired = 0;
        try
        {
            foreach (Type t in SafeTypes(asm))
            {
                if (t.IsAbstract || t.ContainsGenericParameters) continue;
                if (keep is not null && !keep(t)) continue;

                object? instance = null;                // created lazily; shared by lifecycle + instance hook methods
                bool lifecycle = typeof(ILoad).IsAssignableFrom(t) || typeof(IUnload).IsAssignableFrom(t)
                              || typeof(ITick).IsAssignableFrom(t) || typeof(IGui).IsAssignableFrom(t)
                              || typeof(IConfigure).IsAssignableFrom(t);
                if (lifecycle) { instance = Activator.CreateInstance(t); newLifecycle.Add(instance!); }

                Type? target = HookTargetOf(t);
                if (target is null) continue;           // not a Hook<T> — lifecycle (if any) already captured
                wired += WireHookClass(t, target, ref instance, owner, newSubs);
            }
        }
        catch
        {
            foreach (IDisposable s in newSubs) { try { s.Dispose(); } catch { } }   // unwind — no partial commit
            throw;
        }

        // Commit: track the subscriptions under the mod's ALC, then fire Configure (config is ready BEFORE
        // OnLoad — the mod wires with its settings applied), then OnLoad (exception-isolated in Add).
        if (newSubs.Count > 0)
            lock (_gate)
            {
                if (!_subs.TryGetValue(owner, out List<IDisposable>? list)) _subs[owner] = list = new();
                list.AddRange(newSubs);
            }
        var configurers = newLifecycle.OfType<IConfigure>().ToList();
        if (configurers.Count > 0)
            ModConfigStore.Register(friendlyName ?? FriendlyName(asm), owner, configurers);
        foreach (object obj in newLifecycle) Add(obj);
        return wired;
    }

    // Match each declared method of the hook class to a method on `target` (the game proxy) and install it. The
    // MATCH decision is delegated to HookMatch (pure reflection — Tier 0 exact name+types, plus the fallback tiers
    // as they land); this method owns the il2cpp INSTALL. When nothing binds, HookMatch.Diagnose decides whether
    // the miss was an intended-but-mis-spelled hook (warn — fail-loud) or a private helper (stay silent).
    private static int WireHookClass(Type hookType, Type target, ref object? instance,
                                     AssemblyLoadContext owner, List<IDisposable> sink)
    {
        const BindingFlags Decl = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                                | BindingFlags.Instance | BindingFlags.Static;

        // Methods that IMPLEMENT a mod lifecycle interface (ILoad/IUnload/ITick/IGui) are driven by the lifecycle
        // path (Add + FrameDriver), NOT the hook path — exclude them from matching so a class like
        // `Hook<Game>, ITick` never binds its Tick()/OnLoad() to a coincidentally-named game method (e.g. the
        // interface-impl tier mapping a lifecycle Tick() onto the mangled ToyGame_ITicker_Tick).
        var lifecycleImpls = new HashSet<MethodInfo>();
        foreach (Type li in new[] { typeof(ILoad), typeof(IUnload), typeof(ITick), typeof(IGui) })
            if (li.IsAssignableFrom(hookType))
                foreach (MethodInfo tm in hookType.GetInterfaceMap(li).TargetMethods)
                    lifecycleImpls.Add(tm);

        int wired = 0;
        foreach (MethodInfo hm in hookType.GetMethods(Decl))
        {
            if (hm.IsSpecialName || hm.IsGenericMethod) continue;                    // accessors / generic bodies: skip
            if (lifecycleImpls.Contains(hm)) continue;                               // ILoad/ITick/… — the lifecycle path owns these

            MethodInfo? gm = HookMatch.Resolve(target, hm);
            if (gm is null)
            {
                if (HookMatch.Diagnose(target, hm) is string warn) Hooks.Hooks.OnWarning?.Invoke(warn);
                continue;                                                            // no counterpart on T — not a hook
            }
            // Bind-time return validation (the compile-clean/runtime-throw seam): reject a return the marshaller
            // cannot materialize BEFORE installing anything — the atomic unwind turns it into a loud per-mod "load
            // failed" at Discover instead of a MissingMethodException at the first live call.
            if (HookMatch.ValidateReturn(target, gm, hm) is string err)
                throw new InvalidOperationException(err);
            Type[] ptypes = hm.GetParameters().Select(p => p.ParameterType).ToArray();

            if (!hm.IsStatic) instance ??= Activator.CreateInstance(hookType);
            var binding = new HookBinding
            {
                HookMethod = hm,
                Instance = hm.IsStatic ? null : instance,
                ReceiverType = target,
                ParamTypes = ptypes,
                ReturnType = hm.ReturnType,
                GameReturnType = gm.ReturnType,
                Label = $"{target.Name}::{hm.Name}",
            };
            HookCallback around = ctx => HookDispatch.Around(binding, ctx);
            IDisposable sub;
            if (gm.IsGenericMethod)
            {
                // A CLOSED generic instantiation (Tier 3 inference) — the managed name-based Pre can't resolve it
                // (GetMethod(name, concrete-types) never matches an open generic def, and a closed instantiation has
                // no distinct name). Resolve its native il2cpp MethodInfo* directly (Il2CppInterop populates the
                // inflated pointer on the closed MethodInfoStoreGeneric<T>) and hook that — the same install the raw
                // PreNative tier uses.
                nint nativeMi = Hooks.Hooks.NativeMethodInfoOf(gm);
                if (nativeMi == 0)
                    throw new InvalidOperationException(
                        $"could not resolve the native MethodInfo* for the inferred instantiation " +
                        $"{target.Name}::{gm.Name}<{string.Join(",", gm.GetGenericArguments().Select(a => a.Name))}>");
                sub = Hooks.Hooks.PreNative(nativeMi, around);
            }
            else
            {
                Type[] gsig = gm.GetParameters().Select(p => p.ParameterType).ToArray();
                sub = Hooks.Hooks.Pre(gm.DeclaringType!, gm.Name, gsig, around);
            }
            sink.Add(sub);
            wired++;
        }
        return wired;
    }

    // The name a mod's user-facing artifacts (its config file) go by, when the host didn't pass one: the
    // assembly name with the framework's compile prefix stripped (CsModHost mints `inutilmod_<name>`, Coremods
    // `inutilcore_<name>`; libs are raw). Phrased against the FACT — "an inutil* compile prefix" — not an
    // enumerated list, so a prefix added later still resolves; a name with no such prefix is returned unchanged.
    private static string FriendlyName(Assembly asm)
    {
        string n = asm.GetName().Name ?? "mod";
        var m = System.Text.RegularExpressions.Regex.Match(n, "^inutil[a-z]+_(.+)$");
        return m.Success ? m.Groups[1].Value : n;
    }

    // The T of a `: Hook<T>` ancestor (walks intermediate bases), or null if `t` is not a hook class.
    private static Type? HookTargetOf(Type t)
    {
        for (Type? b = t.BaseType; b is not null; b = b.BaseType)
            if (b.IsGenericType && b.GetGenericTypeDefinition() == typeof(Hook<>))
                return b.GetGenericArguments()[0];
        return null;
    }

    // Types of `asm`, tolerating a partially-loadable assembly (a missing dependency yields the types that DID
    // load rather than throwing away the whole scan).
    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    // Fully retire the mod owned by `owner`: dispose its ergonomic hook subscriptions (un-rooting the host-static
    // closures the engine's own RemoveAll can't reach), drop + OnUnload its lifecycle objects, THEN reclaim its
    // concurrency residue — queued MainThread posts (dropped BEFORE coroutines/pins so an already-queued closure
    // can never run against a just-unpinned pointer), running coroutines (else an unloaded generation's
    // `while(true)` loop keeps stepping old code and roots the "collectible" ALC), and leftover il2cpp pins (one
    // leaked GCHandle per Pin per hot-reload otherwise). One call, so the collectible ALC has no host-side root
    // left and can collect. Returns hooks-disposed + lifecycle-dropped (the reclaim counts go to the warning
    // channel — a nonzero count is a mod forgetting its own teardown).
    public static int RemoveAll(AssemblyLoadContext owner)
    {
        int removed = 0;
        List<IDisposable>? subs;
        lock (_gate) { _subs.Remove(owner, out subs); }
        if (subs is not null)
            foreach (IDisposable s in subs) { try { s.Dispose(); removed++; } catch { } }
        removed += RemoveLifecycle(owner);

        ModConfigStore.RemoveAll(owner);   // an unloaded generation's Configure must never re-fire into a dead ALC
        int posts = Host.MainThread.DropPosted(owner);
        int cos = Host.Coroutines.StopAll(owner);
        int pins = Host.MainThread.UnpinAll(owner);
        if (posts + cos + pins > 0)
            Hooks.Hooks.OnWarning?.Invoke(
                $"inutil unload: reclaimed {cos} coroutine(s), {posts} queued post(s), {pins} pin(s) the mod left running " +
                $"(ALC-scoped teardown; OnUnload is the place to stop/unpin your own)");
        return removed;
    }
}
