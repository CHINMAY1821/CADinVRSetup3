// Setup3DatumPlaneController.cs — VERSION 1
// Attach to: Main Camera
// Enabled/disabled by ModeManager. Disabled at startup (Setup 1 is default).
//
// Alt+left-click on surface  → Mode 1: derive datum plane from hit.normal.
// Alt+left-click empty space → Mode 2: OnGUI angle-input panel (reuses VoxelExtruder style).
//
// Local frame construction:
//   planeZ = hit.normal.normalized
//   planeX = Cross(Vector3.up, planeZ).normalized  (fallback: Cross(Vector3.forward, planeZ))
//   planeY = Cross(planeZ, planeX).normalized
//
// DOES NOT touch: SpawnObjAtMousePos, SimpleCameraController, or any other existing script.

using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

public class Setup3DatumPlaneController : MonoBehaviour
{
    enum PlaneFrameMode
    {
        Face,
        EdgeBisector,
        EdgeAngle
    }

    enum PlaneOrientationMode
    {
        Reference,
        Global,
        GlobalAngle
    }

    enum PlaneOriginMode
    {
        Pick,
        Global
    }

    struct EdgeKey
    {
        public int a;
        public int b;

        public EdgeKey(int i0, int i1)
        {
            if (i0 < i1) { a = i0; b = i1; }
            else { a = i1; b = i0; }
        }
    }

    struct VertexKey
    {
        public int x;
        public int y;
        public int z;

        public VertexKey(Vector3 v)
        {
            const float scale = 1000f;
            x = Mathf.RoundToInt(v.x * scale);
            y = Mathf.RoundToInt(v.y * scale);
            z = Mathf.RoundToInt(v.z * scale);
        }
    }

    class EdgeAccum
    {
        public int v0;
        public int v1;
        public Vector3 normalA;
        public Vector3 normalB;
        public int faceCount;
    }

    struct FeatureEdge
    {
        public int v0;
        public int v1;
        public bool isBoundary;
        public int faceCount;
        public Vector3 normalA;
        public Vector3 normalB;
    }

    class MeshFeatureCache
    {
        public Vector3[] vertices;
        public FeatureEdge[] featureEdges;
        public Dictionary<int, List<int>> featureEdgesByVertex;
        public HashSet<int> cornerVertices;
    }

    struct PlaneReferenceOverlay
    {
        public List<Setup3DatumPlaneVisual.ReferenceLine> lines;
        public List<Vector3> corners;
        public Vector3 rawPickPoint;
        public Vector3 snappedPoint;
        public bool hasPickPoint;
        public bool hasSnapPoint;
    }

    struct EdgeCandidate
    {
        public Vector3 snapPoint;
        public Vector3 direction;
        public float distance;
        public float projectedLength;
        public Vector3 normalA;
        public Vector3 normalB;
        public bool canHinge;
        public Setup3DatumPlaneVisual.ReferenceLine overlayLine;
    }

    struct CornerCandidate
    {
        public Vector3 snapPoint;
        public float distance;
        public List<Vector3> directions;
    }

    private const float SharpEdgeAngleDeg = 25f;
    private const float ReferenceSearchRadiusMm = 12f;
    private const float CornerMinAngleDeg = 35f;
    private const float SurfacePatchRadiusMm = 8f;
    private const float AxisAlignMinEdgeLengthMm = 5f;

    private static readonly Dictionary<Mesh, MeshFeatureCache> featureCacheByMesh = new Dictionary<Mesh, MeshFeatureCache>();

    [Header("References")]
    public Setup3SketchTool sketchTool;

    private Setup3DatumPlaneVisual activePlane;
    private Setup3DatumSurfacePatchGrid.SurfaceInteractionMode surfaceInteractionMode = Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.Auto;
    private PlaneFrameMode planeFrameMode = PlaneFrameMode.Face;
    private PlaneOrientationMode orientationMode = PlaneOrientationMode.Global;
    private PlaneOriginMode originMode = PlaneOriginMode.Pick;
    private string gridAngleInput = "0";
    private string planeAngleInput = "45";
    private Vector3 currentPlaneOrigin;
    private Vector3 currentPlaneNormal;
    private Vector3 currentReferenceX;
    private Vector3 currentReferenceY;
    private PlaneReferenceOverlay currentOverlay;
    private RaycastHit? cachedSurfaceHit = null;
    private bool hasCurrentPlaneState = false;
    private bool hasCurrentHinge = false;
    private Vector3 currentHingeAxis;
    private Vector3 currentHingePrimaryNormal;
    private Vector3 currentHingeSecondaryNormal;
    private float currentHingeSignedAngle = 0f;

    public bool IsAnglePanelOpen => showAnglePanel;

    // Mode 2 — angle input
    private bool showAnglePanel = false;
    private string angleInput = "0";

    private GUIStyle panelStyle, labelStyle, confirmStyle, cancelStyle, modeActiveStyle, modeInactiveStyle, modeDisabledStyle;
    private bool stylesReady = false;

    public static Rect CurrentOrientationPanelRect { get; private set; }

    void OnEnable()
    {
        // Clear angle panel state when re-enabled
        showAnglePanel = false;
        gridAngleInput = "0";
        planeAngleInput = "45";
    }

    void OnDisable()
    {
        showAnglePanel = false;
        hasCurrentPlaneState = false;
        CurrentOrientationPanelRect = default;
        if (sketchTool != null)
            sketchTool.SetActivePlane(null);
        if (activePlane != null) { Destroy(activePlane.gameObject); activePlane = null; }
    }

    void Update()
    {
        if (!Mouse.current.leftButton.wasPressedThisFrame) return;
        if (Keyboard.current == null || !Keyboard.current.leftAltKey.isPressed) return;
        if (showAnglePanel) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Ray ray = cam.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            Vector3 planeZ = ResolveVisibleSurfaceNormal(hit.normal, ray.direction);
            Vector3 planeX = Vector3.Cross(Vector3.up, planeZ).normalized;
            if (planeX.sqrMagnitude < 0.001f)
                planeX = Vector3.Cross(Vector3.forward, planeZ).normalized;
            Vector3 planeY = Vector3.Cross(planeZ, planeX).normalized;

            SpawnDatumPlane(hit.point, planeX, planeY, planeZ, hit);
            return;
        }

