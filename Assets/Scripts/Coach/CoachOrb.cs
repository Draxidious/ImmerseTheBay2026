using System.Collections.Generic;
using TMPro;
using UnityEngine;

public enum OrbState { Idle, Listening, Thinking, Speaking }

/// <summary>
/// Floating AI-coach orb with a short text message beside it, for the XREAL Aura (additive, optical see-through).
///
/// Placement: world-locked at the player's eye level, on their right side of the piano.
///  - With a <see cref="PianoOverlay"/> (calibrated, i.e. its <see cref="PianoCalibrator"/> isn't calibrating): anchored to
///    the keyboard, so it follows recalibration and grab-handle nudges.
///  - Without one: placed once relative to the head (forward + right), after the head pose is tracked and settled.
/// The text sits on the orb's inner side (toward the player's center of view) and turns to face the player (yaw only).
///
/// Nothing here depends on the AI scripts: the coach (or anything else) calls the public methods.
/// Editor debug keys: T = sample text, Y = cycle state, H = hide text, V = toggle fake voice level.
/// </summary>
public class CoachOrb : MonoBehaviour
{
    [Header("Parts (created automatically if empty)")]
    public Renderer orbRenderer;
    public TextMeshPro textLabel;
    [Tooltip("URP/Unlit (uses _BaseColor). Leave empty to auto-create at runtime.")]
    public Material orbMaterial;

    [Header("Orb")]
    public float orbDiameter = 0.07f;
    public Color idleColor = new Color(0.25f, 0.55f, 1f);
    public Color listeningColor = new Color(0.3f, 1f, 0.75f);
    public Color thinkingColorA = new Color(0.6f, 0.4f, 1f);
    public Color thinkingColorB = new Color(0.3f, 0.7f, 1f);
    public Color speakingColor = new Color(0.45f, 0.9f, 1f);

    [Header("Text")]
    [Tooltip("World font size. 0.18 ≈ 1.8 cm lines ≈ 1.5° at 0.68 m (Ari's calibration prompt uses 0.135 at 0.6 m).")]
    public float fontSize = 0.18f;
    [Tooltip("Wrap width in meters (≈30° at 0.68 m, ~40 characters per line at fontSize 0.18).")]
    public float textWidth = 0.36f;
    public float textGap = 0.025f;
    public Color textColor = new Color(0.88f, 0.97f, 1f);
    public Color textOutlineColor = new Color(0.25f, 0.65f, 1f);
    [Range(0f, 0.5f)] public float textOutlineWidth = 0.15f;
    [Tooltip("Longer text is cut at a word with \"...\". The coach prompt asks for at most 2 short sentences; real replies run ~160 chars.")]
    public int maxCharacters = 300;
    public float secondsPerWord = 0.3f;
    public float minSeconds = 3f;
    public float fadeSeconds = 0.25f;

    [Header("Placement")]
    [Tooltip("Optional. Found automatically. If present, the orb is anchored to the keyboard.")]
    public PianoOverlay piano;
    [Tooltip("Optional. Found automatically. While it is calibrating, the keyboard pose isn't trusted.")]
    public PianoCalibrator calibrator;
    [Tooltip("Optional. Defaults to Camera.main.")]
    public Transform head;
    [Tooltip("Meters right of the keyboard's center, along the keyboard.")]
    public float rightOfKeyboardCenter = 0.40f;
    [Tooltip("Meters past the front edge of the keys, toward the fallboard (away from the player).")]
    public float pastKeyFront = 0.10f;
    [Tooltip("Height relative to the player's eyes (0 = eye level).")]
    public float eyeLevelOffset = 0f;
    [Tooltip("No piano: meters in front of the head.")]
    public float fallbackForward = 0.55f;
    [Tooltip("No piano: meters to the right of the head.")]
    public float fallbackRight = 0.40f;
    [Tooltip("Head tracking must be valid this long before the orb is placed (poses settle after the session starts).")]
    public float trackingSettleSeconds = 1f;

    [Header("Debug")]
    [Tooltip("Editor only: T = sample text, Y = cycle state, H = hide text, V = fake voice. Untick if these keys are used elsewhere in your scene.")]
    public bool debugKeys = true;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    public OrbState State { get; private set; } = OrbState.Idle;
    public bool IsTextVisible => _textTarget > 0f;

