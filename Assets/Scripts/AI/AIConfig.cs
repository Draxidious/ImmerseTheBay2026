using UnityEngine;

public enum CoachProviderType { Gemini, Groq }
public enum SttProviderType { Groq, Gemini }
public enum TtsProviderType { GeminiTts, Native } // Native = Android TextToSpeech on device, log (+ macOS "say") in the Editor

// One place to switch providers and model names. The asset lives at Assets/Resources/AIConfig.asset.
[CreateAssetMenu(menuName = "AI Coach/AI Config", fileName = "AIConfig")]
public class AIConfig : ScriptableObject
{
    [Header("Providers")]
    public CoachProviderType coachProvider = CoachProviderType.Gemini;
    public SttProviderType sttProvider = SttProviderType.Groq;
    public TtsProviderType ttsProvider = TtsProviderType.GeminiTts;
    [Tooltip("If the selected provider fails, try the other one (TTS falls back to Native).")]
    public bool useFallbackProviders = true;

    [Header("Model names (swap here if a call logs a 404)")]
    public string geminiCoachModel = "gemini-3.8-flash";
    public string groqCoachModel = "openai/gpt-oss-120b";
    public string groqSttModel = "whisper-large-v3-turbo";
    public string geminiSttModel = "gemini-3.8-flash";
    public string geminiTtsModel = "gemini-2.5-flash-preview-tts";
    public string geminiTtsVoice = "Kore";

    [Header("Timeouts (seconds)")]
    [Tooltip("Per coach HTTP request.")]
    public int coachRequestTimeout = 5;
    [Tooltip("Hard cap for GetFeedback including the fallback provider. After this the canned line is returned.")]
    public float coachTotalTimeout = 8f;
    public int sttRequestTimeout = 6;
    public int ttsRequestTimeout = 8;

    [Header("Voice input")]
    public int maxRecordSeconds = 8;

    [Header("Coach")]
    public string fallbackLine = "Nice work, let's try that again";
    [TextArea(6, 20)]
    public string coachSystemPrompt =
        "You are a friendly, encouraging piano coach talking to a student right after they played a song. " +
        "You receive their performance metrics as JSON. " +
        "Reply ONLY with a JSON object: {\"say\": string, \"action\": \"loop_bar\" | \"slow_down\" | \"next_song\" | \"none\", \"bar\": integer}. " +
        "Rules for \"say\": at most 2 short sentences, name exactly ONE specific thing to fix (mention the bar or the note), " +
        "and make it sound natural when spoken aloud: no lists, symbols, or emoji. " +
        "Pick the action that helps most: loop_bar to repeat one trouble bar (set \"bar\" to that bar), " +
        "slow_down if timing is the main problem, next_song if they played it nearly perfectly, otherwise none. " +
        "Use 0 for \"bar\" unless the action is loop_bar.";

    static AIConfig cached;

    // Components use their inspector reference if set, otherwise this.
    public static AIConfig Get()
    {
        if (cached != null) return cached;
        cached = Resources.Load<AIConfig>("AIConfig");
        if (cached == null)
        {
            Debug.LogWarning("[AI] No Assets/Resources/AIConfig.asset found, using built-in defaults.");
            cached = CreateInstance<AIConfig>();
        }
        return cached;
    }
}
