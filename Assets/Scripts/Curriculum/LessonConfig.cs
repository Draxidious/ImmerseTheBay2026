using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public enum EvaluatorType { Stub, Jordan }

// Every threshold of the curriculum in one place. The asset lives at Assets/Resources/LessonConfig.asset.
[CreateAssetMenu(menuName = "AI Coach/Lesson Config", fileName = "LessonConfig")]
public class LessonConfig : ScriptableObject
{
    [Header("Evaluator")]
    [Tooltip("Stub = simple timed note matching (StubEvaluator). Jordan = his PerformanceEvaluator through an adapter, see EVALUATOR_CONTRACT.md.")]
    public EvaluatorType evaluator = EvaluatorType.Stub;
    [Header("Clean run")]
    [Tooltip("DEMO_MODE: one clean run per step and only the first and last tempo steps, for a quick walk-through.")]
    public bool demoMode;
    [Tooltip("CLEAN_STREAK. Clean runs in a row needed to pass a tempo step.")]
    public int cleanStreak = 2;
    [Tooltip("ACCURACY_THRESHOLD. A clean run hits at least this share of its notes (or has no more than maxSlips slips), 0-1.")]
    [FormerlySerializedAs("passThreshold")]
    [Range(0f, 1f)] public float accuracyThreshold = 0.95f;
    [Tooltip("This many slips (a missed note or a wrong key) are always forgiven, so a short chunk with one slip is still clean.")]
    public int maxSlips = 1;
    [Tooltip("HESITATION_MS at the slowest step: a pause this much longer than the music asks for makes a run not clean. " +
             "Scaled with tempo at faster steps. Notated rests and long notes are part of what the music asks for.")]
    public int hesitationMs = 1500;
    [Header("Timing")]
    [Tooltip("TIMING_THRESHOLD. At the top tempo step a clean run also needs this timing score, 0-1. Lower steps track timing but never fail on it.")]
    [Range(0f, 1f)] public float timingThreshold = 0.7f;
    [Tooltip("A correct note this close to its time gets full timing credit.")]
    public int timingFullCreditMs = 100;
    [Tooltip("Timing credit fades to 0 at this distance from the note's time.")]
    public int timingZeroCreditMs = 300;
    [Tooltip("A key press further than this from a note's time does not count as playing that note.")]
    public int matchWindowMs = 500;

    [Header("Tempo ladder")]
    [Tooltip("TEMPO_STEPS, in percent of the song's tempo, slowest first. Every chunk, drilled bar and the warm-up climbs these. " +
             "Lower steps need accuracy only; the last step also needs timing.")]
    public List<int> tempoSteps = new List<int> { 60, 80, 100 };
    [Tooltip("Failing this many times in a row on a step above the first drops back one step.")]
    public int dropStepAfterFails = 2;
    [Tooltip("Seconds between the start of a run and its first note, so falling notes have time to fall.")]
    public float leadInSeconds = 2f;

    [Header("Song")]
    [Tooltip("How many bars of the song to teach (clamped to the bars the song has).")]
    public int songBarCount = 8;
    [Tooltip("PHRASE_LENGTH. Chunks build up inside phrases of this many bars: [1], [1-2], [1-3], [1-4], [5]...")]
    public int phraseLength = 4;

    [Header("Drill")]
    [Tooltip("DRILL_PASS_COUNT. Clean runs of the drilled bar at the top step needed before going back to the chunk (at least the clean streak).")]
    public int drillPassCount = 1;
    [Tooltip("After this many failed tries the bar is demoed.")]
    public int drillDemoAfterFails = 3;
    [Tooltip("After this many failed tries the bar is marked skipped and the lesson moves on.")]
    public int drillSkipAfterFails = 7;

    [Header("Warm-up")]
    [Tooltip("Full tempo of the scale; the ladder steps are percentages of this.")]
    public int warmupTempoBpm = 80;
    [Tooltip("Replay the scale demo before the next try after every this many failed warm-ups.")]
    public int warmupDemoReplayAfterFails = 2;

    [Header("Pacing")]
    [Tooltip("Before a run, a demo or the song, wait for the coach to finish speaking, at most this long.")]
    public float coachMaxWaitSeconds = 15f;

    // The ladder in use: demo mode keeps only the first and last steps.
    public IReadOnlyList<int> Steps
    {
        get
        {
            var steps = tempoSteps != null && tempoSteps.Count > 0 ? tempoSteps : new List<int> { 100 };
            return demoMode && steps.Count > 2 ? new List<int> { steps[0], steps[steps.Count - 1] } : steps;
        }
    }

    public int CleanStreak => demoMode ? 1 : Mathf.Max(1, cleanStreak);

    // HESITATION_MS is set for the slowest step; at faster steps the same musical pause is shorter.
    public int HesitationLimitMs(int tempoPct) => Mathf.RoundToInt(hesitationMs * Steps[0] / (float)Mathf.Max(1, tempoPct));

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
