using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Builds Builds/WakeWordSpike.apk from Dev_Canady_WakeWord.unity only, without touching the build scene list.
// Same package name as Main, so installing it replaces Main on the puck until you rebuild Main.
public static class WakeWordSpikeBuild
{
    const string ScenePath = "Assets/Scenes/Dev_Canady_WakeWord.unity";
    const string ApkPath = "Builds/WakeWordSpike.apk";

    [MenuItem("Tools/Wake Word/Create Spike Scene")]
    public static void CreateScene()
    {
        if (File.Exists(ScenePath))
        {
            Debug.Log("[WakeWord] Spike scene already exists: " + ScenePath);
            return;
        }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var cam = new GameObject("Main Camera", typeof(Camera)).GetComponent<Camera>();
        cam.tag = "MainCamera";
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0, 0, 0, 0);
        new GameObject("WakeWordSpike", typeof(WakeWordSpike));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[WakeWord] Created " + ScenePath);
    }

    [MenuItem("Tools/Wake Word/Build Spike APK")]
    public static void Build()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            Debug.LogError("[WakeWord] Switch the build target to Android first.");
            return;
        }
        CreateScene();
        Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = ApkPath,
            target = BuildTarget.Android,
            options = BuildOptions.Development
        });
        Debug.Log($"[WakeWord] Build {report.summary.result}: {ApkPath} ({report.summary.totalSize / (1024 * 1024)} MB)");
    }
}
