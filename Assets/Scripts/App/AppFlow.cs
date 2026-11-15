using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Runs the app in order and connects the modules; it owns no lesson or AI logic of its own.
///   0. On Android, resolve microphone permission before greeting the player.
///   1. The coach speaks an introduction and asks if the player is ready.
///   2. The microphone opens for a few seconds. Any answer (or none) moves on.
///   3. Calibration starts (PianoCalibrator.Recalibrate).
///   4. Once calibrated, the evaluator and its sheet panel are switched on.
///   5. During the lesson the key to press next is lit on the overlay; right and wrong presses flash.
/// The lines here are fixed text: no model call, so the start of the app never waits on the network.
/// Two kinds of lesson, depending on what is in the scene:
///   - a CurriculumEngine (Main_2): the guided lesson of COACH_PLAYBOOK.md, in timed runs. The engine talks to the coach
///     itself. Left-hand keys are lit amber, right-hand keys cyan.
///   - only a PerformanceEvaluator (Main): the whole excerpt once in wait mode, then one summary to the coach.
/// Editor debug keys: Enter = skip the current wait (speech, listening, a demo or the coach before a run).
/// With a CurriculumEngine, end the current run at once as: F5 = clean, F6 = one wrong note (Shaky),
/// F7 = two wrong notes in the last measure (Problem), F8 = clean but 30% too slow. Calibrate with the calibrator's own C key.
/// </summary>
public class AppFlow : MonoBehaviour
{
    public enum Step { Intro, Listening, Calibrating, Lesson, MicrophonePermission }

    [Header("Found automatically when empty")]
    public VoiceOutput voiceOutput;
    public VoiceInput voiceInput;
    public AICoach coach;
    public CoachOrb orb;
    public PianoCalibrator calibrator;
    public PianoOverlay overlay;
    [Tooltip("The evaluator object starts inactive in the scene and is switched on after calibration.")]
    public PerformanceEvaluator evaluator;
    public SongNoteDisplay sheetDisplay;
    [Tooltip("The guided lesson. When there is one in the scene it runs the lesson and the evaluator is switched off.")]
    public CurriculumEngine engine;

    [Header("Sheet placement (meters from the keyboard's drawn center)")]
    [Tooltip("X = along the keys, Y = above the keys, Z = toward the fallboard. Not scaled with calibration.")]
    public Vector3 sheetOffset = new Vector3(0f, 0.45f, 0.20f);
    [Tooltip("Clear space between the sheet panel's right edge and the coach orb's text, in meters.")]
    public float orbPanelGap = 0.04f;

    [Header("Lines")]
    [TextArea] public string introLine = "Hi, I'm your piano coach. Today we'll play the opening of Für Elise. Are you ready to learn?";
    [TextArea] public string calibrateLine = "Great. First show me where your piano is. Follow the steps in front of you.";
    [TextArea] public string lessonLine = "Got it. Play the first note whenever you're ready.";

    [Header("Timing (seconds)")]
    [Tooltip("Wait before the introduction, so tracking and the orb have settled.")]
    public float startDelay = 1.5f;
    [Tooltip("How long the microphone stays open for the player's answer.")]
    public float listenSeconds = 4f;
    [Tooltip("Longest wait for the answer to be transcribed before moving on anyway.")]
    public float transcriptTimeout = 6f;

    public Step CurrentStep { get; private set; }

    bool _skip, _answered;
    readonly Dictionary<int, double> _litUntil = new Dictionary<int, double>(); // keys lit as "next", and when to turn them off
    readonly List<int> _expired = new List<int>();
    readonly LessonHistory _history = new LessonHistory();

    void Awake()
    {
        if (!voiceOutput) voiceOutput = FindAnyObjectByType<VoiceOutput>();
        if (!voiceInput) voiceInput = FindAnyObjectByType<VoiceInput>();
        if (!coach) coach = FindAnyObjectByType<AICoach>();
        if (!orb) orb = FindAnyObjectByType<CoachOrb>();
        if (!calibrator) calibrator = FindAnyObjectByType<PianoCalibrator>();
        if (!overlay) overlay = FindAnyObjectByType<PianoOverlay>();
        if (!evaluator) evaluator = FindAnyObjectByType<PerformanceEvaluator>(FindObjectsInactive.Include);
        if (!sheetDisplay && evaluator) sheetDisplay = evaluator.GetComponent<SongNoteDisplay>();
        if (!sheetDisplay) sheetDisplay = FindAnyObjectByType<SongNoteDisplay>(FindObjectsInactive.Include);
        if (!engine) engine = FindAnyObjectByType<CurriculumEngine>(FindObjectsInactive.Include);
        if (engine)
        {
            // The engine judges the lesson; the wait-mode evaluator would listen to the same keys.
            // The panel keeps the sheet images set in the scene: they are numbered like the engine's bars.
            if (evaluator) evaluator.enabled = false;
            if (!engine.coach) engine.coach = coach;
        }
        else if (sheetDisplay && evaluator)
        {
            // Show the generated sheet music of the evaluator's song (Assets/Resources/Sheets, one strip per line).
            // Keeps the images set in the scene if there are none.
            LessonSongSource.ApplySheets(sheetDisplay, evaluator.songId, evaluator.songSection);
        }
    }

