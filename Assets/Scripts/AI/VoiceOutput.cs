using System;
using System.Collections.Generic;
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

    GroqTts groq;
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
    public Task Speak(string text) => Speak(text, null);

    // The line, then these clips (cached words: SpokenWords) with a short gap between them, all as one utterance:
    // IsSpeaking stays true until the last clip ends, so whoever waits for the coach waits for all of it.
    public async Task Speak(string text, IReadOnlyList<AudioClip> then, float gap = 0.06f, float clipPitch = 1.15f)
    {
        bool hasClips = then != null && then.Count > 0;
        if (string.IsNullOrWhiteSpace(text) && !hasClips) return;

        Stop();
        int id = speakId;
        Func<bool> cancelled = () => this == null || id != speakId;
        IsSpeaking = true;

        try
        {
            var cfg = Cfg;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool spoken = false;

            if (string.IsNullOrWhiteSpace(text)) spoken = true; // clips only
            else if (cfg.ttsProvider == TtsProviderType.Groq)
            {
                if (groq == null) groq = new GroqTts(cfg, await AISecrets.Load());
                if (!cancelled()) spoken = await groq.Speak(text, source, cancelled);
            }

            if (!spoken && !cancelled() && (cfg.ttsProvider == TtsProviderType.Native || cfg.useFallbackProviders))
                spoken = await native.Speak(text, source, cancelled);

            if (!spoken && !cancelled()) Debug.LogWarning("[AI] TTS could not speak: " + text);
            if (hasClips)
            {
                var textDone = sw.ElapsedMilliseconds;
                source.pitch = clipPitch; // single words, a little quicker than the model speaks them
                foreach (var clip in then)
                {
                    if (cancelled() || clip == null) continue;
                    source.clip = clip;
                    source.Play();
                    while (!cancelled() && source.isPlaying) await Awaitable.NextFrameAsync();
                    if (!cancelled()) await Awaitable.WaitForSecondsAsync(gap);
                }
                source.pitch = 1f;
                Debug.Log($"[AI] Readout clips: {then.Count} words in {sw.ElapsedMilliseconds - textDone} ms.");
            }
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
        if (source != null) { source.Stop(); source.pitch = 1f; }
        native.Stop();
    }
}
