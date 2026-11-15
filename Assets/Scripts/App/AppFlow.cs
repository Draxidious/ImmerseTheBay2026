using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Runs the app in order and connects the modules; it owns no lesson or AI logic of its own.
///   1. The coach speaks an introduction and asks if the player is ready.
///   2. The microphone opens for a few seconds. Any answer (or none) moves on.
///   3. Calibration starts (PianoCalibrator.Recalibrate).
///   4. Once calibrated, the evaluator and its sheet panel are switched on.
///   5. During the lesson the key to press next is lit on the overlay; right and wrong presses flash.
/// The lines here are fixed text: no model call, so the start of the app never waits on the network.
/// Editor debug key: Enter = skip the current wait (speech or listening). Calibrate with the calibrator's own C key.
/// </summary>
public class AppFlow : MonoBehaviour
{
    public enum Step { Intro, Listening, Calibrating, Lesson }

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

    [Header("Sheet placement (meters from the keyboard's drawn center)")]
    [Tooltip("X = along the keys, Y = above the keys, Z = toward the fallboard. Not scaled with calibration.")]
    public Vector3 sheetOffset = new Vector3(0f, 0.45f, 0.20f);

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
    }

    void OnEnable()
    {
        if (calibrator) calibrator.Calibrated += OnCalibrated;
        if (voiceInput) voiceInput.OnCommand += OnAnswer;
        if (evaluator)
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
        if (evaluator)
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
        if (evaluator) evaluator.gameObject.SetActive(false);

        CurrentStep = Step.Intro;
        yield return Wait(startDelay);
        yield return Say(introLine);

        CurrentStep = Step.Listening;
        if (voiceInput)
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
        if (evaluator) evaluator.gameObject.SetActive(true);
        StartCoroutine(Say(lessonLine));
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

    // ------------------------------------------------------------------ Finished run -> coach (outside the note loop)

    async void OnSongFinished(RunStats stats)
    {
        if (!coach || !evaluator || evaluator.Song == null) return;

        // Describe exactly the right-hand excerpt the evaluator judged, including its pickup.
        // Use a separate cache id from the curriculum's song, which also has fingering data.
        var source = evaluator.Song;
        var song = new SongData
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

#if UNITY_EDITOR
    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.enterKey.wasPressedThisFrame) _skip = true;
    }
#endif
}
