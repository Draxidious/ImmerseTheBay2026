using UnityEngine;

/// <summary>
/// Drives a <see cref="CoachOrb"/> from the AI layer (AICoach, VoiceOutput, VoiceInput) without changing the AI scripts.
///
///  - State, by priority: Listening (VoiceInput.IsListening) > Thinking (AICoach.IsThinking) > Speaking (VoiceOutput.IsSpeaking) > Idle.
///  - While Thinking: shows a "..." placeholder beside the orb.
///  - On AICoach.OnResponse: shows result.say (fallback replies too; empty replies show nothing). The text stays up while
///    the coach speaks, then for lingerSeconds more (or the orb's auto reading time, whichever is longer).
///  - While Speaking: voice level from the RMS of VoiceOutput's AudioSource (Groq TTS). Android native TTS doesn't play
///    through that source, so then a gentle synthetic pulse is used.
///
/// Finds the components automatically. Needs a CoachOrb; the AI parts are optional (each one missing just disables its part).
/// Editor debug key: G = send a fake "run finished" context (AIDebugPanel.Improving) to the coach.
/// </summary>
public class CoachOrbBridge : MonoBehaviour
{
    [Tooltip("Optional. Found automatically.")] public CoachOrb orb;
    [Tooltip("Optional. Found automatically.")] public AICoach coach;
    [Tooltip("Optional. Found automatically.")] public VoiceOutput voiceOutput;
    [Tooltip("Optional. Found automatically.")] public VoiceInput voiceInput;

    [Header("Text")]
    [Tooltip("Seconds the reply stays up after the coach stops speaking.")]
    public float lingerSeconds = 2f;
    [Tooltip("Shown beside the orb while the coach is thinking. Empty = nothing.")]
    public string thinkingText = "...";

    [Header("Voice level")]
    [Tooltip("RMS × gain = level (0..1). Speech RMS is usually 0.05–0.2.")]
    public float voiceGain = 6f;
    [Tooltip("RMS below this counts as no audio on the source (e.g. native TTS, or TTS still downloading).")]
    public float silenceRms = 0.002f;

    [Header("Debug")]
    [Tooltip("Log orb state and text changes to the Console.")]
    public bool logChanges;

    const float KeepUp = 600f; // "until we say otherwise" for ShowText

    readonly float[] _samples = new float[256];
    AICoach _subscribed;
    OrbState _lastState = (OrbState)(-1);
    bool _placeholderShown;
    string _reply;            // reply currently shown (null = none)
    float _replyAt;           // when it arrived
    bool _replySpoken;        // the coach started speaking after this reply
    bool _replyReleased;      // final duration already handed to the orb

    void OnDisable() => Unsubscribe();

    void Resolve()
    {
        if (!orb) orb = FindAnyObjectByType<CoachOrb>();
        if (!coach) coach = FindAnyObjectByType<AICoach>();
        if (!voiceOutput) voiceOutput = coach && coach.voiceOutput ? coach.voiceOutput : FindAnyObjectByType<VoiceOutput>();
        if (!voiceInput) voiceInput = FindAnyObjectByType<VoiceInput>();
        if (coach != _subscribed)
        {
            Unsubscribe();
            if (coach) { coach.OnResponse += OnResponse; _subscribed = coach; }
        }
    }

    void Unsubscribe()
    {
        if (_subscribed) _subscribed.OnResponse -= OnResponse;
        _subscribed = null;
    }

    // Raised by AICoach on the main thread, after IsThinking goes false and before the line is spoken.
    void OnResponse(CoachResult result)
    {
        if (!orb) return;
        _placeholderShown = false;
        if (result == null || string.IsNullOrWhiteSpace(result.say))
        {
            _reply = null;
            orb.HideText();
            return;
        }
        _reply = result.say;
        _replyAt = Time.time;
        _replySpoken = false;
        _replyReleased = false;
        if (logChanges) Debug.Log($"[CoachOrbBridge] {Time.time:0.00}s reply{(result.isFallback ? " (fallback)" : "")}: \"{_reply}\"");
        orb.ShowText(_reply, KeepUp); // held until speech ends (see UpdateReplyText)
    }

