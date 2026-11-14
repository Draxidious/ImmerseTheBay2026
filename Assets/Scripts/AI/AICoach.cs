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

    AIConfig Cfg => config != null ? config : AIConfig.Get();

    // The line to speak. The matching action is in LastResponse / OnResponse.
    public async Task<string> GetFeedback(PerformanceMetrics m)
    {
        return (await GetCoachResponse(m)).say;
    }

    // The line plus the action: {say, action, bar}.
    public async Task<CoachResponse> GetCoachResponse(PerformanceMetrics m)
    {
        var cfg = Cfg;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        CoachResponse response = null;
        var work = AskProviders(m, cfg);
        var first = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(cfg.coachTotalTimeout)));
        if (first == work) response = work.Result;
        else Debug.LogWarning($"[AI] Coach timed out after {cfg.coachTotalTimeout:0.#} s.");

        if (response == null)
        {
            response = new CoachResponse { say = cfg.fallbackLine, action = "none", bar = 0 };
            Debug.LogWarning("[AI] Coach using canned fallback line.");
        }

        Debug.Log($"[AI] Coach total {sw.ElapsedMilliseconds} ms -> say=\"{response.say}\" action={response.action} bar={response.bar}");
        LastResponse = response;
        OnResponse?.Invoke(response);
        return response;
    }

    // Selected provider first, then the other one. Returns null if both fail.
    static async Task<CoachResponse> AskProviders(PerformanceMetrics m, AIConfig cfg)
    {
        try
        {
            var keys = await AISecrets.Load();
            string metricsJson = JsonUtility.ToJson(m ?? new PerformanceMetrics());

            var order = cfg.useFallbackProviders
                ? new[] { cfg.coachProvider, AIProviders.Other(cfg.coachProvider) }
                : new[] { cfg.coachProvider };

            foreach (var type in order)
            {
                var provider = AIProviders.Coach(type, cfg, keys);
                string raw = await provider.GetCoachJson(metricsJson);
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
