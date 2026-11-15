using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Left-hand radial menu: Restart Lesson / Free Play / Recalibrate Piano around a center Close button.
///
/// Open / close: touch and hold the ORANGE dot on the back of the left hand (PianoCalibrator.MenuDotPressed; while this
/// menu exists, the dot opens it instead of restarting calibration straight away). Editor: F1 toggles, F2/F3/F4 choose.
/// Select: poke a segment (XRI poke, same as ModeChoicePanel). Pokes are ignored for a moment after opening, and one
/// action runs per opening; the menu closes after it.
///
/// Uses only public APIs: AppFlow (CurrentStep, evaluator, engine, overlay), AppModeSwitch.Request, PianoCalibrator
/// (Recalibrate, CancelCalibration). Built at runtime, adds itself to scenes with an AppFlow + PianoCalibrator.
/// Additive display: bright rims and text only, no fills.
/// </summary>
public class RadialMenu : MonoBehaviour
{
    public enum Choice { RestartLesson, FreePlay, Recalibrate, Close }

    [Header("Found automatically when empty")]
    public AppFlow app;
    public PianoCalibrator calibrator;

    [Header("Placement (head-relative when opened, then fixed in the world)")]
    public float distance = 0.42f;
    public float below = 0.06f;
    public float radius = 0.085f;      // segment centers from the menu center (m)

    [Header("Look")]
    public Color rimColor = new Color(0.3f, 0.9f, 1f);
    public Color focusColor = Color.white;
    public Color disabledColor = new Color(0.25f, 0.4f, 0.45f);
    public Color chosenColor = new Color(0.25f, 1f, 0.35f);
    public float rimWidth = 0.003f;

    [Header("Input")]
    [Tooltip("Pokes are ignored this long after opening (the menu appears under the finger that opened it).")]
    public float debounceSeconds = 0.6f;
    [Tooltip("Closes by itself after this long without a choice (0 = never).")]
    public float autoCloseSeconds = 20f;

    public bool IsOpen => _root && _root.gameObject.activeSelf;
    public event Action<Choice> Chosen;

    class Segment
    {
        public Choice choice;
        public GameObject go;
        public XRSimpleInteractable interactable;
        public readonly List<LineRenderer> lines = new List<LineRenderer>();
        public TextMeshPro label;
        public Material material;
        public bool enabled = true;
    }

    readonly List<Segment> _segments = new List<Segment>();
    Transform _root;
    TextMeshPro _title;
    Segment _focused;
    float _openedAt;
    bool _acted;
    bool _pausedEvaluator; // Main: the wait-mode evaluator is switched off while recalibrating

    // ------------------------------------------------------------------ Auto add

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AddToScene()
    {
        if (FindAnyObjectByType<RadialMenu>(FindObjectsInactive.Include)) return;
        if (!FindAnyObjectByType<AppFlow>() || !FindAnyObjectByType<PianoCalibrator>()) return;
        new GameObject("RadialMenu").AddComponent<RadialMenu>();
    }

    // ------------------------------------------------------------------ API

    public void Toggle() { if (IsOpen) Close(); else Open(); }

    public void Open()
    {
        if (!_root) return;
        _openedAt = Time.time;
        _acted = false;
        SetFocus(null);
        PlaceInFrontOfHead();
        _root.gameObject.SetActive(true);
        Refresh();
        Debug.Log("[RadialMenu] Opened.");
    }

    public void Close()
    {
        if (!_root || !_root.gameObject.activeSelf) return;
        _root.gameObject.SetActive(false);
        SetFocus(null);
        Debug.Log("[RadialMenu] Closed.");
    }

    /// <summary>Run a menu action (what a poke does). Returns false if it isn't available now.</summary>
    public bool Choose(Choice choice)
    {
        if (choice == Choice.Close) { Close(); return true; }
        if (_acted || !IsAvailable(choice, out _)) return false;
        _acted = true;
        Debug.Log($"[RadialMenu] {choice} chosen.");
        var seg = _segments.Find(s => s.choice == choice);
        if (seg != null) SetColor(seg, chosenColor);
        switch (choice)
        {
            case Choice.RestartLesson: RestartLesson(); break;
            case Choice.FreePlay: AppModeSwitch.Request(AppMode.FreePlay, "menu"); break;
            case Choice.Recalibrate:
                if (calibrator.IsCalibrating) calibrator.CancelCalibration();
                else StartRecalibration();
                break;
        }
        Chosen?.Invoke(choice);
        Close();
        return true;
    }

