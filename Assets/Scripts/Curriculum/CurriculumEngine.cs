using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public enum CurriculumState { Idle, WarmupDemo, WarmupPlay, SongIntro, SongLearn, SongPlayThrough, SongRepair, SongComplete }

// A range of bars, 1-based and inclusive.
public readonly struct BarRange
{
    public readonly int First;
    public readonly int Last;

    public BarRange(int first, int last)
    {
        First = first;
        Last = last;
    }

    public bool Contains(int bar) => bar >= First && bar <= Last;
    public override string ToString() => First == Last ? First.ToString() : First + "-" + Last;
}

// One timed run, for drawing falling notes: every note is due at startTime + note.time (Time.unscaledTimeAsDouble).
public class RunInfo
{
    public SongData song;
    public BarRange bars;
    public int tempoPct;
    public HandMode hand;      // which hand(s) this run is played with; notes holds only their notes
    public float secondsPerBeat;
    public double startTime;
    public List<ExpectedNote> notes;
}

// The guided lesson, following COACH_PLAYBOOK.md: a C major scale warm-up, then the song built up one measure at a time.
// Every decision about progression is made here from the evaluator's results. The coach is told what happened and
// what comes next so it can word it; it may ask for a demo or a fingering, nothing else.
//
// Every run is rated: each judged measure Clean / Shaky (one wrong note or one long pause) / Problem (two or more), and
// the run's speed On tempo / Too slow / Too fast. A run passes with no Problem measure, passAccuracy of the notes right
// on the first try, and On tempo (Too fast is fine below song tempo). The tempo ladder is LessonConfig.Steps.
//   Step 1 (SongLearn): measure N alone in up to three stages: right hand, left hand, both hands (a hand with no notes
//     in the measure is skipped, and then so is "both"). Each stage goes slow -> medium -> full speed, one passed run per
//     step. A fail repeats the step; 3 in a row drop a step, or at the slow step the coach (or the app) gives help.
//     Measure 1 goes straight to Step 4.
//   Step 2 (SongPlayThrough): measures 1..N, both hands, at full speed. Every measure is rated per hand. Passed -> Step 4. Problem measures -> Step 3. Failed with no
//     Problem measure: repeat, and after shakyPlayThroughsBeforeRepair of those the Shaky measures go to Step 3.
//   Step 3 (SongRepair): neighbouring weak measures form blocks, earliest first, each played (with up to two lead-in
//     measures if one of them was Shaky; lead-ins are not judged) by each weak hand alone, slow then full speed, and
//     then by both hands at full speed. Then back to Step 2.
//   Step 4: N + 1 and back to Step 1, or the piece is complete.
// Escape hatch: a measure, block or the warm-up that fails skipAfterFails runs over the lesson is set aside, no longer judged.
//
// Runs are timed: notes are due at the current tempo (OnRunStarted / OnNextNote) whether or not the student keeps up.
// Keys come from MidiPianoReader.NoteOn (found automatically) or HandleNoteOn.
public class CurriculumEngine : MonoBehaviour
{
    static readonly CoachActionType[] CoachMayAsk = { CoachActionType.None, CoachActionType.Demo, CoachActionType.ShowFingering };

    [Tooltip("Optional. Defaults to Assets/Resources/LessonConfig.asset.")]
    public LessonConfig config;
    [Tooltip("Optional. Uses the AICoach on this GameObject. Without one the lesson runs silently.")]
    public AICoach coach;
    [Tooltip("Optional. Uses (or adds) a DemoPlayer on this GameObject.")]
    public DemoPlayer demoPlayer;
    [Tooltip("Optional. Found in the scene if empty.")]
    public MidiPianoReader reader;
    [Tooltip("Go straight to the song, without the C major scale warm-up.")]
    public bool skipWarmup;
    [Tooltip("Start the curriculum on Start. Otherwise call StartLesson.")]
    public bool autoStart;
    [Tooltip("Send transitions to the coach. Turn off to walk the curriculum without API calls.")]
    public bool sendToCoach = true;

    // ------------------------------------------------------------------ Events for the overlay and UI

    public event Action<CurriculumState> OnStateChanged;
    public event Action<int> OnMeasureStarted;                 // Step 1: the measure being learned
    public event Action<BarRange> OnPlayThroughStarted;        // Step 2: measures 1..N
    public event Action<BarRange, BarRange> OnRepairStarted;   // Step 3: the range played (with lead-in), the judged block
    public event Action<SongData> OnSongIntro;
    public event Action<RunInfo> OnRunStarted;                 // all notes of the run with their due times, for falling notes
    public event Action<ExpectedNote> OnNextNote;              // this note is due now (light the key); null when the run is over
    public event Action<ExpectedNote, int, bool> OnNotePlayed; // the note the key matched (or the nearest one), key, correct
    public event Action<int, int> OnTempoChanged;              // tempo %, 1-based step
    public event Action<ExpectedNote> OnDemoNoteDown;          // a demo is sounding this note (light the key, show the finger)
    public event Action<ExpectedNote> OnDemoNoteUp;
    public event Action<int, int[]> OnShowFingering;           // the coach asked: bar, one finger per note (1 = thumb)
    public event Action<EvaluationResult> OnRunEvaluated;
    // After OnRunEvaluated, once the run is rated: LastRatings, LastRightRatings, LastLeftRatings, LastTempoRating and
    // LastPassed describe it. The argument is the bars that were judged (a repair's lead-in is not).
    public event Action<BarRange> OnRunRated;

    // ------------------------------------------------------------------ State

    public CurriculumState State { get; private set; } = CurriculumState.Idle;
    public SongData Warmup { get; private set; }
    public SongData Song { get; private set; }
    public int Measure { get; private set; }                 // N: the measure being learned; play-throughs and repairs are of 1..N
    public int MeasureCount { get; private set; }
    public BarRange CurrentRange { get; private set; }       // what the current or next run plays, lead-in included
    public BarRange CurrentBlock { get; private set; }       // repair: the judged block
    public int LeadInFirstBar { get; private set; }          // repair: first lead-in measure, 0 = the block is played alone
    public HandMode Hand { get; private set; }               // which hand(s) the current or next run is played with
    public IReadOnlyList<HandMode> Stages => stages;         // hand stages of the measure being learned or the block being repaired
    public int StageIndex { get; private set; }
    public IReadOnlyList<BarRange> Blocks => blocks;
    public int BlockIndex { get; private set; } = -1;
    public int TempoStep { get; private set; }               // 0-based index into the ladder
    public int TempoPct => Steps[Mathf.Clamp(TempoStep, 0, Steps.Count - 1)];
    public int TempoStepCount => Steps.Count;
    public bool AtTopStep => TempoStep >= Steps.Count - 1;
    public int StepFails { get; private set; }               // failed runs in a row at this step
    public IReadOnlyCollection<int> SkippedBars => skipped;
    public EvaluationResult LastResult { get; private set; }
    public TempoRating LastTempoRating { get; private set; }
    public IReadOnlyDictionary<int, string> LastRatings => lastRatings; // bar -> "clean" | "shaky" | "problem", judged bars only
    public IReadOnlyDictionary<int, string> LastRightRatings => lastRight; // the same per hand; a bar's rating is the worse of the two
    public IReadOnlyDictionary<int, string> LastLeftRatings => lastLeft;
    public bool LastPassed { get; private set; }
    public float LastAccuracy { get; private set; } = 1f;    // 0-1, first try, over the judged bars of the last run
    public LessonStats Stats { get; private set; } = new LessonStats();
    public bool IsRunning => running;
    // Set by StartLesson from LessonConfig; replace it after StartLesson to try another evaluator.
    public IPerformanceEvaluator Evaluator { get => evaluator; set => evaluator = value; }
    public int EvaluatorErrors { get; private set; }       // runs this lesson the evaluator threw or left bars out
    public string LastEvaluatorError { get; private set; }
    public bool IsDemoPlaying => demoPlayer != null && demoPlayer.IsPlaying;
    public RunInfo CurrentRun { get; private set; }
    public IReadOnlyList<double> Clicks => clicks;           // this run's metronome clicks, in Time.unscaledTimeAsDouble

