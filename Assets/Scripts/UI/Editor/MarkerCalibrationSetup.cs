using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.ARSubsystems;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR;

/// <summary>
/// Adds marker calibration (two ArUco 4x4_50 markers on the P71's rear corners) to the open scene, idempotently, and
/// switches on the OpenXR features it needs. Used for Dev_Ari first, then Main (announce Main edits in chat first).
/// </summary>
public static class MarkerCalibrationSetup
{
    const string LibraryPath = "Assets/Scripts/UI/Markers/PianoCornerMarkers.asset";
    const float MarkerEdge = 0.033f; // black symbol width when printed at Actual Size

    [MenuItem("Tools/Piano/Add Marker Calibration To Open Scene")]
    public static void AddToOpenScene()
    {
        var origin = Object.FindAnyObjectByType<XROrigin>(FindObjectsInactive.Include);
        var calibrator = Object.FindAnyObjectByType<PianoCalibrator>(FindObjectsInactive.Include);
        if (!origin || !calibrator)
        {
            Debug.LogError($"[MarkerCalibrationSetup] Needs an XROrigin and a PianoCalibrator in the open scene (origin={(bool)origin}, calibrator={(bool)calibrator}).");
            return;
        }

        var library = GetOrCreateLibrary();

        // AR Foundation trackable managers live on the XR Origin. Disabled until MarkerCalibration has the permission.
        var images = origin.GetComponent<ARTrackedImageManager>();
        if (!images) images = Undo.AddComponent<ARTrackedImageManager>(origin.gameObject);
        images.referenceLibrary = library;
        images.trackedImagePrefab = null;
        images.enabled = false;
        EditorUtility.SetDirty(images);

        var marker = Object.FindAnyObjectByType<MarkerCalibration>(FindObjectsInactive.Include);
        if (!marker)
        {
            var go = new GameObject("MarkerCalibration");
            Undo.RegisterCreatedObjectUndo(go, "Add MarkerCalibration");
            marker = go.AddComponent<MarkerCalibration>();
        }
        marker.calibrator = calibrator;
        marker.imageManager = images;
        EditorUtility.SetDirty(marker);

        // The offsets panel is a world-space UGUI canvas; XRI's interactors reach it through an XRUIInputModule.
        if (!Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>(FindObjectsInactive.Include))
        {
            var es = new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule));
            Undo.RegisterCreatedObjectUndo(es, "Add EventSystem");
        }

        var scene = origin.gameObject.scene;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[MarkerCalibrationSetup] Marker calibration added to {scene.path}: ARTrackedImageManager on '{origin.name}' " +
                  $"(library {library.count} entries), MarkerCalibration, EventSystem. Also run Tools/Piano/Enable Marker Tracking (OpenXR Android).");
    }

    [MenuItem("Tools/Piano/Enable Marker Tracking (OpenXR Android)")]
    public static void EnableOpenXRFeatures()
    {
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (!settings) { Debug.LogError("[MarkerCalibrationSetup] No OpenXR settings for Android."); return; }
        // Marker tracking runs on Google's Android XR extensions session (alongside Unity's Android XR session, as in
        // the AuraMarkerTest prototype).
        foreach (var type in new[] { typeof(Google.XR.Extensions.XRSessionFeature), typeof(Google.XR.Extensions.XRMarkerTrackingFeature) })
        {
            var feature = settings.GetFeature(type);
            if (!feature) { Debug.LogError($"[MarkerCalibrationSetup] Feature {type.Name} not found."); continue; }
            if (!feature.enabled)
            {
                feature.enabled = true;
                EditorUtility.SetDirty(feature);
            }
            Debug.Log($"[MarkerCalibrationSetup] {type.Name} enabled={feature.enabled}");
        }
        AssetDatabase.SaveAssets();
    }

    static XRReferenceImageLibrary GetOrCreateLibrary()
    {
        var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);
        if (!library)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LibraryPath));
            library = ScriptableObject.CreateInstance<XRReferenceImageLibrary>();
            AssetDatabase.CreateAsset(library, LibraryPath);
        }
        // Google's Android XR provider reads marker references by name: "<dictionary>-<id>" plus the physical size.
        string[] names = { "ArUco4x4_50-0", "ArUco4x4_50-1" };
        bool ok = library.count == names.Length;
        for (int i = 0; ok && i < names.Length; i++)
            ok = library[i].name == names[i] && library[i].specifySize && Mathf.Approximately(library[i].width, MarkerEdge);
        if (!ok)
        {
            while (library.count > 0) library.RemoveAt(library.count - 1);
            foreach (var n in names)
            {
                library.Add();
                int i = library.count - 1;
                library.SetName(i, n);
                library.SetSpecifySize(i, true);
                library.SetSize(i, new Vector2(MarkerEdge, MarkerEdge));
            }
            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
        }
        return library;
    }
}
