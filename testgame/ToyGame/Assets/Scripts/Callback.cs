// ToyGame fixture for the CALLBACK MIRROR (Inutil.Bridge.Callback<T> — the INBOUND twin of the Result mirror).
//
// ⚠ REQUIRES A FIXTURE REBUILD: these types only reach the battery after the ToyGame Unity project is rebuilt to
// IL2CPP (Assets/Editor/Builder.cs) and the battery is run under wine (tools/wine/validate.sh). The engine + offline
// gates are already green without them.
//
// The shape mirrors EFT's Comfort.Common.{Result,Callback}<T>: a game method HANDS a hook a Callback<Player[]> it must
// later invoke WITH a Result<Player[]>. Il2CppInterop reifies these over Il2CppReferenceArray<Player> (a broken close
// for a mod to spell), and there is no il2cpp->BCL delegate bridge to receive the callback as a System.Action — so the
// hook spells Inutil.Bridge.Callback<Player[]> and the seam (ContainerBridge.MaterializeCallback) marshals across.

namespace ToyGame
{
    // The game's result value-wrapper (EFT's Comfort.Common.Result<T> analog). Reference type; Il2CppInterop projects
    // Value/Error/ErrorCode as properties and generates ctors matching these — the per-game Mirror.RegisterResult shim
    // builds/reads it reflectively.
    public class Result<T>
    {
        public T Value;
        public string Error;
        public int ErrorCode;

        public Result(T value) { Value = value; Error = null; ErrorCode = 0; }
        public Result(T value, string error, int errorCode) { Value = value; Error = error; ErrorCode = errorCode; }

        public bool Succeed => Error == null;
    }

    // The game's callback (EFT's Comfort.Common.Callback<T> analog) — invoked WITH a Result<T>. Records the last
    // invocation so the battery has an oracle for "the mirror flipped the natural element, wrapped it in a Result, and
    // invoked the real game callback".
    public class Callback<T>
    {
        public bool Invoked;
        public Result<T> Last;

        public void Invoke(Result<T> result) { Invoked = true; Last = result; }
    }

    // A game service that HANDS OUT a callback. The default body invokes it empty (the UNHOOKED baseline: Invoked=true,
    // Count 0). A battery hook REPLACES RequestRoster and invokes cb via the Inutil.Bridge.Callback<Player[]> mirror.
    public class Roster
    {
        public void RequestRoster(Callback<Player[]> cb) => cb.Invoke(new Result<Player[]>(System.Array.Empty<Player>()));

        // Non-generic SCALAR decoders for the battery oracle: read what the callback received, typed over Player[] on the
        // GAME side, so the case reads scalars (count + first name) instead of poking an Il2CppReferenceArray reflectively.
        // No `out` param (an il2cpp `out` projects awkwardly through Il2CppInterop) — two plain returns instead.
        // DecodeCount: -1 = never invoked / null; else the received array length.
        public static int DecodeCount(Callback<Player[]> cb)
            => (cb == null || !cb.Invoked || cb.Last == null || cb.Last.Value == null) ? -1 : cb.Last.Value.Length;

        // DecodeFirst: the first received Player's Name, or "" when empty — the element-content oracle.
        public static string DecodeFirst(Callback<Player[]> cb)
            => (cb == null || !cb.Invoked || cb.Last == null || cb.Last.Value == null || cb.Last.Value.Length == 0)
                ? "" : cb.Last.Value[0].Name;
    }
}
