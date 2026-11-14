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

    [Tooltip("Fraction of each key's front left undrawn so the overlay doesn't sit on the player's fingers.")]
    [Range(0f, 0.8f)] public float frontClearFraction = 0.35f;

    [Header("Look")]
    public Material keyMaterial; // e.g. URP/Unlit (uses _BaseColor). Leave empty to auto-create.
    public Color upcomingColor = new Color(0.15f, 0.35f, 0.6f);
    public Color nextColor = new Color(0.3f, 0.85f, 1f);
    public Color correctColor = new Color(0.3f, 1f, 0.45f);
    public Color wrongColor = new Color(1f, 0.25f, 0.25f);
    public float flashSeconds = 0.35f;
    public float pulseSpeed = 4f;

    static readonly bool[] IsBlackPc = { false, true, false, true, false, false, true, false, true, false, true, false }; // C..B
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    readonly Dictionary<int, Renderer> _keys = new Dictionary<int, Renderer>();
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
        right.y = 0f; // keep the overlay level
        float worldSpan = right.magnitude;
        if (worldSpan < 0.05f || modelSpan < 0.001f) return; // ignore accidental double-pinch

        right /= worldSpan;
        Vector3 forward = Vector3.Cross(right, Vector3.up); // toward the fallboard
        Quaternion rot = Quaternion.LookRotation(forward, Vector3.up);
        float s = worldSpan / modelSpan;

        transform.rotation = rot;
        transform.localScale = Vector3.one * s;
        Vector3 avgFront = (worldLowFront + worldHighFront) * 0.5f;
        Vector3 localMid = (localLow + localHigh) * 0.5f;
        transform.position = avgFront - rot * (localMid * s);

        SaveCalibration();
    }

    // ---------- Build ----------

    public void Build()
    {
        foreach (var r in _keys.Values) if (r) Destroy(r.gameObject);
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
                // Sits on the boundary between the previous and next white keys (simplified, centered).
                width = blackKeyWidth;
                length = blackKeyLength;
                centerX = whiteIndex * whiteKeyWidth;
                zStart = whiteKeyLength - blackKeyLength;
                y = hoverHeight + 0.008f; // black keys are taller
            }

            // Leave the front part clear for the player's fingers.
            float drawStart = zStart + length * frontClearFraction;
            float drawLength = length - (drawStart - zStart);
            var center = new Vector3(centerX, y, drawStart + drawLength * 0.5f);

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"Key_{n}{(black ? "_b" : "")}";
            DestroyImmediate(go.GetComponent<Collider>()); // keys must never have colliders (grab handle only)
            go.transform.SetParent(transform, false);
            go.transform.localPosition = center;
            go.transform.localScale = new Vector3(width, thickness, drawLength);

            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = keyMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;

            _keys[n] = r;
            _keyCenters[n] = center;
            _states[n] = KeyState.Off;
            Paint(n, 1f);
        }
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
        Color c = _states[n] switch
        {
            KeyState.Upcoming => upcomingColor,
            KeyState.Next => nextColor * intensity,
            KeyState.Correct => correctColor,
            KeyState.Wrong => wrongColor,
            _ => Color.black, // additive display: black = invisible
        };
        c.a = 1f;
        var r = _keys[n];
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
