using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

// Hands-free questions during the lesson: "hey teacher" (WakeWordTrigger), Q key or right-hand pinch-hold (BackupVoiceTrigger)
// -> chime -> record the question -> Groq Whisper -> AICoach.Ask. Never touches the note loop: it only reads the evaluator.
// Triggers are armed only in the lesson while nobody is talking, thinking or listening (plus muteAfterSpeechSeconds after the
// coach stops speaking, so the coach's own voice can't trigger it). Added at runtime to any scene with an AppFlow (see Bootstrap).
// Editor debug keys: Q = ask (BackupVoiceTrigger), F9 = save a mic debug clip.
public class QuestionRouter : MonoBehaviour
{
    [Tooltip("Optional. Defaults to Assets/Resources/AIConfig.asset.")]
    public AIConfig config;

    [Header("Found automatically when empty")]
    public AICoach coach;
    public VoiceOutput voiceOutput;
    public VoiceInput voiceInput;
    public AppFlow appFlow;
    public PerformanceEvaluator evaluator;
    public PianoCalibrator calibrator;

    [Header("Chime")]
    [Range(0f, 1f)] public float chimeVolume = 0.25f;

    // For the orb: recording a question / waiting for its transcript.
    public bool IsListening { get; private set; }
    public bool IsTranscribing { get; private set; }

    static readonly Regex LeadingWakeWord =
        new Regex(@"^\s*(?:(?:hey|hi|okay|ok)[\s,.!?-]*)?teacher\b[\s,.!?:;-]*", RegexOptions.IgnoreCase);

    AIConfig Cfg => config != null ? config : AIConfig.Get();

    WakeWordTrigger _wake;
    BackupVoiceTrigger _backup;
    MicQuestionRecorder _mic;
    readonly List<IVoiceTrigger> _triggers = new List<IVoiceTrigger>();

    IQuestionRecorder _recorder;    // recording the current question
    RunStats _lastRun;
    PerformanceEvaluator _subscribed;
    AudioSource _chimeSource;
    AudioClip _chime;

    bool _armed, _wasSpeaking, _debugClipDone;
    float _lastSpeakingEnd = -100f, _lastTriggerAt = -100f, _listenStartedAt, _wakeStartedAt = -1f;

    // ------------------------------------------------------------------ Bootstrap (no edit to Main.unity)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        TryAdd();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => TryAdd();

