using System.Collections.Generic;
using UnityEngine;

public enum KeyState { Off, Upcoming, Next, Correct, Wrong }

/// <summary>
/// Procedural 88-key overlay sized for a Yamaha P71 (A0..C8).
/// Local frame: +X = left-to-right along the keyboard, +Z = from the front edge of the keys
/// toward the fallboard (away from the player), +Y = up. Local origin = front edge of the
/// lowest white key's left side.
///
/// Built for an additive (optical see-through) display: Off = black = invisible.
/// </summary>
public class PianoOverlay : MonoBehaviour
{
    [Header("Range (MIDI note numbers)")]
    public int lowestNote = 21;   // A0
    public int highestNote = 108; // C8

    [Header("Key geometry in meters (standard acoustic sizing; measure your P71 to confirm)")]
    public float whiteKeyWidth = 0.0235f;   // ~165 mm per octave / 7
    public float whiteKeyLength = 0.145f;   // visible playing surface
    public float blackKeyWidth = 0.0130f;
    public float blackKeyLength = 0.090f;
    public float keyGap = 0.0015f;
    public float hoverHeight = 0.004f;      // float slightly above the key tops
    public float thickness = 0.001f;

    [Tooltip("Calibration keeps the keyboard's tilt along its length up to this angle; steeper is treated as noise (level).")]
    public float maxTiltDegrees = 5f;

    [Tooltip("Fraction of each key's front left undrawn so the overlay doesn't sit on the player's fingers.")]
    [Range(0f, 0.8f)] public float frontClearFraction = 0.35f;

    [Tooltip("How far black keys stand above the white key tops (standard ≈ 12 mm).")]
    public float blackKeyRaise = 0.012f;

    [Header("Look")]
    public Material keyMaterial; // e.g. URP/Unlit (uses _BaseColor). Leave empty to auto-create.
    [Tooltip("Draw Off keys so the whole keyboard is visible (black would be invisible on the Aura).")]
    public bool showIdleKeys = true;
    [Tooltip("Draw each key as a bright outline (idle) instead of a dim filled slab. Lit keys get a filled top too. " +
             "Outlines read well on an additive display and show misalignment against the real key gaps.")]
    public bool outlineKeys = true;
    public Color outlineColor = new Color(0.45f, 0.75f, 1f);
    public float outlineWidth = 0.0015f;
    public Color idleWhiteColor = new Color(0.10f, 0.10f, 0.12f);
    public Color idleBlackColor = new Color(0.03f, 0.03f, 0.05f);
    public Color upcomingColor = new Color(0.25f, 0.55f, 1f);
    public Color nextColor = new Color(0.3f, 0.85f, 1f);
    public Color correctColor = new Color(0.3f, 1f, 0.45f);
    public Color wrongColor = new Color(1f, 0.25f, 0.25f);
    public float flashSeconds = 0.35f;
    public float pulseSpeed = 4f;

    static readonly bool[] IsBlackPc = { false, true, false, true, false, false, true, false, true, false, true, false }; // C..B
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    readonly Dictionary<int, Renderer> _keys = new Dictionary<int, Renderer>();          // filled top per key
    readonly Dictionary<int, Renderer[]> _outlines = new Dictionary<int, Renderer[]>();  // outline segments per key
    readonly List<GameObject> _keyRoots = new List<GameObject>();
    readonly Dictionary<int, Vector3> _keyCenters = new Dictionary<int, Vector3>();
    readonly Dictionary<int, KeyState> _states = new Dictionary<int, KeyState>();
    readonly Dictionary<int, float> _flashUntil = new Dictionary<int, float>();
    MaterialPropertyBlock _mpb;

    const string PrefsKey = "PianoOverlayCalibration";

    public static bool IsBlack(int midiNote) => IsBlackPc[((midiNote % 12) + 12) % 12];

    void Awake()
    {
        _mpb = new MaterialPropertyBlock();
        Build();
        LoadCalibration();
    }

    // ---------- Public API (agreed with the lesson logic) ----------

    public void SetRange(int low, int high)
    {
        lowestNote = low;
        highestNote = high;
        Build();
    }

    public void SetKey(int midiNote, KeyState state)
    {
        if (!_keys.ContainsKey(midiNote)) return;
        _states[midiNote] = state;
        if (state == KeyState.Correct || state == KeyState.Wrong)
            _flashUntil[midiNote] = Time.time + flashSeconds;
        Paint(midiNote, 1f);
    }

    /// <summary>Show or hide the faint outline of Off keys at runtime.</summary>
    public void SetIdleKeysVisible(bool visible)
    {
        showIdleKeys = visible;
        foreach (var n in new List<int>(_keys.Keys)) Paint(n, 1f);
    }

