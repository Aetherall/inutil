using Il2CppInterop.Runtime;                       // Il2CppException — Il2CppInterop's own System.Exception over an il2cpp exception
using Il2CppInterop.Runtime.InteropTypes;          // Il2CppObjectBase — the base of the il2cpp exception proxy

namespace Inutil.Marshal;

// The System.Exception FACADE for ConvKind.Exception (Families.cs). A hook spells the natural System.Exception for a
// game Il2CppSystem.Exception param — but an Il2CppSystem.Exception is an Il2CppObjectBase, NOT a System.Exception, so
// it can't be identity-passed as one. This wraps the il2cpp exception whole: it IS-A System.Exception (so it binds the
// hook's param), its .Message / .ToString forward to Il2CppInterop's OWN Il2CppException over the same il2cpp exception
// pointer (game-AGNOSTIC — Il2CppInterop.Runtime, never an Il2CppSystem.* spelling, C4-clean), and it HOLDS the original
// Il2CppObjectBase so the write side (a hook Proceed-ing the same exception on) unwraps it back by identity
// (Il2CppConvRuntime.UnwrapException), never a reconstruction inutil couldn't name game-agnostically.
public sealed class GameException : System.Exception
{
    readonly Il2CppException _inner;               // Il2CppException(IntPtr) forwards Message/ToString to the game exception (its message + stack)

    public Il2CppObjectBase Il2Cpp { get; }

    public GameException(Il2CppObjectBase il2cppException)
    {
        Il2Cpp = il2cppException;
        _inner = new Il2CppException(il2cppException.Pointer);
    }

    public override string Message => _inner.Message;
    public override string ToString() => _inner.ToString();
}
