using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Main_2 renders a tracked left-hand button and a ring that expands around it. Other scenes use the legacy dot menu.
/// Poke the center to open, then poke individual actions.
/// </summary>
public class RadialMenu : MonoBehaviour
{
    public enum Choice { RestartLesson, FreePlay, Recalibrate, Close, Fingering, FineTune, Visuals }

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
        public Color color;
        public Color painted;
        public float pokeRadius;
        public bool enabled = true;
    }

    readonly List<Segment> _segments = new List<Segment>();
    Transform _root;
    TextMeshPro _title;
    Segment _focused;
    FingeringGuide _fingeringGuide;
    MarkerCalibration _markerCalibration;
    Transform _handAnchor;
    Segment _handButton;
    Segment _confirmButton;
    Segment _pending;
    GameObject _menuIcon, _closeIcon;
    Material _buttonEdge, _iconMaterial;
    float _lastHandPoseAt;
    const float PokeDepth = -0.008f;
    const float RearmDepth = -0.022f;
    const float MinimumIndexTravel = 0.005f;
    bool _pokeArmed, _hasPokeSample;
    float _nextRefresh;
    Vector3 _previousPokeSample, _pokeStart;
    bool _wearable => _fingeringGuide;
#if UNITY_EDITOR
    bool _simulateMenuHand;
    Vector3 _simulatedHandOffset;
