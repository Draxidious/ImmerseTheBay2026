using System;
using System.Collections.Generic;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Two-pinch calibration for <see cref="PianoOverlay"/> using XR Hands.
/// Pinch 1 = front-edge center of the LOWEST key, pinch 2 = front-edge center of the HIGHEST key,
/// then calls <see cref="PianoOverlay.CalibrateFromTwoKeys"/>. Shows a small marker at each pinch.
///
/// Also owns the overlay's grab handle (disabled while calibrating, saves the pose on release)
/// and an optional "Recalibrate" button interactable.
/// </summary>
public class PianoCalibrator : MonoBehaviour
{
    public enum Phase { Idle, WaitingForLow, WaitingForHigh }

    [Header("References")]
    public PianoOverlay overlay;
    [Tooltip("Hand joint poses are in XR Origin space. Auto-found if empty.")]
    public XROrigin xrOrigin;
    [Tooltip("Grab interactable on the overlay root (collider = the grab handle only). Optional.")]
    public XRGrabInteractable overlayGrab;
    [Tooltip("Any XRI interactable (poke/pinch) that restarts calibration. Optional.")]
    public XRBaseInteractable recalibrateButton;
    [Tooltip("Floating instruction text. Optional.")]
    public TMP_Text promptLabel;

    [Header("Pinch detection (meters between thumb tip and index tip)")]
    public float pinchStartDistance = 0.02f;
    public float pinchEndDistance = 0.04f;
    [Tooltip("The thumb/index midpoint sits above the key surface when pinching a key edge; lower both points by this much (m).")]
    public float pinchHeightOffset = 0.008f;
    [Tooltip("Reject a calibration whose two points are closer/farther than this fraction of the expected keyboard width.")]
    public float minSpanFraction = 0.6f;
    public float maxSpanFraction = 1.6f;
    [Tooltip("Ignore pinches for this long after (re)starting, so the pinch that pressed Recalibrate doesn't count.")]
    public float startCooldownSeconds = 0.6f;
    [Tooltip("Only count pinches with the palm facing down (how you pinch a key edge). A palm-toward-you pinch is the " +
             "Android XR system-menu gesture and must be left to the OS.")]
    public bool requirePalmDown = true;
    [Range(0f, 1f)] public float palmDownThreshold = 0.35f; // dot(back-of-hand, world up)

    [Header("Markers")]
    public Material markerMaterial; // URP/Unlit. Leave empty to auto-create.
    public float markerSize = 0.012f;
    public Color lowMarkerColor = new Color(1f, 0.85f, 0.2f);
    public Color highMarkerColor = new Color(1f, 0.4f, 1f);
    [Tooltip("Hide markers this many seconds after calibration finishes (0 = keep).")]
    public float hideMarkersAfter = 4f;

    [Header("Behaviour")]
    public bool calibrateOnStart = true;
    [Tooltip("Distance in front of the head where the prompt appears.")]
    public float promptDistance = 0.6f;
    [Tooltip("While calibrating, the prompt stays put and only re-centers after it has been out of view this long.")]
    public float promptRecenterAfterSeconds = 0.6f;
    [Tooltip("Angle (degrees) from the gaze direction beyond which the prompt counts as out of view.")]
    public float promptMaxAngle = 30f;
    [Tooltip("Head tracking must be valid this long before anything is placed (poses settle after the session starts).")]
    public float trackingSettleSeconds = 0.75f;
    [Tooltip("Before the first calibration, move the overlay in front of the user once head tracking is valid " +
             "(the XR session can start long after Start(), so a fixed scene position can end up anywhere).")]
    public bool placeOverlayInFrontWhenTracked = true;
    public Vector3 initialOverlayOffset = new Vector3(0f, -0.35f, 0.45f); // head-relative: (right, up, forward)

    [Header("Key preview (so the overlay is visible on an additive display)")]
    [Tooltip("Pulse the lowest/highest key while calibrating, and light every C for a few seconds afterwards.")]
    public bool previewKeys = true;
    public float previewSecondsAfterCalibration = 5f;

    /// <summary>Raised after a successful two-point calibration.</summary>
    public event Action Calibrated;

    public Phase CurrentPhase => _phase;
    public bool IsCalibrating => _phase != Phase.Idle;

