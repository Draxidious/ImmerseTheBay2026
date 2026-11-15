using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Debug = UnityEngine.Debug;

// Editor test harness for the AI layer, no glasses or piano needed. Use it to tune Assets/Resources/CoachPrompt.txt.
// Keys: 1 = first try, 2 = improving, 3 = stuck on bar 3, 4 = aced it, 5 = lesson start, hold Space = push-to-talk.
// Curriculum: Start lesson, then "Simulate run" ends the current run with made-up playing: the accuracy and timing
// sliders, every note missed in the bars typed in the box, and optionally a long pause in the middle
// (6 = simulate with those settings, 7 = clean run), so the whole curriculum can be walked without a piano.
public class AIDebugPanel : MonoBehaviour
{
    public AICoach coach;
    public VoiceOutput voiceOutput;
    public VoiceInput voiceInput;
    public CurriculumEngine engine;

    readonly List<string> lines = new List<string>();
    CoachContext current; // the scenario the next "how did I do" or question is about
    string failedBarsText = "";
    float simAccuracy = 1f, simTiming = 1f;
    bool simPause;

    void Awake()
    {
        if (coach == null) coach = GetOrAdd<AICoach>();
        if (voiceOutput == null) voiceOutput = GetOrAdd<VoiceOutput>();
        if (voiceInput == null) voiceInput = GetOrAdd<VoiceInput>();
        if (engine == null) engine = GetOrAdd<CurriculumEngine>();
        if (coach.voiceOutput == null) coach.voiceOutput = voiceOutput;
        current = Improving();
    }

    T GetOrAdd<T>() where T : Component
    {
        var c = GetComponent<T>();
        return c != null ? c : gameObject.AddComponent<T>();
    }

    void OnEnable()
    {
        voiceInput.OnCommand += HandleCommand;
        coach.OnCoachAction += HandleAction;
        coach.OnResponse += HandleResponse;
        engine.OnStateChanged += HandleState;
        engine.OnRunEvaluated += HandleEvaluated;
    }

    void OnDisable()
    {
        voiceInput.OnCommand -= HandleCommand;
        coach.OnCoachAction -= HandleAction;
        coach.OnResponse -= HandleResponse;
        engine.OnStateChanged -= HandleState;
        engine.OnRunEvaluated -= HandleEvaluated;
    }

    // ------------------------------------------------------------------ Fake data

    // First 8 bars of Ode to Joy, right hand melody over a simple left hand.
    public static SongData OdeToJoy()
    {
        var song = new SongData { id = "ode_to_joy", name = "Ode to Joy", tempoBpm = 120, beatsPerBar = 4 };
        int[][] melody =
        {
            new[] { 64, 64, 65, 67 }, new[] { 67, 65, 64, 62 }, new[] { 60, 60, 62, 64 }, null,
            new[] { 64, 64, 65, 67 }, new[] { 67, 65, 64, 62 }, new[] { 60, 60, 62, 64 }, null,
        };
        int[] bass = { 48, 43, 48, 43, 48, 43, 48, 43 };

        for (int bar = 1; bar <= melody.Length; bar++)
        {
            if (melody[bar - 1] != null)
            {
                for (int i = 0; i < 4; i++)
                    song.notes.Add(new SongNote { midi = melody[bar - 1][i], bar = bar, beat = i + 1, hand = PianoHand.Right });
            }
            else
            {
                // Bars 4 and 8: dotted note, short note, long note.
                int a = bar == 4 ? 64 : 62, b = bar == 4 ? 62 : 60;
                song.notes.Add(new SongNote { midi = a, bar = bar, beat = 1f, beats = 1.5f, hand = PianoHand.Right });
                song.notes.Add(new SongNote { midi = b, bar = bar, beat = 2.5f, beats = 0.5f, hand = PianoHand.Right });
                song.notes.Add(new SongNote { midi = b, bar = bar, beat = 3f, beats = 2f, hand = PianoHand.Right });
            }
            song.notes.Add(new SongNote { midi = bass[bar - 1], bar = bar, beat = 1f, beats = 4f, hand = PianoHand.Left });
        }
        return song;
    }