    // ------------------------------------------------------------------ Actions

    bool Calibrated => AppModeSwitch.AfterCalibration;
    bool InLesson => app && app.CurrentStep == AppFlow.Step.Lesson;
    bool InFreePlay => app && app.CurrentStep == AppFlow.Step.FreePlay;

    bool IsAvailable(Choice c, out string label)
    {
        bool calibrating = calibrator && calibrator.IsCalibrating;
        switch (c)
        {
            case Choice.RestartLesson:
                label = InLesson ? "Restart\nLesson" : "Start\nLesson";
                return Calibrated && !calibrating && app;
            case Choice.FreePlay:
                label = InFreePlay ? "Free Play\n(on)" : "Free Play";
                return Calibrated && !calibrating && !InFreePlay && app;
            case Choice.Recalibrate:
                // A calibration in progress can be cancelled only if there is an older one to go back to.
                label = calibrating ? "Cancel\nRecalibrate" : "Recalibrate\nPiano";
                return calibrator && Calibrated;
            default:
                label = "Close";
                return true;
        }
    }

    // Same song, from the first note. Main: evaluator.Restart() (AppFlow lights the first key on Restarted, the sheet
    // follows). Main_2: CurriculumEngine.StartLesson() resets the guided lesson. Outside the lesson: enter it (AppFlow
    // starts it from the beginning). Calibration is untouched.
    void RestartLesson()
    {
        if (!InLesson) { AppModeSwitch.Request(AppMode.Lesson, "menu"); return; }
        if (app.overlay) app.overlay.ClearAll(); // no flash / old key stays lit
        if (app.engine) app.engine.StartLesson();
        else if (app.evaluator) app.evaluator.Restart();
    }

    // The calibrator keeps the overlay where it is until a new calibration succeeds, so cancelling (or giving up)
    // keeps the old one. In Main the wait-mode evaluator is switched off meanwhile so pinch-time key presses don't count;
    // its note index is kept and it is switched back on afterwards.
    void StartRecalibration()
    {
        if (InLesson && !app.engine && app.evaluator && app.evaluator.enabled)
        {
            app.evaluator.enabled = false;
            _pausedEvaluator = true;
        }
        calibrator.Recalibrate();
    }

    void ResumeAfterCalibration()
    {
        if (!_pausedEvaluator) return;
        _pausedEvaluator = false;
        if (app && app.evaluator) app.evaluator.enabled = true;
        Debug.Log("[RadialMenu] Calibration over; lesson resumes where it was.");
    }

    // ------------------------------------------------------------------ Lifecycle

    void Awake()
    {
        if (!app) app = FindAnyObjectByType<AppFlow>();
        if (!calibrator) calibrator = FindAnyObjectByType<PianoCalibrator>();
        Build();
        _root.gameObject.SetActive(false);
    }

    void OnEnable()
    {
        if (calibrator)
        {
            calibrator.MenuDotPressed += Toggle;
            calibrator.Calibrated += ResumeAfterCalibration;
            calibrator.CalibrationCancelled += ResumeAfterCalibration;
        }
    }

    void OnDisable()
    {
        if (calibrator)
        {
            calibrator.MenuDotPressed -= Toggle;
            calibrator.Calibrated -= ResumeAfterCalibration;
            calibrator.CalibrationCancelled -= ResumeAfterCalibration;
        }
        ResumeAfterCalibration();
    }

    void OnDestroy()
    {
        foreach (var s in _segments) if (s.material) Destroy(s.material);
    }

    void Update()
    {
        DebugKeys();
        if (!IsOpen) return;
        if (autoCloseSeconds > 0f && Time.time > _openedAt + autoCloseSeconds) { Close(); return; }
        Refresh();
    }

