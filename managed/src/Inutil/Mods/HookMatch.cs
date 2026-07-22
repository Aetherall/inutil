// HookMatch — the DECISION half of Discovery: given the game proxy type T and one method a Hook<T> subclass
// declares, which method on T should it hook? Split out of WireHookClass (the INSTALL half) so it is PURE
// reflection over System.Type — no il2cpp, no native detour — and therefore unit-testable OFFLINE, without booting
// a game (WireHookClass's Hooks.Pre install needs a live il2cpp frame; this does not).
//
// TIERS. Tier 0 is the exact match: same name + same parameter Types. The fallback tiers (container-shape /
// generic-inference / interface-implementor / concrete-container — docs/reference/limits.md, Gap 1) append here,
// each rescuing a hook the exact rule misses. Resolve tries them in order and returns the first that binds, or null.
//
// THE DIAGNOSTIC (fail-loud). When nothing binds, Discover would `continue` SILENTLY — the one seam where an
// intended-but-mis-spelled hook produced no hook and no warning. Diagnose closes that: it warns ONLY when the miss
// looks like an intended hook (T has a method of that name — a signature mismatch, not a private helper), else
// stays silent.
using System.Reflection;
using System.Runtime.CompilerServices;
using Inutil.Schema;   // CorrespondenceRegistry / Families — the SAME engine registry the marshaller consults, so
                       // the matcher only ever binds container spellings the marshaller can actually produce.

// Expose internals to the OFFLINE matcher test so the pure decision logic can be driven against plain classes.
[assembly: InternalsVisibleTo("inutil-mods-tests")]

namespace Inutil;

