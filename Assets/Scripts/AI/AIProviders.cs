using System;
using System.Threading.Tasks;
using UnityEngine;

// Provider interfaces. Implementations: GeminiProviders.cs, GroqProviders.cs, NativeTts.cs.
// All of them return null / false on failure instead of throwing, so callers can fall back.

public interface ICoachProvider
{
    string Name { get; }
    // Returns the raw JSON text of the {say, action, bar} object, or null on failure.
    Task<string> GetCoachJson(string systemPrompt, string userJson);
}

public interface ISttProvider
{
    string Name { get; }
    // Returns the transcript, or null on failure.
    Task<string> Transcribe(byte[] wav);
}

public interface ITtsProvider
{
    string Name { get; }
    // Speaks the line and returns when it finished (or was cancelled). False = could not speak, try another provider.
    Task<bool> Speak(string text, AudioSource source, Func<bool> cancelled);
    void Stop();
}

public static class AIProviders
{
    public static ICoachProvider Coach(CoachProviderType type, AIConfig cfg, AISecrets keys)
    {
        if (type == CoachProviderType.Groq) return new GroqCoach(cfg, keys);
        if (type == CoachProviderType.Claude) return new ClaudeCoach(cfg, keys);
        return new GeminiCoach(cfg, keys);
    }

    // The selected coach provider first, then the others as fallbacks.
    public static CoachProviderType[] CoachOrder(CoachProviderType selected)
    {
        var order = new System.Collections.Generic.List<CoachProviderType> { selected };
        foreach (var type in new[] { CoachProviderType.Claude, CoachProviderType.Gemini, CoachProviderType.Groq })
            if (type != selected) order.Add(type);
        return order.ToArray();
    }

    public static ISttProvider Stt(SttProviderType type, AIConfig cfg, AISecrets keys)
    {
        if (type == SttProviderType.Gemini) return new GeminiStt(cfg, keys);
        return new GroqStt(cfg, keys);
    }

    public static SttProviderType Other(SttProviderType type)
    {
        return type == SttProviderType.Groq ? SttProviderType.Gemini : SttProviderType.Groq;
    }
}
