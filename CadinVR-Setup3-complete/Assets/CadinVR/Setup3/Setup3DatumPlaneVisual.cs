// Setup3DatumPlaneVisual.cs — VERSION 1
// Attach to: spawned DatumPlane GameObject (created at runtime by Setup3DatumPlaneController).
//
// Renders via GL (OnRenderObject) — no World Space Canvas (Rule 10).
//   1. Semi-transparent quad (procedural mesh + runtime transparent material)
//   2. Local axis arrows: X=red, Y=green, Z=blue (normal direction)
//   3. 1mm grid overlay on the visible offset display plane
//   4. World axis widget bottom-left (world XYZ shown in corner)
//
// SetFrame() must be called immediately after AddComponent by Setup3DatumPlaneController.

using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

public class Setup3DatumPlaneVisual : MonoBehaviour
{
    public struct ReferenceLine
    {
        public Vector3 start;
        public Vector3 end;
        public bool highlight;
    }

    // Local coordinate frame (world-space vectors)
    private Vector3 origin;
    private Vector3 displayOrigin;
    private Vector3 localX, localY, localZ;

    private const float DefaultHalfSize = 50f; // default helper plane half-size in mm
    private const float GridExtent  = 50f;   // ±50 mm from origin
    private const float ArrowLen    = 15f;   // axis arrow length (mm)
    private const float OverlayLiftMm = 0.12f;
    private static readonly Color DefaultQuadColor = new Color(0.3f, 0.6f, 1.0f, 38f / 255f);
    private static readonly Color SurfacePatchQuadColor = new Color(0.3f, 0.6f, 1.0f, 8f / 255f);
    private static readonly Color DefaultGridColor = new Color(0.17f, 0.31f, 0.48f, 0.42f);
    private float gridSpacing = 1f;

    private Material glMat;
    private GameObject quadGO;
    private Mesh quadMesh;
    private MeshRenderer quadRenderer;
    private Material quadMaterial;
    private bool frameSet = false;
    private float displayOffsetMm = 0f;
    private bool surfacePatchVisualMode = false;
    private float halfSizeMm = DefaultHalfSize;
    private readonly List<ReferenceLine> referenceLines = new List<ReferenceLine>();
    private readonly List<Vector3> referenceCorners = new List<Vector3>();
    private bool hasPickMarker = false;
    private bool hasSnapMarker = false;
    private Vector3 pickedPoint;
    private Vector3 snappedPoint;

    // Public accessors used by Setup3SketchTool and Setup3DatumPreviewManager
    public Vector3 Origin  => origin;
    public Vector3 DisplayOrigin => displayOrigin;
    public Vector3 LocalX  => localX;
    public Vector3 LocalY  => localY;
    public Vector3 LocalZ  => localZ;
    public float HalfSize => halfSizeMm;
    public float GridSpacing => gridSpacing;
    public float DisplayOffsetMm => displayOffsetMm;
    public bool SurfacePatchVisualMode => surfacePatchVisualMode;

    public void SetDisplayOffset(float offsetMm)
    {
        displayOffsetMm = Mathf.Max(0f, offsetMm);
        if (frameSet)
            ApplyDisplayTransform();
    }

    public void SetGridSpacing(float spacing)
    {
        gridSpacing = Mathf.Max(0.01f, spacing);
    }

    public void SetSurfacePatchVisualMode(bool enabled, float preferredHalfSizeMm = -1f)
    {
        surfacePatchVisualMode = enabled;
        halfSizeMm = enabled && preferredHalfSizeMm > 0f
            ? Mathf.Max(2f, preferredHalfSizeMm)
            : DefaultHalfSize;
        if (frameSet)
            BuildQuadMesh();
        UpdateQuadMaterialColor();
    }

    public void SetFrame(Vector3 o, Vector3 x, Vector3 y, Vector3 z)
    {
        origin = o;
        NormalizeFrame(ref x, ref y, ref z);
        localX = x;
        localY = y;
        localZ = z;
        frameSet = true;
        ApplyDisplayTransform();
        BuildQuadMesh();
    }

