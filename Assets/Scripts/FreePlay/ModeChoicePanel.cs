using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// World-space mode panel, built at runtime: "Lesson" / "Free Play" poke buttons (choice view) or one small "Switch mode"
// button (compact view) that reopens the choice. Sits above the sheet panel, never over the keyboard, so hands resting
// on the keys can't press it. Buttons are dead while calibrating and for a moment after the panel appears.
// Additive display: bright rims and text only, no fills (black is invisible in the lenses but shows in recordings).
public class ModeChoicePanel : MonoBehaviour
{
    [Header("Found automatically when empty")]
    public SongNoteDisplay sheet;
    public PianoCalibrator calibrator;

    [Header("Placement")]
    [Tooltip("Panel center above the sheet panel's top edge, in the sheet's own up direction (m).")]
    public float aboveSheetTop = 0.08f;
    [Tooltip("Without a sheet: distance in front of the head at eye height (m).")]
    public float headDistance = 0.6f;

    [Header("Look")]
    public Color rimColor = new Color(0.3f, 0.9f, 1f);
    public Color hoverColor = Color.white;
    public Color disabledColor = new Color(0.25f, 0.4f, 0.45f);
    public Color textColor = Color.white;
    public float rimWidth = 0.003f;

    [Header("Input")]
    [Tooltip("Pokes are ignored this long after the panel appears.")]
    public float debounceSeconds = 0.5f;

    // Raised when a mode button is pressed (AppModeSwitch.Request is called too).
    public event Action<AppMode> Chosen;

    enum View { Hidden, Choice, Switch }

    class Button
    {
        public GameObject go;
        public XRSimpleInteractable interactable;
        public LineRenderer rim;
        public TextMeshPro label;
        public Material material;
        public bool hovered;
        public Action onPress;
    }

    View _view = View.Hidden;
    Transform _root, _choice, _switch;
    readonly List<Button> _buttons = new List<Button>();
    float _shownAt;
    bool _placedAtHead;
    bool _usesRadialControls;

    // ------------------------------------------------------------------ API

    public void ShowChoice()
    {
        if (_usesRadialControls) FindAnyObjectByType<RadialMenu>()?.Open();
        else Show(View.Choice);
    }

    public void ShowSwitchButton()
    {
        if (_usesRadialControls) FindAnyObjectByType<RadialMenu>()?.Close();
        else Show(View.Switch);
    }

    public void Hide()
    {
        _view = View.Hidden;
        if (_root) _root.gameObject.SetActive(false);
    }

    public bool IsShowingChoice => _view == View.Choice;

    // ------------------------------------------------------------------ Build

    void Awake()
    {
        // Main_2 supplies the in-game controls through its radial menu.
        _usesRadialControls = FindAnyObjectByType<FingeringGuide>() != null;
        if (_usesRadialControls) return;
        if (!sheet) sheet = FindAnyObjectByType<SongNoteDisplay>(FindObjectsInactive.Include);
        if (!calibrator) calibrator = FindAnyObjectByType<PianoCalibrator>(FindObjectsInactive.Include);
        Build();
        Hide();
    }

    void OnDestroy()
    {
        foreach (var b in _buttons) if (b.material) Destroy(b.material);
    }

    void Build()
    {
        _root = new GameObject("ModeChoicePanel").transform;
        _root.SetParent(transform, false);

        _choice = new GameObject("Choice").transform;
        _choice.SetParent(_root, false);
        Label(_choice, "Title", "How do you want to play?", 0.16f, new Vector3(0f, 0.065f, 0f), new Vector2(0.4f, 0.04f));
        AddButton(_choice, "Lesson", "Lesson", new Vector3(-0.1f, 0f, 0f), new Vector2(0.16f, 0.07f), 0.26f,
                  () => Choose(AppMode.Lesson));
        AddButton(_choice, "FreePlay", "Free Play", new Vector3(0.1f, 0f, 0f), new Vector2(0.16f, 0.07f), 0.26f,
                  () => Choose(AppMode.FreePlay));

        _switch = new GameObject("Switch").transform;
        _switch.SetParent(_root, false);
        AddButton(_switch, "SwitchMode", "Switch mode", Vector3.zero, new Vector2(0.12f, 0.04f), 0.16f, ShowChoice);
    }

