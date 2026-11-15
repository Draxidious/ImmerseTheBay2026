using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Hands;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

// Presentation only. Authored lesson notes drive the cues while XR Hands supplies their positions.
internal sealed class FingeringGuide : MonoBehaviour
{
    [SerializeField] AppFlow flow;
    [SerializeField] CurriculumEngine engine;
    [SerializeField] PianoOverlay overlay;
    [SerializeField] PianoCalibrator calibrator;
    [SerializeField, Range(0f, 1f)] float glowOpacity = 0.76f;

    static readonly XRHandJointID[] TipJoints =
    {
        XRHandJointID.ThumbTip, XRHandJointID.IndexTip, XRHandJointID.MiddleTip,
        XRHandJointID.RingTip, XRHandJointID.LittleTip
    };
    static readonly XRHandJointID[] DistalJoints =
    {
        XRHandJointID.ThumbDistal, XRHandJointID.IndexDistal, XRHandJointID.MiddleDistal,
        XRHandJointID.RingDistal, XRHandJointID.LittleDistal
    };

    const string PreferenceKey = "PianoFingeringOverlay";
    const float BadgeDiameter = 0.017f;
    static readonly Color BadgeColor = new Color(1f, 0.77f, 1f);
    static readonly Color RingColor = new Color(0.93f, 0.47f, 1f);
    static readonly Color ApproachGlow = new Color(0.68f, 1f, 0.16f);
    static readonly Color PressGlow = new Color(0.86f, 1f, 0.66f);
    static readonly Color IdleNumber = new Color(0.83f, 0.91f, 1f, 0.36f);
    double _nextPressLog;

    sealed class Finger
    {
        public TextMeshPro number;
        public TextMeshPro badge;
        public SpriteRenderer ring;
        public SpriteRenderer glow;
        public ExpectedNote note;
        public double until;
        public float glowAmount;
        public double pressUntil;
        public int pressedMidi;
    }

    readonly Finger[] _fingers = new Finger[10]; // left thumb to pinky, then right thumb to pinky
    readonly List<XRHandSubsystem> _subsystems = new List<XRHandSubsystem>();
    XRHandSubsystem _hands;
    Sprite _glowSprite;
    Sprite _ringSprite;
    Camera _camera;
    bool _showFingering;
#if UNITY_EDITOR
    bool _simulateHands;
#endif

    bool InLesson => flow && flow.CurrentStep == AppFlow.Step.Lesson && !VisualsMode.NonVisual;
    internal bool OverlayEnabled => _showFingering;

    internal void ToggleOverlay()
    {
        _showFingering = !_showFingering;
        PlayerPrefs.SetInt(PreferenceKey, _showFingering ? 1 : 0);
        PlayerPrefs.Save();
        if (!_showFingering) HideLabels();
    }

    void Awake()
    {
        _showFingering = PlayerPrefs.GetInt(PreferenceKey, 1) != 0;
        _glowSprite = CreateCueSprite(false);
        _ringSprite = CreateCueSprite(true);
        for (int i = 0; i < _fingers.Length; i++)
        {
            string hand = i < 5 ? "L" : "R";
            string number = (i % 5 + 1).ToString();
            _fingers[i] = new Finger
            {
                number = MakeLabel($"Fingertip {hand}{number}", number, 0.010f),
                badge = MakeLabel($"Key badge {hand}{number}", hand + number, 0.009f),
                ring = MakeSprite($"Key ring {hand}{number}", _ringSprite, BadgeDiameter),
                glow = MakeSprite($"Fingertip glow {hand}{number}", _glowSprite, 0.036f)
            };
            _fingers[i].badge.color = BadgeColor;
            _fingers[i].ring.color = RingColor;
        }
    }

    void OnEnable()
    {
        if (!engine) return;
        engine.OnRunStarted += PreviewFirstNotes;
        engine.OnNextNote += OnNoteDue;
        engine.OnDemoNoteDown += OnDemoNoteDown;
        engine.OnDemoNoteUp += OnDemoNoteUp;
        engine.OnNotePlayed += OnNotePlayed;
    }

