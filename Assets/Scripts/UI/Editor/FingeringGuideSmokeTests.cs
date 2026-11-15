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
            Debug.Log("[Fingering smoke] PASS: tracked wrist menu, six controls, fine tuning, coach replay/fallback, and sheet handoff.");
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

        var menus = UnityEngine.Object.FindObjectsByType<RadialMenu>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Require(menus.Length == 1, "Expected one radial menu.");
        var menu = menus[0];
        var anchor = menu.transform.Find("Left Hand Menu");
        var root = anchor.Find("RadialMenuRoot");
        Require(anchor.GetComponentsInChildren<XRSimpleInteractable>(true).Length == 6, "Expected six hand menu controls.");
        var allObjects = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Require(!allObjects.Any(t => t.name == "SwitchModeButton" || t.name == "LessonButton" ||
            t.name == "FreePlayButton" || t.name == "Fingering Toggle"), "A duplicate mode or fingering button was built.");
        Require(!flow.calibrator.recalibrateButton && !flow.calibrator.overlayGrab &&
            !flow.calibrator.handRecalibrateDot && !flow.calibrator.handOffsetsDot &&
            !flow.overlay.transform.Find("GrabHandle"), "The former dots or drag handle are still enabled.");
        AssertDistinctPokeTargets(anchor);
        var menuHand = menu.GetType().GetField("_simulateMenuHand", BindingFlags.Instance | BindingFlags.NonPublic);
        var menuHandOffset = menu.GetType().GetField("_simulatedHandOffset", BindingFlags.Instance | BindingFlags.NonPublic);
        menuHand.SetValue(menu, true);

        var start = PokeControl(root, RadialMenu.Choice.RestartLesson);
        var freePlay = PokeControl(root, RadialMenu.Choice.FreePlay);
        var recalibrate = PokeControl(root, RadialMenu.Choice.Recalibrate);
        var toggle = PokeControl(root, RadialMenu.Choice.Fingering);
        var fineTune = PokeControl(root, RadialMenu.Choice.FineTune);
        var close = PokeControl(anchor, RadialMenu.Choice.Close);
        menu.Open();
        Require(!menu.Choose(RadialMenu.Choice.RestartLesson) && !menu.Choose(RadialMenu.Choice.FreePlay) &&
            !menu.Choose(RadialMenu.Choice.Recalibrate) && !menu.Choose(RadialMenu.Choice.Fingering) &&
            !menu.Choose(RadialMenu.Choice.FineTune),
            "An action was available before calibration.");
        menu.Close();

        var config = AIConfig.Get();
        var startup = config.defaultMode;
        try
        {
            config.defaultMode = StartMode.Ask;
            Calibrate(flow.calibrator);
        }
        finally { config.defaultMode = startup; }
        Require(!flow.calibrator.IsCalibrating, "Simulated marker calibration did not finish.");
        Require(flow.CurrentStep == AppFlow.Step.ChooseMode && !menu.IsOpen, "Startup should wait for the hand button.");
        yield return WaitFor(() => close.isActiveAndEnabled, "left-hand Menu button");
        Require(close.transform.parent.Find("Label").GetComponent<TextMeshPro>().text == "Menu", "Closed hand button is unlabeled.");
        Poke(close);
        Require(menu.IsOpen, "The hand button did not open the menu.");
        Poke(start);
        Require(flow.CurrentStep == AppFlow.Step.ChooseMode, "Startup poke bypassed the opening debounce.");
        yield return WaitFor(() => start.isActiveAndEnabled, "startup Start Lesson poke");
        Vector3 previousCenter = close.transform.position;
        menuHandOffset.SetValue(menu, new Vector3(0.07f, 0.01f, 0f));
        yield return WaitFor(() => Vector3.Distance(previousCenter, close.transform.position) > 0.05f, "the open menu to follow the hand");
        Require(root.parent == anchor && Vector3.Distance(root.position, close.transform.position) < 0.001f,
            "The ring detached from the hand button.");
        menuHandOffset.SetValue(menu, Vector3.zero);
        Require(!toggle.enabled, "Fingering was interactive before Lesson.");
        Poke(start);
        Require(flow.CurrentStep == AppFlow.Step.Lesson && !menu.IsOpen, "The radial Start Lesson did not start the lesson.");

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

        var toggleLabel = toggle.transform.parent.Find("Label").GetComponent<TextMeshPro>();
        menu.Open();
        yield return WaitFor(() => toggle.isActiveAndEnabled, "radial fingering toggle");
        Require(toggleLabel.text == "Fingering\nOn", "Radial menu did not display Fingering On.");
        var currentRun = flow.engine.CurrentRun;
        Poke(toggle);
        Require(!eTip.enabled && !eBadge.enabled && !ring.enabled && !glow.enabled, "The toggle left a fingering cue visible.");
        Require(!menu.IsOpen && PlayerPrefs.GetInt(FingeringPref) == 0, "Turning fingering off did not close the menu and save.");
        Require(flow.engine.CurrentRun == currentRun && flow.overlay.isActiveAndEnabled, "Turning fingering off changed the lesson.");

        var coachAction = flow.engine.GetType().GetMethod("HandleCoachAction", BindingFlags.Instance | BindingFlags.NonPublic);
        var stats = (TMP_Text)flow.sheetDisplay.GetType().GetField("_stats", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(flow.sheetDisplay);
        var help = new CoachAction { type = CoachActionType.ShowFingering, firstBar = 1, lastBar = 1, fingers = new[] { 5, 4 } };
        coachAction.Invoke(flow.engine, new object[] { help });
        Require(stats.text.StartsWith("Fingering,", StringComparison.Ordinal) && !flow.engine.IsDemoPlaying,
            "Coach fingering should use only the sheet fallback while the overlay is off.");

        menu.Open();
        Require(toggleLabel.text == "Fingering\nOff", "Reopening the menu did not display Fingering Off.");
        yield return WaitFor(() => toggle.isActiveAndEnabled, "radial fingering toggle to re-enable");
        Poke(toggle);
        yield return WaitFor(() => eTip.enabled && eBadge.enabled && ring.enabled, "fingering to return");
        Require(PlayerPrefs.GetInt(FingeringPref) == 1, "Turning fingering on did not save.");
        coachAction.Invoke(flow.engine, new object[] { help });
        Require(!stats.text.StartsWith("Fingering,", StringComparison.Ordinal), "Coach replay duplicated the sheet fingering text.");
        yield return WaitFor(() => flow.engine.IsDemoPlaying, "coach fingering replay");

        menu.Open();
        yield return WaitFor(() => close.isActiveAndEnabled, "radial Close");
        var lessonBeforeClose = flow.engine.State;
        Poke(close);
        Require(!menu.IsOpen && flow.engine.State == lessonBeforeClose, "Close changed the lesson.");

        Poke(close);
        yield return WaitFor(() => fineTune.isActiveAndEnabled, "radial Fine Tune");
        Poke(fineTune);
        Require(!menu.IsOpen && GameObject.Find("MarkerOffsetsPanel"), "Fine Tune did not open the marker offsets.");
        Poke(close);
        yield return WaitFor(() => !GameObject.Find("MarkerOffsetsPanel"), "reopening the hand menu to dismiss fine tuning");
        menuHand.SetValue(menu, false);
        yield return WaitFor(() => !anchor.gameObject.activeSelf && !menu.IsOpen, "hand tracking loss to hide and close the menu");
        menuHand.SetValue(menu, true);
        yield return WaitFor(() => close.isActiveAndEnabled, "hand tracking to restore the Menu button");

        Vector3 oldPosition = flow.overlay.transform.position;
        menu.Open();
        yield return WaitFor(() => recalibrate.isActiveAndEnabled, "radial Recalibrate");
        Poke(recalibrate);
        Require(flow.calibrator.IsCalibrating && !menu.IsOpen, "Radial recalibration did not start.");
        yield return WaitFor(() => !eTip.enabled && !dSharpTip.enabled && !eBadge.enabled && !dSharpBadge.enabled,
            "fingering to hide while recalibrating");
        menu.Open();
        yield return WaitFor(() => recalibrate.isActiveAndEnabled, "radial Cancel Recalibrate");
        Require(!toggle.enabled && !start.enabled && !freePlay.enabled && !fineTune.enabled, "Another action was interactive during recalibration.");
        Require(recalibrate.transform.parent.Find("Label").GetComponent<TextMeshPro>().text == "Cancel\nRecalibrate",
            "Cancel Recalibrate label was missing.");
        Poke(recalibrate);
        yield return WaitFor(() => !flow.calibrator.IsCalibrating && flow.overlay.isActiveAndEnabled, "cancel to restore keyboard");
        Require(flow.overlay.transform.position == oldPosition, "Cancelling recalibration moved the keyboard.");

        menu.Open();
        yield return WaitFor(() => freePlay.isActiveAndEnabled, "radial Free Play");
        Poke(freePlay);
        Require(flow.CurrentStep == AppFlow.Step.FreePlay, "Free Play did not start.");
        yield return WaitFor(() => !eTip.enabled && !dSharpTip.enabled && !eBadge.enabled && !dSharpBadge.enabled,
            "fingering to hide in Free Play");
        Require(!flow.sheetDisplay.gameObject.activeInHierarchy, "The lesson sheet remained active in Free Play.");
        var freePlaySheet = UnityEngine.Object.FindAnyObjectByType<FreePlaySheet>();
        Require(freePlaySheet && freePlaySheet.Show("fur_elise", true) && freePlaySheet.IsShowing,
            "The Free Play sheet could not show a song.");
        menu.Open();
        yield return WaitFor(() => start.isActiveAndEnabled, "radial Start Lesson from Free Play");
        Require(!toggle.enabled && !freePlay.enabled, "Fingering or the active mode was interactive in Free Play.");
        Poke(start);
        Require(flow.CurrentStep == AppFlow.Step.Lesson && !freePlaySheet.IsShowing &&
            flow.sheetDisplay.gameObject.activeInHierarchy, "Returning to Lesson left duplicate or missing sheets.");
        menu.Open();
        yield return WaitFor(() => start.isActiveAndEnabled, "radial Restart Lesson");
        Require(start.transform.parent.Find("Label").GetComponent<TextMeshPro>().text == "Restart\nLesson",
            "Restart Lesson label was missing.");
        Poke(start);
        Require(!menu.IsOpen && flow.engine.Measure == 1, "Restart Lesson did not return to the opening measure.");
    }

    static XRSimpleInteractable PokeControl(Transform root, RadialMenu.Choice choice) =>
        root.Find($"{choice}Segment/Poke").GetComponent<XRSimpleInteractable>();

    static void Poke(XRSimpleInteractable control) => control.selectEntered.Invoke(new SelectEnterEventArgs());

    static void AssertDistinctPokeTargets(Transform root)
    {
        var boxes = root.GetComponentsInChildren<BoxCollider>(true);
        Require(boxes.Length == 6, "Expected one collider per radial control.");
        for (int i = 0; i < boxes.Length; i++)
        for (int j = i + 1; j < boxes.Length; j++)
        {
            Vector3 delta = root.InverseTransformPoint(boxes[i].transform.position) -
                root.InverseTransformPoint(boxes[j].transform.position);
            Vector3 halfSize = (boxes[i].size + boxes[j].size) * 0.5f;
            Require(Mathf.Abs(delta.x) >= halfSize.x || Mathf.Abs(delta.y) >= halfSize.y,
                $"Overlapping poke controls: {boxes[i].transform.parent.name} and {boxes[j].transform.parent.name}.");
        }
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
