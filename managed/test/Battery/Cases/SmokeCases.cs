using Il2CppInterop.Runtime;

namespace Inutil.Battery;

// The end-to-end plumbing smoke: the minimum set of cases that proves the WHOLE loop is real —
// build -> deploy -> launch inside a booted IL2CPP game -> emit records -> collect -> aggregate — with no
// dependency on the inutil engine. As the engine's families come online, real marshalling/hooking cases join
// this same Suite; the smoke stays as the loop's own liveness check.
public static class SmokeCases
{
    public static void Register(Suite suite)
    {
        // The plugin loaded and the sink is writable. If this line makes it to disk at all, the deploy +
        // launch + sidecar path are all working — the floor of "our code ran".
        suite.Add("plumbing.alive", () => "battery loaded and the sink is writable");

        // Prove we're inside a genuinely booted IL2CPP game, not just any process: resolve a real game
        // class by its IL2CPP (not managed-proxy) name via il2cpp_class_from_name. Uses only
        // Il2CppInterop.Runtime — zero game-proxy references — so it can't fail for a proxy-binding reason.
        // A miss is a FAIL (not a skip): if we can't touch the runtime, the harness environment is broken
        // and we want that loud.
        suite.Add("il2cpp.runtime", () =>
        {
            IntPtr klass = ResolveClass("ToyGame", "Player", out var via);
            Check.True(klass != IntPtr.Zero,
                "ToyGame.Player did not resolve via any image-name spelling — IL2CPP runtime not up or metadata not loaded");
            return $"resolved ToyGame.Player @ 0x{klass.ToInt64():x} (image \"{via}\")";
        });

        // A live, on-demand RED. With INUTIL_SELFTEST_FAIL set, this case fails, proving the real
        // launch -> collect -> aggregate path turns the build red end-to-end — not just the offline unit
        // self-test. Off by default, so an ordinary validate run is green.
        suite.Add("canary.fail", () =>
        {
            if (Environment.GetEnvironmentVariable("INUTIL_SELFTEST_FAIL") is { Length: > 0 })
                Check.True(false, "INUTIL_SELFTEST_FAIL set — intentional canary failure to prove red works end-to-end");
            return "canary armed (set INUTIL_SELFTEST_FAIL=1 to force a red run)";
        });
    }

    // Il2CppInterop's image lookup is spelling-sensitive across versions; try both the ".dll"-suffixed and
    // bare image name so a smoke check never flakes on that detail.
    static IntPtr ResolveClass(string @namespace, string name, out string via)
    {
        foreach (var image in new[] { "Assembly-CSharp.dll", "Assembly-CSharp" })
        {
            var k = IL2CPP.GetIl2CppClass(image, @namespace, name);
            if (k != IntPtr.Zero) { via = image; return k; }
        }
        via = "(none)";
        return IntPtr.Zero;
    }
}
