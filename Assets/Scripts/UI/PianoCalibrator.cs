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
    [Tooltip("Hidden while calibrating (Recalibrate button, its label, the grab handle) so a pinch on the end keys " +
             "can't hit them and restart calibration.")]
    public GameObject[] hideWhileCalibrating;
    [Tooltip("Optical see-through: the real hands are visible, so the rig's hand meshes only cover the fingers and keys. " +
             "Kept hidden every frame because XR Hands re-enables them when tracking changes. Tracking/pinch still work.")]
    public bool hideHandMeshes = true;
    public Renderer[] handMeshRenderers;
    [Tooltip("Once calibrated, the hand meshes become invisible depth-only occluders, so the overlay is hidden wherever a " +
             "hand is in front of it (like the real keys). While calibrating they stay hidden so pinch markers stay visible.")]
    public bool handsOccludeOverlay = true;
    [Tooltip("Depth-only material (PianoTeacher/HandOccluder). Loaded from Resources/HandOccluder if empty.")]
    public Material handOccluderMaterial;

    [Header("Recalibrate dot on the back of the LEFT hand")]
    [Tooltip("Always available: touch the orange dot on the back of your left hand with your right index finger and hold.")]
    public bool handRecalibrateDot = true;
    public float handDotHeight = 0.035f;     // above the back of the hand (m)
    public float handDotSize = 0.022f;
    public float handDotTouchRadius = 0.03f;
    public float handDotHoldSeconds = 0.6f;
    public Color handDotColor = new Color(1f, 0.55f, 0.1f);
    [Tooltip("Second dot just below the orange one (toward the wrist). Touch and hold it to raise OffsetsDotPressed " +
             "(MarkerCalibration opens its offset sliders). Only shown while something listens to that event.")]
    public bool handOffsetsDot = true;
    public float offsetsDotSpacing = 0.045f;  // from the orange dot toward the wrist (m)
    public Color offsetsDotColor = new Color(0.25f, 0.8f, 1f);

    [Header("Pinch detection (meters between thumb tip and index tip)")]
    public float pinchStartDistance = 0.02f;
    public float pinchEndDistance = 0.04f;
    [Tooltip("Default. Four pinches, one hand at a time: front edge + back end of the lowest key, then of the highest key. " +
             "Each step is checked against the previous ones, so a stray pinch only repeats that step. " +
             "Overrides twoHandCalibration.")]
    public bool fourPointCalibration = true;
    [Tooltip("Accepted distance between a key's front edge and back end (m).")]
    public Vector2 keyDepthRange = new Vector2(0.06f, 0.25f);
    [Tooltip("A back-end pinch must be at least this far behind the front-edge pinch, measured level toward the panel (m).")]
    public float minBackDepth = 0.08f;
    [Tooltip("A back-end pinch may be at most this much higher or lower than its front-edge pinch (m).")]
    public float maxBackHeightDelta = 0.06f;
    [Tooltip("Calibrate with one pinch per hand at the same time (left hand on the lowest key, right hand on the highest). " +
             "A single stray pinch can't finish calibration. Off = the old two-step, one-hand flow.")]
    public bool twoHandCalibration = true;
    [Tooltip("Both hands must pinch within this many seconds of each other.")]
    public float twoHandWindowSeconds = 1.0f;
    [Tooltip("The thumb/index midpoint sits above the key surface when pinching a key edge; lower both points by this much (m).")]
    public float pinchHeightOffset = 0.008f;
    [Tooltip("Reject a calibration whose two points are closer/farther than this fraction of the expected keyboard width.")]
    public float minSpanFraction = 0.85f;
    public float maxSpanFraction = 1.15f;
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

    /// <summary>Raised after a successful calibration (pinches or markers).</summary>
    public event Action Calibrated;
    /// <summary>Raised whenever calibration (re)starts, so other calibration sources (e.g. MarkerCalibration) can begin.</summary>
    public event Action CalibrationStarted;
    /// <summary>Raised when the second (blue) dot on the back of the left hand is touched and held.</summary>
    public event Action OffsetsDotPressed;
    /// <summary>
    /// Raised when the orange dot on the back of the left hand is touched and held. While anything listens (the
    /// RadialMenu), the dot opens that menu instead of calling <see cref="Recalibrate"/> directly.
    /// </summary>
    public event Action MenuDotPressed;
    internal bool UsesHandMenu { get; set; }
    /// <summary>Raised by <see cref="CancelCalibration"/>: calibration stopped, the previous overlay pose is kept.</summary>
    public event Action CalibrationCancelled;
    /// <summary>
    /// Set by MarkerCalibration: the corner markers take over the four pinch steps (pinches are ignored and the
    /// prompt asks for the markers). MarkerCalibration clears it again if marker tracking can't run on the device.
    /// </summary>
    [NonSerialized] public bool markersAvailable;

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
    readonly Vector3[] _pts = new Vector3[4]; // four-point flow, sorted: nearLow, farLow, nearHigh, farHigh
    readonly Vector3[] _raw = new Vector3[4]; // four-point flow, in pinch order
    int _step;
    int _stepHand = -1;              // four-point flow: hand (0 = left, 1 = right) that made the current key's front pinch
    GameObject _backLowMarker, _backHighMarker;
    GameObject _handDot, _offsetsDot;
    float _handDotHeld, _handDotCooldownUntil, _offsetsDotHeld;
    float _leftPinchTime = -10f, _rightPinchTime = -10f;
    Vector3 _leftPinchPoint, _rightPinchPoint;
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
        _backLowMarker = CreateMarker("CalibMarker_BackLow", Color.Lerp(lowMarkerColor, Color.white, 0.5f));
        _backHighMarker = CreateMarker("CalibMarker_BackHigh", Color.Lerp(highMarkerColor, Color.white, 0.5f));
        _handDot = CreateMarker("LeftHandRecalibrateDot", handDotColor);
        _handDot.transform.localScale = Vector3.one * handDotSize;
        _offsetsDot = CreateMarker("LeftHandOffsetsDot", offsetsDotColor);
        _offsetsDot.transform.localScale = Vector3.one * handDotSize;
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
        _backLowMarker.SetActive(false);
        _backHighMarker.SetActive(false);
        _promptSnapped = false; // snap (not lerp) to the head on the next tracked frame
        _leftPinchTime = _rightPinchTime = -10f;
        _step = 0;
        _stepHand = -1;
        SetPhase(Phase.WaitingForLow);
        ShowCalibrationPreview();
        CalibrationStarted?.Invoke();
    }

    /// <summary>
    /// Stop a calibration in progress without changing the overlay: it stays where the last successful calibration put
    /// it (the overlay only moves when a fit succeeds). Raises <see cref="CalibrationCancelled"/>.
    /// </summary>
    public void CancelCalibration()
    {
        if (!IsCalibrating) return;
        _lowMarker.SetActive(false);
        _highMarker.SetActive(false);
        _backLowMarker.SetActive(false);
        _backHighMarker.SetActive(false);
        SetPhase(Phase.Idle);
        ClearPreviewKeys();
        ShowMessage("Recalibration cancelled.\nKeeping the previous calibration.", 3f);
        Debug.Log("[PianoCalibrator] Calibration cancelled; previous calibration kept.");
        CalibrationCancelled?.Invoke();
    }

    /// <summary>
    /// Calibrate from the BACK OUTER corners of the lowest and highest key (where they meet the panel), e.g. from the
    /// corner markers. Each corner is moved half a white key inward to the key centre, and the front points are one
    /// <see cref="PianoOverlay.whiteKeyLength"/> toward the player. With <paramref name="useKnownWidth"/> only the
    /// centre and direction come from the corners; the span is the real keyboard width (removes marker scale error).
    /// Ends calibration like a successful pinch calibration (raises <see cref="Calibrated"/>). Returns null on success.
    /// </summary>
    public string CalibrateFromMarkers(Vector3 backLowOuter, Vector3 backHighOuter, bool useKnownWidth = true)
    {
        string reason = FitFromBackCorners(backLowOuter, backHighOuter, useKnownWidth);
        if (reason != null)
        {
            Debug.Log($"[PianoCalibrator] Marker fit rejected: {reason}. Corners {backLowOuter:F3} → {backHighOuter:F3}.");
            return reason;
        }
        Debug.Log($"[PianoCalibrator] Calibrated (markers): corners {backLowOuter:F3} → {backHighOuter:F3}, " +
                  $"measured span {Vector3.Distance(backLowOuter, backHighOuter) * 100f:F1} cm, knownWidth={useKnownWidth}, " +
                  $"scale {overlay.transform.localScale.x:F3}.");
        _overlayPlaced = true;
        _lowMarker.SetActive(false);
        _highMarker.SetActive(false);
        _backLowMarker.SetActive(false);
        _backHighMarker.SetActive(false);
        SetPhase(Phase.Idle);
        ShowAlignmentPreview();
        ShowMessage("<b>Calibrated from the markers!</b>\nThe blue bars should sit on every C key.\n" +
                    (UsesHandMenu ? "Tap Menu on your left hand for options and fine-tuning."
                        : "Fine-tune: touch the BLUE dot on the back of your left hand.\n" +
                          (MenuDotPressed != null ? "Menu (redo): touch the ORANGE dot." : "Redo: touch the ORANGE dot.")),
                    previewSecondsAfterCalibration);
        Calibrated?.Invoke();
        return null;
    }

    /// <summary>Re-fit from adjusted back corners without ending/raising anything (offset sliders). Null on success.</summary>
    public string ApplyMarkerAdjustment(Vector3 backLowOuter, Vector3 backHighOuter, bool useKnownWidth = true) =>
        FitFromBackCorners(backLowOuter, backHighOuter, useKnownWidth);

    /// <summary>Show a temporary message on the calibration prompt (e.g. marker progress).</summary>
    public void ShowInfo(string text, float seconds) => ShowMessage(text, seconds);

    string FitFromBackCorners(Vector3 lowOuter, Vector3 highOuter, bool useKnownWidth)
    {
        if (!overlay) return "no overlay";
        Vector3 along = highOuter - lowOuter;
        if (along.magnitude < 0.3f) return "corners too close together";
        Vector3 right = along.normalized;
        Vector3 toPanel = Vector3.Cross(right, Vector3.up).normalized; // same convention as the overlay fit
        float keyWidth = overlay.whiteKeyWidth;
        float outerSpan = useKnownWidth ? ExpectedSpanMeters() + keyWidth : along.magnitude; // A0 left edge → C8 right edge
        Vector3 mid = (lowOuter + highOuter) * 0.5f;
        Vector3 backLow = mid - right * (outerSpan * 0.5f - keyWidth * 0.5f);
        Vector3 backHigh = mid + right * (outerSpan * 0.5f - keyWidth * 0.5f);
        Vector3 toPlayer = -toPanel * overlay.whiteKeyLength;
        return overlay.CalibrateFromFourPoints(backLow + toPlayer, backLow, backHigh + toPlayer, backHigh);
    }

    /// <summary>Four-point flow: validate one pinch against the previous ones; a bad pinch only repeats that step.</summary>
    void SubmitFourPointStep(Vector3 p, int hand)
    {
        var h = HeadTransform();
        if (h)
        {
            Vector3 hf = FlatForward(h), hr = Vector3.Cross(Vector3.up, hf), rel = p - h.position;
            Debug.Log($"[PianoCalibrator] Pinch for step {_step + 1} ({(hand == 0 ? "left" : hand == 1 ? "right" : "?")} hand): " +
                      $"world {p:F3}; from head: right {Vector3.Dot(rel, hr) * 100f:F0} cm, up {rel.y * 100f:F0} cm, " +
                      $"forward {Vector3.Dot(rel, hf) * 100f:F0} cm; head pitch {h.eulerAngles.x:F0}°.");
        }
        // A key's second pinch must come from the hand that made its first pinch; the other (resting) hand's
        // stray pinches are ignored instead of being judged as attempts.
        if ((_step == 1 || _step == 3) && hand >= 0 && _stepHand >= 0 && hand != _stepHand)
        {
            Debug.Log($"[PianoCalibrator] Step {_step + 1}: ignored a pinch from the {(hand == 0 ? "left" : "right")} hand.");
            return;
        }

        // Pinch order per key: FAR end (at the panel) first, then NEAR end (closest to the player). The reverse order
        // is also accepted; each key's two pinches are sorted into near/far by distance toward the panel.
        float expected = ExpectedSpanMeters();
        string problem = null;
        Vector3 nearPt = p, farPt = p;
        switch (_step)
        {
            case 1:
            case 3:
            {
                Vector3 first = _raw[_step - 1];
                float d = Vector3.Distance(p, first);
                // Level direction toward the panel: from the left→right line once known (same as the fit), else the gaze.
                Vector3 fwd;
                if (_step == 3) fwd = Vector3.Cross(Flat(_raw[2] - _raw[0]), Vector3.up).normalized;
                else { var head = HeadTransform(); fwd = head ? Flat(head.forward) : Vector3.zero; }
                float along = fwd.sqrMagnitude > 0f ? Vector3.Dot(p - first, fwd) : -d; // + = toward the panel
                float rise = p.y - first.y;
                if (along < 0f) { nearPt = p; farPt = first; } else { nearPt = first; farPt = p; }
                if (d < keyDepthRange.x || d > keyDepthRange.y)
                    problem = $"That was {d * 100f:F0} cm from your first pinch.\nPinch the OTHER end of the same key\n(keys are about 14 cm long).";
                else if (Mathf.Abs(along) < minBackDepth)
                    problem = $"That was only {Mathf.Abs(along) * 100f:F0} cm nearer to / farther from you.\nOpen your fingers, rest your hand on the\nOTHER end of the same key, then pinch.";
                else if (Mathf.Abs(rise) > maxBackHeightDelta)
                    problem = $"That was {Mathf.Abs(rise) * 100f:F0} cm {(rise > 0f ? "above" : "below")} your first pinch.\nPinch ON the key.";
                else if (_step == 3)
                {
                    float depthL = Vector3.Distance(_pts[1], _pts[0]);
                    float farSpan = Vector3.Distance(farPt, _pts[1]), nearSpan = Vector3.Distance(nearPt, _pts[0]);
                    if (Mathf.Abs(d - depthL) > 0.05f)
                        problem = $"This key measured {d * 100f:F0} cm long but the left one {depthL * 100f:F0} cm.\nPinch the other end of the far-RIGHT key again.";
                    else if (Mathf.Abs(farSpan - nearSpan) > 0.06f)
                        problem = "The far ends and near ends aren't parallel.\nPinch the other end of the far-RIGHT key again.";
                }
                break;
            }
            case 2:
            {
                float span = Vector3.Distance(p, _raw[0]);
                var head = HeadTransform();
                if (head && Vector3.Dot(p - _raw[0], head.right) < 0f)
                    problem = "That point is LEFT of the first one.\nPinch the far-RIGHT key.";
                else if (span < expected * minSpanFraction || span > expected * maxSpanFraction)
                    problem = $"That was {span * 100f:F0} cm from the far-left key;\nthe far-right key is about {expected * 100f:F0} cm away.";
                break;
            }
        }

        if (problem != null)
        {
            Debug.Log($"[PianoCalibrator] Step {_step + 1} rejected: {problem.Replace('\n', ' ')}");
            ShowMessage(problem + "\n\n" + PromptFor(_phase), 5f);
            return;
        }

        _raw[_step] = p;
        if (_step == 1 || _step == 3) { _pts[_step - 1] = nearPt; _pts[_step] = farPt; } // _pts = near/far per key
        if (_step == 0 || _step == 2) _stepHand = hand;
        ShowMarker(_step switch { 0 => _lowMarker, 1 => _backLowMarker, 2 => _highMarker, _ => _backHighMarker }, p);
        _step++;
        if (_step < 4)
        {
            SetPhase(_step < 2 ? Phase.WaitingForLow : Phase.WaitingForHigh);
            return;
        }

        Vector3 drop = Vector3.down * pinchHeightOffset; // pinch midpoint → key surface
        string reason = overlay ? overlay.CalibrateFromFourPoints(_pts[0] + drop, _pts[1] + drop, _pts[2] + drop, _pts[3] + drop) : "no overlay";
        if (reason != null)
        {
            Debug.Log($"[PianoCalibrator] Four-point fit rejected: {reason}. Points: nearLow {_pts[0]:F3}, farLow {_pts[1]:F3}, " +
                      $"nearHigh {_pts[2]:F3}, farHigh {_pts[3]:F3}; depth L/R {(_pts[1] - _pts[0]) * 100f:F1} / {(_pts[3] - _pts[2]) * 100f:F1} cm.");
            Recalibrate();
            ShowMessage($"Those points didn't fit a keyboard ({reason}).\nLet's start again.\n\n" + PromptFor(Phase.WaitingForLow), 6f);
            return;
        }
        Debug.Log($"[PianoCalibrator] Calibrated (4 points): span {Vector3.Distance(_pts[0], _pts[2]) * 100f:F0} cm, " +
                  $"key depth {Vector3.Distance(_pts[0], _pts[1]) * 100f:F0}/{Vector3.Distance(_pts[2], _pts[3]) * 100f:F0} cm, " +
                  $"scale {overlay.transform.localScale.x:F3}, whiteKeyLength {overlay.whiteKeyLength * 100f:F1} cm (model).");
        _overlayPlaced = true;
        if (hideMarkersAfter > 0f) _hideMarkersAt = Time.time + hideMarkersAfter;
        SetPhase(Phase.Idle);
        ShowAlignmentPreview();
        ShowMessage("<b>Calibrated!</b>\nThe blue bars should sit on every C key.\n" +
                    (UsesHandMenu ? "Tap Menu on the back of your left hand."
                      : MenuDotPressed != null ? "Menu (restart, free play, redo): touch the\norange dot on the back of your LEFT hand."
                                            : "To redo: touch the orange dot on the\nback of your LEFT hand for a moment."),
                    previewSecondsAfterCalibration);
        Calibrated?.Invoke();
    }

    /// <summary>
    /// Two-hand flow: record one hand's pinch; when the other hand pinches within <see cref="twoHandWindowSeconds"/>,
    /// calibrate from both (left = lowest key, right = highest). A lone pinch only shows its marker.
    /// </summary>
    public void SubmitHandPinch(bool isLeft, Vector3 worldPoint)
    {
        if (!IsCalibrating) return;
        if (isLeft) { _leftPinchTime = Time.time; _leftPinchPoint = worldPoint; ShowMarker(_lowMarker, worldPoint); }
        else { _rightPinchTime = Time.time; _rightPinchPoint = worldPoint; ShowMarker(_highMarker, worldPoint); }

        if (Mathf.Abs(_leftPinchTime - _rightPinchTime) <= twoHandWindowSeconds)
        {
            _leftPinchTime = _rightPinchTime = -10f;
            _phase = Phase.WaitingForHigh; // reuse the two-point path (sanity check, swap, calibrate)
            _lowPoint = _leftPinchPoint;
            SubmitPoint(_rightPinchPoint);
        }
        else
        {
            ShowMessage(isLeft ? "Got the LEFT hand.\nPinch with your RIGHT hand too,\nboth within a second."
                               : "Got the RIGHT hand.\nPinch with your LEFT hand too,\nboth within a second.",
                        twoHandWindowSeconds + 0.5f);
        }
    }

    /// <summary>Feed a pinch point manually (world space). Used by hand tracking and the Editor debug keys.</summary>
    public void SubmitPoint(Vector3 worldPoint) => SubmitPoint(worldPoint, -1);

    /// <param name="hand">0 = left, 1 = right, -1 = unknown (debug keys).</param>
    public void SubmitPoint(Vector3 worldPoint, int hand)
    {
        if (fourPointCalibration && IsCalibrating) { SubmitFourPointStep(worldPoint, hand); return; }
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
                            (UsesHandMenu ? "Tap Menu on the back of your left hand."
                                : "Pinch and drag the blue ball to nudge. Tap the orange tile to redo."), previewSecondsAfterCalibration);
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

    string PromptFor(Phase p) => markersAvailable && p != Phase.Idle
        ? "<b>Calibrate</b>\nLook at the LEFT corner marker (behind the lowest key),\nthen at the RIGHT one. Hold each still for a second."
        : fourPointCalibration && p != Phase.Idle
        ? _step switch
        {
            0 => "<b>Step 1 of 4</b>\nFar-LEFT key: pinch its FAR end,\nwhere it goes under the panel (palm down).",
            1 => "<b>Step 2 of 4</b>\nSame key: pinch its NEAR end,\nthe edge closest to you.",
            2 => "<b>Step 3 of 4</b>\nFar-RIGHT key: pinch its FAR end,\nwhere it goes under the panel.",
            _ => "<b>Step 4 of 4</b>\nSame key: pinch its NEAR end,\nthe edge closest to you.",
        }
        : twoHandCalibration && p != Phase.Idle
        ? "<b>Calibrate</b>\nLEFT hand on the far-LEFT key,\nRIGHT hand on the far-RIGHT key.\n" +
          "Palms down, pinch both FRONT EDGES together."
        : p switch
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

    void LateUpdate()
    {
        if (handMeshRenderers == null) return;
        if (handsOccludeOverlay && !IsCalibrating)
        {
            if (!handOccluderMaterial) handOccluderMaterial = Resources.Load<Material>("HandOccluder");
            if (handOccluderMaterial)
            {
                // XR Hands enables the renderers while a hand is tracked and may reassign its own material; keep ours.
                foreach (var r in handMeshRenderers)
                {
                    if (!r) continue;
                    // The hand mesh must draw NOTHING visible: replace every material slot (the template hands also carry
                    // a grey glove material in slot 2) with the invisible depth-only occluder. Keep the slot COUNT: the rig's
                    // XRI hand affordances (poke / near-far) tint material index 1, and a one-slot array made them throw
                    // IndexOutOfRangeException every frame (~80k log lines per run).
                    var mats = r.sharedMaterials;
                    bool allOccluder = mats.Length > 0;
                    foreach (var m in mats) if (m != handOccluderMaterial) { allOccluder = false; break; }
                    if (!allOccluder)
                    {
                        var occluders = new Material[Mathf.Max(1, mats.Length)];
                        for (int i = 0; i < occluders.Length; i++) occluders[i] = handOccluderMaterial;
                        r.sharedMaterials = occluders;
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }
                    // Renderers hidden during calibration stay off until tracking changes; follow tracking ourselves.
                    if (_hands != null)
                    {
                        bool tracked = r.name.Contains("Left") ? _hands.leftHand.isTracked : _hands.rightHand.isTracked;
                        if (r.enabled != tracked) r.enabled = tracked;
                    }
                }
                return;
            }
        }
        if (!hideHandMeshes) return;
        foreach (var r in handMeshRenderers) if (r && r.enabled) r.enabled = false;
    }

    void Update()
    {
        if (_hideMarkersAt > 0f && Time.time >= _hideMarkersAt)
        {
            _lowMarker.SetActive(false);
            _highMarker.SetActive(false);
            _backLowMarker.SetActive(false);
            _backHighMarker.SetActive(false);
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

        PollHand(_hands.leftHand, MetaAimHand.left, true, ref _leftPinching);
        PollHand(_hands.rightHand, MetaAimHand.right, false, ref _rightPinching);
        UpdateHandDot();
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

    void PollHand(XRHand hand, MetaAimHand aim, bool isLeft, ref bool pinching)
    {
        if (!hand.isTracked) { pinching = false; return; }
        if (!hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb) ||
            !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var index))
            return;

        float d = Vector3.Distance(thumb.position, index.position);
        if (!pinching && d < pinchStartDistance)
        {
            pinching = true; // rising edge = one pinch (also latched for system/palm-up pinches so they never count)
            if (IsCalibrating && !markersAvailable && Time.time >= _ignoreUntil && !IsSystemGesture(aim) && PalmFacesDown(hand))
            {
                Vector3 p = ToWorld((thumb.position + index.position) * 0.5f);
                if (twoHandCalibration && !fourPointCalibration) SubmitHandPinch(isLeft, p);
                else SubmitPoint(p, isLeft ? 0 : 1);
            }
        }
        else if (pinching && d > pinchEndDistance)
        {
            pinching = false;
        }
    }

    static Vector3 Flat(Vector3 v) => Vector3.ProjectOnPlane(v, Vector3.up).normalized;

    Transform TrackingSpace() =>
        xrOrigin ? (xrOrigin.CameraFloorOffsetObject ? xrOrigin.CameraFloorOffsetObject.transform : xrOrigin.transform) : null;

    internal bool TryGetMenuHandPose(out Pose pose)
    {
        pose = default;
        var head = HeadTransform();
        if (!head || !EnsureHandSubsystem() || !_hands.leftHand.isTracked ||
            IsSystemGesture(MetaAimHand.left) || !_hands.leftHand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm))
            return false;

        var space = TrackingSpace();
        Vector3 position = ToWorld(palm.position);
        Vector3 back = palm.rotation * Vector3.up;
        if (space) back = space.TransformDirection(back);
        Vector3 towardHead = (head.position - position).normalized;
        if (Vector3.Dot(back, towardHead) < 0.25f) return false;

        // Keep the watch face near the back of the hand and angle it slightly toward the viewer.
        Vector3 normal = Vector3.Slerp(back, towardHead, 0.35f);
        Vector3 up = Vector3.ProjectOnPlane(head.up, normal);
        if (up.sqrMagnitude < 0.01f) up = Vector3.ProjectOnPlane(head.forward, normal);
        pose = new Pose(position + back * handDotHeight, Quaternion.LookRotation(-normal, up));
        return true;
    }

    internal bool TryGetMenuIndexTip(out Vector3 position)
    {
        position = default;
        if (_hands == null || !_hands.running || !_hands.rightHand.isTracked ||
            IsSystemGesture(MetaAimHand.right) ||
            !_hands.rightHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var tip))
            return false;
        position = ToWorld(tip.position);
        return true;
    }

    /// <summary>Orange dot on the back of the left hand; right index finger touch-and-hold → Recalibrate().</summary>
    void UpdateHandDot()
    {
        if (!_handDot) return;
        var left = _hands.leftHand;
        var head = HeadTransform();
        bool showOffsets = handOffsetsDot && OffsetsDotPressed != null && _offsetsDot;
        if (UsesHandMenu || !handRecalibrateDot || !left.isTracked || !head ||
            !left.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm))
        { HideHandDots(); return; }

        var space = TrackingSpace();
        Vector3 palmPos = ToWorld(palm.position);
        Vector3 back = palm.rotation * Vector3.up; // XR Hands: +Y = back of the hand
        if (space) back = space.TransformDirection(back);
        bool facingUser = Vector3.Dot(back, (head.position - palmPos).normalized) > 0.2f;
        if (!facingUser) { HideHandDots(); return; }
        _handDot.SetActive(true);

        Vector3 dotPos = palmPos + back * handDotHeight;
        _handDot.transform.position = dotPos;

        // Second dot: same height above the hand, one spacing further toward the wrist.
        Vector3 offsetsPos = dotPos;
        if (showOffsets && left.GetJoint(XRHandJointID.Wrist).TryGetPose(out var wrist))
        {
            Vector3 toWrist = Vector3.ProjectOnPlane(ToWorld(wrist.position) - palmPos, back);
            if (toWrist.sqrMagnitude > 1e-6f) offsetsPos = dotPos + toWrist.normalized * offsetsDotSpacing;
            else showOffsets = false;
        }
        else showOffsets = false;
        _offsetsDot.SetActive(showOffsets);
        if (showOffsets) _offsetsDot.transform.position = offsetsPos;

        Vector3 tipPos = Vector3.zero;
        bool hasTip = false;
        var right = _hands.rightHand;
        if (right.isTracked && right.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var tip))
        { tipPos = ToWorld(tip.position); hasTip = true; }

        // The fingertip counts for the nearer dot only, so the two dots can't both fill up.
        float dRecal = hasTip ? Vector3.Distance(tipPos, dotPos) : float.MaxValue;
        float dOffsets = hasTip && showOffsets ? Vector3.Distance(tipPos, offsetsPos) : float.MaxValue;
        bool ready = Time.time >= _handDotCooldownUntil;
        bool touchRecal = ready && dRecal < handDotTouchRadius && dRecal <= dOffsets;
        bool touchOffsets = ready && dOffsets < handDotTouchRadius && dOffsets < dRecal;

        _handDotHeld = touchRecal ? _handDotHeld + Time.deltaTime : 0f;
        _offsetsDotHeld = touchOffsets ? _offsetsDotHeld + Time.deltaTime : 0f;
        _handDot.transform.localScale = Vector3.one * handDotSize * (1f + Mathf.Clamp01(_handDotHeld / handDotHoldSeconds));
        _offsetsDot.transform.localScale = Vector3.one * handDotSize * (1f + Mathf.Clamp01(_offsetsDotHeld / handDotHoldSeconds));
        if (_handDotHeld >= handDotHoldSeconds)
        {
            _handDotHeld = 0f;
            _handDotCooldownUntil = Time.time + 2f;
            if (MenuDotPressed != null)
            {
                Debug.Log("[PianoCalibrator] Left-hand dot: menu.");
                MenuDotPressed.Invoke();
            }
            else
            {
                Debug.Log("[PianoCalibrator] Recalibrate from the left-hand dot.");
                Recalibrate();
            }
        }
        else if (_offsetsDotHeld >= handDotHoldSeconds)
        {
            _offsetsDotHeld = 0f;
            _handDotCooldownUntil = Time.time + 2f;
            Debug.Log("[PianoCalibrator] Offsets dot pressed.");
            OffsetsDotPressed?.Invoke();
        }
    }

    void HideHandDots()
    {
        _handDot.SetActive(false);
        if (_offsetsDot) _offsetsDot.SetActive(false);
        _handDotHeld = _offsetsDotHeld = 0f;
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
        if (hideWhileCalibrating != null)
            foreach (var go in hideWhileCalibrating) if (go) go.SetActive(p == Phase.Idle);

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
            float half = ExpectedSpanMeters() * 0.5f, depth = 0.14f;
            if (fourPointCalibration)
            {
                SubmitPoint(front - right * half);
                SubmitPoint(front - right * half + fwd * depth);
                SubmitPoint(front + right * half);
                SubmitPoint(front + right * half + fwd * depth);
            }
            else
            {
                SubmitPoint(front - right * half);
                SubmitPoint(front + right * half);
            }
        }
#endif
    }
}
