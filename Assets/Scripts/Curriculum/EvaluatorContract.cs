using System;
using System.Collections.Generic;

// The contract between the curriculum and whatever judges a run. See EVALUATOR_CONTRACT.md at the repo root.
// Notes are MIDI note numbers (middle C = 60). Bars are 1-based. Field names follow Jordan's SongNote / NoteResult
// (midi, bar, beat, duration) so his evaluator can be adapted with little code.

// One key press, as it came from the piano.
[Serializable]
public class PlayedNote
{
    public int midi;
    public int velocity;
    public double time;    // seconds from the moment the run's first note was due (negative = before it)
}

// One note the student is supposed to play in this run, in order.
[Serializable]
public class ExpectedNote
{
    public int index;      // position in the run, 0 = first note
    public int midi;
    public int bar;        // 1-based bar of the song
    public float beat;     // when the note starts, in beats from the start of the song
    public float duration; // in beats
    public int finger;     // 1 = thumb ... 5 = little finger, 0 = not specified
    public string hint;    // optional, e.g. "tuck your thumb under to reach F"
    public float time;    // seconds from the first note of the run at the current practice tempo: when it is due
}

// How one bar of the run went.
[Serializable]
public class BarResult
{
    public int barNumber;
    public float score;        // accuracy, 0-1: share of the bar's notes that were hit
    public float timingScore;  // 0-1: how close the hit notes landed to their time
    public bool passed;        // the evaluator's own view; the curriculum decides from score and timingScore
    public List<WrongNote> wrongNotes = new List<WrongNote>();   // WrongNote and Hesitation are the coach's types (CoachContext.cs)
    public List<Hesitation> hesitations = new List<Hesitation>();
}

// How the whole run went. bars has one entry per bar from startBar to endBar, in order.
[Serializable]
public class EvaluationResult
{
    public float overallScore; // accuracy, 0-1
    public float timingScore;  // 0-1
    public bool passed;
    public List<BarResult> bars = new List<BarResult>();

    public BarResult Bar(int barNumber) => bars.Find(b => b.barNumber == barNumber);
}

public interface IPerformanceEvaluator
{
    // played: every key pressed during the run, in order, including wrong ones.
    // expected: the notes of bars startBar..endBar, in order.
    // Must not throw. Must return a BarResult for every bar in the range, even one with no notes played.
    EvaluationResult Evaluate(List<PlayedNote> played, List<ExpectedNote> expected, int startBar, int endBar);
}
