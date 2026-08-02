using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// One member the patch left wearing an il2cpp Nullable type, and the reason it is still there.
//   Known  = a shape this build DELIBERATELY defers (virtual accessor/return, open-generic element) — expected.
//   Unexplained = no known deferral reason applies. That is a HOLE: a member a consumer will meet with the broken
//   Il2CppInterop spelling (an empty Nullable NREs, a present one reads HasValue=False) and no way to write it.
public sealed record ResidualNullable(string Module, string Member, string Why, bool Unexplained);

// The post-patch audit: after every family has run, does any member STILL wear an il2cpp Nullable?
//
// This exists because the passes could only ever report on what they LOOKED AT. A member no pass classified was
// invisible: it produced no flip, no defer, no log line — it simply did not appear. That is exactly how the
// method-backed accessor hole survived (a Nullable auto-property's real get_/set_ deferred as "accessor not
// handleable" while its backing field flipped, so the patch log looked busy and correct). The audit is phrased
// against the FACT — "no member is left il2cpp-Nullable-typed without being named" — not against accessors, so a
// future family/backing/renderer that slips through is caught by the same check without anyone editing it.
//
// It is a REPORT, not a gate: virtual accessors and virtual Nullable returns are deferred on purpose today, so
// failing here would fail every patch. The distinction that matters is Unexplained — a residual with no known
// reason means a pass that should have covered it did not.
public static class ResidualNullableAudit
{
    static readonly CorrespondenceRegistry _families = Families.Default();

    public static IReadOnlyList<ResidualNullable> Scan(ModuleDefinition module)
    {
        var found = new List<ResidualNullable>();
        // GAME-scoped, like every rewriter: a framework proxy's own BCL Nullables are not ours to flip.
        if (CecilProjector.IsFrameworkAssembly(module.Assembly.Name.Name)) return found;

        string mod = module.Assembly.Name.Name;
        foreach (TypeDefinition type in module.GetTypes())
        {
            foreach (FieldDefinition f in type.Fields)
                if (IsIl2CppNullable(f.FieldType))
                    found.Add(Classify(mod, $"{type.FullName}::{f.Name}", f.FieldType,
                        virt: false, kind: "field"));

            foreach (PropertyDefinition p in type.Properties)
                if (IsIl2CppNullable(p.PropertyType))
                    found.Add(Classify(mod, $"{type.FullName}::{p.Name}", p.PropertyType,
                        virt: (p.GetMethod?.IsVirtual ?? false) || (p.SetMethod?.IsVirtual ?? false), kind: "property",
                        // A static setter over a FIELD-backed accessor is the one shape NullableFieldRewriter now
                        // deliberately declines (no static-field write helper) — known, not a hole.
                        staticFieldBackedSetter: p.SetMethod is { IsStatic: true } s && IsFieldBacked(s)));

            foreach (MethodDefinition m in type.Methods)
            {
                // Accessors are reported through their property (above) — reporting both doubles every hit.
                if (m.IsGetter || m.IsSetter) continue;

                if (IsIl2CppNullable(m.ReturnType))
                    found.Add(Classify(mod, $"{type.FullName}::{m.Name} (return)", m.ReturnType,
                        m.IsVirtual, kind: "return"));

                foreach (ParameterDefinition p in m.Parameters)
                    if (IsIl2CppNullable(p.ParameterType))
                        found.Add(Classify(mod, $"{type.FullName}::{m.Name}({p.Name}) (param)", p.ParameterType,
                            m.IsVirtual, kind: "param"));
            }
        }
        return found;
    }

    // Attribute a residual to a KNOWN deferral, or mark it unexplained. The known reasons are stated by the passes
    // themselves (virtual = vtable/interface lockstep not implemented; open-generic element = not concrete), so this
    // list must be kept honest: an entry removed from a pass's guardrails must be removed here too, or a real hole
    // starts reading as expected.
    static ResidualNullable Classify(string module, string member, TypeReference type, bool virt, string kind,
                                     bool staticFieldBackedSetter = false)
    {
        TypeReference? elem = (type as GenericInstanceType)?.GenericArguments[0];
        if (virt)
            return new(module, member, $"{kind}: virtual — deferred, needs the vtable/interface lockstep", false);
        if (staticFieldBackedSetter)
            return new(module, member, $"{kind}: static field-backed setter — deferred, no static-field write helper", false);
        if (elem is not null && elem.IsGenericParameter)
            return new(module, member, $"{kind}: open-generic element <{elem.Name}> — not a concrete type to flip", false);
        return new(module, member, $"{kind}: NO KNOWN DEFERRAL REASON — a pass should have covered this", true);
    }

    // Field-backed == the accessor body locates a FIELD (NativeFieldInfoPtr), the same discriminator
    // NullableFieldRewriter dispatches on. Kept in sync with it by shape, not by a copied member list.
    static bool IsFieldBacked(MethodDefinition accessor)
        => accessor.HasBody && accessor.Body.Instructions.Any(i =>
               i.OpCode == Mono.Cecil.Cil.OpCodes.Ldsfld && i.Operand is FieldReference fr
               && fr.Name.StartsWith("NativeFieldInfoPtr", StringComparison.Ordinal));

    static bool IsIl2CppNullable(TypeReference? t)
        => t is GenericInstanceType && _families.Classify(CecilTypeRef.Of(t)) is { Kind: ConvKind.Nullable };
}
