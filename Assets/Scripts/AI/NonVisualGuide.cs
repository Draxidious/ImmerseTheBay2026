using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Non-visual mode in the guided lesson (Main_2): says what the keys would have shown, using only events the lesson sends.
// - Before a run of new bars or a new hand, the notes and fingers follow the coach's line ("Right hand: E, finger five; ...").
//   The lesson waits for the coach to finish speaking, so the count-in comes after them.
// - A wrong key: "No, E: one key to the right."
// - No right key for nonVisualStuckSeconds while a note is waiting: "E, finger five: a white key, right hand, a step down from F."
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
    }

    void OnDisable()
    {
        if (engine)
        {
            engine.OnRunStarted -= OnRunStarted;
            engine.OnNextNote -= OnNextNote;
            engine.OnNotePlayed -= OnNotePlayed;
        }
        if (coach && coach.LineSuffix == (Func<CoachResult, string>)Readout) coach.LineSuffix = null;
    }

    // ------------------------------------------------------------------ Readout before a run

    string Readout(CoachResult result)
    {
        if (!VisualsMode.NonVisual || !engine || engine.State == CurriculumState.Idle || engine.State == CurriculumState.SongComplete) return null;
        var song = engine.State == CurriculumState.WarmupDemo || engine.State == CurriculumState.WarmupPlay ? engine.Warmup : engine.Song;
        var bars = engine.CurrentRange;
        if (song == null || bars.First < 1) return null;
        string key = bars + "/" + engine.Hand;
        if (key == lastReadout) return null; // same bars and hand again: they know them
        string text = ReadNotes(song, bars, engine.Hand, AIConfig.Get().nonVisualReadoutMaxNotes);
        if (text == null) return null;
        lastReadout = key;
        Debug.Log($"[NonVisual] Readout {bars} {engine.Hand}: {text}");
        return text;
    }

    // "Right hand: E, finger five; D sharp, finger four." Both hands: right, then left. Null if there are more than maxNotes.
    public static string ReadNotes(SongData song, BarRange bars, HandMode hand, int maxNotes)
    {
        var parts = new List<string>();
        int count = 0;
        foreach (var h in hand == HandMode.Both ? new[] { HandMode.Right, HandMode.Left } : new[] { hand })
        {
            var notes = CurriculumSongs.Expected(song, bars.First, bars.Last, 100, h);
            if (notes.Count == 0) continue;
            count += notes.Count;
            parts.Add((h == HandMode.Left ? "Left hand: " : "Right hand: ") + string.Join("; ", notes.Select(n => NoteWords(n.midi, n.finger))) + ".");
        }
        return parts.Count == 0 || count > maxNotes ? null : string.Join(" ", parts);
    }

    // "E, finger five", or just "E" with no finger in the song.
    public static string NoteWords(int midi, int finger) =>
        CoachFacts.SpokenNoteName(midi) + (finger >= 1 && finger <= 5 ? ", finger " + Numbers[finger] : "");

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
        string line = $"{NoteWords(note.midi, note.finger)}: {CoachFacts.Where(run.song, note.bar, note.midi)}.";
        if (!coach.SayNow(line)) return; // talking: try again next frame
        hintedIndex = next;
        Debug.Log($"[NonVisual] Stuck on note {next}: {line}");
    }
}
