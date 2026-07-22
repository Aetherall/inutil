using Inutil.InteropPatch;

namespace Inutil.InteropPatch.Tests;

// The framework-vs-game assembly classifier (CecilProjector.IsFrameworkAssembly) decides which proxy modules the
// patcher flips. It must classify by REAL framework identity, NOT a bare "Il2Cpp" prefix — because MelonLoader
// prefixes the game's OWN secondary modules (Il2CppToyGame.Core) while BepInEx leaves them bare (ToyGame.Core).
// A blanket "Il2Cpp*==framework" would defer the melon cross-module virtual Task ROOT (BackendBase`1, in
// Il2CppToyGame.Core) so the whole slot never flips. Pins the invariant against BOTH loaders' spellings: a GAME
// module is never framework merely for being Il2Cpp-prefixed, and every real framework proxy still is.
static class FrameworkAssemblyTests
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "  ok    " : "  WRONG ") + $"[{name}]"); if (!ok) failures++; }

        // Framework proxies — Il2Cpp-prefixed under both loaders (some Unity modules bare under melon) — ARE framework.
        foreach (string fw in new[]
        {
            "Il2CppSystem", "Il2CppSystem.Core", "Il2CppSystem.Xml", "Il2Cppmscorlib", "Il2CppInterop.Runtime",
            "Il2CppUnityEngine.CoreModule", "UnityEngine.CoreModule", "Il2CppMono.Security", "Il2CppNewtonsoft.Json",
            "Il2Cppnetstandard", "Il2Cpp__Generated", "__Generated",
        })
            Check($"framework (skip): {fw}", CecilProjector.IsFrameworkAssembly(fw));

        // GAME modules — never framework, INCLUDING the melon-prefixed secondary module. `Il2CppToyGame.Core` is the
        // regression the melon battery caught: it must be GAME so the cross-module virtual Task root flips.
        foreach (string game in new[] { "Assembly-CSharp", "ToyGame.Core", "Il2CppToyGame.Core", "Il2CppToyGame" })
            Check($"game (flip, NOT framework): {game}", !CecilProjector.IsFrameworkAssembly(game));

        return failures;
    }
}
