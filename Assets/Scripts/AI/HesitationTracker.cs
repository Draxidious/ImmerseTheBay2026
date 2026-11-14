using System;
using System.Collections.Generic;
using UnityEngine;

// Times how long the student takes on each note and notices when they are stuck.
// The lesson engine calls NoteBecameNext when it highlights a note. Key presses come from the MidiPianoReader
// (found automatically) or from KeyPressed.
public class HesitationTracker : MonoBehaviour
{
    [Tooltip("Optional. Found in the scene if empty. Leave it missing to feed presses through KeyPressed instead.")]
    public MidiPianoReader reader;

    [Header("Stuck trigger")]
    [Tooltip("Waiting this long on one note counts as stuck.")]
    public int stuckAfterMs = 4000;
    [Tooltip("This many wrong keys on one note counts as stuck.")]
    public int stuckAfterMisses = 3;
    [Tooltip("Stuck fires at most once in this many seconds.")]
    public float stuckCooldownSeconds = 10f;

    // Raised when the student is stuck on the current note.
    public event Action<StuckInfo> Stuck;
    // Raised when the correct key is pressed: how long that note took.
    public event Action<Hesitation> NoteResolved;

    // Everything since the last ResetRun. Copy these into CoachContext.thisRun, or call FillRun.
    public readonly List<Hesitation> Hesitations = new List<Hesitation>();
    public readonly List<WrongNote> WrongNotes = new List<WrongNote>();

    public bool IsWaiting { get; private set; }
    public int CurrentNote { get; private set; }
    public int CurrentBar { get; private set; }

    float waitingSince;
    int misses;
    bool timedOut;                       // the time limit already fired for this note
    float lastStuckAt = float.NegativeInfinity;

    void OnEnable()
    {
        if (reader == null) reader = FindAnyObjectByType<MidiPianoReader>();
        if (reader != null) reader.NoteOn += OnNoteOn;
    }

    void OnDisable()
    {
        if (reader != null) reader.NoteOn -= OnNoteOn;
    }

    void OnNoteOn(int note, int velocity) => KeyPressed(note);

    // Call when a note becomes the one the student has to play.
    public void NoteBecameNext(int midiNote, int bar)
    {
        CurrentNote = midiNote;
        CurrentBar = bar;
        waitingSince = Time.realtimeSinceStartup;
        misses = 0;
        timedOut = false;
        IsWaiting = true;
    }

    // Call for every key the student presses (done automatically when a MidiPianoReader is present).
    public void KeyPressed(int midiNote)
    {
        if (!IsWaiting) return;

        if (midiNote == CurrentNote)
        {
            IsWaiting = false;
            var h = new Hesitation { bar = CurrentBar, note = CurrentNote, ms = WaitedMs() };
            Hesitations.Add(h);
            NoteResolved?.Invoke(h);
            return;
        }

        misses++;
        WrongNotes.Add(new WrongNote { bar = CurrentBar, expected = CurrentNote, played = midiNote });
        if (misses >= stuckAfterMisses && FireStuck()) misses = 0; // count a fresh set of misses before firing again
    }

    // Stop timing, for example when the lesson is paused or the coach is talking.
    public void CancelWaiting() => IsWaiting = false;

    // Call at the start of each run through the section.
    public void ResetRun()
    {
        Hesitations.Clear();
        WrongNotes.Clear();
        IsWaiting = false;
    }

    // Copies this run's hesitations and wrong notes into the stats that go to the coach.
    public void FillRun(RunStats run)
    {
        run.hesitations = new List<Hesitation>(Hesitations);
        run.wrongNotes = new List<WrongNote>(WrongNotes);
    }

    void Update()
    {
        if (!IsWaiting || timedOut) return;
        if (WaitedMs() >= stuckAfterMs && FireStuck()) timedOut = true;
    }

    int WaitedMs() => Mathf.RoundToInt((Time.realtimeSinceStartup - waitingSince) * 1000f);

    // False while the cooldown is running; the caller then tries again on the next miss or frame.
    bool FireStuck()
    {
        float now = Time.realtimeSinceStartup;
        if (now - lastStuckAt < stuckCooldownSeconds) return false;
        lastStuckAt = now;

        var info = new StuckInfo { bar = CurrentBar, note = CurrentNote, ms = WaitedMs(), misses = misses };
        Debug.Log($"[AI] Stuck on {SongFormatter.NoteName(info.note)} in bar {info.bar} ({info.ms} ms, {info.misses} misses)");
        Stuck?.Invoke(info);
        return true;
    }
}
