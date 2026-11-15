using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Free play: the player plays anything. Pressed keys light up at once; FreePlayAnalyzer groups the notes into phrases
// (chords, key, steadiness, songs) and after a notable phrase the coach comments (between phrases, never in the note loop).
// AppFlow calls Begin/End when the mode changes (AppModeSwitch.Requested). Added at runtime to any scene with an AppFlow.
public class FreePlayController : MonoBehaviour
{
    [Tooltip("Optional. Defaults to Assets/Resources/AIConfig.asset.")]
    public AIConfig config;

    [Header("Found automatically when empty")]
    public MidiPianoReader reader;
    public PianoOverlay overlay;
    public AICoach coach;
    public VoiceOutput voiceOutput;
    public QuestionRouter questionRouter;
    public PianoCalibrator calibrator;
    [Tooltip("Optional. Added at runtime: the sheet music of a recognized or asked-for song.")]
    public FreePlaySheet sheet;

    public bool IsActive { get; private set; }
    public PhraseAnalysis LastPhrase { get; private set; }
    public FreePlaySession Session => _analyzer?.Session;
    // Raised for every finished phrase (the debug panel shows it).
    public event Action<PhraseAnalysis> PhraseFinished;

    AIConfig Cfg => config != null ? config : AIConfig.Get();

