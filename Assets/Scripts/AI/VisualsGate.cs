using UnityEngine;

// Hides everything of ours while VisualsMode.NonVisual is on: the main camera draws no layers, so the overlay, sheet,
// orb, panels, radial menu, hand dots and hands all disappear without any of those modules knowing, and they keep their
// state (the next key stays lit underneath and is there again when the visuals come back).
// "Visuals on" before the piano was ever calibrated (a non-visual start skips calibration): calibration runs first.
// Editor: F9 toggles. Adds itself to scenes with an AppFlow.
public class VisualsGate : MonoBehaviour
{
    AppFlow app;
    bool calibrated;   // a calibration finished at some point, so the overlay sits on the real keys
    Camera hidden;     // the camera we blanked, and the layers it drew
    int hiddenMask;
    string pendingLine;
    float pendingUntil;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AddToScene()
    {
        if (FindAnyObjectByType<VisualsGate>(FindObjectsInactive.Include) || !FindAnyObjectByType<AppFlow>()) return;
        new GameObject("VisualsGate").AddComponent<VisualsGate>();
    }

    void Awake() => app = FindAnyObjectByType<AppFlow>();

    void OnEnable()
    {
        VisualsMode.Changed += OnChanged;
        if (app && app.calibrator) app.calibrator.Calibrated += OnCalibrated;
    }

    void OnDisable()
    {
        VisualsMode.Changed -= OnChanged;
        if (app && app.calibrator) app.calibrator.Calibrated -= OnCalibrated;
        Unhide();
    }

    void OnChanged(bool nonVisual, string source)
    {
        var cfg = AIConfig.Get();
        if (nonVisual)
        {
            var menu = FindAnyObjectByType<RadialMenu>();
            if (menu) menu.Close();
            Hide();
            Say(cfg.visualsOffLine);
            return;
        }
        Unhide();
        // Setup is over but the overlay was never placed: line it up first. During AppFlow's own calibration step, or
        // before it, AppFlow calibrates anyway.
        if (!calibrated && AppModeSwitch.AfterCalibration && app && app.calibrator && !app.calibrator.IsCalibrating)
        {
            Debug.Log("[Visuals] Never calibrated: calibrating first.");
            if (app.overlay) app.overlay.gameObject.SetActive(true);
            app.calibrator.Recalibrate();
            Say(cfg.visualsCalibrateFirstLine);
        }
    }

    void OnCalibrated()
    {
        calibrated = true;
        // Calibrated after "visuals on" while the mode is still being chosen: AppFlow only shows the choice after its
        // own calibration step, so show it here.
        if (!app || app.CurrentStep != AppFlow.Step.ChooseMode) return;
        var panel = FindAnyObjectByType<ModeChoicePanel>(FindObjectsInactive.Include);
        if (panel && !panel.IsShowingChoice) panel.ShowChoice();
    }

    void Hide()
    {
        var cam = Camera.main;
        if (!cam || cam == hidden) return;
        Unhide();
        hidden = cam;
        hiddenMask = cam.cullingMask;
        cam.cullingMask = 0; // the background stays clear (passthrough), nothing else is drawn
    }

    void Unhide()
    {
        if (hidden) hidden.cullingMask = hiddenMask;
        hidden = null;
    }

    // The coach may be talking; try again for a few seconds.
    void Say(string line)
    {
        pendingLine = line;
        pendingUntil = Time.time + 8f;
    }

    void Update()
    {
#if UNITY_EDITOR
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null && kb.f9Key.wasPressedThisFrame) VisualsMode.Toggle("key");
#endif
        if (VisualsMode.NonVisual) Hide(); // also once Camera.main exists, or if it changes
        if (string.IsNullOrEmpty(pendingLine)) return;
        if (Time.time > pendingUntil) { pendingLine = null; return; }
        if (app && app.coach && app.coach.SayNow(pendingLine)) pendingLine = null;
    }
}