    LessonConfig Cfg => config != null ? config : LessonConfig.Get();
    IReadOnlyList<int> Steps => Cfg.Steps;
    int TopStep => Steps.Count - 1;
    int RunPct => Steps[Mathf.Clamp(runStep, 0, Steps.Count - 1)];
    SongData CurrentSong => State == CurriculumState.WarmupDemo || State == CurriculumState.WarmupPlay ? Warmup : Song;

    readonly List<BarRange> blocks = new List<BarRange>();
    readonly SortedSet<int> skipped = new SortedSet<int>();
    readonly Dictionary<string, List<float>> trends = new Dictionary<string, List<float>>();
    readonly Dictionary<string, int> failsByKey = new Dictionary<string, int>(); // escape hatch, over the whole lesson
    Dictionary<int, string> lastRatings = new Dictionary<int, string>();
    Dictionary<int, string> playThroughRatings = new Dictionary<int, string>(); // the last play-through, for lead-ins
    Dictionary<int, string> lastRight = new Dictionary<int, string>(), lastLeft = new Dictionary<int, string>();
    Dictionary<int, string> playThroughRight = new Dictionary<int, string>(), playThroughLeft = new Dictionary<int, string>(); // for weak hands
    readonly List<HandMode> stages = new List<HandMode>();
    IPerformanceEvaluator evaluator;
    int attempt;           // tries at the current warm-up / measure / play-through / block
    int playThroughAttempts; // play-throughs of 1..N, repairs in between included
    int warmupAttempts;
    int shakyPlayThroughs; // failed play-throughs in a row with no Problem measure
    float startedAt;
    Metronome metronome;
    CoachContext lastRunContext; // the last evaluated run, for "how did I do"

    // Rule 5 help: did the coach answer the Help trigger with a demo or a fingering?
    bool helpWaiting, helpGiven;

    // Stuck hints: the last correct press of this run, and the last hint of the lesson.
    double lastCorrectTime;
    int lastCorrectIndex;
    bool stallHinted;
    float lastHintAt = float.NegativeInfinity;

    // The run in progress.
    SongData runSong;
    BarRange runBars;
    HandMode runHand;
    bool running;
    double runStart, runEnd;   // runEnd is relative to runStart
    int runStep;               // the tempo step the run is played at
    int dueIndex;
    readonly List<double> clicks = new List<double>(); // metronome clicks, in Time.unscaledTimeAsDouble
    int clickIndex;
    List<ExpectedNote> expected = new List<ExpectedNote>();
    bool[] claimed = new bool[0];
    readonly List<PlayedNote> played = new List<PlayedNote>();

    // A step waiting for the coach or a demo to finish. FastForward runs it now.
    Coroutine waitRoutine;
    Action waitThen;
    Coroutine coachDemoRoutine;
    Action coachDemoResume;
    BarRange lastDemo;
    float lastDemoEnded = float.NegativeInfinity;

    // Coach requests go out one at a time, in order. If the student plays faster than the coach talks, only the
    // newest MaxPendingCoach wait (so a warm-up result is still spoken before the song intro).
    const int MaxPendingCoach = 2;
    readonly Queue<CoachContext> pendingCoach = new Queue<CoachContext>();
    bool coachLoopRunning;

    void Awake()
    {
        if (coach == null) coach = GetComponent<AICoach>();
        if (coach == null) coach = FindAnyObjectByType<AICoach>();
        if (demoPlayer == null) demoPlayer = GetComponent<DemoPlayer>();
        if (demoPlayer == null) demoPlayer = gameObject.AddComponent<DemoPlayer>();
        metronome = GetComponent<Metronome>();
        if (metronome == null) metronome = gameObject.AddComponent<Metronome>();
    }

    void OnEnable()
    {
        if (reader == null) reader = FindAnyObjectByType<MidiPianoReader>();
        if (reader != null) reader.NoteOn += HandleNoteOn;
        if (coach != null) coach.OnCoachAction += HandleCoachAction;
        demoPlayer.NoteDown += ForwardDemoDown;
        demoPlayer.NoteUp += ForwardDemoUp;
    }

    void OnDisable()
    {
        if (reader != null) reader.NoteOn -= HandleNoteOn;
        if (coach != null) coach.OnCoachAction -= HandleCoachAction;
        demoPlayer.NoteDown -= ForwardDemoDown;
        demoPlayer.NoteUp -= ForwardDemoUp;
    }

    void Start()
    {
        if (autoStart) StartLesson();
    }

    void ForwardDemoDown(ExpectedNote n) => OnDemoNoteDown?.Invoke(n);
    void ForwardDemoUp(ExpectedNote n) => OnDemoNoteUp?.Invoke(n);

    // ------------------------------------------------------------------ Public control

    public void StartLesson(LessonMode mode = LessonMode.Curriculum)
    {
        if (mode == LessonMode.FreePlay)
        {
            Debug.LogWarning("[Lesson] Free play is not built yet (FreePlayMode).");
            return;
        }

        StopAll();
        var cfg = Cfg;
        evaluator = cfg.CreateEvaluator();
        Warmup = CurriculumSongs.CMajorScale(cfg.warmupTempoBpm);
        Song = CurriculumSongs.FurElise(cfg.SongBarCount);
        if (cfg.SongBarCount > Song.BarCount)
            Debug.LogWarning($"[Lesson] songBarCount is {cfg.SongBarCount} but {Song.name} only has {Song.BarCount} bars written out.");
        MeasureCount = Mathf.Max(1, Mathf.Min(cfg.SongBarCount, Song.BarCount));

        skipped.Clear();
        trends.Clear();
        failsByKey.Clear();
        blocks.Clear();
        lastRatings = new Dictionary<int, string>();
        playThroughRatings = new Dictionary<int, string>();
        lastRight = new Dictionary<int, string>();
        lastLeft = new Dictionary<int, string>();
        playThroughRight = new Dictionary<int, string>();
        playThroughLeft = new Dictionary<int, string>();
        stages.Clear();
        StageIndex = 0;
        Hand = HandMode.Right;
        Stats = new LessonStats();
        warmupAttempts = attempt = playThroughAttempts = shakyPlayThroughs = 0;
        Measure = 0;
        BlockIndex = -1;
        CurrentBlock = default;
        LeadInFirstBar = 0;
        CurrentRange = new BarRange(1, Warmup.BarCount);
        SetStep(0);
        startedAt = Time.time;
        lastHintAt = float.NegativeInfinity;
        LastResult = null;
        LastPassed = false;
        LastTempoRating = TempoRating.OnTempo;
        lastRunContext = null;
        helpWaiting = helpGiven = false;
        EvaluatorErrors = 0;
        LastEvaluatorError = null;

        Debug.Log($"[Lesson] Starting. Tempo steps [{string.Join(", ", Steps)}], {MeasureCount} measures, pass mark {cfg.passAccuracy:0.##}, " +
                  $"set aside after {cfg.skipAfterFails} fails.");
        if (skipWarmup) EnterSongIntro();
        else EnterWarmupDemo();
    }

    public void StopLesson()
    {
        StopAll();
        SetState(CurriculumState.Idle);
    }

    // A key press from the piano.
    public void HandleNoteOn(int midi, int velocity) => Press(midi, velocity, Time.unscaledTimeAsDouble);

    // Skips whatever the lesson is waiting for (a demo, the coach finishing) so a run is ready to play.
    public void FastForward()
    {
        for (int i = 0; i < 6 && !running && State != CurriculumState.Idle && State != CurriculumState.SongComplete; i++)
        {
            if (coachDemoRoutine != null) SkipCoachDemo();
            else if (IsDemoPlaying) demoPlayer.Skip();
            else if (waitRoutine != null) RunWaitNow();
            else break;
        }
    }

    // Fixed voice commands. The plan is the playbook's, so "slower" and "next song" do not change it.
    public void HandleVoiceCommand(VoiceCommand command, string transcript)
    {
        if (State == CurriculumState.Idle) return;
        switch (command)
        {
            case VoiceCommand.Again:
                if (running)
                {
                    // Start the same run over; what was played so far is thrown away, not judged.
                    Debug.Log($"[Lesson] Voice \"again\": restarting {Measures(runBars)}.");
                    running = false;
                    if (metronome != null) metronome.Stop();
                    OnNextNote?.Invoke(null);
                    BeginRun(runSong, runBars);
                }
                else FastForward();
                break;

            case VoiceCommand.HowDidIDo:
                if (coach == null) return;
                if (lastRunContext == null) coach.SayNow("Play a run first and I'll tell you.");
                else if (coach.IsThinking) Debug.Log("[Lesson] Voice \"how did I do\": the coach is busy, skipped.");
                else _ = coach.Ask("How did I do?", lastRunContext);
                break;

            case VoiceCommand.Slower:
            case VoiceCommand.NextSong:
                if (coach != null) coach.SayNow("Let's stick with the plan.");
                break;
        }
    }

