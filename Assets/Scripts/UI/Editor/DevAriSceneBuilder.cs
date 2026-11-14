using System.IO;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Builds Assets/Scenes/Dev_Ari.unity: the project's existing hands XR rig + PianoOverlay (URP Unlit)
/// + grab handle + Recalibrate button + PianoCalibrator. Menu: Tools ▸ Piano ▸ Build Dev_Ari Scene.
/// Safe to re-run; it asks before overwriting.
/// </summary>
public static class DevAriSceneBuilder
{
    const string ScenePath = "Assets/Scenes/Dev_Ari.unity";
    const string MaterialDir = "Assets/Scripts/UI/Materials";
    const string RigPrefab = "Assets/VRTemplateAssets/Prefabs/Setup/Complete XR Origin Set Up Hands Variant.prefab";
    const string HandPermissionsPrefab = "Assets/VRTemplateAssets/Prefabs/Setup/Hands Permissions Manager.prefab";

    [MenuItem("Tools/Piano/Build Dev_Ari Scene")]
    public static void Build()
    {
        if (File.Exists(ScenePath) &&
            !EditorUtility.DisplayDialog("Dev_Ari scene", $"{ScenePath} already exists. Rebuild it from scratch?", "Rebuild", "Cancel"))
            return;
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ---- Materials (URP Unlit) ----
        var keyMat = GetOrCreateUnlit("PianoKeyUnlit", Color.black);
        var handleMat = GetOrCreateUnlit("PianoHandleUnlit", new Color(0.3f, 0.85f, 1f));
        var buttonMat = GetOrCreateUnlit("PianoButtonUnlit", new Color(1f, 0.6f, 0.15f));
        var markerMat = GetOrCreateUnlit("PianoMarkerUnlit", Color.white);

        // ---- XR rig (existing project rig) ----
        new GameObject("XR Interaction Manager", typeof(XRInteractionManager));
        var rig = InstantiatePrefab(RigPrefab);
        InstantiatePrefab(HandPermissionsPrefab);

        // Optical see-through: clear to transparent black (black = invisible on the Aura).
        // Android XR stays in OPAQUE blend mode (100% lens dimming) unless passthrough is on: that needs the
        // "Android XR: Session" + "Android XR: Camera (Passthrough)" OpenXR features, an ARSession, and an
        // enabled ARCameraManager on the camera (then the package switches to ALPHA_BLEND, 0% dimming).
        var origin = rig ? rig.GetComponentInChildren<XROrigin>() : null;
        var cam = origin && origin.Camera ? origin.Camera : null;
        if (cam)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            if (!cam.GetComponent<ARCameraManager>()) cam.gameObject.AddComponent<ARCameraManager>();
        }
        new GameObject("AR Session", typeof(ARSession));

        // Seated AR app: the VR template rig's locomotion (gravity, move, turn, teleport, climb, jump) must be off.
        // With GravityProvider on and no floor collider, the whole rig falls and world-placed UI drifts away.
        if (origin)
        {
            var loco = origin.transform.Find("Locomotion");
            if (loco) loco.gameObject.SetActive(false);
            var cc = origin.GetComponent<CharacterController>();
            if (cc) cc.enabled = false;
        }

        // ---- PianoOverlay root (grab interactable lives here, but only the handle collider is used) ----
        var overlayGo = new GameObject("PianoOverlay");
        overlayGo.transform.position = new Vector3(-0.61f, 0.75f, 0.45f); // rough default; calibration replaces it
        var overlay = overlayGo.AddComponent<PianoOverlay>();
        overlay.keyMaterial = keyMat;

        var rb = overlayGo.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        // Grab handle: separate child with its own collider, front-left of the lowest key.
        var handle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        handle.name = "GrabHandle";
        handle.transform.SetParent(overlayGo.transform, false);
        handle.transform.localPosition = new Vector3(-0.04f, 0.015f, -0.03f);
        handle.transform.localScale = Vector3.one * 0.03f;
        SetupRenderer(handle, handleMat);
        var handleCol = handle.GetComponent<SphereCollider>();

        var grab = overlayGo.AddComponent<XRGrabInteractable>();
        grab.colliders.Clear();
        grab.colliders.Add(handleCol); // keys have no colliders; only the handle is grabbable
        grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
        grab.trackPosition = true;
        grab.trackRotation = false; // nudge only; keep the overlay level and aligned
        grab.trackScale = false;
        grab.throwOnDetach = false;
        grab.useDynamicAttach = true; // no snap-to-hand jump when grabbing
        grab.matchAttachPosition = true;
        grab.matchAttachRotation = false;

        // Recalibrate button: child with its own collider, just behind the handle.
        var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
        button.name = "RecalibrateButton";
        button.transform.SetParent(overlayGo.transform, false);
        button.transform.localPosition = new Vector3(-0.04f, 0.01f, 0.03f);
        button.transform.localScale = new Vector3(0.035f, 0.01f, 0.035f);
        SetupRenderer(button, buttonMat);
        var buttonInteractable = button.AddComponent<XRSimpleInteractable>();
        buttonInteractable.colliders.Add(button.GetComponent<BoxCollider>());

        var buttonLabel = CreateLabel("Label", "Recalibrate", 0.12f);
        buttonLabel.transform.SetParent(overlayGo.transform, false);
        buttonLabel.transform.localPosition = new Vector3(-0.04f, 0.03f, 0.03f);
        buttonLabel.transform.localRotation = Quaternion.Euler(30f, 0f, 0f); // tilt toward the seated player
        buttonLabel.name = "RecalibrateLabel";

        // ---- Calibrator ----
        var calibGo = new GameObject("PianoCalibrator");
        var calib = calibGo.AddComponent<PianoCalibrator>();
        calib.overlay = overlay;
        calib.xrOrigin = origin;
        calib.overlayGrab = grab;
        calib.recalibrateButton = buttonInteractable;
        calib.markerMaterial = markerMat;
        // 0.135 keeps the longest 3-line instruction ≈35° wide at 0.6 m (fits the Aura's field of view).
        var prompt = CreateLabel("CalibrationPrompt", "Pinch the FRONT EDGE of the LOWEST key", 0.135f);
        prompt.transform.SetParent(calibGo.transform, false);
        prompt.transform.position = new Vector3(0f, 1.2f, 0.6f);
        calib.promptLabel = prompt;

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log($"[DevAriSceneBuilder] Built {ScenePath}. Play-mode keys: C = fake calibration, R = recalibrate, 1/2/3/0 = overlay debug.");
    }

    static GameObject InstantiatePrefab(string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (!prefab) { Debug.LogWarning($"[DevAriSceneBuilder] Missing prefab: {path}"); return null; }
        return (GameObject)PrefabUtility.InstantiatePrefab(prefab);
    }

    static Material GetOrCreateUnlit(string name, Color color)
    {
        string path = $"{MaterialDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat) return mat;
        Directory.CreateDirectory(MaterialDir);
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (!shader) { Debug.LogError("[DevAriSceneBuilder] URP Unlit shader not found."); shader = Shader.Find("Unlit/Color"); }
        mat = new Material(shader) { name = name };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    static void SetupRenderer(GameObject go, Material mat)
    {
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    static TextMeshPro CreateLabel(string name, string text, float fontSize)
    {
        var go = new GameObject(name);
        var tmp = go.AddComponent<TextMeshPro>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color = Color.white; // additive display: bright = visible
        tmp.rectTransform.sizeDelta = new Vector2(0.6f, 0.1f);
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        return tmp;
    }
}
