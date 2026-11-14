using System;
using System.Threading.Tasks;
using UnityEngine;

// Provider interfaces. Implementations: ClaudeProviders.cs, GroqProviders.cs, NativeTts.cs.
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
        return new ClaudeCoach(cfg, keys);
    }

    // The selected coach provider first, then the other one as the fallback.
    public static CoachProviderType[] CoachOrder(CoachProviderType selected)
    {
        var other = selected == CoachProviderType.Claude ? CoachProviderType.Groq : CoachProviderType.Claude;
        return new[] { selected, other };
    }
}