    // Editor testing without a piano: ends the current run at once with made-up key presses.
    // accuracy: share of notes hit (the misses are spread out and played as a wrong key). timing: 1 = every hit on
    // time, 0 = every hit at the edge of the timing window. failedBars: every note in these bars is missed.
    // hesitate: the student freezes in the middle of the run, long enough to count as a hesitation.
    public bool SimulateRun(float accuracy, float timing, ICollection<int> failedBars = null, bool hesitate = false)
    {
        FastForward();
        if (!running) return false;
        var cfg = Cfg;

        double freezeFrom = double.MaxValue, freezeTo = double.MinValue;
        if (hesitate && expected.Count > 0)
        {
            double gap = 0;
            for (int i = 1; i < expected.Count; i++) gap = Math.Max(gap, expected[i].time - expected[i - 1].time);
            freezeFrom = expected[expected.Count / 2].time - 0.01;
            freezeTo = freezeFrom + gap + cfg.HesitationLimitMs(TempoPct) / 1000.0 + 0.6;
        }

        float missRate = 1f - Mathf.Clamp01(accuracy);
        float offsetMs = cfg.timingFullCreditMs + (1f - Mathf.Clamp01(timing)) * (cfg.timingZeroCreditMs - cfg.timingFullCreditMs);
        played.Clear();
        int k = 0;
        foreach (var n in expected)
        {
            if (n.time >= freezeFrom && n.time < freezeTo) continue;
            bool barFailed = failedBars != null && failedBars.Contains(n.bar);
            bool miss = barFailed || Math.Floor((k + 1) * missRate + 1e-6) > Math.Floor(k * missRate + 1e-6);
            if (!barFailed) k++;
            if (miss) played.Add(new PlayedNote { midi = n.midi == 108 ? 107 : n.midi + 1, velocity = 80, time = n.time });
            else played.Add(new PlayedNote { midi = n.midi, velocity = 80, time = n.time + (n.index % 2 == 0 ? 1 : -1) * offsetMs / 1000.0 });
        }
        EndRun();
        return true;
    }

    // Editor testing with exact counts: every note right except, in each bar of wrongNotesPerBar, that many notes
    // (the first ones) played as a wrong key at their due time. The whole run is played at tempoRatio (1.2 = 20% fast).
    // In each pause bar the student freezes before the bar's first note for longer than the pause limit.
    public bool SimulateRun(IDictionary<int, int> wrongNotesPerBar, float tempoRatio = 1f, ICollection<int> pauseBars = null)
    {
        FastForward();
        if (!running) return false;
        var cfg = Cfg;

        double ratio = Math.Max(0.05, tempoRatio);
        double first = expected.Count > 0 ? expected[0].time : 0;
        double pause = cfg.HesitationLimitMs(RunPct) / 1000.0 + 0.6;
        var wrongLeft = wrongNotesPerBar != null ? new Dictionary<int, int>(wrongNotesPerBar) : new Dictionary<int, int>();
        double shift = 0;
        int lastBar = int.MinValue;
        played.Clear();
        for (int i = 0; i < expected.Count; i++)
        {
            var n = expected[i];
            if (n.bar != lastBar && pauseBars != null && pauseBars.Contains(n.bar)) shift += pause;
            lastBar = n.bar;
            double time = first + (n.time - first) / ratio + shift;
            bool wrong = wrongLeft.TryGetValue(n.bar, out int left) && left > 0;
            if (wrong) wrongLeft[n.bar] = left - 1;
            played.Add(new PlayedNote { midi = wrong ? WrongKey(i) : n.midi, velocity = 80, time = time });
        }
        EndRun();
        return true;
    }

    // A key next to the note that is not one of its neighbours in the run, so the evaluator can't take it for the
    // note before or after (D# + 1 = E in Für Elise would count as the next note struck early).
    int WrongKey(int i)
    {
        int midi = expected[i].midi;
        var near = new HashSet<int>();
        for (int j = Math.Max(0, i - 2); j <= Math.Min(expected.Count - 1, i + 2); j++) near.Add(expected[j].midi);
        foreach (int d in new[] { 1, -1, 2, -2, 3, -3 })
        {
            int k = midi + d;
            if (k >= 21 && k <= 108 && !near.Contains(k)) return k;
        }
        return midi == 108 ? 107 : midi + 1;
    }

    // ------------------------------------------------------------------ Rating a run (COACH_PLAYBOOK.md, Definitions)

    class RunRating
    {
        public bool passed;
        public float accuracy = 1f;    // first try, judged bars
        public TempoRating tempo;
        public float tempoRatio = 1f;
        public readonly Dictionary<int, string> ratings = new Dictionary<int, string>();
        public readonly Dictionary<int, string> right = new Dictionary<int, string>(); // per hand; only hands with notes in the bar
        public readonly Dictionary<int, string> left = new Dictionary<int, string>();
        public readonly List<int> shaky = new List<int>();
        public readonly List<int> problem = new List<int>();
        public readonly List<int> rushed = new List<int>();
        public readonly List<int> dragged = new List<int>();
    }

    // judged: the bars that decide the run (not lead-ins). Set-aside song bars are never judged.
    RunRating Rate(EvaluationResult r, BarRange judged, bool songBars)
    {
        var cfg = Cfg;
        var v = new RunRating { tempoRatio = r.tempoRatio, tempo = cfg.RateTempo(r.tempoRatio) };
        int limit = cfg.HesitationLimitMs(RunPct);
        int problemAt = Mathf.Max(1, cfg.problemWrongNotes);
        int notes = 0, right = 0;
        foreach (var b in r.bars)
        {
            if (!judged.Contains(b.barNumber) || (songBars && skipped.Contains(b.barNumber))) continue;
            int nRight = expected.Count(e => e.bar == b.barNumber && e.hand == PianoHand.Right);
            int nLeft = expected.Count(e => e.bar == b.barNumber && e.hand == PianoHand.Left);
            int n = nRight + nLeft;
            int wrong = b.firstTryMisses;
            // An evaluator that does not fill firstTryMisses still reports score and wrong keys.
            if (wrong == 0 && n > 0 && b.score < 1f) wrong = Mathf.Max(n - Mathf.RoundToInt(b.score * n), b.wrongNotes.Count);
            // Each hand is rated on its own; the measure's rating is the worse of the two.
            int wrongRight = b.rightMisses, wrongLeft = b.leftMisses;
            if (wrongRight + wrongLeft != wrong) // an evaluator that does not split by hand
            {
                wrongRight = nRight > 0 ? wrong : 0;
                wrongLeft = nRight > 0 ? 0 : wrong;
            }
            int pausesRight = 0, pausesLeft = 0;
            foreach (var h in b.hesitations)
            {
                if (h.ms <= limit) continue;
                bool leftNote = nLeft > 0 && (nRight == 0 ||
                    !expected.Any(e => e.bar == b.barNumber && e.midi == h.note && e.hand == PianoHand.Right));
                if (leftNote) pausesLeft++;
                else pausesRight++;
            }
            string ratingRight = RatingOf(wrongRight + pausesRight, problemAt), ratingLeft = RatingOf(wrongLeft + pausesLeft, problemAt);
            string rating = ratingRight == "problem" || ratingLeft == "problem" ? "problem"
                          : ratingRight == "shaky" || ratingLeft == "shaky" ? "shaky" : "clean";
            v.ratings[b.barNumber] = rating;
            if (nRight > 0) v.right[b.barNumber] = ratingRight;
            if (nLeft > 0) v.left[b.barNumber] = ratingLeft;
            if (rating == "shaky") v.shaky.Add(b.barNumber);
            if (rating == "problem") v.problem.Add(b.barNumber);
            notes += n;
            right += Mathf.Max(0, n - Mathf.Min(wrong, n));
            if (n > 0)
            {
                var t = cfg.RateTempo(b.tempoRatio);
                if (t == TempoRating.TooFast) v.rushed.Add(b.barNumber);
                if (t == TempoRating.TooSlow) v.dragged.Add(b.barNumber);
            }
        }
        v.accuracy = notes == 0 ? 1f : (float)right / notes;
        // Below song tempo, too fast is the student being ahead of the step.
        bool tempoOk = v.tempo == TempoRating.OnTempo || (v.tempo == TempoRating.TooFast && runStep < TopStep);
        v.passed = v.problem.Count == 0 && v.accuracy >= cfg.passAccuracy - 1e-4f && tempoOk;

        lastRatings = new Dictionary<int, string>(v.ratings);
        lastRight = new Dictionary<int, string>(v.right);
        lastLeft = new Dictionary<int, string>(v.left);
        LastTempoRating = v.tempo;
        LastPassed = v.passed;

        string bars = v.ratings.Count == 0 ? "nothing judged" : string.Join(", ", v.ratings.Select(p => $"bar {p.Key} {p.Value}"));
        string leadIn = runBars.First < judged.First ? $", lead-in {new BarRange(runBars.First, judged.First - 1)} not judged" : "";
        Debug.Log($"[Lesson] {State}: {Measures(runBars)} {HandWords(runHand)} at {RunPct}% -> {(v.passed ? "passed" : "failed")} ({bars}{leadIn}), " +
                  $"accuracy {v.accuracy:0.00}, tempo {TempoName(v.tempo)} x{v.tempoRatio:0.00}");
        LastAccuracy = v.accuracy;
        OnRunRated?.Invoke(judged);
        return v;
    }