    static void NormalizeFrame(ref Vector3 x, ref Vector3 y, ref Vector3 z)
    {
        z = z.sqrMagnitude > 1e-8f ? z.normalized : Vector3.up;

        x = Vector3.ProjectOnPlane(x, z);
        if (x.sqrMagnitude <= 1e-8f)
            x = Vector3.ProjectOnPlane(y, z);
        if (x.sqrMagnitude <= 1e-8f)
        {
            Vector3 fallback = Mathf.Abs(Vector3.Dot(z, Vector3.up)) < 0.99f ? Vector3.up : Vector3.right;
            x = Vector3.ProjectOnPlane(fallback, z);
        }
        x.Normalize();

        y = Vector3.ProjectOnPlane(y, z);
        y -= Vector3.Dot(y, x) * x;
        if (y.sqrMagnitude <= 1e-8f)
            y = Vector3.Cross(z, x);
        y.Normalize();

        x = Vector3.Cross(y, z).normalized;
        y = Vector3.Cross(z, x).normalized;
    }

    public void SetReferenceOverlay(IEnumerable<ReferenceLine> lines, IEnumerable<Vector3> corners,
        Vector3? rawPickPoint = null, Vector3? snappedOrigin = null)
    {
        referenceLines.Clear();
        referenceCorners.Clear();

        if (lines != null)
            referenceLines.AddRange(lines);
        if (corners != null)
            referenceCorners.AddRange(corners);

        hasPickMarker = rawPickPoint.HasValue;
        hasSnapMarker = snappedOrigin.HasValue;
        pickedPoint = rawPickPoint ?? Vector3.zero;
        snappedPoint = snappedOrigin ?? Vector3.zero;
    }

    void Awake()
    {
        glMat = new Material(Shader.Find("Hidden/Internal-Colored"));
        glMat.hideFlags = HideFlags.HideAndDontSave;
        glMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        glMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        glMat.SetInt("_Cull", 0);
        glMat.SetInt("_ZWrite", 0);
    }

