using UnityEngine;
using UnityEngine.InputSystem;

// A bright arc spinning around the coach orb while the AI is thinking (CoachOrb.State == Thinking). Quick replies don't
// show it: it fades in only after showAfterSeconds. Reads the orb's state and doesn't change CoachOrb (Nehemiah's).
// Hidden with everything else in non-visual mode (VisualsGate culls the camera). Adds itself to every CoachOrb.
// Editor: F12 keeps it spinning to check the look.
public class ThinkingSpinner : MonoBehaviour
{
    [Tooltip("Ring radius in orb diameters (the orb is 1.0 across).")]
    public float radius = 0.8f;
    [Tooltip("Arc length in degrees.")]
    public float arcDegrees = 100f;
    [Tooltip("Turns per second.")]
    public float turnsPerSecond = 1.2f;
    public float lineWidth = 0.003f;
    public Color color = new Color(0.6f, 0.85f, 1f);
    [Tooltip("Thinking shorter than this shows no ring.")]
    public float showAfterSeconds = 0.3f;
    public float fadeSeconds = 0.2f;

    const int Segments = 24;
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    CoachOrb orb;
    Transform ring;
    LineRenderer line;
    Material material;
    float thinkingSince = -1f, alpha, angle;
    bool forced, shown;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AddToOrbs()
    {
        foreach (var o in FindObjectsByType<CoachOrb>(FindObjectsInactive.Include))
            if (!o.GetComponent<ThinkingSpinner>()) o.gameObject.AddComponent<ThinkingSpinner>();
    }

    void Awake() => orb = GetComponent<CoachOrb>();

    void OnDestroy()
    {
        if (material) Destroy(material);
    }

    // Built lazily: CoachOrb creates its orb renderer in its own Awake.
    bool Build()
    {
        if (ring) return true;
        if (!orb || !orb.orbRenderer) return false;
        // Under the orb renderer: follows its bob and size, and the orb already turns to face the player.
        ring = new GameObject("ThinkingRing").transform;
        ring.SetParent(orb.orbRenderer.transform, false);
        line = ring.gameObject.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.positionCount = Segments + 1;
        float step = arcDegrees * Mathf.Deg2Rad / Segments;
        for (int i = 0; i <= Segments; i++)
            line.SetPosition(i, new Vector3(Mathf.Cos(i * step), Mathf.Sin(i * step), 0f) * radius);
        line.widthMultiplier = lineWidth;
        line.numCapVertices = 2;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        material = new Material(shader) { name = "ThinkingRing (runtime)" };
        line.sharedMaterial = material;
        line.enabled = false;
        return true;
    }

    void Update()
    {
#if UNITY_EDITOR
        if (Keyboard.current?.f12Key.wasPressedThisFrame == true) forced = !forced;
#endif
        if (!Build()) return;
        bool thinking = orb.State == OrbState.Thinking;
        if (!thinking) thinkingSince = -1f;
        else if (thinkingSince < 0f) thinkingSince = Time.unscaledTime;
        bool show = forced || (thinking && Time.unscaledTime - thinkingSince >= showAfterSeconds);

        if (show != shown)
        {
            shown = show;
            Debug.Log(show ? $"[Thinking] Ring on ({(forced ? "F12" : $"thinking {showAfterSeconds:0.0} s")})."
                           : "[Thinking] Ring off.");
        }
        alpha = Mathf.MoveTowards(alpha, show ? 1f : 0f, Time.unscaledDeltaTime / Mathf.Max(0.01f, fadeSeconds));
        line.enabled = alpha > 0f;
        if (!line.enabled) return;

        // Additive display: fade by brightness (black is invisible in the lenses).
        var c = color * alpha;
        c.a = 1f;
        if (material.HasProperty(BaseColorId)) material.SetColor(BaseColorId, c);
        if (material.HasProperty(ColorId)) material.SetColor(ColorId, c);
        angle = (angle - 360f * turnsPerSecond * Time.unscaledDeltaTime) % 360f; // clockwise as the player sees it
        ring.localRotation = Quaternion.Euler(0f, 0f, angle);
    }
}
