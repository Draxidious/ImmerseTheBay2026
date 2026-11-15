# Evaluator contract

The curriculum (`Assets/Scripts/Curriculum/CurriculumEngine.cs`) decides when the student moves on, drills a bar or
repeats the warm-up. It makes those decisions from an evaluator's scores. The evaluator sits behind one interface so
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

The engine calls `Evaluate` once, when a run ends. One run is one timed pass through a chunk (for example bars 1-3),
a drilled bar, or the warm-up scale, at the current step of the tempo ladder (60%, 80%, 100% by default).

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
    public string hint;    // optional coaching hint for this note
    public float time;     // seconds from the first note of the run at the current practice tempo: when it is due
}
```

`midi`, `bar`, `beat` and `duration` mean exactly what they mean in `SongNote` on `jordan-branch`
(`Assets/Scripts/Lesson/SongData.cs`): same units, same bar numbering (the Für Elise pickup counts as bar 1), same values
for Für Elise.

**Runs are timed, not wait mode.** The notes come at the practice tempo whether or not the student keeps up, so a key
press is judged against when its note was due. `ExpectedNote.time` and `PlayedNote.time` are on the same clock
(0 = the first note's due time) and `time` is already scaled for the tempo step, so an evaluator never needs the tempo:
the timing error of a press for note `i` is simply `played.time - expected[i].time`. A missed note has no press at all.
This differs from Jordan's current `PerformanceEvaluator`, which waits for the right key and compares gaps.

## Output types

```csharp
public class EvaluationResult
{
    public float overallScore;   // accuracy, 0-1
    public float timingScore;    // 0-1
    public bool passed;          // informational
    public List<BarResult> bars;
}

public class BarResult
{
    public int barNumber;
    public float score;          // accuracy, 0-1: share of the bar's notes that were hit
    public float timingScore;    // 0-1: how close the hit notes landed to their due time
    public bool passed;          // informational
    public List<WrongNote> wrongNotes;
    public List<Hesitation> hesitations;
}
```

`WrongNote { bar, expected, played }` and `Hesitation { bar, note, ms }` are the coach's existing types from
`Assets/Scripts/AI/CoachContext.cs` (`expected`, `played` and `note` are MIDI numbers). They go to the AI coach as they are.

What each field must mean, because the curriculum decides from them:

- **`score` (accuracy):** hits / notes in the bar. A note is hit when its key was pressed within
  `LessonConfig.matchWindowMs` (500) of its due time.
- **`timingScore`:** the mean timing credit over the bar's **hit** notes (misses already cost accuracy). Credit per note is
  1 when the press is within `timingFullCreditMs` (100 ms) of the due time, fading linearly to 0 at
  `timingZeroCreditMs` (300 ms). Use `LessonConfig.TimingCredit(errorMs)` to get the same curve.
  `overallScore` and `timingScore` on the result are the same over the whole run.
- **`wrongNotes`:** presses that matched no note, charged to the note closest in time.
- **`hesitations`:** silences where notes went by unplayed and that lasted longer than the music's own gaps around them.
  `ms` is how much longer. Measure against the notated gaps, so rests and long notes never count. The engine compares
  `ms` with `HESITATION_MS` (1500 at the slowest step, scaled down at faster steps), so report anything from about 300 ms.
- **`passed`:** your own opinion; the engine does not read it.

How the engine uses them (all in `CurriculumEngine.Judge`, thresholds in `LessonConfig`):
- A run is **clean** when accuracy ≥ 0.95 (or there is at most 1 slip; a slip is a missed note or a wrong key), no
  hesitation is over the limit, and, **only at the top tempo step**, `timingScore` ≥ `timingThreshold` (0.7).
  Lower steps track timing but never fail on it.
- `CLEAN_STREAK` (2) clean runs in a row move up a tempo step, and at the top step pass the chunk or drilled bar.
- A bar with slips, a hesitation, or (at the top step) bar timing below the threshold is a problem bar. At the slowest step
  the first problem bar is drilled on its own.

## The stub that ships now

`StubEvaluator` (`Assets/Scripts/Curriculum/StubEvaluator.cs`) does exactly the above: each expected note in order takes
the first unused press of the same key within the match window; timing credit, wrong notes and hesitations as described.

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
        // 1. Match presses to notes by key and due time (within cfg.matchWindowMs), as the stub does.
        // 2. Timing per hit from played.time - expected.time: his Perfect / Good / Early / Late, mapped to 0-1
        //    (Perfect 1, Good 0.7, Early/Late 0.25, as in his TimingPercent), or cfg.TimingCredit(errorMs).
        // 3. Per bar: score = hits / notes, timingScore = mean timing value over the hits.
        //    Keep timing separate from accuracy; do not blend them with accuracyWeight, the curriculum needs both.
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
