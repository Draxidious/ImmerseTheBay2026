using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

// API keys, loaded from the gitignored Assets/StreamingAssets/secrets.json.
// Copy secrets.example.json to secrets.json and fill in the keys. Never commit secrets.json.
[Serializable]
public class AISecrets
{
    public string GEMINI_API_KEY = "";
    public string GROQ_API_KEY = "";
    public string ANTHROPIC_API_KEY = "";

    static Task<AISecrets> loading;

    public static Task<AISecrets> Load()
    {
        if (loading == null) loading = LoadInternal();
        return loading;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetCache() => loading = null;

    static async Task<AISecrets> LoadInternal()
    {
        // On Android StreamingAssets sits inside the APK, so it has to be read with UnityWebRequest.
        string path = Path.Combine(Application.streamingAssetsPath, "secrets.json");
        string url = path.Contains("://") ? path : "file://" + path;

        using (var req = UnityWebRequest.Get(url))
        {
            await AIHttp.Await(req.SendWebRequest());
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[AI] Could not read " + path + ". Copy secrets.example.json to secrets.json and add your keys.");
                return new AISecrets();
            }

            try
            {
                var secrets = JsonUtility.FromJson<AISecrets>(req.downloadHandler.text) ?? new AISecrets();
                secrets.GEMINI_API_KEY = (secrets.GEMINI_API_KEY ?? "").Trim();
                secrets.GROQ_API_KEY = (secrets.GROQ_API_KEY ?? "").Trim();
                secrets.ANTHROPIC_API_KEY = (secrets.ANTHROPIC_API_KEY ?? "").Trim();
                return secrets;
            }
            catch (Exception e)
            {
                Debug.LogError("[AI] secrets.json is not valid JSON: " + e.Message);
                return new AISecrets();
            }
        }
    }
}
