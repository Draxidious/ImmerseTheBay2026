using System.Reflection;
using UnityEngine;

/// <summary>
/// Drives a <see cref="CoachOrb"/> from the AI layer (AICoach, VoiceOutput, VoiceInput) without changing the AI scripts.
///
///  - State, by priority: Listening (VoiceInput.IsListening) > Thinking (AICoach.IsThinking, or the reply is in but its audio
///    hasn't started yet) > Speaking (VoiceOutput.IsSpeaking) > Idle. Shows "..." while Thinking.
///  - Reply text (AICoach.OnResponse) appears word by word in time with the voice:
///      Groq TTS: the whole line is one AudioClip on VoiceOutput.source, so progress = clip playback position, mapped onto
///      the text with extra weight for pauses after commas / sentence ends.
///      Native TTS (Android) doesn't play through that source: words are revealed at an estimated speaking rate.
///    When speech ends the whole line shows, then stays for lingerSeconds (or the reading time, whichever is longer).
///    If the coach doesn't speak at all, the whole line shows at once.
///  - Voice level while Speaking: RMS of VoiceOutput.source (Groq), otherwise a gentle synthetic pulse.
///
/// Finds the components automatically (orb on this object first). Needs a CoachOrb; the AI parts are optional.
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

    [Header("Words in time with the voice")]
    [Tooltip("Reveal the reply word by word while it is spoken. Off = show the whole line at once.")]
    public bool revealWithSpeech = true;
    [Tooltip("Native TTS pace estimate (characters per second, ≈170 words/min). Groq uses the real audio position instead.")]
    public float charsPerSecond = 14f;
    [Tooltip("Groq: silence at the start / end of the clip, in seconds (kept out of the timing).")]
    public float audioLeadSeconds = 0.15f;
    public float audioTailSeconds = 0.3f;
    [Tooltip("Groq: max seconds to wait for the audio to download before falling back to the estimated pace.")]
    public float maxAudioWait = 5f;
    [Tooltip("Extra pause weight (in characters) after , ; : and after . ! ?")]
    public float commaPause = 3f;
    public float sentencePause = 7f;

    [Header("Voice level")]
    [Tooltip("RMS × gain = level (0..1). Speech RMS is usually 0.05–0.2.")]
    public float voiceGain = 6f;
    [Tooltip("RMS below this counts as no audio on the source (e.g. native TTS, or TTS still downloading).")]
    public float silenceRms = 0.002f;

    [Header("Debug")]
    [Tooltip("Log orb state and text changes to the Console.")]
    public bool logChanges;
    [Tooltip("Editor only: G = send a fake 'run finished' to the coach. Untick if G is used elsewhere in your scene.")]
    public bool debugKeys = true;

    const float KeepUp = 600f; // "until we say otherwise" for ShowText
    const float NeverStartedAfter = 0.5f;

    enum Phase { None, Waiting, Revealing, Lingering }

    readonly float[] _samples = new float[256];
    float _nextSearch;
    AICoach _subscribed;
    OrbState _lastState = (OrbState)(-1);
    bool _placeholderShown;

    Phase _phase;
    string _reply;            // full reply as spoken
    float _replyAt;           // when it arrived
    float _speakStartedAt = -1f;
    bool _spoke;              // VoiceOutput went IsSpeaking for this reply
    bool _audioSynced;        // revealing from the Groq clip position (else: estimated pace)
    AudioClip _clipBefore;    // source.clip when the reply arrived (the new line gets a new clip)
    AudioClip _clip;          // the clip being spoken
    float _estimateFrom;      // time the estimated pace started
    float[] _weights;         // cumulative timing weight per character of _reply
    int _revealed;

    static FieldInfo s_groqField;
    static PropertyInfo s_groqUnavailable;

    void OnDisable() => Unsubscribe();

    void Resolve()
    {
        if (!orb) orb = GetComponent<CoachOrb>();
        // Scene searches are slow: when something is missing, look again at most once a second
        // (so an AI coach spawned later is still picked up, but a scene without one costs nothing).
        bool missing = !orb || !coach || !voiceOutput || !voiceInput;
        if (missing && Time.unscaledTime >= _nextSearch)
        {
            _nextSearch = Time.unscaledTime + 1f;
            if (!orb) orb = FindAnyObjectByType<CoachOrb>();
            if (!coach) coach = FindAnyObjectByType<AICoach>();
            if (!voiceOutput) voiceOutput = coach && coach.voiceOutput ? coach.voiceOutput : FindAnyObjectByType<VoiceOutput>();
            if (!voiceInput) voiceInput = FindAnyObjectByType<VoiceInput>();
        }
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

    void Log(string msg) { if (logChanges) Debug.Log($"[CoachOrbBridge] {Time.time:0.00}s {msg}"); }

    // Raised by AICoach on the main thread, after IsThinking goes false and before VoiceOutput.Speak is called.
    void OnResponse(CoachResult result)
    {
        if (!orb) return;
        if (result == null || string.IsNullOrWhiteSpace(result.say))
        {
            _phase = Phase.None;
            _reply = null;
            if (_placeholderShown) { orb.HideText(); _placeholderShown = false; }
            return;
        }
        _reply = result.say.Trim();
        _replyAt = Time.time;
        _speakStartedAt = -1f;
        _spoke = false;
        _audioSynced = false;
        _clip = null;
        _clipBefore = voiceOutput && voiceOutput.source ? voiceOutput.source.clip : null;
        _weights = null;
        _revealed = 0;
        _phase = Phase.Waiting; // keep "..." up until the voice starts (see UpdateReply)
        Log($"reply{(result.isFallback ? " (fallback)" : "")}: \"{_reply}\"");
    }

    void Update()
    {
        Resolve();
        if (!orb) return;

        bool listening = voiceInput && voiceInput.IsListening;
        bool thinking = coach && coach.IsThinking;
        bool speaking = voiceOutput && voiceOutput.IsSpeaking;

        if (thinking && _phase != Phase.None) { _phase = Phase.None; _reply = null; } // a new request supersedes the old reply

        UpdateReply(speaking);
        bool waitingForVoice = _phase == Phase.Waiting && speaking;

        var state = listening ? OrbState.Listening
                  : thinking || waitingForVoice ? OrbState.Thinking
                  : speaking ? OrbState.Speaking
                  : OrbState.Idle;
        // Only on change, so the orb's own debug keys (Y/V) still work while nothing is happening.
        if (state != _lastState)
        {
            Log($"state {_lastState} -> {state}");
            orb.SetState(state);
            _lastState = state;
        }

        UpdatePlaceholder(thinking || waitingForVoice || (_phase == Phase.Waiting && Time.time - _replyAt < NeverStartedAfter));
        if (state == OrbState.Speaking) orb.SetVoiceLevel(VoiceLevel());

#if UNITY_EDITOR
        DebugKeys();
#endif
    }

    void UpdatePlaceholder(bool want)
    {
        if (want && !_placeholderShown && !string.IsNullOrEmpty(thinkingText))
        {
            Log($"placeholder \"{thinkingText}\"");
            orb.ShowText(thinkingText, KeepUp);
            _placeholderShown = true;
        }
        else if (!want && _placeholderShown)
        {
            _placeholderShown = false;
            if (_phase == Phase.None || _phase == Phase.Waiting) orb.HideText(); // nothing replaced it
        }
    }

    // ---------- Reply text in time with the voice ----------

    void UpdateReply(bool speaking)
    {
        if (_phase == Phase.None || _phase == Phase.Lingering || _reply == null) return;

        if (speaking && !_spoke) { _spoke = true; _speakStartedAt = Time.time; }
        var src = voiceOutput ? voiceOutput.source : null;
        bool newClipPlaying = src && src.isPlaying && src.clip && src.clip != _clipBefore;

        if (_phase == Phase.Waiting)
        {
            if (!_spoke)
            {
                // The coach isn't speaking this line (speakReplies off, no VoiceOutput, TTS failed outright): show it all.
                if (Time.time - _replyAt >= NeverStartedAfter) FinishReply("speech never started");
                return;
            }
            if (!revealWithSpeech) { StartReveal(); FinishRevealNow(); return; }

            if (newClipPlaying) { StartReveal(); _audioSynced = true; _clip = src.clip; Log($"words follow the audio ({_clip.length:0.0}s)"); }
            else if (!GroqExpected() || Time.time - _speakStartedAt > maxAudioWait)
            {
                StartReveal(); _estimateFrom = GroqExpected() ? Time.time : _speakStartedAt;
                Log($"words at estimated pace ({charsPerSecond:0} chars/s)");
            }
            else if (!speaking) { FinishReply("speech ended before audio"); return; }
            if (_phase == Phase.Waiting) return;
        }

        // Revealing
        if (!speaking) { FinishReply("speech ended"); return; }
        if (!revealWithSpeech) return; // whole line already shown
        if (!_audioSynced && newClipPlaying) { _audioSynced = true; _clip = src.clip; Log("audio arrived late, switching to audio timing"); }

        float fraction;
        if (_audioSynced && src && src.clip == _clip && _clip.length > 0.01f)
        {
            float usable = Mathf.Max(0.1f, _clip.length - audioLeadSeconds - audioTailSeconds);
            fraction = src.isPlaying ? Mathf.Clamp01((src.time - audioLeadSeconds) / usable) : 1f;
        }
        else
        {
            float total = _weights[_weights.Length - 1];
            fraction = total > 0f ? Mathf.Clamp01((Time.time - _estimateFrom) * charsPerSecond / total) : 1f;
        }
        Reveal(fraction);
    }

    void StartReveal()
    {
        _phase = Phase.Revealing;
        _placeholderShown = false;
        _weights = BuildWeights(_reply);
        orb.ShowText(_reply, KeepUp);
        _revealed = 0;
        orb.SetRevealedCharacters(0);
    }

    void FinishRevealNow() { orb.SetRevealedCharacters(-1); }

    // fraction of the spoken line (0..1) → reveal whole words up to that point. Never goes backwards.
    void Reveal(float fraction)
    {
        string shown = orb.DisplayedText; // may be truncated with "..." (same prefix as _reply)
        float target = fraction * _weights[_weights.Length - 1];
        int pos = 0;
        while (pos < _weights.Length && _weights[pos] < target) pos++;
        // Show the word that is being spoken now (up to its end), so it appears as it starts.
        int end = pos;
        while (end < _reply.Length && _reply[end] != ' ') end++;
        end = Mathf.Min(end, shown.Length);
        if (end > _revealed)
        {
            _revealed = end;
            orb.SetRevealedCharacters(_revealed);
        }
    }

    float[] BuildWeights(string s)
    {
        var w = new float[Mathf.Max(1, s.Length)];
        float acc = 0f;
        for (int i = 0; i < s.Length; i++)
        {
            acc += 1f;
            char c = s[i];
            bool endsWord = i + 1 >= s.Length || s[i + 1] == ' ';
            if (endsWord && (c == ',' || c == ';' || c == ':')) acc += commaPause;
            else if (endsWord && (c == '.' || c == '!' || c == '?')) acc += sentencePause;
            w[i] = acc;
        }
        return w;
    }

    void FinishReply(string why)
    {
        if (_phase == Phase.Waiting) { orb.ShowText(_reply, KeepUp); _placeholderShown = false; }
        orb.SetRevealedCharacters(-1);
        int words = _reply.Split(' ').Length;
        float readTime = Mathf.Max(orb.minSeconds, words * orb.secondsPerWord) - (Time.time - _replyAt);
        float keep = Mathf.Max(lingerSeconds, readTime);
        Log($"{why}, whole line shown, stays {keep:0.0}s");
        orb.ShowText(_reply, keep); // same text, new duration (no flicker)
        _phase = Phase.Lingering;
    }

    /// <summary>
    /// Whether VoiceOutput will play this line through its AudioSource (Groq) rather than native TTS. Reads VoiceOutput's
    /// private Groq provider by reflection (read-only) to see if it has given up for this session; defaults to the config.
    /// </summary>
    bool GroqExpected()
    {
        if (!voiceOutput) return false;
        var cfg = voiceOutput.config ? voiceOutput.config : AIConfig.Get();
        if (!cfg || cfg.ttsProvider != TtsProviderType.Groq) return false;
        try
        {
            if (s_groqField == null) s_groqField = typeof(VoiceOutput).GetField("groq", BindingFlags.Instance | BindingFlags.NonPublic);
            var groq = s_groqField?.GetValue(voiceOutput);
            if (groq == null) return true; // not created yet: first line of the session
            if (s_groqUnavailable == null) s_groqUnavailable = groq.GetType().GetProperty("Unavailable");
            return !(s_groqUnavailable != null && (bool)s_groqUnavailable.GetValue(groq));
        }
        catch { return true; }
    }

    // ---------- Voice level ----------

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
        // No measurable audio (native TTS): gentle synthetic pulse.
        float t = Time.time;
        return 0.35f + 0.2f * Mathf.Sin(t * 7.3f) * Mathf.Sin(t * 2.1f + 1f);
    }

#if UNITY_EDITOR
    void DebugKeys()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (!debugKeys || kb == null || !coach) return;
        if (kb.gKey.wasPressedThisFrame && !coach.IsThinking)
        {
            Debug.Log("[CoachOrbBridge] G: sending a fake 'run finished' context to the coach.");
            _ = coach.Coach(AIDebugPanel.Improving());
        }
    }
#endif
}
