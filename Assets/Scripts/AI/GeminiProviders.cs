using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

// Google Gemini REST (generateContent) for coach, STT fallback and TTS.

static class Gemini
{
    [Serializable] public class Response { public Candidate[] candidates; }
    [Serializable] public class Candidate { public Content content; }
    [Serializable] public class Content { public Part[] parts; }
    [Serializable] public class Part { public string text; public InlineData inlineData; }
    [Serializable] public class InlineData { public string mimeType; public string data; }

    public static string Url(string model)
    {
        return "https://generativelanguage.googleapis.com/v1beta/models/" + model.Trim() + ":generateContent";
    }

    public static bool HasKey(AISecrets keys)
    {
        if (!string.IsNullOrEmpty(keys.GEMINI_API_KEY)) return true;
        Debug.LogWarning("[AI] GEMINI_API_KEY is empty in StreamingAssets/secrets.json.");
        return false;
    }

    public static Part[] Parts(string responseJson)
    {
        try
        {
            var r = JsonUtility.FromJson<Response>(responseJson);
            if (r?.candidates == null || r.candidates.Length == 0) return null;
            return r.candidates[0].content?.parts;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Text(string responseJson)
    {
        var parts = Parts(responseJson);
        if (parts == null) return null;
        var sb = new StringBuilder();
        foreach (var p in parts)
            if (!string.IsNullOrEmpty(p.text)) sb.Append(p.text);
        return sb.Length > 0 ? sb.ToString() : null;
    }
}

public class GeminiCoach : ICoachProvider
{
    const string Schema =
        "{\"type\":\"OBJECT\",\"properties\":{" +
        "\"say\":{\"type\":\"STRING\"}," +
        "\"action\":{\"type\":\"STRING\",\"enum\":[\"loop_bar\",\"slow_down\",\"next_song\",\"none\"]}," +
        "\"bar\":{\"type\":\"INTEGER\"}}," +
        "\"required\":[\"say\",\"action\",\"bar\"]}";

    readonly AIConfig cfg;
    readonly AISecrets keys;

    public string Name => "Gemini";

    public GeminiCoach(AIConfig cfg, AISecrets keys) { this.cfg = cfg; this.keys = keys; }

    public async Task<string> GetCoachJson(string metricsJson)
    {
        if (!Gemini.HasKey(keys)) return null;

        string body =
            "{\"systemInstruction\":{\"parts\":[{\"text\":" + AIHttp.Quote(cfg.coachSystemPrompt) + "}]}," +
            "\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":" + AIHttp.Quote(metricsJson) + "}]}]," +
            "\"generationConfig\":{\"temperature\":0.7,\"responseMimeType\":\"application/json\",\"responseSchema\":" + Schema + "}}";

        var r = await AIHttp.PostJson("Coach Gemini (" + cfg.geminiCoachModel + ")", Gemini.Url(cfg.geminiCoachModel), body,
            cfg.coachRequestTimeout, ("x-goog-api-key", keys.GEMINI_API_KEY));
        return r.ok ? Gemini.Text(r.text) : null;
    }
}

public class GeminiStt : ISttProvider
{
    readonly AIConfig cfg;
    readonly AISecrets keys;

    public string Name => "Gemini";

    public GeminiStt(AIConfig cfg, AISecrets keys) { this.cfg = cfg; this.keys = keys; }

    public async Task<string> Transcribe(byte[] wav)
    {
        if (!Gemini.HasKey(keys)) return null;

        string body =
            "{\"contents\":[{\"role\":\"user\",\"parts\":[" +
            "{\"text\":\"Transcribe this audio. Reply with the transcript only, no quotes or commentary. If there is no speech, reply with an empty string.\"}," +
            "{\"inlineData\":{\"mimeType\":\"audio/wav\",\"data\":\"" + Convert.ToBase64String(wav) + "\"}}]}]," +
            "\"generationConfig\":{\"temperature\":0}}";

        var r = await AIHttp.PostJson("STT Gemini (" + cfg.geminiSttModel + ")", Gemini.Url(cfg.geminiSttModel), body,
            cfg.sttRequestTimeout, ("x-goog-api-key", keys.GEMINI_API_KEY));
        if (!r.ok) return null;
        return (Gemini.Text(r.text) ?? "").Trim();
    }
}