    static CoachContext Scenario(CoachTrigger trigger, int tempoPct)
    {
        return new CoachContext
        {
            trigger = trigger,
            mode = "curriculum",
            song = OdeToJoy(),
            currentSection = new SectionInfo { firstBar = 1, lastBar = 4, tempoPct = tempoPct },
        };
    }

    public static CoachContext LessonStart() => Scenario(CoachTrigger.LessonStart, 70);

    public static CoachContext FirstTry()
    {
        var c = Scenario(CoachTrigger.RunFinished, 70);
        c.thisRun = new RunStats
        {
            accuracy = 56f,
            wrongNotes = new List<WrongNote>
            {
                new WrongNote { bar = 1, expected = 65, played = 66 },
                new WrongNote { bar = 2, expected = 62, played = 60 },
                new WrongNote { bar = 3, expected = 62, played = 61 },
                new WrongNote { bar = 3, expected = 64, played = 65 },
                new WrongNote { bar = 4, expected = 62, played = 64 },
            },
            hesitations = new List<Hesitation>
            {
                new Hesitation { bar = 1, note = 67, ms = 2600 },
                new Hesitation { bar = 3, note = 60, ms = 3100 },
                new Hesitation { bar = 4, note = 62, ms = 2200 },
            },
        };
        c.history = new LessonHistory { attemptsThisSection = 1, accuracyTrend = new List<float> { 56f } };
        return c;
    }

    public static CoachContext Improving()
    {
        var c = Scenario(CoachTrigger.RunFinished, 80);
        c.thisRun = new RunStats
        {
            accuracy = 84f,
            wrongNotes = new List<WrongNote> { new WrongNote { bar = 3, expected = 62, played = 61 } },
            hesitations = new List<Hesitation> { new Hesitation { bar = 3, note = 62, ms = 1900 } },
            rushedBars = new List<int> { 4 },
        };
        c.history = new LessonHistory { attemptsThisSection = 4, accuracyTrend = new List<float> { 58f, 66f, 75f, 84f } };
        c.lastFeedback = "Good, that was smoother. Keep your hand relaxed going into bar two.";
        return c;
    }

    public static CoachContext StuckOnBar3(StuckInfo stuck)
    {
        var c = Scenario(CoachTrigger.Stuck, 80);
        c.thisRun = new RunStats
        {
            accuracy = 70f,
            wrongNotes = new List<WrongNote>
            {
                new WrongNote { bar = 3, expected = 62, played = 61 },
                new WrongNote { bar = 3, expected = 62, played = 63 },
                new WrongNote { bar = 3, expected = 62, played = 61 },
            },
        };
        c.history = new LessonHistory { attemptsThisSection = 3, accuracyTrend = new List<float> { 62f, 71f, 70f } };
        c.stuck = stuck;
        return c;
    }

    public static CoachContext AcedIt()
    {
        var c = Scenario(CoachTrigger.RunFinished, 100);
        c.thisRun = new RunStats { accuracy = 100f };
        c.history = new LessonHistory { attemptsThisSection = 6, accuracyTrend = new List<float> { 78f, 86f, 92f, 97f, 100f } };
        c.lastFeedback = "So close. Let's take it once more at full speed.";
        return c;
    }

    // ------------------------------------------------------------------ Running a scenario

    async void Run(string label, CoachContext ctx)
    {
        if (coach.IsThinking) return;
        current = ctx;
        Print($"{label}: asking the coach...");
        var sw = Stopwatch.StartNew();
        var r = await coach.Coach(ctx);
        if (string.IsNullOrEmpty(r.say)) return;
        Print($"Coach ({sw.ElapsedMilliseconds} ms): \"{r.say}\"");
    }

    // Stuck on the D in bar 3 after three wrong keys.
    void SimulateStuck() => Run("Stuck on bar 3", StuckOnBar3(new StuckInfo { bar = 3, note = 62, ms = 4200, misses = 3 }));

    void HandleAction(CoachAction action) => Print("Action: " + action);

    // ------------------------------------------------------------------ Curriculum

