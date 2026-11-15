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
// -> chime -> record the question -> Groq Whisper -> AICoach.Ask. Never touches the note loop: it only reads the evaluator (or the guided lesson).
// Triggers are armed only in the lesson, free play or the mode choice, while nobody is talking, thinking or listening (plus
// muteAfterSpeechSeconds after the coach stops speaking, so the coach's own voice can't trigger it). "free play" / "lesson mode"
// switch modes (AppModeSwitch) instead of going to the coach. Added at runtime to any scene with an AppFlow (see Bootstrap).
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
    [Tooltip("Main_2's guided lesson. While it runs, questions use its context instead of the evaluator's.")]
    public CurriculumEngine engine;
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
    CurriculumEngine _subscribedEngine;
    int _engineBar;                 // bar of the guided lesson's note due now; 0 between runs
    bool _confirmAsk;               // "free play" heard in the lesson: ask "switch to free play?" once the coach is free
    bool _confirmPending;           // asked; the next thing heard is the answer
    float _confirmAskedAt;
    const float ConfirmAskWindowSeconds = 40f; // the guided lesson's coach lines can take 20 s to speak
    const float ConfirmTimeoutSeconds = 20f;   // after asking: covers the question's TTS + speech + the answer
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
        if (!engine) engine = FindAnyObjectByType<CurriculumEngine>(FindObjectsInactive.Include);
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
        if (engine && _subscribedEngine != engine) { engine.OnNextNote += OnEngineNote; _subscribedEngine = engine; }
    }

    void OnDisable()
    {
        if (_subscribed) _subscribed.SongFinished -= OnSongFinished;
        _subscribed = null;
        if (_subscribedEngine) _subscribedEngine.OnNextNote -= OnEngineNote;
        _subscribedEngine = null;
    }

    void OnDestroy()
    {
        if (_chime) Destroy(_chime);
    }

    void OnSongFinished(RunStats stats) => _lastRun = stats;
    void OnEngineNote(ExpectedNote note) => _engineBar = note != null ? note.bar : 0;

    // ------------------------------------------------------------------ Arming

    void Update()
    {
        var cfg = Cfg;
        bool speaking = voiceOutput && voiceOutput.IsSpeaking;
        if (_wasSpeaking && !speaking) _lastSpeakingEnd = Time.time;
        _wasSpeaking = speaking;

        bool armed = CanListen(cfg, speaking);
        if (armed != _armed)
        {
            _armed = armed;
            foreach (var t in _triggers) t.SetArmed(armed);
        }

        // Left the lesson some other way (the panel) while asking: there is nothing left to confirm.
        if ((_confirmAsk || _confirmPending) && !(appFlow && appFlow.CurrentStep == AppFlow.Step.Lesson))
            _confirmAsk = _confirmPending = false;

        // The guided lesson's coach may be mid-reply when "free play" is heard; ask as soon as it is done.
        if (_confirmAsk)
        {
            if (Time.time - _confirmAskedAt > ConfirmAskWindowSeconds)
            {
                _confirmAsk = false;
                Debug.Log("[Question] The coach stayed busy; free play not confirmed, staying in the lesson.");
            }
            else if (coach && coach.SayNow(cfg.confirmFreePlayLine))
            {
                _confirmAsk = false;
                _confirmPending = true;
                _confirmAskedAt = Time.time;
            }
        }

        // Waiting for yes / no to "switch to free play?": listen without a wake word once the coach has finished asking.
        if (_confirmPending && !IsListening && !IsTranscribing)
        {
            if (Time.time - _confirmAskedAt > ConfirmTimeoutSeconds)
            {
                _confirmPending = false;
                Debug.Log("[Question] No answer to the free play question; staying in the lesson.");
            }
            // Checked again now: `armed` was worked out before the question above started this frame.
            else if (CanListen(cfg, voiceOutput && voiceOutput.IsSpeaking)) ListenForAnswer();
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

    // In the lesson, or anywhere after the first calibration (choosing a mode, free play; AppFlow sets AfterCalibration).
    bool CanListen(AIConfig cfg, bool speaking) =>
        cfg.wakeWordEnabled
        && appFlow && (appFlow.CurrentStep == AppFlow.Step.Lesson || AppModeSwitch.AfterCalibration)
        && !(voiceInput && voiceInput.IsListening)
        && !(coach && coach.IsThinking)
        && !speaking
        && Time.time >= _lastSpeakingEnd + cfg.muteAfterSpeechSeconds
        && !IsListening && !IsTranscribing;

    // ------------------------------------------------------------------ Trigger -> record

    void OnTriggered(IVoiceTrigger trigger)
    {
        var cfg = Cfg;
        bool isWake = ReferenceEquals(trigger, _wake);
        // Re-check now, not just the per-frame flag: the coach may have started talking earlier this frame.
        if (!_armed || !CanListen(cfg, voiceOutput && voiceOutput.IsSpeaking))
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

    void ListenForAnswer()
    {
        IsListening = true;
        _listenStartedAt = Time.time;
        PlayChime();
        _recorder = _wake.IsRunning ? (IQuestionRecorder)_wake : _mic;
        _recorder.BeginQuestion();
        Debug.Log($"[Question] Listening for yes or no (recorder: {(ReferenceEquals(_recorder, _wake) ? "plugin" : "Unity mic")}).");
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
        bool confirming = _confirmPending;
        _confirmPending = false;
        try
        {
            IsListening = false;
            if (wav == null)
            {
                Debug.Log("[Question] Nothing heard; back to idle.");
                if (confirming && coach) coach.SayNow(cfg.keepLessonLine);
                return;
            }

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
            if (confirming)
            {
                // "yes", "sure", or "free play" again switches; anything else (or nothing) keeps the lesson.
                bool yes = IsYes(question) || (TryParseModeCommand(question, out var again) && again == AppMode.FreePlay);
                Debug.Log($"[Question] Free play? \"{question}\" -> {(yes ? "yes" : "no")}");
                if (yes) AppModeSwitch.Request(AppMode.FreePlay, "voice");
                else coach.SayNow(cfg.keepLessonLine);
                return;
            }
            if (string.IsNullOrEmpty(question)) { coach.SayNow(cfg.questionFallbackLine); return; }

            if (TryParseModeCommand(question, out var mode))
            {
                Debug.Log($"[Question] Mode command \"{question}\" -> {mode}");
                // Leaving a lesson throws its progress away, so ask first. Leaving free play loses nothing.
                if (mode == AppMode.FreePlay && appFlow && appFlow.CurrentStep == AppFlow.Step.Lesson)
                {
                    _confirmAsk = true; // Update asks once the coach is free
                    _confirmAskedAt = Time.time;
                    return;
                }
                AppModeSwitch.Request(mode, "voice");
                return;
            }
            if (IsReplayCommand(question))
            {
                // No model call: the guided lesson plays the current bars again (Main has no demo player).
                bool replayed = engine && engine.ReplayDemo();
                Debug.Log($"[Question] Replay command \"{question}\" -> {(replayed ? "demo" : "not possible now")}");
                if (!replayed) coach.SayNow(cfg.cantReplayLine);
                return;
            }

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

    // "free play", "switch to free play mode", "lesson", "lesson mode", "back to the lesson", ... Case and punctuation are
    // ignored, and the whole question must be the command (so "what is a lesson about" still goes to the coach).
    public static bool TryParseModeCommand(string text, out AppMode mode)
    {
        mode = AppMode.Lesson;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = Regex.Replace(text.ToLowerInvariant(), @"[^a-z\s]", " ");
        t = Regex.Replace(t, @"\bfreeplay\b", "free play");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        t = Regex.Replace(t, @"^(?:(?:please|can we|can you|let s|lets|i want to|i d like to|ok|okay)\s+)*", "");
        t = Regex.Replace(t, @"\s+please$", "");
        var m = ModeCommand.Match(t);
        if (!m.Success) return false;
        mode = m.Groups["free"].Success ? AppMode.FreePlay : AppMode.Lesson;
        return true;
    }

    // "play it again", "can you play that again please", "play this one more time", "show me again". The whole question
    // must be the command, like TryParseModeCommand.
    public static bool IsReplayCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = Regex.Replace(text.ToLowerInvariant(), @"[^a-z\s]", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        t = Regex.Replace(t, @"^(?:(?:please|can you|could you|will you|ok|okay)\s+)*", "");
        t = Regex.Replace(t, @"\s+(?:please|for me)$", "");
        return ReplayCommand.IsMatch(t);
    }

    static readonly Regex ReplayCommand = new Regex(
        @"^(?:play|show me|show)\s+(?:(?:it|that|this)(?:\s+(?:one|bar|part))?\s+)?(?:for me\s+)?(?:again|one more time)$");

    // A yes to "do you want to switch to free play?". Any no-word wins ("okay, no thanks" is a no).
    public static bool IsYes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = Regex.Replace(text.ToLowerInvariant(), @"[^a-z\s]", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (NoWord.IsMatch(t)) return false;
        return YesStart.IsMatch(t);
    }

    static readonly Regex NoWord = new Regex(@"\b(?:no|nope|nah|not|don t|dont|stay|keep|later|wait)\b");
    static readonly Regex YesStart = new Regex(
        @"^(?:yes|yeah|yea|yep|yup|sure|ok|okay|please|do it|go ahead|switch|absolutely|definitely|of course|let s do it|lets do it)\b");

    static readonly Regex ModeCommand = new Regex(
        @"^(?:(?:switch|go|change|move|get|go back)\s+(?:to|into)\s+|back\s+to\s+|start\s+|play\s+)?(?:the\s+)?" +
        @"(?:(?<free>free play)|lesson)(?:\s+mode)?$");

    CoachContext BuildContext()
    {
        var freePlay = FindFreePlay();
        if (freePlay && freePlay.IsActive)
            return new CoachContext
            {
                mode = CoachContext.FreePlayMode,
                freePlayJson = freePlay.FactsJson(true),
                allowedActions = new[] { CoachActionType.None }
            };

        var last = coach ? coach.LastContext : null;
        bool lastIsLesson = last != null && last.song != null && !last.IsFreePlay;

        // Main_2: the guided lesson's own context (its section, the run it just judged); the evaluator is off there.
        if (engine && engine.State != CurriculumState.Idle)
        {
            var guided = lastIsLesson ? last.Clone()
                : new CoachContext { mode = "curriculum", allowedActions = new[] { CoachActionType.None } };
            guided.currentBar = _engineBar > 0 ? _engineBar : engine.CurrentRange.First;
            return guided;
        }

        var ctx = lastIsLesson ? last.Clone() : LessonContext();
        ctx.thisRun = _lastRun;
        ctx.currentBar = evaluator && evaluator.Song != null ? evaluator.CurrentNote?.bar ?? 0 : 0;
        return ctx;
    }

    FreePlayController _freePlay;

    FreePlayController FindFreePlay()
    {
        if (!_freePlay) _freePlay = FindAnyObjectByType<FreePlayController>(FindObjectsInactive.Include);
        return _freePlay;
    }

    // Mirrors AppFlow.OnSongFinished (Assets/Scripts/App/AppFlow.cs). Update both together.
    CoachContext LessonContext()
    {
        var ctx = new CoachContext
        {
            mode = "freeplay",
            allowedActions = new[] { CoachActionType.None }
        };
        if (!evaluator || evaluator.Song == null) return ctx;

        // The song database's section when the evaluator's song is there, otherwise built from the evaluator's own notes.
        var song = LessonSongSource.ForCoach(evaluator.songId, evaluator.songSection);
        if (song == null)
        {
            var source = evaluator.Song;
            song = new SongData
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
        }
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