internal static class HookMatch
{
    // Candidate game methods live at any visibility, and static-vs-instance is matched to the hook method (a hook
    // for a static game method is itself static, and vice-versa — the frame ABI differs).
    static BindingFlags Flags(bool isStatic) =>
        BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);

    // The game method `hook` corresponds to, or null if nothing binds. Tiers tried in specificity order; first
    // non-null wins.
    public static MethodInfo? Resolve(Type target, MethodInfo hook)
    {
        Type[] ptypes = hook.GetParameters().Select(p => p.ParameterType).ToArray();
        return MatchExact(target, hook, ptypes)
            ?? MatchWidenedContainer(target, hook, ptypes)
            ?? MatchInterfaceImpl(target, hook, ptypes)
            ?? MatchMirrorParams(target, hook, ptypes)
            ?? MatchGenericInference(target, hook, ptypes);
    }

    // Tier 0 — exact name + exact parameter Types. Reflection returns the MOST-DERIVED method for the name, so a
    // virtual name yields the override (the body every dispatch enters), not the slot base.
    static MethodInfo? MatchExact(Type target, MethodInfo hook, Type[] ptypes)
    {
        try
        {
            MethodInfo? m = target.GetMethod(hook.Name, Flags(hook.IsStatic), binder: null, types: ptypes, modifiers: null);
            // A generic method DEFINITION (open) is NOT an exact concrete match: GetMethod binds it on parameter
            // types ALONE (ignoring the return), so a 0-param generic like `LoadProfiles<T>() : Task<T>` matches a
            // hook `Task<UnparsedData> LoadProfiles()` and returns the OPEN def — which Discover cannot hook (its
            // il2cpp method-pointer field lives on the OPEN MethodInfoStoreGeneric<> -> fld.GetValue throws). Defer
            // to Tier 3 (MatchGenericInference), which unifies the hook's return against the open sig and CLOSES it.
            if (m is not null && m.IsGenericMethodDefinition) return null;
            return m;
        }
        catch (AmbiguousMatchException) { return null; }   // an overloaded name the exact types didn't disambiguate
    }

    // Tier 1 — container interface widening. A hook may spell a READ-ONLY supertype (IReadOnlyList/IEnumerable) of
    // the flipped proxy's concrete container (List); the marshalling engine reads the il2cpp list INTO that
    // spelling. Match a same-name/same-arity/same-static game method whose every parameter the hook names exactly
    // OR widens (see ParamBinds). Bind only a UNIQUE such method — two is an ambiguity we must not silently guess
    // through (fall to null, and the diagnostic fires).
    static MethodInfo? MatchWidenedContainer(Type target, MethodInfo hook, Type[] ptypes)
    {
        MethodInfo? widened = null;
        foreach (MethodInfo cand in target.GetMethods(Flags(hook.IsStatic)))
        {
            if (cand.Name != hook.Name) continue;
            ParameterInfo[] cps = cand.GetParameters();
            if (cps.Length != ptypes.Length) continue;
            if (cand.ReturnType != hook.ReturnType) continue;                        // widen params only (return exact)
            bool all = true;
            for (int i = 0; i < ptypes.Length && all; i++) all = ParamBinds(ptypes[i], cps[i].ParameterType);
            if (!all) continue;
            if (widened is not null) return null;                                    // >1 candidate — ambiguous, don't guess
            widened = cand;
        }
        return widened;
    }

    // Tier 2 — explicit interface implementation. Il2CppInterop FLATTENS interfaces: an explicit impl `Ns.IFoo.Bar`
    // lands on the proxy as a PUBLIC method mangled `Ns_IFoo_Bar`, and the proxy declares no `: IFoo` and keeps no
    // interface map (verified against the real ToyGame proxy — its interface list is empty and the method is named
    // `ToyGame_ITicker_Tick`), so GetInterfaceMap can't reach it and the name is the only handle. A hook names the
    // PLAIN method (`Bar`); recognize the mangled counterpart by SHAPE — name ends `_<Bar>`, the `_`-segment before
    // it is an interface simple name (`I` + uppercase) — plus an EXACT signature and matching static-ness. UNIQUE,
    // else null (the diagnostic fires). Signature exact, so the install marshals as a normal method — pure name
    // resolution.
    static MethodInfo? MatchInterfaceImpl(Type target, MethodInfo hook, Type[] ptypes)
    {
        string suffix = "_" + hook.Name;
        MethodInfo? found = null;
        foreach (MethodInfo cand in target.GetMethods(Flags(hook.IsStatic)))
        {
            if (cand.Name.Length <= suffix.Length || !cand.Name.EndsWith(suffix, StringComparison.Ordinal)) continue;
            string prefix = cand.Name.Substring(0, cand.Name.Length - suffix.Length);   // the mangled interface path
            int us = prefix.LastIndexOf('_');
            string ifaceSeg = us >= 0 ? prefix.Substring(us + 1) : prefix;              // the interface's simple name
            if (ifaceSeg.Length < 2 || ifaceSeg[0] != 'I' || !char.IsUpper(ifaceSeg[1])) continue;   // I<Name> shape
            ParameterInfo[] cps = cand.GetParameters();
            if (cps.Length != ptypes.Length || cand.ReturnType != hook.ReturnType) continue;
            bool sig = true;
            for (int i = 0; i < ptypes.Length && sig; i++) sig = cps[i].ParameterType == ptypes[i];
            if (!sig) continue;
            if (found is not null) return null;                                        // >1 candidate — ambiguous, don't guess
            found = cand;
        }
        return found;
    }

    // Tier 2.5 — mirror params (Inutil.Bridge.Callback<T> and the inbound-delegate mirrors Inutil.Bridge.Action/Func<..>).
    // A hook RECEIVES a game reference-wrapper it can neither spell (the broken Callback<T[]> close) nor receive as a
    // natural System.Action (no reverse delegate bridge — ConvKind.Delegate fails loud) nor bind by the tiers above; it
    // spells inutil's mirror instead, and the seam MATERIALIZES the game value into it. A game callback param can't be
    // interop-patch-flipped (a GAME type — Comfort.Common.Callback — the patch may name only Il2CppSystem.*), and a
    // game delegate param exposes as a NATURAL System.Action<Il2CppElem> (never the mirror), so the mirror<->game
    // correspondence is recognized HERE. Match a same-name/same-arity/same-static/same-return method where every param
    // the hook names EXACTLY or binds as a mirror (>=1 mirror, else the exact/widened tiers own it). UNIQUE, or null.
    static MethodInfo? MatchMirrorParams(Type target, MethodInfo hook, Type[] ptypes)
    {
        MethodInfo? found = null;
        foreach (MethodInfo cand in target.GetMethods(Flags(hook.IsStatic)))
        {
            if (cand.Name != hook.Name) continue;
            ParameterInfo[] cps = cand.GetParameters();
            if (cps.Length != ptypes.Length || cand.ReturnType != hook.ReturnType) continue;
            bool all = true, anyMirror = false;
            for (int i = 0; i < ptypes.Length && all; i++)
            {
                if (ptypes[i] == cps[i].ParameterType) continue;                     // exact param
                if (MirrorParamBinds(ptypes[i], cps[i].ParameterType)) { anyMirror = true; continue; }
                // A NON-mirror param may still be any spelling the widened tier accepts (natural array <->
                // Il2CppStringArray/…, natural container <-> raw il2cpp, container widening) — a real signature mixes
                // them (serverstub's GetInsurancePrice: two string[] params BESIDE the Callback mirror straddled the
                // tiers and bound NOWHERE). The runtime already marshals each param from the hook's OWN spelling, so
                // accepting ParamBinds here is pure name-resolution, same as Tier 1.
                if (ParamBinds(ptypes[i], cps[i].ParameterType)) continue;
                all = false;
            }
            if (!all || !anyMirror) continue;                                        // needs >=1 mirror (else a Tier 0/1 job)
            if (found is not null) return null;                                      // >1 candidate — ambiguous, don't guess
            found = cand;
        }
        return found;
    }

    // Does a hook param spelled `hook` (a mirror) bind a game param `game`? Two mirror families bind a PARAM:
    //   * Callback — `hook` is the registered Callback mirror, `game`'s open is the per-game callback open
    //     (Mirror.RegisterCallback), and the mirror element corresponds to the game element.
    //   * DelegateIn — `hook` is a registered inbound-delegate mirror (Inutil.Bridge.Action/Func<..>), and `game` is
    //     the NATURAL System.Action`N/Func`N the interop proxy exposes a delegate param as — same open (from the
    //     mirror<->System map) and same arity, each mirror element (incl. a Func's return) corresponding to the game
    //     element. NO per-game shim — the game open is the BCL System delegate itself, not a registered game type.
    // Either way pure reflection over Types — offline-testable.
    static bool MirrorParamBinds(Type hook, Type game)
    {
        if (!hook.IsConstructedGenericType || !game.IsConstructedGenericType) return false;
        Type hookOpen = hook.GetGenericTypeDefinition();
        ConvKind? kind = _reg.ByBclOpenType(hookOpen)?.Kind;

        if (kind == ConvKind.Callback)
        {
            Type? gameOpen = Inutil.Bridge.Mirror.GameCallbackOpen;
            if (gameOpen is null || game.GetGenericTypeDefinition() != gameOpen) return false;
            return ElementCorresponds(hook.GetGenericArguments()[0], game.GetGenericArguments()[0]);
        }

        if (kind == ConvKind.DelegateIn)
        {
            Type? sysOpen = Inutil.Bridge.DelegateMirrors.SystemOpen(hookOpen);   // the System.Action/Func open this mirror stands in for
            if (sysOpen is null || game.GetGenericTypeDefinition() != sysOpen) return false;
            Type[] h = hook.GetGenericArguments(), g = game.GetGenericArguments();
            if (h.Length != g.Length) return false;                              // same arity (a Func mirror's last arg is the return)
            for (int i = 0; i < h.Length; i++) if (!ElementCorresponds(h[i], g[i])) return false;
            return true;
        }

        return false;
    }

    // The mirror element `mirrorElem` corresponds to the game element `gameElem`: identity (a shared proxy / raw
    // il2cpp Il2CppSystem.Object), a reference-element array E[] <-> Il2CppReferenceArray<E> (MapArrayArgToIl2Cpp),
    // the ERASED TOP — natural `object` <-> the game's Il2CppSystem.Object (game name from the registry, never
    // spelled here) — or a NESTED natural container: a mirror element spelled as a BCL container (Dictionary/List)
    // against the RAW il2cpp container inside the game close (a generic arg inside a game close is never
    // interop-patch-flipped — e.g. Callback<Dictionary<string, Dictionary<string, int>>>). That arm is
    // NaturalContainerBindsRaw, each generic argument recursing back through here, so correspondence stays pure
    // reflection and the runtime marshals through the ConvKind.List/Dictionary write path from the SPELLED element.
    static bool ElementCorresponds(Type mirrorElem, Type gameElem)
        => mirrorElem == gameElem
           || MapArrayArgToIl2Cpp(mirrorElem) == gameElem
           || (mirrorElem == typeof(object) && gameElem.FullName == _reg.ByBclOpenType(typeof(object))?.Il2CppFullName)
           || NaturalContainerBindsRaw(mirrorElem, gameElem);

    // Tier 3 — generic-method instantiation inference. A generic game method `Echo<T>(T)` has no single body to
    // hook (one per instantiation); the modder names a SPECIFIC one by writing a CONCRETE hook `int Echo(int)`.
    // Infer the type arguments by unifying the generic definition's open signature against the hook's concrete one
    // (T=int), inflate the closed instantiation (`Echo<int>`), and VERIFY by exact-signature match after inflation.
    // Return that closed MethodInfo (WireHookClass resolves its native mi and hooks it); a UNIQUE match or null.
    // The hook itself must be concrete — a generic hook method can't name which instantiation to hook.
    static MethodInfo? MatchGenericInference(Type target, MethodInfo hook, Type[] ptypes)
    {
        if (hook.IsGenericMethod) return null;
        MethodInfo? found = null;
        foreach (MethodInfo cand in target.GetMethods(Flags(hook.IsStatic)))
        {
            if (!cand.IsGenericMethodDefinition || cand.Name != hook.Name) continue;
            ParameterInfo[] cps = cand.GetParameters();
            if (cps.Length != ptypes.Length) continue;

            // infer the type args by unifying the open signature (return + params) against the concrete hook
            var inferred = new Type?[cand.GetGenericArguments().Length];
            bool ok = InferInto(cand.ReturnType, hook.ReturnType, inferred);
            for (int i = 0; i < cps.Length && ok; i++) ok = InferInto(cps[i].ParameterType, ptypes[i], inferred);
            if (!ok || Array.Exists(inferred, a => a is null)) continue;

            MethodInfo closed;
            try { closed = cand.MakeGenericMethod(inferred!); }
            catch { continue; }                                                    // generic constraints violated

            // verify: the inflated signature must EXACTLY equal the hook's (belt-and-suspenders over inference)
            if (closed.ReturnType != hook.ReturnType) continue;
            ParameterInfo[] clps = closed.GetParameters();
            bool sig = true;
            for (int i = 0; i < clps.Length && sig; i++) sig = clps[i].ParameterType == ptypes[i];
            if (!sig) continue;

            // NATIVE-RESOLUTION re-close: the hook spells the NATURAL element (Task<Foo[]>), so inference closes over
            // the managed array Foo[]. But Il2CppInterop reified the game's instantiation over Il2CppReferenceArray<Foo>,
            // NOT the managed Foo[] — whose MethodInfoStoreGeneric<Foo[]> cctor THROWS (never reified). Re-close over
            // the il2cpp reference-array counterpart so WireHookClass resolves the reified native mi; the HookBinding
            // still marshals the hook's natural Task<Foo[]> return (Discover builds it from the HOOK method, this gm is
            // used ONLY for the native MethodInfo* lookup). Value-element arrays and non-arrays stay as-is.
            Type[] il2Args = Array.ConvertAll(inferred!, MapArrayArgToIl2Cpp);
            if (!ReferenceArgsEqual(il2Args, inferred!))
            {
                try { closed = cand.MakeGenericMethod(il2Args); }
                catch { continue; }                                                // il2cpp-array close violated a constraint — skip
                if (closed.GetParameters().Length != ptypes.Length) continue;      // arity guard (paranoia; unchanged)
            }

            if (found is not null) return null;                                    // >1 candidate — ambiguous, don't guess
            found = closed;
        }
        return found;
    }

    // Map a managed single-dimension REFERENCE-element array Foo[] -> Il2CppReferenceArray<Foo> (the il2cpp instantiation
    // Il2CppInterop actually reifies for a game array). Everything else (value-element arrays, non-arrays) is identity.
    static Type MapArrayArgToIl2Cpp(Type? t)
        => t is { IsArray: true } && t.GetArrayRank() == 1 && t.GetElementType() is { IsValueType: false } e
            ? typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<>).MakeGenericType(e)
            : t!;

    // A natural single-dim array T[] -> the il2cpp array WRAPPER Il2CppInterop reifies for its element KIND: reference
    // -> Il2CppReferenceArray<T>, value/blittable -> Il2CppStructArray<T>, string -> Il2CppStringArray. Unlike
    // MapArrayArgToIl2Cpp (reference-only), this covers ALL three because it powers a PARAM bind, not a native
    // re-close. Null for a non-array. (Jagged/multi-dim left to a follow-up.)
    static Type? MapNaturalArrayToIl2Cpp(Type t)
    {
        if (!t.IsArray || t.GetArrayRank() != 1) return null;
        Type e = t.GetElementType()!;
        if (e == typeof(string)) return typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray);
        if (e.IsValueType)       return typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<>).MakeGenericType(e);
        return typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<>).MakeGenericType(e);
    }

    static bool ReferenceArgsEqual(Type[] a, Type?[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i]) && a[i] != b[i]) return false;
        return true;
    }

    // Unify an OPEN type from a generic definition against a CONCRETE type from the hook, filling `inferred` at
    // each generic-method-parameter position (consistently — a conflict returns false). Recurses through
    // constructed generics (`List<T>` vs `List<int>`), by-ref and arrays; a fully-concrete open type must equal
    // the concrete one.
    static bool InferInto(Type open, Type concrete, Type?[] inferred)
    {
        if (open.IsGenericMethodParameter)
        {
            int pos = open.GenericParameterPosition;
            if (inferred[pos] is null) { inferred[pos] = concrete; return true; }
            return inferred[pos] == concrete;
        }
        if (open.IsByRef && concrete.IsByRef) return InferInto(open.GetElementType()!, concrete.GetElementType()!, inferred);
        if (open.IsArray && concrete.IsArray && open.GetArrayRank() == concrete.GetArrayRank())
            return InferInto(open.GetElementType()!, concrete.GetElementType()!, inferred);
        if (open.IsConstructedGenericType && concrete.IsConstructedGenericType
            && open.GetGenericTypeDefinition() == concrete.GetGenericTypeDefinition())
        {
            Type[] oa = open.GetGenericArguments(), ca = concrete.GetGenericArguments();
            for (int i = 0; i < oa.Length; i++) if (!InferInto(oa[i], ca[i], inferred)) return false;
            return true;
        }
        return open == concrete;                                                   // both concrete — must be equal
    }

    static readonly CorrespondenceRegistry _reg = Families.Default();

    // Does a game parameter of type `game` bind a hook parameter spelled `hook`? Exact type — always. Otherwise a
    // CONTAINER WIDENING: same element type(s), `hook` a genuine supertype of `game` (the value the engine
    // materializes IS assignable to what the body declared), and BOTH spellings registry-backed containers (never a
    // spelling the marshaller can't produce, never a loose covariant IsAssignableFrom like IEnumerable<object> over
    // List<string>, which the same-element-types check rejects).
    static bool ParamBinds(Type hook, Type game)
    {
        if (hook == game) return true;
        // Natural array PARAM <-> the game's raw il2cpp array wrapper (T[] <-> Il2CppReferenceArray/StructArray/StringArray).
        // The ADDITIVE twin of the ArrayRewriter's signature flip: a hook spells natural T[] and binds against a proxy
        // the interop-patch left RAW — chiefly a VIRTUAL array slot ArrayRewriter defers (vtable lockstep). No proxy is
        // rewritten; the hook-dispatch marshals through the ConvKind.Array path from the hook's spelled T[] (Hook.cs
        // reads args by ParamTypes), so this is pure name-resolution — the raw spelling still binds Tier 0.
        if (hook.IsArray && MapNaturalArrayToIl2Cpp(hook) == game) return true;
        // Natural BCL container PARAM <-> the RAW il2cpp container the interop-patch left UNFLIPPED. Same additive idea,
        // one level over: a writeTarget:false READ-spelling (IReadOnlyList/IEnumerable) is NEVER flipped in-place, so a
        // hook spelling the natural interface had nothing to bind.
        if (NaturalContainerBindsRaw(hook, game)) return true;
        // A natural System.Exception PARAM <-> the game's Il2CppSystem.Exception (an Il2CppObjectBase, NOT a
        // System.Exception, so never a Tier-0 match). Registry-driven: the game name from Families, never spelled here.
        if (hook == typeof(System.Exception) && _reg.ByBclOpenType(typeof(System.Exception))?.Il2CppFullName == game.FullName)
            return true;
        if (!hook.IsConstructedGenericType || !game.IsConstructedGenericType) return false;
        if (!hook.GetGenericArguments().SequenceEqual(game.GetGenericArguments())) return false;   // same element type(s)
        if (!IsEngineContainer(hook) || !IsEngineContainer(game)) return false;
        return hook.IsAssignableFrom(game);                                          // game IS-A hook — a real widening
    }

    // A natural BCL container (List/Dict/Set/IReadOnlyList/IEnumerable) spelled against the RAW il2cpp container the
    // interop-patch left unflipped. Matched by the registry ANCHOR NAME (game open-def FullName vs the row's
    // Il2CppFullName) so it stays PURE reflection (offline-testable) — plus element correspondence. Additive: the raw
    // il2cpp spelling still binds Tier 0, the interop-patch flip stays the fast path for concrete write-target
    // containers on non-virtual methods — this covers the read-spellings + deferred slots the flip can't reach.
    static bool NaturalContainerBindsRaw(Type hook, Type game)
    {
        if (!hook.IsConstructedGenericType || !game.IsConstructedGenericType) return false;
        var row = _reg.ByBclOpenType(hook.GetGenericTypeDefinition());
        if (row is null || game.GetGenericTypeDefinition().FullName != row.Il2CppFullName) return false;
        Type[] h = hook.GetGenericArguments(), g = game.GetGenericArguments();
        if (h.Length != g.Length) return false;
        for (int i = 0; i < h.Length; i++) if (!ElementCorresponds(h[i], g[i])) return false;
        return true;
    }

    // A constructed generic whose open definition the engine's correspondence registry marshals (List/IReadOnlyList/
    // IEnumerable/HashSet/Dictionary/…) — the single source of truth for "the marshaller can produce this".
    static bool IsEngineContainer(Type t)
        => t.IsConstructedGenericType && _reg.ByBclOpenType(t.GetGenericTypeDefinition()) is not null;

    // A precise, actionable warning for a hook method that binds NOTHING — or null to stay silent. Self-contained
    // (re-checks Resolve): warn ONLY when the proxy HAS a method of this name (the modder meant to hook it but the
    // signature is wrong, which must not fail silently). Name absent on the proxy = a private helper; stay silent.
    // The message names the hook method AND the proxy's real overload(s), so the fix reads straight off the warning.
    public static string? Diagnose(Type target, MethodInfo hook)
    {
        if (Resolve(target, hook) is not null) return null;                          // it bound — nothing to warn about

        // Name-collision across visibility AND static-ness (so a wrong static/instance choice is caught too).
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        MethodInfo[] sameName = target.GetMethods(Any).Where(m => m.Name == hook.Name).ToArray();
        if (sameName.Length == 0)
        {
            // Name absent on the proxy. Visibility declares intent: a PUBLIC method on a hook class is an intended
            // hook (warn with the fix — else a typo'd hook, or one the game renamed, vanishes with zero output); a
            // private/internal method is a helper, silent. The escape for a public helper is one keyword.
            if (!hook.IsPublic) return null;
            return $"inutil hook: '{hook.DeclaringType?.Name}.{Sig(hook)}' matched NOTHING on '{target.Name}' — no method "
                 + $"of that name exists there. If this is a hook, the name is wrong (or the game renamed it — "
                 + $"check with surface-query/Introspect); if it is a helper, make it private or internal "
                 + $"(public methods on a Hook<T> class are treated as intended hooks).";
        }

        string have = string.Join("; ", sameName.Select(Sig));
        return $"inutil hook: '{hook.DeclaringType?.Name}.{Sig(hook)}' matched no method on '{target.Name}' — "
             + $"'{target.Name}' has [{have}]. Hooks bind by exact name + parameter types; fix the parameter types "
             + $"(or rename the method if it is a private helper, not a hook).";
    }

    // Bind-time RETURN validation — the compile-clean/runtime-throw seam. Resolve matches by name + PARAMETER types;
    // the return was never checked, and Task is the one shape whose failure defers to the first live dispatch: a
    // managed Task/Task<T> declared against an UNFLIPPED il2cpp Task proxy (unpatched interop, or a generic-method
    // Task return — never flipped) compiles, binds, then throws MissingMethodException inside the return-marshal
    // ("Constructor on type 'Task' not found"). Returns an actionable error to REJECT the bind with, or null when
    // bridgeable (flipped proxy / identical spelling / not the Task seam).
    public static string? ValidateReturn(Type target, MethodInfo game, MethodInfo hook)
    {
        Type r = hook.ReturnType, g = game.ReturnType;
        if (!typeof(System.Threading.Tasks.Task).IsAssignableFrom(r)) return null;   // only the Task seam defers its failure
        if (typeof(System.Threading.Tasks.Task).IsAssignableFrom(g)) return null;    // flipped (or managed-identical) — the WrapTask carrier bridges it
        return $"inutil hook: '{hook.DeclaringType?.Name}.{hook.Name}' declares the managed Task return '{Pretty(r)}' but "
             + $"'{target.Name}.{game.Name}' returns '{Pretty(g)}' — an UNFLIPPED il2cpp Task the ergonomic tier cannot "
             + $"materialize (it would throw MissingMethodException at the first call, after compiling and binding clean). "
             + $"Spell the return as the il2cpp Task type, or run the natural-typing interop patch; generic-method Task "
             + $"returns are NEVER flipped — those must stay il2cpp-spelled or use the raw Hooks.Pre tier.";
    }

    // "Name(T1, T2)" — a method's name + parameter types, ref/out marked, for the diagnostic message.
    static string Sig(MethodInfo m) => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => Pretty(p.ParameterType)))})";

    // Render a type readably: ref/out prefixed, generics as `List<Int32>` (not the raw `List`1`) so the signature
    // in the warning matches how the modder spelled it.
    static string Pretty(Type t)
    {
        if (t.IsByRef) return "ref " + Pretty(t.GetElementType()!);
        if (!t.IsGenericType) return t.Name;
        string name = t.Name;
        int tick = name.IndexOf('`');
        if (tick >= 0) name = name.Substring(0, tick);
        return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>";
    }
}
