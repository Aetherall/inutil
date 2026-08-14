using Mono.Cecil;
using Mono.Cecil.Cil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// Projects Mono.Cecil definitions onto the pure schema abstractions (ISlotType/ISlotMethod), so the
// VirtualSlotPlanner reasons about a REAL proxy through the same interface it reasons about synthetic test
// shapes — the seam is a thin adapter, the rules stay in the pure engine.
//
// MEMOIZED: exactly one adapter instance per Cecil definition. The planner groups slot members by ISlotMethod
// reference identity, so two references that resolve to the same base method MUST project to the SAME
// ISlotMethod — which they do, because Resolve() returns a stable MethodDefinition and this caches by it.
public sealed class CecilProjector
{
    readonly Dictionary<TypeDefinition, CecilSlotType> _types = new();
    readonly Dictionary<MethodDefinition, CecilSlotMethod> _methods = new();

    public CecilSlotType Type(TypeDefinition td)
    {
        if (!_types.TryGetValue(td, out var t)) _types[td] = t = new CecilSlotType(td, this);
        return t;
    }

    public CecilSlotMethod Method(MethodDefinition md)
    {
        if (!_methods.TryGetValue(md, out var m)) _methods[md] = m = new CecilSlotMethod(md, this);
        return m;
    }

    // Framework proxies (Il2CppSystem, Il2Cppmscorlib, UnityEngine, Il2CppInterop, …) are never flipped; the game's
    // OWN proxies are. A denylist on the ASSEMBLY name — but a bare "Il2Cpp" prefix does NOT imply framework:
    // MelonLoader prefixes EVERY generated il2cpp assembly, INCLUDING the game's own secondary modules
    // (Il2CppToyGame.Core), whereas BepInEx leaves game modules bare (ToyGame.Core). A blanket "Il2Cpp*==framework"
    // therefore misclassifies the melon-prefixed game module as framework, deferring its cross-module virtual slots —
    // a real loader-divergence bug. So: strip an optional leading "Il2Cpp" and test the REMAINDER against real
    // framework names (Il2CppSystem->System = framework; Il2CppToyGame.Core->ToyGame.Core = game). Framework proxies
    // are Il2Cpp-prefixed under BOTH loaders, so stripping is safe for bepinex too (its game modules were already bare).
    static readonly string[] FrameworkNames =
        { "UnityEngine", "Unity.", "System", "mscorlib", "netstandard", "Mono.", "Microsoft.", "Newtonsoft", "Interop", "__Generated" };

    public static bool IsFrameworkAssembly(string name)
    {
        string bare = name.StartsWith("Il2Cpp", StringComparison.Ordinal) ? name["Il2Cpp".Length..] : name;
        return FrameworkNames.Any(p => bare.StartsWith(p, StringComparison.Ordinal));
    }
}

public sealed class CecilSlotType : ISlotType
{
    readonly CecilProjector _p;
    public TypeDefinition Definition { get; }

    public CecilSlotType(TypeDefinition td, CecilProjector p) { Definition = td; _p = p; }

    public string FullName => Definition.FullName;
    public object ModuleId => Definition.Module;
    public bool IsFrameworkModule => CecilProjector.IsFrameworkAssembly(Definition.Module.Assembly.Name.Name);

    CecilSlotType? _base;
    bool _baseResolved;
    public ISlotType? BaseType
    {
        get
        {
            if (!_baseResolved)
            {
                _baseResolved = true;
                // Resolve() follows the module's assembly resolver — CROSS-MODULE bases (a generic base in
                // another proxy DLL) resolve here. An unresolvable base yields null, so the base-chain walk stops.
                TypeDefinition? bt = null;
                try { bt = Definition.BaseType?.Resolve(); } catch { /* unresolvable base -> stop the walk */ }
                _base = bt is null ? null : _p.Type(bt);
            }
            return _base;
        }
    }

    IReadOnlyList<ISlotMethod>? _virtuals;
    public IReadOnlyList<ISlotMethod> VirtualMethods =>
        _virtuals ??= Definition.Methods.Where(m => m.IsVirtual).Select(m => (ISlotMethod)_p.Method(m)).ToList();
}

public sealed class CecilSlotMethod : ISlotMethod
{
    readonly CecilProjector _p;
    public MethodDefinition Definition { get; }

    public CecilSlotMethod(MethodDefinition md, CecilProjector p) { Definition = md; _p = p; }

    public ISlotType DeclaringType => _p.Type(Definition.DeclaringType);
    public string Name => Definition.Name;

    IReadOnlyList<string>? _params;
    public IReadOnlyList<string> ParameterTypeNames =>
        _params ??= Definition.Parameters.Select(pp => pp.ParameterType.FullName).ToList();

    public bool IsNewSlot => Definition.IsNewSlot;
    public bool HasBody => Definition.HasBody;
    public bool HasExceptionHandler => Definition.HasBody && Definition.Body.ExceptionHandlers.Count > 0;
    public bool HasReturn => Definition.HasBody && Definition.Body.Instructions.Any(i => i.OpCode == OpCodes.Ret);
}