    void BuildQuadMesh()
    {
        if (quadGO == null)
        {
            quadGO = new GameObject("DatumQuad");
            quadGO.transform.SetParent(transform, false);

            var mf = quadGO.AddComponent<MeshFilter>();
            quadMesh = new Mesh { name = "DatumQuadMesh" };
            mf.sharedMesh = quadMesh;

            quadRenderer = quadGO.AddComponent<MeshRenderer>();
            quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
            quadRenderer.receiveShadows = false;

            quadMaterial = RuntimeTransparentMaterial.Create(
                DefaultQuadColor,
                doubleSided: true);
            if (quadMaterial != null)
                quadRenderer.sharedMaterial = quadMaterial;
        }
        else if (quadRenderer == null)
        {
            quadRenderer = quadGO.GetComponent<MeshRenderer>();
        }

        float hs = halfSizeMm;
        // The root is positioned at the display-plane origin, so the quad
        // vertices stay in local plane space here.
        Vector3 v0 = -localX * hs - localY * hs;
        Vector3 v1 =  localX * hs - localY * hs;
        Vector3 v2 =  localX * hs + localY * hs;
        Vector3 v3 = -localX * hs + localY * hs;

        quadMesh.Clear();
        quadMesh.vertices = new[] { v0, v1, v2, v3 };
        // Double-sided: front + back
        quadMesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 2, 1, 0, 3, 2, 0 };
        quadMesh.RecalculateNormals();
        quadMesh.RecalculateBounds();
        UpdateQuadMaterialColor();
    }

    void ApplyDisplayTransform()
    {
        displayOrigin = origin + localZ * displayOffsetMm;
        transform.position = displayOrigin;
    }

    void OnRenderObject()
    {
        if (!frameSet || glMat == null) return;

        GL.PushMatrix();
        glMat.SetPass(0);

        DrawGrid();
        DrawAxisArrows();
        DrawReferenceOverlay();
        DrawWorldWidget();

        GL.PopMatrix();
    }

    void DrawReferenceOverlay()
    {
        if (referenceLines.Count > 0)
        {
            GL.Begin(GL.LINES);
            foreach (var line in referenceLines)
            {
                GL.Color(line.highlight
                    ? new Color(1f, 0.88f, 0.22f, 0.92f)
                    : new Color(1f, 0.78f, 0.18f, 0.58f));
                GL.Vertex(ToOverlayPoint(line.start));
                GL.Vertex(ToOverlayPoint(line.end));
            }
            GL.End();
        }

        foreach (var corner in referenceCorners)
            DrawMarker(ToOverlayPoint(corner), new Color(1f, 0.74f, 0.12f, 1f), 1.2f);

        if (hasPickMarker)
            DrawMarker(ToOverlayPoint(pickedPoint), new Color(0.45f, 0.95f, 1f, 1f), 1.0f);

        if (hasSnapMarker)
            DrawMarker(ToOverlayPoint(snappedPoint), new Color(1f, 1f, 1f, 1f), 1.35f);
    }

    void DrawMarker(Vector3 point, Color color, float size)
    {
        GL.Begin(GL.LINES);
        GL.Color(color);
        GL.Vertex(point - localX * size); GL.Vertex(point + localX * size);
        GL.Vertex(point - localY * size); GL.Vertex(point + localY * size);
        GL.End();
    }

    void DrawGrid()
    {
        if (surfacePatchVisualMode)
            return;

        Color gridColor = DefaultGridColor;
        if (gridColor.a <= 0.001f)
            return;

        int steps = Mathf.RoundToInt(GridExtent / gridSpacing);
        GL.Begin(GL.LINES);
        GL.Color(gridColor);
        for (int i = -steps; i <= steps; i++)
        {
            float t = i * gridSpacing;
            // Lines parallel to localX
            GL.Vertex(displayOrigin + localY * t - localX * GridExtent);
            GL.Vertex(displayOrigin + localY * t + localX * GridExtent);
            // Lines parallel to localY
            GL.Vertex(displayOrigin + localX * t - localY * GridExtent);
            GL.Vertex(displayOrigin + localX * t + localY * GridExtent);
        }
        GL.End();
    }

    void DrawAxisArrows()
    {
        GL.Begin(GL.LINES);
        // X — red
        GL.Color(new Color(1f, 0.25f, 0.25f, 1f));
        GL.Vertex(displayOrigin); GL.Vertex(displayOrigin + localX * ArrowLen);
        // Y — green
        GL.Color(new Color(0.25f, 1f, 0.25f, 1f));
        GL.Vertex(displayOrigin); GL.Vertex(displayOrigin + localY * ArrowLen);
        // Z (normal) — blue
        GL.Color(new Color(0.25f, 0.5f, 1f, 1f));
        GL.Vertex(displayOrigin); GL.Vertex(displayOrigin + localZ * ArrowLen);
        GL.End();
    }

    Vector3 ToOverlayPoint(Vector3 anchorPoint)
    {
        return anchorPoint + localZ * (displayOffsetMm + OverlayLiftMm);
    }

    void DrawWorldWidget()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        // Widget anchor: bottom-left screen corner, projected to near plane
        float margin = 55f;
        Vector3 screenAnchor = new Vector3(margin, Screen.height - margin, cam.nearClipPlane + 0.05f);
        Vector3 worldAnchor  = cam.ScreenToWorldPoint(screenAnchor);

        // Scale arrows so they appear the same screen size regardless of distance
        float dist  = Vector3.Distance(cam.transform.position, worldAnchor);
        float scale = dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2f / Screen.height * 30f;

        GL.Begin(GL.LINES);
        GL.Color(new Color(1f, 0.25f, 0.25f, 0.85f));
        GL.Vertex(worldAnchor); GL.Vertex(worldAnchor + Vector3.right   * scale);
        GL.Color(new Color(0.25f, 1f, 0.25f, 0.85f));
        GL.Vertex(worldAnchor); GL.Vertex(worldAnchor + Vector3.up      * scale);
        GL.Color(new Color(0.25f, 0.5f, 1f, 0.85f));
        GL.Vertex(worldAnchor); GL.Vertex(worldAnchor + Vector3.forward * scale);
        GL.End();
    }

    void OnDestroy()
    {
        if (glMat  != null) Destroy(glMat);
        if (quadGO != null) Destroy(quadGO);
    }

    void UpdateQuadMaterialColor()
    {
        if (quadRenderer != null)
            quadRenderer.enabled = !surfacePatchVisualMode;

        if (quadMaterial == null)
            return;

        Color color = surfacePatchVisualMode ? SurfacePatchQuadColor : DefaultQuadColor;
        if (quadMaterial.HasProperty("_BaseColor"))
            quadMaterial.SetColor("_BaseColor", color);
        if (quadMaterial.HasProperty("_Color"))
            quadMaterial.SetColor("_Color", color);
    }
}
