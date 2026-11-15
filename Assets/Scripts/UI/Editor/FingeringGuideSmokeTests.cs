using System;
using System.Collections;
using System.Reflection;
using Google.XR.Extensions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEditor.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[InitializeOnLoad]
public static class FingeringGuideSmokeTests
{
    const string MainScene = "Assets/Scenes/Main_2.unity";
    const string CalibrationPref = "PianoOverlayCalibration";
    const string Running = "FingeringGuideSmokeTests.Running";
    const string XrStartupState = "FingeringGuideSmokeTests.XrStartup";
    const string StartSceneState = "FingeringGuideSmokeTests.StartScene";
    const string StreamingState = "FingeringGuideSmokeTests.Streaming";
    const string FingeringPref = "PianoFingeringOverlay";
    const string FingeringState = "FingeringGuideSmokeTests.FingeringPref";
    static IEnumerator _checks;
    static CustomYieldInstruction _wait;

    static FingeringGuideSmokeTests() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

    [MenuItem("Tools/Piano/Run Fingering Smoke Test")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool(Running, false))
        {
            Debug.LogWarning("[Fingering smoke] Start this check from Edit mode.");
            return;
        }

        var main = AssetDatabase.LoadAssetAtPath<SceneAsset>(MainScene);
        Require(main, $"Missing scene {MainScene}.");
        SessionState.SetString(StartSceneState, AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene) ?? "");
        EditorSceneManager.playModeStartScene = main;

        // Use simulated hands without connecting the Editor to the puck or requiring an OpenXR runtime.
        var xr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        if (xr != null)
        {
            SessionState.SetInt(XrStartupState, xr.InitManagerOnStart ? 1 : 0);
            xr.InitManagerOnStart = false;
        }
        SuspendStreaming();
        SessionState.SetInt(FingeringState, PlayerPrefs.GetInt(FingeringPref, -1));
        PlayerPrefs.SetInt(FingeringPref, 1);
        SessionState.SetBool(Running, true);
        Debug.Log("[Fingering smoke] Starting Main_2 with simulated hands.");
        EditorApplication.isPlaying = true;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (!SessionState.GetBool(Running, false)) return;
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            _checks = CheckMainScene();
            EditorApplication.update += RunNextCheck;
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            EditorApplication.update -= RunNextCheck;
            SessionState.EraseBool(Running);
            RestoreEditorSettings();
        }
    }

    static void RunNextCheck()
    {
        try
        {
            if (_wait != null && _wait.keepWaiting) return;
            if (_checks.MoveNext())
            {
                _wait = _checks.Current as CustomYieldInstruction;
                return;
            }
            Debug.Log("[Fingering smoke] PASS: startup, circular badges, fingertip cues, glow, toggle, recalibration, and Free Play.");
        }
        catch (Exception error)
        {
            Debug.LogError($"[Fingering smoke] FAIL: {error.Message}");
        }

        EditorApplication.update -= RunNextCheck;
        _checks = null;
        _wait = null;
        EditorApplication.isPlaying = false;
    }

    static IEnumerator CheckMainScene()
    {
        var flow = UnityEngine.Object.FindAnyObjectByType<AppFlow>();
        Require(flow && flow.gameObject.scene.path == MainScene, "Main_2 AppFlow did not load.");
        flow.startDelay = 0f;
        flow.voiceInput = null;
        flow.voiceOutput = null;
        flow.engine.sendToCoach = false;
        yield return WaitFor(() => flow.CurrentStep == AppFlow.Step.Calibrating, "calibration prompt");

        var guide = flow.GetComponent("FingeringGuide");
        Require(guide, "Main_2 is missing FingeringGuide.");
        var simulatedHands = guide.GetType().GetField("_simulateHands", BindingFlags.Instance | BindingFlags.NonPublic);
        Require(simulatedHands != null, "FingeringGuide is missing its Editor hand simulation.");
        simulatedHands.SetValue(guide, true);

        Calibrate(flow.calibrator);
        Require(!flow.calibrator.IsCalibrating, "Simulated marker calibration did not finish.");
        AppModeSwitch.Request(AppMode.Lesson, "Fingering smoke test");
        Require(flow.CurrentStep == AppFlow.Step.Lesson, "Lesson mode did not start.");

        var eBadge = FindLabel(flow, "Key badge R5");
        var eTip = FindLabel(flow, "Fingertip R5");
        var dSharpBadge = FindLabel(flow, "Key badge R4");
        var dSharpTip = FindLabel(flow, "Fingertip R4");
        var ring = flow.transform.Find("Key ring R5").GetComponent<SpriteRenderer>();

        yield return WaitFor(() => eBadge.enabled, "R5 cue on opening E5");
        AssertCue(eBadge, eTip, "R5", "5", flow.overlay.GetKeyWorldCenter(76));
        Require(ring.enabled && ring.sprite, "The R5 badge has no circular outline.");
        Require(Vector3.Dot(-ring.transform.forward, flow.overlay.transform.up) > 0.99f, "The circle is not flat on the key.");
        eTip.ForceMeshUpdate();
        eBadge.ForceMeshUpdate();
        Require(eTip.textBounds.size.x * eTip.transform.lossyScale.x < 0.008f, "Fingertip number is wider than 8 mm.");
        Require(eBadge.textBounds.size.x * eBadge.transform.lossyScale.x < 0.013f, "Key badge text is wider than 13 mm.");
        yield return WaitFor(() => dSharpBadge.enabled, "R4 cue on D sharp 5");
        AssertCue(dSharpBadge, dSharpTip, "R4", "4", flow.overlay.GetKeyWorldCenter(75));

        yield return WaitFor(() => flow.engine.CurrentRun != null && eBadge.enabled, "count-in preview");
        AssertCue(eBadge, eTip, "R5", "5", flow.overlay.GetKeyWorldCenter(76));

        var glow = flow.transform.Find("Fingertip glow R5").GetComponent<SpriteRenderer>();
        yield return WaitFor(() => glow.enabled && glow.color.a > 0f, "translucent glow near E5");
        Require(glow.color.a < 0.5f, "The fingertip glow is too opaque.");

        var toggle = flow.transform.Find("Fingering Toggle").GetComponent<XRSimpleInteractable>();
        var toggleLabel = toggle.GetComponentInChildren<TextMeshPro>();
        Require(toggle.isActiveAndEnabled && toggleLabel.text == "Fingering On", "The Fingering toggle is missing.");
        var currentRun = flow.engine.CurrentRun;
        toggle.selectEntered.Invoke(new SelectEnterEventArgs());
        Require(!eTip.enabled && !eBadge.enabled && !ring.enabled && !glow.enabled, "The toggle left a fingering cue visible.");
        Require(toggleLabel.text == "Fingering Off" && PlayerPrefs.GetInt(FingeringPref) == 0, "Off was not displayed and saved.");
        Require(flow.engine.CurrentRun == currentRun && flow.overlay.isActiveAndEnabled, "Turning fingering off changed the lesson.");
        toggle.selectEntered.Invoke(new SelectEnterEventArgs());
        yield return WaitFor(() => eTip.enabled && eBadge.enabled && ring.enabled, "fingering to return");
        Require(toggleLabel.text == "Fingering On" && PlayerPrefs.GetInt(FingeringPref) == 1, "On was not displayed and saved.");

        flow.calibrator.Recalibrate();
        yield return WaitFor(() => !toggle.gameObject.activeSelf && !eTip.enabled && !dSharpTip.enabled && !eBadge.enabled && !dSharpBadge.enabled,
            "fingering to hide while recalibrating");
        Calibrate(flow.calibrator);

        AppModeSwitch.Request(AppMode.FreePlay, "Fingering smoke test");
        Require(flow.CurrentStep == AppFlow.Step.FreePlay, "Free Play did not start.");
        yield return WaitFor(() => !toggle.gameObject.activeSelf && !eTip.enabled && !dSharpTip.enabled && !eBadge.enabled && !dSharpBadge.enabled,
            "fingering to hide in Free Play");
    }

    static void RestoreEditorSettings()
    {
        string startScene = SessionState.GetString(StartSceneState, null);
        if (startScene != null)
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(startScene)
                ? null
                : AssetDatabase.LoadAssetAtPath<SceneAsset>(startScene);
        SessionState.EraseString(StartSceneState);
        int startup = SessionState.GetInt(XrStartupState, -1);
        var xr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        if (xr != null && startup >= 0) xr.InitManagerOnStart = startup == 1;
        SessionState.EraseInt(XrStartupState);
        RestoreStreaming();
        int fingering = SessionState.GetInt(FingeringState, -1);
        if (fingering < 0) PlayerPrefs.DeleteKey(FingeringPref);
        else PlayerPrefs.SetInt(FingeringPref, fingering);
        PlayerPrefs.Save();
        SessionState.EraseInt(FingeringState);
    }

    static SerializedObject GetStreamingSettings()
    {
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        var feature = settings ? settings.GetFeature<XRStreamingFeature>() : null;
        return feature ? new SerializedObject(feature) : null;
    }

    static void SuspendStreaming()
    {
        using var settings = GetStreamingSettings();
        if (settings == null) return;
        var autoStart = settings.FindProperty("_autoStartOnPlay");
        var autoStop = settings.FindProperty("_autoStopOnExit");
        if (autoStart == null || autoStop == null) return;
        SessionState.SetInt(StreamingState, (autoStart.boolValue ? 1 : 0) | (autoStop.boolValue ? 2 : 0));
        autoStart.boolValue = false;
        autoStop.boolValue = false;
        settings.ApplyModifiedPropertiesWithoutUndo();
    }

    static void RestoreStreaming()
    {
        int state = SessionState.GetInt(StreamingState, -1);
        SessionState.EraseInt(StreamingState);
        if (state < 0) return;
        using var settings = GetStreamingSettings();
        if (settings == null) return;
        settings.FindProperty("_autoStartOnPlay").boolValue = (state & 1) != 0;
        settings.FindProperty("_autoStopOnExit").boolValue = (state & 2) != 0;
        settings.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssetIfDirty(settings.targetObject);
    }

    static CustomYieldInstruction WaitFor(Func<bool> condition, string stage)
    {
        double deadline = EditorApplication.timeSinceStartup + 12;
        return new WaitUntil(() =>
        {
            if (condition()) return true;
            Require(EditorApplication.timeSinceStartup < deadline, $"Timed out waiting for {stage} in Main_2.");
            return false;
        });
    }

    static TextMeshPro FindLabel(AppFlow flow, string objectName)
    {
        var label = flow.transform.Find(objectName)?.GetComponent<TextMeshPro>();
        Require(label, $"Missing label {objectName}.");
        return label;
    }

    static void AssertCue(TextMeshPro badge, TextMeshPro tip, string badgeText, string tipText, Vector3 keyCenter)
    {
        Require(badge.enabled && tip.enabled, $"The {badgeText} cue is hidden.");
        Require(badge.text == badgeText && tip.text == tipText, $"Incorrect label text for {badgeText}.");
        Require(tip.color != badge.color, $"The {badgeText} badge should be distinct from the fingertip color.");
        Require(Vector3.Distance(badge.transform.position, keyCenter) < 0.04f, $"{badgeText} is on the wrong key.");
    }

    static void Calibrate(PianoCalibrator calibrator)
    {
        var camera = Camera.main;
        Require(camera, "Main_2 is missing its camera.");
        var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        var right = Vector3.Cross(Vector3.up, forward);
        var center = camera.transform.position + forward * 0.65f + Vector3.down * 0.32f;

        bool hadCalibration = PlayerPrefs.HasKey(CalibrationPref);
        string savedCalibration = PlayerPrefs.GetString(CalibrationPref);
        try
        {
            string error = calibrator.CalibrateFromMarkers(center - right * 0.62f, center + right * 0.62f);
            Require(error == null, error);
        }
        finally
        {
            if (hadCalibration) PlayerPrefs.SetString(CalibrationPref, savedCalibration);
            else PlayerPrefs.DeleteKey(CalibrationPref);
            PlayerPrefs.Save();
        }
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
