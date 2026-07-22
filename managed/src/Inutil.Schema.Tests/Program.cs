using Inutil.Schema.Tests;

// Standalone unit tests for the schema core: planner + classifier, over synthetic type shapes — no game, no
// wine, no real proxy. Dependency-free; exit 0 = all green, 1 = any wrong.

Console.WriteLine("── planner (§7.4) ──");
PlannerTests.Run();

Console.WriteLine("\n── classifier (§7.1) ──");
ClassifyTests.Run();

Console.WriteLine("\n── family registry: one source, three lookups (C1) ──");
FamiliesTests.Run();

Console.WriteLine("\n── registry drift guardrail (C4: no family strings outside Families.cs) ──");
RegistryDriftTest.Run();

Console.WriteLine("\n── wire classifier (metadata pillar §4.2/§8: member -> authored facts) ──");
WireClassifyTests.Run();

Console.WriteLine("\n── wire registry drift guardrail (§4.3: no attribute strings outside WireFamilies.cs) ──");
WireRegistryDriftTest.Run();

Console.WriteLine("\n── runtime isolation (§7: Inutil.dll has no edge to the metadata pillar / Cpp2IL) ──");
RuntimeIsolationTest.Run();

Console.WriteLine("\n── recursive conversion tree (§7.3) ──");
ConvTests.Run();

Console.WriteLine("\n── correspondence validation (§7.3 Conv.Il2CppType == gameElem) ──");
CorrespondenceValidatorTests.Run();

Console.WriteLine("\n── content-addressed marker (§7.2 / docs/contribution/architecture/16-metadata.md: registry hash + stamp/verify) ──");
MarkerTests.Run();

Console.WriteLine(T.Failures == 0
    ? "\nSCHEMA TESTS GREEN — planner is per-member and gates safely; classifier binds by name + honest probe."
    : $"\nSCHEMA TESTS RED — {T.Failures} wrong.");
return T.Failures == 0 ? 0 : 1;
