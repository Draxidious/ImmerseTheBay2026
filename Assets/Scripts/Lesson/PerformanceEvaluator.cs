using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

public enum TimingJudgement { None, Perfect, Good, Early, Late }

/// <summary>How one note of the song was played.</summary>
public class NoteResult
{
    public int index;
    public LessonNote note;
    public int wrongAttempts;      // wrong keys pressed before the right one
    public TimingJudgement timing; // None for the first note (nothing to measure against)
    public float timingErrorMs;    // actual gap minus expected gap since the previous note; negative = early
    public float ratio;            // actual gap / expected gap; 1 = exactly on tempo

    public bool FirstTry => wrongAttempts == 0;
}

/// <summary>
/// Judges a player against a song in "wait mode": the song waits for the correct key, so a wrong key never advances.
///  - Note accuracy: share of notes hit on the first try.
///  - Tempo: the gap between consecutive correct notes is compared with the gap the song asks for at its target tempo.
/// Listens to MidiPianoReader.NoteOn. No AI involved; SongFinished hands a RunStats to whoever wants it (AICoach).
/// Editor debug keys: Space = play the correct note, X = play a wrong note, P = auto-play on/off, Backspace = restart.
/// </summary>
public class PerformanceEvaluator : MonoBehaviour
{
    public MidiPianoReader reader;

    [Header("Song")]
    [Tooltip("Target tempo in beats per minute. For Für Elise one beat is an eighth note.")]
    public float targetBpm = 90f;
    [Tooltip("Song id in the song database (Assets/Resources/SongDB). Empty or unknown = the built-in SongLibrary.FurElise.")]
    public string songId = "fur_elise";
    [Tooltip("Section of that song (its \"sections\" list), right hand. Empty = the whole song.")]
    public string songSection = "A section (bars 1–8)";

    [Header("Timing windows (gap vs. expected gap)")]
    [Tooltip("Within this fraction of the expected gap counts as Perfect (0.15 = 15% fast or slow).")]
    public float perfectTolerance = 0.15f;
    [Tooltip("Within this fraction counts as Good. Beyond it the note is Early or Late.")]
    public float goodTolerance = 0.35f;

    [Header("Score weights")]
    [Range(0f, 1f)] public float accuracyWeight = 0.6f;

    [Tooltip("After the song ends, playing its first note again starts a new attempt once this many seconds have passed.")]
    public float restartDelay = 1.5f;

    public LessonSong Song { get; private set; }
    public int CurrentIndex { get; private set; }
    public bool IsFinished => Song != null && CurrentIndex >= Song.notes.Count;
    public LessonNote CurrentNote => IsFinished ? null : Song.notes[CurrentIndex];
    public IReadOnlyList<NoteResult> Results => _results;
    public int WrongPressCount { get; private set; }
    public RunStats LastMetrics { get; private set; }

    public event Action Restarted;
    public event Action<NoteResult> NoteJudged;          // the correct key was pressed
    public event Action<int, int> WrongNotePlayed;       // expected midi, played midi
    public event Action<RunStats> SongFinished;

    private readonly List<NoteResult> _results = new List<NoteResult>();
    private readonly List<WrongNote> _wrongNotes = new List<WrongNote>();
    private int _wrongAttemptsOnCurrent;
    private double _lastCorrectTime;
    private double _finishedTime;
    private MidiPianoReader _subscribed;

    private void Awake()
    {
        Song = LoadSong();
    }

    // The song database first (Nehemiah's Assets/Scripts/Songs), the built-in Für Elise if it isn't there.
    private LessonSong LoadSong()
    {
        var song = string.IsNullOrEmpty(songId) ? null : LessonSongSource.ForLesson(songId, songSection, targetBpm);
        if (song == null && !string.IsNullOrEmpty(songId))
            Debug.LogWarning($"[Evaluator] \"{songId}\" / \"{songSection}\" not in the song database, using SongLibrary.FurElise.");
        return song ?? SongLibrary.FurElise(targetBpm);
    }

