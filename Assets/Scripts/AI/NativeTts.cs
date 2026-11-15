using System;
using System.Threading.Tasks;
using UnityEngine;

// No-API TTS. On device: Android's TextToSpeech through AndroidJavaObject.
// In the Editor: logs the line and speaks it with the system voice (Windows System.Speech through PowerShell, macOS "say").
// Where there is no voice at all it waits about as long as the line would take, so IsSpeaking still means something.
public class NativeTts : ITtsProvider
{
    public string Name => "Native TTS";

#if UNITY_ANDROID && !UNITY_EDITOR
    class InitListener : AndroidJavaProxy
    {
        public volatile int status = int.MinValue; // TextToSpeech.SUCCESS == 0
        public InitListener() : base("android.speech.tts.TextToSpeech$OnInitListener") { }
        public void onInit(int s) { status = s; }
    }

    AndroidJavaObject tts;
    InitListener listener;

    async Task<bool> EnsureInit()
    {
        if (tts == null)
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            {
                var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                listener = new InitListener();
                tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, listener);
            }
        }

        float deadline = Time.realtimeSinceStartup + 3f;
        while (listener.status == int.MinValue && Time.realtimeSinceStartup < deadline)
            await Awaitable.NextFrameAsync();
        return listener.status == 0;
    }

    public async Task<bool> Speak(string text, AudioSource source, Func<bool> cancelled)
    {
        try
        {
            if (!await EnsureInit())
            {
                Debug.LogWarning("[AI] Android TextToSpeech failed to initialise (no TTS engine on this device?). Line: " + text);
                return false;
            }
            if (cancelled()) return true;

            const int QueueFlush = 0;
            using (var noParams = new AndroidJavaObject("java.util.HashMap"))
            {
                if (tts.Call<int>("speak", text, QueueFlush, noParams) != 0) return false;
            }

            // isSpeaking takes a moment to flip on.
            float startBy = Time.realtimeSinceStartup + 0.5f;
            while (!cancelled() && !tts.Call<bool>("isSpeaking") && Time.realtimeSinceStartup < startBy)
                await Awaitable.NextFrameAsync();
            while (!cancelled() && tts.Call<bool>("isSpeaking"))
                await Awaitable.NextFrameAsync();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AI] Android TextToSpeech error: " + e.Message);
            return false;
        }
    }

    public void Stop()
    {
        try { tts?.Call<int>("stop"); }
        catch (Exception) { }
    }
#else
    System.Diagnostics.Process sayProcess;

    public async Task<bool> Speak(string text, AudioSource source, Func<bool> cancelled)
    {
        Debug.Log("[AI] (native TTS) Coach says: " + text);
#if UNITY_EDITOR_OSX
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("/usr/bin/say", "\"" + text.Replace("\"", "'") + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            sayProcess = System.Diagnostics.Process.Start(info);
            var mine = sayProcess;
            while (mine != null && !mine.HasExited && !cancelled()) await Awaitable.NextFrameAsync();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AI] macOS say failed: " + e.Message);
        }
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        // Windows' built-in voice (System.Speech), so the Editor still talks when the online voice is unavailable.
        // The text goes in on stdin, so quotes and symbols in it need no escaping.
        bool started = false;
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Add-Type -AssemblyName System.Speech; " +
                "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Speak([Console]::In.ReadToEnd())\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true
            };
            sayProcess = System.Diagnostics.Process.Start(info);
            var mine = sayProcess;
            if (mine != null)
            {
                started = true;
                mine.StandardInput.Write(text);
                mine.StandardInput.Close();
                while (!mine.HasExited && !cancelled()) await Awaitable.NextFrameAsync();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AI] Windows speech failed: " + e.Message);
        }
        if (!started) await SilentPause(text, cancelled);
#else
        await SilentPause(text, cancelled);
#endif
        return true;
    }

    // No voice on this platform: take about as long as saying the line would, so whatever waits for the coach to
    // finish (the lesson, the words appearing beside the orb) keeps its pace instead of rushing on.
    static async Task SilentPause(string text, Func<bool> cancelled)
    {
        int words = text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        float until = Time.realtimeSinceStartup + Mathf.Max(1.5f, words * 0.35f);
        while (!cancelled() && Time.realtimeSinceStartup < until) await Awaitable.NextFrameAsync();
    }

    public void Stop()
    {
        try
        {
            if (sayProcess != null && !sayProcess.HasExited) sayProcess.Kill();
        }
        catch (Exception) { }
        sayProcess = null;
    }
#endif
}