    void Update()
    {
        Resolve();
        if (!orb) return;

        bool listening = voiceInput && voiceInput.IsListening;
        bool thinking = coach && coach.IsThinking;
        bool speaking = voiceOutput && voiceOutput.IsSpeaking;

        var state = listening ? OrbState.Listening
                  : thinking ? OrbState.Thinking
                  : speaking ? OrbState.Speaking
                  : OrbState.Idle;
        // Only on change, so the orb's own debug keys (Y/V) still work while nothing is happening.
        if (state != _lastState)
        {
            if (logChanges) Debug.Log($"[CoachOrbBridge] {Time.time:0.00}s state {_lastState} -> {state}");
            orb.SetState(state);
            _lastState = state;
        }

        UpdatePlaceholder(thinking);
        UpdateReplyText(speaking);
        if (state == OrbState.Speaking) orb.SetVoiceLevel(VoiceLevel());

#if UNITY_EDITOR
        DebugKeys();
#endif
    }

    void UpdatePlaceholder(bool thinking)
    {
        if (thinking && !_placeholderShown && !string.IsNullOrEmpty(thinkingText))
        {
            _reply = null; // a new request supersedes the old reply
            if (logChanges) Debug.Log($"[CoachOrbBridge] {Time.time:0.00}s thinking placeholder \"{thinkingText}\"");
            orb.ShowText(thinkingText, KeepUp);
            _placeholderShown = true;
        }
        else if (!thinking && _placeholderShown)
        {
            // Thinking ended without a reply (e.g. a skipped "stuck" nudge). A reply would have cleared the flag.
            _placeholderShown = false;
            orb.HideText();
        }
    }

    void UpdateReplyText(bool speaking)
    {
        if (_reply == null || _replyReleased) return;
        if (speaking) { _replySpoken = true; return; }

        // Not speaking: either speech finished, or it never started (speakReplies off, no VoiceOutput, TTS failed).
        // Give speech a moment to start before deciding it never will.
        if (!_replySpoken && Time.time - _replyAt < 0.5f) return;

        int words = _reply.Split(' ').Length;
        float readTime = Mathf.Max(orb.minSeconds, words * orb.secondsPerWord) - (Time.time - _replyAt);
        float keep = Mathf.Max(lingerSeconds, readTime);
        if (logChanges) Debug.Log($"[CoachOrbBridge] {Time.time:0.00}s speech {(_replySpoken ? "ended" : "never started")}, text stays {keep:0.0}s");
        orb.ShowText(_reply, keep); // same text, new duration (no flicker)
        _replyReleased = true;
    }

    float VoiceLevel()
    {
        var src = voiceOutput ? voiceOutput.source : null;
        if (src && src.isPlaying)
        {
            src.GetOutputData(_samples, 0);
            float sum = 0f;
            for (int i = 0; i < _samples.Length; i++) sum += _samples[i] * _samples[i];
            float rms = Mathf.Sqrt(sum / _samples.Length);
            if (rms > silenceRms) return Mathf.Clamp01(rms * voiceGain);
        }
        // No measurable audio (native TTS, or the TTS request is still in flight): gentle synthetic pulse.
        float t = Time.time;
        return 0.35f + 0.2f * Mathf.Sin(t * 7.3f) * Mathf.Sin(t * 2.1f + 1f);
    }

#if UNITY_EDITOR
    void DebugKeys()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null || !coach) return;
        if (kb.gKey.wasPressedThisFrame && !coach.IsThinking)
        {
            Debug.Log("[CoachOrbBridge] G: sending a fake 'run finished' context to the coach.");
            _ = coach.Coach(AIDebugPanel.Improving());
        }
    }
#endif
}