    void AddButton(Transform parent, string name, string text, Vector3 position, Vector2 size, float fontSize, Action onPress)
    {
        var go = new GameObject(name + "Button");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;

        // Collider first: the interactable collects its colliders on Awake. Local +Z points away from the player,
        // so a poke from the player's side moves along +Z (XRPokeFilter's default axis).
        var box = go.AddComponent<BoxCollider>();
        box.size = new Vector3(size.x, size.y, 0.02f);
        var interactable = go.AddComponent<XRSimpleInteractable>();
        var filter = go.AddComponent<XRPokeFilter>();
        filter.pokeInteractable = interactable;
        filter.pokeCollider = box;

        var rim = go.AddComponent<LineRenderer>();
        rim.useWorldSpace = false;
        rim.loop = true;
        rim.positionCount = 4;
        float hx = size.x * 0.5f, hy = size.y * 0.5f;
        rim.SetPositions(new[] { new Vector3(-hx, -hy, 0f), new Vector3(-hx, hy, 0f), new Vector3(hx, hy, 0f), new Vector3(hx, -hy, 0f) });
        rim.widthMultiplier = rimWidth;
        rim.numCornerVertices = 2;
        rim.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rim.receiveShadows = false;
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        var mat = new Material(shader) { name = "ModeButtonRim (runtime)" };
        rim.sharedMaterial = mat;

        var b = new Button
        {
            go = go, interactable = interactable, rim = rim, material = mat, onPress = onPress,
            label = Label(go.transform, "Label", text, fontSize, new Vector3(0f, 0f, -0.002f), size)
        };
        interactable.selectEntered.AddListener(_ => Press(b));
        interactable.hoverEntered.AddListener(_ => b.hovered = true);
        interactable.hoverExited.AddListener(_ => b.hovered = false);
        _buttons.Add(b);
        SetColor(b, rimColor);
    }

    TextMeshPro Label(Transform parent, string name, string text, float fontSize, Vector3 position, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = textColor; // additive display: bright = visible
        tmp.rectTransform.sizeDelta = size;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode = TextOverflowModes.Overflow;
        return tmp;
    }

    static void SetColor(Button b, Color c)
    {
        if (b.material.HasProperty("_BaseColor")) b.material.SetColor("_BaseColor", c);
        if (b.material.HasProperty("_Color")) b.material.SetColor("_Color", c);
    }

    // ------------------------------------------------------------------ Show / press

    void Show(View view)
    {
        _view = view;
        _shownAt = Time.time;
        _placedAtHead = false;
        _root.gameObject.SetActive(true);
        _choice.gameObject.SetActive(view == View.Choice);
        _switch.gameObject.SetActive(view == View.Switch);
        foreach (var b in _buttons) b.hovered = false;
        Place();
        UpdateButtons();
    }

    bool Usable => _view != View.Hidden && Time.time >= _shownAt + debounceSeconds && !(calibrator && calibrator.IsCalibrating);

    void Press(Button b)
    {
        if (!Usable || !b.go.activeInHierarchy) return;
        b.onPress?.Invoke();
    }

    void Choose(AppMode mode)
    {
        Debug.Log($"[FreePlay] Panel: {mode} chosen.");
        Chosen?.Invoke(mode);
        AppModeSwitch.Request(mode, "panel");
    }

    // ------------------------------------------------------------------ Per frame

    void Update()
    {
        if (_view == View.Hidden) return;
        Place();
        UpdateButtons();
    }

    void UpdateButtons()
    {
        bool usable = Usable;
        foreach (var b in _buttons)
        {
            if (!b.go.activeInHierarchy) continue;
            if (b.interactable.enabled != usable) b.interactable.enabled = usable;
            if (!usable) b.hovered = false;
            SetColor(b, !usable ? disabledColor : b.hovered ? hoverColor : rimColor);
        }
    }

    // Above the sheet in its own rotation (follows recalibration); without a sheet, once in front of the head.
    void Place()
    {
        if (sheet)
        {
            var t = sheet.transform;
            Vector3 up = t.rotation * Vector3.up;
            Vector3 top;
            var canvas = sheet.GetComponentInChildren<Canvas>(true);
            if (canvas)
            {
                var corners = new Vector3[4];
                ((RectTransform)canvas.transform).GetWorldCorners(corners);
                top = (corners[1] + corners[2]) * 0.5f;
            }
            else top = t.position + up * (sheet.panelSize.y * sheet.metersPerUnit * 0.5f * t.lossyScale.y);
            _root.SetPositionAndRotation(top + up * aboveSheetTop, t.rotation);
            return;
        }

        if (_placedAtHead) return;
        var cam = Camera.main ? Camera.main.transform : null;
        if (!cam || !HeadIsTracked()) return;
        Vector3 forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
        forward.Normalize();
        _root.SetPositionAndRotation(cam.position + forward * headDistance, Quaternion.LookRotation(forward, Vector3.up));
        _placedAtHead = true;
    }

    // True once the runtime reports a tracked head pose (always true without an XR device, e.g. Editor play mode).
    static bool HeadIsTracked()
    {
        if (!UnityEngine.XR.XRSettings.isDeviceActive) return true;
        var head = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
        return head.isValid && head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool tracked) && tracked;
    }
}
