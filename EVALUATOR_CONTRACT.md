# Evaluator contract

The curriculum (`Assets/Scripts/Curriculum/CurriculumEngine.cs`) applies `COACH_PLAYBOOK.md`: it decides when the student
moves up or down the tempo ladder, adds the next measure, plays measures 1..N through, or repairs a problem block. It makes those decisions from an evaluator's scores. The evaluator sits behind one interface so
that the stub that ships now can be swapped for Jordan's `PerformanceEvaluator` by changing one setting and adding one
small adapter.

All types are in `Assets/Scripts/Curriculum/EvaluatorContract.cs`. MIDI note numbers throughout (middle C = 60).
Bars are 1-based.

## The interface

```csharp
public interface IPerformanceEvaluator
{
    EvaluationResult Evaluate(List<PlayedNote> played, List<ExpectedNote> expected, int startBar, int endBar);
}
```

The engine calls `Evaluate` once, when a run ends. One run is one timed pass through a new measure (Step 1), a
play-through of measures 1..N (Step 2), a repair block with its lead-in (Step 3), or the warm-up scale, at the current
step of the tempo ladder (50%, 75%, 100% by default).

| Argument | What it is |
|---|---|
| `played` | Every key pressed during the run, in order, **including wrong ones**. |
| `expected` | The notes of bars `startBar..endBar`, in playing order. |
| `startBar`, `endBar` | The bars of this run, inclusive. |

Rules:
- Return a `BarResult` for **every** bar from `startBar` to `endBar`, in order, even when no note was played in it.
  (The engine fills in missing bars as perfect, but do not rely on that.)
- Never throw. If it does, the engine logs an error and counts the run as clean so the lesson cannot get stuck.
- Do not keep state between calls. Each run is judged on its own.
- It is called on Unity's main thread and should return within a frame.

## Input types

```csharp
public class PlayedNote
{
    public int midi;
    public int velocity;
    public double time;    // seconds from the moment the run's first note was due (negative = before it)
}

public class ExpectedNote
{
    public int index;      // position in the run, 0 = first note
    public int midi;
    public int bar;        // 1-based bar of the song
    public float beat;     // start, in beats from the start of the song
    public float duration; // in beats
    public int finger;     // 1 = thumb ... 5 = little finger, 0 = not specified
    public PianoHand hand; // the hand that plays it (a run holds one hand's notes, or both hands')
    public string hint;    // optional coaching hint for this note
    public float time;     // seconds from the first note of the run at the current practice tempo: when it is due
}
```

`midi`, `bar`, `beat` and `duration` mean exactly what they mean in `SongNote` on `jordan-branch`
(`Assets/Scripts/Lesson/SongData.cs`): same units, same bar numbering (the Für Elise pickup counts as bar 1), same values
for Für Elise.