    static readonly List<XRHandSubsystem> s_Subsystems = new List<XRHandSubsystem>();
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    XRHandSubsystem _hands;
    Phase _phase = Phase.Idle;
    Vector3 _lowPoint;
    float _ignoreUntil;
    float _hideMarkersAt = -1f;
    bool _leftPinching, _rightPinching;
    GameObject _lowMarker, _highMarker;
    bool _overlayPlaced;     // initial head-relative placement done (or calibrated)
    bool _promptSnapped;     // prompt has been placed at least once with a tracked head
    float _clearPreviewAt = -1f;
    float _messageUntil = -1f;       // a temporary message (error / done) is showing until then
    float _trackedSince = -1f;       // when the head pose became valid
    float _promptOutOfViewSince = -1f;
    Vector3 _promptGlideTarget; Quaternion _promptGlideRot; bool _promptGliding;
    readonly List<UnityEngine.XR.XRInputSubsystem> _inputSubsystems = new List<UnityEngine.XR.XRInputSubsystem>();
    UnityEngine.XR.XRInputSubsystem _inputSubsystem;

    void Awake()
    {
        if (!overlay) overlay = FindAnyObjectByType<PianoOverlay>();
        if (!xrOrigin) xrOrigin = FindAnyObjectByType<XROrigin>();
        if (!markerMaterial)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            markerMaterial = new Material(shader);
        }
        _lowMarker = CreateMarker("CalibMarker_Low", lowMarkerColor);
        _highMarker = CreateMarker("CalibMarker_High", highMarkerColor);
    }

    void OnEnable()
    {
        if (recalibrateButton) recalibrateButton.selectEntered.AddListener(OnRecalibratePressed);
        if (overlayGrab) overlayGrab.selectExited.AddListener(OnOverlayReleased);
    }

    void OnDisable()
    {
        if (recalibrateButton) recalibrateButton.selectEntered.RemoveListener(OnRecalibratePressed);
        if (overlayGrab) overlayGrab.selectExited.RemoveListener(OnOverlayReleased);
        if (_inputSubsystem != null) _inputSubsystem.trackingOriginUpdated -= OnTrackingOriginUpdated;
        _inputSubsystem = null;
    }

    // The runtime can re-center / switch reference space after start (logcat: ReferenceSpaceChangePending).
    // Head-relative content placed before that ends up offset, so re-place it instead of letting it glide.
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

    void OnTrackingOriginUpdated(UnityEngine.XR.XRInputSubsystem _)
    {
        _trackedSince = -1f;      // wait for the new pose to settle
        _promptSnapped = false;   // then snap the prompt (no glide)
        _promptGliding = false;
        if (IsCalibrating) _overlayPlaced = false; // still uncalibrated: re-place in front of the user
    }

    void Start()
    {
        if (calibrateOnStart) Recalibrate();
        else SetPhase(Phase.Idle);
    }

    // ---------- Public API ----------

    /// <summary>Forget the current points and wait for two new pinches.</summary>
    public void Recalibrate()
    {
        _ignoreUntil = Time.time + startCooldownSeconds;
        _hideMarkersAt = -1f;
        _lowMarker.SetActive(false);
        _highMarker.SetActive(false);
        _promptSnapped = false; // snap (not lerp) to the head on the next tracked frame
        SetPhase(Phase.WaitingForLow);
        ShowCalibrationPreview();
    }

    /// <summary>Feed a pinch point manually (world space). Used by hand tracking and the Editor debug keys.</summary>
    public void SubmitPoint(Vector3 worldPoint)
    {
        switch (_phase)
        {
            case Phase.WaitingForLow:
                _lowPoint = worldPoint;
                ShowMarker(_lowMarker, worldPoint);
                SetPhase(Phase.WaitingForHigh);
                break;

            case Phase.WaitingForHigh:
            {
                if (Vector3.Distance(worldPoint, _lowPoint) < 0.05f) return; // same spot twice (pinch bounce): ignore
                Vector3 low = _lowPoint, high = worldPoint;

                // If the user did right-then-left, swap instead of flipping the keyboard around.
                var head = HeadTransform();
                if (head && Vector3.Dot(high - low, head.right) < 0f) { var t = low; low = high; high = t; }

                float span = Vector3.Distance(low, high), expected = ExpectedSpanMeters();
                if (span < expected * minSpanFraction || span > expected * maxSpanFraction)
                {
                    Debug.Log($"[PianoCalibrator] Rejected: points {span * 100f:F0} cm apart, expected ~{expected * 100f:F0} cm.");
                    Recalibrate();
                    ShowMessage($"Those two pinches were {span * 100f:F0} cm apart, but the ends of the\n" +
                                $"keyboard are about {expected * 100f:F0} cm apart. Let's try again.\n\n" + PromptFor(Phase.WaitingForLow),
                                6f);
                    return;
                }

                ShowMarker(_lowMarker, low);
                ShowMarker(_highMarker, high);
                Vector3 drop = Vector3.down * pinchHeightOffset; // pinch midpoint → key surface
                if (overlay) overlay.CalibrateFromTwoKeys(low + drop, high + drop);
                Debug.Log($"[PianoCalibrator] Calibrated: low {low}, high {high}, span {span * 100f:F0} cm (expected ~{expected * 100f:F0}), scale {(overlay ? overlay.transform.localScale.x : 0f):F3}.");
                _overlayPlaced = true;
                if (hideMarkersAfter > 0f) _hideMarkersAt = Time.time + hideMarkersAfter;
                SetPhase(Phase.Idle);
                ShowAlignmentPreview();
                ShowMessage("<b>Calibrated!</b>\nThe blue bars should sit on every C key.\n" +
                            "Pinch and drag the blue ball to nudge. Tap the orange tile to redo.", previewSecondsAfterCalibration);
                Calibrated?.Invoke();
                break;
            }
        }
    }

    /// <summary>Distance between the front-centers of the lowest and highest key at real size (≈1.2 m for 88 keys).</summary>
    public float ExpectedSpanMeters()
    {
        if (!overlay) return 1.2f;
        int whites = 0;
        for (int n = overlay.lowestNote; n <= overlay.highestNote; n++) if (!PianoOverlay.IsBlack(n)) whites++;
        return Mathf.Max(0.1f, (whites - 1) * overlay.whiteKeyWidth);
    }

    static string PromptFor(Phase p) => p switch
    {
        Phase.WaitingForLow => "<b>Step 1 of 2</b>\nPinch thumb + index finger on the FRONT EDGE of the\n<b>far-LEFT key</b> (the lowest note). Keep your palm facing down.",
        Phase.WaitingForHigh => "<b>Step 2 of 2</b>\nNow pinch the FRONT EDGE of the\n<b>far-RIGHT key</b> (the highest note), about 1.2 m to the right.",
        _ => "",
    };

    void ShowMessage(string text, float seconds)
    {
        if (!promptLabel) return;
        promptLabel.text = text;
        promptLabel.gameObject.SetActive(true);
        _messageUntil = seconds > 0f ? Time.time + seconds : -1f;
        _promptSnapped = false; // put it in front of the user
    }

    // ---------- Hand tracking ----------

    void Update()
    {
        if (_hideMarkersAt > 0f && Time.time >= _hideMarkersAt)
        {
            _lowMarker.SetActive(false);
            _highMarker.SetActive(false);
            _hideMarkersAt = -1f;
        }
        if (_messageUntil > 0f && Time.time >= _messageUntil)
        {
            _messageUntil = -1f;
            if (promptLabel)
            {
                promptLabel.text = PromptFor(_phase);
                promptLabel.gameObject.SetActive(IsCalibrating);
            }
        }
        if (_clearPreviewAt > 0f && Time.time >= _clearPreviewAt)
        {
            _clearPreviewAt = -1f;
            if (!IsCalibrating) ClearPreviewKeys();
        }

        UpdateHeadRelativePlacement();

        if (!EnsureHandSubsystem()) { DebugKeys(); return; }

        PollHand(_hands.leftHand, MetaAimHand.left, ref _leftPinching);
        PollHand(_hands.rightHand, MetaAimHand.right, ref _rightPinching);
        DebugKeys();
    }

    /// <summary>True while the OS reports a system gesture on this hand (palm toward the headset → system menu).</summary>
    static bool IsSystemGesture(MetaAimHand aim)
    {
        if (aim == null || !aim.added) return false;
        var flags = (MetaAimFlags)(ulong)(uint)aim.aimFlags.ReadValue();
        return (flags & MetaAimFlags.SystemGesture) != 0;
    }

    bool EnsureHandSubsystem()
    {
        if (_hands != null && _hands.running) return true;
        _hands = null;
        SubsystemManager.GetSubsystems(s_Subsystems);
        foreach (var s in s_Subsystems)
            if (s.running) { _hands = s; break; }
        return _hands != null;
    }

    void PollHand(XRHand hand, MetaAimHand aim, ref bool pinching)
    {
        if (!hand.isTracked) { pinching = false; return; }
        if (!hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb) ||
            !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var index))
            return;

        float d = Vector3.Distance(thumb.position, index.position);
        if (!pinching && d < pinchStartDistance)
        {
            pinching = true; // rising edge = one pinch (also latched for system/palm-up pinches so they never count)
            if (IsCalibrating && Time.time >= _ignoreUntil && !IsSystemGesture(aim) && PalmFacesDown(hand))
                SubmitPoint(ToWorld((thumb.position + index.position) * 0.5f));
        }
        else if (pinching && d > pinchEndDistance)
        {
            pinching = false;
        }
    }

    bool PalmFacesDown(XRHand hand)
    {
        if (!requirePalmDown) return true;
        if (!hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm)) return true; // can't tell: don't block
        // XR Hands joint frame: +Y = back of the hand. Palm down ⇒ back of hand points up.
        Vector3 backOfHand = palm.rotation * Vector3.up;
        Transform space = xrOrigin && xrOrigin.CameraFloorOffsetObject ? xrOrigin.CameraFloorOffsetObject.transform : null;
        if (space) backOfHand = space.TransformDirection(backOfHand);
        return Vector3.Dot(backOfHand, Vector3.up) > palmDownThreshold;
    }

    Vector3 ToWorld(Vector3 originSpacePoint)
    {
        // XR Hands joint poses share the camera's tracking space (the XR Origin's Camera Offset).
        Transform space = xrOrigin
            ? (xrOrigin.CameraFloorOffsetObject ? xrOrigin.CameraFloorOffsetObject.transform : xrOrigin.transform)
            : null;
        return space ? space.TransformPoint(originSpacePoint) : originSpacePoint;
    }

    // ---------- UI bits ----------

    void SetPhase(Phase p)
    {
        _phase = p;
        if (overlayGrab) overlayGrab.enabled = p == Phase.Idle; // no nudging mid-calibration

        if (!promptLabel) return;
        promptLabel.gameObject.SetActive(p != Phase.Idle);
        _messageUntil = -1f; // a phase change replaces any temporary message
        promptLabel.text = PromptFor(p);
    }

    Transform HeadTransform() =>
        xrOrigin && xrOrigin.Camera ? xrOrigin.Camera.transform : (Camera.main ? Camera.main.transform : null);

    /// <summary>True once the runtime reports a tracked head pose (always true without an XR device, e.g. Editor play mode).</summary>
    static bool HeadIsTracked()
    {
        if (!UnityEngine.XR.XRSettings.isDeviceActive) return true;
        var head = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
        return head.isValid && head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool tracked) && tracked;
    }

    static Vector3 FlatForward(Transform t)
    {
        Vector3 f = t.forward; f.y = 0f;
        return f.sqrMagnitude < 1e-4f ? Vector3.forward : f.normalized;
    }

    // The XR session can become visible long after Start() (e.g. glasses put on later), so anything placed
    // relative to the head in Start() lands at an arbitrary spot. Re-place once tracking is valid, then follow.
    void UpdateHeadRelativePlacement()
    {
        HookTrackingOriginEvents();
        var head = HeadTransform();
        if (!head || !HeadIsTracked()) { _trackedSince = -1f; return; }
        if (_trackedSince < 0f) _trackedSince = Time.time;
        if (Time.time - _trackedSince < trackingSettleSeconds) return; // let the pose settle first
        Vector3 fwd = FlatForward(head);
        Quaternion yaw = Quaternion.LookRotation(fwd, Vector3.up);

        if (placeOverlayInFrontWhenTracked && !_overlayPlaced && overlay)
        {
            // Center the keyboard (middle C) at the head-relative offset, keys running left-to-right.
            overlay.transform.rotation = yaw;
            overlay.transform.localScale = Vector3.one; // real size; ignore any stale saved calibration
            Vector3 target = head.position + yaw * initialOverlayOffset;
            Vector3 centerOffset = overlay.GetKeyWorldCenter(60) - overlay.transform.position;
            overlay.transform.position = target - centerOffset;
            _overlayPlaced = true;
        }

        if ((IsCalibrating || _messageUntil > 0f) && promptLabel)
        {
            var t = promptLabel.transform;
            Vector3 target = head.position + fwd * promptDistance + Vector3.down * 0.1f;
            Quaternion rot = Quaternion.LookRotation(fwd, Vector3.up); // TMP faces -Z, so look away from the user

            if (!_promptSnapped)
            {
                t.SetPositionAndRotation(target, rot);
                _promptSnapped = true; _promptGliding = false; _promptOutOfViewSince = -1f;
                return;
            }

            // Stay still while it's in view (no jitter); re-center only after it has been out of view for a moment.
            Vector3 toPrompt = t.position - head.position;
            float angle = Vector3.Angle(head.forward, toPrompt);
            if (!_promptGliding)
            {
                if (angle > promptMaxAngle)
                {
                    if (_promptOutOfViewSince < 0f) _promptOutOfViewSince = Time.time;
                    if (Time.time - _promptOutOfViewSince >= promptRecenterAfterSeconds)
                    {
                        _promptGlideTarget = target; _promptGlideRot = rot; _promptGliding = true;
                    }
                }
                else _promptOutOfViewSince = -1f;
            }
            if (_promptGliding)
            {
                float k = 1f - Mathf.Exp(-8f * Time.deltaTime);
                t.SetPositionAndRotation(Vector3.Lerp(t.position, _promptGlideTarget, k), Quaternion.Slerp(t.rotation, _promptGlideRot, k));
                if (Vector3.Distance(t.position, _promptGlideTarget) < 0.005f) { _promptGliding = false; _promptOutOfViewSince = -1f; }
            }
        }
    }

    // The preview only touches keys it lit itself, so it never wipes highlights set by the lesson logic.
    readonly List<int> _previewLit = new List<int>();

    void LightPreviewKey(int note, KeyState state)
    {
        overlay.SetKey(note, state);
        if (!_previewLit.Contains(note)) _previewLit.Add(note);
    }

    void ClearPreviewKeys()
    {
        if (overlay) foreach (var n in _previewLit) overlay.SetKey(n, KeyState.Off);
        _previewLit.Clear();
    }

    void ShowCalibrationPreview()
    {
        if (!previewKeys || !overlay) return;
        _clearPreviewAt = -1f;
        ClearPreviewKeys();
        LightPreviewKey(overlay.lowestNote, KeyState.Next);  // pulse the two keys to pinch
        LightPreviewKey(overlay.highestNote, KeyState.Next);
    }

    void ShowAlignmentPreview()
    {
        if (!previewKeys || !overlay) return;
        ClearPreviewKeys();
        for (int n = overlay.lowestNote; n <= overlay.highestNote; n++)
            if (n % 12 == 0) LightPreviewKey(n, KeyState.Upcoming); // every C, to check alignment along the keyboard
        LightPreviewKey(overlay.lowestNote, KeyState.Next);
        LightPreviewKey(overlay.highestNote, KeyState.Next);
        _clearPreviewAt = previewSecondsAfterCalibration > 0f ? Time.time + previewSecondsAfterCalibration : -1f;
    }

    GameObject CreateMarker(string name, Color color)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        DestroyImmediate(go.GetComponent<Collider>());
        go.transform.SetParent(transform, false);
        go.transform.localScale = Vector3.one * markerSize;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = markerMaterial;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
        var mpb = new MaterialPropertyBlock();
        mpb.SetColor(BaseColorId, color);
        mpb.SetColor(ColorId, color);
        r.SetPropertyBlock(mpb);
        go.SetActive(false);
        return go;
    }

    void ShowMarker(GameObject marker, Vector3 worldPoint)
    {
        marker.transform.position = worldPoint;
        marker.SetActive(true);
    }

    void OnRecalibratePressed(SelectEnterEventArgs _) => Recalibrate();

    void OnOverlayReleased(SelectExitEventArgs _)
    {
        if (overlay) overlay.SaveCalibration();
    }

    // ---------- Editor debug (no headset needed) ----------
    // C = fake both pinches 0.5 m in front of the camera, 1.22 m apart (≈ A0..C8). R = Recalibrate.

    void DebugKeys()
    {
#if UNITY_EDITOR
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return;
        if (kb.rKey.wasPressedThisFrame) Recalibrate();
        if (kb.cKey.wasPressedThisFrame)
        {
            var cam = Camera.main ? Camera.main.transform : null;
            Vector3 origin = cam ? cam.position : Vector3.zero;
            Vector3 fwd = cam ? Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized : Vector3.forward;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 front = origin + fwd * 0.5f + Vector3.down * 0.35f;
            if (!IsCalibrating) Recalibrate();
            _ignoreUntil = 0f;
            SubmitPoint(front - right * 0.61f);
            SubmitPoint(front + right * 0.61f);
        }
#endif
    }
}
