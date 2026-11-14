using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Debug = UnityEngine.Debug;

// Editor test harness for the AI layer, no glasses needed.
// Keys: 1 = send fake metrics, 2 = speak test line, 3 = metrics + speak the reply, hold Space = push-to-talk.
public class AIDebugPanel : MonoBehaviour
{
    public AICoach coach;
    public VoiceOutput voiceOutput;
    public VoiceInput voiceInput;

    public string testLine = "Nice job! Try bar three once more, and keep your left hand steady.";
    public bool speakCoachReplies = true;

    readonly List<string> lines = new List<string>();
    bool busy;

    void Awake()
    {
        if (coach == null) coach = GetOrAdd<AICoach>();
        if (voiceOutput == null) voiceOutput = GetOrAdd<VoiceOutput>();
        if (voiceInput == null) voiceInput = GetOrAdd<VoiceInput>();
    }

    T GetOrAdd<T>() where T : Component
    {
        var c = GetComponent<T>();
        return c != null ? c : gameObject.AddComponent<T>();
    }

    void OnEnable() => voiceInput.OnCommand += HandleCommand;
    void OnDisable() => voiceInput.OnCommand -= HandleCommand;

    public static PerformanceMetrics FakeMetrics()
    {
        return new PerformanceMetrics
        {
            songName = "Ode to Joy",
            wrongNotes = new List<WrongNote>
            {
                new WrongNote { bar = 3, expectedNote = "E4", playedNote = "F4" },
                new WrongNote { bar = 3, expectedNote = "G4", playedNote = "F#4" },
                new WrongNote { bar = 7, expectedNote = "D4", playedNote = "C4" },
            },
            avgTimingErrorMs = 85f,
            rushedBars = new List<int> { 5, 6 },
            draggedBars = new List<int>(),
            accuracyPercent = 82f,
        };
    }

    async void SendFakeMetrics(bool speak)
    {
        if (busy) return;
        busy = true;
        try
        {
            Print("Coach: sending fake metrics...");
            var sw = Stopwatch.StartNew();
            var r = await coach.GetCoachResponse(FakeMetrics());
            Print($"Coach ({sw.ElapsedMilliseconds} ms): \"{r.say}\"  action={r.action} bar={r.bar}");
            busy = false;
            if (speak) await voiceOutput.Speak(r.say);
        }
        finally
        {
            busy = false;
        }
    }

    async void SpeakTestLine()
    {
        Print("TTS: speaking test line...");
        var sw = Stopwatch.StartNew();
        await voiceOutput.Speak(testLine);
        Print($"TTS: done ({sw.ElapsedMilliseconds} ms including playback)");
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
        if (command == VoiceCommand.HowDidIDo) SendFakeMetrics(speakCoachReplies);
    }

    void Update()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.digit1Key.wasPressedThisFrame) SendFakeMetrics(false);
        if (kb.digit2Key.wasPressedThisFrame) SpeakTestLine();
        if (kb.digit3Key.wasPressedThisFrame) SendFakeMetrics(true);
        if (kb.spaceKey.wasPressedThisFrame) StartTalk();
        if (kb.spaceKey.wasReleasedThisFrame) StopTalk();
#endif
    }

    void Print(string line)
    {
        Debug.Log("[AI Debug] " + line);
        lines.Add(line);
        if (lines.Count > 12) lines.RemoveAt(0);
    }

    void OnGUI()
    {
        float scale = Mathf.Max(1f, Screen.height / 540f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

        GUILayout.BeginArea(new Rect(10, 10, 560, 520), GUI.skin.box);
        GUILayout.Label("AI Debug Panel");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Send fake metrics (1)", GUILayout.Height(28))) SendFakeMetrics(false);
        if (GUILayout.Button("Speak test line (2)", GUILayout.Height(28))) SpeakTestLine();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Metrics + speak reply (3)", GUILayout.Height(28))) SendFakeMetrics(true);
        if (GUILayout.Button(voiceInput.IsListening ? "Stop recording" : "Record mic (or hold Space)", GUILayout.Height(28)))
        {
            if (voiceInput.IsListening) StopTalk();
            else StartTalk();
        }
        GUILayout.EndHorizontal();

        if (GUILayout.Button("Stop speaking")) voiceOutput.Stop();
        speakCoachReplies = GUILayout.Toggle(speakCoachReplies, " Speak the coach reply after a \"how did I do\" voice command");

        GUILayout.Space(6);
        foreach (string line in lines) GUILayout.Label(line);
        GUILayout.EndArea();
    }
}