    MaterialPropertyBlock _mpb;
    Color _color;
    float _voiceTarget, _voiceLevel, _lastVoiceTime = -10f;
    float _textAlpha, _textTarget, _hideAt = -1f;

    float _trackedSince = -1f;
    bool _placed;          // fallback (no piano) placement done
    bool _haveEyeHeight;
    float _eyeY;           // world Y of the player's eyes, measured when placed
    UnityEngine.XR.XRInputSubsystem _inputSubsystem;
    readonly List<UnityEngine.XR.XRInputSubsystem> _inputSubsystems = new List<UnityEngine.XR.XRInputSubsystem>();

    // ---------- Public API ----------

    /// <summary>Idle / Listening / Thinking / Speaking visuals.</summary>
    public void SetState(OrbState state)
    {
        State = state;
        if (state != OrbState.Speaking) _voiceTarget = 0f;
    }

    /// <summary>0..1 loudness of the AI voice; drives the pulse while Speaking. Call every frame during speech.</summary>
    public void SetVoiceLevel(float level01)
    {
        _voiceTarget = Mathf.Clamp01(level01);
        _lastVoiceTime = Time.time;
    }

    /// <summary>
    /// Show a short message beside the orb. Replaces any current message.
    /// seconds &lt;= 0: auto (secondsPerWord per word, at least minSeconds). Text over maxCharacters is cut at a word.
    /// </summary>
    public void ShowText(string text, float seconds = -1f)
    {
        if (string.IsNullOrWhiteSpace(text)) { HideText(); return; }
        EnsureParts();
        string clean = Truncate(System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " "), maxCharacters);
        textLabel.text = clean;
        int words = clean.Split(' ').Length;
        float duration = seconds > 0f ? seconds : Mathf.Max(minSeconds, words * secondsPerWord);
        _hideAt = Time.time + duration;
        _textTarget = 1f;
    }

    /// <summary>Fade the message out.</summary>
    public void HideText()
    {
        _textTarget = 0f;
        _hideAt = -1f;
    }

    /// <summary>Re-measure eye height and re-place the orb on the next tracked frame (e.g. after the player moves).</summary>
    public void Replace()
    {
        _placed = false;
        _haveEyeHeight = false;
        _trackedSince = -1f;
    }

    // ---------- Lifecycle ----------

    void Awake()
    {
        _mpb = new MaterialPropertyBlock();
        EnsureParts();
        _color = idleColor;
        textLabel.alpha = 0f;
        if (Application.isPlaying && textOutlineWidth > 0f)
        {
            // Bright rim instead of a dark box (black is invisible on the Aura). Instances the font material at runtime only.
            textLabel.outlineWidth = textOutlineWidth;
            textLabel.outlineColor = textOutlineColor;
        }
    }

    void OnEnable()
    {
        if (!piano) piano = FindAnyObjectByType<PianoOverlay>();
        if (!calibrator) calibrator = FindAnyObjectByType<PianoCalibrator>();
        if (calibrator) calibrator.Calibrated += OnPianoCalibrated;
    }

    void OnDisable()
    {
        if (calibrator) calibrator.Calibrated -= OnPianoCalibrated;
        if (_inputSubsystem != null) _inputSubsystem.trackingOriginUpdated -= OnTrackingOriginUpdated;
        _inputSubsystem = null;
    }

    void OnPianoCalibrated() => _haveEyeHeight = false; // re-measure eye level for the new piano pose

    void OnTrackingOriginUpdated(UnityEngine.XR.XRInputSubsystem _) => Replace(); // runtime re-centered

    void Update()
    {
        AnimateOrb();
        AnimateText();
#if UNITY_EDITOR
        DebugKeys();
#endif
    }

    void LateUpdate() => UpdatePlacement();

    // ---------- Placement ----------

    Transform Head() => head ? head : (Camera.main ? Camera.main.transform : null);

    /// <summary>True once the runtime reports a tracked head pose (always true without an XR device, e.g. Editor play mode).</summary>
    static bool HeadIsTracked()
    {
        if (!UnityEngine.XR.XRSettings.isDeviceActive) return true;
        var h = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
        return h.isValid && h.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool tracked) && tracked;
    }

    void HookTrackingOriginEvents()
    {
        if (_inputSubsystem != null && _inputSubsystem.running) return;
        if (_inputSubsystem != null) _inputSubsystem.trackingOriginUpdated -= OnTrackingOriginUpdated;
        _inputSubsystem = null;
        SubsystemManager.GetSubsystems(_inputSubsystems);
        foreach (var s in _inputSubsystems)
            if (s.running) { _inputSubsystem = s; break; }
        if (_inputSubsystem != null) _inputSubsystem.trackingOriginUpdated += OnTrackingOriginUpdated;
    }

    static Vector3 Flat(Vector3 v, Vector3 fallback)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-6f ? fallback : v.normalized;
    }

    bool PianoIsUsable() => piano && piano.isActiveAndEnabled && !(calibrator && calibrator.IsCalibrating);

    void UpdatePlacement()
    {
        HookTrackingOriginEvents();
        var h = Head();
        if (!h || !HeadIsTracked()) { _trackedSince = -1f; return; }
        if (_trackedSince < 0f) _trackedSince = Time.time;
        if (Time.time - _trackedSince < trackingSettleSeconds) return; // let the pose settle first

        if (!_haveEyeHeight) { _eyeY = h.position.y; _haveEyeHeight = true; }
        float y = _eyeY + eyeLevelOffset;

        if (PianoIsUsable())
        {
            // Follow the keyboard every frame (recalibration, grab nudges), at the stored eye height.
            Vector3 low = piano.GetKeyWorldCenter(piano.lowestNote);
            Vector3 high = piano.GetKeyWorldCenter(piano.highestNote);
            Vector3 right = Flat(high - low, Vector3.right);
            Vector3 away = Vector3.Cross(right, Vector3.up); // toward the fallboard
            Vector3 front = piano.transform.position;          // local origin = front edge of the keys
            Vector3 center = (low + high) * 0.5f;
            float frontDepth = Vector3.Dot(center - front, away);
            Vector3 p = center + right * rightOfKeyboardCenter + away * (pastKeyFront - frontDepth);
            p.y = y;
            transform.position = p;
            _placed = true;
        }
        else if (!_placed)
        {
            Vector3 fwd = Flat(h.forward, Vector3.forward);
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 p = h.position + fwd * fallbackForward + right * fallbackRight;
            p.y = y;
            transform.position = p;
            _placed = true;
        }

        // Yaw-only billboard: TMP faces -Z, so point +Z away from the player.
        Vector3 look = Flat(transform.position - h.position, transform.forward);
        transform.rotation = Quaternion.LookRotation(look, Vector3.up);

        // The text sits beside the orb, so turn it to face the player from its own position (no oblique skew).
        if (textLabel)
        {
            Vector3 textLook = Flat(textLabel.transform.position - h.position, look);
            textLabel.transform.rotation = Quaternion.LookRotation(textLook, Vector3.up);
        }
    }

    // ---------- Visuals ----------

    void AnimateOrb()
    {
        if (!orbRenderer) return;
        float t = Time.time;
        float dt = Time.deltaTime;

        // Voice level: fast attack, slower release; decays if nobody calls SetVoiceLevel.
        if (Time.time - _lastVoiceTime > 0.25f) _voiceTarget = 0f;
        float rate = _voiceTarget > _voiceLevel ? 25f : 8f;
        _voiceLevel = Mathf.Lerp(_voiceLevel, _voiceTarget, 1f - Mathf.Exp(-rate * dt));

        Color target;
        float scale = 1f;
        switch (State)
        {
            case OrbState.Listening:
                target = listeningColor * (0.9f + 0.1f * Mathf.Sin(t * Mathf.PI * 2f / 1.2f));
                scale = 1.08f;
                break;
            case OrbState.Thinking:
                target = Color.Lerp(thinkingColorA, thinkingColorB, 0.5f + 0.5f * Mathf.Sin(t * Mathf.PI * 2f / 1.6f)) * 0.85f;
                break;
            case OrbState.Speaking:
                target = speakingColor * (0.55f + 0.45f * _voiceLevel);
                scale = 1f + 0.18f * _voiceLevel;
                break;
            default: // Idle: slow, subtle breathing
                target = idleColor * (0.5f + 0.15f * Mathf.Sin(t * Mathf.PI * 2f / 4f));
                break;
        }

        _color = Color.Lerp(_color, target, 1f - Mathf.Exp(-8f * dt));
        var c = _color; c.a = 1f;
        orbRenderer.GetPropertyBlock(_mpb);
        _mpb.SetColor(BaseColorId, c);
        _mpb.SetColor(ColorId, c);
        orbRenderer.SetPropertyBlock(_mpb);
        orbRenderer.transform.localScale = Vector3.one * (orbDiameter * scale);
    }

    void AnimateText()
    {
        if (!textLabel) return;
        if (_hideAt > 0f && Time.time >= _hideAt) HideText();
        float step = fadeSeconds > 0f ? Time.deltaTime / fadeSeconds : 1f;
        _textAlpha = Mathf.MoveTowards(_textAlpha, _textTarget, step);
        textLabel.alpha = _textAlpha;
        bool show = _textAlpha > 0.001f;
        if (textLabel.gameObject.activeSelf != show) textLabel.gameObject.SetActive(show);
    }

    static string Truncate(string s, int max)
    {
        if (max <= 3 || s.Length <= max) return s;
        int cut = s.LastIndexOf(' ', max - 3);
        if (cut < max / 2) cut = max - 3;
        return s.Substring(0, cut).TrimEnd(',', ';', ':', ' ') + "...";
    }

    // ---------- Parts ----------

    /// <summary>Create the orb sphere and text label if they're missing. Called by the editor menu and at Awake.</summary>
    public void EnsureParts()
    {
        if (!orbRenderer)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Orb";
            if (Application.isPlaying) Destroy(go.GetComponent<Collider>()); else DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * orbDiameter;
            orbRenderer = go.GetComponent<Renderer>();
            orbRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            orbRenderer.receiveShadows = false;
        }
        if (!orbMaterial)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            orbMaterial = new Material(shader) { name = "CoachOrbUnlit (runtime)" };
        }
        if (orbRenderer.sharedMaterial != orbMaterial) orbRenderer.sharedMaterial = orbMaterial;

        if (!textLabel)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(transform, false);
            textLabel = go.AddComponent<TextMeshPro>();
            textLabel.text = "";
        }
        // Inner side of the orb = toward the player's center of view (the orb sits on their right).
        textLabel.transform.localPosition = new Vector3(-(orbDiameter * 0.5f + textGap + textWidth * 0.5f), 0f, 0f);
        textLabel.transform.localRotation = Quaternion.identity;
        textLabel.rectTransform.sizeDelta = new Vector2(textWidth, 0.2f);
        textLabel.fontSize = fontSize;
        textLabel.color = textColor;
        textLabel.alignment = TextAlignmentOptions.Right; // hug the orb
        textLabel.textWrappingMode = TextWrappingModes.Normal;
        textLabel.overflowMode = TextOverflowModes.Overflow;
    }

#if UNITY_EDITOR
    static readonly string[] SampleLines =
    {
        "Nice work! Your left hand was steady in bars 3 and 4.",
        "Watch the E in bar 6. Try that phrase a little slower.",
        "Ready? Let's start from the top.",
        "That was much smoother. One more time, and keep the wrist relaxed through the run in bar 7.",
    };
    int _sample;
    bool _fakeVoice;

    void DebugKeys()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (!debugKeys || kb == null) return;
        if (kb.tKey.wasPressedThisFrame) ShowText(SampleLines[_sample++ % SampleLines.Length]);
        if (kb.yKey.wasPressedThisFrame) SetState((OrbState)(((int)State + 1) % 4));
        if (kb.hKey.wasPressedThisFrame) HideText();
        if (kb.vKey.wasPressedThisFrame)
        {
            _fakeVoice = !_fakeVoice;
            SetState(_fakeVoice ? OrbState.Speaking : OrbState.Idle);
        }
        if (_fakeVoice && State == OrbState.Speaking)
            SetVoiceLevel(Mathf.Clamp01(0.5f + 0.5f * Mathf.Sin(Time.time * 9f) * Mathf.Sin(Time.time * 2.3f)));
    }
#endif
}
