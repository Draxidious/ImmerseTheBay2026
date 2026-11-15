using System.Collections.Generic;
using UnityEngine;

public enum EvaluatorType { Stub, Jordan }

// How the speed a run was played at compares with the speed asked for (COACH_PLAYBOOK.md, "Tempo rating").
public enum TempoRating { OnTempo, TooSlow, TooFast }

// Every threshold of the curriculum in one place, following the tuning table of COACH_PLAYBOOK.md.
// The asset lives at Assets/Resources/LessonConfig.asset.
[CreateAssetMenu(menuName = "AI Coach/Lesson Config", fileName = "LessonConfig")]
public class LessonConfig : ScriptableObject
{
    [Header("Evaluator")]
    [Tooltip("Stub = in-order note matching (StubEvaluator). Jordan = his PerformanceEvaluator through an adapter, see EVALUATOR_CONTRACT.md.")]
    public EvaluatorType evaluator = EvaluatorType.Stub;
    [Tooltip("DEMO_MODE: only the first and last tempo steps (50% -> 100%) and the first 4 measures of the song, for a quick walk-through.")]
    public bool demoMode;

    [Header("Passed run")]
    [Tooltip("Pass mark: share of notes right on the first try a passed run needs, 0-1.")]
    [Range(0f, 1f)] public float passAccuracy = 0.9f;
    [Tooltip("A measure with this many wrong notes (or wrong notes plus long pauses) is a Problem. One is Shaky.")]
    public int problemWrongNotes = 2;
    [Tooltip("Long pause at the slowest step: a pause this much longer than the music asks for makes a measure Shaky. " +
             "Scaled with tempo at faster steps. Notated rests and long notes are part of what the music asks for.")]
    public int hesitationMs = 1500;
    [Tooltip("On tempo: the played speed is within this share of the asked speed, 0-1.")]
    [Range(0f, 1f)] public float tempoTolerance = 0.15f;

    [Header("Note matching (evaluator)")]
    [Tooltip("A correct note this close to where it is expected (after following the student's own tempo) gets full timing credit.")]
    public int timingFullCreditMs = 100;
    [Tooltip("Timing credit fades to 0 at this distance. Also the shortest pause worth reporting.")]
    public int timingZeroCreditMs = 300;
    [Tooltip("How far a press may be from where its note is expected, once the student's own tempo is followed, and still count as that note.")]
    public int matchWindowMs = 600;

    [Header("Tempo ladder")]
    [Tooltip("TEMPO_STEPS, in percent of the song's tempo, slowest first. The last step is song tempo.")]
    public List<int> tempoSteps = new List<int> { 50, 75, 100 };
    [Tooltip("Failed runs in a row at one tempo before going down a step (at the slow step: the coach gives help).")]
    public int stepDownAfterFails = 3;
    [Tooltip("Metronome clicks at the run's tempo before its first note (also gives falling notes time to fall).")]
    public int countInBeats = 4;
    [Tooltip("Keep clicking on every beat during the run, at steps up to metronomeMaxTempoPct.")]
    public bool metronomeDuringRun = true;
    [Tooltip("The click plays during runs only at steps at or below this tempo (50 = the slow step only).")]
    public int metronomeMaxTempoPct = 50;
    [Range(0f, 1f)] public float metronomeVolume = 0.6f;
    [Tooltip("Clicks during the run, as a share of the count-in's volume: quieter, so the drop tells the student to play.")]
    [Range(0f, 1f)] public float metronomeRunVolume = 0.3f;

    [Header("Song")]
    [Tooltip("How many measures of the song to teach (clamped to the measures the song has).")]
    public int songBarCount = 8;

    [Header("Play-through and repair")]
    [Tooltip("Failed play-throughs in a row with no Problem measure before the Shaky measures are repaired.")]
    public int shakyPlayThroughsBeforeRepair = 2;
    [Tooltip("Lead-in length: up to this many measures before a repair block, played when one of them was Shaky.")]
    public int leadInMeasures = 2;
    [Tooltip("SKIP_AFTER_FAILS. After this many failed runs on the same measure or block, over the whole lesson, it is set aside and no longer judged.")]
    public int skipAfterFails = 7;

    [Header("Warm-up")]
    [Tooltip("Song tempo of the scale; the ladder steps are percentages of this.")]
    public int warmupTempoBpm = 80;

    [Header("Pacing")]
    [Tooltip("Before a run, a demo or the song, wait for the coach to finish speaking, at most this long.")]
    public float coachMaxWaitSeconds = 45f;
    [Tooltip("A stuck hint (no correct key within the pause limit) is spoken at most this often.")]
    public float stuckHintMinIntervalSeconds = 10f;

    // The ladder in use: demo mode keeps only the first and last steps.
    public IReadOnlyList<int> Steps
    {
        get
        {
            var steps = tempoSteps != null && tempoSteps.Count > 0 ? tempoSteps : new List<int> { 100 };
            return demoMode && steps.Count > 2 ? new List<int> { steps[0], steps[steps.Count - 1] } : steps;
        }
    }

    public int SongBarCount => Mathf.Max(1, demoMode ? Mathf.Min(4, songBarCount) : songBarCount);

    // The long-pause limit is set for the slowest step; at faster steps the same musical pause is shorter.
    public int HesitationLimitMs(int tempoPct) => Mathf.RoundToInt(hesitationMs * Steps[0] / (float)Mathf.Max(1, tempoPct));

    // tempoRatio = played speed / asked speed (EvaluationResult.tempoRatio).
    public TempoRating RateTempo(float tempoRatio)
    {
        if (tempoRatio < 1f - tempoTolerance) return TempoRating.TooSlow;
        if (tempoRatio > 1f + tempoTolerance) return TempoRating.TooFast;
        return TempoRating.OnTempo;
    }

    // 1 within timingFullCreditMs, fading linearly to 0 at timingZeroCreditMs.
    public float TimingCredit(float errorMs)
    {
        float e = Mathf.Abs(errorMs);
        if (e <= timingFullCreditMs) return 1f;
        if (e >= timingZeroCreditMs) return 0f;
        return 1f - (e - timingFullCreditMs) / Mathf.Max(1f, timingZeroCreditMs - timingFullCreditMs);
    }

    public IPerformanceEvaluator CreateEvaluator()
    {
        switch (evaluator)
        {
            // Add the adapter here once his evaluator is merged: case EvaluatorType.Jordan: return new JordanEvaluatorAdapter(this);
            case EvaluatorType.Jordan:
                Debug.LogWarning("[Lesson] Jordan's evaluator is not merged yet, using the stub.");
                return new StubEvaluator(this);
            default:
                return new StubEvaluator(this);
        }
    }

    static LessonConfig cached;

    public static LessonConfig Get()
    {
        if (cached != null) return cached;
        cached = Resources.Load<LessonConfig>("LessonConfig");
        if (cached == null)
        {
            Debug.LogWarning("[Lesson] No Assets/Resources/LessonConfig.asset found, using built-in defaults.");
            cached = CreateInstance<LessonConfig>();
        }
        return cached;
    }
}