    static string RatingOf(int marks, int problemAt) => marks == 0 ? "clean" : marks >= problemAt ? "problem" : "shaky";

    static string TempoName(TempoRating t) => t == TempoRating.TooSlow ? "too_slow" : t == TempoRating.TooFast ? "too_fast" : "on_tempo";

    // ------------------------------------------------------------------ Tempo ladder (rules 2, 4, 5, 14)

    enum Next { StepUp, Done, Repeat, StepDown, Help, SetAside }

    void SetStep(int step)
    {
        TempoStep = Mathf.Clamp(step, 0, Steps.Count - 1);
        StepFails = 0;
        OnTempoChanged?.Invoke(TempoPct, TempoStep + 1);
    }

    // What follows a run on the ladder. jump: repairs go slow -> song tempo, and back to slow when they step down.
    Next Ladder(bool passed, string key, bool jump)
    {
        var cfg = Cfg;
        if (passed)
        {
            StepFails = 0;
            if (AtTopStep) return Next.Done;
            SetStep(jump ? TopStep : TempoStep + 1);
            return Next.StepUp;
        }

        failsByKey.TryGetValue(key, out int fails);
        failsByKey[key] = ++fails;
        if (cfg.skipAfterFails > 0 && fails >= cfg.skipAfterFails) return Next.SetAside;

        StepFails++;
        int limit = Mathf.Max(1, cfg.stepDownAfterFails);
        if (TempoStep > 0)
        {
            if (StepFails < limit) return Next.Repeat;
            SetStep(jump ? 0 : TempoStep - 1);
            Stats.tempoDrops++;
            return Next.StepDown;
        }
        return StepFails % limit == 0 ? Next.Help : Next.Repeat; // help again every few fails at the slow step
    }

    // Words and trigger for staying on the same thing after a run. what: "measure 3", "the scale".
    void TellLadder(Next next, CoachTrigger repeatTrigger, SongData song, string what, EvaluationResult r, RunRating v, CurriculumInfo info)
    {
        CoachTrigger trigger;
        switch (next)
        {
            case Next.StepUp:
                trigger = CoachTrigger.TempoUp;
                info.nextStep = $"They play {what} again at {Speed()}.";
                break;
            case Next.StepDown:
                trigger = CoachTrigger.TempoDown;
                info.escalation = "slower";
                info.nextStep = $"They slow down and play {what} again at {Speed()}.";
                break;
            case Next.Help:
                trigger = CoachTrigger.Help;
                info.escalation = "help";
                info.nextStep = "They try again after the coach shows them how.";
                break;
            default:
                trigger = repeatTrigger;
                info.nextStep = $"They play {what} again at {Speed()}.";
                break;
        }
        Tell(trigger, song, CurrentRange, r, v, info, RunKey());
        if (next == Next.Help) NextRunWithHelp();
        else NextRun();
    }

    string Speed() => SpeedName(TempoStep);
    string SpeedName(int step) => step >= Steps.Count - 1 ? "full speed" : step <= 0 ? "a slow speed" : "a medium speed";

    // ------------------------------------------------------------------ Warm-up

    void EnterWarmupDemo()
    {
        CurrentRange = new BarRange(1, Warmup.BarCount);
        SetState(CurriculumState.WarmupDemo);
        AfterCoach(() => PlayDemo(Warmup, CurrentRange, TempoPct, EnterWarmupPlay));
    }

    void EnterWarmupPlay()
    {
        CurrentRange = new BarRange(1, Warmup.BarCount);
        SetState(CurriculumState.WarmupPlay);
        BeginRun(Warmup, CurrentRange);
    }

    void EvaluateWarmup()
    {
        var r = Evaluate();
        var v = Rate(r, runBars, false);
        warmupAttempts++;
        attempt = warmupAttempts;
        Stats.warmupAttempts = warmupAttempts;
        var info = Info("warmup", v);

        var next = Ladder(v.passed, "warmup", false);
        if (next == Next.Done || next == Next.SetAside)
        {
            // The song starts at the slow step; say so to the coach now.
            SetStep(0);
            if (next == Next.Done)
            {
                info.nextStep = "The warm-up is done. Next the app introduces the song.";
                Tell(CoachTrigger.WarmupResult, Warmup, runBars, r, v, info, RunKey());
            }
            else
            {
                info.escalation = "set_aside";
                info.nextStep = "The warm-up is set aside for now. Next the app introduces the song.";
                Debug.Log("[Lesson] Warm-up set aside after too many failed runs.");
                Tell(CoachTrigger.SetAside, Warmup, runBars, r, v, info, RunKey());
            }
            EnterSongIntro();
            return;
        }
        TellLadder(next, CoachTrigger.WarmupResult, Warmup, "the scale", r, v, info);
    }

    // ------------------------------------------------------------------ Song

    void EnterSongIntro()
    {
        SetState(CurriculumState.SongIntro);
        OnSongIntro?.Invoke(Song);
        Measure = 1;
        CurrentRange = new BarRange(1, 1);
        SetStep(0);

        var info = Info("", null);
        SetStages(StagesFor(Song, CurrentRange));
        info.nextStep = "They learn the piece one measure at a time: the right hand, then the left hand, then both together, each slow first, " +
                        "then faster, and then join it to the measures before it. " +
                        $"First they play measure 1 on its own {HandWords(Hand)} at a slow speed.";
        Tell(CoachTrigger.LessonStart, Song, CurrentRange, null, null, info, null);
        AfterCoach(() => StartMeasure(1));
    }

    void StartMeasure(int n)
    {
        SetupMeasure(n);
        BeginRun(Song, CurrentRange);
    }

    // The Setup methods get the next run ready without starting it, so Tell can describe it. Step 1:
    void SetupMeasure(int n)
    {
        Measure = n;
        CurrentRange = new BarRange(n, n);
        CurrentBlock = default;
        LeadInFirstBar = 0;
        BlockIndex = -1;
        attempt = 0;
        SetStages(StagesFor(Song, CurrentRange));
        SetStep(0);
        SetState(CurriculumState.SongLearn);
        OnMeasureStarted?.Invoke(n);
    }

    // Step 2. fresh: coming from Step 1, not back from repairs.
    void SetupPlayThrough(bool fresh)
    {
        if (fresh)
        {
            playThroughAttempts = 0;
            shakyPlayThroughs = 0;
        }
        attempt = playThroughAttempts;
        CurrentRange = new BarRange(1, Measure);
        CurrentBlock = default;
        LeadInFirstBar = 0;
        BlockIndex = -1;
        SetStages(new List<HandMode> { Together(Song, CurrentRange) });
        SetStep(TopStep);
        SetState(CurriculumState.SongPlayThrough);
        OnPlayThroughStarted?.Invoke(CurrentRange);
    }

