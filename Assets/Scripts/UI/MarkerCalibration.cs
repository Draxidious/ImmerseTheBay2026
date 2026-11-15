using System.Collections.Generic;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Calibrates the keyboard from two printed ArUco 4x4_50 markers on the P71's rear corners (ID 0 behind A0, ID 1
/// behind C8), taking over <see cref="PianoCalibrator"/>'s four pinch steps. Ported from the AuraMarkerTest prototype
/// (see docs/PIANO_CORNERS_HANDOFF.md).
///
/// Flow: PianoCalibrator.Recalibrate() → CalibrationStarted → ask for the scene-understanding permission → enable the
/// ARTrackedImageManager → lock the left marker, then the right one (stable dwell, span gate) → apply the offsets to get
/// the back outer corners of A0 and C8 → PianoCalibrator.CalibrateFromMarkers() (which raises Calibrated as usual).
/// If marker tracking can't run (permission denied, extension unavailable, nothing found in time) the pinch steps
/// come back for that round.
///
/// Offsets are metres in the order (inward, down, toward the player) from each marker centre to that key's back outer
/// corner. The blue dot on the back of the left hand (PianoCalibrator.OffsetsDotPressed) opens a slider panel to
/// fine-tune and save them.
/// Editor keys: M = simulate a marker lock in front of the camera, O = toggle the offsets panel.
/// </summary>
public class MarkerCalibration : MonoBehaviour
{
    const string ScenePermission = "android.permission.SCENE_UNDERSTANDING_COARSE";
    const string OffsetFile = "marker-offsets.json";
    const string LeftCue = "Look at the marker behind the far-left key. Keep looking until the amber dot appears.";
    const string RightCue = "Look at the marker behind the far-right key. Keep looking until the key guides appear.";
    const string RetryCue = "Check that the markers are behind the two end keys.";

    [Header("References (auto-found if empty)")]
    public PianoCalibrator calibrator;
    public ARTrackedImageManager imageManager;

    [Header("Markers")]
    public string leftMarker = "ArUco4x4_50-0";
    public string rightMarker = "ArUco4x4_50-1";
    [Tooltip("Marker centre → back outer corner of A0 (inward, down, toward player), metres. Saved on the Aura 2026-11-15 (Main run).")]
    public Vector3 leftOffset = new Vector3(-0.005f, 0.016f, 0.107f);
    [Tooltip("Marker centre → back outer corner of C8 (inward, down, toward player), metres. Saved on the Aura 2026-11-15 (Main run).")]
    public Vector3 rightOffset = new Vector3(0.015f, 0.016f, 0.090f);
    [Tooltip("Use the real keyboard width; take only the centre and direction from the markers (tracked spans read ~2% long).")]
    public bool useKnownWidth = true;

    [Header("Lock")]
    public int lockSamples = 30;
    public float lockSeconds = 1.2f;
    [Tooltip("All samples in the window must be within this distance of their mean (m).")]
    public float lockTolerance = 0.010f;
    [Tooltip("Accepted distance between the two marker centres (m). The P71 markers are ~1.24 m apart.")]
    public Vector2 spanRange = new Vector2(1.18f, 1.32f);
    [Tooltip("Fall back to the pinch steps if no marker is found within this many seconds of the tracker starting.")]
    public float giveUpSeconds = 45f;

    [Header("Keyboard")]
    [Tooltip("Hide the keyboard overlay while the markers are being scanned (it covered the marker dot and the prompt); it comes " +
             "back when both markers lock, on a pinch fallback, or when calibration ends any other way.")]
    public bool hideKeyboardWhileScanning = true;

    [Header("Offsets panel")]
    public float sliderRangeMm = 60f;

    // State
    readonly Queue<Vector3> _samples = new Queue<Vector3>();
    TrackableId _sampleId;
    float _firstSampleTime;
    Vector3? _left;
    bool _scanning, _hasMarkerPose, _waitingForPermission, _applyingMarkerPose;
    volatile bool _permissionGranted, _permissionDenied;
    float _trackerStartedAt = -1f;
    Vector3 _markerLeft, _markerRight, _axis, _toPlayer;
    Vector3 _defaultLeft, _defaultRight;
    GameObject _leftDot;
    Canvas _panel;
    VoiceOutput _voice;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    [System.Serializable]
    class SavedOffsets { public Vector3 left; public Vector3 right; }