    bool Usable => IsOpen && !_acted && Time.time >= _openedAt + debounceSeconds;

    void Refresh()
    {
        bool usable = Usable;
        foreach (var s in _segments)
        {
            s.enabled = IsAvailable(s.choice, out string label);
            if (s.label.text != label) s.label.text = label;
            bool live = usable && s.enabled;
            if (s.interactable.enabled != live) s.interactable.enabled = live;
            if (!live && _focused == s) SetFocus(null);
            if (!_acted) SetColor(s, !s.enabled ? disabledColor : _focused == s ? focusColor : rimColor);
            s.label.color = s.enabled ? Color.white : disabledColor;
        }
        if (_title) _title.text = Calibrated || (calibrator && calibrator.IsCalibrating) ? "Menu" : "Finish setup first";
    }

    void SetFocus(Segment s)
    {
        _focused = s;
        foreach (var x in _segments) x.go.transform.localScale = Vector3.one * (x == s ? 1.08f : 1f);
    }

    void PlaceInFrontOfHead()
    {
        var cam = Camera.main ? Camera.main.transform : null;
        if (!cam) return;
        Vector3 forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        forward.Normalize();
        Vector3 pos = cam.position + forward * distance + Vector3.down * below;
        _root.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - cam.position, Vector3.up));
    }

    void DebugKeys()
    {
#if UNITY_EDITOR
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return;
        if (kb.f1Key.wasPressedThisFrame) Toggle();
        if (!IsOpen) return;
        if (kb.f2Key.wasPressedThisFrame) Choose(Choice.RestartLesson);
        if (kb.f3Key.wasPressedThisFrame) Choose(Choice.FreePlay);
        if (kb.f4Key.wasPressedThisFrame) Choose(Choice.Recalibrate);
        if (kb.escapeKey.wasPressedThisFrame) Close();
#endif
    }

    // ------------------------------------------------------------------ Build

    void Build()
    {
        _root = new GameObject("RadialMenuRoot").transform;
        _root.SetParent(transform, false);

        // Three ring segments, 120° each, gaps of 8°: Restart on top, Free Play lower left, Recalibrate lower right.
        AddSegment(Choice.RestartLesson, 90f, Icon.Restart);
        AddSegment(Choice.FreePlay, 210f, Icon.Keys);
        AddSegment(Choice.Recalibrate, 330f, Icon.Target);
        AddCenter();
        _title = Label(_root, "Title", "Menu", 0.2f, new Vector3(0f, radius * 1.85f, 0f), new Vector2(0.2f, 0.03f));
    }

    enum Icon { Restart, Keys, Target }

    void AddSegment(Choice choice, float centerDeg, Icon icon)
    {
        var s = NewSegment(choice, choice.ToString());
        float inner = radius * 0.55f, outer = radius * 1.55f, half = 56f;
        var outline = new List<Vector3>();
        for (int i = 0; i <= 16; i++) outline.Add(Polar(outer, centerDeg - half + i * (2 * half / 16f)));
        for (int i = 0; i <= 16; i++) outline.Add(Polar(inner, centerDeg + half - i * (2 * half / 16f)));
        Line(s, outline, true);

        Vector3 c = Polar(radius * 1.05f, centerDeg);
        Vector3 iconAt = c + new Vector3(0f, 0.017f, 0f);
        switch (icon)
        {
            case Icon.Restart: // circular arrow
                var arc = new List<Vector3>();
                for (int i = 0; i <= 12; i++) arc.Add(iconAt + Polar(0.009f, 60f + i * 22f));
                Line(s, arc, false);
                Vector3 tip = arc[0];
                Line(s, new List<Vector3> { tip + new Vector3(-0.005f, 0.004f, 0f), tip, tip + new Vector3(0.002f, 0.006f, 0f) }, false);
                break;
            case Icon.Keys: // three piano keys
                for (int k = -1; k <= 1; k++)
                {
                    float x = k * 0.007f;
                    Line(s, new List<Vector3> { iconAt + new Vector3(x - 0.003f, -0.009f, 0f), iconAt + new Vector3(x - 0.003f, 0.009f, 0f),
                                                iconAt + new Vector3(x + 0.003f, 0.009f, 0f), iconAt + new Vector3(x + 0.003f, -0.009f, 0f) }, true);
                }
                break;
            case Icon.Target: // crosshair
                var ring = new List<Vector3>();
                for (int i = 0; i < 16; i++) ring.Add(iconAt + Polar(0.007f, i * 22.5f));
                Line(s, ring, true);
                Line(s, new List<Vector3> { iconAt + new Vector3(-0.011f, 0f, 0f), iconAt + new Vector3(0.011f, 0f, 0f) }, false);
                Line(s, new List<Vector3> { iconAt + new Vector3(0f, -0.011f, 0f), iconAt + new Vector3(0f, 0.011f, 0f) }, false);
                break;
        }
        s.label = Label(s.go.transform, "Label", "", 0.2f, c + new Vector3(0f, -0.013f, -0.002f), new Vector2(0.1f, 0.03f));
        AddPoke(s, c, new Vector3(radius * 0.95f, radius * 0.7f, 0.02f));
    }

    void AddCenter()
    {
        var s = NewSegment(Choice.Close, "Close");
        var ring = new List<Vector3>();
        float r = radius * 0.4f;
        for (int i = 0; i < 24; i++) ring.Add(Polar(r, i * 15f));
        Line(s, ring, true);
        s.label = Label(s.go.transform, "Label", "Close", 0.17f, new Vector3(0f, 0f, -0.002f), new Vector2(0.06f, 0.02f));
        AddPoke(s, Vector3.zero, new Vector3(r * 1.6f, r * 1.6f, 0.02f));
    }

    Segment NewSegment(Choice choice, string name)
    {
        var go = new GameObject(name + "Segment");
        go.transform.SetParent(_root, false);
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        var s = new Segment { choice = choice, go = go, material = new Material(shader) { name = "RadialMenuRim (runtime)" } };
        _segments.Add(s);
        return s;
    }

    void AddPoke(Segment s, Vector3 center, Vector3 size)
    {
        // Collider first: the interactable collects its colliders on Awake. Local +Z points away from the player,
        // so a poke from the player's side moves along +Z (XRPokeFilter's default axis).
        var holder = new GameObject("Poke");
        holder.transform.SetParent(s.go.transform, false);
        holder.transform.localPosition = center;
        var box = holder.AddComponent<BoxCollider>();
        box.size = size;
        s.interactable = holder.AddComponent<XRSimpleInteractable>();
        var filter = holder.AddComponent<XRPokeFilter>();
        filter.pokeInteractable = s.interactable;
        filter.pokeCollider = box;
        s.interactable.hoverEntered.AddListener(_ => { if (Usable && s.enabled) SetFocus(s); });
        s.interactable.hoverExited.AddListener(_ => { if (_focused == s) SetFocus(null); });
        s.interactable.selectEntered.AddListener(_ => { if (Usable && s.enabled) Choose(s.choice); });
    }

    void Line(Segment s, List<Vector3> points, bool loop)
    {
        var go = new GameObject("Line");
        go.transform.SetParent(s.go.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.loop = loop;
        lr.positionCount = points.Count;
        lr.SetPositions(points.ToArray());
        lr.widthMultiplier = rimWidth;
        lr.numCornerVertices = 2;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.sharedMaterial = s.material;
        s.lines.Add(lr);
    }

    static TextMeshPro Label(Transform parent, string name, string text, float fontSize, Vector3 position, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white; // additive display: bright = visible
        tmp.rectTransform.sizeDelta = size;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        return tmp;
    }

    static Vector3 Polar(float r, float deg) =>
        new Vector3(Mathf.Cos(deg * Mathf.Deg2Rad) * r, Mathf.Sin(deg * Mathf.Deg2Rad) * r, 0f);

    static void SetColor(Segment s, Color c)
    {
        if (s.material.HasProperty("_BaseColor")) s.material.SetColor("_BaseColor", c);
        if (s.material.HasProperty("_Color")) s.material.SetColor("_Color", c);
    }
}