    void OnDisable()
    {
        if (engine)
        {
            engine.OnRunStarted -= PreviewFirstNotes;
            engine.OnNextNote -= OnNoteDue;
            engine.OnDemoNoteDown -= OnDemoNoteDown;
            engine.OnDemoNoteUp -= OnDemoNoteUp;
            engine.OnNotePlayed -= OnNotePlayed;
        }
        ClearNotes();
        HideLabels();
    }

    void OnDestroy()
    {
        foreach (var finger in _fingers)
        {
            if (finger == null) continue;
            Destroy(finger.number.gameObject);
            Destroy(finger.badge.gameObject);
            Destroy(finger.ring.gameObject);
            Destroy(finger.glow.gameObject);
        }
        if (_glowSprite) Destroy(_glowSprite.texture);
        if (_ringSprite) Destroy(_ringSprite.texture);
        Destroy(_glowSprite);
        Destroy(_ringSprite);
    }

    void PreviewFirstNotes(RunInfo run)
    {
        ClearNotes();
        if (run.notes.Count == 0) return;
        float onset = run.notes[0].time;
        foreach (var note in run.notes)
        {
            if (note.time > onset + 0.001f) break;
            SetNote(note, RunCueEnd(run, note));
        }
    }

    void OnNoteDue(ExpectedNote note)
    {
        if (note == null)
            ClearNotes();
        else if (engine.CurrentRun != null)
            SetNote(note, RunCueEnd(engine.CurrentRun, note));
    }

    static double RunCueEnd(RunInfo run, ExpectedNote note) =>
        run.startTime + note.time + Mathf.Max(0.12f, note.duration * run.secondsPerBeat);

    void OnDemoNoteDown(ExpectedNote note) => SetNote(note, double.PositiveInfinity);

    void OnDemoNoteUp(ExpectedNote note)
    {
        if (HasFingering(note) && _fingers[FingerIndex(note)].note == note)
            _fingers[FingerIndex(note)].note = null;
    }

    internal bool TryReplayFingering(CurriculumEngine source, int bar)
    {
        if (!_showFingering || source != engine || !engine || !InLesson || !calibrator || calibrator.IsCalibrating)
            return false;
        var run = engine.CurrentRun;
        if (run == null || !engine.CurrentRange.Contains(bar)) return false;
        foreach (var note in run.notes)
        {
            if (note.bar != bar || !HasFingering(note)) continue;
            return engine.ReplayDemo();
        }
        return false;
    }

    static bool HasFingering(ExpectedNote note) => note != null && note.finger >= 1 && note.finger <= 5;

    static int FingerIndex(ExpectedNote note) => (note.hand == PianoHand.Left ? 0 : 5) + note.finger - 1;

    void SetNote(ExpectedNote note, double until)
    {
        if (!HasFingering(note)) return;
        var finger = _fingers[FingerIndex(note)];
        foreach (var previous in _fingers)
        {
            if (previous == finger || previous.note?.midi != note.midi || previous.note.hand != note.hand) continue;
            previous.note = null;
        }
        if (finger.note != note && finger.note?.midi != note.midi) finger.glowAmount = 0f;
        finger.note = note;
        finger.until = until;
    }

    void ClearNotes()
    {
        foreach (var finger in _fingers)
        {
            if (finger == null) continue;
            finger.note = null;
            finger.glowAmount = 0f;
            finger.pressUntil = 0;
        }
    }

    void HideLabels()
    {
        foreach (var finger in _fingers)
        {
            if (finger == null) continue;
            finger.number.enabled = false;
            finger.badge.enabled = false;
            finger.ring.enabled = false;
            finger.glow.enabled = false;
            finger.glowAmount = 0f;
            finger.pressUntil = 0;
        }
    }

