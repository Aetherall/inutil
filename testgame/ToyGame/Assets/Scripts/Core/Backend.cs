// ToyGame.Core — a SEPARATE assembly-definition (compiles to ToyGame.Core.dll, its own Il2CppInterop proxy module).
// It exists solely to give the CROSS-MODULE virtual-Task-return case a cross-module vtable slot: the generic base
// BackendBase<T> declares the virtual Task<Session>-returning slot HERE, and Assembly-CSharp's Backend<T> overrides
// it THERE — so the override's slot root lives in a different proxy module. That split makes PatchVirtualTaskReturns'
// ResolveSlot bucket the override "external" (measured: a single-assembly generic interface impl is instead a NewSlot
// that flips fine). Session is the closed return type kept in THIS assembly so BackendBase need not reference
// Assembly-CSharp (a circular asmdef reference).
using System.Threading.Tasks;

namespace ToyGame
{
    // The closed Task<Session> result type (a reference proxy) — lives in ToyGame.Core.dll, a DIFFERENT module than the
    // Backend<T> override in Assembly-CSharp, mirroring EFT's IEftSession-in-another-assembly shape.
    public class Session
    {
        public string Name;
        public Session(string name) { Name = name; }
    }

    // The generic base that DECLARES the virtual Task<Session> slot. Backend<T> (Assembly-CSharp) overrides it, so the
    // override's slot ROOT is BackendBase`1::OpenSession here in ToyGame.Core.dll — the cross-module root that trips
    // ResolveSlot's `root.DeclaringType.Module == module` gate.
    public abstract class BackendBase<T>
    {
        public T Tag;
        public abstract Task<Session> OpenSession();
    }
}
