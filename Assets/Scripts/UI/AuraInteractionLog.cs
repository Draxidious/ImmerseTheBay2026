using UnityEngine;

// Keep just the interaction breadcrumbs needed for on-device testing.
internal static class AuraInteractionLog
{
#if UNITY_ANDROID && !UNITY_EDITOR
    static bool _fileUnavailable;
    static string _path;
#endif

    internal static void Write(string message)
    {
        Debug.Log(message);
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_fileUnavailable) return;
        try
        {
            _path ??= System.IO.Path.Combine(Application.persistentDataPath, "interaction-test.log");
            var file = new System.IO.FileInfo(_path);
            if (file.Exists && file.Length >= 256 * 1024)
            {
                string previous = _path + ".previous";
                System.IO.File.Delete(previous);
                System.IO.File.Move(_path, previous);
            }
            System.IO.File.AppendAllText(_path, $"{System.DateTime.UtcNow:O} {message}\n");
        }
        catch (System.IO.IOException error)
        {
            _fileUnavailable = true;
            Debug.LogWarning($"[Interaction test] Could not persist log: {error.Message}");
        }
        catch (System.UnauthorizedAccessException error)
        {
            _fileUnavailable = true;
            Debug.LogWarning($"[Interaction test] Could not persist log: {error.Message}");
        }
#endif
    }
}