    // What is switched on once the piano is calibrated: the sheet panel (and, in Main, the evaluator on the same object).
    GameObject LessonPanel => sheetDisplay ? sheetDisplay.gameObject : evaluator ? evaluator.gameObject : null;

    void OnEnable()
    {
        if (calibrator) calibrator.Calibrated += OnCalibrated;
        if (voiceInput) voiceInput.OnCommand += OnAnswer;
        if (engine)
        {
            engine.OnRunStarted += OnRunStarted;
            engine.OnNextNote += OnNoteDue;
            engine.OnNotePlayed += OnNotePlayed;
            engine.OnDemoNoteDown += OnDemoNoteDown;
            engine.OnDemoNoteUp += OnDemoNoteUp;
        }
        else if (evaluator)
        {
            evaluator.Restarted += ShowNextKey;
            evaluator.NoteJudged += OnNoteJudged;
            evaluator.WrongNotePlayed += OnWrongNote;
            evaluator.SongFinished += OnSongFinished;
        }
    }

    void OnDisable()
    {
        if (calibrator) calibrator.Calibrated -= OnCalibrated;
        if (voiceInput) voiceInput.OnCommand -= OnAnswer;
        if (engine)
        {
            engine.OnRunStarted -= OnRunStarted;
            engine.OnNextNote -= OnNoteDue;
            engine.OnNotePlayed -= OnNotePlayed;
            engine.OnDemoNoteDown -= OnDemoNoteDown;
            engine.OnDemoNoteUp -= OnDemoNoteUp;
        }
        else if (evaluator)
        {
            evaluator.Restarted -= ShowNextKey;
            evaluator.NoteJudged -= OnNoteJudged;
            evaluator.WrongNotePlayed -= OnWrongNote;
            evaluator.SongFinished -= OnSongFinished;
        }
    }

    IEnumerator Start()
    {
        // Nothing of the lesson is shown until the player has been greeted.
        if (overlay) overlay.gameObject.SetActive(false);
        if (LessonPanel) LessonPanel.SetActive(false);

        CurrentStep = Step.MicrophonePermission;
        yield return RequestMicrophonePermission();
        CurrentStep = Step.Intro;
        yield return Wait(startDelay);
        yield return Say(introLine);

        CurrentStep = Step.Listening;
        if (voiceInput && HasMicrophonePermission())
        {
            _answered = false;
            voiceInput.StartListening();
            if (voiceInput.IsListening)
            {
                yield return Wait(listenSeconds);
                voiceInput.StopListening(); // OnCommand follows once the recording is transcribed
                for (float t = 0f; !_answered && !_skip && t < transcriptTimeout; t += Time.unscaledDeltaTime) yield return null;
            }
        }

        CurrentStep = Step.Calibrating;
        if (overlay) overlay.gameObject.SetActive(true);
        if (calibrator) calibrator.Recalibrate();
        yield return Say(calibrateLine);
    }

    static bool HasMicrophonePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return Permission.HasUserAuthorizedPermission(Permission.Microphone);
#else
        return true;
#endif
    }

    IEnumerator RequestMicrophonePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!voiceInput || HasMicrophonePermission()) yield break;

        Debug.Log("[AppFlow] Requesting microphone permission before the introduction.");
        bool resolved = false;
        var callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += _ => resolved = true;
        callbacks.PermissionDenied += _ => resolved = true;
        callbacks.PermissionDeniedAndDontAskAgain += _ => resolved = true;
        callbacks.PermissionRequestDismissed += _ => resolved = true;
        Permission.RequestUserPermission(Permission.Microphone, callbacks);
        // Wait for the actual decision, not a timeout while the player is reading the system dialog.
        while (!resolved) yield return null;
        Debug.Log(HasMicrophonePermission()
            ? "[AppFlow] Microphone permission granted; starting introduction."
            : "[AppFlow] Microphone permission not granted; continuing without listening.");