    private void OnEnable()
    {
        if (reader == null) reader = FindAnyObjectByType<MidiPianoReader>();
        if (reader == null)
        {
            Debug.LogWarning("[Evaluator] No MidiPianoReader in the scene; only the Editor debug keys will work.");
            return;
        }
        _subscribed = reader;
        _subscribed.NoteOn += HandleNoteOn;
    }

    private void OnDisable()
    {
        if (_subscribed != null) _subscribed.NoteOn -= HandleNoteOn;
        _subscribed = null;
    }

    private void Start() => Restart();

    /// <summary>Start the song again from the first note and clear the score.</summary>
    public void Restart()
    {
        if (Song == null || !Mathf.Approximately(Song.bpm, targetBpm)) Song = LoadSong();
        CurrentIndex = 0;
        WrongPressCount = 0;
        _wrongAttemptsOnCurrent = 0;
        _results.Clear();
        _wrongNotes.Clear();
        LastMetrics = null;
        Restarted?.Invoke();
    }

    /// <summary>Feed a key press. Called by the MIDI reader; public so tests and debug drivers can call it too.</summary>
    public void HandleNoteOn(int midi, int velocity)
    {
        double now = Time.unscaledTimeAsDouble;

        if (IsFinished)
        {
            if (now - _finishedTime < restartDelay || midi != Song.notes[0].midi) return;
            Restart();
        }

        LessonNote expected = Song.notes[CurrentIndex];
        if (midi != expected.midi)
        {
            WrongPressCount++;
            _wrongAttemptsOnCurrent++;
            _wrongNotes.Add(new WrongNote { bar = expected.bar, expected = expected.midi, played = midi });
            WrongNotePlayed?.Invoke(expected.midi, midi);
            return;
        }

        var result = new NoteResult { index = CurrentIndex, note = expected, wrongAttempts = _wrongAttemptsOnCurrent, ratio = 1f };
        if (CurrentIndex > 0)
        {
            float expectedGap = (expected.beat - Song.notes[CurrentIndex - 1].beat) * 60f / Song.bpm;
            float actualGap = (float)(now - _lastCorrectTime);
            result.ratio = actualGap / expectedGap;
            result.timingErrorMs = (actualGap - expectedGap) * 1000f;
            result.timing = Judge(result.ratio);
        }

        _results.Add(result);
        _lastCorrectTime = now;
        _wrongAttemptsOnCurrent = 0;
        CurrentIndex++;
        NoteJudged?.Invoke(result);

        if (IsFinished)
        {
            _finishedTime = now;
            LastMetrics = BuildMetrics();
            Debug.Log($"[Evaluator] {Song.name} finished: grade {Grade}, accuracy {AccuracyPercent:0}%, timing {TimingPercent:0}%, " +
                      $"tempo {PlayedBpm:0} bpm (target {Song.bpm:0}), {WrongPressCount} wrong presses.");
            SongFinished?.Invoke(LastMetrics);
        }
    }

    private TimingJudgement Judge(float ratio)
    {
        // Symmetric: 15% faster and 15% slower are treated the same.
        float off = ratio >= 1f ? ratio - 1f : 1f / Mathf.Max(ratio, 0.0001f) - 1f;
        if (off <= perfectTolerance) return TimingJudgement.Perfect;
        if (off <= goodTolerance) return TimingJudgement.Good;
        return ratio < 1f ? TimingJudgement.Early : TimingJudgement.Late;
    }

    // ------------------------------------------------------------------ Scores (all 0-100, over the notes played so far)

    /// <summary>Share of notes hit on the first try.</summary>
    public float AccuracyPercent
    {
        get
        {
            if (_results.Count == 0) return 100f;
            int firstTry = 0;
            foreach (var r in _results) if (r.FirstTry) firstTry++;
            return 100f * firstTry / _results.Count;
        }
    }

    /// <summary>How close the gaps between notes were to the target tempo. Perfect = 1, Good = 0.7, Early/Late = 0.25.</summary>
    public float TimingPercent
    {
        get
        {
            float sum = 0f;
            int n = 0;
            foreach (var r in _results)
            {
                if (r.timing == TimingJudgement.None) continue;
                n++;
                sum += r.timing == TimingJudgement.Perfect ? 1f : r.timing == TimingJudgement.Good ? 0.7f : 0.25f;
            }
            return n == 0 ? 100f : 100f * sum / n;
        }
    }

