using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

// Groq (OpenAI-compatible) for coach fallback, Whisper STT and TTS.

static class Groq
{
    public const string ChatUrl = "https://api.groq.com/openai/v1/chat/completions";
    public const string TranscribeUrl = "https://api.groq.com/openai/v1/audio/transcriptions";
    public const string SpeechUrl = "https://api.groq.com/openai/v1/audio/speech";

    [Serializable] public class ChatResponse { public Choice[] choices; }
    [Serializable] public class Choice { public Message message; }
    [Serializable] public class Message { public string content; }
    [Serializable] public class TranscriptResponse { public string text; }

    public static bool HasKey(AISecrets keys)
    {
        if (!string.IsNullOrEmpty(keys.GROQ_API_KEY)) return true;
        Debug.LogWarning("[AI] GROQ_API_KEY is empty in StreamingAssets/secrets.json.");
        return false;
    }
}

public class GroqCoach : ICoachProvider
{
    readonly AIConfig cfg;
    readonly AISecrets keys;

    public string Name => "Groq";

    public GroqCoach(AIConfig cfg, AISecrets keys) { this.cfg = cfg; this.keys = keys; }

    public async Task<string> GetCoachJson(string systemPrompt, string userJson)
    {
        if (!Groq.HasKey(keys)) return null;

        // JSON mode: the system prompt already describes the JSON object, which Groq requires.
        string body =
            "{\"model\":" + AIHttp.Quote(cfg.groqCoachModel.Trim()) + "," +
            "\"temperature\":0.7," +
            "\"response_format\":{\"type\":\"json_object\"}," +
            "\"messages\":[" +
            "{\"role\":\"system\",\"content\":" + AIHttp.Quote(systemPrompt) + "}," +
            "{\"role\":\"user\",\"content\":" + AIHttp.Quote(userJson) + "}]}";

        var r = await AIHttp.PostJson("Coach Groq (" + cfg.groqCoachModel + ")", Groq.ChatUrl, body,
            cfg.coachRequestTimeout, ("Authorization", "Bearer " + keys.GROQ_API_KEY));
        if (!r.ok) return null;

        try
        {
            var parsed = JsonUtility.FromJson<Groq.ChatResponse>(r.text);
            if (parsed?.choices == null || parsed.choices.Length == 0) return null;
            return parsed.choices[0].message?.content;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public class GroqStt : ISttProvider
{
    readonly AIConfig cfg;
    readonly AISecrets keys;

    public string Name => "Groq Whisper";

    public GroqStt(AIConfig cfg, AISecrets keys) { this.cfg = cfg; this.keys = keys; }

    public async Task<string> Transcribe(byte[] wav)
    {
        if (!Groq.HasKey(keys)) return null;

        var form = new WWWForm();
        form.AddField("model", cfg.groqSttModel.Trim());
        form.AddField("response_format", "json");
        form.AddField("language", "en");
        form.AddBinaryData("file", wav, "speech.wav", "audio/wav");

        var req = UnityWebRequest.Post(Groq.TranscribeUrl, form);
        req.SetRequestHeader("Authorization", "Bearer " + keys.GROQ_API_KEY);

        var r = await AIHttp.Send("STT Groq (" + cfg.groqSttModel + ")", req, cfg.sttRequestTimeout);
        if (!r.ok) return null;

        try
        {
            var parsed = JsonUtility.FromJson<Groq.TranscriptResponse>(r.text);
            return (parsed?.text ?? "").Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

// Groq TTS returns a WAV file. The model only takes short inputs, so a line is split at sentence breaks,
// the pieces are requested in parallel and joined into one clip.
public class GroqTts : ITtsProvider
{
    readonly AIConfig cfg;
    readonly AISecrets keys;

    public string Name => "Groq TTS";

    // Set when the model/key clearly can't do TTS (terms not accepted, bad model name, no access), so we stop paying the latency.
    public bool Unavailable { get; private set; }

    public GroqTts(AIConfig cfg, AISecrets keys) { this.cfg = cfg; this.keys = keys; }

    public async Task<bool> Speak(string text, AudioSource source, Func<bool> cancelled)
    {
        if (Unavailable || !Groq.HasKey(keys)) return false;

        var requests = new List<Task<AIHttp.Result>>();
        foreach (string piece in Split(text, Mathf.Max(20, cfg.groqTtsMaxChars)))
        {
            string body =
                "{\"model\":" + AIHttp.Quote(cfg.groqTtsModel.Trim()) + "," +
                "\"input\":" + AIHttp.Quote(piece) + "," +
                "\"voice\":" + AIHttp.Quote(cfg.groqTtsVoice.Trim()) + "," +
                "\"response_format\":\"wav\"}";
            requests.Add(AIHttp.PostJson("TTS Groq (" + cfg.groqTtsModel + ")", Groq.SpeechUrl, body,
                cfg.ttsRequestTimeout, ("Authorization", "Bearer " + keys.GROQ_API_KEY)));
        }

        var samples = new List<float>();
        int rate = 0, channels = 1;
        foreach (var r in await Task.WhenAll(requests))
        {
            if (!r.ok)
            {
                bool terms = (r.text ?? "").Contains("model_terms_required");
                if (terms || r.status == 401 || r.status == 403 || r.status == 404)
                {
                    Unavailable = true;
                    Debug.LogWarning("[AI] Groq TTS is not usable with this model/key" +
                                     (terms ? " (accept the model terms in the Groq console)" : "") +
                                     ". Using native TTS for the rest of this session.");
                }
                return false;
            }

            float[] part = WavUtility.Decode(r.data, out int partRate, out int partChannels);
            if (part == null || (rate != 0 && (partRate != rate || partChannels != channels)))
            {
                Debug.LogWarning("[AI] Groq TTS returned audio that could not be read.");
                return false;
            }
            rate = partRate;
            channels = partChannels;
            samples.AddRange(part);
        }
        if (samples.Count == 0) return false;

        if (cancelled()) return true; // a newer line replaced this one while we were waiting

        var clip = AudioClip.Create("GroqTts", samples.Count / channels, channels, rate, false);
        clip.SetData(samples.ToArray(), 0);
        source.clip = clip;
        source.Play();
        while (!cancelled() && source.isPlaying) await Awaitable.NextFrameAsync();
        return true;
    }

    public void Stop() { }

    // Pieces of at most maxChars, cut after sentence punctuation where possible, otherwise at a space.
    static List<string> Split(string text, int maxChars)
    {
        var pieces = new List<string>();
        string rest = text.Trim();
        while (rest.Length > maxChars)
        {
            int cut = -1;
            for (int i = maxChars - 1; i > 0; i--)
            {
                char c = rest[i];
                if ((c == '.' || c == '!' || c == '?' || c == ';') && (i + 1 == rest.Length || rest[i + 1] == ' ')) { cut = i + 1; break; }
            }
            if (cut < 0) cut = rest.LastIndexOf(',', maxChars - 1) + 1;
            if (cut <= 0) cut = rest.LastIndexOf(' ', maxChars - 1);
            if (cut <= 0) cut = maxChars;
            pieces.Add(rest.Substring(0, cut).Trim());
            rest = rest.Substring(cut).Trim();
        }
        if (rest.Length > 0) pieces.Add(rest);
        return pieces;
    }
}
