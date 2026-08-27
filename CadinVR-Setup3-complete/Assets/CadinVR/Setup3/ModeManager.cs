using UnityEngine;

public class ModeManager : MonoBehaviour
{
    [Header("Setup 3 Source")]
    public GameObject sourceBodyPrefab;
    public bool followVoxelBodySource = true;
    public string sourceMeshPath = "";
    public Vector3Int bodySize = new Vector3Int(100, 100, 100);

    [Header("Setup 3")]
    public Setup3DatumPreviewManager setup3PreviewManager;
    public Setup3DatumPlaneController setup3Controller;
    public Setup3SketchTool setup3SketchTool;
    public Setup3DatumSetupHUD setup3Hud;

    void Awake()
    {
        ResolveExistingSetup3Components();
    }

    void Start()
    {
        EnsureSetup3Components();
        ActivateSetup3();
        Debug.Log("[ModeManager] Setup 3 (Sketch/Surface) activated");
    }

    void ActivateSetup3()
    {
        if (setup3PreviewManager != null)
        {
            setup3PreviewManager.enabled = true;
            setup3PreviewManager.ActivateSetup();
        }

        if (setup3Controller != null) setup3Controller.enabled = true;
        if (setup3SketchTool != null) setup3SketchTool.enabled = true;
        if (setup3Hud != null) setup3Hud.enabled = true;
    }

    void ResolveExistingSetup3Components()
    {
        if (setup3PreviewManager == null)
            setup3PreviewManager = GetComponent<Setup3DatumPreviewManager>();
        if (setup3Controller == null)
            setup3Controller = GetComponent<Setup3DatumPlaneController>();
        if (setup3SketchTool == null)
            setup3SketchTool = GetComponent<Setup3SketchTool>();
        if (setup3Hud == null)
            setup3Hud = GetComponent<Setup3DatumSetupHUD>();
    }

    void EnsureSetup3Components()
    {
        ResolveExistingSetup3Components();

        if (setup3PreviewManager == null)
            setup3PreviewManager = GetComponent<Setup3DatumPreviewManager>() ?? gameObject.AddComponent<Setup3DatumPreviewManager>();
        if (setup3Controller == null)
            setup3Controller = GetComponent<Setup3DatumPlaneController>() ?? gameObject.AddComponent<Setup3DatumPlaneController>();
        if (setup3SketchTool == null)
            setup3SketchTool = GetComponent<Setup3SketchTool>() ?? gameObject.AddComponent<Setup3SketchTool>();
        if (setup3Hud == null)
            setup3Hud = GetComponent<Setup3DatumSetupHUD>() ?? gameObject.AddComponent<Setup3DatumSetupHUD>();

        setup3Controller.sketchTool = setup3SketchTool;
        setup3SketchTool.previewManager = setup3PreviewManager;
        setup3SketchTool.planeController = setup3Controller;
        setup3Hud.previewManager = setup3PreviewManager;
        setup3Hud.sketchTool = setup3SketchTool;

        if (setup3PreviewManager.sourceBodyPrefab == null)
            setup3PreviewManager.sourceBodyPrefab = sourceBodyPrefab;
        if (string.IsNullOrWhiteSpace(setup3PreviewManager.sourceMeshPath))
            setup3PreviewManager.sourceMeshPath = sourceMeshPath;

        setup3PreviewManager.followVoxelBodySource = followVoxelBodySource;
        setup3PreviewManager.bodySize = bodySize;
    }
}
