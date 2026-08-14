using Inutil.Schema;
using Task = System.Threading.Tasks.Task;

namespace Inutil.Schema.Tests;

// Classifier unit tests: the registry finds the right family by anchor name AND an honest CanBind probe,
// surfaces its shape/direction/BclOpenType, and returns null when nothing binds — including the "never
// hard-bind" case where the name matches but the instantiation is still open.
static class ClassifyTests
{
    public static void Run()
    {
        var reg = new CorrespondenceRegistry()
            .Register(new TypeCorrespondence("Il2CppSystem.Threading.Tasks.Task`1", typeof(Task<>), ConvKind.Task, new FakeTaskBridge()))
            .Register(new TypeCorrespondence("Il2CppSystem.Nullable`1", typeof(Nullable<>), ConvKind.Nullable, new FakeNullableBridge()))
            .Register(new TypeCorrespondence("Il2CppSystem.Collections.Generic.IEnumerable`1", typeof(IEnumerable<>), ConvKind.Enumerable, new FakeEnumerableBridge()));

        var int32 = FakeTypeRef.Simple("System.Int32");
        var taskInt = FakeTypeRef.Generic("Il2CppSystem.Threading.Tasks.Task`1", int32);
        var nullInt = FakeTypeRef.Generic("Il2CppSystem.Nullable`1", int32);
        var enumInt = FakeTypeRef.Generic("Il2CppSystem.Collections.Generic.IEnumerable`1", int32);
        var listInt = FakeTypeRef.Generic("Il2CppSystem.Collections.Generic.List`1", int32);
        var taskOpen = FakeTypeRef.Generic("Il2CppSystem.Threading.Tasks.Task`1", FakeTypeRef.Param("T"));

        // Each shape routes to its family.
        var task = reg.Classify(taskInt);
        T.Check("Task`1<int> -> ReferenceBespoke", task?.Shape == BridgeShape.ReferenceBespoke);
        T.Check("Task correspondence direction Il2CppToBcl", task?.Direction == Directionality.Il2CppToBcl);
        T.Check("Task correspondence carries typeof(Task<>)", task?.BclOpenType == typeof(Task<>));

        T.Check("Nullable`1<int> -> ValueLayout", reg.Classify(nullInt)?.Shape == BridgeShape.ValueLayout);
        T.Check("IEnumerable`1<int> -> ContainerAdapter", reg.Classify(enumInt)?.Shape == BridgeShape.ContainerAdapter);

        // No false positives / no binding where nothing should.
        T.Check("unregistered List`1<int> -> null", reg.Classify(listInt) is null);
        T.Check("plain unmatched type -> null", reg.Classify(FakeTypeRef.Simple("Il2CppFoo.Bar")) is null);

        // The "never hard-bind, always probe" rule: right anchor name, but the argument is open -> null.
        T.Check("never hard-bind: open Task`1<!T> -> null (probe rejects)", reg.Classify(taskOpen) is null);
    }
}