    public void ClearAll()
    {
        foreach (var n in new List<int>(_keys.Keys)) SetKey(n, KeyState.Off);
        _flashUntil.Clear();
    }

    /// <summary>World-space center of a key's drawn area (for lines from the sheet panel, particles, etc.).</summary>
    public Vector3 GetKeyWorldCenter(int midiNote) =>
        _keyCenters.TryGetValue(midiNote, out var c) ? transform.TransformPoint(c) : transform.position;

    /// <summary>
    /// Snap the overlay onto the real piano. Pass the world positions the user pinched on the
    /// FRONT EDGE, CENTER of the lowest key (A0) and the highest key (C8).
    /// </summary>
    public void CalibrateFromTwoKeys(Vector3 worldLowFront, Vector3 worldHighFront)
    {
        Vector3 localLow = FrontCenterLocal(lowestNote);
        Vector3 localHigh = FrontCenterLocal(highestNote);
        float modelSpan = Vector3.Distance(localLow, localHigh);

        Vector3 right = worldHighFront - worldLowFront;
        float worldSpan = right.magnitude;
        if (worldSpan < 0.05f || modelSpan < 0.001f) return; // ignore accidental double-pinch
        right /= worldSpan;

        // Keep the real tilt along the keyboard (a 1° slope is ±1 cm at the ends), but treat anything steeper
        // than maxTiltDegrees as pinch noise and stay level. Front-to-back tilt can't be measured from two
        // front-edge points, so "forward" stays horizontal.
        float tilt = Mathf.Asin(Mathf.Clamp(right.y, -1f, 1f)) * Mathf.Rad2Deg;
        if (Mathf.Abs(tilt) > maxTiltDegrees) { right.y = 0f; right.Normalize(); }
        Vector3 forward = Vector3.Cross(right, Vector3.up).normalized; // toward the fallboard
        Vector3 up = Vector3.Cross(forward, right);
        Quaternion rot = Quaternion.LookRotation(forward, up);
        float s = worldSpan / modelSpan;

        transform.rotation = rot;
        transform.localScale = Vector3.one * s;
        Vector3 avgFront = (worldLowFront + worldHighFront) * 0.5f;
        Vector3 localMid = (localLow + localHigh) * 0.5f;
        transform.position = avgFront - rot * (localMid * s);

        SaveCalibration();
    }

    /// <summary>
    /// Snap the overlay onto the real piano from four pinched points: the FRONT edge and the BACK end (where the key
    /// meets the panel) of the lowest and the highest key. Gives position, both tilts (clamped to maxTiltDegrees, else
    /// level), uniform scale from the width, and the real visible key length from the depth (keys are rebuilt).
    /// Returns null on success, or a short reason when the points don't describe a keyboard.
    /// </summary>
    public string CalibrateFromFourPoints(Vector3 frontLow, Vector3 backLow, Vector3 frontHigh, Vector3 backHigh)
    {
        Vector3 localLow = FrontCenterLocal(lowestNote);
        Vector3 localHigh = FrontCenterLocal(highestNote);
        float modelSpan = Vector3.Distance(localLow, localHigh);

        Vector3 frontVec = frontHigh - frontLow, backVec = backHigh - backLow;
        float span = (frontVec.magnitude + backVec.magnitude) * 0.5f;
        if (span < 0.05f || modelSpan < 0.001f) return "points too close together";

        Vector3 right = (frontVec.normalized + backVec.normalized).normalized;
        // Side-to-side tilt is measured over ~1.2 m (reliable); keep it up to maxTiltDegrees, else level.
        float roll = Mathf.Asin(Mathf.Clamp(right.y, -1f, 1f)) * Mathf.Rad2Deg;
        if (Mathf.Abs(roll) > maxTiltDegrees) { right.y = 0f; right.Normalize(); }
        // Front-to-back tilt over ~14 cm of key depth would be dominated by ~1 cm hand-tracking jitter, so
        // "forward" stays horizontal; the back points give the key length and a direction check only.
        Vector3 forward = Vector3.Cross(right, Vector3.up).normalized; // toward the fallboard
        Vector3 up = Vector3.Cross(forward, right);
        Vector3 depthVec = ((backLow - frontLow) + (backHigh - frontHigh)) * 0.5f;
        float depth = Vector3.Dot(depthVec, forward);
        if (depth < 0.02f) return "the back points aren't behind the front points";

        float s = span / modelSpan;
        // Real visible white-key length from the measured depth (model units), black keys in proportion.
        float ratio = whiteKeyLength > 0f ? blackKeyLength / whiteKeyLength : 0.62f;
        whiteKeyLength = depth / s;
        blackKeyLength = whiteKeyLength * ratio;
        Build();

        Quaternion rot = Quaternion.LookRotation(forward, up);
        transform.rotation = rot;
        transform.localScale = Vector3.one * s;
        Vector3 avgFront = (frontLow + frontHigh) * 0.5f;
        Vector3 localMid = (FrontCenterLocal(lowestNote) + FrontCenterLocal(highestNote)) * 0.5f;
        transform.position = avgFront - rot * (localMid * s);

        SaveCalibration();
        return null;
    }