    // Step 3, rule 13: the lead-in is the (up to) two measures before the block, if either was Shaky in the play-through.
    void SetupBlock(int index)
    {
        BlockIndex = index;
        var block = blocks[index];
        CurrentBlock = block;
        LeadInFirstBar = 0;
        int first = block.First;
        for (int bar = block.First - 1; bar >= 1 && block.First - bar <= Mathf.Max(0, Cfg.leadInMeasures) && !skipped.Contains(bar); bar--)
            first = bar;
        bool shakyBefore = false;
        for (int bar = first; bar < block.First; bar++)
            shakyBefore |= playThroughRatings.TryGetValue(bar, out string rating) && rating == "shaky";
        if (shakyBefore) LeadInFirstBar = first;
        CurrentRange = new BarRange(LeadInFirstBar > 0 ? LeadInFirstBar : block.First, block.Last);
        attempt = 0;

        // Rules 16-17: each weak hand alone (right first), then both hands. A hand that was Clean in the block is not repaired.
        bool hasRight = CurriculumSongs.HasNotes(Song, block.First, block.Last, PianoHand.Right);
        bool hasLeft = CurriculumSongs.HasNotes(Song, block.First, block.Last, PianoHand.Left);
        bool weakRight = hasRight && WeakIn(playThroughRight, block), weakLeft = hasLeft && WeakIn(playThroughLeft, block);
        if (!weakRight && !weakLeft) { weakRight = hasRight; weakLeft = hasLeft; }
        var hands = new List<HandMode>();
        if (weakRight) hands.Add(HandMode.Right);
        if (weakLeft) hands.Add(HandMode.Left);
        if (hasRight && hasLeft) hands.Add(HandMode.Both);
        if (hands.Count == 0) hands.Add(HandMode.Right);
        SetStages(hands);
        SetStep(RepairStartStep());
        SetState(CurriculumState.SongRepair);
        OnRepairStarted?.Invoke(CurrentRange, block);
    }

    void EvaluateLearn()
    {
        var r = Evaluate();
        var v = Rate(r, runBars, true);
        Stats.runs++;
        attempt++;
        int n = Measure;
        var info = Info("learn", v);

        var next = Ladder(v.passed, "m" + n + runHand, false);
        switch (next)
        {
            case Next.Done:
                if (StageIndex + 1 < stages.Count)
                {
                    // Rule 4: this hand is learned at song tempo; the next stage starts slow again.
                    NextStage();
                    attempt = 0;
                    SetStep(0);
                    info.nextStep = $"Measure {n} is learned {HandWords(runHand)}. Next they play measure {n} {HandWords(Hand)} at {Speed()}.";
                    Tell(CoachTrigger.RunFinished, Song, CurrentRange, r, v, info, RunKey());
                    NextRun();
                    return;
                }
                Stats.measuresLearned++;
                FinishMeasure(CoachTrigger.MeasureLearned, $"Measure {n} is learned.", r, v, info);
                return;
            case Next.SetAside:
                SetAside(runBars);
                info.escalation = "set_aside";
                info.skippedBars = skipped.ToList();
                FinishMeasure(CoachTrigger.SetAside, $"Measure {n} is set aside for now and no longer judged.", r, v, info);
                return;
        }
        TellLadder(next, CoachTrigger.RunFinished, Song, $"measure {n} {HandWords(Hand)}", r, v, info);
    }

    // Rule 3: measure N is done (learned or set aside). Measure 1 has nothing to join, so it goes straight to Step 4.
    void FinishMeasure(CoachTrigger trigger, string done, EvaluationResult r, RunRating v, CurriculumInfo info)
    {
        if (Measure <= 1)
        {
            Advance(trigger, done, r, v, info);
            return;
        }
        SetupPlayThrough(true);
        info.nextStep = $"{done} Next they play {Measures(CurrentRange)} {HandWords(Hand)} at full speed.";
        Tell(trigger, Song, CurrentRange, r, v, info, RunKey());
        NextRun();
    }

    void EvaluatePlayThrough()
    {
        var cfg = Cfg;
        var r = Evaluate();
        var v = Rate(r, runBars, true);
        Stats.runs++;
        Stats.playThroughs++;
        Stats.finalRunAccuracy = v.accuracy * 100f;
        attempt = ++playThroughAttempts;
        playThroughRatings = new Dictionary<int, string>(v.ratings);
        playThroughRight = new Dictionary<int, string>(v.right);
        playThroughLeft = new Dictionary<int, string>(v.left);
        var range = runBars;
        var info = Info("play_through", v);

        if (v.passed)
        {
            if (attempt == 1) Stats.playThroughsPassedFirstTry++;
            shakyPlayThroughs = 0;
            Advance(CoachTrigger.PlayThroughPassed, $"{Cap(Measures(range))} {IsAre(range)} solid.", r, v, info);
            return;
        }

        if (v.problem.Count > 0)
        {
            StartRepair(v.problem, r, v, info);
            return;
        }

        shakyPlayThroughs++;
        if (shakyPlayThroughs >= Mathf.Max(1, cfg.shakyPlayThroughsBeforeRepair) && v.shaky.Count > 0)
        {
            StartRepair(v.shaky, r, v, info);
            return;
        }

        // Off tempo with clean notes, or not yet enough shaky play-throughs: the coach asks for the one fix.
        info.nextStep = $"They play {Measures(range)} again at full speed.";
        Tell(CoachTrigger.RunFinished, Song, CurrentRange, r, v, info, RunKey());
        NextRun();
    }

    // Rules 11-12: neighbouring bars form one block, earliest first.
    void StartRepair(List<int> bars, EvaluationResult r, RunRating v, CurriculumInfo info)
    {
        blocks.Clear();
        foreach (int bar in bars.Distinct().OrderBy(b => b))
        {
            if (blocks.Count > 0 && blocks[blocks.Count - 1].Last == bar - 1)
                blocks[blocks.Count - 1] = new BarRange(blocks[blocks.Count - 1].First, bar);
            else blocks.Add(new BarRange(bar, bar));
        }
        blocks.RemoveAll(b => Enumerable.Range(b.First, b.Last - b.First + 1).All(skipped.Contains));
        if (blocks.Count == 0)
        {
            info.nextStep = $"They play {Measures(CurrentRange)} again at full speed.";
            Tell(CoachTrigger.RunFinished, Song, CurrentRange, r, v, info, RunKey());
            NextRun();
            return;
        }

        SetupBlock(0);
        FillBlock(info);
        string need = string.Join(" and ", blocks.Select(b => Measures(b)));
        bool one = blocks.Count == 1 && blocks[0].First == blocks[0].Last;
        info.nextStep = $"{Cap(need)} {(one ? "needs" : "need")} work" +
                        (blocks.Count > 1 ? $", one at a time, starting with {Measures(blocks[0])}. " : ". ") + BlockPlan();
        Debug.Log($"[Lesson] Repair blocks: {string.Join(", ", blocks.Select(b => "[" + b + "]"))}");
        Tell(CoachTrigger.RepairStart, Song, CurrentRange, r, v, info, RunKey());
        NextRun();
    }

    // The next repair run in words: "They play measures 3 to 5 at a slow speed; 3 and 4 lead in and are not judged."
    string BlockPlan()
    {
        var block = CurrentBlock;
        if (LeadInFirstBar <= 0)
            return $"They play {Measures(block)} {(block.First == block.Last ? "on its own" : "on their own")} {HandWords(Hand)} at {Speed()}.";
        int leadLast = block.First - 1;
        string lead = LeadInFirstBar == leadLast ? $"{leadLast} leads in and is not judged" : $"{LeadInFirstBar} and {leadLast} lead in and are not judged";
        if (leadLast - LeadInFirstBar > 1) lead = $"{LeadInFirstBar} to {leadLast} lead in and are not judged";
        return $"They play {Measures(CurrentRange)} {HandWords(Hand)} at {Speed()}; {lead}.";
    }