    void Awake()
    {
        if (!calibrator) calibrator = FindAnyObjectByType<PianoCalibrator>();
        if (!imageManager) imageManager = FindAnyObjectByType<ARTrackedImageManager>(FindObjectsInactive.Include);
        _defaultLeft = leftOffset;
        _defaultRight = rightOffset;
        LoadOffsets();
        // The tracker must not start before the permission is granted (the provider can't create it otherwise).
        if (imageManager) imageManager.enabled = false;
    }

    void OnEnable()
    {
        if (!calibrator) return;
        calibrator.CalibrationStarted += BeginScan;
        calibrator.Calibrated += OnCalibrationCompleted;
        calibrator.OffsetsDotPressed += TogglePanel;
        calibrator.markersAvailable = imageManager;
        if (imageManager) imageManager.trackablesChanged.AddListener(OnTrackablesChanged);
    }

    void OnDisable()
    {
        if (calibrator)
        {
            calibrator.CalibrationStarted -= BeginScan;
            calibrator.Calibrated -= OnCalibrationCompleted;
            calibrator.OffsetsDotPressed -= TogglePanel;
            calibrator.markersAvailable = false;
        }
        if (imageManager) imageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
        SetKeyboardVisible(true);
    }

    /// <summary>Show/hide the keyboard overlay (AppFlow turns it on when calibration starts; we hide it during the scan).</summary>
    void SetKeyboardVisible(bool visible)
    {
        var overlay = calibrator ? calibrator.overlay : null;
        if (!overlay || overlay.gameObject.activeSelf == visible) return;
        if (!visible && !hideKeyboardWhileScanning) return;
        overlay.gameObject.SetActive(visible);
        Debug.Log($"[MarkerCalibration] Keyboard {(visible ? "shown" : "hidden while scanning")}.");
    }

    void Start()
    {
        if (!calibrator || !imageManager)
        {
            Debug.LogWarning($"[MarkerCalibration] Disabled: calibrator={(bool)calibrator}, imageManager={(bool)imageManager}.");
            enabled = false;
            return;
        }
        // Calibration may already have started before we subscribed (calibrateOnStart).
        if (calibrator.IsCalibrating && !_scanning) BeginScan();
    }

    // ---------- Scan ----------

    void OnCalibrationCompleted()
    {
        // A four-pinch fit uses different corners. Applying offsets from an older marker fit would move it back.
        if (!_applyingMarkerPose) _hasMarkerPose = false;
    }

    void BeginScan()
    {
        ClosePanel();
        _samples.Clear();
        _left = null;
        _scanning = true;
        _trackerStartedAt = -1f;
        if (_leftDot) _leftDot.SetActive(false);
        calibrator.markersAvailable = true;
        SetKeyboardVisible(false);
        Debug.Log($"[MarkerCalibration] Scan started. Look at {leftMarker} (left), then {rightMarker} (right).");

#if UNITY_ANDROID && !UNITY_EDITOR
        if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(ScenePermission)) { ShowLeftCue(); StartTracker(); return; }
        if (_waitingForPermission) return;
        _waitingForPermission = true;
        _permissionGranted = _permissionDenied = false;
        StartCoroutine(RequestScenePermission());
#else
        ShowLeftCue();
        Debug.Log("[MarkerCalibration] Editor: no marker tracking. Press M to simulate a marker lock (or C for the pinch fake).");
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    IEnumerator RequestScenePermission()
    {
        const string line = "Allow scene access so I can find the piano markers.";
        calibrator.ShowInfo(line, 0f);
        var speech = Speak(line);
        while (speech != null && !speech.IsCompleted && _scanning) yield return null;
        if (!_scanning || !calibrator.IsCalibrating) { _waitingForPermission = false; yield break; }
        var callbacks = new UnityEngine.Android.PermissionCallbacks();
        callbacks.PermissionGranted += _ => _permissionGranted = true;     // may arrive on a Java thread
        callbacks.PermissionDenied += _ => _permissionDenied = true;
        callbacks.PermissionRequestDismissed += _ => _permissionDenied = true;
        UnityEngine.Android.Permission.RequestUserPermission(ScenePermission, callbacks);
        Debug.Log("[MarkerCalibration] Requested scene-understanding permission.");
    }
#endif