#endif
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
        if (!_root || IsOpen || VisualsMode.NonVisual || (_wearable && !_handAnchor.gameObject.activeSelf)) return;
        _openedAt = Time.time;
        _acted = false;
        SetFocus(null);
        if (_wearable)
        {
            if (_markerCalibration) _markerCalibration.CloseFineTune();
            _root.localScale = Vector3.one * 0.85f;
        }
        else PlaceInFrontOfHead();
        _root.gameObject.SetActive(true);
        UpdateHandButton();
        Refresh();
        Debug.Log("[RadialMenu] Opened.");
    }

    public void Close()
    {
        if (!_root || !_root.gameObject.activeSelf) return;
        _root.gameObject.SetActive(false);
        SetConfirmation(null);
        SetFocus(null);
        UpdateHandButton();
        Debug.Log("[RadialMenu] Closed.");
    }

    /// <summary>Run a menu action (what a poke does). Returns false if it isn't available now.</summary>
    public bool Choose(Choice choice)
    {
        if (choice == Choice.Close) { Close(); return true; }
        if (_acted || VisualsMode.NonVisual || !IsAvailable(choice, out _)) return false;
        if (NeedsConfirmation(choice))
        {
            if (!IsOpen) return false;
            foreach (var segment in _segments)
            {
                if (segment.choice != choice) continue;
                SetConfirmation(segment);
                Refresh();
                return true;
            }
        }
        return ExecuteChoice(choice);
    }

    bool NeedsConfirmation(Choice choice) => _wearable &&
        (choice == Choice.RestartLesson && InLesson ||
         choice == Choice.Recalibrate && calibrator && !calibrator.IsCalibrating);

    void SetConfirmation(Segment segment)
    {
        _pending = segment;
        if (_confirmButton == null) return;
        _confirmButton.go.SetActive(segment != null);
        if (segment == null) return;
        _confirmButton.label.text = segment.choice == Choice.Recalibrate ? "Confirm\nrecalibration" : "Confirm\nrestart";
        _confirmButton.go.transform.localPosition = segment.go.transform.localPosition.normalized * 0.120f;
        _confirmButton.color = Color.Lerp(segment.color, SoftIvory, 0.40f);
        SetColor(_confirmButton, _confirmButton.color);
    }

    bool ExecuteChoice(Choice choice)
    {
        if (_acted || VisualsMode.NonVisual || !IsAvailable(choice, out _)) return false;
        _acted = true;
        if (_wearable) AuraInteractionLog.Write($"[RadialMenu] {choice} chosen.");
        else Debug.Log($"[RadialMenu] {choice} chosen.");
        switch (choice)
        {
            case Choice.RestartLesson: RestartLesson(); break;
            case Choice.FreePlay: AppModeSwitch.Request(AppMode.FreePlay, "menu"); break;
            case Choice.Visuals: VisualsMode.Set(true, "menu"); break; // back on by voice: "hey teacher, visuals on"
            case Choice.Fingering: _fingeringGuide.ToggleOverlay(); break;
            case Choice.FineTune: _markerCalibration.OpenFineTune(); break;
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
            case Choice.Visuals:
                label = "Visuals\nOff";
                return Calibrated && !calibrating && (!_wearable || Application.isEditor || app && app.VoiceCommandsConfigured);
            case Choice.Recalibrate:
                // A calibration in progress can be cancelled only if there is an older one to go back to.
                label = calibrating ? "Cancel\nRecalibrate" : "Recalibrate\nPiano";
                return calibrator && Calibrated;
            case Choice.Fingering:
                bool enabled = _fingeringGuide && _fingeringGuide.OverlayEnabled;
                label = !InLesson ? "Fingering" : enabled ? "Fingering\nOn" : "Fingering\nOff";
                return _fingeringGuide && Calibrated && !calibrating && InLesson;
            case Choice.FineTune:
                bool canFineTune = _markerCalibration && _markerCalibration.CanFineTune;
                label = canFineTune ? "Fine\nTune" : "Fine Tune\nScan first";
                return Calibrated && canFineTune;
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
        if (app) _fingeringGuide = app.GetComponent<FingeringGuide>();
        if (_wearable)
        {
            _markerCalibration = FindAnyObjectByType<MarkerCalibration>();
            BuildWearable();
            AuraInteractionLog.Write("[RadialMenu] Individual poke menu ready.");
        }
        else Build();
        _root.gameObject.SetActive(false);
    }

    void OnEnable()
    {
        if (calibrator)
        {
            if (_wearable) calibrator.UsesHandMenu = true;
            else calibrator.MenuDotPressed += Toggle;
            calibrator.Calibrated += ResumeAfterCalibration;
            calibrator.CalibrationCancelled += ResumeAfterCalibration;
        }
    }

    void OnDisable()
    {
        if (calibrator)
        {
            calibrator.MenuDotPressed -= Toggle;
            if (_wearable) calibrator.UsesHandMenu = false;
            calibrator.Calibrated -= ResumeAfterCalibration;
            calibrator.CalibrationCancelled -= ResumeAfterCalibration;
        }
        if (_handAnchor) _handAnchor.gameObject.SetActive(false);
        Close();
        ResumeAfterCalibration();
    }

    void OnDestroy()
    {
        foreach (var s in _segments) if (s.material) Destroy(s.material);
        if (_handButton != null) Destroy(_handButton.material);
        if (_confirmButton != null) Destroy(_confirmButton.material);
        Destroy(_buttonEdge);
        Destroy(_iconMaterial);
    }

    void Update()
    {
        if (VisualsMode.NonVisual)
        {
            Close();
            if (_handAnchor && _handAnchor.gameObject.activeSelf) _handAnchor.gameObject.SetActive(false);
            ResetPoke();
            return;
        }
        if (_wearable)
        {
            UpdateHandTracking();
            if (_handAnchor.gameObject.activeSelf && calibrator.TryGetMenuIndexTip(out var tip))
                UpdatePoke(tip);
            else ResetPoke();
        }
        DebugKeys();
        if (!IsOpen) return;
        if (_wearable && Time.time - _openedAt < 0.15f)
            _root.localScale = Vector3.one * Mathf.SmoothStep(0.85f, 1f, (Time.time - _openedAt) / 0.15f);
        else if (autoCloseSeconds > 0f && Time.time > _openedAt + autoCloseSeconds &&
            !_wearable && (!app || app.CurrentStep != AppFlow.Step.ChooseMode)) { Close(); return; }
        if (_wearable && _root.localScale.x < 1f && Time.time - _openedAt >= 0.15f)
            _root.localScale = Vector3.one;
        if (!_wearable || Time.unscaledTime >= _nextRefresh) Refresh();
    }

    bool Usable => IsOpen && !_acted && Time.time >= _openedAt + debounceSeconds;

    void Refresh()
    {
        _nextRefresh = Time.unscaledTime + 0.12f;
        bool usable = Usable;
        foreach (var s in _segments)
        {
            s.enabled = IsAvailable(s.choice, out string label);
            if (s.label.text != label) s.label.text = label;
            bool live = s.enabled && (_wearable || usable);
            if (s.interactable && s.interactable.enabled != live) s.interactable.enabled = live;
            if (!live && _focused == s) SetFocus(null);
            if (!_acted)
                SetColor(s, _wearable
                    ? !s.enabled || (_pending != null && _pending != s) ? MutedClay :
                        _focused == s ? Color.Lerp(s.color, Color.white, 0.35f) : s.color
                    : !s.enabled ? disabledColor : _focused == s ? focusColor : rimColor);
            Color labelColor = s.enabled ? Color.white : _wearable ? SoftIvory : disabledColor;
            if (s.label.color != labelColor) s.label.color = labelColor;
        }
        if (_pending != null)
            SetColor(_confirmButton, _focused == _confirmButton
                ? Color.Lerp(_confirmButton.color, Color.white, 0.35f) : _confirmButton.color);
        if (_title)
            _title.text = app && app.CurrentStep == AppFlow.Step.ChooseMode ? "Choose a mode" :
                Calibrated || (calibrator && calibrator.IsCalibrating) ? "Menu" : "Finish setup first";
    }

    void SetFocus(Segment s)
    {
        if (_focused == s) return;
        _focused = s;
        if (_wearable) Refresh();
        else foreach (var x in _segments) x.go.transform.localScale = Vector3.one * (x == s ? 1.08f : 1f);
    }

    // Interpolate the face crossing so a quick poke can pass through the button.
    void UpdatePoke(Vector3 tip)
    {
        Vector3 local = _handAnchor.InverseTransformPoint(tip);
        if (local.z <= RearmDepth)
        {
            _pokeStart = tip;
            _pokeArmed = true;
        }
        Vector3 previous = _previousPokeSample;
        bool crossing = _hasPokeSample && previous.z < PokeDepth && local.z >= PokeDepth;
        _previousPokeSample = local;
        _hasPokeSample = true;
        if (!crossing)
        {
            SetFocus(_pokeArmed && local.z < PokeDepth && local.z > -0.065f ? FindPokeTarget(local) : null);
            return;
        }

        bool armed = _pokeArmed;
        _pokeArmed = false;
        SetFocus(null);
        float forwardTravel = Vector3.Dot(tip - _pokeStart, _handAnchor.forward);
        if (!armed || forwardTravel < MinimumIndexTravel) return;
        Vector3 facePoint = Vector3.Lerp(previous, local, (PokeDepth - previous.z) / (local.z - previous.z));
        var target = FindPokeTarget(facePoint);
        if (target == null) return;
        string label = target == _handButton ? (IsOpen ? "Close" : "Menu") :
            target == _confirmButton ? $"Confirm {_pending.choice}" : target.choice.ToString();
        AuraInteractionLog.Write($"[RadialMenu] Poked: {label}; right-index travel {forwardTravel * 1000f:F0} mm.");
        if (target == _handButton) Toggle();
        else if (target == _confirmButton) ExecuteChoice(_pending.choice);
        else Choose(target.choice);
    }

    Segment FindPokeTarget(Vector2 point)
    {
        if (point.sqrMagnitude <= _handButton.pokeRadius * _handButton.pokeRadius) return _handButton;
        if (!IsOpen) return null;
        if (_pending != null)
            return IsAvailable(_pending.choice, out _) && Hits(_confirmButton, point) ? _confirmButton : null;
        foreach (var segment in _segments)
        {
            if (IsAvailable(segment.choice, out _) && Hits(segment, point)) return segment;
        }
        return null;
    }

    bool Hits(Segment segment, Vector2 point)
    {
        float size = _root.localScale.x;
        float radius = segment.pokeRadius * size;
        return (point - (Vector2)segment.go.transform.localPosition * size).sqrMagnitude <= radius * radius;
    }

    void ResetPoke()
    {
        _pokeArmed = false;
        _hasPokeSample = false;
        if (_wearable) SetFocus(null);
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
        if (kb.f1Key.wasPressedThisFrame)
        {
            if (_wearable && !_handAnchor.gameObject.activeSelf)
            {
                _simulateMenuHand = true;
                UpdateHandTracking();
            }
            Toggle();
        }
        if (!IsOpen) return;
        if (kb.f2Key.wasPressedThisFrame) Choose(Choice.RestartLesson);
        if (kb.f3Key.wasPressedThisFrame) Choose(Choice.FreePlay);
        if (kb.f4Key.wasPressedThisFrame) Choose(Choice.Recalibrate);
        if (kb.escapeKey.wasPressedThisFrame) Close();
#endif
    }

    // ------------------------------------------------------------------ Build

    static readonly Color Clay = new Color(0.64f, 0.25f, 0.13f);
    static readonly Color MutedClay = new Color(0.31f, 0.22f, 0.23f);
    static readonly Color SoftIvory = new Color(0.94f, 0.78f, 0.65f);
    static readonly Color Amber = new Color(0.72f, 0.39f, 0.12f);

    void UpdateHandTracking()
    {
        bool tracked = Calibrated && calibrator && calibrator.TryGetMenuHandPose(out _handPose);
#if UNITY_EDITOR
        if (_simulateMenuHand && Calibrated && Camera.main)
        {
            var head = Camera.main.transform;
            _handPose = new Pose(head.TransformPoint(new Vector3(-0.045f, -0.04f, 0.40f) + _simulatedHandOffset), head.rotation);
            tracked = true;
        }
#endif
        _handAnchor.gameObject.SetActive(tracked);
        if (tracked)
        {
            _lastHandPoseAt = Time.unscaledTime;
            _handAnchor.SetPositionAndRotation(_handPose.position, _handPose.rotation);
        }
        else if (IsOpen && Time.unscaledTime - _lastHandPoseAt > 0.35f)
        {
            Close();
        }
    }

    Pose _handPose;

    void BuildWearable()
    {
        _handAnchor = new GameObject("Left Hand Menu").transform;
        _handAnchor.SetParent(transform, false);
        _handAnchor.gameObject.SetActive(false);
        _root = new GameObject("RadialMenuRoot").transform;
        _root.SetParent(_handAnchor, false);
        _root.gameObject.SetActive(false);

        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        _buttonEdge = new Material(shader);
        PaintMaterial(_buttonEdge, new Color(0.43f, 0.19f, 0.12f));
        _iconMaterial = new Material(shader);
        PaintMaterial(_iconMaterial, new Color(1f, 0.89f, 0.67f));

        const float orbit = 0.065f;
        AddWearableAction(Choice.RestartLesson, 90f, orbit, new Color(0.68f, 0.30f, 0.15f));
        AddWearableAction(Choice.Recalibrate, 30f, orbit, new Color(0.67f, 0.23f, 0.27f));
        AddWearableAction(Choice.FineTune, -30f, orbit, new Color(0.49f, 0.38f, 0.16f));
        AddWearableAction(Choice.Fingering, -90f, orbit, new Color(0.47f, 0.29f, 0.64f));
        AddWearableAction(Choice.Visuals, -150f, orbit, new Color(0.21f, 0.43f, 0.58f));
        AddWearableAction(Choice.FreePlay, 150f, orbit, new Color(0.25f, 0.53f, 0.35f));
        _confirmButton = NewSegment(Choice.Close, "Confirm", addToRing: false);
        MakeButtonBody(_confirmButton, 0.024f);
        _confirmButton.label = Label(_confirmButton.go.transform, "Label", "", 0.074f,
            new Vector3(0f, 0f, -0.006f), new Vector2(0.047f, 0.031f));
        _confirmButton.go.SetActive(false);

        _handButton = NewSegment(Choice.Close, "Close", _handAnchor, addToRing: false);
        MakeButtonBody(_handButton, 0.019f);
        _handButton.label = Label(_handButton.go.transform, "Label", "Menu", 0.120f,
            new Vector3(0f, -0.006f, -0.006f), new Vector2(0.032f, 0.014f));
        _menuIcon = new GameObject("Piano keys icon");
        _menuIcon.transform.SetParent(_handButton.go.transform, false);
        for (int i = -1; i <= 1; i++)
            MakeShape(_menuIcon.transform, PrimitiveType.Cube, new Vector3(i * 0.005f, 0.005f, -0.006f),
                new Vector3(0.0036f, 0.009f, 0.0015f), Quaternion.identity, _iconMaterial);
        _closeIcon = new GameObject("Close icon");
        _closeIcon.transform.SetParent(_handButton.go.transform, false);
        foreach (float tilt in new[] { -45f, 45f })
            MakeShape(_closeIcon.transform, PrimitiveType.Cube, new Vector3(0f, 0.005f, -0.006f),
                new Vector3(0.002f, 0.011f, 0.0015f), Quaternion.Euler(0f, 0f, tilt), _iconMaterial);

        UpdateHandButton();
    }

    void UpdateHandButton()
    {
        if (_handButton == null) return;
        _menuIcon.SetActive(!IsOpen);
        _closeIcon.SetActive(IsOpen);
        _handButton.label.text = IsOpen ? "Close" : "Menu";
        SetColor(_handButton, IsOpen ? Clay : Amber);
    }

    void AddWearableAction(Choice choice, float angle, float orbit, Color color)
    {
        var segment = NewSegment(choice, choice.ToString());
        segment.color = color;
        segment.go.transform.localPosition = Polar(orbit, angle);
        MakeButtonBody(segment, 0.024f);
        segment.label = Label(segment.go.transform, "Label", "", 0.118f,
            new Vector3(0f, 0f, -0.006f), new Vector2(0.045f, 0.031f));
    }

    void MakeButtonBody(Segment segment, float buttonRadius)
    {
        segment.pokeRadius = buttonRadius;
        var parent = segment.go.transform;
        var rotation = Quaternion.Euler(90f, 0f, 0f);
        MakeShape(parent, PrimitiveType.Cylinder, Vector3.zero,
            new Vector3(buttonRadius * 2f, 0.003f, buttonRadius * 2f), rotation, _buttonEdge);
        MakeShape(parent, PrimitiveType.Cylinder, new Vector3(0f, 0f, -0.003f),
            new Vector3(buttonRadius * 1.87f, 0.002f, buttonRadius * 1.87f), rotation, segment.material);
    }

    static void MakeShape(Transform parent, PrimitiveType type, Vector3 position, Vector3 scale,
        Quaternion rotation, Material material)
    {
        var shape = GameObject.CreatePrimitive(type);
        var collider = shape.GetComponent<Collider>();
        collider.enabled = false;
        Destroy(collider);
        shape.transform.SetParent(parent, false);
        shape.transform.SetLocalPositionAndRotation(position, rotation);
        shape.transform.localScale = scale;
        var renderer = shape.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    void Build()
    {
        _root = new GameObject("RadialMenuRoot").transform;
        _root.SetParent(transform, false);

        // Four ring segments, 90° each, gaps of 8°: Restart on top, Free Play left, Recalibrate right, and at the bottom
        // Fingering (with a FingeringGuide) or Visuals.
        AddSegment(Choice.RestartLesson, 90f, Icon.Restart);
        AddSegment(Choice.FreePlay, 180f, Icon.Keys);
        AddSegment(Choice.Recalibrate, 0f, Icon.Target);
        if (_fingeringGuide) AddSegment(Choice.Fingering, 270f, Icon.None);
        else AddSegment(Choice.Visuals, 270f, Icon.Eye);
        AddCenter();
        _title = Label(_root, "Title", "Menu", 0.2f, new Vector3(0f, radius * 1.85f, 0f), new Vector2(0.2f, 0.03f));
    }

    enum Icon { Restart, Keys, Target, None, Eye }

    void AddSegment(Choice choice, float centerDeg, Icon icon)
    {
        var s = NewSegment(choice, choice.ToString());
        float inner = radius * 0.55f, outer = radius * 1.55f, half = 41f;
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
            case Icon.Eye: // almond outline, pupil
                var eye = new List<Vector3>();
                for (int i = 0; i <= 12; i++) { float a = i / 12f * Mathf.PI; eye.Add(iconAt + new Vector3(-Mathf.Cos(a) * 0.011f, Mathf.Sin(a) * 0.006f, 0f)); }
                for (int i = 1; i < 12; i++) { float a = i / 12f * Mathf.PI; eye.Add(iconAt + new Vector3(Mathf.Cos(a) * 0.011f, -Mathf.Sin(a) * 0.006f, 0f)); }
                Line(s, eye, true);
                var pupil = new List<Vector3>();
                for (int i = 0; i < 12; i++) pupil.Add(iconAt + Polar(0.003f, i * 30f));
                Line(s, pupil, true);
                break;
        }
        float labelOffset = icon == Icon.None ? 0f : -0.013f;
        s.label = Label(s.go.transform, "Label", "", 0.2f, c + new Vector3(0f, labelOffset, -0.002f), new Vector2(0.1f, 0.03f));
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

    Segment NewSegment(Choice choice, string name, Transform parent = null, bool addToRing = true)
    {
        var go = new GameObject(name + "Segment");
        go.transform.SetParent(parent ? parent : _root, false);
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        var s = new Segment { choice = choice, go = go, material = new Material(shader) { name = "RadialMenuRim (runtime)" } };
        if (addToRing) _segments.Add(s);
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
        if (s.painted == c) return;
        s.painted = c;
        PaintMaterial(s.material, c);
    }

    static void PaintMaterial(Material material, Color c)
    {
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", c);
        if (material.HasProperty("_Color")) material.SetColor("_Color", c);
    }
}
