using System;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public enum VoiceCommand { Again, Slower, HowDidIDo, NextSong, Unknown }

// Push-to-talk: StartListening() on press, StopListening() on release, then OnCommand fires.
public class VoiceInput : MonoBehaviour
{
    const int SampleRate = 16000;

    [Tooltip("Optional. Defaults to Assets/Resources/AIConfig.asset.")]
    public AIConfig config;

    // (command, transcript). Always fires once per StopListening(), with Unknown if nothing was understood.
    public event Action<VoiceCommand, string> OnCommand;

    public bool IsListening { get; private set; }

    AudioClip recording;

    AIConfig Cfg => config != null ? config : AIConfig.Get();

    public void StartListening()
    {
        if (IsListening) return;

#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Permission.RequestUserPermission(Permission.Microphone);
            Debug.LogWarning("[AI] Microphone permission requested. Press talk again once it is granted.");
            return;
        }
#endif
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[AI] No microphone found.");
            return;
        }

        recording = Microphone.Start(null, false, Mathf.Max(1, Cfg.maxRecordSeconds), SampleRate);
        IsListening = recording != null;
    }

    public void StopListening()
    {
        if (!IsListening) return;
        IsListening = false;

        int length = Microphone.GetPosition(null);
        bool stillRecording = Microphone.IsRecording(null);
        Microphone.End(null);
        if (!stillRecording || length <= 0) length = recording.samples; // hit the max length

        var samples = new float[length * recording.channels];
        recording.GetData(samples, 0);
        byte[] wav = WavUtility.Encode(samples, SampleRate, recording.channels);
        float seconds = (float)length / SampleRate;
        Destroy(recording);
        recording = null;

        _ = Process(wav, seconds);
    }

    async Task Process(byte[] wav, float seconds)
    {
        string transcript = "";
        try
        {
            if (seconds < 0.3f)
            {
                Debug.LogWarning("[AI] Recording too short, ignoring.");
            }
            else
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                transcript = await Transcribe(wav) ?? "";
                Debug.Log($"[AI] STT total {sw.ElapsedMilliseconds} ms for {seconds:0.0} s of audio -> \"{transcript}\"");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AI] STT error: " + e.Message);
        }

        if (this == null) return;
        var command = ParseCommand(transcript);
        Debug.Log("[AI] Voice command: " + command);
        OnCommand?.Invoke(command, transcript);
    }

    // Selected provider first, then the other one. Returns null if both fail.
    async Task<string> Transcribe(byte[] wav)
    {
        var cfg = Cfg;
        var keys = await AISecrets.Load();

        var order = cfg.useFallbackProviders
            ? new[] { cfg.sttProvider, AIProviders.Other(cfg.sttProvider) }
            : new[] { cfg.sttProvider };

        foreach (var type in order)
        {
            string text = await AIProviders.Stt(type, cfg, keys).Transcribe(wav);
            if (text != null) return text;
        }
        return null;
    }

    // Simple keyword matching, no LLM call.
    public static VoiceCommand ParseCommand(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return VoiceCommand.Unknown;
        string t = transcript.ToLowerInvariant();

        if (Has(t, "slow")) return VoiceCommand.Slower;
        if (Has(t, "next", "skip", "another song", "different song", "new song")) return VoiceCommand.NextSong;
        if (Has(t, "how did i", "how'd i", "how was", "how am i", "did i do", "feedback")) return VoiceCommand.HowDidIDo;
        if (Has(t, "again", "repeat", "one more", "retry", "redo", "restart")) return VoiceCommand.Again;
        return VoiceCommand.Unknown;
    }

    static bool Has(string text, params string[] keywords)
    {
        foreach (string k in keywords)
            if (text.Contains(k)) return true;
        return false;
    }

    void OnDisable()
    {
        if (!IsListening) return;
        IsListening = false;
        Microphone.End(null);
    }
}
