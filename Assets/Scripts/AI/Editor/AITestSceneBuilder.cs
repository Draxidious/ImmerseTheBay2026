using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Creates Assets/Resources/AIConfig.asset (if missing) and the Assets/Scenes/AITest.unity test scene.
public static class AITestSceneBuilder
{
    const string ScenePath = "Assets/Scenes/AITest.unity";
    const string BaseScenePath = "Assets/Scenes/BasicScene.unity";
    const string ConfigPath = "Assets/Resources/AIConfig.asset";

    [MenuItem("Tools/AI Coach/Create Test Scene")]
    public static void Build()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var config = AssetDatabase.LoadAssetAtPath<AIConfig>(ConfigPath);
        if (config == null)
        {
            Directory.CreateDirectory("Assets/Resources");
            AssetDatabase.Refresh();
            config = ScriptableObject.CreateInstance<AIConfig>();
            AssetDatabase.CreateAsset(config, ConfigPath);
        }

        // Start from a copy of BasicScene so the test scene has the same XR setup.
        AssetDatabase.DeleteAsset(ScenePath);
        if (!AssetDatabase.CopyAsset(BaseScenePath, ScenePath))
        {
            Debug.LogError("[AI] Could not copy " + BaseScenePath + " to " + ScenePath);
            return;
        }
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var go = new GameObject("AI");
        var source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;

        var coach = go.AddComponent<AICoach>();
        var voiceOutput = go.AddComponent<VoiceOutput>();
        var voiceInput = go.AddComponent<VoiceInput>();
        var panel = go.AddComponent<AIDebugPanel>();

        coach.config = config;
        voiceOutput.config = config;
        voiceOutput.source = source;
        voiceInput.config = config;
        panel.coach = coach;
        panel.voiceOutput = voiceOutput;
        panel.voiceInput = voiceInput;

        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("[AI] Created " + ScenePath);
    }
}
