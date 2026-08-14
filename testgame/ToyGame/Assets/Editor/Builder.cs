// Headless build entry for batchmode:
//   Unity.exe -batchmode -quit -projectPath <proj> -executeMethod Builder.BuildWin64IL2CPP [-out <dir>]
// Configures IL2CPP / win-x64 / minimal stripping, ensures a one-object scene with the Bootstrap component, and
// builds. Produces ToyGame.exe + ToyGame_Data + GameAssembly.dll + global-metadata.dat — the real il2cpp surface
// inutil validates against.
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class Builder
{
    const string ScenePath = "Assets/Scenes/Main.unity";

    [MenuItem("Build/Win64 IL2CPP")]
    public static void BuildWin64IL2CPP()
    {
        var group = BuildTargetGroup.Standalone;
        PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP);
        // Release (not Debug): a Debug IL2CPP build emits atypical native codegen that breaks Il2CppInterop's
        // function-signature scan (e.g. `Class::Init signatures exhausted`), deadlocking real Il2CppInterop loaders
        // (BepInEx/MelonLoader) at startup. Release mirrors a shipped game's codegen, which Il2CppInterop targets.
        PlayerSettings.SetIl2CppCompilerConfiguration(group, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetManagedStrippingLevel(group, ManagedStrippingLevel.Minimal);
        PlayerSettings.SetApiCompatibilityLevel(group, ApiCompatibilityLevel.NET_Standard_2_0);
        // Incremental GC: Boehm in INCREMENTAL mode makes a GC write barrier REQUIRED when storing object refs into
        // live heap value-type storage — e.g. a ref-bearing struct written back through a ref/out param. Mirror that
        // so the barrier case is actually exercised by the fixture instead of accidentally passing under non-incremental GC.
        PlayerSettings.gcIncremental = true;

        EnsureScene();

        string outDir = GetArg("-out") ?? "Build/Win64";
        Directory.CreateDirectory(outDir);
        string exe = Path.Combine(outDir, "ToyGame.exe");

        var opts = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = exe,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = group,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(opts);
        var s = report.summary;
        Debug.Log($"[Builder] result={s.result} errors={s.totalErrors} size={s.totalSize} out={Path.GetFullPath(exe)}");
        EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
    }

    static void EnsureScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var go = new GameObject("Bootstrap");
        go.AddComponent<Bootstrap>();
        EditorSceneManager.SaveScene(scene, ScenePath);
    }

    static string GetArg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
        return null;
    }
}