    public float OverallPercent => accuracyWeight * AccuracyPercent + (1f - accuracyWeight) * TimingPercent;

    /// <summary>The tempo the player actually held, in the song's beats per minute. 0 until two notes are played.</summary>
    public float PlayedBpm
    {
        get
        {
            if (_results.Count < 2) return 0f;
            float beats = _results[_results.Count - 1].note.beat - _results[0].note.beat;
            float seconds = 0f;
            for (int i = 1; i < _results.Count; i++)
                seconds += (_results[i].note.beat - _results[i - 1].note.beat) * 60f / Song.bpm * _results[i].ratio;
            return seconds <= 0f ? 0f : beats / seconds * 60f;
        }
    }

    public string Grade
    {
        get
        {
            float s = OverallPercent;
            return s >= 90f ? "A" : s >= 80f ? "B" : s >= 70f ? "C" : s >= 60f ? "D" : "F";
        }
    }

    public int CountTiming(TimingJudgement j)
    {
        int n = 0;
        foreach (var r in _results) if (r.timing == j) n++;
        return n;
    }

    /// <summary>The run summary the AI coach takes: put it in a CoachContext (thisRun) and call AICoach.Coach.</summary>
    public RunStats BuildMetrics()
    {
        var m = new RunStats { accuracy = AccuracyPercent, timing = TimingPercent };
        m.wrongNotes.AddRange(_wrongNotes);

        float played = PlayedBpm;
        if (played > 0f)
        {
            float speed = played / Song.bpm;
            m.tempoRating = speed > 1f + perfectTolerance ? "too_fast" : speed < 1f / (1f + perfectTolerance) ? "too_slow" : "on_tempo";
        }

        var barSum = new Dictionary<int, float>();
        var barCount = new Dictionary<int, int>();
        foreach (var r in _results)
        {
            if (r.timing == TimingJudgement.None) continue;
            barSum.TryGetValue(r.note.bar, out float s);
            barCount.TryGetValue(r.note.bar, out int c);
            barSum[r.note.bar] = s + Mathf.Log(Mathf.Max(r.ratio, 0.0001f), 2f); // log so 2x slow and 2x fast cancel out
            barCount[r.note.bar] = c + 1;
        }

        float limit = Mathf.Log(1f + perfectTolerance, 2f);
        foreach (var kv in barSum)
        {
            float mean = kv.Value / barCount[kv.Key];
            if (mean < -limit) m.rushedBars.Add(kv.Key);
            else if (mean > limit) m.draggedBars.Add(kv.Key);
        }
        m.rushedBars.Sort();
        m.draggedBars.Sort();
        return m;
    }

    // ------------------------------------------------------------------ Editor debug driver (no piano needed)

#if UNITY_EDITOR
    private bool _autoPlay;
    private double _autoNextTime;

    private void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.backspaceKey.wasPressedThisFrame) { _autoPlay = false; Restart(); }
        if (kb.spaceKey.wasPressedThisFrame) PressCorrect();
        if (kb.xKey.wasPressedThisFrame && !IsFinished) HandleNoteOn(CurrentNote.midi + 1, 90);
        if (kb.pKey.wasPressedThisFrame)
        {
            _autoPlay = !_autoPlay;
            _autoNextTime = Time.unscaledTimeAsDouble;
            if (_autoPlay && IsFinished) Restart();
        }

        if (_autoPlay && !IsFinished && Time.unscaledTimeAsDouble >= _autoNextTime)
        {
            int i = CurrentIndex;
            PressCorrect();
            if (IsFinished) { _autoPlay = false; return; }
            // Next press at the target tempo, with a little human wobble.
            float gap = (Song.notes[i + 1].beat - Song.notes[i].beat) * 60f / Song.bpm;
            _autoNextTime += gap * UnityEngine.Random.Range(0.9f, 1.12f);
        }
    }

    private void PressCorrect()
    {
        if (IsFinished) Restart();
        HandleNoteOn(CurrentNote.midi, 90);
    }
#endif
}