    void OnNotePlayed(ExpectedNote note, int midi, bool correct)
    {
        if (!correct || !_showFingering || !InLesson || calibrator.IsCalibrating || !HasFingering(note) ||
            !overlay.TryGetKeySurface(midi, out var surface)) return;
        int index = FingerIndex(note);
        if (!TryGetTip(index, TrackingSpace(), midi, out var tip, out _, out _) ||
            KeyProximity(overlay.transform.InverseTransformPoint(tip), surface) < 0.8f) return;
        _fingers[index].pressedMidi = midi;
        double now = Time.unscaledTimeAsDouble;
        _fingers[index].pressUntil = now + 0.24;
        if (now >= _nextPressLog)
        {
            _nextPressLog = now + 1;
            AuraInteractionLog.Write($"[FingeringGuide] Press glow: MIDI {midi}, {note.hand} finger {note.finger}.");
        }
    }

    Transform TrackingSpace()
    {
        var origin = calibrator.xrOrigin;
        return origin ? (origin.CameraFloorOffsetObject ? origin.CameraFloorOffsetObject.transform : origin.transform) : null;
    }

    void LateUpdate()
    {
        bool inCalibratedLesson = InLesson && overlay && calibrator &&
            !calibrator.IsCalibrating && overlay.isActiveAndEnabled;
        if (!inCalibratedLesson || !_showFingering)
        {
            HideLabels();
            return;
        }

        if (!_camera) _camera = Camera.main;
        if (!_camera) return;
        if (_hands == null || !_hands.running)
        {
            _hands = null;
            SubsystemManager.GetSubsystems(_subsystems);
            foreach (var subsystem in _subsystems)
            {
                if (!subsystem.running) continue;
                _hands = subsystem;
                break;
            }
        }
#if UNITY_EDITOR
        if (Keyboard.current?.f10Key.wasPressedThisFrame == true) _simulateHands = !_simulateHands;
#endif

        var cameraPose = _camera.transform;
        var keyboard = overlay.transform;
        var badgeRotation = keyboard.rotation * Quaternion.Euler(90f, 0f, 0f);
        var trackingSpace = TrackingSpace();
        double now = Time.unscaledTimeAsDouble;

        for (int i = 0; i < _fingers.Length; i++)
        {
            var finger = _fingers[i];
            var note = finger.note;
            if (note != null && now >= finger.until)
                finger.note = note = null;

            Bounds surface = default;
            bool hasKey = note != null && overlay.TryGetKeySurface(note.midi, out surface);
            finger.badge.enabled = hasKey;
            finger.ring.enabled = hasKey;
            if (hasKey)
            {
                // Stack badges along the key when both hands are assigned the same pitch.
                Vector3 anchor = surface.center + Vector3.forward * SharedKeyBadgeOffset(i, note.midi);
                anchor.y += overlay.hoverHeight + overlay.thickness + 0.001f;
                Vector3 badgePosition = keyboard.TransformPoint(anchor);
                finger.ring.transform.SetPositionAndRotation(badgePosition, badgeRotation);
                finger.badge.transform.SetPositionAndRotation(badgePosition + keyboard.up * 0.0005f, badgeRotation);
            }

            bool pressed = now < finger.pressUntil && (note == null || note.midi == finger.pressedMidi);
            int tipMidi = note?.midi ?? (pressed ? finger.pressedMidi : (i < 5 ? 48 : 64) + i % 5);
            bool tracked = TryGetTip(i, trackingSpace, tipMidi, out Vector3 tip,
                out Vector3 backAlongFinger, out float radius);
            finger.number.enabled = tracked;
            if (!tracked)
            {
                finger.glow.enabled = false;
                finger.glowAmount = 0f;
                continue;
            }

            Vector3 center = tip + backAlongFinger * 0.005f;
            Vector3 towardEye = (cameraPose.position - center).normalized;
            finger.number.color = pressed || hasKey ? Color.white : IdleNumber;
            finger.number.transform.SetPositionAndRotation(center + towardEye * (radius + 0.002f), cameraPose.rotation);
            float proximity = hasKey ? KeyProximity(keyboard.InverseTransformPoint(tip), surface) : 0f;
            finger.glowAmount = Mathf.MoveTowards(finger.glowAmount, proximity, Time.unscaledDeltaTime * 10f);
            finger.glow.enabled = pressed || finger.glowAmount > 0f;
            if (finger.glow.enabled)
            {
                Color cueColor = pressed ? PressGlow : ApproachGlow;
                cueColor.a = pressed ? 1f : glowOpacity * finger.glowAmount;
                finger.glow.color = cueColor;
                finger.glow.transform.localScale = Vector3.one * (pressed ? 0.044f : 0.036f);
                finger.glow.transform.SetPositionAndRotation(center + towardEye * (radius + 0.001f), cameraPose.rotation);
            }
        }
    }

