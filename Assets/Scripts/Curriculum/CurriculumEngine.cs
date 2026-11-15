using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

public enum CurriculumState { Idle, WarmupDemo, WarmupPlay, WarmupEvaluate, SongIntro, SongChain, SongDrill, SongComplete }

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
    public float secondsPerBeat;
    public double startTime;
    public List<ExpectedNote> notes;
}

// The guided lesson: C major scale warm-up, then the song built up in chunks with drills on the bars that fail.
// Every decision about progression is made here from the evaluator's scores. The coach is told what happened and
// what comes next so it can word it; it may ask for a demo or a fingering, nothing else.
//
// Runs are timed: notes are due at the current tempo (OnRunStarted / OnNextNote) whether or not the student keeps up.
// Every chunk, drilled bar and the warm-up climbs the tempo ladder (LessonConfig.tempoSteps): it has to be played
// clean CLEAN_STREAK times in a row at a step to move up, and at the top step to pass. Lower steps need accurate notes
// and no long pauses; the top step also needs timing. Two misses in a row above the first step drop back one step.
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
    [Tooltip("Start the curriculum on Start. Otherwise call StartLesson.")]
    public bool autoStart;
    [Tooltip("Send transitions to the coach. Turn off to walk the curriculum without API calls.")]
    public bool sendToCoach = true;

    // ------------------------------------------------------------------ Events for the overlay and UI

    public event Action<CurriculumState> OnStateChanged;
    public event Action<BarRange> OnChunkStarted;
    public event Action<int> OnDrillStarted;                   // the bar
    public event Action<SongData> OnSongIntro;
    public event Action<RunInfo> OnRunStarted;                 // all notes of the run with their due times, for falling notes
    public event Action<ExpectedNote> OnNextNote;              // this note is due now (light the key); null when the run is over
    public event Action<ExpectedNote, int, bool> OnNotePlayed; // the note the key matched (or the nearest one), key, correct
    public event Action<int, int> OnTempoChanged;              // tempo %, 1-based step
    public event Action<ExpectedNote> OnDemoNoteDown;          // a demo is sounding this note (light the key, show the finger)
    public event Action<ExpectedNote> OnDemoNoteUp;
    public event Action<int, int[]> OnShowFingering;           // the coach asked: bar, one finger per note (1 = thumb)
    public event Action<EvaluationResult> OnRunEvaluated;

    // ------------------------------------------------------------------ State

    public CurriculumState State { get; private set; } = CurriculumState.Idle;
    public SongData Warmup { get; private set; }
    public SongData Song { get; private set; }
    public IReadOnlyList<BarRange> Chunks => chunks;
    public int ChunkIndex { get; private set; } = -1;
    public BarRange CurrentChunk => ChunkIndex >= 0 && ChunkIndex < chunks.Count ? chunks[ChunkIndex] : default;
    public int DrillBar { get; private set; }
    public int DrillFails { get; private set; }
    public int TempoStep { get; private set; }               // 0-based index into the ladder
    public int TempoPct => Steps[Mathf.Clamp(TempoStep, 0, Steps.Count - 1)];
    public bool AtTopStep => TempoStep >= Steps.Count - 1;
    public int CleanStreak { get; private set; }
    public int StepFails { get; private set; }               // misses in a row at this step
    public IReadOnlyCollection<int> SkippedBars => skipped;
    public EvaluationResult LastResult { get; private set; }
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

    readonly List<BarRange> chunks = new List<BarRange>();
    readonly SortedSet<int> skipped = new SortedSet<int>();
    readonly Dictionary<string, List<float>> trends = new Dictionary<string, List<float>>();
    readonly Dictionary<int, int> chunkAttempts = new Dictionary<int, int>();
    IPerformanceEvaluator evaluator;
    int warmupAttempts, warmupFails;
    int chunkFails;        // missed runs of the current chunk since it started, at any step
    int drillReturnStep;   // the step the chunk goes back to once its drill passes
    float startedAt;
    Metronome metronome;

    // The run in progress.
    SongData runSong;
    BarRange runBars;
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
        BuildChunks(Song.BarCount, Mathf.Max(1, cfg.phraseLength));

        skipped.Clear();
        trends.Clear();
        chunkAttempts.Clear();
        Stats = new LessonStats();
        warmupAttempts = warmupFails = 0;
        ChunkIndex = -1;
        DrillBar = 0;
        ResetLadder();
        startedAt = Time.time;
        LastResult = null;
        EvaluatorErrors = 0;
        LastEvaluatorError = null;

        Debug.Log($"[Lesson] Starting. Tempo steps [{string.Join(", ", Steps)}], {cfg.CleanStreak} clean in a row per step. " +
                  $"Chunks: {string.Join(", ", chunks.Select(c => "[" + c + "]"))}");
        EnterWarmupDemo();
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

    // ------------------------------------------------------------------ Tempo ladder

    enum Step { StreakGoing, StepUp, Passed, Retry, StepDown }

    void ResetLadder(int step = 0)
    {
        TempoStep = Mathf.Clamp(step, 0, Steps.Count - 1);
        CleanStreak = 0;
        StepFails = 0;
        OnTempoChanged?.Invoke(TempoPct, TempoStep + 1);
    }

    // Moves along the ladder after a run. needed = clean runs in a row to pass the step.
    Step Climb(bool clean, int needed)
    {
        if (clean)
        {
            StepFails = 0;
            if (++CleanStreak < needed) return Step.StreakGoing;
            CleanStreak = 0;
            if (AtTopStep) return Step.Passed;
            TempoStep++;
            OnTempoChanged?.Invoke(TempoPct, TempoStep + 1);
            return Step.StepUp;
        }

        CleanStreak = 0;
        StepFails++;
        if (TempoStep > 0 && StepFails >= Mathf.Max(1, Cfg.dropStepAfterFails))
        {
            TempoStep--;
            StepFails = 0;
            Stats.tempoDrops++;
            OnTempoChanged?.Invoke(TempoPct, TempoStep + 1);
            return Step.StepDown;
        }
        return Step.Retry;
    }

    // Whether a run is clean, and which bars had a problem. Skipped bars never count.
    class Verdict
    {
        public bool clean;
        public bool atTop;         // the run was at the top step, so timing counted
        public float accuracy = 1f;
        public int slips;
        public bool hesitated;
        public bool timingFailed;
        public List<int> problemBars = new List<int>();
        public List<int> offBeatBars = new List<int>();
    }

    Verdict Judge(EvaluationResult r)
    {
        var cfg = Cfg;
        var v = new Verdict { atTop = AtTopStep };
        int limit = cfg.HesitationLimitMs(TempoPct);
        int notes = 0, hits = 0;
        foreach (var b in r.bars)
        {
            if (skipped.Contains(b.barNumber)) continue;
            int n = expected.Count(e => e.bar == b.barNumber);
            int barHits = Mathf.RoundToInt(b.score * n);
            int slips = Mathf.Max(n - barHits, b.wrongNotes.Count); // a wrong key instead of the right one is one slip
            bool hesitated = b.hesitations.Any(h => h.ms > limit);
            bool offBeat = AtTopStep && barHits > 0 && b.timingScore < cfg.timingThreshold;
            if (slips > 0 || hesitated || offBeat) v.problemBars.Add(b.barNumber);
            if (offBeat) v.offBeatBars.Add(b.barNumber);
            notes += n;
            hits += barHits;
            v.slips += slips;
            v.hesitated |= hesitated;
        }
        v.accuracy = notes == 0 ? 1f : (float)hits / notes;
        v.timingFailed = AtTopStep && r.timingScore < cfg.timingThreshold;
        // "95%, max 1 slip": one slip is always forgiven, so a short chunk does not have to be perfect. A drilled bar
        // is a few notes looped on purpose, so it has to be exact (drillMaxSlips).
        bool notesOk = State == CurriculumState.SongDrill ? v.slips <= cfg.drillMaxSlips
                     : v.accuracy >= cfg.accuracyThreshold || v.slips <= cfg.maxSlips;
        v.clean = notesOk && !v.hesitated && !v.timingFailed;
        if (v.clean) v.problemBars.Clear();
        return v;
    }

    string Speed() => SpeedName(TempoStep);
    string SpeedName(int step) => step >= Steps.Count - 1 ? "full speed" : step <= 0 ? "a slow speed" : "a medium speed";

    // The bar to drill when no single bar was flagged (say timing failed overall): the lowest scoring one.
    int WorstBar(EvaluationResult r) => r.bars.Where(b => !skipped.Contains(b.barNumber))
        .OrderBy(b => b.score).ThenBy(b => b.timingScore).Select(b => b.barNumber).FirstOrDefault();

    // ------------------------------------------------------------------ Warm-up

    void EnterWarmupDemo()
    {
        SetState(CurriculumState.WarmupDemo);
        AfterCoach(() => PlayDemo(Warmup, new BarRange(1, Warmup.BarCount), TempoPct, () => EnterWarmupPlay(false)));
    }

    void EnterWarmupPlay(bool waitForCoach = true)
    {
        SetState(CurriculumState.WarmupPlay);
        var bars = new BarRange(1, Warmup.BarCount);
        if (waitForCoach) AfterCoach(() => BeginRun(Warmup, bars));
        else BeginRun(Warmup, bars);
    }

    void EvaluateWarmup()
    {
        SetState(CurriculumState.WarmupEvaluate);
        var cfg = Cfg;
        var r = Evaluate();
        var v = Judge(r);
        var range = new BarRange(1, Warmup.BarCount);
        warmupAttempts++;
        Stats.warmupAttempts = warmupAttempts;
        if (!v.clean) warmupFails++;

        var step = Climb(v.clean, cfg.CleanStreak);
        bool replayDemo = !v.clean && cfg.warmupDemoReplayAfterFails > 0 && warmupFails % cfg.warmupDemoReplayAfterFails == 0;

        var info = Info("warmup", v, r);
        info.attempt = warmupAttempts;
        string again = replayDemo ? "The app plays the scale for them again, then they" : "They";
        switch (step)
        {
            case Step.Passed:
                info.nextStep = "The warm-up is done. Next the app introduces the song.";
                Tell(CoachTrigger.WarmupResult, Warmup, range, r, v, info, "warmup");
                EnterSongIntro();
                return;
            case Step.StepUp:
                info.nextStep = $"They play the scale again at {Speed()}.";
                Tell(CoachTrigger.TempoUp, Warmup, range, r, v, info, "warmup");
                break;
            case Step.StreakGoing:
                info.nextStep = $"One more clean scale at {Speed()} before going on.";
                Tell(CoachTrigger.RunFinished, Warmup, range, r, v, info, "warmup");
                break;
            case Step.StepDown:
                info.nextStep = $"{again} play the scale at {Speed()} for a while.";
                Tell(CoachTrigger.TempoDown, Warmup, range, r, v, info, "warmup");
                break;
            default:
                info.nextStep = $"{again} play the scale again at {Speed()}.";
                Tell(CoachTrigger.WarmupResult, Warmup, range, r, v, info, "warmup");
                break;
        }

        if (replayDemo) EnterWarmupDemo();
        else EnterWarmupPlay();
    }

    // ------------------------------------------------------------------ Song

    void EnterSongIntro()
    {
        SetState(CurriculumState.SongIntro);
        OnSongIntro?.Invoke(Song);

        var info = Info("song", null, null);
        info.nextStep = $"They start with bar {chunks[0].First}, then add one bar at a time, {Cfg.phraseLength} bars per phrase. " +
                        "Each group of bars starts slow and speeds up once it is clean.";
        Tell(CoachTrigger.LessonStart, Song, chunks[0], null, null, info, null); // the bars they are about to play

        AfterCoach(() => StartChunk(0));
    }

    void StartChunk(int index, int tempoStep = 0)
    {
        ChunkIndex = index;
        DrillBar = 0;
        chunkFails = 0;
        ResetLadder(tempoStep);
        SetState(CurriculumState.SongChain);
        OnChunkStarted?.Invoke(CurrentChunk);
        AfterCoach(() => BeginRun(Song, CurrentChunk));
    }

    void EvaluateChunk()
    {
        var cfg = Cfg;
        var chunk = CurrentChunk;
        var r = Evaluate();
        var v = Judge(r);
        Stats.runs++;
        chunkAttempts.TryGetValue(ChunkIndex, out int attempts);
        chunkAttempts[ChunkIndex] = ++attempts;
        bool isFinal = ChunkIndex == chunks.Count - 1;
        if (isFinal) Stats.finalRunAccuracy = r.overallScore * 100f;

        // Too many misses on this chunk, at whatever steps: drill its worst bar, so a bar that only breaks at full
        // speed cannot bounce between the top steps forever.
        if (!v.clean && ++chunkFails >= Mathf.Max(1, cfg.drillAfterTotalFails))
        {
            int bar = v.problemBars.Count > 0 ? v.problemBars[0] : WorstBar(r);
            if (bar > 0)
            {
                drillReturnStep = Mathf.Max(0, TempoStep - 1);
                CleanStreak = StepFails = 0;
                var drillInfo = Info("song", v, r);
                drillInfo.attempt = attempts;
                drillInfo.drillBar = bar;
                drillInfo.nextStep = $"The app loops bar {bar} on its own until it is clean at full speed, then they play {Bars(chunk)} again at {SpeedName(drillReturnStep)}.";
                Tell(CoachTrigger.ChunkFailed, Song, chunk, r, v, drillInfo, Key(chunk));
                EnterDrill(bar);
                return;
            }
        }

        var step = Climb(v.clean, cfg.CleanStreak);
        var info = Info("song", v, r);
        info.attempt = attempts;

        switch (step)
        {
            case Step.Passed:
                Stats.chunksPassed++;
                if (attempts == cfg.CleanStreak * Steps.Count) Stats.chunksPassedFirstTry++; // never missed a run
                info.nextStep = isFinal ? "That was the whole piece. The lesson is complete."
                              : ChunkIndex + 1 == chunks.Count - 1 ? $"Next they play the whole piece, bars {chunks[ChunkIndex + 1]}, starting slow."
                              : $"Next they play {Bars(chunks[ChunkIndex + 1])}, starting slow.";
                if (isFinal) EnterSongComplete(r);
                else
                {
                    Tell(CoachTrigger.ChunkPassed, Song, chunk, r, v, info, Key(chunk));
                    StartChunk(ChunkIndex + 1);
                }
                return;

            case Step.StepUp:
                info.nextStep = $"They play {Bars(chunk)} again at {Speed()}.";
                Tell(CoachTrigger.TempoUp, Song, chunk, r, v, info, Key(chunk));
                break;

            case Step.StreakGoing:
                info.nextStep = $"One more clean run of {Bars(chunk)} at {Speed()} before speeding up.";
                if (AtTopStep) info.nextStep = $"One more clean run of {Bars(chunk)} at full speed and these bars are done.";
                Tell(CoachTrigger.RunFinished, Song, chunk, r, v, info, Key(chunk));
                break;

            case Step.StepDown:
                info.nextStep = $"The app slows {Bars(chunk)} back down to {Speed()} for now.";
                Tell(CoachTrigger.TempoDown, Song, chunk, r, v, info, Key(chunk));
                break;

            default:
                info.nextStep = $"They play {Bars(chunk)} again at {Speed()}.";
                Tell(CoachTrigger.RunFinished, Song, chunk, r, v, info, Key(chunk));
                break;
        }
        AfterCoach(() => BeginRun(Song, chunk));
    }

    void EnterDrill(int bar)
    {
        DrillBar = bar;
        DrillFails = 0;
        ResetLadder();
        trends.Remove(Key(new BarRange(bar, bar)));
        if (!Stats.drilledBars.Contains(bar)) Stats.drilledBars.Add(bar);
        SetState(CurriculumState.SongDrill);
        OnDrillStarted?.Invoke(bar);
        AfterCoach(() => BeginRun(Song, new BarRange(bar, bar)));
    }

    void EvaluateDrill()
    {
        var cfg = Cfg;
        var range = new BarRange(DrillBar, DrillBar);
        var r = Evaluate();
        var v = Judge(r);
        Stats.runs++;
        int attempt = DrillFails + 1;
        if (!v.clean) DrillFails++;

        int needed = AtTopStep ? Mathf.Max(cfg.CleanStreak, cfg.drillPassCount) : cfg.CleanStreak;
        var step = Climb(v.clean, needed);
        var info = Info("song", v, r);
        info.attempt = attempt;
        info.drillBar = DrillBar;

        switch (step)
        {
            case Step.Passed:
                // Back to the chunk one step below where it broke, not all the way down.
                info.nextStep = $"Bar {DrillBar} is fixed. They go back to {Bars(CurrentChunk)} at {SpeedName(drillReturnStep)}.";
                Tell(CoachTrigger.DrillPassed, Song, range, r, v, info, Key(range));
                StartChunk(ChunkIndex, drillReturnStep);
                return;
            case Step.StepUp:
                info.nextStep = $"They play bar {DrillBar} again at {Speed()}.";
                Tell(CoachTrigger.TempoUp, Song, range, r, v, info, Key(range));
                AfterCoach(() => BeginRun(Song, range));
                return;
            case Step.StreakGoing:
                info.nextStep = $"One more clean bar {DrillBar} at {Speed()}.";
                Tell(CoachTrigger.RunFinished, Song, range, r, v, info, Key(range));
                AfterCoach(() => BeginRun(Song, range));
                return;
        }

        if (DrillFails >= cfg.drillSkipAfterFails)
        {
            skipped.Add(DrillBar);
            Stats.skippedBars = skipped.ToList();
            info.escalation = "skip";
            info.skippedBars = skipped.ToList();
            info.nextStep = $"Bar {DrillBar} is set aside for later. They go back to {Bars(CurrentChunk)} and that bar no longer has to be perfect.";
            Tell(CoachTrigger.DrillSkipped, Song, range, r, v, info, Key(range));
            StartChunk(ChunkIndex);
            return;
        }

        bool demo = DrillFails == cfg.drillDemoAfterFails;
        bool slower = step == Step.StepDown;
        info.escalation = demo ? "demo" : slower ? "slower" : "";
        string then = slower ? $"they try it again at {Speed()}" : "they try it again";
        info.nextStep = demo ? $"The app plays bar {DrillBar} for them, then {then}."
                      : slower ? $"The app slows bar {DrillBar} back down to {Speed()} and they try it again."
                      : $"They try bar {DrillBar} again at {Speed()}.";
        Tell(slower ? CoachTrigger.TempoDown : CoachTrigger.DrillStart, Song, range, r, v, info, Key(range));

        // The coach may already have played it while explaining.
        if (demo) AfterCoach(() =>
        {
            if (RecentlyDemoed(range)) BeginRun(Song, range);
            else PlayDemo(Song, range, TempoPct, () => BeginRun(Song, range));
        });
        else AfterCoach(() => BeginRun(Song, range));
    }

    void EnterSongComplete(EvaluationResult final)
    {
        SetState(CurriculumState.SongComplete);
        Stats.minutes = (Time.time - startedAt) / 60f;
        Stats.skippedBars = skipped.ToList();

        var v = Judge(final);
        var info = Info("song", v, final);
        info.stats = Stats;
        info.nextStep = "The lesson is over.";
        Tell(CoachTrigger.SongComplete, Song, new BarRange(1, Song.BarCount), final, v, info, null);
        Debug.Log($"[Lesson] Complete: {Stats.runs} runs, drilled [{string.Join(",", Stats.drilledBars)}], skipped [{string.Join(",", Stats.skippedBars)}], " +
                  $"{Stats.tempoDrops} tempo drops, {Stats.minutes:0.0} min.");
    }

    // [1], [1-2], [1-3], [1-4], [5], [5-6], ... then the whole piece when it has more than one phrase.
    void BuildChunks(int bars, int phrase)
    {
        chunks.Clear();
        for (int start = 1; start <= bars; start += phrase)
            for (int end = start; end <= Mathf.Min(start + phrase - 1, bars); end++)
                chunks.Add(new BarRange(start, end));
        if (bars > phrase) chunks.Add(new BarRange(1, bars));
    }

    // ------------------------------------------------------------------ Runs

    void BeginRun(SongData song, BarRange bars)
    {
        var cfg = Cfg;
        runSong = song;
        runBars = bars;
        expected = CurriculumSongs.Expected(song, bars.First, bars.Last, TempoPct);
        claimed = new bool[expected.Count];
        played.Clear();
        dueIndex = 0;
        runStep = TempoStep;
        runEnd = expected.Count > 0 ? expected[expected.Count - 1].time + cfg.matchWindowMs / 1000.0 + 0.2 : 0;

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
            song = song, bars = bars, tempoPct = TempoPct, secondsPerBeat = spb,
            startTime = runStart, notes = expected,
        };
        Debug.Log($"[Lesson] {State}: play {Bars(bars)} of {song.name} ({expected.Count} notes) at {TempoPct}%, step {TempoStep + 1}/{Steps.Count}, " +
                  $"clean streak {CleanStreak}.");
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
        if (t >= runEnd) EndRun();
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
        if (match >= 0) claimed[match] = true;
        OnNotePlayed?.Invoke(expected[match >= 0 ? match : nearest], midi, match >= 0);
    }

    void EndRun()
    {
        running = false;
        OnNextNote?.Invoke(null);
        switch (State)
        {
            case CurriculumState.WarmupPlay: EvaluateWarmup(); break;
            case CurriculumState.SongChain: EvaluateChunk(); break;
            case CurriculumState.SongDrill: EvaluateDrill(); break;
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
        Debug.Log($"[Lesson] {Bars(runBars)} at {TempoPct}%: accuracy {r.overallScore:0.00} timing {r.timingScore:0.00} | " +
                  string.Join("  ", r.bars.Select(b => $"bar {b.barNumber} {b.score:0.00}/{b.timingScore:0.00}" +
                                                       (b.hesitations.Count > 0 ? $" pause {b.hesitations.Max(h => h.ms)}ms" : ""))));
        OnRunEvaluated?.Invoke(r);
        return r;
    }

    void EvaluatorError(string what, Exception e = null)
    {
        EvaluatorErrors++;
        LastEvaluatorError = $"{Bars(runBars)}: {what}";
        Debug.LogError($"[Lesson] EVALUATOR ERROR on {Bars(runBars)}: {what}. Counting it as clean so the lesson goes on." +
                       (e != null ? "\n" + e : ""));
    }

    // ------------------------------------------------------------------ Demos and waiting

    void PlayDemo(SongData song, BarRange bars, int tempoPct, Action then)
    {
        var notes = CurriculumSongs.Expected(song, bars.First, bars.Last, tempoPct);
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
        switch (a.type)
        {
            case CoachActionType.Demo:
                var song = State <= CurriculumState.WarmupEvaluate ? Warmup : Song;
                var bars = new BarRange(Mathf.Clamp(a.firstBar, 1, song.BarCount), Mathf.Clamp(a.lastBar, 1, song.BarCount));
                if (IsDemoPlaying || State == CurriculumState.WarmupDemo) return;
                if (RecentlyDemoed(bars)) return;
                if (coachDemoRoutine != null) return;
                Debug.Log($"[Lesson] Coach asked for a demo of {Bars(bars)}.");
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

    CurriculumInfo Info(string stage, Verdict v, EvaluationResult r)
    {
        var info = new CurriculumInfo
        {
            stage = stage,
            passed = v != null && v.clean,
            clean = v != null && v.clean,
            cleanStreak = CleanStreak,
            cleanNeeded = Cfg.CleanStreak,
            chunkCount = chunks.Count,
            skippedBars = skipped.ToList(),
        };
        if (stage == "song" && ChunkIndex >= 0)
        {
            info.chunkFirstBar = CurrentChunk.First;
            info.chunkLastBar = CurrentChunk.Last;
            info.chunkNumber = ChunkIndex + 1;
        }
        if (v != null) info.failedBars = new List<int>(v.problemBars);
        return info;
    }

    static string Key(BarRange bars) => bars.ToString();
    static string Bars(BarRange bars) => (bars.First == bars.Last ? "bar " : "bars ") + bars;

    // Builds the context for one transition and queues it for the coach. Tempo fields describe the step the next run
    // is at (after any step up or down).
    void Tell(CoachTrigger trigger, SongData song, BarRange section, EvaluationResult r, Verdict v, CurriculumInfo info, string trendKey)
    {
        var ctx = new CoachContext
        {
            trigger = trigger,
            mode = "curriculum",
            song = song,
            currentSection = new SectionInfo { firstBar = section.First, lastBar = section.Last, tempoPct = TempoPct },
            curriculum = info,
            allowedActions = CoachMayAsk,
            currentTempoPct = TempoPct,
            lastRunSpeed = r != null ? SpeedName(runStep) : null,
            nextRunSpeed = Speed(),
            tempoStep = TempoStep + 1,
            tempoStepCount = Steps.Count,
            timingRequired = AtTopStep, // for the next run
        };

        if (r != null)
        {
            ctx.thisRun = new RunStats
            {
                accuracy = r.overallScore * 100f,
                wrongNotes = r.bars.SelectMany(b => b.wrongNotes).ToList(),
                hesitations = r.bars.SelectMany(b => b.hesitations).ToList(),
                timing = r.timingScore * 100f,
                offBeatBars = v != null ? v.offBeatBars : new List<int>(),
                // Timing only earns praise when it counted for the run that just ended (the top step).
                timingChecked = v != null && v.atTop && !v.timingFailed,
            };
        }

        var trend = new List<float>();
        if (trendKey != null && r != null)
        {
            if (!trends.TryGetValue(trendKey, out trend)) trends[trendKey] = trend = new List<float>();
            trend.Add(r.overallScore * 100f);
        }
        ctx.history = new LessonHistory { attemptsThisSection = info.attempt, accuracyTrend = new List<float>(trend) };

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
