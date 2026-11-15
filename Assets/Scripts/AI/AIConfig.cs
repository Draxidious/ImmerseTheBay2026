using UnityEngine;

public enum CoachProviderType { Claude, Groq }
public enum TtsProviderType { Groq, Native } // Native = Android TextToSpeech on device, log (+ macOS "say") in the Editor

// One place to switch providers and model names. The asset lives at Assets/Resources/AIConfig.asset.
[CreateAssetMenu(menuName = "AI Coach/AI Config", fileName = "AIConfig")]
public class AIConfig : ScriptableObject
{
    [Header("Providers")]
    public CoachProviderType coachProvider = CoachProviderType.Claude;
    public TtsProviderType ttsProvider = TtsProviderType.Groq;
    [Tooltip("If the selected provider fails, try the other one (TTS falls back to Native). Speech-to-text is always Groq Whisper.")]
    public bool useFallbackProviders = true;

    [Header("Model names (swap here if a call logs a 404)")]
    public string claudeCoachModel = "claude-haiku-5-5";
    public string groqCoachModel = "openai/gpt-oss-120b";
    public string groqSttModel = "whisper-large-v3-turbo";
    public string groqTtsModel = "canopylabs/orpheus-v1-english";
    public string groqTtsVoice = "hannah";
    [Tooltip("The Groq speech model only accepts short inputs, so longer lines are split at sentence breaks.")]
    public int groqTtsMaxChars = 200;

    [Header("Timeouts (seconds)")]
    [Tooltip("Per coach HTTP request.")]
    public int coachRequestTimeout = 5;
    [Tooltip("Hard cap for one coach reply including the fallback provider. After this the canned line is returned.")]
    public float coachTotalTimeout = 8f;
    public int sttRequestTimeout = 6;
    public int ttsRequestTimeout = 8;

    [Header("Voice input")]
    public int maxRecordSeconds = 8;

    [Header("Coach")]
    [Tooltip("The system prompt. Leave empty to use Assets/Resources/CoachPrompt.txt. Edit the text file to tune the coach, no recompile needed.")]
    public TextAsset coachPrompt;
    [Tooltip("Spoken when the model cannot be reached.")]
    public string fallbackLine = "Nice work, let's try that again";
    public string questionFallbackLine = "Sorry, I didn't catch that. Could you ask me again?";

    [Header("Wake word")]
    [Tooltip("Hands-free questions: say \"hey teacher\" (Android plugin), press Q, or pinch-hold the right hand.")]
    public bool wakeWordEnabled = true;
    [Tooltip("Keyword spotter boosting score.")]
    public float keywordsScore = 2.0f;
    [Tooltip("Keyword spotter trigger threshold. Lower = more sensitive.")]
    public float keywordsThreshold = 0.25f;
    [Tooltip("Frame RMS below this is never speech.")]
    public float minSpeechRms = 0.005f;
    [Tooltip("Speech = RMS above noise floor x this.")]
    public float vadNoiseRatio = 3f;
    [Tooltip("Silence that ends a question.")]
    public float endSilenceSeconds = 1f;
    public float maxQuestionSeconds = 8f;
    [Tooltip("Give up when no speech starts within this time.")]
    public float questionStartTimeoutSeconds = 3f;
    [Tooltip("Audio kept from before the wake word.")]
    public float preRollSeconds = 0.5f;
    [Tooltip("Triggers within this time of the previous one are ignored.")]
    public float wakeCooldownSeconds = 2f;
    [Tooltip("Triggers stay off this long after the coach stops speaking.")]
    public float muteAfterSpeechSeconds = 1f;
    [Tooltip("VOICE_COMMUNICATION enables call-mode echo cancellation; may change output volume/routing.")]
    public bool voiceCommunicationSource = false;
    [Tooltip("Echo canceller + noise suppressor on the mic, when the device has them.")]
    public bool audioEffects = true;
    [Tooltip("Editor/desktop: Q asks a question.")]
    public bool backupKeyEnabled = true;
    [Tooltip("Right-hand pinch held this long asks a question.")]
    public bool pinchHoldEnabled = true;
    public float pinchHoldSeconds = 0.6f;
    [Tooltip("Save raw mic audio after a trigger, to check levels (persistentDataPath).")]
    public bool saveMicDebugClip = false;
    public float micDebugClipSeconds = 10f;

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