    static void TryAdd()
    {
        var cfg = AIConfig.Get();
        if (!cfg.wakeWordEnabled) return;
        if (!FindAnyObjectByType<AppFlow>(FindObjectsInactive.Include)) return;
        if (FindAnyObjectByType<QuestionRouter>(FindObjectsInactive.Include)) return;
        new GameObject("QuestionRouter").AddComponent<QuestionRouter>();
        Debug.Log("[Question] Router added (wake word: " + (WakeWordAvailable ? "on" : "unavailable") +
                  ", key " + (cfg.backupKeyEnabled ? "Q" : "off") + ", pinch-hold: " + (cfg.pinchHoldEnabled ? "on" : "off") + ")");
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    static bool WakeWordAvailable => true;
#else
    static bool WakeWordAvailable => false;
#endif

    // ------------------------------------------------------------------ Setup

    void Awake()
    {
        if (!coach) coach = FindAnyObjectByType<AICoach>(FindObjectsInactive.Include);
        if (!voiceOutput) voiceOutput = coach && coach.voiceOutput ? coach.voiceOutput : FindAnyObjectByType<VoiceOutput>(FindObjectsInactive.Include);
        if (!voiceInput) voiceInput = FindAnyObjectByType<VoiceInput>(FindObjectsInactive.Include);
        if (!appFlow) appFlow = FindAnyObjectByType<AppFlow>(FindObjectsInactive.Include);
        if (!evaluator) evaluator = FindAnyObjectByType<PerformanceEvaluator>(FindObjectsInactive.Include);
        if (!calibrator) calibrator = FindAnyObjectByType<PianoCalibrator>(FindObjectsInactive.Include);

        // The wake word gets its own child: UnitySendMessage finds the receiver by GameObject name.
        _wake = GetComponentInChildren<WakeWordTrigger>(true);
        if (!_wake)
        {
            var go = new GameObject("QuestionRouter_WakeWordReceiver");
            go.transform.SetParent(transform, false);
            _wake = go.AddComponent<WakeWordTrigger>();
        }
        _backup = GetComponent<BackupVoiceTrigger>();
        if (!_backup) _backup = gameObject.AddComponent<BackupVoiceTrigger>();
        if (!_backup.calibrator) _backup.calibrator = calibrator;
        _mic = GetComponent<MicQuestionRecorder>();
        if (!_mic) _mic = gameObject.AddComponent<MicQuestionRecorder>();
        if (!_mic.voiceInput) _mic.voiceInput = voiceInput;
        if (config)
        {
            if (!_wake.config) _wake.config = config;
            if (!_backup.config) _backup.config = config;
            if (!_mic.config) _mic.config = config;
        }

        _triggers.Add(_wake);
        _triggers.Add(_backup);
        foreach (var t in _triggers) t.Triggered += OnTriggered;
        _wake.QuestionRecorded += wav => OnQuestionRecorded(_wake, wav);
        _mic.QuestionRecorded += wav => OnQuestionRecorded(_mic, wav);

        _chimeSource = gameObject.AddComponent<AudioSource>();
        _chimeSource.playOnAwake = false;
        _chimeSource.spatialBlend = 0f;
        _chime = MakeChime();

        // The orb bridge only searches while one of its required references is missing; hand it this router directly.
        var orbBridge = FindAnyObjectByType<CoachOrbBridge>(FindObjectsInactive.Include);
        if (orbBridge && !orbBridge.questionRouter) orbBridge.questionRouter = this;

        if (!coach) Debug.LogWarning("[Question] No AICoach in the scene; questions can't be answered.");
    }

    void OnEnable()
    {
        if (evaluator && _subscribed != evaluator) { evaluator.SongFinished += OnSongFinished; _subscribed = evaluator; }
    }

    void OnDisable()
    {
        if (_subscribed) _subscribed.SongFinished -= OnSongFinished;
        _subscribed = null;
    }

    void OnDestroy()
    {
        if (_chime) Destroy(_chime);
    }

    void OnSongFinished(RunStats stats) => _lastRun = stats;

    // ------------------------------------------------------------------ Arming

    void Update()
    {
        var cfg = Cfg;
        bool speaking = voiceOutput && voiceOutput.IsSpeaking;
        if (_wasSpeaking && !speaking) _lastSpeakingEnd = Time.time;
        _wasSpeaking = speaking;

        bool armed = cfg.wakeWordEnabled
                  && appFlow && appFlow.CurrentStep == AppFlow.Step.Lesson
                  && !(voiceInput && voiceInput.IsListening)
                  && !(coach && coach.IsThinking)
                  && !speaking
                  && Time.time >= _lastSpeakingEnd + cfg.muteAfterSpeechSeconds
                  && !IsListening && !IsTranscribing;
        if (armed != _armed)
        {
            _armed = armed;
            foreach (var t in _triggers) t.SetArmed(armed);
        }

        // A recording that never reports back (plugin stalled) must not block questions forever.
        if (IsListening && Time.time - _listenStartedAt > cfg.maxQuestionSeconds + cfg.questionStartTimeoutSeconds + 4f)
        {
            Debug.LogWarning("[Question] Recording never finished; giving up.");
            _recorder?.Cancel();
            _recorder = null;
            IsListening = false;
        }

        UpdateDebugClip(cfg);
    }

    // ------------------------------------------------------------------ Trigger -> record

    void OnTriggered(IVoiceTrigger trigger)
    {
        var cfg = Cfg;
        bool isWake = ReferenceEquals(trigger, _wake);
        if (!_armed || IsListening || IsTranscribing)
        {
            Debug.Log($"[Question] {trigger.Name} ignored (busy or not armed).");
            if (isWake && !ReferenceEquals(_recorder, _wake)) _wake.Cancel();
            return;
        }
        if (Time.time < _lastTriggerAt + cfg.wakeCooldownSeconds)
        {
            Debug.Log($"[Question] {trigger.Name} ignored (cooldown).");
            if (isWake) _wake.Cancel();
            return;
        }
        _lastTriggerAt = Time.time;

        IsListening = true;
        _listenStartedAt = Time.time;
        PlayChime();

        if (isWake) _recorder = _wake; // the plugin is already recording
        else
        {
            _recorder = _wake.IsRunning ? (IQuestionRecorder)_wake : _mic;
            _recorder.BeginQuestion(); // may report null right away; IsListening is already set
        }
        Debug.Log($"[Question] Listening ({trigger.Name}, recorder: {(ReferenceEquals(_recorder, _wake) ? "plugin" : "Unity mic")}).");
    }

    void OnQuestionRecorded(IQuestionRecorder from, byte[] wav)
    {
        if (!ReferenceEquals(from, _recorder) || !IsListening) return;
        _recorder = null;
        _ = Answer(wav);
    }

    // ------------------------------------------------------------------ Record -> transcribe -> coach

    async Task Answer(byte[] wav)
    {
        var cfg = Cfg;
        try
        {
            IsListening = false;
            if (wav == null) { Debug.Log("[Question] Nothing heard; back to idle."); return; }

            IsTranscribing = true;
            string text = null;
            var keys = await AISecrets.Load();
            if (string.IsNullOrEmpty(keys.GROQ_API_KEY))
            {
                Debug.LogWarning("[Question] No GROQ_API_KEY in StreamingAssets/secrets.json; can't transcribe the question.");
                IsTranscribing = false;
                if (this && coach) coach.SayNow(cfg.questionFallbackLine);
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            text = await new GroqStt(cfg, keys).Transcribe(wav);
            IsTranscribing = false;
            if (!this) return; // destroyed while waiting
            Debug.Log($"[Question] STT {sw.ElapsedMilliseconds} ms -> \"{text}\"");

            string question = StripWakeWord(text);
            if (!coach) return;
            if (string.IsNullOrEmpty(question)) { coach.SayNow(cfg.questionFallbackLine); return; }

            Debug.Log($"[Question] Asking the coach: \"{question}\"");
            await coach.Ask(question, BuildContext());
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Question] Failed: " + e.Message);
        }
        finally
        {
            IsListening = false;
            IsTranscribing = false;
        }
    }

    public static string StripWakeWord(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return LeadingWakeWord.Replace(text.Trim(), "", 1).Trim();
    }

    CoachContext BuildContext()
    {
        var last = coach ? coach.LastContext : null;
        var ctx = last != null && last.song != null ? last.Clone() : FreeplayContext();
        ctx.thisRun = _lastRun;
        ctx.currentBar = evaluator && evaluator.Song != null ? evaluator.CurrentNote?.bar ?? 0 : 0;
        return ctx;
    }

    // Mirrors AppFlow.OnSongFinished (Assets/Scripts/App/AppFlow.cs). Update both together.
    CoachContext FreeplayContext()
    {
        var ctx = new CoachContext
        {
            mode = "freeplay",
            allowedActions = new[] { CoachActionType.None }
        };
        if (!evaluator || evaluator.Song == null) return ctx;

        var source = evaluator.Song;
        var song = new SongData
        {
            id = "wait_mode_fur_elise", name = source.name,
            tempoBpm = Mathf.RoundToInt(source.bpm), beatsPerBar = source.beatsPerBar,
            timeSignature = "3/8"
        };
        var barStarts = new Dictionary<int, float>();
        foreach (var note in source.notes)
        {
            if (!barStarts.ContainsKey(note.bar)) barStarts[note.bar] = note.beat;
            song.notes.Add(new SongNote
            {
                midi = note.midi, bar = note.bar, beat = note.beat - barStarts[note.bar] + 1f,
                beats = note.duration, hand = PianoHand.Right
            });
        }
        SongFormatter.Invalidate(song.id);
        ctx.song = song;
        ctx.currentSection = new SectionInfo { firstBar = 1, lastBar = song.BarCount, tempoPct = 100 };
        return ctx;
    }

    // ------------------------------------------------------------------ Chime

    void PlayChime()
    {
        if (_chimeSource && _chime) _chimeSource.PlayOneShot(_chime, chimeVolume);
    }

    // Two quick sine tones (880 Hz, then 1320 Hz), 0.25 s in all, with short fades so they don't click.
    static AudioClip MakeChime()
    {
        const int rate = 44100;
        const float toneSeconds = 0.125f, fade = 0.01f;
        int toneSamples = Mathf.RoundToInt(rate * toneSeconds);
        var data = new float[toneSamples * 2];
        for (int i = 0; i < data.Length; i++)
        {
            float freq = i < toneSamples ? 880f : 1320f;
            float t = (i % toneSamples) / (float)rate;
            float env = Mathf.Min(1f, t / fade, (toneSeconds - t) / fade);
            data[i] = Mathf.Sin(2f * Mathf.PI * freq * i / rate) * Mathf.Max(0f, env) * 0.5f;
        }
        var clip = AudioClip.Create("QuestionChime", data.Length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ------------------------------------------------------------------ Mic debug clip

    void UpdateDebugClip(AIConfig cfg)
    {
        if (_wake.IsRunning && _wakeStartedAt < 0f) _wakeStartedAt = Time.time;
#if !(UNITY_ANDROID && !UNITY_EDITOR)
        if (_armed && _wakeStartedAt < 0f) _wakeStartedAt = Time.time; // Editor: from the first arming in the lesson
#endif
        if (cfg.saveMicDebugClip && !_debugClipDone && _wakeStartedAt >= 0f && Time.time >= _wakeStartedAt + 3f)
        {
            _debugClipDone = true;
            SaveDebugClip(cfg.micDebugClipSeconds);
        }

#if UNITY_EDITOR
        var kb = Keyboard.current;
        if (kb != null && kb.f9Key.wasPressedThisFrame) SaveDebugClip(cfg.micDebugClipSeconds);
#endif
    }

    void SaveDebugClip(float seconds)
    {
        string path = Path.Combine(Application.persistentDataPath, $"mic_debug_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        Debug.Log("[Question] Mic debug clip -> " + path);
        if (_wake.IsRunning) _wake.SaveDebugClip(path, seconds);
        else _mic.SaveDebugClip(path, seconds);
    }
}