    FreePlayAnalyzer _analyzer;
    MidiPianoReader _subscribed;
    KeyEstimate _lastReportedKey;
    string _lessonSongId;
    float _lastCommentAt = float.NegativeInfinity;
    readonly Dictionary<int, float> _lit = new Dictionary<int, float>(); // held key -> when it was last lit

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
        if (!FindAnyObjectByType<AppFlow>(FindObjectsInactive.Include)) return;
        if (FindAnyObjectByType<FreePlayController>(FindObjectsInactive.Include)) return;
        var go = new GameObject("FreePlay");
        go.AddComponent<FreePlayController>();
        go.AddComponent<ModeChoicePanel>();
#if UNITY_EDITOR
        go.AddComponent<FreePlayDebugPanel>();
#endif
        Debug.Log("[FreePlay] Controller and mode panel added.");
    }

    // ------------------------------------------------------------------ Setup

    void Awake() => FindReferences();

    void FindReferences()
    {
        if (!reader) reader = FindAnyObjectByType<MidiPianoReader>(FindObjectsInactive.Include);
        if (!overlay) overlay = FindAnyObjectByType<PianoOverlay>(FindObjectsInactive.Include);
        if (!coach) coach = FindAnyObjectByType<AICoach>(FindObjectsInactive.Include);
        if (!voiceOutput) voiceOutput = coach && coach.voiceOutput ? coach.voiceOutput : FindAnyObjectByType<VoiceOutput>(FindObjectsInactive.Include);
        if (!questionRouter) questionRouter = FindAnyObjectByType<QuestionRouter>(FindObjectsInactive.Include);
        if (!calibrator) calibrator = FindAnyObjectByType<PianoCalibrator>(FindObjectsInactive.Include);
        if (!sheet) sheet = GetComponent<FreePlaySheet>() ?? gameObject.AddComponent<FreePlaySheet>();
    }

    void OnDisable()
    {
        if (IsActive) End();
    }

    // ------------------------------------------------------------------ Begin / End (AppFlow)

    public void Begin()
    {
        if (IsActive) return;
        FindReferences(); // the router is added by its own bootstrap, maybe after this object
        var cfg = Cfg;
        _analyzer = new FreePlayAnalyzer(cfg.FreePlaySettings(), SongTemplates.FromDatabase());
        LastPhrase = null;
        _lastReportedKey = null;
        _lastCommentAt = float.NegativeInfinity;
        _lessonSongId = FreePlayFacts.LessonSongId();
        _lit.Clear();
        if (overlay) overlay.ClearAll(); // drop the lesson's "next key"

        if (reader)
        {
            reader.NoteOn += OnMidiNoteOn;
            reader.NoteOff += OnMidiNoteOff;
            _subscribed = reader;
        }
        else Debug.LogWarning("[FreePlay] No MidiPianoReader in the scene; only the debug panel can play.");

        IsActive = true;
        Debug.Log($"[FreePlay] Begin (phrase gap {cfg.freePlayPhraseGapSeconds:0.#} s, comment cooldown {cfg.freePlayCommentCooldownSeconds:0} s).");
    }

    public void End()
    {
        if (!IsActive) return;
        IsActive = false;
        if (_subscribed)
        {
            _subscribed.NoteOn -= OnMidiNoteOn;
            _subscribed.NoteOff -= OnMidiNoteOff;
            _subscribed = null;
        }
        if (overlay)
        {
            foreach (var midi in _lit.Keys) overlay.SetKey(midi, KeyState.Off);
            overlay.ClearAll();
        }
        _lit.Clear();
        if (sheet) sheet.Hide();
        Debug.Log("[FreePlay] End.");
    }

    // ------------------------------------------------------------------ Notes in (MIDI and the debug panel)

    void OnMidiNoteOn(int midi, int velocity) => Feed(midi, velocity, true);
    void OnMidiNoteOff(int midi) => Feed(midi, 0, false);

    // Lights the key immediately, then hands the note to the analyzer. Nothing here waits on the AI.
    public void Feed(int midi, int velocity, bool on)
    {
        if (!IsActive || _analyzer == null) return;
        if (on && velocity <= 0) on = false; // running-status note-off
        double now = Time.realtimeSinceStartupAsDouble;
        if (on)
        {
            if (overlay) overlay.SetKey(midi, KeyState.Correct);
            _lit[midi] = Time.time;
            _analyzer.NoteOn(midi, velocity, now);
            if (sheet) sheet.NoteOn(midi);
        }
        else
        {
            if (_lit.Remove(midi) && overlay) overlay.SetKey(midi, KeyState.Off);
            _analyzer.NoteOff(midi, now);
        }
    }

    void Update()
    {
        if (!IsActive || _analyzer == null) return;

        // The overlay's Correct state is a flash that turns itself off; keep held keys lit.
        if (overlay && _lit.Count > 0)
        {
            float refresh = Mathf.Max(0.05f, overlay.flashSeconds * 0.5f);
            List<int> due = null;
            foreach (var kv in _lit)
                if (Time.time - kv.Value >= refresh) (due ??= new List<int>()).Add(kv.Key);
            if (due != null)
                foreach (var midi in due)
                {
                    overlay.SetKey(midi, KeyState.Correct);
                    _lit[midi] = Time.time;
                }
        }

        var phrase = _analyzer.Poll(Time.realtimeSinceStartupAsDouble);
        if (phrase != null) OnPhrase(phrase);
    }

    // ------------------------------------------------------------------ Finished phrase -> coach (outside the note loop)

    void OnPhrase(PhraseAnalysis p)
    {
        LastPhrase = p;
        if (sheet) sheet.OnPhrase(p); // a recognized song opens its sheet music
        var cfg = Cfg;
        bool notable = FreePlayFacts.IsNotable(p, _lastReportedKey, out var reasons);

        string skip = null;
        if (!notable) skip = "nothing notable";
        else if (!coach) skip = "no coach";
        else if (Time.time < _lastCommentAt + cfg.freePlayCommentCooldownSeconds)
            skip = $"cooldown ({_lastCommentAt + cfg.freePlayCommentCooldownSeconds - Time.time:0} s left)";
        else if (coach.IsThinking) skip = "coach thinking";
        else if (voiceOutput && voiceOutput.IsSpeaking) skip = "coach speaking";
        else if (questionRouter && (questionRouter.IsListening || questionRouter.IsTranscribing)) skip = "question in progress";
        else if (calibrator && calibrator.IsCalibrating) skip = "calibrating";

        string why = notable ? " [" + string.Join("; ", reasons) + "]" : "";
        Debug.Log($"[FreePlay] Phrase: {FreePlayFacts.Describe(p)}{why} -> {(skip == null ? "sent to coach" : "not sent: " + skip)}");

        if (skip == null)
        {
            _lastCommentAt = Time.time;
            var facts = FreePlayFacts.Build(p, Session, _lastReportedKey, _lessonSongId); // notable vs the previous key
            if (p.key != null && p.key.IsClear) _lastReportedKey = p.key;
            SendToCoach(new CoachContext
            {
                trigger = CoachTrigger.PhraseEnded,
                mode = CoachContext.FreePlayMode,
                freePlayJson = facts.ToJson(),
                allowedActions = new[] { CoachActionType.None }
            });
        }

        PhraseFinished?.Invoke(p);
    }

    async void SendToCoach(CoachContext ctx)
    {
        try { await coach.Coach(ctx); }
        catch (Exception e) { Debug.LogWarning("[FreePlay] Coach comment failed: " + e.Message); }
    }

    // ------------------------------------------------------------------ Facts for questions

    // forQuestion: the phrase being played right now (or the last one), without "notable". Otherwise the last finished phrase.
    public string FactsJson(bool forQuestion)
    {
        PhraseAnalysis p = LastPhrase;
        bool inProgress = false;
        if (forQuestion && _analyzer != null)
        {
            inProgress = _analyzer.PhraseInProgress;
            p = _analyzer.Peek(Time.realtimeSinceStartupAsDouble) ?? LastPhrase;
        }
        return FreePlayFacts.Build(p, Session, _lastReportedKey, _lessonSongId, inProgress, !forQuestion).ToJson();
    }
}
