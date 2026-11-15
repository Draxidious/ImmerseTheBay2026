using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Editor-only free play driver: buttons that switch mode and play scripted phrases into FreePlayController.Feed,
// so free play and its coach comments can be tested without the piano. Buttons only (no keys: they clash with the evaluator).
public class FreePlayDebugPanel : MonoBehaviour
{
#if UNITY_EDITOR
    public FreePlayController controller;
    public ModeChoicePanel panel;

    readonly HashSet<int> _held = new HashSet<int>();
    Coroutine _running;
    string _summary = "(no phrase yet)";
    Rect _window;
    const int WindowId = 0x46504442; // "FPDB"

    static readonly int[][] Chords =
    {
        new[] { 48, 60, 64, 67 }, // C
        new[] { 53, 60, 65, 69 }, // F
        new[] { 55, 59, 62, 67 }, // G
        new[] { 48, 60, 64, 67 }, // C
    };
    static readonly int[] FurElise = { 76, 75, 76, 75, 76, 71, 74, 72, 69 };
    static readonly int[] CMajor = { 60, 62, 64, 65, 67, 69, 71, 72 };

    void Awake()
    {
        if (!controller) controller = GetComponent<FreePlayController>();
        if (!controller) controller = FindAnyObjectByType<FreePlayController>(FindObjectsInactive.Include);
        if (!panel) panel = GetComponent<ModeChoicePanel>();
        if (!panel) panel = FindAnyObjectByType<ModeChoicePanel>(FindObjectsInactive.Include);
        _window = new Rect(Screen.width - 250, 10, 240, 10);
    }

    void OnEnable() { if (controller) controller.PhraseFinished += OnPhrase; }
    void OnDisable() { if (controller) controller.PhraseFinished -= OnPhrase; }

    void OnPhrase(PhraseAnalysis p) => _summary = FreePlayFacts.Describe(p);

    void OnGUI()
    {
        if (!controller) return;
        _window.x = Screen.width - _window.width - 10;
        _window = GUILayout.Window(WindowId, _window, DrawWindow, "Free Play (debug)");
    }

    void DrawWindow(int id)
    {
        GUILayout.Label((controller.IsActive ? "Active" : "Inactive") + (AppModeSwitch.Current.HasValue ? $" (mode {AppModeSwitch.Current})" : ""));
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Start Free Play")) SwitchTo(AppMode.FreePlay);
        if (GUILayout.Button("Back to Lesson")) SwitchTo(AppMode.Lesson);
        GUILayout.EndHorizontal();
        if (panel && GUILayout.Button("Show mode panel")) panel.ShowChoice();

        GUI.enabled = controller.IsActive;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("C–F–G–C")) Run(PlayChords());
        if (GUILayout.Button("Für Elise")) Run(PlayMelody(FurElise, 0.25f));
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Uneven run")) Run(PlayUneven());
        GUI.enabled = true;
        if (GUILayout.Button("Stop")) Stop();
        GUILayout.EndHorizontal();

        GUILayout.Label(_running != null ? "Playing..." : "Last phrase:");
        GUILayout.Label(_summary, GUILayout.Width(220));
        GUI.DragWindow();
    }

    // Through AppModeSwitch once AppFlow handles it; until then drive the controller directly.
    void SwitchTo(AppMode mode)
    {
        if (AppModeSwitch.HasHandler) { AppModeSwitch.Request(mode, "debug"); return; }
        Stop();
        if (mode == AppMode.FreePlay) controller.Begin(); else controller.End();
        AppModeSwitch.Current = mode;
        AppModeSwitch.AfterCalibration = true;
        Debug.Log($"[FreePlay] Debug: {mode} (no AppModeSwitch handler, driving the controller directly).");
    }

    // ------------------------------------------------------------------ Sequences

    void Run(IEnumerator sequence)
    {
        Stop();
        _running = StartCoroutine(Wrap(sequence));
    }

    IEnumerator Wrap(IEnumerator sequence)
    {
        yield return sequence;
        // Wait for the phrase gap so the phrase is analysed (and maybe commented) before the next one.
        yield return new WaitForSecondsRealtime(AIConfig.Get().freePlayPhraseGapSeconds + 0.3f);
        _running = null;
    }

    void Stop()
    {
        if (_running != null) StopCoroutine(_running);
        _running = null;
        foreach (var midi in new List<int>(_held)) Off(midi);
    }

    void On(int midi, int velocity = 80) { _held.Add(midi); controller.Feed(midi, velocity, true); }
    void Off(int midi) { _held.Remove(midi); controller.Feed(midi, 0, false); }

    // Block chords, one every 0.9 s, each held 0.8 s.
    IEnumerator PlayChords()
    {
        foreach (var chord in Chords)
        {
            foreach (var m in chord) On(m);
            yield return new WaitForSecondsRealtime(0.8f);
            foreach (var m in chord) Off(m);
            yield return new WaitForSecondsRealtime(0.1f);
        }
    }

    IEnumerator PlayMelody(int[] notes, float step)
    {
        for (int i = 0; i < notes.Length; i++)
        {
            On(notes[i]);
            bool last = i == notes.Length - 1;
            yield return new WaitForSecondsRealtime(last ? step * 2f : step * 0.8f);
            Off(notes[i]);
            if (!last) yield return new WaitForSecondsRealtime(step * 0.2f);
        }
    }

    // About 12 notes of C major with jittered gaps (0.12 to 0.6 s).
    IEnumerator PlayUneven()
    {
        int count = Random.Range(11, 14);
        for (int i = 0; i < count; i++)
        {
            int midi = CMajor[Random.Range(0, CMajor.Length)];
            float gap = Random.Range(0.12f, 0.6f);
            On(midi, Random.Range(50, 100));
            yield return new WaitForSecondsRealtime(gap * 0.8f);
            Off(midi);
            yield return new WaitForSecondsRealtime(gap * 0.2f);
        }
    }
#endif
}