// Gemini TTS returns base64 raw PCM (16-bit mono, 24 kHz) inside JSON, so the clip is built directly
// instead of going through UnityWebRequestMultimedia.
public class GeminiTts : ITtsProvider
{
    readonly AIConfig cfg;
    readonly AISecrets keys;

    public string Name => "Gemini TTS";

    // Set when the model/key clearly can't do TTS (bad model name, no access), so we stop paying the latency.
    public bool Unavailable { get; private set; }

    public GeminiTts(AIConfig cfg, AISecrets keys) { this.cfg = cfg; this.keys = keys; }

    public async Task<bool> Speak(string text, AudioSource source, Func<bool> cancelled)
    {
        if (Unavailable || !Gemini.HasKey(keys)) return false;

        string body =
            "{\"contents\":[{\"parts\":[{\"text\":" + AIHttp.Quote(text) + "}]}]," +
            "\"generationConfig\":{\"responseModalities\":[\"AUDIO\"]," +
            "\"speechConfig\":{\"voiceConfig\":{\"prebuiltVoiceConfig\":{\"voiceName\":" + AIHttp.Quote(cfg.geminiTtsVoice) + "}}}}}";

        var r = await AIHttp.PostJson("TTS Gemini (" + cfg.geminiTtsModel + ")", Gemini.Url(cfg.geminiTtsModel), body,
            cfg.ttsRequestTimeout, ("x-goog-api-key", keys.GEMINI_API_KEY));

        if (!r.ok)
        {
            if (r.status == 400 || r.status == 403 || r.status == 404)
            {
                Unavailable = true;
                Debug.LogWarning("[AI] Gemini TTS is not usable with this model/key. Using native TTS for the rest of this session.");
            }
            return false;
        }

        AudioClip clip = ToClip(Gemini.Parts(r.text));
        if (clip == null)
        {
            Debug.LogWarning("[AI] Gemini TTS returned no audio.");
            return false;
        }

        if (cancelled()) return true; // a newer line replaced this one while we were waiting

        source.clip = clip;
        source.Play();
        while (!cancelled() && source.isPlaying) await Awaitable.NextFrameAsync();
        return true;
    }

    public void Stop() { }

    static AudioClip ToClip(Gemini.Part[] parts)
    {
        if (parts == null) return null;
        foreach (var p in parts)
        {
            if (p.inlineData == null || string.IsNullOrEmpty(p.inlineData.data)) continue;

            byte[] pcm = Convert.FromBase64String(p.inlineData.data);
            int rate = 24000;
            string mime = p.inlineData.mimeType ?? "";
            int i = mime.IndexOf("rate=", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                string digits = mime.Substring(i + 5);
                int end = 0;
                while (end < digits.Length && char.IsDigit(digits[end])) end++;
                if (end > 0) int.TryParse(digits.Substring(0, end), out rate);
            }

            // Older TTS models return raw PCM, newer ones a 16-bit WAV file. For WAV, read the header and use only the data chunk.
            int offset = 0, length = pcm.Length, channels = 1;
            if (pcm.Length > 44 && pcm[0] == 'R' && pcm[1] == 'I' && pcm[2] == 'F' && pcm[3] == 'F')
            {
                length = 0;
                int pos = 12;
                while (pos + 8 <= pcm.Length)
                {
                    string id = Encoding.ASCII.GetString(pcm, pos, 4);
                    int size = BitConverter.ToInt32(pcm, pos + 4);
                    if (id == "fmt " && pos + 16 <= pcm.Length)
                    {
                        channels = Mathf.Max(1, BitConverter.ToInt16(pcm, pos + 10));
                        rate = BitConverter.ToInt32(pcm, pos + 12);
                    }
                    else if (id == "data")
                    {
                        offset = pos + 8;
                        length = Mathf.Clamp(size, 0, pcm.Length - offset);
                        break;
                    }
                    pos += 8 + Mathf.Max(0, size) + (size & 1);
                }
            }

            var samples = new float[length / 2];
            for (int s = 0; s < samples.Length; s++)
                samples[s] = (short)(pcm[offset + s * 2] | (pcm[offset + s * 2 + 1] << 8)) / 32768f;
            if (samples.Length == 0) return null;

            var clip = AudioClip.Create("GeminiTts", samples.Length / channels, channels, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
        return null;
    }
}
