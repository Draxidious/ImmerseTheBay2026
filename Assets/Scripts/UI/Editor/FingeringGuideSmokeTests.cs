using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Google.XR.Extensions;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEditor.XR.Management;
using UnityEngine.XR.OpenXR;
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
            Debug.Log("[Fingering smoke] PASS: tracked swipes, center cancellation, confirmations, ray exclusion, fingering pulse, and sheet handoff.");
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

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var guide = flow.GetComponent("FingeringGuide");
        Require(guide, "Main_2 is missing FingeringGuide.");
        guide.GetType().GetField("_simulateHands", flags).SetValue(guide, true);
        var menus = UnityEngine.Object.FindObjectsByType<RadialMenu>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Require(menus.Length == 1, "Expected one radial menu.");
        var menu = menus[0];
        var anchor = menu.transform.Find("Left Hand Menu");
        var root = anchor.Find("RadialMenuRoot");
        Require(root.childCount == 7 && root.Find("VisualsSegment"), "Expected six actions and one confirmation branch.");
        Require(anchor.GetComponentsInChildren<XRSimpleInteractable>(true).Length == 0 &&
            !anchor.GetComponentsInChildren<Collider>(true).Any(c => c.enabled), "The wrist menu accepts ray interaction.");
        var objects = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Require(!objects.Any(t => t.name == "SwitchModeButton" || t.name == "LessonButton" ||
            t.name == "FreePlayButton" || t.name == "Fingering Toggle"), "A duplicate mode or fingering button was built.");
        Require(!flow.calibrator.overlayGrab && !flow.overlay.transform.Find("GrabHandle"), "The old drag handle is present.");

        var simulateHand = menu.GetType().GetField("_simulateMenuHand", flags);
        var handOffset = menu.GetType().GetField("_simulatedHandOffset", flags);
        var updateSwipe = menu.GetType().GetMethod("UpdateSwipe", flags);
        simulateHand.SetValue(menu, true);
        Vector3 Direction(RadialMenu.Choice choice) => root.Find(choice + "Segment").localPosition.normalized;
        void Sample(Vector3 local) => updateSwipe.Invoke(menu, new object[] { anchor.TransformPoint(local) });
        void Touch() { Sample(Vector3.back * 0.05f); Sample(Vector3.zero); }
        void Lift(Vector3 local) => Sample(local + Vector3.back * 0.05f);
        void Swipe(RadialMenu.Choice choice, float distance = 0.065f)
        {
            Touch();
            Vector3 target = Direction(choice) * distance;
            Sample(target);
            Lift(target);
        }

        var config = AIConfig.Get();
        var startup = config.defaultMode;
        try { config.defaultMode = StartMode.Ask; Calibrate(flow.calibrator); }
        finally { config.defaultMode = startup; }
        yield return WaitFor(() => anchor.gameObject.activeSelf, "the left-hand Menu button");
        Sample(Vector3.back * 0.05f);
        Sample(Vector3.forward * 0.045f);
        Require(menu.IsOpen && flow.CurrentStep == AppFlow.Step.ChooseMode, "Skipping past the menu face should still open the menu.");
        Require(root.Find("FingeringSegment/Label").GetComponent<TextMeshPro>().text == "Fingering\nLesson only",
            "The startup fingering label does not explain why it is unavailable.");
        Lift(Vector3.zero);
        Vector3 previousCenter = anchor.position;
        handOffset.SetValue(menu, new Vector3(0.07f, 0.01f, 0f));
        yield return WaitFor(() => Vector3.Distance(previousCenter, anchor.position) > 0.05f, "the open menu to follow the hand");
        Require(root.parent == anchor && root.localPosition == Vector3.zero, "The ring detached from the hand.");

        Swipe(RadialMenu.Choice.RestartLesson, 0.025f);
        Require(menu.IsOpen && flow.CurrentStep == AppFlow.Step.ChooseMode, "A short movement selected an action.");
        Touch();
        Sample(Direction(RadialMenu.Choice.RestartLesson) * 0.065f + Vector3.forward * 0.06f);
        Require(flow.CurrentStep == AppFlow.Step.ChooseMode, "The action fired before lift.");
        Sample(Vector3.zero);
        Lift(Vector3.zero);
        Require(menu.IsOpen && flow.CurrentStep == AppFlow.Step.ChooseMode, "Returning to center selected an action.");

        Touch();
        Vector3 stationaryTip = anchor.position;
        Vector3 originalAnchor = anchor.position;
        anchor.position -= anchor.TransformVector(Direction(RadialMenu.Choice.RestartLesson) * 0.065f);
        updateSwipe.Invoke(menu, new object[] { stationaryTip });
        updateSwipe.Invoke(menu, new object[] { stationaryTip - anchor.forward * 0.05f });
        anchor.position = originalAnchor;
        Require(menu.IsOpen && flow.CurrentStep == AppFlow.Step.ChooseMode, "Wrist movement alone selected an action.");

        Sample(Vector3.back * 0.05f);
        Sample(Vector3.forward * 0.045f);
        Vector3 startTarget = Direction(RadialMenu.Choice.RestartLesson) * 0.065f;
        Sample(startTarget + Vector3.forward * 0.06f);
        Require(flow.CurrentStep == AppFlow.Step.ChooseMode, "Pushing through the plane committed an action before withdrawal.");
        Lift(startTarget);
        Require(flow.CurrentStep == AppFlow.Step.Lesson && !menu.IsOpen, "The Start Lesson swipe did not work.");
        var badge = FindLabel(flow, "Key badge R5");
        var nextBadge = FindLabel(flow, "Key badge R4");
        var glow = flow.transform.Find("Fingertip glow R5").GetComponent<SpriteRenderer>();
        Require(!flow.transform.Find("Fingertip R5"), "A fingertip number is still visible.");
        yield return WaitFor(() => badge.enabled, "R5 on E5");
        Require(badge.text == "R5" && Vector3.Distance(badge.transform.position, flow.overlay.GetKeyWorldCenter(76)) < 0.04f,
            "R5 is on the wrong key.");
        yield return WaitFor(() => nextBadge.enabled, "R4 on D sharp 5 during the demo");
        yield return WaitFor(() => flow.engine.CurrentRun != null && badge.enabled, "first count-in cue");

        var press = guide.GetType().GetMethod("OnNotePlayed", flags);
        var note = flow.engine.CurrentRun.notes[0];
        yield return WaitFor(() =>
        {
            if (glow.enabled && glow.color.a > 0.95f && glow.color.g > glow.color.r && glow.color.g > glow.color.b)
                return true;
            press.Invoke(guide, new object[] { note, note.midi, true });
            return false;
        }, "the bright correct-press pulse");

        Swipe(RadialMenu.Choice.Fingering);
        Require(!badge.enabled && !glow.enabled && !menu.IsOpen && PlayerPrefs.GetInt(FingeringPref) == 0,
            "Turning off fingering left a cue active.");
        var coachAction = flow.engine.GetType().GetMethod("HandleCoachAction", flags);
        var stats = (TMP_Text)flow.sheetDisplay.GetType().GetField("_stats", flags).GetValue(flow.sheetDisplay);
        var help = new CoachAction { type = CoachActionType.ShowFingering, firstBar = 1, lastBar = 1, fingers = new[] { 5, 4 } };
        coachAction.Invoke(flow.engine, new object[] { help });
        Require(stats.text.StartsWith("Fingering,", StringComparison.Ordinal), "Coach fingering should use the sheet with the overlay off.");
        Swipe(RadialMenu.Choice.Fingering);
        Require(PlayerPrefs.GetInt(FingeringPref) == 1, "Turning on fingering did not save.");
        coachAction.Invoke(flow.engine, new object[] { help });
        Require(!stats.text.StartsWith("Fingering,", StringComparison.Ordinal), "Coach replay duplicated the sheet text.");
        yield return WaitFor(() => flow.engine.IsDemoPlaying, "coach fingering replay");

        Swipe(RadialMenu.Choice.FineTune);
        Require(!menu.IsOpen && GameObject.Find("MarkerOffsetsPanel"), "Fine Tune did not open the marker offsets.");
        Touch();
        Lift(Vector3.zero);
        yield return WaitFor(() => !GameObject.Find("MarkerOffsetsPanel"), "the menu to dismiss fine tuning");
        simulateHand.SetValue(menu, false);
        yield return WaitFor(() => !anchor.gameObject.activeSelf && !menu.IsOpen, "tracking loss to close the ring");
        simulateHand.SetValue(menu, true);
        yield return WaitFor(() => anchor.gameObject.activeSelf, "the hand button to return");

        var confirm = root.Find("ConfirmSegment");
        Vector3 oldPosition = flow.overlay.transform.position;
        Touch();
        Vector3 recalibrateDirection = Direction(RadialMenu.Choice.Recalibrate);
        Sample(recalibrateDirection * 0.065f + Vector3.forward * 0.05f);
        Require(!flow.calibrator.IsCalibrating && confirm.gameObject.activeSelf &&
            Vector3.Dot(confirm.localPosition.normalized, Direction(RadialMenu.Choice.Recalibrate)) > 0.99f,
            "Recalibrate should preview its confirmation while the index is still down.");
        Sample(Vector3.zero);
        Lift(Vector3.zero);
        Require(menu.IsOpen && !confirm.gameObject.activeSelf && !flow.calibrator.IsCalibrating,
            "Returning to center should dismiss the confirmation preview.");
        Swipe(RadialMenu.Choice.Recalibrate);
        Swipe(RadialMenu.Choice.Recalibrate);
        Require(!flow.calibrator.IsCalibrating && menu.IsOpen, "A short swipe confirmed recalibration.");
        Touch();
        Lift(Vector3.zero);
        Require(!menu.IsOpen && !confirm.gameObject.activeSelf && !flow.calibrator.IsCalibrating, "Close did not cancel confirmation.");
        Touch();
        Sample(recalibrateDirection * 0.065f + Vector3.forward * 0.05f);
        Sample(recalibrateDirection * 0.105f + Vector3.forward * 0.06f);
        Require(!flow.calibrator.IsCalibrating, "Recalibration started before release.");
        Lift(recalibrateDirection * 0.105f);
        Require(flow.calibrator.IsCalibrating && !menu.IsOpen, "Confirmed recalibration did not start.");
        Swipe(RadialMenu.Choice.Recalibrate);
        yield return WaitFor(() => !flow.calibrator.IsCalibrating && flow.overlay.isActiveAndEnabled, "cancellation to restore the keyboard");
        Require(flow.overlay.transform.position == oldPosition, "Cancelling recalibration moved the keyboard.");

        Swipe(RadialMenu.Choice.FreePlay);
        Require(flow.CurrentStep == AppFlow.Step.FreePlay && !flow.sheetDisplay.gameObject.activeInHierarchy, "Incorrect Free Play handoff.");
        var freePlaySheet = UnityEngine.Object.FindAnyObjectByType<FreePlaySheet>();
        Require(freePlaySheet && freePlaySheet.Show("fur_elise", true), "The Free Play sheet could not show a song.");
        Swipe(RadialMenu.Choice.RestartLesson);
        Require(flow.CurrentStep == AppFlow.Step.Lesson && !freePlaySheet.IsShowing &&
            flow.sheetDisplay.gameObject.activeInHierarchy, "Returning to Lesson left duplicate or missing sheets.");
        Swipe(RadialMenu.Choice.RestartLesson);
        Require(menu.IsOpen && confirm.gameObject.activeSelf &&
            Vector3.Dot(confirm.localPosition.normalized, Direction(RadialMenu.Choice.RestartLesson)) > 0.99f,
            "Restart did not branch to its own confirmation.");
        Swipe(RadialMenu.Choice.RestartLesson, 0.118f);
        Require(!menu.IsOpen && flow.engine.Measure == 1, "Confirmed restart did not return to the opening measure.");
        Swipe(RadialMenu.Choice.Visuals);
        Require(VisualsMode.NonVisual && !menu.IsOpen && Camera.main.cullingMask == 0,
            "Visuals Off must hide the app and close the menu.");
        yield return WaitFor(() => !anchor.gameObject.activeSelf, "audio-only mode to hide the wrist");
        menu.Open();
        Require(!menu.IsOpen && !menu.Choose(RadialMenu.Choice.RestartLesson), "An invisible menu accepted input.");
        VisualsMode.Set(false, "smoke test");
        yield return WaitFor(() => anchor.gameObject.activeSelf && Camera.main.cullingMask != 0,
            "the wrist and rendering to return with visuals on");
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
            var marker = UnityEngine.Object.FindAnyObjectByType<MarkerCalibration>();
            Require(marker, "Main_2 is missing marker calibration.");
            var lockMarker = marker.GetType().GetMethod("OnMarkerLocked", BindingFlags.Instance | BindingFlags.NonPublic);
            lockMarker.Invoke(marker, new object[] { center - right * 0.62f });
            lockMarker.Invoke(marker, new object[] { center + right * 0.62f });
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
