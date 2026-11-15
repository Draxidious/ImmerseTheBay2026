using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Non-visual mode in the guided lesson (Main_2): says what the keys would have shown, using only events the lesson sends.
// - Before a run of new bars or a new hand, the notes follow the coach's line, with the finger on each bar's first note
//   ("Right hand: E, finger five, D sharp, E, ..."), played from cached word clips (SpokenWords) so it is quick. The lesson
//   waits for the coach to finish speaking, so the count-in comes after them.
// - A wrong key: "No, E: one key to the right."
// - No right key for nonVisualStuckSeconds while a note is waiting: "E, finger five: the white key just right of the two
//   black keys."
// Silent while the visuals are on, and in Main (no CurriculumEngine). Adds itself to scenes with a CurriculumEngine.
public class NonVisualGuide : MonoBehaviour
{
    CurriculumEngine engine;
    AICoach coach;
    string lastReadout;     // bars and hand of the last run read out
    RunInfo run;
    bool[] done;
    bool running;
    double lastPress;
    int hintedIndex = -1;
    float lastWrongAt = float.NegativeInfinity;

    static readonly string[] Numbers = { "zero", "one", "two", "three", "four", "five" };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AddToScene()
    {
        if (FindAnyObjectByType<NonVisualGuide>(FindObjectsInactive.Include)) return;
        if (!FindAnyObjectByType<CurriculumEngine>(FindObjectsInactive.Include)) return;
        new GameObject("NonVisualGuide").AddComponent<NonVisualGuide>();
    }

    void Awake()
    {
        engine = FindAnyObjectByType<CurriculumEngine>(FindObjectsInactive.Include);
        coach = FindAnyObjectByType<AICoach>(FindObjectsInactive.Include);
    }

    void OnEnable()
    {
        if (engine)
        {
            engine.OnRunStarted += OnRunStarted;
            engine.OnNextNote += OnNextNote;
            engine.OnNotePlayed += OnNotePlayed;
        }
        if (coach) coach.LineSuffix = Readout;
        VisualsMode.Changed += OnVisualsChanged;
    }

    void Start()
    {
        if (VisualsMode.NonVisual) _ = SpokenWords.Preload();
    }

    void OnVisualsChanged(bool nonVisual, string source)
    {
        if (nonVisual) _ = SpokenWords.Preload();
    }

    void OnDisable()
    {
        if (engine)
        {
            engine.OnRunStarted -= OnRunStarted;
            engine.OnNextNote -= OnNextNote;
            engine.OnNotePlayed -= OnNotePlayed;
        }
        if (coach && coach.LineSuffix == (Func<CoachResult, (string, IReadOnlyList<AudioClip>)>)Readout) coach.LineSuffix = null;
        VisualsMode.Changed -= OnVisualsChanged;
    }

    // ------------------------------------------------------------------ Readout before a run

    // Cached word clips when they are all there (fast), otherwise the same words as text for TTS.
    (string, IReadOnlyList<AudioClip>) Readout(CoachResult result)
    {
        if (!VisualsMode.NonVisual || !engine || engine.State == CurriculumState.Idle || engine.State == CurriculumState.SongComplete) return default;
        var song = engine.State == CurriculumState.WarmupDemo || engine.State == CurriculumState.WarmupPlay ? engine.Warmup : engine.Song;
        var bars = engine.CurrentRange;
        if (song == null || bars.First < 1) return default;
        string key = bars + "/" + engine.Hand;
        if (key == lastReadout) return default; // same bars and hand again: they know them
        var groups = ReadWords(song, bars, engine.Hand, AIConfig.Get().nonVisualReadoutMaxNotes);
        if (groups == null) return default;
        lastReadout = key;
        string text = ToText(groups);
        // One hand, and the coach's sentence already names it ("Bar one, right hand, slowly."): skip the hand word.
        string line = AICoach.KeepWholeSentences(result.say ?? "", 1);
        bool handSaid = groups.Count == 1 && line.IndexOf(groups[0][0], StringComparison.OrdinalIgnoreCase) >= 0;
        var clips = SpokenWords.Get(groups.SelectMany(g => handSaid ? g.Skip(1) : g));
        Debug.Log($"[NonVisual] Readout {bars} {engine.Hand} ({(clips != null ? "cached clips" : "TTS")}): {text}");
        return clips != null ? (null, clips) : (text, null);
    }