    // ---------- Build ----------

    public void Build()
    {
        foreach (var root in _keyRoots) if (root) Destroy(root);
        foreach (var r in _keys.Values) if (r && r.transform.parent == transform) Destroy(r.gameObject); // older layout
        _keyRoots.Clear(); _outlines.Clear();
        _keys.Clear(); _keyCenters.Clear(); _states.Clear(); _flashUntil.Clear();

        if (keyMaterial == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            keyMaterial = new Material(shader);
        }

        int whiteIndex = 0;
        for (int n = lowestNote; n <= highestNote; n++)
        {
            bool black = IsBlack(n);
            float width, length, centerX, zStart, y;

            if (!black)
            {
                width = whiteKeyWidth - keyGap;
                length = whiteKeyLength;
                centerX = whiteIndex * whiteKeyWidth + whiteKeyWidth * 0.5f;
                zStart = 0f;
                y = hoverHeight;
                whiteIndex++;
            }
            else
            {
                // Standard layout: C#/D# split the C–E span (3 white keys) into 5 equal parts, F#/G#/A# split the
                // F–B span (4 white keys) into 7. Positions are measured from the left edge of the octave's C.
                int pc = ((n % 12) + 12) % 12;
                int prevWhiteFromC = pc switch { 1 => 0, 3 => 1, 6 => 3, 8 => 4, _ => 5 }; // C, D, F, G, A
                float cLeft = (whiteIndex - 1 - prevWhiteFromC) * whiteKeyWidth;
                float offset = pc switch { 1 => 1.5f * 3f / 5f, 3 => 3.5f * 3f / 5f, 6 => 3f + 1.5f * 4f / 7f, 8 => 3f + 3.5f * 4f / 7f, _ => 3f + 5.5f * 4f / 7f };
                width = blackKeyWidth;
                length = blackKeyLength;
                centerX = cLeft + offset * whiteKeyWidth;
                zStart = whiteKeyLength - blackKeyLength;
                y = hoverHeight + blackKeyRaise; // black keys stand above the white keys
            }

            var root = new GameObject($"Key_{n}{(black ? "_b" : "")}");
            root.transform.SetParent(transform, false);
            _keyRoots.Add(root);

            // Filled top (lit states). Leave the front part clear for the player's fingers.
            float drawStart = zStart + length * frontClearFraction;
            float drawLength = length - (drawStart - zStart);
            var center = new Vector3(centerX, y, drawStart + drawLength * 0.5f);
            var r = CreateBar(root.transform, "Fill", center, new Vector3(width, thickness, drawLength));

            // Outline over the whole key: left, right, front, back. White-key sides stop where a black key covers them.
            var segs = new List<Renderer>();
            if (outlineKeys)
            {
                float w = outlineWidth, half = width * 0.5f, oy = y + thickness;
                float zEnd = zStart + length;
                float leftEnd = zEnd, rightEnd = zEnd;
                if (!black)
                {
                    int pc = ((n % 12) + 12) % 12;
                    float blackFront = whiteKeyLength - blackKeyLength;
                    bool blackOnLeft = (pc == 2 || pc == 4 || pc == 7 || pc == 9 || pc == 11) && n - 1 >= lowestNote;
                    bool blackOnRight = (pc == 0 || pc == 2 || pc == 5 || pc == 7 || pc == 9) && n + 1 <= highestNote;
                    if (blackOnLeft) leftEnd = blackFront;
                    if (blackOnRight) rightEnd = blackFront;
                }
                segs.Add(CreateBar(root.transform, "OutlineL", new Vector3(centerX - half + w * 0.5f, oy, zStart + (leftEnd - zStart) * 0.5f), new Vector3(w, thickness, leftEnd - zStart)));
                segs.Add(CreateBar(root.transform, "OutlineR", new Vector3(centerX + half - w * 0.5f, oy, zStart + (rightEnd - zStart) * 0.5f), new Vector3(w, thickness, rightEnd - zStart)));
                segs.Add(CreateBar(root.transform, "OutlineFront", new Vector3(centerX, oy, zStart + w * 0.5f), new Vector3(width, thickness, w)));
                if (black) segs.Add(CreateBar(root.transform, "OutlineBack", new Vector3(centerX, oy, zEnd - w * 0.5f), new Vector3(width, thickness, w)));
            }

            _keys[n] = r;
            _outlines[n] = segs.ToArray();
            _keyCenters[n] = center;
            _states[n] = KeyState.Off;
            Paint(n, 1f);
        }
    }