    void EvaluateRepair()
    {
        var r = Evaluate();
        var block = CurrentBlock;
        var v = Rate(r, block, true); // rule 15: lead-in measures are not judged
        Stats.runs++;
        attempt++;
        var info = Info("repair", v);

        var next = Ladder(v.passed, $"b{block.First}-{block.Last}{runHand}", true);
        switch (next)
        {
            case Next.Done:
                if (StageIndex + 1 < stages.Count)
                {
                    // Rules 16-17: this hand is fixed; the next weak hand starts slow, both hands go straight to song tempo.
                    NextStage();
                    attempt = 0;
                    SetStep(RepairStartStep());
                    info.nextStep = $"{Cap(Measures(block))} {IsAre(block)} fixed {HandWords(runHand)}. {BlockPlan()}";
                    Tell(CoachTrigger.RunFinished, Song, CurrentRange, r, v, info, RunKey());
                    NextRun();
                    return;
                }
                Stats.repairedBlocks.Add(block.ToString());
                NextBlock(CoachTrigger.BlockFixed, $"{Cap(Measures(block))} {IsAre(block)} fixed.", r, v, info);
                return;
            case Next.SetAside:
                SetAside(block);
                info.escalation = "set_aside";
                info.skippedBars = skipped.ToList();
                NextBlock(CoachTrigger.SetAside, $"{Cap(Measures(block))} {IsAre(block)} set aside for now and no longer judged.", r, v, info);
                return;
        }
        TellLadder(next, CoachTrigger.RunFinished, Song, $"{Measures(CurrentRange)} {HandWords(Hand)}", r, v, info);
    }

    // Rule 17: after the last block, back to the full play-through.
    void NextBlock(CoachTrigger trigger, string done, EvaluationResult r, RunRating v, CurriculumInfo info)
    {
        int index = BlockIndex + 1;
        while (index < blocks.Count && Enumerable.Range(blocks[index].First, blocks[index].Last - blocks[index].First + 1).All(skipped.Contains))
            index++;
        if (index < blocks.Count)
        {
            SetupBlock(index);
            info.nextStep = $"{done} Next they repair {Measures(CurrentBlock)}. {BlockPlan()}";
        }
        else
        {
            shakyPlayThroughs = 0;
            SetupPlayThrough(false);
            info.nextStep = $"{done} All repairs are done. They play {Measures(CurrentRange)} {HandWords(Hand)} at full speed again.";
        }
        Tell(trigger, Song, CurrentRange, r, v, info, RunKey());
        NextRun();
    }

    // Step 4: add the next measure, or the piece is complete.
    void Advance(CoachTrigger trigger, string done, EvaluationResult r, RunRating v, CurriculumInfo info)
    {
        int n = Measure;
        if (n >= MeasureCount)
        {
            EnterSongComplete(r, v, info);
            return;
        }
        SetupMeasure(n + 1);
        info.nextStep = $"{done} Next they learn measure {n + 1} on its own, {HandWords(Hand)}{(stages.Count > 1 ? " first" : "")}, starting slow.";
        Tell(trigger, Song, CurrentRange, r, v, info, RunKey());
        NextRun();
    }

    void EnterSongComplete(EvaluationResult final, RunRating v, CurriculumInfo info)
    {
        SetState(CurriculumState.SongComplete);
        Stats.minutes = (Time.time - startedAt) / 60f;
        Stats.skippedBars = skipped.ToList();
        if (Stats.playThroughs == 0) Stats.finalRunAccuracy = v.accuracy * 100f; // a one-measure lesson has no play-through

        info.stats = Stats;
        info.nextStep = "That was the whole piece. The lesson is over.";
        Tell(CoachTrigger.SongComplete, Song, new BarRange(1, MeasureCount), final, v, info, null);
        Debug.Log($"[Lesson] Complete: {Stats.runs} runs, {Stats.measuresLearned} measures learned, {Stats.playThroughs} play-throughs " +
                  $"({Stats.playThroughsPassedFirstTry} first try), repaired [{string.Join(", ", Stats.repairedBlocks)}], " +
                  $"set aside [{string.Join(",", Stats.skippedBars)}], {Stats.tempoDrops} tempo drops, {Stats.minutes:0.0} min.");
    }

    // Escape hatch: no longer judged for the rest of the lesson.
    void SetAside(BarRange bars)
    {
        for (int bar = bars.First; bar <= bars.Last; bar++) skipped.Add(bar);
        Stats.skippedBars = skipped.ToList();
        Debug.Log($"[Lesson] Set aside {Measures(bars)} after {Cfg.skipAfterFails} failed runs.");
    }

    // ------------------------------------------------------------------ Runs

    void NextRun()
    {
        var song = CurrentSong;
        AfterCoach(() => BeginRun(song, CurrentRange));
    }

    // Rule 5 at the slow step: the coach was asked for help. If it neither played a demo nor showed the fingering
    // (or could not be asked), the app plays the demo itself.
    void NextRunWithHelp()
    {
        var song = CurrentSong;
        helpWaiting = true;
        helpGiven = false;
        AfterCoach(() =>
        {
            helpWaiting = false;
            var range = CurrentRange;
            if (helpGiven || RecentlyDemoed(range)) BeginRun(song, range);
            else
            {
                Debug.Log($"[Lesson] The coach gave no help, playing {Measures(range)} for them.");
                PlayDemo(song, range, TempoPct, () => BeginRun(song, range));
            }
        });
    }

    void BeginRun(SongData song, BarRange bars)
    {
        var cfg = Cfg;
        runSong = song;
        runBars = bars;
        runHand = Hand;
        expected = CurriculumSongs.Expected(song, bars.First, bars.Last, TempoPct, Hand);
        claimed = new bool[expected.Count];
        played.Clear();
        dueIndex = 0;
        runStep = TempoStep;
        runEnd = expected.Count > 0 ? expected[expected.Count - 1].time + cfg.matchWindowMs / 1000.0 + 0.2 : 0;
        lastCorrectTime = expected.Count > 0 ? expected[0].time : 0;
        lastCorrectIndex = -1;
        stallHinted = false;

        // Count-in: countInBeats clicks on the beats before the first note, then (at slow steps) a click on every beat.
        // The clicks sit on whole beats of the song, so a first note off the beat still gets an in-time count.
        float spb = CurriculumSongs.SecondsPerBeat(song, TempoPct);
        double grid = expected.Count > 0 ? (Math.Floor(expected[0].beat + 1e-4) - expected[0].beat) * spb : 0; // <= 0
        int countIn = Mathf.Max(0, cfg.countInBeats);
        runStart = Time.unscaledTimeAsDouble + 0.3 + countIn * spb - grid;
        clicks.Clear();
        clickIndex = 0;
        for (int i = countIn; i >= 1; i--) clicks.Add(runStart + grid - i * spb);
        if (cfg.metronomeDuringRun && TempoPct <= cfg.metronomeMaxTempoPct && expected.Count > 0)
            for (double c = grid; c <= expected[expected.Count - 1].time + 1e-4; c += spb) clicks.Add(runStart + c);
        metronome.volume = cfg.metronomeVolume;
        running = true;

        CurrentRun = new RunInfo
        {
            song = song, bars = bars, tempoPct = TempoPct, hand = Hand, secondsPerBeat = spb,
            startTime = runStart, notes = expected,
        };
        Debug.Log($"[Lesson] {State}: play {Measures(bars)} of {song.name} {HandWords(Hand)} ({expected.Count} notes) at {TempoPct}%, step {TempoStep + 1}/{Steps.Count}" +
                  (State == CurriculumState.SongRepair ? $", judging block {CurrentBlock}" : "") +
                  (StepFails > 0 ? $", {StepFails} failed in a row" : "") + ".");
        OnRunStarted?.Invoke(CurrentRun);
    }

    void Update()
    {
        if (!running || IsDemoPlaying) return;
        double now = Time.unscaledTimeAsDouble;
        while (clickIndex < clicks.Count && clicks[clickIndex] <= now + 0.1) // scheduled a little ahead on the audio clock
        {
            metronome.ClickAt(clicks[clickIndex], clickIndex == 0);
            clickIndex++;
        }
        double t = now - runStart;
        while (dueIndex < expected.Count && expected[dueIndex].time <= t)
        {
            var note = expected[dueIndex];
            dueIndex++; // not inside Invoke: ?. skips its arguments when nobody listens
            OnNextNote?.Invoke(note);
        }
        CheckStuck(t);
        if (t >= runEnd) EndRun();
    }