    // The panel's own scenarios print their replies in Run; this prints the ones the curriculum asked for.
    void HandleResponse(CoachResult r)
    {
        if (coach.LastContext?.curriculum == null || string.IsNullOrEmpty(r.say)) return;
        Print($"Coach ({CoachContext.TriggerName(r.trigger)}): \"{r.say}\"");
    }

    void HandleState(CurriculumState state)
    {
        string where = state == CurriculumState.SongChain ? $" bars {engine.CurrentChunk} (chunk {engine.ChunkIndex + 1}/{engine.Chunks.Count})"
                     : state == CurriculumState.SongDrill ? $" bar {engine.DrillBar}" : "";
        Print("Lesson: " + state + where);
    }

    void HandleEvaluated(EvaluationResult r)
    {
        var bars = new List<string>();
        foreach (var b in r.bars) bars.Add($"{b.barNumber}:{b.score:0.##}/{b.timingScore:0.##}" + (b.hesitations.Count > 0 ? "p" : ""));
        Print($"  accuracy {r.overallScore:0.##}, timing {r.timingScore:0.##}  [bar:acc/timing {string.Join(" ", bars)}]");
    }

    void SimulateRun(bool clean)
    {
        if (engine.State == CurriculumState.Idle || engine.State == CurriculumState.SongComplete)
        {
            Print("Start the lesson first.");
            return;
        }
        var failed = new List<int>();
        if (!clean)
            foreach (var part in failedBarsText.Split(new[] { ',', ' ', ';' }, System.StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out int bar)) failed.Add(bar);
        float acc = clean ? 1f : simAccuracy, timing = clean ? 1f : simTiming;
        bool pause = !clean && simPause;
        Print($"Simulating at {engine.TempoPct}%: accuracy {acc:0.##}, timing {timing:0.##}" +
              (failed.Count > 0 ? $", missing bar(s) {string.Join(", ", failed)}" : "") + (pause ? ", with a long pause" : "") + "...");
        if (!engine.SimulateRun(acc, timing, failed, pause)) Print("(no run to play right now)");
    }

    string LessonStatus()
    {
        if (engine.State == CurriculumState.Idle) return "not started";
        string where = engine.State == CurriculumState.SongChain ? $"chunk {engine.ChunkIndex + 1}/{engine.Chunks.Count}: bars {engine.CurrentChunk}"
                     : engine.State == CurriculumState.SongDrill ? $"drilling bar {engine.DrillBar} ({engine.DrillFails} misses)"
                     : engine.State.ToString();
        if (engine.State == CurriculumState.SongComplete) return where;
        return $"{where} | tempo {engine.TempoPct}% (step {engine.TempoStep + 1}), clean streak {engine.CleanStreak}" +
               (engine.StepFails > 0 ? $", {engine.StepFails} miss in a row" : "");
    }

    async void AskCoach(string question)
    {
        if (coach.IsThinking) return;
        var sw = Stopwatch.StartNew();
        var r = await coach.Ask(question, current);
        Print($"Coach ({sw.ElapsedMilliseconds} ms): \"{r.say}\"");
    }

    void StartTalk()
    {
        if (voiceInput.IsListening) return;
        voiceInput.StartListening();
        if (voiceInput.IsListening) Print("Mic: listening...");
    }

    void StopTalk()
    {
        if (!voiceInput.IsListening) return;
        voiceInput.StopListening();
        Print("Mic: transcribing...");
    }