    Renderer CreateBar(Transform parent, string name, Vector3 localPos, Vector3 localScale)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        DestroyImmediate(go.GetComponent<Collider>()); // keys must never have colliders (grab handle only)
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = keyMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        return r;
    }

    Vector3 FrontCenterLocal(int midiNote)
    {
        // X of the key's center, at the front edge (z = 0) of the white keys.
        Vector3 c = _keyCenters[midiNote];
        return new Vector3(c.x, 0f, 0f);
    }

    // ---------- Animation ----------

    void Update()
    {
        if (_keys.Count == 0) return;
        float pulse = 0.65f + 0.35f * Mathf.Sin(Time.time * pulseSpeed);

        foreach (var kv in _states)
        {
            int n = kv.Key;
            var state = kv.Value;

            if ((state == KeyState.Correct || state == KeyState.Wrong) &&
                _flashUntil.TryGetValue(n, out var until) && Time.time > until)
            {
                _states[n] = KeyState.Off; // flashes revert automatically
                _flashUntil.Remove(n);
                Paint(n, 1f);
                break; // collection modified; continue next frame
            }

            if (state == KeyState.Next) Paint(n, pulse);
        }
    }

    void Paint(int n, float intensity)
    {
        var state = _states[n];
        Color c = state switch
        {
            KeyState.Upcoming => upcomingColor,
            KeyState.Next => nextColor * intensity,
            KeyState.Correct => correctColor,
            KeyState.Wrong => wrongColor,
            _ => !showIdleKeys || outlineKeys ? Color.black // additive display: black = invisible
                 : IsBlack(n) ? idleBlackColor : idleWhiteColor,
        };
        SetColor(_keys[n], c);

        if (_outlines.TryGetValue(n, out var segs))
        {
            Color o = state == KeyState.Off ? (showIdleKeys ? outlineColor : Color.black) : c;
            foreach (var s in segs) SetColor(s, o);
        }
    }

    void SetColor(Renderer r, Color c)
    {
        c.a = 1f;
        r.GetPropertyBlock(_mpb);
        _mpb.SetColor(BaseColorId, c);
        _mpb.SetColor(ColorId, c);
        r.SetPropertyBlock(_mpb);
    }

    // ---------- Persistence (so you don't recalibrate every launch) ----------

    /// <summary>Persist the current pose/scale (called after calibration and after a grab-handle nudge).</summary>
    public void SaveCalibration()
    {
        var p = transform.position; var q = transform.rotation; var s = transform.localScale.x;
        PlayerPrefs.SetString(PrefsKey, string.Join(",", p.x, p.y, p.z, q.x, q.y, q.z, q.w, s));
        PlayerPrefs.Save();
    }

    void LoadCalibration()
    {
        // Note: only meaningful if the app's world origin is stable between launches.
        // On see-through glasses it usually isn't, so treat this as a starting guess and recalibrate.
        var raw = PlayerPrefs.GetString(PrefsKey, "");
        var parts = raw.Split(',');
        if (parts.Length != 8) return;
        var f = new float[8];
        for (int i = 0; i < 8; i++)
            if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out f[i])) return;
        transform.SetPositionAndRotation(new Vector3(f[0], f[1], f[2]), new Quaternion(f[3], f[4], f[5], f[6]));
        transform.localScale = Vector3.one * f[7];
    }

#if UNITY_EDITOR
    // Editor test without a piano: 1 = Next on middle C, 2 = Correct, 3 = Wrong, 0 = clear.
    void LateUpdate()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current; // XR projects use the Input System package
        if (kb == null) return;
        if (kb.digit1Key.wasPressedThisFrame) { ClearAll(); SetKey(60, KeyState.Next); SetKey(64, KeyState.Upcoming); SetKey(67, KeyState.Upcoming); }
        if (kb.digit2Key.wasPressedThisFrame) SetKey(60, KeyState.Correct);
        if (kb.digit3Key.wasPressedThisFrame) SetKey(61, KeyState.Wrong);
        if (kb.digit0Key.wasPressedThisFrame) ClearAll();
    }
#endif
}
