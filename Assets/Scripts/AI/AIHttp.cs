using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

// Thin UnityWebRequest helper: await, timeout, latency log, and loud errors for bad model names.
public static class AIHttp
{
    public class Result
    {
        public bool ok;
        public long status;
        public string text;
        public long ms;
    }

    public static Task Await(UnityWebRequestAsyncOperation op)
    {
        var tcs = new TaskCompletionSource<bool>();
        if (op.isDone) tcs.TrySetResult(true);
        else op.completed += _ => tcs.TrySetResult(true);
        return tcs.Task;
    }

    public static Task<Result> PostJson(string label, string url, string json, int timeoutSeconds, params (string key, string value)[] headers)
    {
        var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        foreach (var h in headers) req.SetRequestHeader(h.key, h.value);
        return Send(label, req, timeoutSeconds);
    }

    // Sends and disposes the request. Never throws; check Result.ok.
    public static async Task<Result> Send(string label, UnityWebRequest req, int timeoutSeconds)
    {
        using (req)
        {
            req.timeout = Mathf.Max(1, timeoutSeconds);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Await(req.SendWebRequest());
            sw.Stop();

            var result = new Result
            {
                ok = req.result == UnityWebRequest.Result.Success,
                status = req.responseCode,
                text = req.downloadHandler != null ? req.downloadHandler.text : "",
                ms = sw.ElapsedMilliseconds
            };

            if (result.ok)
            {
                Debug.Log($"[AI] {label}: OK in {result.ms} ms");
                return result;
            }

            string body = result.text ?? "";
            if (body.Length > 400) body = body.Substring(0, 400) + "...";

            if (result.status == 404)
                Debug.LogError($"[AI] {label}: 404 NOT FOUND after {result.ms} ms. The model name is probably wrong or not available on this key. " +
                               $"Change it in Assets/Resources/AIConfig.asset (no code change needed). Response: {body}");
            else if (result.status == 429)
                Debug.LogWarning($"[AI] {label}: 429 rate limited / quota exceeded after {result.ms} ms. Response: {body}");
            else if (result.status == 401 || result.status == 403)
                Debug.LogError($"[AI] {label}: {result.status} auth error after {result.ms} ms. Check the key in StreamingAssets/secrets.json. Response: {body}");
            else
                Debug.LogWarning($"[AI] {label}: failed after {result.ms} ms ({req.error}, HTTP {result.status}). Response: {body}");

            return result;
        }
    }

    // JSON string literal, with quotes.
    public static string Quote(string s)
    {
        var sb = new StringBuilder((s?.Length ?? 0) + 2);
        sb.Append('"');
        if (s != null)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