    void HandleCommand(VoiceCommand command, string transcript)
    {
        Print($"Voice: {command}  (\"{transcript}\")");
        if (command == VoiceCommand.HowDidIDo)
        {
            var ctx = current.Clone();
            ctx.trigger = CoachTrigger.RunFinished;
            ctx.lastFeedback = null;
            Run("How did I do", ctx);
        }
        else if (command == VoiceCommand.Unknown && !string.IsNullOrWhiteSpace(transcript)) AskCoach(transcript);
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null || GUIUtility.keyboardControl != 0) return; // typing in the failed-bars box (Enter leaves it)
        if (kb.digit1Key.wasPressedThisFrame) Run("First try", FirstTry());
        if (kb.digit2Key.wasPressedThisFrame) Run("Improving", Improving());
        if (kb.digit3Key.wasPressedThisFrame) SimulateStuck();
        if (kb.digit4Key.wasPressedThisFrame) Run("Aced it", AcedIt());
        if (kb.digit5Key.wasPressedThisFrame) Run("Lesson start", LessonStart());
        if (kb.digit6Key.wasPressedThisFrame) SimulateRun(false);
        if (kb.digit7Key.wasPressedThisFrame) SimulateRun(true);
        if (kb.spaceKey.wasPressedThisFrame) StartTalk();
        if (kb.spaceKey.wasReleasedThisFrame) StopTalk();
#endif
    }

    void Print(string line)
    {
        Debug.Log("[AI Debug] " + line);
        lines.Add(line);
        if (lines.Count > 10) lines.RemoveAt(0);
    }

    void OnGUI()
    {
        float scale = Mathf.Max(1f, Screen.height / 540f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

        GUILayout.BeginArea(new Rect(10, 10, 640, 520), GUI.skin.box);
        GUILayout.Label("AI Debug Panel  (prompt: Assets/Resources/CoachPrompt.txt)");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("First try (1)", GUILayout.Height(28))) Run("First try", FirstTry());
        if (GUILayout.Button("Improving (2)", GUILayout.Height(28))) Run("Improving", Improving());
        if (GUILayout.Button("Stuck on bar 3 (3)", GUILayout.Height(28))) SimulateStuck();
        if (GUILayout.Button("Aced it (4)", GUILayout.Height(28))) Run("Aced it", AcedIt());
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Lesson start (5)", GUILayout.Height(28))) Run("Lesson start", LessonStart());
        if (GUILayout.Button(voiceInput.IsListening ? "Stop recording" : "Record mic (or hold Space)", GUILayout.Height(28)))
        {
            if (voiceInput.IsListening) StopTalk();
            else StartTalk();
        }
        if (GUILayout.Button("Stop speaking", GUILayout.Height(28))) voiceOutput.Stop();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        coach.speakReplies = GUILayout.Toggle(coach.speakReplies, " Speak the coach's replies");
        engine.sendToCoach = GUILayout.Toggle(engine.sendToCoach, " Lesson talks to the coach");
        GUILayout.EndHorizontal();

        GUILayout.Label("Lesson: " + LessonStatus() + (engine.IsDemoPlaying ? "  (demo playing)" : ""));
        if (engine.EvaluatorErrors > 0)
        {
            var color = GUI.color;
            GUI.color = Color.red;
            GUILayout.Label($"EVALUATOR ERROR x{engine.EvaluatorErrors} (runs counted as clean): {engine.LastEvaluatorError}");
            GUI.color = color;
        }
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(engine.State == CurriculumState.Idle ? "Start lesson" : "Restart lesson", GUILayout.Height(28))) engine.StartLesson();
        GUILayout.Label("Fail bars:", GUILayout.Width(62));
        // Enter in the box simulates the run and gives the number keys back to the shortcuts.
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return && GUI.GetNameOfFocusedControl() == "failBars")
        {
            GUIUtility.keyboardControl = 0;
            SimulateRun(false);
        }
        GUI.SetNextControlName("failBars");
        failedBarsText = GUILayout.TextField(failedBarsText, GUILayout.Width(70), GUILayout.Height(24));
        if (GUILayout.Button("Simulate run (6)", GUILayout.Height(28))) SimulateRun(false);
        if (GUILayout.Button("Clean run (7)", GUILayout.Height(28))) SimulateRun(true);
        if (GUILayout.Button("Skip wait", GUILayout.Height(28))) engine.FastForward();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Accuracy {simAccuracy:0.00}", GUILayout.Width(95));
        simAccuracy = Mathf.Round(GUILayout.HorizontalSlider(simAccuracy, 0f, 1f, GUILayout.Width(150)) * 20f) / 20f;
        GUILayout.Label($"Timing {simTiming:0.00}", GUILayout.Width(85));
        simTiming = Mathf.Round(GUILayout.HorizontalSlider(simTiming, 0f, 1f, GUILayout.Width(150)) * 20f) / 20f;
        simPause = GUILayout.Toggle(simPause, " Long pause");
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        foreach (string line in lines) GUILayout.Label(line);
        GUILayout.EndArea();
    }
}
