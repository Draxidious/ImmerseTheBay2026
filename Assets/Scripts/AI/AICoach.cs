using System;
using System.Threading.Tasks;
using UnityEngine;

// The teaching layer. Give it a CoachContext and it speaks one short line and raises OnCoachAction.
// Never throws and never hangs: on error or timeout it says the canned fallback line with action None.
public class AICoach : MonoBehaviour
{
    const string PromptResource = "CoachPrompt"; // Assets/Resources/CoachPrompt.txt

    [Tooltip("Optional. Defaults to Assets/Resources/AIConfig.asset.")]
    public AIConfig config;
    [Tooltip("Optional. Uses the VoiceOutput on this GameObject.")]
    public VoiceOutput voiceOutput;
    [Tooltip("Speak every reply through VoiceOutput.")]
    public bool speakReplies = true;

    // Raised for every reply, including None, before the line is spoken. The lesson engine and overlay listen to this.
    public event Action<CoachAction> OnCoachAction;
    // The full reply (line + action), also raised for fallbacks.
    public event Action<CoachResult> OnResponse;

    public CoachResult LastResult { get; private set; }
    public CoachContext LastContext { get; private set; }
    public bool IsThinking { get; private set; }

    AIConfig Cfg => config != null ? config : AIConfig.Get();

    void Awake()
    {
        if (voiceOutput == null) voiceOutput = GetComponent<VoiceOutput>();
    }

    // Main entry point: lesson start, a finished run, or a stuck student.
    public async Task<CoachResult> Coach(CoachContext ctx)
    {
        if (ctx == null) ctx = new CoachContext();

        // A stuck nudge is not worth interrupting a reply that is already on its way.
        if (ctx.trigger == CoachTrigger.Stuck && IsThinking)
        {
            Debug.Log("[AI] Coach is busy, skipping the stuck trigger.");
            return new CoachResult { say = "", trigger = ctx.trigger, isFallback = true };
        }

        if (string.IsNullOrEmpty(ctx.lastFeedback) && LastResult != null) ctx.lastFeedback = LastResult.say;
        LastContext = ctx;

        var cfg = Cfg;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IsThinking = true;

        CoachResult result = null;
        try
        {
            var work = AskProviders(SystemPrompt(cfg), UserMessage(ctx), ctx, cfg);
            var first = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(cfg.coachTotalTimeout)));
            if (first == work) result = work.Result;
            else Debug.LogWarning($"[AI] Coach timed out after {cfg.coachTotalTimeout:0.#} s.");
        }
        finally
        {
            IsThinking = false;
        }

        if (result == null)
        {
            // An unprompted free-play comment that failed says nothing; "let's try that again" makes no sense there.
            string line = ctx.trigger == CoachTrigger.UserAsked ? cfg.questionFallbackLine
                        : ctx.trigger == CoachTrigger.PhraseEnded ? "" : cfg.fallbackLine;
            result = new CoachResult { say = line, trigger = ctx.trigger, isFallback = true };
            Debug.LogWarning("[AI] Coach using canned fallback line.");
        }

        Debug.Log($"[AI] Coach ({CoachContext.TriggerName(ctx.trigger)}) total {sw.ElapsedMilliseconds} ms -> say=\"{result.say}\" action={result.action}");
        if (this == null) return result; // destroyed while waiting

        if (ctx.IsFreePlay && !result.isFallback) result.say = KeepWholeSentences(result.say, FreePlayMaxChars);
        LastResult = result;
        OnCoachAction?.Invoke(result.action);
        OnResponse?.Invoke(result);
        if (speakReplies && voiceOutput != null) _ = voiceOutput.Speak(result.say);
        return result;
    }

    // Speaks a fixed line now, with no model call (stuck hints, "let's stick with the plan"). Returns false (and says nothing)
    // when the coach is thinking or already speaking. LastResult is left alone so last_feedback still holds the model's line.
    public bool SayNow(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || voiceOutput == null || IsThinking || voiceOutput.IsSpeaking) return false;
        Debug.Log($"[AI] Coach (say_now) -> say=\"{line}\"");
        _ = voiceOutput.Speak(line);
        return true;
    }

    // Something the student said that was not a fixed voice command. Uses the last context unless one is passed in.
    public Task<CoachResult> Ask(string question, CoachContext ctx = null)
    {
        var c = (ctx ?? LastContext ?? new CoachContext()).Clone();
        c.trigger = CoachTrigger.UserAsked;
        c.userQuestion = question;
        c.lastFeedback = null;
        return Coach(c);
    }

    // Free play asks for under 110 characters, but the model often runs long and the 2nd sentence is where it starts inventing.
    const int FreePlayMaxChars = 110;

    // Whole sentences up to maxChars; always at least the first sentence.
    public static string KeepWholeSentences(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        var parts = System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?])\s+");
        string kept = parts[0];
        for (int i = 1; i < parts.Length && kept.Length + 1 + parts[i].Length <= maxChars; i++) kept += " " + parts[i];
        return kept;
    }

    // Read on every request so the prompt file can be edited while the app is running in the Editor.
    static string SystemPrompt(AIConfig cfg)
    {
        var asset = cfg.coachPrompt != null ? cfg.coachPrompt : Resources.Load<TextAsset>(PromptResource);
        if (asset != null && !string.IsNullOrWhiteSpace(asset.text)) return asset.text;

        Debug.LogError("[AI] Coach prompt not found. Expected Assets/Resources/" + PromptResource + ".txt");
        return "You are a friendly piano coach. Reply ONLY with a JSON object: " +
               "{\"say\": string, \"action\": \"none\", \"from_bar\": 0, \"to_bar\": 0, \"hand\": \"right\"}.";
    }

    static string UserMessage(CoachContext ctx)
    {
        string song = SongFormatter.Format(ctx.song);
        return (song.Length > 0 ? "SONG\n" + song + "\n\n" : "") + "CONTEXT\n" + ctx.ToJson();
    }

    // Selected provider first, then the other one. Returns null if both fail.
    static async Task<CoachResult> AskProviders(string systemPrompt, string userMessage, CoachContext ctx, AIConfig cfg)
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
                string raw = await provider.GetCoachJson(systemPrompt, userMessage);
                var parsed = CoachResult.Parse(raw, ctx);
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
