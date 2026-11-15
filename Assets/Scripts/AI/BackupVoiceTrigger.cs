using System;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

// Asks a question without the wake word: Q key (Editor/desktop) or holding a right-hand pinch for AIConfig.pinchHoldSeconds.
// A hold fires once. It is rejected while calibrating, during the Android XR system gesture (palm toward you), or with the
// palm facing down (playing / pinching key edges). Same pinch thresholds as PianoCalibrator.
public class BackupVoiceTrigger : MonoBehaviour, IVoiceTrigger
{
    const float PinchStartDistance = 0.02f;
    const float PinchEndDistance = 0.04f;
    const float PalmDownThreshold = 0.35f; // dot(back-of-hand, world up), as PianoCalibrator

    [Tooltip("Optional. Defaults to Assets/Resources/AIConfig.asset.")]
    public AIConfig config;
    [Tooltip("Optional. Found automatically. No pinch-hold while it is calibrating.")]
    public PianoCalibrator calibrator;
    [Tooltip("Optional. Found automatically. Hand poses are in its tracking space.")]
    public XROrigin xrOrigin;

    public string Name => "backup";
    public bool IsActive => Cfg.backupKeyEnabled || Cfg.pinchHoldEnabled;
    public event Action<IVoiceTrigger> Triggered;
    // Set by QuestionRouter: why the trigger is not armed right now, for the log line when Q is pressed in vain.
    public Func<string> WhyNotArmed;

    static readonly List<XRHandSubsystem> s_Subsystems = new List<XRHandSubsystem>();

    AIConfig Cfg => config != null ? config : AIConfig.Get();

    XRHandSubsystem _hands;
    bool _armed, _pinching, _holdRejected, _holdFired;
    float _held;

    void Awake()
    {
        if (!calibrator) calibrator = FindAnyObjectByType<PianoCalibrator>();
        if (!xrOrigin) xrOrigin = FindAnyObjectByType<XROrigin>();
    }

    public void SetArmed(bool armed)
    {
        _armed = armed;
        // A hold already in progress when arming does not count.
        if (armed && _pinching) _holdRejected = true;
    }

    void Update()
    {
        var cfg = Cfg;
        var kb = Keyboard.current;
        if (cfg.backupKeyEnabled && kb != null && kb.qKey.wasPressedThisFrame)
        {
            if (_armed) Fire("Q key");
            else Debug.Log("[Question] Q ignored: " + (WhyNotArmed?.Invoke() ?? "not armed (not in the lesson, or the coach is busy)") + ".");
        }

        if (cfg.pinchHoldEnabled) PollPinch(cfg);
    }

    void PollPinch(AIConfig cfg)
    {
        if (!EnsureHandSubsystem()) { _pinching = false; return; }
        var hand = _hands.rightHand;
        if (!hand.isTracked ||
            !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb) ||
            !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var index))
        { _pinching = false; return; }

        float d = Vector3.Distance(thumb.position, index.position);
        if (!_pinching && d < PinchStartDistance)
        {
            _pinching = true;
            _held = 0f;
            _holdFired = false;
            _holdRejected = !_armed;
        }
        else if (_pinching && d > PinchEndDistance)
        {
            _pinching = false;
            return;
        }
        if (!_pinching || _holdFired) return;

        // Any bad frame rejects the whole hold.
        if (!_armed || (calibrator && calibrator.IsCalibrating) || IsSystemGesture(MetaAimHand.right) || PalmFacesDown(hand))
            _holdRejected = true;
        if (_holdRejected) return;

        _held += Time.deltaTime;
        if (_held >= cfg.pinchHoldSeconds)
        {
            _holdFired = true;
            Fire("pinch-hold");
        }
    }

    void Fire(string how)
    {
        Debug.Log("[Question] Triggered by " + how + ".");
        Triggered?.Invoke(this);
    }

    // Private copies of PianoCalibrator's checks (keep in step with Assets/Scripts/UI/PianoCalibrator.cs).
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

    bool PalmFacesDown(XRHand hand)
    {
        if (!hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm)) return false; // can't tell: allow
        // XR Hands joint frame: +Y = back of the hand. Palm down => back of hand points up.
        Vector3 backOfHand = palm.rotation * Vector3.up;
        Transform space = xrOrigin && xrOrigin.CameraFloorOffsetObject ? xrOrigin.CameraFloorOffsetObject.transform : null;
        if (space) backOfHand = space.TransformDirection(backOfHand);
        return Vector3.Dot(backOfHand, Vector3.up) > PalmDownThreshold;
    }
}