    System.Threading.Tasks.Task Speak(string line)
    {
        if (!_voice) _voice = FindAnyObjectByType<AppFlow>()?.voiceOutput;
        return _voice ? _voice.Speak(line) : null;
    }

    void ShowLeftCue(bool retry = false)
    {
        calibrator.ShowInfo(retry ? $"<b>1 of 2</b>\n{RetryCue}\n{LeftCue}" : $"<b>1 of 2</b>\n{LeftCue}", 0f);
        _ = Speak(retry ? RetryCue + " " + LeftCue : LeftCue);
    }

    void StartTracker()
    {
        imageManager.enabled = true;
        _trackerStartedAt = Time.time;
        Debug.Log($"[MarkerCalibration] Image tracker enabled; library={(imageManager.referenceLibrary != null ? imageManager.referenceLibrary.count : 0)} entries.");
    }

    void StopTracker()
    {
        if (imageManager) imageManager.enabled = false;
        _trackerStartedAt = -1f;
    }

    /// <summary>Marker tracking can't help this round: give the four pinch steps back.</summary>
    void FallBackToPinches(string why)
    {
        Debug.LogWarning($"[MarkerCalibration] Falling back to pinch calibration: {why}");
        _scanning = false;
        StopTracker();
        calibrator.markersAvailable = false;
        SetKeyboardVisible(true); // the pinch steps pulse the end keys on the keyboard
        if (calibrator.IsCalibrating) calibrator.ShowInfo($"Can't use the markers ({why}).\nUse the pinch steps instead.", 5f);
    }

    void Update()
    {
        if (_waitingForPermission)
        {
            if (_permissionGranted) { _waitingForPermission = false; Debug.Log("[MarkerCalibration] Permission granted."); if (_scanning && calibrator.IsCalibrating) { ShowLeftCue(); StartTracker(); } }
            else if (_permissionDenied) { _waitingForPermission = false; if (_scanning && calibrator.IsCalibrating) FallBackToPinches("scene permission denied"); }
        }

        if (_scanning)
        {
            if (!calibrator.IsCalibrating) // calibrated some other way (debug key, pinches after a fallback)
            {
                _scanning = false;
                StopTracker();
                SetKeyboardVisible(true);
            }
            else if (_trackerStartedAt > 0f)
            {
                float t = Time.time - _trackerStartedAt;
                if (t > 5f && imageManager.subsystem?.running != true)
                    FallBackToPinches("marker tracking isn't running on this device");
                else if (t > giveUpSeconds && !_left.HasValue)
                    FallBackToPinches($"no marker found in {giveUpSeconds:F0} s");
            }
        }

        DebugKeys();
    }