    // One word list per hand: the hand, each note, and the finger after the first note of each bar.
    // Both hands: right, then left. Null if there is nothing to read or more than maxNotes notes.
    public static List<List<string>> ReadWords(SongData song, BarRange bars, HandMode hand, int maxNotes)
    {
        var groups = new List<List<string>>();
        int count = 0;
        foreach (var h in hand == HandMode.Both ? new[] { HandMode.Right, HandMode.Left } : new[] { hand })
        {
            var notes = CurriculumSongs.Expected(song, bars.First, bars.Last, 100, h);
            if (notes.Count == 0) continue;
            count += notes.Count;
            var words = new List<string> { SpokenWords.HandWords[h == HandMode.Left ? 1 : 0] };
            for (int i = 0; i < notes.Count; i++)
            {
                words.Add(CoachFacts.SpokenNoteName(notes[i].midi));
                bool firstInBar = i == 0 || notes[i - 1].bar != notes[i].bar;
                if (firstInBar && notes[i].finger >= 1 && notes[i].finger <= 5) words.Add(FingerWords(notes[i].finger));
            }
            groups.Add(words);
        }
        return groups.Count == 0 || count > maxNotes ? null : groups;
    }

    // "Right hand: E, finger five, D sharp, E. Left hand: A, finger five, E."
    public static string ToText(List<List<string>> groups) =>
        string.Join(" ", groups.Select(g => g[0] + ": " + string.Join(", ", g.Skip(1)) + "."));

    public static string ReadNotes(SongData song, BarRange bars, HandMode hand, int maxNotes)
    {
        var groups = ReadWords(song, bars, hand, maxNotes);
        return groups == null ? null : ToText(groups);
    }

    public static string FingerWords(int finger) => "finger " + Numbers[Mathf.Clamp(finger, 0, 5)];

    // "E, finger five", or just "E" with no finger in the song.
    public static string NoteWords(int midi, int finger) =>
        CoachFacts.SpokenNoteName(midi) + (finger >= 1 && finger <= 5 ? ", " + FingerWords(finger) : "");

    // Where the key is, from the black-key groups: "the white key just right of the two black keys".
    public static string Landmark(int midi)
    {
        switch (((midi % 12) + 12) % 12)
        {
            case 0: return "the white key just left of the two black keys";
            case 1: return "the left one of the two black keys";
            case 2: return "the white key between the two black keys";
            case 3: return "the right one of the two black keys";
            case 4: return "the white key just right of the two black keys";
            case 5: return "the white key just left of the three black keys";
            case 6: return "the first of the three black keys";
            case 7: return "the white key between the first two of the three black keys";
            case 8: return "the middle one of the three black keys";
            case 9: return "the white key between the last two of the three black keys";
            case 10: return "the last of the three black keys";
            default: return "the white key just right of the three black keys";
        }
    }

    // From the key played to the right one: "one key to the right", "an octave lower", "higher up".
    public static string Direction(int played, int target)
    {
        int d = target - played, n = Math.Abs(d);
        if (n == 0) return "";
        if (n <= 4) return $"{Numbers[n]} key{(n > 1 ? "s" : "")} to the {(d > 0 ? "right" : "left")}";
        if (n == 12) return d > 0 ? "an octave higher" : "an octave lower";
        return d > 0 ? "higher up" : "lower down";
    }

    // ------------------------------------------------------------------ During a run

    void OnRunStarted(RunInfo r)
    {
        run = r;
        done = new bool[r.notes.Count];
        running = true;
        lastPress = r.goTime;
        hintedIndex = -1;
    }

    void OnNextNote(ExpectedNote n)
    {
        if (n == null) running = false; // the run is over (or a demo interrupted it)
    }

    void OnNotePlayed(ExpectedNote note, int key, bool correct)
    {
        lastPress = Time.unscaledTimeAsDouble;
        if (correct)
        {
            if (done != null && note.index >= 0 && note.index < done.Length) done[note.index] = true;
            return;
        }
        if (!VisualsMode.NonVisual || !coach || Time.unscaledTime - lastWrongAt < AIConfig.Get().nonVisualWrongCooldownSeconds) return;
        string line = $"No, {CoachFacts.SpokenNoteName(note.midi)}: {Direction(key, note.midi)}.";
        if (coach.SayNow(line))
        {
            lastWrongAt = Time.unscaledTime;
            Debug.Log($"[NonVisual] Wrong key {key}: {line}");
        }
    }

    void Update()
    {
        if (!VisualsMode.NonVisual || !running || run == null || done == null || !coach) return;
        int next = Array.IndexOf(done, false);
        if (next < 0 || next == hintedIndex) return;
        var note = run.notes[next];
        double now = Time.unscaledTimeAsDouble;
        // Waiting since the note was due, or since the last key if they are behind the clock.
        if (now - Math.Max(lastPress, run.startTime + note.time) < AIConfig.Get().nonVisualStuckSeconds) return;
        string line = $"{NoteWords(note.midi, note.finger)}: {Landmark(note.midi)}.";
        if (!coach.SayNow(line)) return; // talking: try again next frame
        hintedIndex = next;
        Debug.Log($"[NonVisual] Stuck on note {next}: {line}");
    }
}
