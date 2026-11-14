#if UNITY_ANDROID
using System;
using System.IO;
using UnityEditor.Android;
using UnityEngine;

// Android 11+ hides TTS engines from apps unless the manifest declares this <queries> entry.
// Needed by NativeTts (Android TextToSpeech). Patches the generated Gradle project only, nothing in the repo.
public class AITtsManifestPatch : IPostGenerateGradleAndroidProject
{
    const string Queries =
        "  <queries>\n    <intent>\n      <action android:name=\"android.intent.action.TTS_SERVICE\" />\n    </intent>\n  </queries>\n";

    public int callbackOrder => 100;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        try
        {
            string manifest = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifest)) return;

            string xml = File.ReadAllText(manifest);
            if (xml.Contains("android.intent.action.TTS_SERVICE")) return;

            int end = xml.LastIndexOf("</manifest>", StringComparison.Ordinal);
            if (end < 0) return;

            File.WriteAllText(manifest, xml.Insert(end, Queries));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[AI] Could not add the TTS <queries> entry to the Android manifest: " + e.Message);
        }
    }
}
#endif
