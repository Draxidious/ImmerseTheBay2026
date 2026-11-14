using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// Menu: Tools ▸ Coach ▸ Add Coach Orb             — adds a CoachOrb to the open scene (or selects the existing one).
///       Tools ▸ Coach ▸ Add Aura XR Rig to Scene  — the project's hands rig + passthrough setup (same as Dev_Ari),
///                                                    so the open scene can run on the Aura.
/// Neither saves the scene; save it yourself.
/// </summary>
public static class CoachOrbMenu
{
    const string MaterialDir = "Assets/Scripts/Coach/Materials";
    const string RigPrefab = "Assets/VRTemplateAssets/Prefabs/Setup/Complete XR Origin Set Up Hands Variant.prefab";
    const string HandPermissionsPrefab = "Assets/VRTemplateAssets/Prefabs/Setup/Hands Permissions Manager.prefab";

    [MenuItem("Tools/Coach/Add Coach Orb")]
    public static void AddCoachOrb()
    {
        var existing = Object.FindAnyObjectByType<CoachOrb>();
        if (existing)
        {
            Selection.activeGameObject = existing.gameObject;
            Debug.Log("[CoachOrbMenu] The scene already has a CoachOrb; selected it.");
            return;
        }

        var go = new GameObject("CoachOrb");
        Undo.RegisterCreatedObjectUndo(go, "Add Coach Orb");
        var orb = go.AddComponent<CoachOrb>();
        orb.orbMaterial = GetOrCreateUnlit("CoachOrbUnlit", orb.idleColor);
        orb.piano = Object.FindAnyObjectByType<PianoOverlay>();
        orb.calibrator = Object.FindAnyObjectByType<PianoCalibrator>();
        orb.EnsureParts();

        // Editor preview spot only: at runtime the orb places itself once the head is tracked.
        var cam = Camera.main ? Camera.main.transform : null;
        if (cam)
        {
            Vector3 fwd = cam.forward; fwd.y = 0f; fwd = fwd.sqrMagnitude < 1e-6f ? Vector3.forward : fwd.normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            go.transform.position = cam.position + fwd * orb.fallbackForward + right * orb.fallbackRight;
            go.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        }

        Selection.activeGameObject = go;
        EditorSceneManager.MarkSceneDirty(go.scene);
        Debug.Log("[CoachOrbMenu] Added CoachOrb. Play-mode keys: T = sample text, Y = cycle state, H = hide, V = fake voice.");
    }

    [MenuItem("Tools/Coach/Add Aura XR Rig to Scene")]
    public static void AddAuraRig()
    {
        if (Object.FindAnyObjectByType<XROrigin>())
        {
            Debug.Log("[CoachOrbMenu] The scene already has an XR Origin; nothing added.");
            return;
        }

        // A plain non-XR camera would fight the rig's camera (both tagged MainCamera).
        foreach (var c in Object.FindObjectsByType<Camera>())
            if (!c.GetComponentInParent<XROrigin>()) Undo.DestroyObjectImmediate(c.gameObject);

        if (!Object.FindAnyObjectByType<XRInteractionManager>())
            Undo.RegisterCreatedObjectUndo(new GameObject("XR Interaction Manager", typeof(XRInteractionManager)), "Add XR rig");
        var rig = InstantiatePrefab(RigPrefab);
        InstantiatePrefab(HandPermissionsPrefab);

        // Same passthrough setup as Dev_Ari (see CLAUDE.md "Building for the Aura"): transparent-black clear,
        // ARCameraManager on the camera + an ARSession, otherwise Android XR runs OPAQUE (room blacked out).
        var origin = rig ? rig.GetComponentInChildren<XROrigin>() : null;
        var cam = origin && origin.Camera ? origin.Camera : null;
        if (cam)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            if (!cam.GetComponent<ARCameraManager>()) cam.gameObject.AddComponent<ARCameraManager>();
        }
        if (!Object.FindAnyObjectByType<ARSession>())
            Undo.RegisterCreatedObjectUndo(new GameObject("AR Session", typeof(ARSession)), "Add XR rig");

        // Seated AR: the template rig's locomotion (gravity etc.) must be off or the rig falls with no floor.
        if (origin)
        {
            var loco = origin.transform.Find("Locomotion");
            if (loco) loco.gameObject.SetActive(false);
            var cc = origin.GetComponent<CharacterController>();
            if (cc) cc.enabled = false;
        }

        var orb = Object.FindAnyObjectByType<CoachOrb>();
        if (orb && cam) orb.head = cam.transform;

        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        Debug.Log("[CoachOrbMenu] Added the Aura XR rig (hands, passthrough, locomotion off).");
    }

    static GameObject InstantiatePrefab(string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (!prefab) { Debug.LogWarning($"[CoachOrbMenu] Missing prefab: {path}"); return null; }
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(go, "Add XR rig");
        return go;
    }

    static Material GetOrCreateUnlit(string name, Color color)
    {
        string path = $"{MaterialDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat) return mat;
        Directory.CreateDirectory(MaterialDir);
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (!shader) { Debug.LogError("[CoachOrbMenu] URP Unlit shader not found."); shader = Shader.Find("Unlit/Color"); }
        mat = new Material(shader) { name = name };
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        AssetDatabase.CreateAsset(mat, path);
        AssetDatabase.SaveAssets();
        return mat;
    }
}