    // No correct key for longer than the pause limit while a note is waiting: say how to find it, without the model.
    void CheckStuck(double t)
    {
        if (!sendToCoach || coach == null || stallHinted || expected.Count == 0) return;
        if (Time.unscaledTime - lastHintAt < Cfg.stuckHintMinIntervalSeconds) return;
        int next = -1;
        for (int i = lastCorrectIndex + 1; i < expected.Count && expected[i].time <= t; i++)
            if (!claimed[i]) { next = i; break; }
        if (next < 0) return;
        var note = expected[next];
        // Waiting since the later of the last correct key and the moment this note was due.
        if (t - Math.Max(lastCorrectTime, note.time) <= Cfg.HesitationLimitMs(RunPct) / 1000.0) return;

        string line = CoachFacts.StuckHint(runSong, note.bar, note.midi);
        if (!coach.SayNow(line)) return; // busy talking: try again next frame
        stallHinted = true;
        lastHintAt = Time.unscaledTime;
        Debug.Log($"[Lesson] Stuck hint: {line}");
    }

    void Press(int midi, int velocity, double time)
    {
        if (!running || IsDemoPlaying) return;
        double t = time - runStart;
        played.Add(new PlayedNote { midi = midi, velocity = velocity, time = t });
        if (expected.Count == 0) return;

        // Live feedback only; the evaluator does the real judging at the end of the run.
        double window = Cfg.matchWindowMs / 1000.0;
        int match = -1, nearest = 0;
        for (int i = 0; i < expected.Count; i++)
        {
            double d = Math.Abs(expected[i].time - t);
            if (d < Math.Abs(expected[nearest].time - t)) nearest = i;
            if (!claimed[i] && expected[i].midi == midi && d <= window && (match < 0 || d < Math.Abs(expected[match].time - t))) match = i;
        }
        if (match >= 0)
        {
            claimed[match] = true;
            lastCorrectTime = t;
            lastCorrectIndex = Math.Max(lastCorrectIndex, match);
            stallHinted = false;
        }
        OnNotePlayed?.Invoke(expected[match >= 0 ? match : nearest], midi, match >= 0);
    }

    void EndRun()
    {
        running = false;
        OnNextNote?.Invoke(null);
        switch (State)
        {
            case CurriculumState.WarmupPlay: EvaluateWarmup(); break;
            case CurriculumState.SongLearn: EvaluateLearn(); break;
            case CurriculumState.SongPlayThrough: EvaluatePlayThrough(); break;
            case CurriculumState.SongRepair: EvaluateRepair(); break;
        }
    }

    EvaluationResult Evaluate()
    {
        EvaluationResult r;
        try
        {
            r = evaluator.Evaluate(new List<PlayedNote>(played), expected, runBars.First, runBars.Last);
        }
        catch (Exception e)
        {
            // Counted as clean so the lesson can never stall on a broken evaluator, but loudly.
            EvaluatorError($"threw {e.GetType().Name}: {e.Message}", e);
            r = new EvaluationResult { overallScore = 1f, timingScore = 1f, passed = true };
        }
        if (r == null)
        {
            EvaluatorError("returned null");
            r = new EvaluationResult { overallScore = 1f, timingScore = 1f, passed = true };
        }
        var missing = new List<int>();
        for (int bar = runBars.First; bar <= runBars.Last; bar++)
        {
            if (r.Bar(bar) != null) continue;
            missing.Add(bar);
            r.bars.Add(new BarResult { barNumber = bar, score = 1f, timingScore = 1f, passed = true });
        }
        if (missing.Count > 0 && r.bars.Count > missing.Count) // a whole made-up result has already been reported
            EvaluatorError($"left out bar(s) {string.Join(",", missing)}");
        r.bars.Sort((a, b) => a.barNumber.CompareTo(b.barNumber));

        LastResult = r;
        Debug.Log($"[Lesson] {Measures(runBars)} at {RunPct}%: accuracy {r.overallScore:0.00} tempo x{r.tempoRatio:0.00} timing {r.timingScore:0.00} | " +
                  string.Join("  ", r.bars.Select(b => $"bar {b.barNumber} {b.score:0.00} miss {b.firstTryMisses} x{b.tempoRatio:0.00}" +
                                                       (b.hesitations.Count > 0 ? $" pause {b.hesitations.Max(h => h.ms)}ms" : ""))));
        OnRunEvaluated?.Invoke(r);
        return r;
    }

    void EvaluatorError(string what, Exception e = null)
    {
        EvaluatorErrors++;
        LastEvaluatorError = $"{Measures(runBars)}: {what}";
        Debug.LogError($"[Lesson] EVALUATOR ERROR on {Measures(runBars)}: {what}. Counting it as clean so the lesson goes on." +
                       (e != null ? "\n" + e : ""));
    }

    // ------------------------------------------------------------------ Demos and waiting

    void PlayDemo(SongData song, BarRange bars, int tempoPct, Action then)
    {
        // The hand(s) being practised; if they have nothing in these bars (the coach asked for other bars), everything.
        var notes = CurriculumSongs.Expected(song, bars.First, bars.Last, tempoPct, Hand);
        if (notes.Count == 0) notes = CurriculumSongs.Expected(song, bars.First, bars.Last, tempoPct, HandMode.Both);
        demoPlayer.Play(notes, CurriculumSongs.SecondsPerBeat(song, tempoPct), () =>
        {
            lastDemo = bars;
            lastDemoEnded = Time.time;
            then?.Invoke();
        });
    }

    bool RecentlyDemoed(BarRange bars) => bars.First == lastDemo.First && bars.Last == lastDemo.Last && Time.time - lastDemoEnded < 5f;

    // Plays the demo the coach asked for once it has finished saying why.
    IEnumerator CoachDemo(SongData song, BarRange bars, int tempoPct)
    {
        yield return null; // OnCoachAction fires just before the line starts speaking
        float until = Time.time + Cfg.coachMaxWaitSeconds;
        while (Time.time < until && coach != null && coach.voiceOutput != null && coach.voiceOutput.IsSpeaking) yield return null;
        coachDemoRoutine = null;
        PlayDemo(song, bars, tempoPct, TakeCoachDemoResume());
    }

    Action TakeCoachDemoResume()
    {
        var resume = coachDemoResume;
        coachDemoResume = null;
        return resume;
    }

    // Runs the next step once the coach has finished speaking and no demo is playing, so the student hears it all
    // before notes start coming.
    void AfterCoach(Action then)
    {
        CancelWait();
        waitThen = then;
        waitRoutine = StartCoroutine(WaitForCoach());
    }

    IEnumerator WaitForCoach()
    {
        yield return null; // let a request sent this frame start
        float until = Time.time + Cfg.coachMaxWaitSeconds;
        while (Time.time < until && (CoachBusy || IsDemoPlaying || coachDemoRoutine != null)) yield return null;
        while (IsDemoPlaying) yield return null; // a demo the coach asked for always finishes
        RunWaitNow();
    }

    void RunWaitNow()
    {
        if (waitRoutine != null) StopCoroutine(waitRoutine);
        waitRoutine = null;
        var then = waitThen;
        waitThen = null;
        then?.Invoke();
    }

    void CancelWait()
    {
        if (waitRoutine != null) StopCoroutine(waitRoutine);
        waitRoutine = null;
        waitThen = null;
    }

    void SkipCoachDemo()
    {
        StopCoroutine(coachDemoRoutine);
        coachDemoRoutine = null;
        TakeCoachDemoResume()?.Invoke();
    }

    void StopAll()
    {
        CancelWait();
        if (coachDemoRoutine != null) StopCoroutine(coachDemoRoutine);
        coachDemoRoutine = null;
        coachDemoResume = null;
        running = false;
        helpWaiting = false;
        if (demoPlayer != null) demoPlayer.Stop();
        if (metronome != null) metronome.Stop();
        pendingCoach.Clear();
        OnNextNote?.Invoke(null);
    }

    bool CoachBusy => coachLoopRunning || (coach != null && (coach.IsThinking || (coach.voiceOutput != null && coach.voiceOutput.IsSpeaking)));

    // ------------------------------------------------------------------ Coach