#endif
        yield break;
    }

    void OnAnswer(VoiceCommand command, string transcript)
    {
        _answered = true;
        Debug.Log($"[AppFlow] Player answered: \"{transcript}\"");
    }

    // Also raised after a later recalibration; the lesson is only started the first time.
    void OnCalibrated()
    {
        PlaceSheetPanel();
        if (CurrentStep != Step.Calibrating) return;
        CurrentStep = Step.Lesson;
        if (LessonPanel) LessonPanel.SetActive(true); // before the lesson starts, so the panel hears its first events
        if (engine) engine.StartLesson();             // the coach introduces the piece itself
        else StartCoroutine(Say(lessonLine));
    }

    void PlaceSheetPanel()
    {
        if (!overlay || !sheetDisplay) return;
        Vector3 center = (overlay.GetKeyWorldCenter(overlay.lowestNote) +
                          overlay.GetKeyWorldCenter(overlay.highestNote)) * 0.5f;
        // Canvas text faces local -Z, toward the player. Keep the panel upright and aligned
        // with the keyboard's yaw; don't inherit calibration scale or side-to-side tilt.
        Vector3 forward = Vector3.ProjectOnPlane(overlay.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f) return;
        Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);
        sheetDisplay.transform.SetPositionAndRotation(center + rotation * sheetOffset, rotation);

        // The orb's words appear to the left of the orb. Put the orb far enough right of the keyboard's center that
        // they start past the panel's right edge instead of lying over its status text.
        if (orb)
        {
            float panelRight = sheetOffset.x + sheetDisplay.panelSize.x * sheetDisplay.metersPerUnit * 0.5f;
            // The text turns to face the player, so out here it takes up about 0.8 of its width along the keyboard.
            float textReach = orb.orbDiameter * 0.5f + orb.textGap + orb.textWidth * 0.8f;
            orb.rightOfKeyboardCenter = Mathf.Max(orb.rightOfKeyboardCenter, panelRight + orbPanelGap + textReach);
        }
    }

    // ------------------------------------------------------------------ Evaluator -> piano overlay

    void ShowNextKey()
    {
        if (overlay && evaluator && !evaluator.IsFinished) overlay.SetKey(evaluator.CurrentNote.midi, KeyState.Next);
    }

    void OnNoteJudged(NoteResult result)
    {
        if (overlay) overlay.SetKey(result.note.midi, KeyState.Correct); // flashes, then goes off by itself
        ShowNextKey();
    }

    void OnWrongNote(int expected, int played)
    {
        if (overlay) overlay.SetKey(played, KeyState.Wrong);
    }

    // ------------------------------------------------------------------ CurriculumEngine -> piano overlay

    // A run is about to start (the count-in follows): show where the first note is, in its hand's colour.
    void OnRunStarted(RunInfo run)
    {
        if (!overlay) return;
        overlay.ClearAll();
        _litUntil.Clear();
        if (run.notes.Count == 0) return;
        foreach (var note in run.notes)
        {
            if (note.time > run.notes[0].time + 0.001f) break;
            overlay.SetKey(note.midi, KeyState.Upcoming, note.hand);
        }
    }

    // This note is due now: light its key until the note is over, and show the one after it as coming up.
    void OnNoteDue(ExpectedNote note)
    {
        if (!overlay) return;
        if (note == null) // the run is over
        {
            overlay.ClearAll();
            _litUntil.Clear();
            return;
        }
        var run = engine.CurrentRun;
        overlay.SetKey(note.midi, KeyState.Next, note.hand);
        _litUntil[note.midi] = run.startTime + note.time + Mathf.Max(0.3f, note.duration * run.secondsPerBeat);

        int next = note.index + 1;
        if (next < run.notes.Count && !_litUntil.ContainsKey(run.notes[next].midi))
            overlay.SetKey(run.notes[next].midi, KeyState.Upcoming, run.notes[next].hand);
    }

    void OnNotePlayed(ExpectedNote note, int key, bool correct)
    {
        if (!overlay) return;
        _litUntil.Remove(key);
        overlay.SetKey(key, correct ? KeyState.Correct : KeyState.Wrong); // flashes, then goes off by itself
    }

    void OnDemoNoteDown(ExpectedNote note)
    {
        if (overlay) overlay.SetKey(note.midi, KeyState.Next, note.hand);
    }

    void OnDemoNoteUp(ExpectedNote note)
    {
        if (overlay) overlay.SetKey(note.midi, KeyState.Off);
    }

    void TurnOffPlayedOutKeys()
    {
        if (_litUntil.Count == 0 || !overlay) return;
        double now = Time.unscaledTimeAsDouble;
        _expired.Clear();
        foreach (var kv in _litUntil) if (kv.Value <= now) _expired.Add(kv.Key);
        foreach (int midi in _expired)
        {
            _litUntil.Remove(midi);
            overlay.SetKey(midi, KeyState.Off);
        }
    }

    // ------------------------------------------------------------------ Finished run -> coach (outside the note loop)

    async void OnSongFinished(RunStats stats)
    {
        if (!coach || !evaluator || evaluator.Song == null) return;

        // Describe exactly the right-hand excerpt the evaluator judged, including its pickup: from the song database
        // (Assets/Resources/SongDB) when the evaluator's song is there, otherwise built from the evaluator's own notes.
        var song = LessonSongSource.ForCoach(evaluator.songId, evaluator.songSection);
        if (song == null)
        {
            // Use a separate cache id from the curriculum's song, which also has fingering data.
            var source = evaluator.Song;
            song = new SongData
            {
                id = "wait_mode_fur_elise", name = source.name,
                tempoBpm = Mathf.RoundToInt(source.bpm), beatsPerBar = source.beatsPerBar,
                timeSignature = "3/8"
            };
            var barStarts = new Dictionary<int, float>();
            foreach (var note in source.notes)
            {
                if (!barStarts.ContainsKey(note.bar)) barStarts[note.bar] = note.beat;
                song.notes.Add(new SongNote
                {
                    midi = note.midi, bar = note.bar, beat = note.beat - barStarts[note.bar] + 1f,
                    beats = note.duration, hand = PianoHand.Right
                });
            }
            SongFormatter.Invalidate(song.id);
        }
        _history.attemptsThisSection++;
        _history.accuracyTrend.Add(stats.accuracy);
        if (_history.accuracyTrend.Count > 5) _history.accuracyTrend.RemoveAt(0);
        var context = new CoachContext
        {
            trigger = CoachTrigger.RunFinished, mode = "freeplay", song = song, thisRun = stats,
            currentSection = new SectionInfo { firstBar = 1, lastBar = song.BarCount, tempoPct = 100 },
            history = new LessonHistory
            {
                attemptsThisSection = _history.attemptsThisSection,
                accuracyTrend = new List<float>(_history.accuracyTrend)
            },
            // Main has no demo/fingering action player. Keep the feedback spoken only.
            allowedActions = new[] { CoachActionType.None }
        };
        try { await coach.Coach(context); }
        catch (System.Exception e) { Debug.LogWarning("[AppFlow] Coach feedback failed: " + e.Message); }
    }

    // ------------------------------------------------------------------ Helpers

    // Speaks a fixed line and shows it beside the orb. Ends when the speech does (or on the debug skip).
    IEnumerator Say(string line)
    {
        Debug.Log($"[AppFlow] {CurrentStep}: \"{line}\"");
        if (!voiceOutput)
        {
            if (orb) orb.ShowText(line);
            yield break;
        }
        if (orb) orb.ShowText(line, 600f); // stays up for as long as the speech takes
        var speech = voiceOutput.Speak(line);
        _skip = false;
        while (!speech.IsCompleted && !_skip) yield return null;
        if (orb && orb.DisplayedText.Length > 0 && line.StartsWith(orb.DisplayedText.TrimEnd('.')))
            orb.ShowText(line, 2f); // still our line: let it linger briefly, then fade
    }

    IEnumerator Wait(float seconds)
    {
        _skip = false;
        for (float t = 0f; t < seconds && !_skip; t += Time.unscaledDeltaTime) yield return null;
    }

    void Update()
    {
        TurnOffPlayedOutKeys();
#if UNITY_EDITOR
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.enterKey.wasPressedThisFrame)
        {
            _skip = true;
            if (engine && CurrentStep == Step.Lesson) engine.FastForward();
        }
        if (!engine || CurrentStep != Step.Lesson) return;

        // End the current run with made-up key presses, to walk the playbook without a piano.
        int lastBar = engine.State == CurriculumState.SongRepair ? engine.CurrentBlock.Last : engine.CurrentRange.Last;
        if (kb.f5Key.wasPressedThisFrame) engine.SimulateRun(null);
        if (kb.f6Key.wasPressedThisFrame) engine.SimulateRun(new Dictionary<int, int> { { lastBar, 1 } });
        if (kb.f7Key.wasPressedThisFrame) engine.SimulateRun(new Dictionary<int, int> { { lastBar, 2 } });
        if (kb.f8Key.wasPressedThisFrame) engine.SimulateRun(null, 0.7f);
#endif
    }
}
