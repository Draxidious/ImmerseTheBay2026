using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

// Step A spike for the "hey teacher" wake word: asks for the mic, starts WakeWordBridge.java, logs detections.
// Lives only in Dev_Canady_WakeWord.unity (built by Tools > Wake Word > Build Spike APK). Not in Main.
// Logcat: adb logcat -s Unity WakeWord
public class WakeWordSpike : MonoBehaviour
{
    [Tooltip("Global boosting score; keywords.txt's per-keyword :2.0 overrides it.")]
    public float keywordsScore = 1.5f;
    [Tooltip("Global trigger threshold; keywords.txt's per-keyword #0.25 overrides it.")]
    public float keywordsThreshold = 0.25f;

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject _bridge;
#endif

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (Permission.HasUserAuthorizedPermission(Permission.Microphone)) StartBridge();
        else
        {
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => StartBridge();
            callbacks.PermissionDenied += _ => Debug.LogWarning("[WakeWord] Microphone permission denied.");
            Permission.RequestUserPermission(Permission.Microphone, callbacks);
        }
#else
        Debug.Log("[WakeWord] Plugin is Android only; nothing to start in the Editor.");
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    void StartBridge()
    {
        try
        {
            var activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer").GetStatic<AndroidJavaObject>("currentActivity");
            _bridge = new AndroidJavaObject("com.canady.wakeword.WakeWordBridge", activity, gameObject.name);
            _bridge.Call("setTuning", keywordsScore, keywordsThreshold);
            _bridge.Call("start");
            Debug.Log("[WakeWord] Bridge started.");
        }
        catch (System.Exception e)
        {
            Debug.LogError("[WakeWord] Could not start the plugin: " + e);
            _bridge = null;
        }
    }

    void OnDestroy()
    {
        if (_bridge == null) return;
        _bridge.Call("stop");
        _bridge.Dispose();
        _bridge = null;
    }
#endif

    public void OnWakeWord(string keyword) => Debug.Log("[WakeWord] DETECTED " + keyword);

    public void OnWakeWordStatus(string text) => Debug.Log("[WakeWord] " + text);
}
