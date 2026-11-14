using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

// Groq (OpenAI-compatible) for coach fallback and Whisper STT.

static class Groq
{
    public const string ChatUrl = "https://api.groq.com/openai/v1/chat/completions";
    public const string TranscribeUrl = "https://api.groq.com/openai/v1/audio/transcriptions";

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

    public async Task<string> GetCoachJson(string metricsJson)
    {
        if (!Groq.HasKey(keys)) return null;

        // JSON mode: the system prompt already describes the JSON object, which Groq requires.
        string body =
            "{\"model\":" + AIHttp.Quote(cfg.groqCoachModel.Trim()) + "," +
            "\"temperature\":0.7," +
            "\"response_format\":{\"type\":\"json_object\"}," +
            "\"messages\":[" +
            "{\"role\":\"system\",\"content\":" + AIHttp.Quote(cfg.coachSystemPrompt) + "}," +
            "{\"role\":\"user\",\"content\":" + AIHttp.Quote(metricsJson) + "}]}";

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
