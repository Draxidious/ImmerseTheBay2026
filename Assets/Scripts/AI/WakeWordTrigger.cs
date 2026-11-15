using System;
using System.IO;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

// "Hey teacher" wake word: wraps com.canady.wakeword.WakeWordBridge (Assets/Plugins/Android/WakeWordBridge.java).
// The plugin owns the mic while running: it spots the keyword, then records the question and sends back a WAV path.
// Started lazily the first time it is armed (the lesson, after the intro's Microphone.End()). Any plugin error disables it
// for the session; the backup trigger keeps working. UnitySendMessage targets this GameObject's name, so keep it unique.
// Logcat: adb logcat -s Unity WakeWord
public class WakeWordTrigger : MonoBehaviour, IVoiceTrigger, IQuestionRecorder
{
    [Tooltip("Optional. Defaults to Assets/Resources/AIConfig.asset.")]
    public AIConfig config;

    public string Name => "wake word";
    public bool IsActive => Available && !_failed;
    public bool IsRunning { get; private set; }   // the plugin was started and has not failed
    public bool IsRecording { get; private set; }

    public event Action<IVoiceTrigger> Triggered;
    public event Action<byte[]> QuestionRecorded;

    bool _armed, _paused, _failed;

    AIConfig Cfg => config != null ? config : AIConfig.Get();

#if UNITY_ANDROID && !UNITY_EDITOR
    const bool Available = true;
    AndroidJavaObject _bridge;
#else
    const bool Available = false;
#endif

    void Awake()
    {
#if !(UNITY_ANDROID && !UNITY_EDITOR)
        Debug.Log("[WakeWord] Plugin not available in the Editor; using the backup trigger only.");
#endif
    }

    public void SetArmed(bool armed)
    {
        _armed = armed;
        if (!IsActive) return;
        if (armed && !IsRunning) StartBridge();
        ApplyPause();
    }

    public void BeginQuestion()
    {
        if (!IsRunning) { QuestionRecorded?.Invoke(null); return; }
        if (_paused) { _paused = false; Call("resume"); }
        IsRecording = true;
        Call("beginQuestion");
    }

    public void Cancel()
    {
        if (!IsRunning) { IsRecording = false; return; }
        if (IsRecording) Call("cancelQuestion");
        IsRecording = false;
        ApplyPause();
    }

    // Raw mic audio (before VAD/effects) for the next `seconds`, as a 16 kHz WAV at `path`.
    public void SaveDebugClip(string path, float seconds)
    {
        if (!IsRunning) { Debug.LogWarning("[WakeWord] Debug clip skipped: plugin not running."); return; }
        Call("saveDebugClip", path, seconds);
        Debug.Log($"[WakeWord] Saving {seconds:0.#} s debug clip to {path}");
    }

    // Pausing is deferred while a question is being recorded: pause() would cancel the capture.
    void ApplyPause()
    {
        if (!IsRunning) return;
        bool pause = !_armed && !IsRecording;
        if (pause == _paused) return;
        _paused = pause;
        Call(pause ? "pause" : "resume");
    }

    void StartBridge()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            Disable("microphone permission not granted");
            return;
        }
        try
        {
            var cfg = Cfg;
            var activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer").GetStatic<AndroidJavaObject>("currentActivity");
            _bridge = new AndroidJavaObject("com.canady.wakeword.WakeWordBridge", activity, gameObject.name);
            _bridge.Call("setTuning", cfg.keywordsScore, cfg.keywordsThreshold);
            _bridge.Call("setVad", cfg.minSpeechRms, cfg.vadNoiseRatio, cfg.endSilenceSeconds, cfg.maxQuestionSeconds,
                         cfg.questionStartTimeoutSeconds, cfg.preRollSeconds);
            _bridge.Call("setAudio", cfg.voiceCommunicationSource, cfg.audioEffects);
            _bridge.Call("start");
            IsRunning = true;
            _paused = false;
            Debug.Log($"[WakeWord] Bridge started on \"{gameObject.name}\" (score {cfg.keywordsScore}, threshold {cfg.keywordsThreshold}).");
        }
        catch (Exception e)
        {
            Disable("could not start the plugin: " + e.Message);
        }
#endif
    }

    void Call(string method, params object[] args)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_bridge == null) return;
        try { _bridge.Call(method, args); }
        catch (Exception e) { Disable(method + "() failed: " + e.Message); }
#endif
    }

    // Permanent for this session. A recording in progress ends with "nothing heard" so the router resets.
    void Disable(string why)
    {
        if (_failed) return;
        _failed = true;
        bool wasRecording = IsRecording;
        IsRunning = false;
        IsRecording = false;
        Debug.LogError("[WakeWord] Disabled for this session: " + why + ". The backup trigger still works.");
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_bridge != null)
        {
            try { _bridge.Call("stop"); } catch (Exception) { }
            try { _bridge.Dispose(); } catch (Exception) { }
            _bridge = null;
        }
#endif
        if (wasRecording) QuestionRecorded?.Invoke(null);
    }

    // ---------- Callbacks from Java (UnityPlayer.UnitySendMessage, main thread) ----------

    public void OnWakeWord(string keyword)
    {
        if (!IsRunning) return;
        Debug.Log("[WakeWord] Detected " + keyword);
        IsRecording = true; // Java has already started capturing the question
        Triggered?.Invoke(this);
    }

    public void OnQuestionAudio(string path)
    {
        byte[] wav = null;
        if (!string.IsNullOrEmpty(path))
        {
            try { wav = File.ReadAllBytes(path); }
            catch (Exception e) { Debug.LogWarning("[WakeWord] Could not read question audio: " + e.Message); }
            try { File.Delete(path); } catch (Exception) { }
        }
        if (!IsRecording) return; // cancelled meanwhile
        IsRecording = false;
        Debug.Log(wav != null ? $"[WakeWord] Question audio {wav.Length / 1024} KB" : "[WakeWord] No speech after the trigger.");
        ApplyPause();
        QuestionRecorded?.Invoke(wav);
    }

    public void OnWakeWordStatus(string text) => Debug.Log("[WakeWord] " + text);

    public void OnWakeWordError(string text) => Disable("plugin error: " + text);

    // ---------- Lifecycle ----------

    void OnApplicationPause(bool paused)
    {
        if (!IsRunning) return;
        Call(paused ? "suspendMic" : "resumeMic");
    }

    void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_bridge == null) return;
        try { _bridge.Call("stop"); } catch (Exception) { }
        try { _bridge.Dispose(); } catch (Exception) { }
        _bridge = null;
#endif
        IsRunning = false;
    }
}