    // The coach may only ask for a demo or a fingering. A demo stops the run and starts it again afterwards.
    void HandleCoachAction(CoachAction a)
    {
        if (State == CurriculumState.Idle || a == null) return;
        if (helpWaiting && (a.type == CoachActionType.Demo || a.type == CoachActionType.ShowFingering)) helpGiven = true;
        switch (a.type)
        {
            case CoachActionType.Demo:
                var song = CurrentSong;
                var bars = new BarRange(Mathf.Clamp(a.firstBar, 1, song.BarCount), Mathf.Clamp(a.lastBar, 1, song.BarCount));
                if (IsDemoPlaying || State == CurriculumState.WarmupDemo) return;
                if (RecentlyDemoed(bars)) return;
                if (coachDemoRoutine != null) return;
                Debug.Log($"[Lesson] Coach asked for a demo of {Measures(bars)}.");
                bool resume = running;
                running = false;
                OnNextNote?.Invoke(null);
                coachDemoResume = () => { if (resume) BeginRun(runSong, runBars); };
                coachDemoRoutine = StartCoroutine(CoachDemo(song, bars, TempoPct));
                break;

            case CoachActionType.ShowFingering:
                OnShowFingering?.Invoke(a.firstBar, a.fingers);
                break;

            case CoachActionType.None:
                break;

            default:
                Debug.Log($"[Lesson] Ignoring coach action {a}: the curriculum decides progression and tempo.");
                break;
        }
    }

    // What was just played. step: "warmup", "learn", "play_through", "repair" ("" = nothing yet).
    CurriculumInfo Info(string step, RunRating v)
    {
        var info = new CurriculumInfo
        {
            stage = step == "warmup" ? "warmup" : "song",
            step = step,
            passed = v != null && v.passed,
            measure = Measure,
            measureCount = MeasureCount,
            attempt = attempt,
            skippedBars = skipped.ToList(),
        };
        if (v != null)
        {
            info.shakyBars = new List<int>(v.shaky);
            info.problemBars = new List<int>(v.problem);
        }
        if (step == "repair") FillBlock(info);
        return info;
    }

    void FillBlock(CurriculumInfo info)
    {
        info.blockFirstBar = CurrentBlock.First;
        info.blockLastBar = CurrentBlock.Last;
        info.leadInFirstBar = LeadInFirstBar;
        info.blockNumber = BlockIndex + 1;
        info.blockCount = blocks.Count;
    }

    // The coach is sent the song as text with every request. For a long piece that is only the measures around the
    // section (at most 16, ending at the later of the section and measure N), with their real bar numbers.
    SongData coachSong;

    SongData ForCoach(SongData song, BarRange section)
    {
        if (Song == null || !ReferenceEquals(song, Song)) return song;
        int last = Mathf.Max(section.Last, Measure);
        int first = Mathf.Max(1, Mathf.Min(section.First, Measure - 7), last - 15);
        if (first <= 1 && last >= Song.BarCount) return song;
        string id = $"{Song.id}_{first}-{last}";
        if (coachSong != null && coachSong.id == id) return coachSong;
        coachSong = new SongData
        {
            id = id, name = Song.name, tempoBpm = Song.tempoBpm, beatsPerBar = Song.beatsPerBar, timeSignature = Song.timeSignature,
        };
        coachSong.notes.AddRange(Song.notes.Where(n => n.bar >= first && n.bar <= last));
        return coachSong;
    }

    // Trend key of the run that just ended: its bars and hand.
    string RunKey() => runBars + ":" + runHand;

    static string HandWords(HandMode hand) =>
        hand == HandMode.Both ? "with both hands together" : hand == HandMode.Left ? "with the left hand alone" : "with the right hand alone";

    // Rule 1: right hand, left hand, both. Rule 5: a hand with nothing to play is skipped, and then so is "both".
    static List<HandMode> StagesFor(SongData song, BarRange bars)
    {
        bool right = CurriculumSongs.HasNotes(song, bars.First, bars.Last, PianoHand.Right);
        bool left = CurriculumSongs.HasNotes(song, bars.First, bars.Last, PianoHand.Left);
        var list = new List<HandMode>();
        if (right) list.Add(HandMode.Right);
        if (left) list.Add(HandMode.Left);
        if (right && left) list.Add(HandMode.Both);
        if (list.Count == 0) list.Add(HandMode.Right);
        return list;
    }

    // Both hands when both have notes in the bars, otherwise the one that does.
    static HandMode Together(SongData song, BarRange bars)
    {
        bool right = CurriculumSongs.HasNotes(song, bars.First, bars.Last, PianoHand.Right);
        bool left = CurriculumSongs.HasNotes(song, bars.First, bars.Last, PianoHand.Left);
        return right && left ? HandMode.Both : left ? HandMode.Left : HandMode.Right;
    }

    static bool WeakIn(Dictionary<int, string> ratings, BarRange bars)
    {
        for (int bar = bars.First; bar <= bars.Last; bar++)
            if (ratings.TryGetValue(bar, out string rating) && rating != "clean") return true;
        return false;
    }

    void SetStages(List<HandMode> hands)
    {
        stages.Clear();
        stages.AddRange(hands);
        StageIndex = 0;
        Hand = stages[0];
    }

    void NextStage()
    {
        StageIndex++;
        Hand = stages[StageIndex];
    }

    // Repairs: a single hand starts slow; the closing both-hands run (after the single hands) starts at song tempo.
    int RepairStartStep() => Hand == HandMode.Both && StageIndex > 0 ? TopStep : 0;
    static string Measures(BarRange bars) => bars.First == bars.Last ? $"measure {bars.First}" : $"measures {bars.First} to {bars.Last}";
    static string IsAre(BarRange bars) => bars.First == bars.Last ? "is" : "are";
    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    // Builds the context for one transition and queues it for the coach. Call it once the next run is set up:
    // section and the tempo fields describe the next run, lastRunSpeed the one that just ended.
    void Tell(CoachTrigger trigger, SongData song, BarRange section, EvaluationResult r, RunRating v, CurriculumInfo info, string trendKey)
    {
        var ctx = new CoachContext
        {
            trigger = trigger,
            mode = "curriculum",
            song = ForCoach(song, section),
            currentSection = new SectionInfo { firstBar = section.First, lastBar = section.Last, tempoPct = TempoPct },
            curriculum = info,
            allowedActions = CoachMayAsk,
            currentTempoPct = TempoPct,
            lastRunSpeed = r != null ? SpeedName(runStep) : null,
            nextRunSpeed = Speed(),
            tempoStep = TempoStep + 1,
            tempoStepCount = Steps.Count,
        };

        if (r != null)
        {
            // Only the judged bars: lead-ins and set-aside measures do not count, so the coach does not talk about them.
            var judged = r.bars.Where(b => v == null || v.ratings.ContainsKey(b.barNumber)).ToList();
            ctx.thisRun = new RunStats
            {
                accuracy = (v != null ? v.accuracy : r.overallScore) * 100f,
                wrongNotes = judged.SelectMany(b => b.wrongNotes).ToList(),
                hesitations = judged.SelectMany(b => b.hesitations).ToList(),
                timing = r.timingScore * 100f,
                tempoRating = v != null ? TempoName(v.tempo) : null,
                rushedBars = v != null ? new List<int>(v.rushed) : new List<int>(),
                draggedBars = v != null ? new List<int>(v.dragged) : new List<int>(),
            };
        }

        var trend = new List<float>();
        if (trendKey != null && r != null)
        {
            if (!trends.TryGetValue(trendKey, out trend)) trends[trendKey] = trend = new List<float>();
            trend.Add(ctx.thisRun.accuracy);
        }
        ctx.history = new LessonHistory { attemptsThisSection = info.attempt, accuracyTrend = new List<float>(trend) };
        if (r != null) lastRunContext = ctx;

        Debug.Log($"[Lesson] -> coach {CoachContext.TriggerName(trigger)}: {info.nextStep}");
        if (!sendToCoach || coach == null) return;
        pendingCoach.Enqueue(ctx);
        while (pendingCoach.Count > MaxPendingCoach) pendingCoach.Dequeue();
        if (!coachLoopRunning) CoachLoop();
    }

    async void CoachLoop()
    {
        coachLoopRunning = true;
        try
        {
            while (pendingCoach.Count > 0 && this != null)
            {
                var ctx = pendingCoach.Dequeue();
                await coach.Coach(ctx);
                while (this != null && coach != null && coach.voiceOutput != null && coach.voiceOutput.IsSpeaking)
                    await Task.Delay(100);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[Lesson] Coach request failed: " + e.Message);
        }
        finally
        {
            coachLoopRunning = false;
        }
    }

    void SetState(CurriculumState s)
    {
        State = s;
        Debug.Log("[Lesson] State: " + s);
        OnStateChanged?.Invoke(s);
    }
}
