using System;
using System.Threading.Tasks;
using UnityEngine;

// Turns PerformanceMetrics into one short spoken coaching line plus an optional action.
// Never throws and never hangs: on error or timeout it returns the canned fallback line with action "none".
public class AICoach : MonoBehaviour
{
    [Tooltip("Optional. Defaults to Assets/Resources/AIConfig.asset.")]
    public AIConfig config;

    // Fired on every result, including fallbacks.
    public event Action<CoachResponse> OnResponse;
    public CoachResponse LastResponse { get; private set; }
    // The metrics from the most recent GetCoachResponse call, used as context for Ask().
    public PerformanceMetrics LastMetrics { get; private set; }

    AIConfig Cfg => config != null ? config : AIConfig.Get();

    // The line to speak. The matching action is in LastResponse / OnResponse.
    public async Task<string> GetFeedback(PerformanceMetrics m)
    {
        return (await GetCoachResponse(m)).say;
    }

    // The line plus the action: {say, action, bar}.
    public Task<CoachResponse> GetCoachResponse(PerformanceMetrics m)
    {
        LastMetrics = m;
        var cfg = Cfg;
        return Respond(cfg.coachSystemPrompt, JsonUtility.ToJson(m ?? new PerformanceMetrics()), cfg.fallbackLine, cfg);
    }

    // Free-form question or remark from the student (e.g. a transcript that VoiceInput reported as Unknown).
    // Music and practice questions get an answer, anything else gets a short "back to the piano" line.
    // Uses the metrics from the last GetCoachResponse call unless some are passed in.
    public Task<CoachResponse> Ask(string question, PerformanceMetrics m = null)
    {
        if (m == null) m = LastMetrics;
        var cfg = Cfg;
        string userJson = "{\"question\":" + AIHttp.Quote(question ?? "") + ",\"metrics\":" + (m != null ? JsonUtility.ToJson(m) : "null") + "}";
        return Respond(cfg.questionSystemPrompt, userJson, cfg.questionFallbackLine, cfg);
    }

    async Task<CoachResponse> Respond(string systemPrompt, string userJson, string fallbackLine, AIConfig cfg)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        CoachResponse response = null;
        var work = AskProviders(systemPrompt, userJson, cfg);
        var first = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(cfg.coachTotalTimeout)));
        if (first == work) response = work.Result;
        else Debug.LogWarning($"[AI] Coach timed out after {cfg.coachTotalTimeout:0.#} s.");

        if (response == null)
        {
            response = new CoachResponse { say = fallbackLine, action = "none", bar = 0 };
            Debug.LogWarning("[AI] Coach using canned fallback line.");
        }

        Debug.Log($"[AI] Coach total {sw.ElapsedMilliseconds} ms -> say=\"{response.say}\" action={response.action} bar={response.bar}");
        LastResponse = response;
        OnResponse?.Invoke(response);
        return response;
    }

    // Selected provider first, then the others. Returns null if all fail.
    static async Task<CoachResponse> AskProviders(string systemPrompt, string userJson, AIConfig cfg)
    {
        try
        {
            var keys = await AISecrets.Load();

            var order = cfg.useFallbackProviders
                ? AIProviders.CoachOrder(cfg.coachProvider)
                : new[] { cfg.coachProvider };

            foreach (var type in order)
            {
                var provider = AIProviders.Coach(type, cfg, keys);
                string raw = await provider.GetCoachJson(systemPrompt, userJson);
                var parsed = CoachResponse.Parse(raw);
                if (parsed != null) return parsed;
                if (raw != null) Debug.LogWarning($"[AI] Coach {provider.Name} returned bad JSON: {raw}");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AI] Coach error: " + e.Message);
        }
        return null;
    }
}