        showAnglePanel = true;
        angleInput = "0";
    }

    void SpawnDatumPlane(Vector3 origin, Vector3 localX, Vector3 localY, Vector3 localZ, RaycastHit? surfaceHit = null)
    {
        if (activePlane != null) Destroy(activePlane.gameObject);

        PlaneReferenceOverlay overlay = new PlaneReferenceOverlay
        {
            lines = new List<Setup3DatumPlaneVisual.ReferenceLine>(),
            corners = new List<Vector3>(),
            rawPickPoint = origin,
            snappedPoint = origin,
            hasPickPoint = false,
            hasSnapPoint = false
        };
        Vector3 adjustedOrigin = origin;
        Vector3 adjustedLocalX = localX;
        Vector3 adjustedLocalY = localY;
        EdgeCandidate hingeCandidate = default;
        bool hasHingeCandidate = false;
        if (surfaceHit.HasValue)
            TrySnapPlaneToProjectedReferences(surfaceHit.Value, ref adjustedOrigin, ref adjustedLocalX, ref adjustedLocalY, localZ, out overlay, out hingeCandidate, out hasHingeCandidate);

        currentPlaneOrigin = adjustedOrigin;
        currentPlaneNormal = localZ;
        currentReferenceX = adjustedLocalX;
        currentReferenceY = adjustedLocalY;
        currentOverlay = overlay;
        cachedSurfaceHit = surfaceHit;
        hasCurrentPlaneState = true;
        CacheHingeReference(localZ, hasHingeCandidate, hingeCandidate);
        if (!hasCurrentHinge && planeFrameMode != PlaneFrameMode.Face)
            planeFrameMode = PlaneFrameMode.Face;

        BuildCurrentPlaneFrame(out Vector3 planeZ, out Vector3 referenceX, out Vector3 referenceY);
        ApplyOrientationMode(planeZ, referenceX, referenceY, out adjustedLocalX, out adjustedLocalY, surfaceHit);

        if (originMode == PlaneOriginMode.Global)
        {
            adjustedOrigin = ProjectPointOntoPlane(Vector3.zero, adjustedOrigin, planeZ);
            currentPlaneOrigin = adjustedOrigin;
        }

        var go = new GameObject("DatumPlane");
        go.transform.position = adjustedOrigin;
        activePlane = go.AddComponent<Setup3DatumPlaneVisual>();
        activePlane.SetDisplayOffset(ResolveDefaultDisplayPlaneOffsetMm(ResolveSharedCellSize()));
        activePlane.SetGridSpacing(ResolveSharedCellSize());
        activePlane.SetFrame(adjustedOrigin, adjustedLocalX, adjustedLocalY, planeZ);
        Setup3DatumSurfacePatchGrid patchGrid = go.GetComponent<Setup3DatumSurfacePatchGrid>();
        if (patchGrid == null)
            patchGrid = go.AddComponent<Setup3DatumSurfacePatchGrid>();
        patchGrid.SetInteractionMode(surfaceInteractionMode);
        if (surfaceHit.HasValue)
            patchGrid.Initialize(activePlane, surfaceHit.Value, ResolveSharedCellSize(), SurfacePatchRadiusMm);
        ApplyOverlayForCurrentMode(planeZ);

        if (sketchTool != null) sketchTool.SetActivePlane(activePlane);

        Debug.Log($"[Setup3DatumPlaneController] Plane at {adjustedOrigin}, normal={planeZ:F3}");
    }

    void OnGUI()
    {
        if (!showAnglePanel)
        {
            CurrentOrientationPanelRect = default;
            if (activePlane != null)
                DrawOrientationPanel();
            return;
        }
        InitStyles();

        float pw = 300f, ph = 130f;
        float px = (Screen.width - pw) * 0.5f, py = (Screen.height - ph) * 0.5f;
        CurrentOrientationPanelRect = new Rect(px, py, pw, ph);

        GUI.Box(new Rect(px, py, pw, ph), "", panelStyle);
        GUI.Label(new Rect(px + 12, py + 12, pw - 24, 20),
            "DATUM PLANE — Angle from horizontal (°)", labelStyle);

        DrawAngleControls(px, py + 42f, pw, "Tilt °", ref angleInput, -180f, 180f);

        if (GUI.Button(new Rect(px + 12, py + 94, 132, 30), "✔  Confirm", confirmStyle))
            ConfirmAngle();

        if (GUI.Button(new Rect(px + 156, py + 94, 132, 30), "✕  Cancel", cancelStyle))
            showAnglePanel = false;

        // Keyboard confirm/cancel
        Event e = Event.current;
        if (e != null && e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { ConfirmAngle(); e.Use(); }
            if (e.keyCode == KeyCode.Escape) { showAnglePanel = false; e.Use(); }
        }
    }

    void DrawOrientationPanel()
    {
        InitStyles();

        bool showPlaneAngle = planeFrameMode == PlaneFrameMode.EdgeAngle && hasCurrentHinge;
        bool showGridAngle = orientationMode == PlaneOrientationMode.GlobalAngle;
        float ph = 196f + (showPlaneAngle ? 36f : 0f) + (showGridAngle ? 36f : 0f);
        float pw = 420f;
        float px = (Screen.width - pw) * 0.5f;
        float py = 18f;
        CurrentOrientationPanelRect = new Rect(px, py, pw, ph);

        GUI.Box(new Rect(px, py, pw, ph), "", panelStyle);
        float y = py + 10f;
        GUI.Label(new Rect(px + 12f, y, pw - 24f, 18f),
            "DATUM CONTROL — body snap stays active", labelStyle);
        y += 24f;

        GUI.Label(new Rect(px + 12f, y + 3f, 76f, 18f), "Plane", labelStyle);
        if (GUI.Button(new Rect(px + 86f, y, 88f, 24f), "Face", planeFrameMode == PlaneFrameMode.Face ? modeActiveStyle : modeInactiveStyle))
            SetPlaneFrameMode(PlaneFrameMode.Face);
        GUIStyle bisectorStyle = hasCurrentHinge
            ? (planeFrameMode == PlaneFrameMode.EdgeBisector ? modeActiveStyle : modeInactiveStyle)
            : modeDisabledStyle;
        if (GUI.Button(new Rect(px + 180f, y, 108f, 24f), "Edge 45/45", bisectorStyle))
            SetPlaneFrameMode(PlaneFrameMode.EdgeBisector);
        GUIStyle edgeAngleStyle = hasCurrentHinge
            ? (planeFrameMode == PlaneFrameMode.EdgeAngle ? modeActiveStyle : modeInactiveStyle)
            : modeDisabledStyle;
        if (GUI.Button(new Rect(px + 294f, y, 114f, 24f), "Edge+Angle", edgeAngleStyle))
            SetPlaneFrameMode(PlaneFrameMode.EdgeAngle);
        y += 30f;

        if (!hasCurrentHinge)
        {
            GUI.Label(new Rect(px + 12f, y, pw - 24f, 18f),
                "No valid sharp edge near pick for hinge rotation", labelStyle);
            y += 24f;
        }
        else if (showPlaneAngle)
        {
            y = DrawAngleControls(px, y, pw, "Plane °", ref planeAngleInput, -Mathf.Abs(currentHingeSignedAngle), Mathf.Abs(currentHingeSignedAngle));
        }

        GUI.Label(new Rect(px + 12f, y + 3f, 76f, 18f), "Grid", labelStyle);
        if (GUI.Button(new Rect(px + 86f, y, 88f, 24f), "Ref", orientationMode == PlaneOrientationMode.Reference ? modeActiveStyle : modeInactiveStyle))
            SetOrientationMode(PlaneOrientationMode.Reference);
        if (GUI.Button(new Rect(px + 180f, y, 88f, 24f), "Global", orientationMode == PlaneOrientationMode.Global ? modeActiveStyle : modeInactiveStyle))
            SetOrientationMode(PlaneOrientationMode.Global);
        if (GUI.Button(new Rect(px + 274f, y, 134f, 24f), "Global+Angle", orientationMode == PlaneOrientationMode.GlobalAngle ? modeActiveStyle : modeInactiveStyle))
            SetOrientationMode(PlaneOrientationMode.GlobalAngle);
        y += 30f;

        GUI.Label(new Rect(px + 12f, y + 3f, 76f, 18f), "Anchor", labelStyle);
        if (GUI.Button(new Rect(px + 86f, y, 88f, 24f), "Pick", originMode == PlaneOriginMode.Pick ? modeActiveStyle : modeInactiveStyle))
            SetOriginMode(PlaneOriginMode.Pick);
        if (GUI.Button(new Rect(px + 180f, y, 88f, 24f), "Global", originMode == PlaneOriginMode.Global ? modeActiveStyle : modeInactiveStyle))
            SetOriginMode(PlaneOriginMode.Global);
        y += 30f;

        GUI.Label(new Rect(px + 12f, y + 3f, 76f, 18f), "Surface", labelStyle);
        if (GUI.Button(new Rect(px + 86f, y, 88f, 24f), "Auto",
            surfaceInteractionMode == Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.Auto ? modeActiveStyle : modeInactiveStyle))
            SetSurfaceInteractionMode(Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.Auto);
        if (GUI.Button(new Rect(px + 180f, y, 88f, 24f), "Plane",
            surfaceInteractionMode == Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.PlaneOnly ? modeActiveStyle : modeInactiveStyle))
            SetSurfaceInteractionMode(Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.PlaneOnly);
        if (GUI.Button(new Rect(px + 274f, y, 134f, 24f), "Surface",
            surfaceInteractionMode == Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.SurfaceOnly ? modeActiveStyle : modeInactiveStyle))
            SetSurfaceInteractionMode(Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.SurfaceOnly);
        y += 30f;

        if (showGridAngle)
            y = DrawAngleControls(px, y, pw, "Grid °", ref gridAngleInput, -180f, 180f);

        string planeText = planeFrameMode switch
        {
            PlaneFrameMode.Face => "Plane: picked face normal",
            PlaneFrameMode.EdgeBisector => "Plane: bisector between clicked face and adjacent face along hinge edge",
            _ => $"Plane: rotate around hinge edge ({Mathf.Abs(currentHingeSignedAngle):0.#}° max toward adjacent face)"
        };
        GUI.Label(new Rect(px + 12f, y, pw - 24f, 18f), planeText, labelStyle);
        y += 18f;

        string gridText = orientationMode switch
        {
            PlaneOrientationMode.Reference => "Grid: projected edges can steer axes",
            PlaneOrientationMode.Global => "Grid: world axes projected onto plane",
            _ => "Grid: projected world axes rotated inside the plane"
        };
        GUI.Label(new Rect(px + 12f, y, pw - 24f, 18f), gridText, labelStyle);
        y += 22f;

        if (activePlane != null)
        {
            string selectedModeText = surfaceInteractionMode switch
            {
                Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.Auto => "Auto",
                Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.PlaneOnly => "Plane",
                _ => "Surface"
            };
            string runtimeModeText = activePlane.SurfacePatchVisualMode
                ? "curved patch active"
                : (surfaceInteractionMode == Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.SurfaceOnly ? "patch unavailable" : "planar fallback");
            string surfaceModeText = $"Surface: {selectedModeText} | {runtimeModeText} | offset {activePlane.DisplayOffsetMm:0.###} mm";
            GUI.Label(new Rect(px + 12f, y, pw - 140f, 18f), surfaceModeText, labelStyle);
            y += 20f;
        }

        if (GUI.Button(new Rect(px + pw - 118f, y - 2f, 106f, 24f), "Close Plane", cancelStyle))
            CloseActivePlane();
    }

    void ConfirmAngle()
    {
        float angle = ParseInvariantFloat(angleInput);

        // Place plane 2m in front of camera at given tilt from horizontal
        Vector3 origin = Camera.main.transform.position + Camera.main.transform.forward * 2f;
        float rad = angle * Mathf.Deg2Rad;
        Vector3 planeZ = new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad)).normalized;
        Vector3 planeX = Vector3.right;
        Vector3 planeY = Vector3.Cross(planeZ, planeX).normalized;

        SpawnDatumPlane(origin, planeX, planeY, planeZ);
        showAnglePanel = false;
    }

    static Vector3 ResolveVisibleSurfaceNormal(Vector3 rawNormal, Vector3 rayDirection)
    {
        Vector3 normal = rawNormal.sqrMagnitude > 1e-8f ? rawNormal.normalized : Vector3.up;
        Vector3 castDir = rayDirection.sqrMagnitude > 1e-8f ? rayDirection.normalized : Vector3.forward;
        if (Vector3.Dot(normal, castDir) > 0f)
            normal = -normal;
        return normal;
    }

    void SetPlaneFrameMode(PlaneFrameMode mode)
    {
        if (mode != PlaneFrameMode.Face && !hasCurrentHinge)
            return;

        planeFrameMode = mode;
        ReapplyActivePlaneOrientation();
    }

    void SetOrientationMode(PlaneOrientationMode mode)
    {
        orientationMode = mode;
        ReapplyActivePlaneOrientation();
    }

    void SetOriginMode(PlaneOriginMode mode)
    {
        originMode = mode;
        ReapplyActivePlaneOrientation();
    }

    void SetSurfaceInteractionMode(Setup3DatumSurfacePatchGrid.SurfaceInteractionMode mode)
    {
        surfaceInteractionMode = mode;
        if (activePlane == null)
            return;

        Setup3DatumSurfacePatchGrid patchGrid = activePlane.GetComponent<Setup3DatumSurfacePatchGrid>();
        if (patchGrid != null)
            patchGrid.SetInteractionMode(mode);

        ApplyOverlayForCurrentMode(activePlane.LocalZ);
    }

    void ReapplyActivePlaneOrientation()
    {
        if (activePlane == null || !hasCurrentPlaneState)
            return;

        BuildCurrentPlaneFrame(out Vector3 planeZ, out Vector3 referenceX, out Vector3 referenceY);
        ApplyOrientationMode(planeZ, referenceX, referenceY, out Vector3 localX, out Vector3 localY, cachedSurfaceHit);
        
        Vector3 newOrigin = currentPlaneOrigin;
        if (originMode == PlaneOriginMode.Global)
        {
            newOrigin = ProjectPointOntoPlane(Vector3.zero, currentPlaneOrigin, planeZ);
        }

        activePlane.SetDisplayOffset(ResolveDefaultDisplayPlaneOffsetMm(ResolveSharedCellSize()));
        activePlane.SetFrame(newOrigin, localX, localY, planeZ);
        Setup3DatumSurfacePatchGrid patchGrid = activePlane.GetComponent<Setup3DatumSurfacePatchGrid>();
        if (patchGrid != null)
            patchGrid.OnPlaneFrameChanged();
        ApplyOverlayForCurrentMode(planeZ);
    }

    void ApplyOverlayForCurrentMode(Vector3 planeZ)
    {
        if (activePlane == null)
            return;

        if (activePlane.SurfacePatchVisualMode)
        {
            activePlane.SetReferenceOverlay(null, null, null, null);
            return;
        }

        if (planeFrameMode == PlaneFrameMode.Face)
        {
            activePlane.SetReferenceOverlay(
                currentOverlay.lines,
                currentOverlay.corners,
                currentOverlay.hasPickPoint ? currentOverlay.rawPickPoint : (Vector3?)null,
                currentOverlay.hasSnapPoint ? currentOverlay.snappedPoint : (Vector3?)null);
            return;
        }

        Vector3? pick = currentOverlay.hasPickPoint
            ? ProjectPointOntoPlane(currentOverlay.rawPickPoint, currentPlaneOrigin, planeZ)
            : (Vector3?)null;
        Vector3? snap = currentOverlay.hasSnapPoint
            ? ProjectPointOntoPlane(currentOverlay.snappedPoint, currentPlaneOrigin, planeZ)
            : (Vector3?)null;
        activePlane.SetReferenceOverlay(null, null, pick, snap);
    }

    void BuildCurrentPlaneFrame(out Vector3 planeZ, out Vector3 referenceX, out Vector3 referenceY)
    {
        planeZ = currentPlaneNormal;
        referenceX = currentReferenceX;
        referenceY = currentReferenceY;

        if (planeFrameMode == PlaneFrameMode.Face || !hasCurrentHinge)
            return;

        if (planeFrameMode == PlaneFrameMode.EdgeBisector)
        {
            Vector3 bisector = currentHingePrimaryNormal + currentHingeSecondaryNormal;
            if (bisector.sqrMagnitude > 0.001f)
                planeZ = bisector.normalized;
        }
        else
        {
            float angleValue = ParseInvariantFloat(planeAngleInput);
            float maxAngle = Mathf.Abs(currentHingeSignedAngle);
            float amount = Mathf.Clamp(Mathf.Abs(angleValue), 0f, maxAngle);
            float towardSign = Mathf.Sign(currentHingeSignedAngle);
            float signedAmount = angleValue >= 0f ? amount * towardSign : -amount * towardSign;
            planeZ = (Quaternion.AngleAxis(signedAmount, currentHingeAxis) * currentHingePrimaryNormal).normalized;
        }

        referenceX = currentHingeAxis.normalized;
        if (Vector3.Dot(referenceX, currentReferenceX) < 0f)
            referenceX = -referenceX;
        referenceY = Vector3.Cross(planeZ, referenceX).normalized;
        referenceX = Vector3.Cross(referenceY, planeZ).normalized;
    }

    void ApplyOrientationMode(Vector3 planeZ, Vector3 referenceX, Vector3 referenceY, out Vector3 localX, out Vector3 localY)
    {
        // Delegate with no surface hit (reapply path)
        ApplyOrientationMode(planeZ, referenceX, referenceY, out localX, out localY, null);
    }

    void ApplyOrientationMode(Vector3 planeZ, Vector3 referenceX, Vector3 referenceY, out Vector3 localX, out Vector3 localY, RaycastHit? surfaceHit)
    {
        localX = referenceX;
        localY = referenceY;

        if (orientationMode == PlaneOrientationMode.Reference)
            return;

        // Try UV-based orientation first (matches mesh texture grid exactly)
        bool usedUV = false;
        if (surfaceHit.HasValue)
            usedUV = TryBuildUVBasis(surfaceHit.Value, planeZ, out localX, out localY);

        if (!usedUV)
            BuildProjectedGlobalBasis(planeZ, out localX, out localY);

        if (orientationMode == PlaneOrientationMode.GlobalAngle)
        {
            float angle = ParseInvariantFloat(gridAngleInput);
            Quaternion rotation = Quaternion.AngleAxis(angle, planeZ);
            localX = (rotation * localX).normalized;
            localY = Vector3.Cross(planeZ, localX).normalized;
        }
    }

    /// <summary>
    /// Compute grid orientation from the mesh triangle's UV gradient.
    /// The UV parameterization follows the mesh's polygon structure, so the
    /// resulting axes will align exactly with the mesh texture grid.
    /// </summary>
    static bool TryBuildUVBasis(RaycastHit hit, Vector3 planeZ, out Vector3 localX, out Vector3 localY)
    {
        localX = Vector3.right;
        localY = Vector3.up;

        if (!(hit.collider is MeshCollider meshCollider) || meshCollider.sharedMesh == null)
            return false;

        Mesh mesh = meshCollider.sharedMesh;
        Vector3[] vertices = mesh.vertices;
        Vector2[] uvs = mesh.uv;
        int[] triangles = mesh.triangles;

        if (uvs == null || uvs.Length == 0 || triangles == null)
            return false;

        int triBase = hit.triangleIndex * 3;
        if (triBase + 2 >= triangles.Length)
            return false;

        int i0 = triangles[triBase];
        int i1 = triangles[triBase + 1];
        int i2 = triangles[triBase + 2];

        if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
            return false;
        if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length)
            return false;

        Transform t = meshCollider.transform;
        Vector3 p0 = t.TransformPoint(vertices[i0]);
        Vector3 p1 = t.TransformPoint(vertices[i1]);
        Vector3 p2 = t.TransformPoint(vertices[i2]);

        Vector2 uv0 = uvs[i0];
        Vector2 uv1 = uvs[i1];
        Vector2 uv2 = uvs[i2];

        // Compute the world-space gradient of UV.u and UV.v across the triangle.
        // edge1 = p1 - p0, edge2 = p2 - p0
        // duv1 = uv1 - uv0, duv2 = uv2 - uv0
        // tangentU = (duv2.y * edge1 - duv1.y * edge2) / det
        // tangentV = (duv1.x * edge2 - duv2.x * edge1) / det
        Vector3 edge1 = p1 - p0;
        Vector3 edge2 = p2 - p0;
        Vector2 duv1 = uv1 - uv0;
        Vector2 duv2 = uv2 - uv0;

        float det = duv1.x * duv2.y - duv1.y * duv2.x;
        if (Mathf.Abs(det) < 1e-8f)
            return false; // degenerate UV mapping

        float invDet = 1f / det;
        Vector3 tangentU = (duv2.y * edge1 - duv1.y * edge2) * invDet;
        Vector3 tangentV = (duv1.x * edge2 - duv2.x * edge1) * invDet;

        // Project onto the tangent plane defined by planeZ
        Vector3 projU = Vector3.ProjectOnPlane(tangentU, planeZ);
        Vector3 projV = Vector3.ProjectOnPlane(tangentV, planeZ);

        if (projU.sqrMagnitude < 1e-6f || projV.sqrMagnitude < 1e-6f)
            return false;

        localX = projU.normalized;
        localY = projV.normalized;

        // Ensure orthogonality: force localY perpendicular to localX within the plane
        localY = Vector3.Cross(planeZ, localX).normalized;
        localX = Vector3.Cross(localY, planeZ).normalized;

        return true;
    }

    public static void BuildProjectedGlobalBasis(Vector3 planeZ, out Vector3 localX, out Vector3 localY)
    {
        planeZ = planeZ.sqrMagnitude > 1e-8f ? planeZ.normalized : Vector3.up;

        // Fallback: project world axes onto the tangent plane.
        // Pick the world axis whose projection is longest (most visible on surface).
        Vector3[] worldAxes = { Vector3.right, Vector3.up, Vector3.forward };

        Vector3 bestAxis = Vector3.right;
        float bestLen = -1f;
        for (int i = 0; i < worldAxes.Length; i++)
        {
            Vector3 proj = Vector3.ProjectOnPlane(worldAxes[i], planeZ);
            float len = proj.sqrMagnitude;
            if (len > bestLen + 1e-8f)
            {
                bestLen = len;
                bestAxis = proj;
            }
        }

        if (bestAxis.sqrMagnitude <= 1e-8f)
        {
            Vector3 fallback = Mathf.Abs(Vector3.Dot(planeZ, Vector3.up)) < 0.99f ? Vector3.up : Vector3.right;
            bestAxis = Vector3.ProjectOnPlane(fallback, planeZ);
        }

        localX = bestAxis.normalized;
        localY = Vector3.Cross(planeZ, localX).normalized;
        localX = Vector3.Cross(localY, planeZ).normalized;
    }

    static float ParseInvariantFloat(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0f;

        if (float.TryParse(text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float value))
            return value;

        return 0f;
    }

    public static float ResolveSharedCellSize()
    {
        VoxelBody[] bodies = FindObjectsByType<VoxelBody>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var body in bodies)
        {
            if (body != null && body.bodyVoxelSize > 0f)
                return body.bodyVoxelSize;
        }

        return 1f;
    }

    float DrawAngleControls(float px, float y, float pw, string label, ref string valueText, float minAngle, float maxAngle)
    {
        GUI.Label(new Rect(px + 12f, y + 3f, 56f, 18f), label, labelStyle);
        GUI.Box(new Rect(px + 64f, y, 58f, 24f), FormatAngle(ParseInvariantFloat(valueText)), panelStyle);

        float startX = px + 128f;
        float buttonW = 34f;
        float gap = 4f;
        float[] deltas = { -45f, -15f, -5f, -1f, 1f, 5f, 15f, 45f };
        for (int i = 0; i < deltas.Length; i++)
        {
            float bx = startX + i * (buttonW + gap);
            string labelText = deltas[i] > 0f ? $"+{deltas[i]:0}" : $"{deltas[i]:0}";
            if (GUI.Button(new Rect(bx, y, buttonW, 24f), labelText, modeInactiveStyle))
            {
                valueText = AdjustAngleString(valueText, deltas[i], minAngle, maxAngle);
                ReapplyActivePlaneOrientation();
            }
        }

        if (GUI.Button(new Rect(px + pw - 54f, y + 28f, 42f, 22f), "0°", cancelStyle))
        {
            valueText = "0";
            ReapplyActivePlaneOrientation();
        }

        return y + 36f;
    }

    string AdjustAngleString(string current, float delta, float minAngle, float maxAngle)
    {
        float value = ParseInvariantFloat(current);
        value = Mathf.Clamp(value + delta, minAngle, maxAngle);
        return FormatAngle(value);
    }

    string FormatAngle(float value)
    {
        value = Mathf.Abs(value) < 0.0001f ? 0f : value;
        if (Mathf.Abs(value - Mathf.Round(value)) < 0.001f)
            return Mathf.RoundToInt(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }

    void CacheHingeReference(Vector3 faceNormal, bool hasHingeCandidate, EdgeCandidate hingeCandidate)
    {
        hasCurrentHinge = false;
        currentHingeAxis = Vector3.zero;
        currentHingePrimaryNormal = Vector3.zero;
        currentHingeSecondaryNormal = Vector3.zero;
        currentHingeSignedAngle = 0f;

        if (!hasHingeCandidate || !hingeCandidate.canHinge)
            return;

        Vector3 normalA = Vector3.ProjectOnPlane(hingeCandidate.normalA, hingeCandidate.direction).normalized;
        Vector3 normalB = Vector3.ProjectOnPlane(hingeCandidate.normalB, hingeCandidate.direction).normalized;
        if (normalA.sqrMagnitude < 0.01f || normalB.sqrMagnitude < 0.01f)
            return;

        float angleToA = Vector3.Angle(faceNormal, normalA);
        float angleToB = Vector3.Angle(faceNormal, normalB);
        currentHingePrimaryNormal = angleToA <= angleToB ? normalA : normalB;
        currentHingeSecondaryNormal = angleToA <= angleToB ? normalB : normalA;
        currentHingeAxis = hingeCandidate.direction.normalized;
        currentHingeSignedAngle = Vector3.SignedAngle(currentHingePrimaryNormal, currentHingeSecondaryNormal, currentHingeAxis);

        if (Mathf.Abs(currentHingeSignedAngle) < 0.1f)
            return;

        hasCurrentHinge = true;
    }

    public void CloseActivePlane()
    {
        if (sketchTool != null)
            sketchTool.SetActivePlane(null);
        if (activePlane != null)
        {
            Destroy(activePlane.gameObject);
            activePlane = null;
        }
        CurrentOrientationPanelRect = default;
        hasCurrentPlaneState = false;
        showAnglePanel = false;
    }

    void InitStyles()
    {
        if (stylesReady) return;
        stylesReady = true;

        panelStyle = new GUIStyle(GUI.skin.box);
        panelStyle.normal.background = MakeTex(2, 2, new Color(0.12f, 0.14f, 0.18f, 0.96f));

        labelStyle = new GUIStyle(GUI.skin.label);
        labelStyle.normal.textColor = new Color(0.85f, 0.95f, 1f);
        labelStyle.fontSize = 12;

        confirmStyle = new GUIStyle(GUI.skin.button);
        confirmStyle.normal.background  = MakeTex(2, 2, new Color(0.20f, 0.55f, 0.90f, 1f));
        confirmStyle.hover.background   = MakeTex(2, 2, new Color(0.30f, 0.65f, 1.00f, 1f));
        confirmStyle.normal.textColor   = Color.white;
        confirmStyle.fontSize = 13;

        cancelStyle = new GUIStyle(GUI.skin.button);
        cancelStyle.normal.background   = MakeTex(2, 2, new Color(0.45f, 0.13f, 0.13f, 1f));
        cancelStyle.hover.background    = MakeTex(2, 2, new Color(0.65f, 0.18f, 0.18f, 1f));
        cancelStyle.normal.textColor    = Color.white;
        cancelStyle.fontSize = 13;

        modeActiveStyle = new GUIStyle(GUI.skin.button);
        modeActiveStyle.normal.background = MakeTex(2, 2, new Color(0.20f, 0.55f, 0.90f, 1f));
        modeActiveStyle.hover.background = MakeTex(2, 2, new Color(0.28f, 0.63f, 0.98f, 1f));
        modeActiveStyle.normal.textColor = Color.white;
        modeActiveStyle.fontSize = 12;

        modeInactiveStyle = new GUIStyle(GUI.skin.button);
        modeInactiveStyle.normal.background = MakeTex(2, 2, new Color(0.24f, 0.27f, 0.33f, 1f));
        modeInactiveStyle.hover.background = MakeTex(2, 2, new Color(0.31f, 0.34f, 0.40f, 1f));
        modeInactiveStyle.normal.textColor = new Color(0.88f, 0.92f, 0.96f);
        modeInactiveStyle.fontSize = 12;

        modeDisabledStyle = new GUIStyle(modeInactiveStyle);
        modeDisabledStyle.normal.background = MakeTex(2, 2, new Color(0.18f, 0.18f, 0.20f, 1f));
        modeDisabledStyle.normal.textColor = new Color(0.45f, 0.48f, 0.52f);
    }

    Texture2D MakeTex(int w, int h, Color col)
    {
        return SolidColorTextureCache.Get(col);
    }

    bool TrySnapPlaneToProjectedReferences(RaycastHit hit, ref Vector3 origin, ref Vector3 planeX, ref Vector3 planeY,
        Vector3 planeZ, out PlaneReferenceOverlay overlay, out EdgeCandidate hingeCandidate, out bool hasHingeCandidate)
    {
        overlay = new PlaneReferenceOverlay
        {
            lines = new List<Setup3DatumPlaneVisual.ReferenceLine>(),
            corners = new List<Vector3>(),
            rawPickPoint = hit.point,
            snappedPoint = origin,
            hasPickPoint = true,
            hasSnapPoint = false
        };
        hingeCandidate = default;
        hasHingeCandidate = false;

        if (!(hit.collider is MeshCollider meshCollider) || meshCollider.sharedMesh == null)
            return false;

        MeshFeatureCache cache = GetFeatureCache(meshCollider.sharedMesh);
        if (cache == null || cache.featureEdges == null || cache.featureEdges.Length == 0)
            return false;

        Transform meshTransform = meshCollider.transform;
        Vector3 pickPoint = ProjectPointOntoPlane(hit.point, origin, planeZ);

        EdgeCandidate bestEdge = default;
        bool hasEdge = false;

        foreach (var featureEdge in cache.featureEdges)
        {
            Vector3 world0 = meshTransform.TransformPoint(cache.vertices[featureEdge.v0]);
            Vector3 world1 = meshTransform.TransformPoint(cache.vertices[featureEdge.v1]);
            Vector3 proj0 = ProjectPointOntoPlane(world0, origin, planeZ);
            Vector3 proj1 = ProjectPointOntoPlane(world1, origin, planeZ);
            float projectedLength = Vector3.Distance(proj0, proj1);
            if (projectedLength < 0.25f) continue;

            Vector3 nearest = ClosestPointOnSegment(proj0, proj1, pickPoint);
            float distance = Vector3.Distance(nearest, pickPoint);
            if (distance > ReferenceSearchRadiusMm) continue;

            if (!hasEdge || distance < bestEdge.distance || (Mathf.Approximately(distance, bestEdge.distance) && projectedLength > bestEdge.projectedLength))
            {
                hasEdge = true;
                bestEdge = new EdgeCandidate
                {
                    snapPoint = nearest,
                    direction = (world1 - world0).normalized,
                    distance = distance,
                    projectedLength = projectedLength,
                    normalA = featureEdge.normalA,
                    normalB = featureEdge.normalB,
                    canHinge = featureEdge.faceCount >= 2,
                    overlayLine = new Setup3DatumPlaneVisual.ReferenceLine
                    {
                        start = proj0,
                        end = proj1,
                        highlight = true
                    }
                };
            }
        }

        CornerCandidate bestCorner = default;
        bool hasCorner = false;
        foreach (int cornerVertex in cache.cornerVertices)
        {
            Vector3 worldCorner = meshTransform.TransformPoint(cache.vertices[cornerVertex]);
            Vector3 projectedCorner = ProjectPointOntoPlane(worldCorner, origin, planeZ);
            float distance = Vector3.Distance(projectedCorner, pickPoint);
            if (distance > ReferenceSearchRadiusMm) continue;

            if (!hasCorner || distance < bestCorner.distance)
            {
                hasCorner = true;
                bestCorner = new CornerCandidate
                {
                    snapPoint = projectedCorner,
                    distance = distance,
                    directions = GetProjectedDirectionsForVertex(cache, cornerVertex, meshTransform, origin, planeZ)
                };
            }
        }

        if (hasCorner)
        {
            origin = bestCorner.snapPoint;
            overlay.snappedPoint = origin;
            overlay.hasSnapPoint = true;
            overlay.corners.Add(bestCorner.snapPoint);
            TryAddSnapGuide(overlay.lines, pickPoint, bestCorner.snapPoint);
            if (hasEdge)
            {
                hingeCandidate = bestEdge;
                hasHingeCandidate = bestEdge.canHinge;
            }
            TryAlignFrameToCorner(bestCorner.directions, planeZ, ref planeX, ref planeY);
            return true;
        }

        if (hasEdge)
        {
            origin = bestEdge.snapPoint;
            overlay.snappedPoint = origin;
            overlay.hasSnapPoint = true;
            overlay.lines.Add(bestEdge.overlayLine);
            TryAddSnapGuide(overlay.lines, pickPoint, bestEdge.snapPoint);
            hingeCandidate = bestEdge;
            hasHingeCandidate = bestEdge.canHinge;

            if (bestEdge.projectedLength >= AxisAlignMinEdgeLengthMm)
                AlignFrameToEdge(bestEdge.direction, planeZ, ref planeX, ref planeY);

            return true;
        }

        // Fallback: Snap to the nearest raw mesh vertex for repeatable accuracy on smooth surfaces
        float bestVertexDist = ReferenceSearchRadiusMm;
        int bestVertexIndex = -1;
        for (int i = 0; i < cache.vertices.Length; i++)
        {
            Vector3 worldVertex = meshTransform.TransformPoint(cache.vertices[i]);
            Vector3 projectedVertex = ProjectPointOntoPlane(worldVertex, origin, planeZ);
            float dist = Vector3.Distance(projectedVertex, pickPoint);
            if (dist < bestVertexDist)
            {
                bestVertexDist = dist;
                bestVertexIndex = i;
            }
        }

        if (bestVertexIndex >= 0)
        {
            Vector3 worldVertex = meshTransform.TransformPoint(cache.vertices[bestVertexIndex]);
            Vector3 projectedVertex = ProjectPointOntoPlane(worldVertex, origin, planeZ);
            origin = projectedVertex;
            overlay.snappedPoint = origin;
            overlay.hasSnapPoint = true;
            overlay.corners.Add(origin);
            TryAddSnapGuide(overlay.lines, pickPoint, origin);
            return true;
        }

        return false;
    }

    MeshFeatureCache GetFeatureCache(Mesh mesh)
    {
        if (featureCacheByMesh.TryGetValue(mesh, out MeshFeatureCache cached))
            return cached;

        Vector3[] rawVertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        Vector3[] vertices = BuildWeldedVertices(rawVertices, triangles, out int[] weldedTriangles);
        if (vertices == null || triangles == null || triangles.Length < 3)
            return null;

        var edgeMap = new Dictionary<EdgeKey, EdgeAccum>();

        for (int i = 0; i < weldedTriangles.Length; i += 3)
        {
            int i0 = weldedTriangles[i];
            int i1 = weldedTriangles[i + 1];
            int i2 = weldedTriangles[i + 2];
            Vector3 n = Vector3.Cross(vertices[i1] - vertices[i0], vertices[i2] - vertices[i0]).normalized;
            AccumulateEdge(edgeMap, i0, i1, n);
            AccumulateEdge(edgeMap, i1, i2, n);
            AccumulateEdge(edgeMap, i2, i0, n);
        }

        var featureEdges = new List<FeatureEdge>();
        var featureEdgesByVertex = new Dictionary<int, List<int>>();

        foreach (var kvp in edgeMap)
        {
            EdgeAccum edge = kvp.Value;
            bool isBoundary = edge.faceCount == 1;
            bool isSharp = isBoundary;
            if (edge.faceCount >= 2)
            {
                float angle = Vector3.Angle(edge.normalA, edge.normalB);
                isSharp = angle >= SharpEdgeAngleDeg;
            }

            if (!isSharp) continue;

            int edgeIndex = featureEdges.Count;
            featureEdges.Add(new FeatureEdge
            {
                v0 = edge.v0,
                v1 = edge.v1,
                isBoundary = isBoundary,
                faceCount = edge.faceCount,
                normalA = edge.normalA,
                normalB = edge.faceCount >= 2 ? edge.normalB : edge.normalA
            });
            AddIncidentEdge(featureEdgesByVertex, edge.v0, edgeIndex);
            AddIncidentEdge(featureEdgesByVertex, edge.v1, edgeIndex);
        }

        var cornerVertices = new HashSet<int>();
        foreach (var kvp in featureEdgesByVertex)
        {
            if (IsCornerVertex(kvp.Key, featureEdges, vertices, featureEdgesByVertex))
                cornerVertices.Add(kvp.Key);
        }

        cached = new MeshFeatureCache
        {
            vertices = vertices,
            featureEdges = featureEdges.ToArray(),
            featureEdgesByVertex = featureEdgesByVertex,
            cornerVertices = cornerVertices
        };
        featureCacheByMesh[mesh] = cached;
        return cached;
    }

    static Vector3[] BuildWeldedVertices(Vector3[] rawVertices, int[] rawTriangles, out int[] weldedTriangles)
    {
        if (rawVertices == null || rawTriangles == null)
        {
            weldedTriangles = null;
            return null;
        }

        var weldedVertices = new List<Vector3>();
        var keyToIndex = new Dictionary<VertexKey, int>();
        var rawToWelded = new int[rawVertices.Length];

        for (int i = 0; i < rawVertices.Length; i++)
        {
            VertexKey key = new VertexKey(rawVertices[i]);
            if (!keyToIndex.TryGetValue(key, out int weldedIndex))
            {
                weldedIndex = weldedVertices.Count;
                weldedVertices.Add(rawVertices[i]);
                keyToIndex[key] = weldedIndex;
            }

            rawToWelded[i] = weldedIndex;
        }

        weldedTriangles = new int[rawTriangles.Length];
        for (int i = 0; i < rawTriangles.Length; i++)
            weldedTriangles[i] = rawToWelded[rawTriangles[i]];

        return weldedVertices.ToArray();
    }

    static void AccumulateEdge(Dictionary<EdgeKey, EdgeAccum> edgeMap, int v0, int v1, Vector3 normal)
    {
        EdgeKey key = new EdgeKey(v0, v1);
        if (!edgeMap.TryGetValue(key, out EdgeAccum accum))
        {
            accum = new EdgeAccum
            {
                v0 = v0,
                v1 = v1,
                normalA = normal,
                faceCount = 1
            };
            edgeMap[key] = accum;
            return;
        }

        if (accum.faceCount == 1)
            accum.normalB = normal;
        accum.faceCount++;
    }

    static void AddIncidentEdge(Dictionary<int, List<int>> incidentMap, int vertexIndex, int edgeIndex)
    {
        if (!incidentMap.TryGetValue(vertexIndex, out List<int> edges))
        {
            edges = new List<int>();
            incidentMap[vertexIndex] = edges;
        }
        edges.Add(edgeIndex);
    }

    static bool IsCornerVertex(int vertexIndex, List<FeatureEdge> featureEdges, Vector3[] vertices,
        Dictionary<int, List<int>> featureEdgesByVertex)
    {
        if (!featureEdgesByVertex.TryGetValue(vertexIndex, out List<int> incidentEdges) || incidentEdges.Count < 2)
            return false;

        var dirs = new List<Vector3>();
        foreach (int edgeIndex in incidentEdges)
        {
            FeatureEdge edge = featureEdges[edgeIndex];
            int otherIndex = edge.v0 == vertexIndex ? edge.v1 : edge.v0;
            Vector3 dir = (vertices[otherIndex] - vertices[vertexIndex]).normalized;
            if (dir.sqrMagnitude > 0.01f)
                dirs.Add(dir);
        }

        for (int i = 0; i < dirs.Count; i++)
        {
            for (int j = i + 1; j < dirs.Count; j++)
            {
                float angle = Vector3.Angle(dirs[i], dirs[j]);
                if (angle >= CornerMinAngleDeg && angle <= 180f - CornerMinAngleDeg)
                    return true;
            }
        }

        return false;
    }

    List<Vector3> GetProjectedDirectionsForVertex(MeshFeatureCache cache, int vertexIndex, Transform meshTransform,
        Vector3 planeOrigin, Vector3 planeNormal)
    {
        var result = new List<Vector3>();
        if (!cache.featureEdgesByVertex.TryGetValue(vertexIndex, out List<int> incidentEdges))
            return result;

        Vector3 projectedVertex = ProjectPointOntoPlane(meshTransform.TransformPoint(cache.vertices[vertexIndex]), planeOrigin, planeNormal);
        foreach (int edgeIndex in incidentEdges)
        {
            FeatureEdge edge = cache.featureEdges[edgeIndex];
            int otherIndex = edge.v0 == vertexIndex ? edge.v1 : edge.v0;
            Vector3 projectedOther = ProjectPointOntoPlane(meshTransform.TransformPoint(cache.vertices[otherIndex]), planeOrigin, planeNormal);
            Vector3 dir = projectedOther - projectedVertex;
            if (dir.sqrMagnitude < 0.25f) continue;
            result.Add(dir.normalized);
        }

        return result;
    }

    static Vector3 ProjectPointOntoPlane(Vector3 point, Vector3 planeOrigin, Vector3 planeNormal)
    {
        return point - planeNormal * Vector3.Dot(point - planeOrigin, planeNormal);
    }

    static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        Vector3 ab = b - a;
        float denom = ab.sqrMagnitude;
        if (denom < 1e-6f) return a;
        float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / denom);
        return a + ab * t;
    }

    static void TryAddSnapGuide(List<Setup3DatumPlaneVisual.ReferenceLine> lines, Vector3 pickPoint, Vector3 snappedPoint)
    {
        if (lines == null)
            return;

        if ((snappedPoint - pickPoint).sqrMagnitude < 0.01f)
            return;

        lines.Add(new Setup3DatumPlaneVisual.ReferenceLine
        {
            start = pickPoint,
            end = snappedPoint,
            highlight = true
        });
    }

    static void AlignFrameToEdge(Vector3 edgeDir, Vector3 planeZ, ref Vector3 planeX, ref Vector3 planeY)
    {
        Vector3 flatDir = Vector3.ProjectOnPlane(edgeDir, planeZ).normalized;
        if (flatDir.sqrMagnitude < 0.01f) return;

        float xScore = Mathf.Abs(Vector3.Dot(flatDir, planeX));
        float yScore = Mathf.Abs(Vector3.Dot(flatDir, planeY));

        if (xScore >= yScore)
        {
            planeX = MatchSign(flatDir, planeX);
            planeY = Vector3.Cross(planeZ, planeX).normalized;
        }
        else
        {
            planeY = MatchSign(flatDir, planeY);
            planeX = Vector3.Cross(planeY, planeZ).normalized;
        }
    }

    static void TryAlignFrameToCorner(List<Vector3> directions, Vector3 planeZ, ref Vector3 planeX, ref Vector3 planeY)
    {
        if (directions == null || directions.Count == 0) return;

        Vector3 bestPrimary = MatchSign(Vector3.ProjectOnPlane(directions[0], planeZ).normalized, planeX);
        float bestPrimaryScore = Mathf.Abs(Vector3.Dot(bestPrimary, planeX));

        foreach (Vector3 dir in directions)
        {
            Vector3 flatDir = Vector3.ProjectOnPlane(dir, planeZ).normalized;
            if (flatDir.sqrMagnitude < 0.01f) continue;
            float score = Mathf.Abs(Vector3.Dot(flatDir, planeX));
            if (score > bestPrimaryScore)
            {
                bestPrimary = MatchSign(flatDir, planeX);
                bestPrimaryScore = score;
            }
        }

        planeX = bestPrimary;
        planeY = Vector3.Cross(planeZ, planeX).normalized;

        Vector3 bestSecondary = planeY;
        float bestSecondaryScore = -1f;
        foreach (Vector3 dir in directions)
        {
            Vector3 flatDir = Vector3.ProjectOnPlane(dir, planeZ).normalized;
            if (flatDir.sqrMagnitude < 0.01f) continue;

            float angle = Vector3.Angle(flatDir, planeX);
            if (angle < 60f || angle > 120f) continue;

            float score = Mathf.Abs(Vector3.Dot(flatDir, planeY));
            if (score > bestSecondaryScore)
            {
                bestSecondary = MatchSign(flatDir, planeY);
                bestSecondaryScore = score;
            }
        }

        if (bestSecondaryScore >= 0f)
        {
            planeY = bestSecondary;
            planeX = Vector3.Cross(planeY, planeZ).normalized;
        }
    }

    static Vector3 MatchSign(Vector3 candidate, Vector3 reference)
    {
        return Vector3.Dot(candidate, reference) >= 0f ? candidate : -candidate;
    }

    public static float ResolveDefaultDisplayPlaneOffsetMm(float spacing)
    {
        return 0f;
    }
}
