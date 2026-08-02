using Mono.Cecil;
using Inutil.Schema;

namespace Inutil.InteropPatch;

// One member the patch left wearing an il2cpp type a family COULD have naturalized, and the reason it is still there.
//   Known  = a shape this build DELIBERATELY defers (virtual, open-generic element, static field-backed setter).
//   Unexplained = no known deferral reason applies. That is a HOLE: a member a consumer meets with the raw
//   Il2CppInterop spelling (for Nullable, a broken one — an empty Nullable NREs, a present one reads HasValue=False).
public sealed record Residual(string Module, string Member, string Why, bool Unexplained);

// The post-patch audit: after every family has run, is any member STILL wearing an il2cpp type we can naturalize?
//
// This exists because the passes could only ever report on what they LOOKED AT. A member no pass classified was
// invisible: it produced no flip, no defer, no log line — it simply did not appear. Two holes hid exactly there: the
// method-backed Nullable accessor (deferred while its backing field flipped, so the log looked busy and correct) and
// the static container setter (deferred by a hardcoded ldarg.1, 529 of them in one real game, indistinguishable from
// routine defer noise).
//
// Phrased against the FACT — "no member is left wearing a naturalizable il2cpp type without being named" — not
// against any pass's internals, so a future family/backing/renderer that slips through is caught without anyone
// editing this check.
//
// The families are asked THEMSELVES whether a member was naturalizable, never a name list here:
//   * Nullable  — the correspondence registry classifies it (ConvKind.Nullable).
//   * Container — ContainerFlip.NaturalReturn returns non-null ONLY for a container this build can fully naturalize,
//     so the by-design refusals (a family not in Families.Default() like Queue/Stack, a read-only spelling like
//     IEnumerable, a non-naturalizable element, an open generic arg) are excluded at the source rather than
//     re-enumerated here — where they would rot the moment a family is added.
//
// It is a REPORT, not a gate: virtual accessors are deferred on purpose today, so failing would fail every patch.
// Unexplained is the number to act on.
public static class ResidualAudit
{
    static readonly CorrespondenceRegistry _families = Families.Default();

    public static IReadOnlyList<Residual> Scan(ModuleDefinition module)
    {
        var found = new List<Residual>();
        // GAME-scoped, like every rewriter: a framework proxy's own BCL types are not ours to flip.
        if (CecilProjector.IsFrameworkAssembly(module.Assembly.Name.Name)) return found;

        string mod = module.Assembly.Name.Name;
        foreach (TypeDefinition type in module.GetTypes())
        {
            foreach (FieldDefinition f in type.Fields)
                if (Naturalizable(module, f.FieldType) is { } fk)
                    found.Add(Classify(mod, $"{type.FullName}::{f.Name}", f.FieldType, virt: false, kind: $"{fk} field"));

            foreach (PropertyDefinition p in type.Properties)
                if (Naturalizable(module, p.PropertyType) is { } pk)
                    found.Add(Classify(mod, $"{type.FullName}::{p.Name}", p.PropertyType,
                        virt: (p.GetMethod?.IsVirtual ?? false) || (p.SetMethod?.IsVirtual ?? false), kind: $"{pk} property",
                        // The one shape NullableFieldRewriter deliberately declines: no static-field write helper.
                        staticFieldBackedSetter: p.SetMethod is { IsStatic: true } s && IsFieldBacked(s)));

            foreach (MethodDefinition m in type.Methods)
            {
                // Accessors are reported through their property (above) — reporting both doubles every hit.
                if (m.IsGetter || m.IsSetter) continue;

                if (Naturalizable(module, m.ReturnType) is { } rk)
                    found.Add(Classify(mod, $"{type.FullName}::{m.Name} (return)", m.ReturnType, m.IsVirtual, $"{rk} return"));

                foreach (ParameterDefinition p in m.Parameters)
                    if (Naturalizable(module, p.ParameterType) is { } ak)
                        found.Add(Classify(mod, $"{type.FullName}::{m.Name}({p.Name}) (param)", p.ParameterType, m.IsVirtual, $"{ak} param"));
            }
        }
        return found;
    }

    // Which family could have naturalized this type, or null if none can (in which case it is not residual at all).
    static string? Naturalizable(ModuleDefinition module, TypeReference? t)
    {
        if (t is null) return null;
        if (IsIl2CppNullable(t)) return "nullable";
        // Ask the container family itself — non-null means THIS build can produce a fully natural spelling for it.
        if (t is GenericInstanceType && ContainerFlip.NaturalReturn(module, t) is not null) return "container";
        return null;
    }

    // Attribute a residual to a KNOWN deferral, or mark it unexplained. This list is the audit's one hand-maintained
    // seam: a guardrail removed from a pass must be removed here too, or a real hole starts reading as expected.
    static Residual Classify(string module, string member, TypeReference type, bool virt, string kind,
                             bool staticFieldBackedSetter = false)
    {
        TypeReference? elem = (type as GenericInstanceType)?.GenericArguments.FirstOrDefault();
        // NB — this arm is now COARSE. Virtual Nullable RETURNS and virtual Nullable ACCESSORS both flip in lockstep
        // today, so a virtual residual is no longer explained by "virtual" alone: the only legitimate reasons left
        // are the planner's slot gates (ExternalRoot — the slot's true root is a base we cannot see; or a
        // FRAMEWORK-module root we never flip). A virtual member deferred for any OTHER reason is a hole this arm
        // will wrongly report as expected. Tightening it means resolving the slot root here, the planner's job —
        // until then, treat a rising virtual count as something to investigate, not to accept.
        if (virt)
            return new(module, member, $"{kind}: virtual — expected only if its slot root is external/framework; verify", false);
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
