using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

// Anthropic Claude REST (Messages API) for the coach.

static class Claude
{
    public const string MessagesUrl = "https://api.anthropic.com/v1/messages";
    public const string ApiVersion = "2026-10-21";

    [Serializable] public class Response { public Block[] content; public string stop_reason; }
    [Serializable] public class Block { public string type; public string text; }

    public static bool HasKey(AISecrets keys)
    {
        if (!string.IsNullOrEmpty(keys.ANTHROPIC_API_KEY)) return true;
        Debug.LogWarning("[AI] ANTHROPIC_API_KEY is empty in StreamingAssets/secrets.json.");
        return false;
    }

    // The reply can start with thinking blocks, so pick the text blocks by type.
    public static string Text(string responseJson)
    {
        try
        {
            var r = JsonUtility.FromJson<Response>(responseJson);
            if (r?.content == null) return null;
            if (r.stop_reason == "refusal" || r.stop_reason == "max_tokens")
            {
                Debug.LogWarning("[AI] Coach Claude stopped early: " + r.stop_reason);
                return null;
            }
            var sb = new StringBuilder();
            foreach (var b in r.content)
                if (b.type == "text" && !string.IsNullOrEmpty(b.text)) sb.Append(b.text);
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public class ClaudeCoach : ICoachProvider
{
    const string Schema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"say\":{\"type\":\"string\"}," +
        "\"action\":{\"type\":\"string\",\"enum\":[\"loop_bar\",\"slow_down\",\"next_song\",\"none\"]}," +
        "\"bar\":{\"type\":\"integer\"}}," +
        "\"required\":[\"say\",\"action\",\"bar\"],\"additionalProperties\":false}";

    readonly AIConfig cfg;
    readonly AISecrets keys;

    public string Name => "Claude";

    public ClaudeCoach(AIConfig cfg, AISecrets keys) { this.cfg = cfg; this.keys = keys; }

    public async Task<string> GetCoachJson(string systemPrompt, string userJson)
    {
        if (!Claude.HasKey(keys)) return null;

        // No temperature: this API version rejects sampling parameters. Low effort keeps the reply fast,
        // and max_tokens leaves room for the little thinking the model may still do.
        string body =
            "{\"model\":" + AIHttp.Quote(cfg.claudeCoachModel.Trim()) + "," +
            "\"max_tokens\":1024," +
            "\"system\":" + AIHttp.Quote(systemPrompt) + "," +
            "\"output_config\":{\"effort\":\"low\",\"format\":{\"type\":\"json_schema\",\"schema\":" + Schema + "}}," +
            "\"messages\":[{\"role\":\"user\",\"content\":" + AIHttp.Quote(userJson) + "}]}";

        var r = await AIHttp.PostJson("Coach Claude (" + cfg.claudeCoachModel + ")", Claude.MessagesUrl, body,
            cfg.coachRequestTimeout, ("x-api-key", keys.ANTHROPIC_API_KEY), ("anthropic-version", Claude.ApiVersion));
        return r.ok ? Claude.Text(r.text) : null;
    }
}
