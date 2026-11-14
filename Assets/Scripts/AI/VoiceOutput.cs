using System;
using System.Threading.Tasks;
using UnityEngine;

// Speaks a line. A new Speak() call cuts off the current one.
public class VoiceOutput : MonoBehaviour
{
    [Tooltip("Optional. Defaults to Assets/Resources/AIConfig.asset.")]
    public AIConfig config;
    [Tooltip("Optional. Uses (or adds) an AudioSource on this GameObject.")]
    public AudioSource source;

    public bool IsSpeaking { get; private set; }

    GeminiTts gemini;
    readonly NativeTts native = new NativeTts();
    int speakId;

    AIConfig Cfg => config != null ? config : AIConfig.Get();

    void Awake()
    {
        if (source == null) source = GetComponent<AudioSource>();
        if (source == null) source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
    }

    void OnDestroy() => Stop();

    // Completes when the line finished playing or was replaced by a newer one.
    public async Task Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Stop();
        int id = speakId;
        Func<bool> cancelled = () => this == null || id != speakId;
        IsSpeaking = true;

        try
        {
            var cfg = Cfg;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool spoken = false;

            if (cfg.ttsProvider == TtsProviderType.GeminiTts)
            {
                if (gemini == null) gemini = new GeminiTts(cfg, await AISecrets.Load());
                if (!cancelled()) spoken = await gemini.Speak(text, source, cancelled);
            }

            if (!spoken && !cancelled() && (cfg.ttsProvider == TtsProviderType.Native || cfg.useFallbackProviders))
                spoken = await native.Speak(text, source, cancelled);

            if (!spoken && !cancelled()) Debug.LogWarning("[AI] TTS could not speak: " + text);
            Debug.Log($"[AI] Speak finished after {sw.ElapsedMilliseconds} ms (request + playback).");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AI] TTS error: " + e.Message);
        }
        finally
        {
            if (id == speakId) IsSpeaking = false;
        }
    }

    public void Stop()
    {
        speakId++;
        IsSpeaking = false;
        if (source != null) source.Stop();
        native.Stop();
    }
}