    float SharedKeyBadgeOffset(int index, int midi)
    {
        for (int i = 0; i < _fingers.Length; i++)
        {
            if (i == index || _fingers[i].note?.midi != midi || Time.unscaledTimeAsDouble >= _fingers[i].until) continue;
            return index < i ? -0.010f : 0.010f;
        }
        return 0f;
    }

    bool TryGetTip(int index, Transform trackingSpace, int midi, out Vector3 tip, out Vector3 backAlongFinger, out float radius)
    {
        backAlongFinger = Vector3.zero;
        radius = 0.008f;
#if UNITY_EDITOR
        if (_simulateHands)
        {
            float approach = 0.004f + Mathf.PingPong(Time.unscaledTime * 0.05f, 0.022f);
            tip = overlay.GetKeyWorldCenter(midi) + overlay.transform.up * approach;
            backAlongFinger = -overlay.transform.forward;
            return true;
        }
#endif
        if (_hands != null && trackingSpace)
        {
            var hand = index < 5 ? _hands.leftHand : _hands.rightHand;
            var joint = hand.GetJoint(TipJoints[index % 5]);
            if (hand.isTracked && joint.TryGetPose(out var pose))
            {
                tip = trackingSpace.TransformPoint(pose.position);
                if (hand.GetJoint(DistalJoints[index % 5]).TryGetPose(out var distal))
                    backAlongFinger = (trackingSpace.TransformPoint(distal.position) - tip).normalized;
                if (joint.TryGetRadius(out float trackedRadius))
                    radius = Mathf.Clamp(trackedRadius, 0.005f, 0.011f);
                return true;
            }
        }
        tip = default;
        return false;
    }

    static float KeyProximity(Vector3 tip, Bounds surface)
    {
        var closest = surface.ClosestPoint(tip);
        float edgeDistance = new Vector2(tip.x - closest.x, tip.z - closest.z).magnitude;
        float height = tip.y - closest.y;
        float footprint = 1f - Mathf.SmoothStep(0f, 1f, edgeDistance / 0.006f);
        float above = 1f - Mathf.SmoothStep(0f, 1f, (height - 0.012f) / 0.034f);
        float below = 1f - Mathf.SmoothStep(0f, 1f, (-height - 0.010f) / 0.012f);
        return footprint * above * below;
    }

    TextMeshPro MakeLabel(string objectName, string text, float scale)
    {
        var label = new GameObject(objectName).AddComponent<TextMeshPro>();
        label.transform.SetParent(transform, false);
        label.transform.localScale = Vector3.one * scale;
        label.text = text;
        label.fontSize = 8f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.rectTransform.sizeDelta = new Vector2(2f, 1f);
        label.enabled = false;
        return label;
    }

    SpriteRenderer MakeSprite(string objectName, Sprite sprite, float diameter)
    {
        var go = new GameObject(objectName);
        go.transform.SetParent(transform, false);
        go.transform.localScale = Vector3.one * diameter;
        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.enabled = false;
        return renderer;
    }

    static Sprite CreateCueSprite(bool ring)
    {
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var uv = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
            float radius = (uv - Vector2.one * 0.5f).magnitude * 2f;
            float alpha = ring
                ? Mathf.SmoothStep(0f, 1f, (radius - 0.78f) / 0.07f) * (1f - Mathf.SmoothStep(0f, 1f, (radius - 0.93f) / 0.07f))
                : 1f - Mathf.SmoothStep(0f, 1f, (radius - 0.25f) / 0.75f);
            pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
        }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
    }
}