    void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> change)
    {
        foreach (var image in change.added) Read(image);
        foreach (var image in change.updated) Read(image);
        foreach (var entry in change.removed)
            if (entry.Key == _sampleId) _samples.Clear();
    }

    void Read(ARTrackedImage image)
    {
        if (!_scanning) return;
        string wanted = _left.HasValue ? rightMarker : leftMarker;
        if (image.referenceImage.name != wanted) return;
        if (image.trackingState != TrackingState.Tracking || _sampleId != image.trackableId) _samples.Clear();
        _sampleId = image.trackableId;
        if (image.trackingState != TrackingState.Tracking) return;

        if (_samples.Count == 0) _firstSampleTime = Time.time;
        if (_samples.Count >= lockSamples * 2) _samples.Dequeue();
        _samples.Enqueue(image.transform.position);
        if (_samples.Count < lockSamples || Time.time - _firstSampleTime < lockSeconds) return;

        Vector3 center = Vector3.zero;
        foreach (var p in _samples) center += p;
        center /= _samples.Count;
        float tol2 = lockTolerance * lockTolerance;
        foreach (var p in _samples) if ((p - center).sqrMagnitude > tol2) return;
        _samples.Clear();
        OnMarkerLocked(center);
    }

    void OnMarkerLocked(Vector3 center)
    {
        if (!_left.HasValue)
        {
            _left = center;
            ShowLeftDot(center);
            Debug.Log($"[MarkerCalibration] Left marker locked at {center:F3}.");
            calibrator.ShowInfo($"<b>2 of 2</b>\n{RightCue}", 0f);
            _ = Speak(RightCue);
            return;
        }

        float span = Vector3.Distance(_left.Value, center);
        Debug.Log($"[MarkerCalibration] Right marker locked at {center:F3}; span {span:F3} m.");
        if (span < spanRange.x || span > spanRange.y)
        {
            Debug.LogWarning($"[MarkerCalibration] Rejected span {span:F3} m (expected {spanRange.x:F2}–{spanRange.y:F2}); starting over.");
            _left = null;
            if (_leftDot) _leftDot.SetActive(false);
            ShowLeftCue(retry: true);
            return;
        }

        _markerLeft = _left.Value;
        _markerRight = center;
        _axis = Vector3.ProjectOnPlane(_markerRight - _markerLeft, Vector3.up).normalized;
        _toPlayer = Vector3.Cross(Vector3.up, _axis);
        _scanning = false;
        StopTracker();
        _hasMarkerPose = true;

        Corners(out var low, out var high);
        SetKeyboardVisible(true); // both scans done: the keyboard appears where the markers put it
        _applyingMarkerPose = true;
        string reason;
        try { reason = calibrator.CalibrateFromMarkers(low, high, useKnownWidth); }
        finally { _applyingMarkerPose = false; }
        if (reason != null)
        {
            _hasMarkerPose = false;
            _left = null;
            SetKeyboardVisible(false);
            Debug.LogWarning($"[MarkerCalibration] Fit failed: {reason}");
            if (_leftDot) _leftDot.SetActive(false);
            ShowLeftCue(retry: true);
            _scanning = true;
            StartTracker();
            return;
        }
        if (_leftDot) _leftDot.SetActive(false);
        Debug.Log($"[MarkerCalibration] Calibrated. Offsets left={leftOffset:F4} right={rightOffset:F4}; corners {low:F3} → {high:F3}.");
    }

    void Corners(out Vector3 low, out Vector3 high)
    {
        low = _markerLeft + leftOffset.x * _axis - leftOffset.y * Vector3.up + leftOffset.z * _toPlayer;
        high = _markerRight - rightOffset.x * _axis - rightOffset.y * Vector3.up + rightOffset.z * _toPlayer;
    }

    void Reapply()
    {
        if (!_hasMarkerPose) return;
        Corners(out var low, out var high);
        string reason = calibrator.ApplyMarkerAdjustment(low, high, useKnownWidth);
        if (reason != null) Debug.LogWarning($"[MarkerCalibration] Adjustment rejected: {reason}");
    }

    void ShowLeftDot(Vector3 center)
    {
        if (!_leftDot)
        {
            _leftDot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _leftDot.name = "MarkerLeftLocked";
            Destroy(_leftDot.GetComponent<Collider>());
            var r = _leftDot.GetComponent<Renderer>();
            r.sharedMaterial = calibrator.markerMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor(BaseColorId, calibrator.lowMarkerColor);
            mpb.SetColor(ColorId, calibrator.lowMarkerColor);
            r.SetPropertyBlock(mpb);
            _leftDot.transform.localScale = Vector3.one * 0.008f;
        }
        _leftDot.transform.position = center; // exactly the tracked centre, so tracking error is visible
        _leftDot.SetActive(true);
    }

    // ---------- Offsets (save/load) ----------

    string OffsetPath => System.IO.Path.Combine(Application.persistentDataPath, OffsetFile);

    void LoadOffsets()
    {
        try
        {
            if (!System.IO.File.Exists(OffsetPath)) return;
            var saved = JsonUtility.FromJson<SavedOffsets>(System.IO.File.ReadAllText(OffsetPath));
            if (saved == null) return;
            leftOffset = saved.left;
            rightOffset = saved.right;
            Debug.Log($"[MarkerCalibration] Loaded offsets left={leftOffset:F4} right={rightOffset:F4}.");
        }
        catch (System.Exception e) { Debug.LogWarning($"[MarkerCalibration] Could not read offsets: {e.Message}"); }
    }

    void SaveOffsets()
    {
        try
        {
            System.IO.File.WriteAllText(OffsetPath, JsonUtility.ToJson(new SavedOffsets { left = leftOffset, right = rightOffset }, true));
            Debug.Log($"[MarkerCalibration] Saved offsets left={leftOffset:F4} right={rightOffset:F4} to {OffsetPath}.");
            calibrator.ShowInfo("Offsets saved.", 3f);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[MarkerCalibration] Save failed: {e.Message}");
            calibrator.ShowInfo("Couldn't save the offsets.", 3f);
        }
    }

    // ---------- Offsets panel (standard world-space UGUI, driven by the rig's XRI interactors) ----------

    internal bool CanFineTune => isActiveAndEnabled && _hasMarkerPose && calibrator && !calibrator.IsCalibrating;

    internal void OpenFineTune()
    {
        if (CanFineTune && !_panel) OpenPanel();
    }

    internal void CloseFineTune()
    {
        if (_panel) ClosePanel();
    }

    void TogglePanel()
    {
        if (_panel) { ClosePanel(); return; }
        if (!_hasMarkerPose)
        {
            calibrator.ShowInfo("Calibrate with the markers first\n(touch the ORANGE dot).", 4f);
            return;
        }
        OpenPanel();
    }

    void ClosePanel()
    {
        if (_panel)
        {
            _panel.gameObject.SetActive(false);
            Destroy(_panel.gameObject);
        }
        _panel = null;
    }

    void OpenPanel()
    {
        var head = Camera.main ? Camera.main.transform : null;
        if (!head) return;
        Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        fwd = fwd.sqrMagnitude < 1e-4f ? Vector3.forward : fwd.normalized;

        var go = new GameObject("MarkerOffsetsPanel", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(TrackedDeviceGraphicRaycaster));
        _panel = go.GetComponent<Canvas>();
        _panel.renderMode = RenderMode.WorldSpace;
        _panel.worldCamera = Camera.main;
        var rect = (RectTransform)go.transform;
        rect.sizeDelta = new Vector2(620f, 300f);
        rect.localScale = Vector3.one * 0.001f; // 1 canvas unit = 1 mm
        Vector3 viewDir = Quaternion.AngleAxis(20f, Vector3.Cross(Vector3.up, fwd)) * fwd; // look slightly down at it
        rect.SetPositionAndRotation(head.position + fwd * 0.45f + Vector3.down * 0.12f, Quaternion.LookRotation(viewDir, Vector3.up));

        // Slider right / ">" = that part of the line moves toward the player / up / right. ±sliderRangeMm, arrows 1 mm.
        AddRow(0, "Left depth", () => leftOffset.z, v => leftOffset.z = v);
        AddRow(1, "Right depth", () => rightOffset.z, v => rightOffset.z = v);
        AddRow(2, "Height", () => -leftOffset.y, v => leftOffset.y = rightOffset.y = -v);
        // With the known width the span is fixed, so the ends move together: one sideways control.
        float sideCenterL = leftOffset.x, sideCenterR = rightOffset.x;
        AddRow(3, "Sideways", () => leftOffset.x - sideCenterL, v => { leftOffset.x = sideCenterL + v; rightOffset.x = sideCenterR - v; });

        MakeButton("Save", new Vector2(-170f, -115f), new Vector2(130f, 40f), new Color(0.1f, 0.8f, 0.3f), SaveOffsets);
        MakeButton("Defaults", new Vector2(0f, -115f), new Vector2(130f, 40f), new Color(0.1f, 0.5f, 0.8f), () =>
        {
            leftOffset = _defaultLeft;
            rightOffset = _defaultRight;
            Reapply();
            Debug.Log("[MarkerCalibration] Offsets reset to defaults.");
            ClosePanel();
            OpenPanel();
        });
        MakeButton("Close", new Vector2(170f, -115f), new Vector2(130f, 40f), new Color(1f, 0.45f, 0.1f), ClosePanel);
        Debug.Log("[MarkerCalibration] Offsets panel opened.");
    }

    void AddRow(int index, string label, System.Func<float> get, System.Action<float> set)
    {
        float y = 115f - index * 50f;
        float center = get();
        MakeText(label, new Vector2(-235f, y), new Vector2(130f, 40f), TextAlignmentOptions.Right, Color.white);
        var value = MakeText("0.0 mm", new Vector2(250f, y), new Vector2(110f, 40f), TextAlignmentOptions.Left, new Color(0.4f, 0.9f, 1f));

        var slider = DefaultControls.CreateSlider(new DefaultControls.Resources()).GetComponent<Slider>();
        var sliderRect = (RectTransform)slider.transform;
        sliderRect.SetParent(_panel.transform, false);
        sliderRect.anchoredPosition = new Vector2(10f, y);
        sliderRect.sizeDelta = new Vector2(240f, 26f);
        slider.minValue = -sliderRangeMm;
        slider.maxValue = sliderRangeMm;
        slider.SetValueWithoutNotify(0f);
        foreach (var image in slider.GetComponentsInChildren<Image>())
            image.color = image.transform == slider.handleRect ? new Color(1f, 0.55f, 0.1f)
                : slider.fillRect && image.transform == slider.fillRect ? new Color(0.1f, 0.6f, 0.9f)
                : new Color(0.15f, 0.3f, 0.4f);
        slider.handleRect.sizeDelta = new Vector2(26f, 0f);
        slider.onValueChanged.AddListener(mm =>
        {
            set(center + mm / 1000f);
            value.text = $"{mm:+0.0;-0.0;0.0} mm";
            Reapply();
        });
        MakeButton("<", new Vector2(-145f, y), new Vector2(38f, 38f), new Color(0.15f, 0.4f, 0.55f), () => Step(slider, label, -1f));
        MakeButton(">", new Vector2(165f, y), new Vector2(38f, 38f), new Color(0.15f, 0.4f, 0.55f), () => Step(slider, label, 1f));
    }

    void Step(Slider slider, string label, float mm)
    {
        slider.value = Mathf.Round(slider.value + mm);
        Debug.Log($"[MarkerCalibration] {label} {slider.value:+0;-0;0} mm; left={leftOffset:F4} right={rightOffset:F4}");
    }

    TextMeshProUGUI MakeText(string text, Vector2 position, Vector2 size, TextAlignmentOptions align, Color color)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        var rect = (RectTransform)go.transform;
        rect.SetParent(_panel.transform, false);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        var label = go.GetComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = 24;
        label.alignment = align;
        label.color = color;
        label.raycastTarget = false;
        return label;
    }

    void MakeButton(string text, Vector2 position, Vector2 size, Color color, UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject($"{text} button", typeof(RectTransform), typeof(Image), typeof(Button));
        var rect = (RectTransform)go.transform;
        rect.SetParent(_panel.transform, false);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        go.GetComponent<Image>().color = color;
        go.GetComponent<Button>().onClick.AddListener(onClick);
        var label = MakeText(text, Vector2.zero, size, TextAlignmentOptions.Center, Color.white);
        label.rectTransform.SetParent(rect, false);
        label.rectTransform.anchoredPosition = Vector2.zero;
    }

    // ---------- Editor debug ----------

    void DebugKeys()
    {
#if UNITY_EDITOR
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return;
        if (kb.oKey.wasPressedThisFrame) TogglePanel();
        if (kb.mKey.wasPressedThisFrame && calibrator.IsCalibrating)
        {
            // Fake markers 1.24 m apart, ~0.6 m ahead and 0.3 m below the camera, as on the real P71.
            var cam = Camera.main ? Camera.main.transform : null;
            if (!cam) return;
            Vector3 f = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized, r = Vector3.Cross(Vector3.up, f);
            Vector3 c = cam.position + f * 0.62f + Vector3.down * 0.28f;
            _scanning = true;
            _left = null;
            OnMarkerLocked(c - r * 0.62f);
            OnMarkerLocked(c + r * 0.62f);
        }
#endif
    }
}