**Runs are timed, not wait mode.** The notes come at the practice tempo whether or not the student keeps up.
`ExpectedNote.time` and `PlayedNote.time` are on the same clock (0 = the first note's due time) and `time` is already
scaled for the tempo step, so an evaluator never needs the tempo itself.

**Presses are matched to notes in order, not by due time.** A student who plays every note right but 20% slow is
always behind the clock, and judging each press against its due time would call most of the run missed. So:
- Expected notes and presses are aligned in sequence, like a diff: the alignment keeps as many notes matched as
  possible, a note matches only a press of its own key, and presses are used in order (one press, one note).
- Among equally long alignments, the one whose gaps between presses best follow the written gaps at the student's own
  tempo wins. That is what places repeated pitches (E D# E D# E) and skipped notes correctly.
- Absolute time only anchors the first matched note (within about a beat, at least `matchWindowMs`, of 0). After that
  each note is expected where the previous matched press plus the written gap, scaled by the student's tempo, puts it.
- A press may come any amount later than expected: a student who freezes and then plays on in order gets hits plus a
  hesitation. A press much earlier than expected (more than `matchWindowMs`, plus a quarter of the gap) does not match:
  that would be skipping ahead.
- Notes the student skipped are missed. A frozen student in a timed run often skips notes and comes back in where the
  music is; the notes after that still match.
- Notes due at the same time (a chord) may be pressed in any order; presses within 50 ms of each other count as struck
  together.
- Presses well before the first note (during the count-in) and presses after the last note was matched (playing on) are
  ignored. A second press of the same key within 80 ms is a key bounce and ignored.

This differs from Jordan's current `PerformanceEvaluator`, which waits for the right key and compares gaps.

## Output types

```csharp
public class EvaluationResult
{
    public float overallScore;   // accuracy, 0-1: share of notes right on the first try
    public float timingScore;    // 0-1, informational
    public float tempoRatio;     // played speed / asked speed, 1 = on tempo
    public bool passed;          // informational
    public List<BarResult> bars;
}

public class BarResult
{
    public int barNumber;
    public float score;          // accuracy, 0-1: share of the bar's notes right on the first try
    public float timingScore;    // 0-1, informational
    public float tempoRatio;     // the same tempo measure over this bar
    public int firstTryMisses;   // the bar's notes not right on the first try
    public int missed;           // of firstTryMisses, the notes never played at all; they are not in wrongNotes, and
                                 // the coach is told about them (RunStats.notesMissed / missedBars / nothingPlayed)
    public int rightMisses;      // firstTryMisses split by the hand of the note (they add up to firstTryMisses);
    public int leftMisses;       // the curriculum rates each hand of a measure from these
    public bool passed;          // informational
    public List<WrongNote> wrongNotes;
    public List<Hesitation> hesitations;
}
```

`WrongNote { bar, expected, played }` and `Hesitation { bar, note, ms }` are the coach's existing types from
`Assets/Scripts/AI/CoachContext.cs` (`expected`, `played` and `note` are MIDI numbers). They go to the AI coach as they are.

What each field must mean, because the curriculum decides from them:

- **`score` (accuracy):** notes right on the first try / notes in the bar. A note is right on the first try when it was
  matched (see above) and no wrong key was pressed while it was the next note. `overallScore` is the same over the run.
- **`firstTryMisses`:** the bar's notes that were not right on the first try: missed entirely, or preceded by a wrong
  key while they were next (they count once, however many wrong keys). `score = 1 - firstTryMisses / notes`.
- **`wrongNotes`:** one per wrong key pressed, charged to the note that was next when it was pressed (the note it was
  played instead of). A wrong key followed by the right one is a wrong note and a first-try miss, not a missed note.
  Striking the next note's own key early, or twice, is not a wrong note.
- **`tempoRatio`:** played speed / asked speed. For every two consecutive notes that were both matched and are written
  apart in time, written gap / played gap; the run's value is the median of those, 1 when fewer than 2 pairs could be
  measured. 1 = on tempo, 0.8 = 20% slow, 1.25 = 25% fast. Pairs across a hesitation are left out, so a freeze does not
  read as slow playing. A bar's value is the median over the pairs whose later note is in that bar (1 when there are none).
- **`hesitations`:** a pause before a matched note that is longer than the music asks for, at the student's own tempo:
  the gap from the previous matched press, minus the written gap scaled by `1 / tempoRatio`. `ms` is that excess,
  charged to the first note after the pause. Rests and long notes are part of the written gap, so they never count, and
  steady slow playing has no hesitations. Late starts count too (measured from 0). Reported from `timingZeroCreditMs`
  (300 ms); the engine compares `ms` with `HesitationLimitMs` (1500 at the slowest step, scaled down at faster steps).
- **`timingScore`:** informational; the engine does not decide from it. Mean timing credit over the bar's matched notes,
  measured against where the previous press and the student's tempo put each note (not the absolute due time), with
  `LessonConfig.TimingCredit(errorMs)`: 1 within `timingFullCreditMs` (100 ms), fading to 0 at `timingZeroCreditMs`.
- **`passed`:** `score >= passAccuracy` (0.9); informational, the engine does not read it.

How the engine uses them (thresholds in `LessonConfig`, rules in `COACH_PLAYBOOK.md`):
- **Measure rating:** Clean = every note right on the first try and no hesitation over the limit. Shaky = one wrong
  note or one long pause. Problem = `problemWrongNotes` (2) or more.
- **Tempo rating:** `cfg.RateTempo(tempoRatio)`: On tempo within `tempoTolerance` (15%) of 1, otherwise Too slow
  ("a little faster") or Too fast ("slow down"). The per-bar `tempoRatio` names the rushed and dragged measures.
- **Passed run:** no Problem measure, `overallScore >= passAccuracy` (90%), and On tempo. Below song tempo a run that is
  Too fast but otherwise passes still passes (the student is ahead of the step); at song tempo Too fast fails.
- A new measure is learned at 50%, 75% and 100%; play-throughs of 1..N are at song tempo; Problem measures (or, after
  `shakyPlayThroughsBeforeRepair` failed play-throughs with none, the Shaky ones) are repaired as blocks, with up to
  `leadInMeasures` lead-in measures that are not judged.

## The stub that ships now

`StubEvaluator` (`Assets/Scripts/Curriculum/StubEvaluator.cs`) does exactly the above. The alignment is a small dynamic
program over (note, press of the same key): each matched note is worth more than any timing preference, so it finds the
longest in-order match first and uses timing only to choose between equally long ones. It aligns once at the asked
tempo, measures `tempoRatio`, then aligns again following that tempo. Under 2 ms for a 60-note run.

## Switching to Jordan's evaluator

1. Merge his branch.
2. Write `JordanEvaluatorAdapter : IPerformanceEvaluator` in `Assets/Scripts/Curriculum/`.
3. In `LessonConfig.CreateEvaluator()` replace the `EvaluatorType.Jordan` case with `return new JordanEvaluatorAdapter(this);`.
4. Set **Evaluator = Jordan** on `Assets/Resources/LessonConfig.asset`.

Or Jordan implements `IPerformanceEvaluator` directly on a class of his and the case returns that.

His `PerformanceEvaluator` is a live MonoBehaviour in wait mode that owns the song, while this interface is a pure
function over a finished timed run. The parts that carry over are his timing judgement (`Judge`: Perfect within 15%,
Good within 35%) and his accuracy/timing weighting. An adapter (or a static method he pulls out of his class) can:

```csharp
public class JordanEvaluatorAdapter : IPerformanceEvaluator
{
    readonly LessonConfig cfg;
    public JordanEvaluatorAdapter(LessonConfig cfg) { this.cfg = cfg; }

    public EvaluationResult Evaluate(List<PlayedNote> played, List<ExpectedNote> expected, int startBar, int endBar)
    {
        // 1. Match presses to notes in order, following the student's tempo, as the stub does.
        // 2. Timing per hit from the gap to the previous hit vs. the written gap (his Judge compares gaps already):
        //    Perfect / Good / Early / Late mapped to 0-1, or cfg.TimingCredit(errorMs). Informational.
        // 3. Per bar: score = notes right on the first try / notes, firstTryMisses, tempoRatio.
        //    Keep accuracy and tempo separate; do not blend them with accuracyWeight, the curriculum needs both.
        // 4. Fill wrongNotes and hesitations as described above.
    }
}
```

## Things to sort out at merge time

These are not caused by this contract, but they will show up when `jordan-branch` is merged into `main`:

- **Duplicate class `SongNote`.** `jordan-branch` defines `SongNote` (and `Song`, `SongLibrary`) in
  `Assets/Scripts/Lesson/SongData.cs`; `main` already has a different `SongNote` in `Assets/Scripts/AI/SongData.cs`
  (fields `midi, bar, beat (in the bar), beats, hand, finger`). Both are in the global namespace, so the project will
  not compile until one is renamed. The curriculum only uses the `main` one through `SongData`.
- **`PerformanceMetrics` no longer exists on `main`.** `PerformanceEvaluator.BuildMetrics()` and `SongFinished` use it,
  and `WrongNote` on `main` now has `expected` / `played` as MIDI numbers instead of `expectedNote` / `playedNote`
  strings. Through this interface those are not needed: the adapter returns `EvaluationResult` instead.
- His Editor debug keys (Space, X, P, Backspace) overlap with `AIDebugPanel` (Space = push-to-talk). Do not put both in
  one test scene.
