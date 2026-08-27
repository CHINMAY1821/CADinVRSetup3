using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

public class Setup3SketchTool : MonoBehaviour
{
    enum SketchMode
    {
        Line,
        Circle
    }

    class SketchPoint
    {
        public Vector3 world;
        public Vector3 surfaceWorld;
        public Vector3 normal;
        public int gridX;
        public int gridY;
        public bool surfaceNode;
    }

    class SketchLoop
    {
        public int sequenceId;
        public readonly List<SketchPoint> points = new List<SketchPoint>();
    }

    enum CircleSizeMode
    {
        Radius,
        Diameter
    }

    class SketchCircle
    {
        public int sequenceId;
        public SketchPoint center;
        public SketchPoint rim;
        public string sizeInput = "0";
        public CircleSizeMode sizeMode = CircleSizeMode.Diameter;
    }

    enum ExtrudeTargetKind
    {
        None,
        Loop,
        Circle
    }

    struct ExtrudeTargetRef
    {
        public ExtrudeTargetKind kind;
        public int index;
        public int sequenceId;
    }

    enum NumericInputTarget
    {
        Depth,
        CircleSize
    }

    class SketchPrismMesh
    {
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<int> triangles = new List<int>();
        public int boundaryVertexCount;
    }

    struct LoopBuildPoint
    {
        public Vector2 chart;
        public Vector3 world;
        public Vector3 normal;
    }

    private const int MinClosedLoopPointCount = 3;
    private const float PointMarkerScale = 0.16f;
    private const float HoverMarkerScale = 0.18f;
    private const float LineWidthHintScale = 0.03f;
    private const float MinExtrudeDepthMm = 0.01f;
    private const float TriangulationEpsilon = 1e-6f;
    private const float TriangulationAreaEpsilon = 1e-5f;

    public Setup3DatumPreviewManager previewManager;
    public Setup3DatumPlaneController planeController;

    private Setup3DatumPlaneVisual activePlane;
    private Setup3DatumSurfacePatchGrid activePatchGrid;
    private readonly List<SketchPoint> openLine = new List<SketchPoint>();
    private readonly List<SketchLoop> closedLoops = new List<SketchLoop>();
    private readonly List<SketchCircle> circles = new List<SketchCircle>();
    private readonly List<ExtrudeTargetRef> extrudeTargetScratch = new List<ExtrudeTargetRef>();
    private SketchPoint circleCenter;
    private SketchPoint hoverPoint;
    private SketchMode sketchMode = SketchMode.Line;
    private int nextSketchSequenceId = 1;
    private string extrudeDepthInput = "2";
    private string circleSizeInput = "0";
    private CircleSizeMode circleSizeMode = CircleSizeMode.Diameter;
    private NumericInputTarget activeNumericInput = NumericInputTarget.Depth;
    private int activeCircleSequenceId = -1;
    private string lastExtrudeMessage = "";
    private bool cutMode = false;
    private Material glMat;
    private Rect panelRect;
    private GUIStyle panelStyle, titleStyle, labelStyle, activeButtonStyle, buttonStyle, dangerButtonStyle,
        addButtonStyle, cutButtonStyle, disabledButtonStyle, textFieldStyle, valueStyle, numBtnStyle,
        numBtnSpecial, confirmStyle;
    private bool stylesReady = false;

    public bool IsPointerOverPanel
    {
        get
        {
            Vector2 guiMouse = GetGuiMousePosition();
            return panelRect.Contains(guiMouse);
        }
    }

    public int UndoableItemCount
    {
        get
        {
            return (circleCenter != null ? 1 : 0) + openLine.Count + closedLoops.Count + circles.Count;
        }
    }

    public void SetActivePlane(Setup3DatumPlaneVisual plane)
    {
        activePlane = plane;
        activePatchGrid = plane != null ? plane.GetComponent<Setup3DatumSurfacePatchGrid>() : null;
        ClearSketch();
        hoverPoint = null;
        if (plane == null)
            panelRect = default;
    }

    public static Vector2Int SnapGridIntersection(float localX, float localY, float cellSizeMm)
    {
        float safeCell = Mathf.Max(0.01f, cellSizeMm);
        return new Vector2Int(
            Mathf.RoundToInt(localX / safeCell),
            Mathf.RoundToInt(localY / safeCell));
    }

    public static bool IsClosedLoopCandidate(int existingPointCount, Vector2Int firstNode, Vector2Int candidateNode,
        bool firstIsSurfaceNode, bool candidateIsSurfaceNode)
    {
        return existingPointCount >= MinClosedLoopPointCount &&
               firstNode == candidateNode &&
               firstIsSurfaceNode == candidateIsSurfaceNode;
    }

    public static float ComputeSketchBooleanOverlapEpsilon(float cellSizeMm)
    {
        float cellSize = Mathf.Max(cellSizeMm, 0.0001f);
        return Mathf.Clamp(cellSize * 0.05f, 0.001f, 0.05f);
    }

    public static float ComputeSketchSurfaceMeshOverlapEpsilon(float cellSizeMm)
    {
        float cellSize = Mathf.Max(cellSizeMm, 0.0001f);
        return Mathf.Clamp(cellSize * 0.4f, 0.08f, 1.5f);
    }

    public static float ComputeSketchCutEntryPadding(float cellSizeMm)
    {
        float cellSize = Mathf.Max(cellSizeMm, 0.0001f);
        return Mathf.Clamp(cellSize * 0.25f, 0.05f, 0.25f);
    }

    public static float ResolveCircleEntryPadding(float cellSizeMm, bool isCut, bool surfaceCircle)
    {
        if (isCut)
            return ComputeSketchCutEntryPadding(cellSizeMm);
        return surfaceCircle
            ? ComputeSketchSurfaceMeshOverlapEpsilon(cellSizeMm)
            : ComputeSketchBooleanOverlapEpsilon(cellSizeMm);
    }

    public static void ResolvePlanarSurfacePrismOffsets(float requestedDepthMm, float entryPadMm, bool isCut,
        float minSurfaceOffsetMm, float maxSurfaceOffsetMm, out float baseOffsetMm, out float topOffsetMm)
    {
        float requestedDepth = Mathf.Max(0f, requestedDepthMm);
        float entryPad = Mathf.Max(0f, entryPadMm);

        if (isCut)
        {
            baseOffsetMm = Mathf.Max(minSurfaceOffsetMm, maxSurfaceOffsetMm) + entryPad;
            topOffsetMm = -requestedDepth;
        }
        else
        {
            baseOffsetMm = Mathf.Min(minSurfaceOffsetMm, maxSurfaceOffsetMm) - entryPad;
            topOffsetMm = requestedDepth;
        }
    }

    public static float ComputeCircleRadiusMm(float circleSizeMmInput, bool interpretInputAsDiameter)
    {
        float sizeMm = Mathf.Max(0f, circleSizeMmInput);
        if (sizeMm <= 0f)
            return 0f;

        return interpretInputAsDiameter ? sizeMm * 0.5f : sizeMm;
    }

    public static string DescribeSketchTargetCounts(int loopCount, int circleCount)
    {
        int safeLoopCount = Mathf.Max(0, loopCount);
        int safeCircleCount = Mathf.Max(0, circleCount);
        if (safeLoopCount == 0 && safeCircleCount == 0)
            return "no closed targets";
        if (safeLoopCount > 0 && safeCircleCount > 0)
            return $"{safeLoopCount} loop(s), {safeCircleCount} circle(s)";
        if (safeLoopCount > 0)
            return safeLoopCount == 1 ? "1 loop" : $"{safeLoopCount} loops";
        return safeCircleCount == 1 ? "1 circle" : $"{safeCircleCount} circles";
    }

    public static float SignedClosedLoopArea(IList<Vector2> points)
    {
        if (points == null || points.Count < 3)
            return 0f;

        double area = 0.0;
        for (int i = 0; i < points.Count; i++)
        {
            Vector2 a = points[i];
            Vector2 b = points[(i + 1) % points.Count];
            area += (double)a.x * b.y - (double)b.x * a.y;
        }
        return (float)(area * 0.5);
    }

    public static bool TryTriangulateClosedLoop(IList<Vector2> points, List<int> triangles)
    {
        if (triangles == null)
            return false;

        triangles.Clear();
        if (points == null || points.Count < 3)
            return false;

        float area = SignedClosedLoopArea(points);
        if (Mathf.Abs(area) <= TriangulationAreaEpsilon)
            return false;

        bool ccw = area > 0f;
        var remaining = new List<int>(points.Count);
        for (int i = 0; i < points.Count; i++)
            remaining.Add(i);

        int guard = points.Count * points.Count;
        while (remaining.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int prevIndex = remaining[(i - 1 + remaining.Count) % remaining.Count];
                int currentIndex = remaining[i];
                int nextIndex = remaining[(i + 1) % remaining.Count];

                if (!IsEar(points, remaining, prevIndex, currentIndex, nextIndex, ccw))
                    continue;

                if (ccw)
                {
                    triangles.Add(prevIndex);
                    triangles.Add(currentIndex);
                    triangles.Add(nextIndex);
                }
                else
                {
                    triangles.Add(prevIndex);
                    triangles.Add(nextIndex);
                    triangles.Add(currentIndex);
                }

                remaining.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                triangles.Clear();
                return false;
            }
        }

        if (remaining.Count == 3)
        {
            if (ccw)
            {
                triangles.Add(remaining[0]);
                triangles.Add(remaining[1]);
                triangles.Add(remaining[2]);
            }
            else
            {
                triangles.Add(remaining[0]);
                triangles.Add(remaining[2]);
                triangles.Add(remaining[1]);
            }
        }

        return triangles.Count >= 3;
    }

    static bool IsEar(IList<Vector2> points, List<int> remaining, int prevIndex, int currentIndex, int nextIndex, bool ccw)
    {
        Vector2 prev = points[prevIndex];
        Vector2 current = points[currentIndex];
        Vector2 next = points[nextIndex];
        float cross = Cross2D(current - prev, next - current);
        if (ccw ? cross <= TriangulationEpsilon : cross >= -TriangulationEpsilon)
            return false;

        for (int i = 0; i < remaining.Count; i++)
        {
            int testIndex = remaining[i];
            if (testIndex == prevIndex || testIndex == currentIndex || testIndex == nextIndex)
                continue;

            if (PointInTriangle(points[testIndex], prev, current, next))
                return false;
        }

        return true;
    }

    static float Cross2D(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float c0 = Cross2D(b - a, p - a);
        float c1 = Cross2D(c - b, p - b);
        float c2 = Cross2D(a - c, p - c);
        bool hasNeg = c0 < -TriangulationEpsilon || c1 < -TriangulationEpsilon || c2 < -TriangulationEpsilon;
        bool hasPos = c0 > TriangulationEpsilon || c1 > TriangulationEpsilon || c2 > TriangulationEpsilon;
        if (hasNeg && hasPos)
            return false;

        return Mathf.Abs(c0) > TriangulationEpsilon &&
               Mathf.Abs(c1) > TriangulationEpsilon &&
               Mathf.Abs(c2) > TriangulationEpsilon;
    }

    public static bool CanUseConvexFanTriangulation(IList<Vector2> points)
    {
        return IsConvexOrColinearLoop(points) && !HasSelfIntersection(points);
    }

    static bool IsConvexOrColinearLoop(IList<Vector2> points)
    {
        if (points == null || points.Count < 3)
            return false;

        float area = SignedClosedLoopArea(points);
        if (Mathf.Abs(area) <= TriangulationAreaEpsilon)
            return false;

        bool ccw = area > 0f;
        bool hasCorner = false;
        for (int i = 0; i < points.Count; i++)
        {
            Vector2 prev = points[(i - 1 + points.Count) % points.Count];
            Vector2 current = points[i];
            Vector2 next = points[(i + 1) % points.Count];
            float cross = Cross2D(current - prev, next - current);
            if (Mathf.Abs(cross) <= TriangulationEpsilon)
                continue;

            if (ccw ? cross < -TriangulationEpsilon : cross > TriangulationEpsilon)
                return false;
            hasCorner = true;
        }

        return hasCorner;
    }

    static bool HasSelfIntersection(IList<Vector2> points)
    {
        if (points == null || points.Count < 4)
            return false;

        for (int i = 0; i < points.Count; i++)
        {
            Vector2 a0 = points[i];
            Vector2 a1 = points[(i + 1) % points.Count];
            for (int j = i + 1; j < points.Count; j++)
            {
                if (Mathf.Abs(i - j) <= 1)
                    continue;
                if (i == 0 && j == points.Count - 1)
                    continue;

                Vector2 b0 = points[j];
                Vector2 b1 = points[(j + 1) % points.Count];
                if (SegmentsIntersect(a0, a1, b0, b1))
                    return true;
            }
        }

        return false;
    }

    static bool SegmentsIntersect(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
    {
        float d1 = Cross2D(a1 - a0, b0 - a0);
        float d2 = Cross2D(a1 - a0, b1 - a0);
        float d3 = Cross2D(b1 - b0, a0 - b0);
        float d4 = Cross2D(b1 - b0, a1 - b0);

        if (((d1 > TriangulationEpsilon && d2 < -TriangulationEpsilon) ||
             (d1 < -TriangulationEpsilon && d2 > TriangulationEpsilon)) &&
            ((d3 > TriangulationEpsilon && d4 < -TriangulationEpsilon) ||
             (d3 < -TriangulationEpsilon && d4 > TriangulationEpsilon)))
            return true;

        return (Mathf.Abs(d1) <= TriangulationEpsilon && PointOnSegment(b0, a0, a1)) ||
               (Mathf.Abs(d2) <= TriangulationEpsilon && PointOnSegment(b1, a0, a1)) ||
               (Mathf.Abs(d3) <= TriangulationEpsilon && PointOnSegment(a0, b0, b1)) ||
               (Mathf.Abs(d4) <= TriangulationEpsilon && PointOnSegment(a1, b0, b1));
    }

    static bool PointOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        return p.x >= Mathf.Min(a.x, b.x) - TriangulationEpsilon &&
               p.x <= Mathf.Max(a.x, b.x) + TriangulationEpsilon &&
               p.y >= Mathf.Min(a.y, b.y) - TriangulationEpsilon &&
               p.y <= Mathf.Max(a.y, b.y) + TriangulationEpsilon;
    }

    void Awake()
    {
        glMat = new Material(Shader.Find("Hidden/Internal-Colored"));
        glMat.hideFlags = HideFlags.HideAndDontSave;
        glMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        glMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        glMat.SetInt("_Cull", 0);
        glMat.SetInt("_ZWrite", 0);
    }

    void OnDisable()
    {
        hoverPoint = null;
        circleCenter = null;
        activePlane = null;
        activePatchGrid = null;
    }

    void OnDestroy()
    {
        if (glMat != null)
            Destroy(glMat);
    }

    void Update()
    {
        if (activePlane == null || Mouse.current == null)
            return;

        UpdateHover();

        if (!Mouse.current.leftButton.wasPressedThisFrame)
            return;
        if (Keyboard.current != null && Keyboard.current.leftAltKey.isPressed)
            return;
        if (IsPointerBlockedByGui())
            return;
        if (Camera.main == null)
            return;

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (TryResolveSketchPoint(ray, out SketchPoint point))
            AddSketchPoint(point);
    }

    void UpdateHover()
    {
        hoverPoint = null;
        if (Camera.main == null || Mouse.current == null || IsPointerBlockedByGui())
            return;

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (TryResolveSketchPoint(ray, out SketchPoint point))
            hoverPoint = point;
    }

    bool IsPointerBlockedByGui()
    {
        Vector2 guiMouse = GetGuiMousePosition();
        Rect orientationRect = Setup3DatumPlaneController.CurrentOrientationPanelRect;
        return panelRect.Contains(guiMouse) ||
               (orientationRect.width > 0f && orientationRect.height > 0f && orientationRect.Contains(guiMouse));
    }

    static Vector2 GetGuiMousePosition()
    {
        if (Mouse.current == null)
            return Vector2.zero;
        Vector2 screenMouse = Mouse.current.position.ReadValue();
        return new Vector2(screenMouse.x, Screen.height - screenMouse.y);
    }

    void AddSketchPoint(SketchPoint point)
    {
        if (point == null)
            return;

        if (sketchMode == SketchMode.Circle)
        {
            AddCirclePoint(point);
            return;
        }

        AddLinePoint(point);
    }

    void AddLinePoint(SketchPoint point)
    {
        circleCenter = null;

        if (openLine.Count == 0)
        {
            openLine.Add(point);
            return;
        }

        SketchPoint last = openLine[openLine.Count - 1];
        if (IsSameSketchNode(last, point))
            return;

        SketchPoint first = openLine[0];
        if (IsClosedLoopCandidate(openLine.Count,
                new Vector2Int(first.gridX, first.gridY),
                new Vector2Int(point.gridX, point.gridY),
                first.surfaceNode,
                point.surfaceNode))
        {
            var loop = new SketchLoop();
            loop.sequenceId = nextSketchSequenceId++;
            loop.points.AddRange(openLine);
            closedLoops.Add(loop);
            openLine.Clear();
            Debug.Log($"[Setup3SketchTool] Closed line loop with {loop.points.Count} point(s)");
            return;
        }

        if (IsSameSketchNode(first, point))
            return;

        openLine.Add(point);
    }

    void AddCirclePoint(SketchPoint point)
    {
        openLine.Clear();
        circleCenter = null;

        var circle = new SketchCircle
        {
            sequenceId = nextSketchSequenceId++,
            center = point,
            rim = point,
            sizeMode = CircleSizeMode.Diameter,
            sizeInput = "1"
        };
        
        circles.Add(circle);
        activeCircleSequenceId = circle.sequenceId;
        circleSizeInput = circle.sizeInput;
        circleSizeMode = circle.sizeMode;
        activeNumericInput = NumericInputTarget.CircleSize;
    }

    void SeedCircleSizeInput(SketchCircle circle)
    {
        float radius = ComputeSketchedCircleRadius(circle);
        if (radius <= 1e-4f)
            return;

        float size = circleSizeMode == CircleSizeMode.Diameter ? radius * 2f : radius;
        circleSizeInput = FormatMm(size);
        circle.sizeInput = circleSizeInput;
        circle.sizeMode = circleSizeMode;
    }

    static bool IsSameSketchNode(SketchPoint a, SketchPoint b)
    {
        return a != null &&
               b != null &&
               a.gridX == b.gridX &&
               a.gridY == b.gridY &&
               a.surfaceNode == b.surfaceNode;
    }

    bool TryResolveSketchPoint(Ray ray, out SketchPoint point)
    {
        point = null;
        if (activePlane == null)
            return false;

        if (TryResolveSurfaceNode(ray, out point))
            return true;

        return TryResolvePlaneIntersection(ray, out point);
    }

    bool TryResolveSurfaceNode(Ray ray, out SketchPoint point)
    {
        point = null;
        if (activePatchGrid == null ||
            activePatchGrid.InteractionMode == Setup3DatumSurfacePatchGrid.SurfaceInteractionMode.PlaneOnly ||
            !activePatchGrid.HasValidPatch)
            return false;

        if (!activePatchGrid.TryPickSurfaceCell(ray, out Setup3DatumSurfacePatchGrid.SurfacePatchCell cell,
                out Vector3 hitWorld, out _))
            return false;

        int[,] candidates =
        {
            { cell.ix, cell.iy },
            { cell.ix + 1, cell.iy },
            { cell.ix, cell.iy + 1 },
            { cell.ix + 1, cell.iy + 1 }
        };

        bool found = false;
        int bestX = 0;
        int bestY = 0;
        Vector3 bestWorld = Vector3.zero;
        Vector3 bestSurfaceWorld = Vector3.zero;
        Vector3 bestNormal = Vector3.up;
        float bestDistSq = float.PositiveInfinity;

        for (int i = 0; i < 4; i++)
        {
            int nodeX = candidates[i, 0];
            int nodeY = candidates[i, 1];
            if (!activePatchGrid.TryGetPatchNode(nodeX, nodeY, true, out Vector3 surfaceWorld, out Vector3 normalWorld))
                continue;

            float distSq = (surfaceWorld - hitWorld).sqrMagnitude;
            if (distSq >= bestDistSq)
                continue;

            if (!activePatchGrid.TryGetPatchNode(nodeX, nodeY, false, out Vector3 visualWorld, out _))
                visualWorld = surfaceWorld;

            found = true;
            bestX = nodeX;
            bestY = nodeY;
            bestWorld = visualWorld;
            bestSurfaceWorld = surfaceWorld;
            bestNormal = normalWorld.sqrMagnitude > 1e-6f ? normalWorld.normalized : activePlane.LocalZ;
            bestDistSq = distSq;
        }

        if (!found)
            return false;

        point = new SketchPoint
        {
            world = bestWorld,
            surfaceWorld = bestSurfaceWorld,
            normal = bestNormal,
            gridX = bestX,
            gridY = bestY,
            surfaceNode = true
        };
        return true;
    }

    bool TryResolvePlaneIntersection(Ray ray, out SketchPoint point)
    {
        point = null;
        Plane plane = new Plane(activePlane.LocalZ, activePlane.DisplayOrigin);
        if (!plane.Raycast(ray, out float enter))
            return false;

        Vector3 world = ray.GetPoint(enter);
        Vector3 offset = world - activePlane.DisplayOrigin;
        float localX = Vector3.Dot(offset, activePlane.LocalX);
        float localY = Vector3.Dot(offset, activePlane.LocalY);
        if (Mathf.Abs(localX) > activePlane.HalfSize || Mathf.Abs(localY) > activePlane.HalfSize)
            return false;

        Vector2Int snapped = SnapGridIntersection(localX, localY, activePlane.GridSpacing);
        Vector3 snappedWorld = activePlane.DisplayOrigin
            + activePlane.LocalX * (snapped.x * activePlane.GridSpacing)
            + activePlane.LocalY * (snapped.y * activePlane.GridSpacing);

        point = new SketchPoint
        {
            world = snappedWorld,
            surfaceWorld = snappedWorld,
            normal = activePlane.LocalZ,
            gridX = snapped.x,
            gridY = snapped.y,
            surfaceNode = false
        };
        return true;
    }

    void OnRenderObject()
    {
        if (activePlane == null || glMat == null)
            return;

        glMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        glMat.SetPass(0);
        DrawExtrudeGhost();

        glMat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
        glMat.SetPass(0);
        DrawClosedLoops();
        DrawOpenLine();
        DrawCircles();
        DrawPendingCircle();
        DrawHover();
    }

    void DrawExtrudeGhost()
    {
        if (!TryParseExtrudeDepth(out float requestedDepth))
            return;

        CollectExtrudeTargets(extrudeTargetScratch);
        if (extrudeTargetScratch.Count == 0)
            return;

        Color color = cutMode
            ? new Color(1f, 0.24f, 0.28f, 0.78f)
            : new Color(0.18f, 1f, 0.55f, 0.78f);

        for (int i = 0; i < extrudeTargetScratch.Count; i++)
        {
            ExtrudeTargetRef target = extrudeTargetScratch[i];
            if (target.kind == ExtrudeTargetKind.Circle)
            {
                if (target.index >= 0 && target.index < circles.Count)
                    DrawCircleExtrudeGhost(circles[target.index], requestedDepth, cutMode, color);
                continue;
            }

            if (target.index >= 0 && target.index < closedLoops.Count)
                DrawLoopExtrudeGhost(closedLoops[target.index], requestedDepth, cutMode, color);
        }
    }

    void DrawLoopExtrudeGhost(SketchLoop loop, float requestedDepth, bool isCut, Color color)
    {
        if (loop == null ||
            !LoopUsesOneSurfaceMode(loop) ||
            !TryBuildLoopPrismMesh(loop, requestedDepth, isCut, out SketchPrismMesh mesh, out _))
            return;

        DrawSketchPrismGhost(mesh, color);
    }

    void DrawCircleExtrudeGhost(SketchCircle circle, float requestedDepth, bool isCut, Color color)
    {
        if (!TryBuildCircleExtrudeOperation(circle, requestedDepth, isCut,
                out Setup3DatumOperation op, out _))
            return;

        DrawRoundPrimitiveGhost(op, color);
    }

    void DrawSketchPrismGhost(SketchPrismMesh mesh, Color color)
    {
        if (mesh == null || mesh.vertices.Count < 6 || mesh.triangles.Count < 3 || mesh.boundaryVertexCount < 3)
            return;

        Color fill = new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a * 0.16f));
        Color edge = new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a * 0.92f));

        GL.Begin(GL.TRIANGLES);
        GL.Color(fill);
        for (int i = 0; i + 2 < mesh.triangles.Count; i += 3)
        {
            GL.Vertex(mesh.vertices[mesh.triangles[i]]);
            GL.Vertex(mesh.vertices[mesh.triangles[i + 1]]);
            GL.Vertex(mesh.vertices[mesh.triangles[i + 2]]);
        }
        GL.End();

        int count = mesh.boundaryVertexCount;
        int topOffset = count;
        if (topOffset + count > mesh.vertices.Count)
            return;

        GL.Begin(GL.LINES);
        GL.Color(edge);
        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;
            GL.Vertex(mesh.vertices[i]); GL.Vertex(mesh.vertices[j]);
            GL.Vertex(mesh.vertices[topOffset + i]); GL.Vertex(mesh.vertices[topOffset + j]);
            GL.Vertex(mesh.vertices[i]); GL.Vertex(mesh.vertices[topOffset + i]);
        }
        GL.End();
    }

    void DrawRoundPrimitiveGhost(Setup3DatumOperation op, Color color)
    {
        Vector3 normal = op.normalAxis.sqrMagnitude > 1e-8f ? op.normalAxis.normalized : Vector3.up;
        Vector3 localX = op.localX.sqrMagnitude > 1e-8f ? op.localX.normalized : Vector3.right;
        Vector3 localY = Vector3.Cross(normal, localX).normalized;
        if (localY.sqrMagnitude <= 1e-8f)
        {
            Vector3 fallback = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) < 0.95f ? Vector3.up : Vector3.right;
            localX = Vector3.Cross(fallback, normal).normalized;
            localY = Vector3.Cross(normal, localX).normalized;
        }

        float radius = Mathf.Max(op.roundRadius, 0.01f);
        float depth = Mathf.Max(op.depth, 0.02f);
        Vector3 baseCenter = op.origin;
        Vector3 topCenter = op.origin + normal * depth;

        float targetEdge = Mathf.Max(ResolveCellSize() * 0.35f, 0.05f);
        int fillSegments = Mathf.Clamp(Mathf.CeilToInt((Mathf.PI * 2f * radius) / targetEdge), 32, 96);
        int lineSegments = Mathf.Clamp(fillSegments, 24, 48);
        int verticalCount = 12;

        Color capFill = new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a * 0.16f));
        Color sideFill = new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a * 0.10f));
        Color edge = new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a * 0.92f));

        GL.Begin(GL.TRIANGLES);
        GL.Color(capFill);
        for (int i = 0; i < fillSegments; i++)
        {
            float a0 = i * Mathf.PI * 2f / fillSegments;
            float a1 = (i + 1) * Mathf.PI * 2f / fillSegments;
            Vector3 base0 = baseCenter + (localX * Mathf.Cos(a0) + localY * Mathf.Sin(a0)) * radius;
            Vector3 base1 = baseCenter + (localX * Mathf.Cos(a1) + localY * Mathf.Sin(a1)) * radius;
            Vector3 top0 = topCenter + (localX * Mathf.Cos(a0) + localY * Mathf.Sin(a0)) * radius;
            Vector3 top1 = topCenter + (localX * Mathf.Cos(a1) + localY * Mathf.Sin(a1)) * radius;

            GL.Vertex(topCenter); GL.Vertex(top0); GL.Vertex(top1);
            GL.Color(sideFill);
            GL.Vertex(base0); GL.Vertex(top1); GL.Vertex(top0);
            GL.Vertex(base0); GL.Vertex(base1); GL.Vertex(top1);
            GL.Color(capFill);
        }
        GL.End();

        GL.Begin(GL.LINES);
        GL.Color(edge);
        for (int i = 0; i < lineSegments; i++)
        {
            float a0 = i * Mathf.PI * 2f / lineSegments;
            float a1 = (i + 1) * Mathf.PI * 2f / lineSegments;
            Vector3 base0 = baseCenter + (localX * Mathf.Cos(a0) + localY * Mathf.Sin(a0)) * radius;
            Vector3 base1 = baseCenter + (localX * Mathf.Cos(a1) + localY * Mathf.Sin(a1)) * radius;
            Vector3 top0 = topCenter + (localX * Mathf.Cos(a0) + localY * Mathf.Sin(a0)) * radius;
            Vector3 top1 = topCenter + (localX * Mathf.Cos(a1) + localY * Mathf.Sin(a1)) * radius;

            GL.Vertex(base0); GL.Vertex(base1);
            GL.Vertex(top0); GL.Vertex(top1);
        }

        for (int i = 0; i < verticalCount; i++)
        {
            float angle = i * Mathf.PI * 2f / verticalCount;
            Vector3 baseEdge = baseCenter + (localX * Mathf.Cos(angle) + localY * Mathf.Sin(angle)) * radius;
            Vector3 topEdge = topCenter + (localX * Mathf.Cos(angle) + localY * Mathf.Sin(angle)) * radius;
            GL.Vertex(baseEdge); GL.Vertex(topEdge);
        }
        GL.End();
    }

    void DrawClosedLoops()
    {
        for (int i = 0; i < closedLoops.Count; i++)
        {
            List<SketchPoint> points = closedLoops[i].points;
            if (points.Count < MinClosedLoopPointCount)
                continue;

            for (int p = 1; p < points.Count; p++)
                DrawSketchLine(points[p - 1], points[p], new Color(0.18f, 1f, 0.95f, 0.95f));
            DrawSketchLine(points[points.Count - 1], points[0], new Color(0.18f, 1f, 0.95f, 0.95f));

            for (int p = 0; p < points.Count; p++)
                DrawPointMarker(points[p], new Color(0.18f, 1f, 0.95f, 0.90f), PointMarkerScale);
        }
    }

    void DrawOpenLine()
    {
        for (int i = 1; i < openLine.Count; i++)
            DrawSketchLine(openLine[i - 1], openLine[i], new Color(0.18f, 1f, 0.95f, 0.95f));

        for (int i = 0; i < openLine.Count; i++)
            DrawPointMarker(openLine[i], new Color(0.18f, 1f, 0.95f, 0.90f), PointMarkerScale);

        if (openLine.Count > 0 && hoverPoint != null && sketchMode == SketchMode.Line)
            DrawSketchLine(openLine[openLine.Count - 1], hoverPoint, new Color(1f, 0.92f, 0.16f, 0.70f));
    }

    void DrawCircles()
    {
        for (int i = 0; i < circles.Count; i++)
            DrawCircle(circles[i], new Color(0.18f, 1f, 0.95f, 0.95f));
    }

    void DrawPendingCircle()
    {
        if (circleCenter == null)
            return;

        DrawPointMarker(circleCenter, new Color(1f, 0.92f, 0.16f, 1f), PointMarkerScale);
        if (hoverPoint != null)
            DrawCircle(new SketchCircle { center = circleCenter, rim = hoverPoint }, new Color(1f, 0.92f, 0.16f, 0.70f));
    }

    void DrawHover()
    {
        if (hoverPoint == null)
            return;
        DrawPointMarker(hoverPoint, new Color(1f, 1f, 1f, 0.92f), HoverMarkerScale);
    }

    void DrawSketchLine(SketchPoint a, SketchPoint b, Color color)
    {
        if (a == null || b == null)
            return;

        if (TryDrawSurfaceGridPath(a, b, color))
            return;

        DrawWorldLine(a.world, b.world, color);
    }

    bool TryDrawSurfaceGridPath(SketchPoint a, SketchPoint b, Color color)
    {
        if (activePatchGrid == null || !a.surfaceNode || !b.surfaceNode)
            return false;

        float dx = b.gridX - a.gridX;
        float dy = b.gridY - a.gridY;
        int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) * 4f), 1, 160);

        if (!activePatchGrid.TryGetPatchChartPoint(a.gridX, a.gridY, false, out Vector3 prev, out _))
            return false;

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float gridX = Mathf.Lerp(a.gridX, b.gridX, t);
            float gridY = Mathf.Lerp(a.gridY, b.gridY, t);
            if (!activePatchGrid.TryGetPatchChartPoint(gridX, gridY, false, out Vector3 next, out _))
                return false;

            DrawWorldLine(prev, next, color);
            prev = next;
        }

        return true;
    }

    void DrawWorldLine(Vector3 a, Vector3 b, Color color)
    {
        GL.Begin(GL.LINES);
        GL.Color(color);
        GL.Vertex(a);
        GL.Vertex(b);

        Vector3 delta = b - a;
        if (delta.sqrMagnitude > 1e-8f)
        {
            Vector3 side = Vector3.Cross(delta.normalized, activePlane.LocalZ);
            if (side.sqrMagnitude > 1e-8f)
            {
                side.Normalize();
                side *= Mathf.Max(0.01f, activePlane.GridSpacing * LineWidthHintScale);
                GL.Vertex(a + side);
                GL.Vertex(b + side);
            }
        }
        GL.End();
    }

    void DrawPointMarker(SketchPoint point, Color color, float scale)
    {
        if (point == null)
            return;

        float s = Mathf.Max(0.04f, activePlane.GridSpacing * scale);
        Vector3 x = activePlane.LocalX * s;
        Vector3 y = activePlane.LocalY * s;
        Vector3 n = point.normal.sqrMagnitude > 1e-6f ? point.normal.normalized * s : activePlane.LocalZ * s;

        GL.Begin(GL.LINES);
        GL.Color(color);
        GL.Vertex(point.world - x); GL.Vertex(point.world + x);
        GL.Vertex(point.world - y); GL.Vertex(point.world + y);
        GL.Vertex(point.world); GL.Vertex(point.world + n);
        GL.End();
    }

    void DrawCircle(SketchCircle circle, Color color)
    {
        if (circle.center == null || circle.rim == null)
            return;

        float radius = ResolveDisplayCircleRadius(circle);
        if (radius <= 1e-4f)
            return;

        const int segments = 96;
        Vector3 centerWorld = ResolveOperationWorld(circle.center);
        GL.Begin(GL.LINES);
        GL.Color(color);
        Vector3 prev = ResolveCirclePoint(centerWorld, radius, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float t = (Mathf.PI * 2f * i) / segments;
            Vector3 next = ResolveCirclePoint(centerWorld, radius, t);
            GL.Vertex(prev);
            GL.Vertex(next);
            prev = next;
        }
        GL.End();

        DrawPointMarker(circle.center, color, PointMarkerScale);
    }

    float ResolveDisplayCircleRadius(SketchCircle circle)
    {
        if (IsLatestCircle(circle) && TryResolveCircleRadius(out float inputRadius))
            return inputRadius;

        return ComputeSketchedCircleRadius(circle);
    }

    float ComputeSketchedCircleRadius(SketchCircle circle)
    {
        if (circle.center == null || circle.rim == null)
            return 0f;

        Vector3 center = ResolveOperationWorld(circle.center);
        Vector3 rim = ResolveOperationWorld(circle.rim);
        Vector3 normal = ResolveStableNormal(circle.center, circle.rim);
        return Vector3.ProjectOnPlane(rim - center, normal).magnitude;
    }

    bool IsLatestCircle(SketchCircle circle)
    {
        return TryResolveLatestExtrudeTarget(out ExtrudeTargetKind kind, out int index) &&
               kind == ExtrudeTargetKind.Circle &&
               index >= 0 &&
               index < circles.Count &&
               circles[index].sequenceId == circle.sequenceId;
    }

    Vector3 ResolveCirclePoint(Vector3 center, float radius, float angle)
    {
        return center
            + activePlane.LocalX * (Mathf.Cos(angle) * radius)
            + activePlane.LocalY * (Mathf.Sin(angle) * radius);
    }

    void ConfirmExtrude(bool isCut)
    {
        var operations = new List<Setup3DatumOperation>();
        if (!TryBuildExtrudeOperations(isCut, operations, out string error))
        {
            lastExtrudeMessage = error;
            Debug.LogWarning($"[Setup3SketchTool] Extrude skipped — {error}");
            return;
        }

        previewManager.AddOperations(operations);
        string action = isCut ? "Cut" : "Add";
        lastExtrudeMessage = $"{action} extrude sent to rebuild ({operations.Count} target(s))";
        Debug.Log($"[Setup3SketchTool] {(isCut ? "CUT" : "ADD")} extrude queued for {operations.Count} target(s)");
    }

    bool TryBuildExtrudeOperations(bool isCut, List<Setup3DatumOperation> operations, out string error)
    {
        error = null;
        if (operations == null)
        {
            error = "Operation list missing";
            return false;
        }
        operations.Clear();

        if (activePlane == null)
        {
            error = "Create datum plane first";
            return false;
        }

        if (previewManager == null)
        {
            error = "Setup 3 preview manager missing";
            return false;
        }

        NormalizeActiveNumericTarget();

        if (!TryParseExtrudeDepth(out float requestedDepth))
        {
            error = "Depth must be positive";
            return false;
        }

        var targets = new List<ExtrudeTargetRef>();
        CollectExtrudeTargets(targets);
        if (targets.Count == 0)
        {
            error = "Close a line loop or create a circle first";
            return false;
        }

        int failCount = 0;
        string lastError = "";

        for (int i = 0; i < targets.Count; i++)
        {
            ExtrudeTargetRef target = targets[i];
            bool ok;
            Setup3DatumOperation op;
            string targetError;
            string label = DescribeTarget(target.kind, target.index);
            if (target.kind == ExtrudeTargetKind.Circle)
                ok = TryBuildCircleExtrudeOperation(circles[target.index], requestedDepth, isCut, out op, out targetError);
            else
                ok = TryBuildLoopExtrudeOperation(closedLoops[target.index], requestedDepth, isCut, out op, out targetError);

            if (!ok)
            {
                lastError = $"{label}: {targetError}";
                Debug.LogWarning($"[Setup3SketchTool] Extrude skipped — {lastError}");
                failCount++;
                continue;
            }

            operations.Add(op);
        }

        if (operations.Count == 0 && failCount > 0)
        {
            error = lastError;
            return false;
        }

        if (failCount > 0)
        {
            error = $"{failCount} target(s) skipped";
        }

        return operations.Count > 0;
    }

    void CollectExtrudeTargets(List<ExtrudeTargetRef> targets)
    {
        targets.Clear();
        if (TryResolveLatestExtrudeTarget(out ExtrudeTargetKind kind, out int index))
        {
            int sequenceId = -1;
            if (kind == ExtrudeTargetKind.Loop && index >= 0 && index < closedLoops.Count)
                sequenceId = closedLoops[index].sequenceId;
            else if (kind == ExtrudeTargetKind.Circle && index >= 0 && index < circles.Count)
                sequenceId = circles[index].sequenceId;

            targets.Add(new ExtrudeTargetRef
            {
                kind = kind,
                index = index,
                sequenceId = sequenceId
            });
        }
    }

    static string DescribeTarget(ExtrudeTargetKind kind, int index)
    {
        return kind == ExtrudeTargetKind.Circle
            ? $"Circle {index + 1}"
            : $"Loop {index + 1}";
    }

    bool TryResolveLatestExtrudeTarget(out ExtrudeTargetKind kind, out int index)
    {
        kind = ExtrudeTargetKind.None;
        index = -1;
        int bestSequence = -1;

        for (int i = 0; i < closedLoops.Count; i++)
        {
            if (closedLoops[i].sequenceId > bestSequence)
            {
                bestSequence = closedLoops[i].sequenceId;
                kind = ExtrudeTargetKind.Loop;
                index = i;
            }
        }

        for (int i = 0; i < circles.Count; i++)
        {
            if (circles[i].sequenceId > bestSequence)
            {
                bestSequence = circles[i].sequenceId;
                kind = ExtrudeTargetKind.Circle;
                index = i;
            }
        }

        return kind != ExtrudeTargetKind.None && index >= 0;
    }

    bool TryBuildCircleExtrudeOperation(SketchCircle circle, float requestedDepth, bool isCut,
        out Setup3DatumOperation op, out string error)
    {
        op = default;
        error = null;

        if (circle.center == null || circle.rim == null)
        {
            error = "Circle is incomplete";
            return false;
        }

        bool centerOnSurface = circle.center.surfaceNode;
        bool rimOnSurface = circle.rim.surfaceNode;
        if (centerOnSurface != rimOnSurface)
        {
            error = "Circle mixes plane and surface nodes";
            return false;
        }

        bool surfaceCircle = centerOnSurface;
        Vector3 center = ResolveOperationWorld(circle.center);
        Vector3 normal = surfaceCircle
            ? ResolveStableNormal(circle.center)
            : ResolveStableNormal(circle.center, circle.rim);
        Vector3 direction = isCut ? -normal : normal;
        Vector3 localX = ResolveCircleLocalX(circle, center, normal);
        if (!TryResolveCircleRadius(circle, out float radius))
        {
            error = "Circle radius/diameter must be positive";
            return false;
        }

        if (localX.sqrMagnitude <= 1e-8f && activePlane != null)
            localX = Vector3.ProjectOnPlane(activePlane.LocalX, direction);
        if (localX.sqrMagnitude <= 1e-8f)
            localX = Vector3.ProjectOnPlane(Vector3.right, direction);
        if (localX.sqrMagnitude <= 1e-8f)
            localX = Vector3.ProjectOnPlane(Vector3.up, direction);
        if (localX.sqrMagnitude <= 1e-8f)
        {
            error = "Circle orientation is invalid";
            return false;
        }
        localX.Normalize();

        float cellSize = ResolveCellSize();
        float entryPad = ResolveCircleEntryPadding(cellSize, isCut, surfaceCircle);
        float booleanDepth = requestedDepth + entryPad;
        Vector3 origin = center - direction * entryPad;
        Vector3 referenceTopCenter = center + direction * requestedDepth;

        if (surfaceCircle)
        {
            if (!TryResolveSurfaceCircleOffsetRange(circle, center, radius, normal,
                    out float minSurfaceOffset, out float maxSurfaceOffset, out error))
                return false;

            ResolvePlanarSurfacePrismOffsets(requestedDepth, entryPad, isCut,
                minSurfaceOffset, maxSurfaceOffset, out float baseOffset, out float topOffset);
            booleanDepth = isCut ? baseOffset - topOffset : topOffset - baseOffset;
            if (booleanDepth <= MinExtrudeDepthMm)
            {
                error = "Circle extrude depth is invalid";
                return false;
            }

            origin = center + normal * baseOffset;
            referenceTopCenter = center + normal * topOffset;
        }

        op = new Setup3DatumOperation
        {
            isCut = isCut,
            useRoundPrimitive = true,
            origin = origin,
            normalAxis = direction,
            localX = localX,
            roundRadius = radius,
            depth = booleanDepth,
            hasRoundReference = true,
            roundReferenceSurfaceCenter = center,
            roundReferenceTopCenter = referenceTopCenter,
            roundReferenceRequestedDepth = requestedDepth
        };
        return true;
    }

    Vector3 ResolveCircleLocalX(SketchCircle circle, Vector3 center, Vector3 normal)
    {
        Vector3 localX = Vector3.zero;
        if (circle != null && circle.center != null && circle.rim != null)
        {
            if (circle.center.surfaceNode && circle.rim.surfaceNode && activePlane != null)
            {
                Vector2 centerChart = ResolveSketchChartPoint(circle.center);
                Vector2 rimChart = ResolveSketchChartPoint(circle.rim);
                Vector2 chartDelta = rimChart - centerChart;
                localX = activePlane.LocalX * chartDelta.x + activePlane.LocalY * chartDelta.y;
            }
            else
            {
                Vector3 rim = ResolveOperationWorld(circle.rim);
                localX = rim - center;
            }
        }

        return Vector3.ProjectOnPlane(localX, normal);
    }

    bool TryResolveSurfaceCircleOffsetRange(SketchCircle circle, Vector3 center, float radius, Vector3 normal,
        out float minSurfaceOffset, out float maxSurfaceOffset, out string error)
    {
        minSurfaceOffset = float.PositiveInfinity;
        maxSurfaceOffset = float.NegativeInfinity;
        error = null;

        if (activePlane == null || activePatchGrid == null || !activePatchGrid.HasValidPatch)
        {
            error = "Surface circle patch is missing";
            return false;
        }

        float cellSize = ResolveCellSize();
        float radiusInCells = radius / Mathf.Max(cellSize, 0.0001f);
        int minCellX = Mathf.FloorToInt(circle.center.gridX - radiusInCells - 1f);
        int maxCellX = Mathf.CeilToInt(circle.center.gridX + radiusInCells + 1f);
        int minCellY = Mathf.FloorToInt(circle.center.gridY - radiusInCells - 1f);
        int maxCellY = Mathf.CeilToInt(circle.center.gridY + radiusInCells + 1f);
        activePatchGrid.EnsureCellRangeCoverage(minCellX, maxCellX, minCellY, maxCellY);

        AccumulateSurfaceCircleOffset(center, center, normal, ref minSurfaceOffset, ref maxSurfaceOffset);

        int segments = Mathf.Clamp(
            Mathf.CeilToInt((Mathf.PI * 2f * radius) / Mathf.Max(cellSize * 0.5f, 0.05f)),
            24,
            128);
        float[] sampleRadii = { radius * 0.5f, radius };
        for (int ring = 0; ring < sampleRadii.Length; ring++)
        {
            float sampleRadius = sampleRadii[ring];
            if (sampleRadius <= 1e-4f)
                continue;

            int ringSegments = ring == sampleRadii.Length - 1 ? segments : Mathf.Max(12, segments / 2);
            for (int i = 0; i < ringSegments; i++)
            {
                float angle = (Mathf.PI * 2f * i) / ringSegments;
                float dx = Mathf.Cos(angle) * sampleRadius;
                float dy = Mathf.Sin(angle) * sampleRadius;
                float gridX = circle.center.gridX + dx / cellSize;
                float gridY = circle.center.gridY + dy / cellSize;
                if (!activePatchGrid.TryGetPatchChartPoint(gridX, gridY, true,
                        out Vector3 surfaceWorld, out _))
                {
                    error = "Surface circle footprint no longer resolves on the patch";
                    return false;
                }

                Vector3 planePoint = center + activePlane.LocalX * dx + activePlane.LocalY * dy;
                AccumulateSurfaceCircleOffset(surfaceWorld, planePoint, normal,
                    ref minSurfaceOffset, ref maxSurfaceOffset);
            }
        }

        if (float.IsInfinity(minSurfaceOffset) || float.IsInfinity(maxSurfaceOffset))
        {
            error = "Surface circle footprint has no valid samples";
            return false;
        }

        return true;
    }

    static void AccumulateSurfaceCircleOffset(Vector3 surfaceWorld, Vector3 planePoint, Vector3 normal,
        ref float minSurfaceOffset, ref float maxSurfaceOffset)
    {
        float surfaceOffset = Vector3.Dot(surfaceWorld - planePoint, normal);
        minSurfaceOffset = Mathf.Min(minSurfaceOffset, surfaceOffset);
        maxSurfaceOffset = Mathf.Max(maxSurfaceOffset, surfaceOffset);
    }

    bool TryBuildLoopExtrudeOperation(SketchLoop loop, float requestedDepth, bool isCut,
        out Setup3DatumOperation op, out string error)
    {
        op = default;
        error = null;

        if (loop == null || loop.points.Count < MinClosedLoopPointCount)
        {
            error = "Closed loop needs at least three points";
            return false;
        }

        if (!LoopUsesOneSurfaceMode(loop))
        {
            error = "Loop mixes plane and surface nodes";
            return false;
        }

        if (TryBuildLoopPolyPrismOperation(loop, requestedDepth, isCut, out op, out error))
            return true;

        if (!TryBuildLoopPrismMesh(loop, requestedDepth, isCut, out SketchPrismMesh mesh, out error))
            return false;
        string meshPath = CreateSketchMeshOperandPath();
        WriteSketchMeshObj(mesh, meshPath);

        op = new Setup3DatumOperation
        {
            isCut = isCut,
            useMeshOperand = true,
            meshOperandPath = meshPath
        };
        return true;
    }

    bool TryBuildLoopPolyPrismOperation(SketchLoop loop, float requestedDepth, bool isCut,
        out Setup3DatumOperation op, out string error)
    {
        op = default;
        error = null;

        var sampledBoundary = new List<LoopBuildPoint>();
        if (!TryBuildLoopBoundary(loop, sampledBoundary, out error))
            return false;

        var originalChart = new List<Vector2>(loop.points.Count);
        for (int i = 0; i < loop.points.Count; i++)
            originalChart.Add(ResolveSketchChartPoint(loop.points[i]));
        if (!TryTriangulateClosedLoop(originalChart, new List<int>()))
        {
            if (!CanUseConvexFanTriangulation(originalChart))
            {
                error = "Closed loop is self-crossing or degenerate";
                return false;
            }
        }

        bool surfaceLoop = loop.points[0].surfaceNode;
        Vector3 normal = surfaceLoop ? ResolveSketchPlaneNormal(sampledBoundary) : ResolveStableNormal(loop.points);
        float cellSize = ResolveCellSize();
        float entryPad = ResolveLoopMeshEntryPad(cellSize, isCut, surfaceLoop);
        Vector3 direction = isCut ? -normal : normal;
        var basePoints = new List<Vector3>(loop.points.Count);
        float depth;

        if (surfaceLoop)
        {
            ResolveSurfaceLoopOffsetRange(sampledBoundary, normal, out float minSurfaceOffset, out float maxSurfaceOffset);
            ResolvePlanarSurfacePrismOffsets(requestedDepth, entryPad, isCut,
                minSurfaceOffset, maxSurfaceOffset, out float baseOffset, out float topOffset);
            depth = isCut ? baseOffset - topOffset : topOffset - baseOffset;
            for (int i = 0; i < originalChart.Count; i++)
                basePoints.Add(ResolveSketchPlanePoint(originalChart[i]) + normal * baseOffset);
        }
        else
        {
            depth = ResolvePlanarLoopMeshTopTravel(requestedDepth, entryPad);
            for (int i = 0; i < loop.points.Count; i++)
            {
                Vector3 p = ResolveOperationWorld(loop.points[i]);
                basePoints.Add(p - direction * entryPad);
            }
        }

        if (depth <= MinExtrudeDepthMm || basePoints.Count < MinClosedLoopPointCount)
        {
            error = "Closed loop extrude depth is invalid";
            return false;
        }

        op = new Setup3DatumOperation
        {
            isCut = isCut,
            usePolyPrism = true,
            normalAxis = direction,
            depth = depth,
            polygonPoints = basePoints
        };
        return true;
    }

    bool TryBuildLoopPrismMesh(SketchLoop loop, float requestedDepth, bool isCut,
        out SketchPrismMesh mesh, out string error)
    {
        mesh = new SketchPrismMesh();
        error = null;

        var boundary = new List<LoopBuildPoint>();
        if (!TryBuildLoopBoundary(loop, boundary, out error))
            return false;
        mesh.boundaryVertexCount = boundary.Count;

        var loopChart = new List<Vector2>(boundary.Count);
        var capTriangles = new List<int>();
        for (int i = 0; i < boundary.Count; i++)
            loopChart.Add(boundary[i].chart);

        bool useFanCaps = false;
        if (!TryTriangulateClosedLoop(loopChart, capTriangles))
        {
            if (!CanUseConvexFanTriangulation(loopChart))
            {
                error = "Closed loop is self-crossing or degenerate";
                return false;
            }
            useFanCaps = true;
        }

        bool surfaceLoop = loop.points[0].surfaceNode;
        Vector3 normal = surfaceLoop ? ResolveSketchPlaneNormal(boundary) : ResolveStableNormal(boundary);
        Vector3 direction = isCut ? -normal : normal;
        float cellSize = ResolveCellSize();
        float entryPad = ResolveLoopMeshEntryPad(cellSize, isCut, surfaceLoop);

        if (surfaceLoop)
        {
            AddPlanarSurfaceLoopPrismRings(mesh, boundary, normal, requestedDepth, entryPad, isCut);
        }
        else
        {
            float topTravel = ResolvePlanarLoopMeshTopTravel(requestedDepth, entryPad);
            for (int i = 0; i < boundary.Count; i++)
            {
                Vector3 baseExportNormal = isCut ? -normal : normal;
                mesh.vertices.Add(boundary[i].world - baseExportNormal * entryPad);
            }

            for (int i = 0; i < boundary.Count; i++)
                mesh.vertices.Add(boundary[i].world + direction * topTravel);
        }

        int topOffset = boundary.Count;

        if (useFanCaps)
        {
            AddFanCaps(mesh, boundary.Count, topOffset, direction);
        }
        else
        {
            for (int i = 0; i + 2 < capTriangles.Count; i += 3)
            {
                int a = capTriangles[i];
                int b = capTriangles[i + 1];
                int c = capTriangles[i + 2];
                AddTriangleOriented(mesh, a, b, c, -direction);
                AddTriangleOriented(mesh, topOffset + a, topOffset + b, topOffset + c, direction);
            }
        }

        Vector3 prismCenter = Vector3.zero;
        for (int i = 0; i < mesh.vertices.Count; i++)
            prismCenter += mesh.vertices[i];
        prismCenter /= Mathf.Max(1, mesh.vertices.Count);

        int count = boundary.Count;
        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;
            int b0 = i;
            int b1 = j;
            int t0 = topOffset + i;
            int t1 = topOffset + j;
            Vector3 faceCenter = (mesh.vertices[b0] + mesh.vertices[b1] + mesh.vertices[t0] + mesh.vertices[t1]) * 0.25f;
            Vector3 outward = faceCenter - prismCenter;
            if (outward.sqrMagnitude <= 1e-8f)
                outward = Vector3.Cross(direction, mesh.vertices[b1] - mesh.vertices[b0]);
            AddTriangleOriented(mesh, b0, b1, t1, outward);
            AddTriangleOriented(mesh, b0, t1, t0, outward);
        }

        return mesh.vertices.Count >= 6 && mesh.triangles.Count >= 12;
    }

    void AddPlanarSurfaceLoopPrismRings(SketchPrismMesh mesh, IList<LoopBuildPoint> boundary,
        Vector3 normal, float requestedDepth, float entryPad, bool isCut)
    {
        ResolveSurfaceLoopOffsetRange(boundary, normal, out float minSurfaceOffset, out float maxSurfaceOffset);
        var planePoints = new List<Vector3>(boundary.Count);
        for (int i = 0; i < boundary.Count; i++)
            planePoints.Add(ResolveSketchPlanePoint(boundary[i].chart));

        ResolvePlanarSurfacePrismOffsets(requestedDepth, entryPad, isCut,
            minSurfaceOffset, maxSurfaceOffset, out float baseOffset, out float topOffset);

        for (int i = 0; i < planePoints.Count; i++)
            mesh.vertices.Add(planePoints[i] + normal * baseOffset);
        for (int i = 0; i < planePoints.Count; i++)
            mesh.vertices.Add(planePoints[i] + normal * topOffset);
    }

    void ResolveSurfaceLoopOffsetRange(IList<LoopBuildPoint> boundary, Vector3 normal,
        out float minSurfaceOffset, out float maxSurfaceOffset)
    {
        minSurfaceOffset = float.PositiveInfinity;
        maxSurfaceOffset = float.NegativeInfinity;
        if (boundary != null)
        {
            for (int i = 0; i < boundary.Count; i++)
            {
                Vector3 planePoint = ResolveSketchPlanePoint(boundary[i].chart);
                float surfaceOffset = Vector3.Dot(boundary[i].world - planePoint, normal);
                minSurfaceOffset = Mathf.Min(minSurfaceOffset, surfaceOffset);
                maxSurfaceOffset = Mathf.Max(maxSurfaceOffset, surfaceOffset);
            }
        }

        if (float.IsInfinity(minSurfaceOffset) || float.IsInfinity(maxSurfaceOffset))
        {
            minSurfaceOffset = 0f;
            maxSurfaceOffset = 0f;
        }
    }

    static float ResolveLoopMeshEntryPad(float cellSizeMm, bool isCut, bool surfaceLoop)
    {
        if (isCut)
            return ComputeSketchCutEntryPadding(cellSizeMm);
        return surfaceLoop
            ? ComputeSketchSurfaceMeshOverlapEpsilon(cellSizeMm)
            : ComputeSketchBooleanOverlapEpsilon(cellSizeMm);
    }

    static float ResolvePlanarLoopMeshTopTravel(float requestedDepthMm, float entryPadMm)
    {
        float requestedDepth = Mathf.Max(0f, requestedDepthMm);
        float entryPad = Mathf.Max(0f, entryPadMm);
        return requestedDepth + entryPad;
    }

    void AddFanCaps(SketchPrismMesh mesh, int count, int topOffset, Vector3 direction)
    {
        Vector3 bottomCenter = Vector3.zero;
        Vector3 topCenter = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            bottomCenter += mesh.vertices[i];
            topCenter += mesh.vertices[topOffset + i];
        }
        bottomCenter /= Mathf.Max(1, count);
        topCenter /= Mathf.Max(1, count);

        int bottomCenterIndex = mesh.vertices.Count;
        mesh.vertices.Add(bottomCenter);
        int topCenterIndex = mesh.vertices.Count;
        mesh.vertices.Add(topCenter);

        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;
            AddTriangleOriented(mesh, bottomCenterIndex, i, j, -direction);
            AddTriangleOriented(mesh, topCenterIndex, topOffset + i, topOffset + j, direction);
        }
    }

    bool TryBuildLoopBoundary(SketchLoop loop, List<LoopBuildPoint> boundary, out string error)
    {
        boundary.Clear();
        error = null;

        if (loop == null || loop.points.Count < MinClosedLoopPointCount)
        {
            error = "Closed loop needs at least three points";
            return false;
        }

        bool surfaceLoop = loop.points[0].surfaceNode;
        if (!surfaceLoop)
        {
            for (int i = 0; i < loop.points.Count; i++)
                AddBoundaryPointIfDistinct(boundary, BuildBoundaryPoint(loop.points[i]));
            if (boundary.Count < MinClosedLoopPointCount)
            {
                error = "Closed loop needs at least three distinct points";
                return false;
            }
            return true;
        }

        if (activePatchGrid == null)
        {
            error = "Surface loop patch is missing";
            return false;
        }

        int minCellX = int.MaxValue;
        int maxCellX = int.MinValue;
        int minCellY = int.MaxValue;
        int maxCellY = int.MinValue;
        for (int i = 0; i < loop.points.Count; i++)
        {
            minCellX = Mathf.Min(minCellX, loop.points[i].gridX);
            maxCellX = Mathf.Max(maxCellX, loop.points[i].gridX);
            minCellY = Mathf.Min(minCellY, loop.points[i].gridY);
            maxCellY = Mathf.Max(maxCellY, loop.points[i].gridY);
        }
        activePatchGrid.EnsureCellRangeCoverage(minCellX - 1, maxCellX + 1, minCellY - 1, maxCellY + 1);

        float cellSize = ResolveCellSize();
        for (int i = 0; i < loop.points.Count; i++)
        {
            SketchPoint a = loop.points[i];
            SketchPoint b = loop.points[(i + 1) % loop.points.Count];
            if (a == null || b == null)
            {
                error = "Surface loop contains an empty point";
                return false;
            }

            if (i == 0)
                AddBoundaryPointIfDistinct(boundary, BuildBoundaryPoint(a));

            float dx = b.gridX - a.gridX;
            float dy = b.gridY - a.gridY;
            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) * 4f), 1, 160);
            for (int step = 1; step <= steps; step++)
            {
                float t = step / (float)steps;
                float gridX = Mathf.Lerp(a.gridX, b.gridX, t);
                float gridY = Mathf.Lerp(a.gridY, b.gridY, t);
                if (!activePatchGrid.TryGetPatchChartPoint(gridX, gridY, true, out Vector3 world, out Vector3 normal))
                {
                    error = "Surface loop path no longer resolves on the patch";
                    return false;
                }

                AddBoundaryPointIfDistinct(boundary, new LoopBuildPoint
                {
                    chart = new Vector2(gridX * cellSize, gridY * cellSize),
                    world = world,
                    normal = normal
                });
            }
        }

        if (boundary.Count > 1 && (boundary[boundary.Count - 1].chart - boundary[0].chart).sqrMagnitude <= 1e-8f)
            boundary.RemoveAt(boundary.Count - 1);

        if (boundary.Count < MinClosedLoopPointCount)
        {
            error = "Closed loop needs at least three distinct points";
            return false;
        }

        return true;
    }

    LoopBuildPoint BuildBoundaryPoint(SketchPoint point)
    {
        return new LoopBuildPoint
        {
            chart = ResolveSketchChartPoint(point),
            world = ResolveOperationWorld(point),
            normal = point != null ? point.normal : Vector3.up
        };
    }

    static void AddBoundaryPointIfDistinct(List<LoopBuildPoint> boundary, LoopBuildPoint point)
    {
        if (boundary.Count == 0 ||
            (boundary[boundary.Count - 1].chart - point.chart).sqrMagnitude > 1e-8f)
        {
            boundary.Add(point);
        }
    }

    static void AddTriangleOriented(SketchPrismMesh mesh, int a, int b, int c, Vector3 desiredNormal)
    {
        Vector3 normal = Vector3.Cross(mesh.vertices[b] - mesh.vertices[a], mesh.vertices[c] - mesh.vertices[a]);
        if (desiredNormal.sqrMagnitude > 1e-8f && Vector3.Dot(normal, desiredNormal) < 0f)
        {
            mesh.triangles.Add(a);
            mesh.triangles.Add(c);
            mesh.triangles.Add(b);
            return;
        }

        mesh.triangles.Add(a);
        mesh.triangles.Add(b);
        mesh.triangles.Add(c);
    }

    static bool LoopUsesOneSurfaceMode(SketchLoop loop)
    {
        if (loop == null || loop.points.Count == 0)
            return false;

        bool firstSurfaceMode = loop.points[0].surfaceNode;
        for (int i = 1; i < loop.points.Count; i++)
            if (loop.points[i].surfaceNode != firstSurfaceMode)
                return false;
        return true;
    }

    Vector2 ResolveSketchChartPoint(SketchPoint point)
    {
        if (point == null)
            return Vector2.zero;

        float cellSize = ResolveCellSize();
        return new Vector2(point.gridX * cellSize, point.gridY * cellSize);
    }

    Vector3 ResolveOperationWorld(SketchPoint point)
    {
        if (point == null)
            return Vector3.zero;
        if (point.surfaceNode)
            return point.surfaceWorld;
        return point.world;
    }

    Vector3 ResolveSketchPlanePoint(Vector2 chart)
    {
        if (activePlane == null)
            return Vector3.zero;
        return ResolveSketchChartOrigin()
            + activePlane.LocalX * chart.x
            + activePlane.LocalY * chart.y;
    }

    Vector3 ResolveSketchChartOrigin()
    {
        if (activePlane != null)
            return activePlane.Origin;
        return Vector3.zero;
    }

    Vector3 ResolveSketchPlaneNormal(IList<LoopBuildPoint> boundary)
    {
        return ResolveStableNormal(boundary);
    }

    Vector3 ResolveStableNormal(params SketchPoint[] points)
    {
        if (points == null)
            return activePlane != null ? activePlane.LocalZ : Vector3.up;

        Vector3 normal = Vector3.zero;
        for (int i = 0; i < points.Length; i++)
            if (points[i] != null)
                normal += points[i].normal;
        return ResolveStableNormal(normal);
    }

    Vector3 ResolveStableNormal(List<SketchPoint> points)
    {
        Vector3 normal = Vector3.zero;
        for (int i = 0; i < points.Count; i++)
            if (points[i] != null)
                normal += points[i].normal;
        return ResolveStableNormal(normal);
    }

    Vector3 ResolveStableNormal(IList<LoopBuildPoint> points)
    {
        Vector3 normal = Vector3.zero;
        if (points != null)
            for (int i = 0; i < points.Count; i++)
                normal += points[i].normal;
        return ResolveStableNormal(normal);
    }

    Vector3 ResolveStableNormal(Vector3 normal)
    {
        if (normal.sqrMagnitude <= 1e-8f)
            normal = activePlane != null ? activePlane.LocalZ : Vector3.up;
        else
            normal.Normalize();

        if (activePlane != null && Vector3.Dot(normal, activePlane.LocalZ) < 0f)
            normal = -normal;
        return normal;
    }

    float ResolveCellSize()
    {
        if (activePlane != null)
            return Mathf.Max(0.01f, activePlane.GridSpacing);
        return Setup3DatumPlaneController.ResolveSharedCellSize();
    }

    bool TryParseExtrudeDepth(out float depth)
    {
        return TryParsePositiveMm(extrudeDepthInput, MinExtrudeDepthMm, out depth);
    }

    bool TryParseCircleSize(out float size)
    {
        return TryParsePositiveMm(circleSizeInput, MinExtrudeDepthMm, out size);
    }

    bool TryResolveCircleRadius(out float radius)
    {
        radius = 0f;
        if (!TryParseCircleSize(out float size))
            return false;

        radius = ComputeCircleRadiusMm(size, circleSizeMode == CircleSizeMode.Diameter);
        return radius > 1e-4f;
    }

    bool TryResolveCircleRadius(SketchCircle circle, out float radius)
    {
        radius = 0f;
        if (circle == null)
            return false;

        if (!TryParsePositiveMm(circle.sizeInput, MinExtrudeDepthMm, out float size))
            return false;

        radius = ComputeCircleRadiusMm(size, circle.sizeMode == CircleSizeMode.Diameter);
        return radius > 1e-4f;
    }

    static bool TryParsePositiveMm(string input, float minValue, out float value)
    {
        string normalized = (input ?? "").Trim().Replace(',', '.');
        if (!float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;

        return value >= minValue;
    }

    static string FormatMm(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    string ResolveExtrudeTargetText()
    {
        if (!TryResolveLatestExtrudeTarget(out ExtrudeTargetKind kind, out int index))
            return "Target: close loop or circle";

        return kind == ExtrudeTargetKind.Circle
            ? $"Target: Circle {index + 1}"
            : $"Target: Loop {index + 1}";
    }

    int ClosedTargetCount()
    {
        return closedLoops.Count + circles.Count;
    }

    string CreateSketchMeshOperandPath()
    {
        string folder = Path.Combine(Application.persistentDataPath, "setup3");
        Directory.CreateDirectory(folder);
        string fileName = $"setup3_sketch_extrude_{System.DateTime.UtcNow.Ticks}_{System.Guid.NewGuid().ToString("N")}.obj";
        return NormalizeMeshOperandPath(Path.Combine(folder, fileName));
    }

    static string NormalizeMeshOperandPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        return path.Replace('\\', '/');
    }

    static void WriteSketchMeshObj(SketchPrismMesh mesh, string path)
    {
        var sb = new StringBuilder(mesh.vertices.Count * 48 + mesh.triangles.Count * 12 + 96);
        sb.AppendLine("# Setup 3 sketch extrude operand");
        for (int i = 0; i < mesh.vertices.Count; i++)
        {
            Vector3 v = mesh.vertices[i];
            sb.Append("v ")
                .Append(v.x.ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
                .Append(v.y.ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
                .Append(v.z.ToString("F6", CultureInfo.InvariantCulture)).AppendLine();
        }

        for (int i = 0; i + 2 < mesh.triangles.Count; i += 3)
        {
            sb.Append("f ")
                .Append(mesh.triangles[i] + 1).Append(' ')
                .Append(mesh.triangles[i + 1] + 1).Append(' ')
                .Append(mesh.triangles[i + 2] + 1).AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    void OnGUI()
    {
        if (activePlane == null)
        {
            panelRect = default;
            return;
        }

        InitStyles();
        NormalizeActiveNumericTarget();
        ProcessKeyboardEvent();
        NormalizeActiveNumericTarget();

        bool circleTargetActive = IsCircleTargetActive(out _);
        float pw = 292f;
        float ph = Mathf.Min(circleTargetActive ? 740f : 690f, Mathf.Max(520f, Screen.height - 32f));
        float px = Screen.width - pw - 18f;
        float py = Mathf.Max(16f, Screen.height - ph - 16f);
        panelRect = new Rect(px, py, pw, ph);

        GUI.Box(panelRect, "", panelStyle);
        GUI.Label(new Rect(px + 12f, py + 10f, pw - 24f, 22f), "SETUP 3 SKETCH", titleStyle);

        float y = py + 42f;
        DrawModeButton(px + 12f, y, 130f, SketchMode.Line, "Line");
        DrawModeButton(px + 150f, y, 130f, SketchMode.Circle, "Circle");

        y += 42f;
        GUI.Label(new Rect(px + 12f, y, pw - 24f, 20f), ResolveStatusText(), labelStyle);

        y += 34f;
        if (GUI.Button(new Rect(px + 12f, y, 130f, 30f), "Undo", buttonStyle))
            UndoLast();
        if (GUI.Button(new Rect(px + 150f, y, 130f, 30f), "Clear", dangerButtonStyle))
            ClearSketch();

        y += 44f;
        string counts = $"Loops {closedLoops.Count}   Open {openLine.Count}   Circles {circles.Count}";
        GUI.Label(new Rect(px + 12f, y, pw - 24f, 20f), counts, labelStyle);

        y += 28f;
        GUI.Label(new Rect(px + 12f, y, pw - 24f, 20f), "OPERATION", labelStyle);
        y += 22f;
        if (GUI.Button(new Rect(px + 12f, y, 130f, 28f), "ADD", cutMode ? buttonStyle : addButtonStyle))
            cutMode = false;
        if (GUI.Button(new Rect(px + 150f, y, 130f, 28f), "CUT", cutMode ? cutButtonStyle : buttonStyle))
            cutMode = true;

        y += 34f;
        GUI.Label(new Rect(px + 12f, y, pw - 24f, 20f), ResolveExtrudeTargetText(), labelStyle);

        if (circleTargetActive)
        {
            y += 28f;
            GUI.Label(new Rect(px + 12f, y, pw - 24f, 20f), "ROUND SIZE", labelStyle);

            y += 22f;
            if (GUI.Button(new Rect(px + 12f, y, 130f, 28f), "Radius",
                    circleSizeMode == CircleSizeMode.Radius ? activeButtonStyle : buttonStyle))
                SetCircleSizeMode(CircleSizeMode.Radius);
            if (GUI.Button(new Rect(px + 150f, y, 130f, 28f), "Diameter",
                    circleSizeMode == CircleSizeMode.Diameter ? activeButtonStyle : buttonStyle))
                SetCircleSizeMode(CircleSizeMode.Diameter);

            y += 36f;
            string sizeLabel = circleSizeMode == CircleSizeMode.Radius ? "R" : "D";
            if (GUI.Button(new Rect(px + 12f, y, 130f, 28f), $"{sizeLabel} : {ResolveCircleSizeDisplay()}",
                    activeNumericInput == NumericInputTarget.CircleSize ? activeButtonStyle : buttonStyle))
                activeNumericInput = NumericInputTarget.CircleSize;
            if (GUI.Button(new Rect(px + 150f, y, 130f, 28f), $"Depth : {ResolveDepthDisplay()}",
                    activeNumericInput == NumericInputTarget.Depth ? activeButtonStyle : buttonStyle))
                activeNumericInput = NumericInputTarget.Depth;

            y += 36f;
            GUI.Label(new Rect(px + 12f, y, pw - 24f, 34f), ResolveActiveNumericDisplay(), valueStyle);
        }
        else
        {
            activeNumericInput = NumericInputTarget.Depth;
            y += 28f;
            GUI.Label(new Rect(px + 12f, y, pw - 24f, 34f), $"Depth = {ResolveDepthDisplay()} mm", valueStyle);
        }

        y += 42f;
        string[,] keys = { { "7", "8", "9" }, { "4", "5", "6" }, { "1", "2", "3" }, { ".", "0", "C" } };
        float btnW = 84f, btnH = 40f, gap = 5f, numL = px + 12f;
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                string key = keys[row, col];
                GUIStyle keyStyle = key == "." || key == "C" ? numBtnSpecial : numBtnStyle;
                if (GUI.Button(new Rect(numL + col * (btnW + gap), y + row * (btnH + gap), btnW, btnH), key, keyStyle))
                    HandleNumericKey(key);
            }
        }

        y += 4 * (btnH + gap) + 2f;
        if (GUI.Button(new Rect(numL, y, 3 * btnW + 2 * gap, 36f), "backspace", numBtnSpecial))
            HandleNumericKey("backspace");

        y += 46f;
        GUI.Label(new Rect(px + 12f, y, pw - 24f, 34f), BuildExtrudeSummary(), labelStyle);

        y += 38f;
        bool canExtrude = CanExtrudeNow();
        GUI.enabled = canExtrude;
        string confirmLabel = cutMode ? "Confirm CUT" : "Confirm ADD";
        GUIStyle confirmButtonStyle = canExtrude ? confirmStyle : disabledButtonStyle;
        if (GUI.Button(new Rect(px + 12f, y, 130f, 30f), confirmLabel, confirmButtonStyle))
            ConfirmExtrude(cutMode);
        GUI.enabled = true;
        if (GUI.Button(new Rect(px + 150f, y, 130f, 30f), "Close Sketch", dangerButtonStyle))
            CloseSketchPlane();

        y += 38f;
        string snap = hoverPoint != null
            ? $"Snap ({hoverPoint.gridX},{hoverPoint.gridY}) {(hoverPoint.surfaceNode ? "surface" : "plane")}"
            : "Snap --";
        GUI.Label(new Rect(px + 12f, y, pw - 24f, 20f), snap, labelStyle);

        y += 22f;
        GUI.Label(new Rect(px + 12f, y, pw - 24f, 20f), ResolveBuildStatusText(), labelStyle);
    }

    void DrawModeButton(float x, float y, float w, SketchMode mode, string label)
    {
        if (GUI.Button(new Rect(x, y, w, 28f), label, sketchMode == mode ? activeButtonStyle : buttonStyle))
        {
            sketchMode = mode;
            circleCenter = null;
        }
    }

    string ResolveStatusText()
    {
        if (activePlane == null)
            return "Create datum plane first";
        if (sketchMode == SketchMode.Circle && circleCenter != null)
            return $"Circle center ({circleCenter.gridX},{circleCenter.gridY})";
        if (sketchMode == SketchMode.Line && openLine.Count > 0)
            return $"Line open: {openLine.Count} node(s)";
        return sketchMode.ToString();
    }

    void CloseSketchPlane()
    {
        ClearSketch();
        if (planeController == null)
            planeController = FindFirstObjectByType<Setup3DatumPlaneController>();

        if (planeController != null)
        {
            planeController.CloseActivePlane();
            return;
        }

        if (activePlane != null)
            Destroy(activePlane.gameObject);
        SetActivePlane(null);
    }

    bool CanExtrudeNow()
    {
        if (activePlane == null || previewManager == null || !TryParseExtrudeDepth(out _))
            return false;

        if (ClosedTargetCount() == 0)
            return false;

        for (int i = 0; i < circles.Count; i++)
            if (!TryResolveCircleRadius(circles[i], out _))
                return false;

        return true;
    }

    string ResolveDepthDisplay()
    {
        string text = string.IsNullOrWhiteSpace(extrudeDepthInput) ? "0" : extrudeDepthInput.Trim();
        return text;
    }

    string ResolveCircleSizeDisplay()
    {
        string text = string.IsNullOrWhiteSpace(circleSizeInput) ? "0" : circleSizeInput.Trim();
        return text;
    }

    string ResolveActiveNumericDisplay()
    {
        if (activeNumericInput == NumericInputTarget.CircleSize)
        {
            string label = circleSizeMode == CircleSizeMode.Radius ? "R" : "D";
            return $"{label} = {ResolveCircleSizeDisplay()} mm";
        }

        return $"Depth = {ResolveDepthDisplay()} mm";
    }

    string BuildExtrudeSummary()
    {
        string op = cutMode ? "CUT" : "ADD";
        string target = ResolveExtrudeTargetText()
            .Replace("Target: ", "")
            .Replace("Targets: ", "");
        string depth = ResolveDepthDisplay();
        if (ClosedTargetCount() == 1 && IsCircleTargetActive(out _))
        {
            string sizeLabel = circleSizeMode == CircleSizeMode.Radius ? "R" : "D";
            return $"{op} | {target} | {sizeLabel} {ResolveCircleSizeDisplay()} mm | depth {depth} mm";
        }

        return $"{op} | {target} | depth {depth} mm";
    }

    void HandleNumericKey(string key)
    {
        NormalizeActiveNumericTarget();
        string cur = GetActiveNumericText();

        if (key == "C")
        {
            SetActiveNumericText("0");
            return;
        }

        if (key == "backspace" || key == "⌫")
        {
            SetActiveNumericText(cur.Length > 1 ? cur.Substring(0, cur.Length - 1) : "0");
            return;
        }

        if (key == ".")
        {
            if (!cur.Contains("."))
                SetActiveNumericText(cur + ".");
            return;
        }

        if (key.Length == 1 && key[0] >= '0' && key[0] <= '9')
        {
            if (cur == "0")
                cur = "";
            if (cur.Length < 8)
                SetActiveNumericText(cur + key);
        }
    }

    string GetActiveNumericText()
    {
        string value = activeNumericInput == NumericInputTarget.CircleSize ? circleSizeInput : extrudeDepthInput;
        return string.IsNullOrWhiteSpace(value) ? "0" : value.Trim();
    }

    void SetActiveNumericText(string value)
    {
        if (activeNumericInput == NumericInputTarget.CircleSize)
        {
            circleSizeInput = value;
            StoreActiveCircleSizeState();
        }
        else
        {
            extrudeDepthInput = value;
        }
    }

    bool IsCircleTargetActive(out int index)
    {
        index = -1;
        return TryResolveLatestExtrudeTarget(out ExtrudeTargetKind kind, out index) &&
               kind == ExtrudeTargetKind.Circle;
    }

    void NormalizeActiveNumericTarget()
    {
        if (IsCircleTargetActive(out int index))
        {
            SyncCircleSizeStateFromTarget(index);
            return;
        }

        activeCircleSequenceId = -1;
        if (activeNumericInput == NumericInputTarget.CircleSize)
            activeNumericInput = NumericInputTarget.Depth;
    }

    void SyncCircleSizeStateFromTarget(int index)
    {
        if (index < 0 || index >= circles.Count)
            return;

        SketchCircle circle = circles[index];
        if (activeCircleSequenceId == circle.sequenceId)
            return;

        circleSizeInput = string.IsNullOrWhiteSpace(circle.sizeInput) ? "0" : circle.sizeInput;
        circleSizeMode = circle.sizeMode;
        activeCircleSequenceId = circle.sequenceId;
    }

    void StoreActiveCircleSizeState()
    {
        if (!IsCircleTargetActive(out int index) || index < 0 || index >= circles.Count)
            return;

        circles[index].sizeInput = circleSizeInput;
        circles[index].sizeMode = circleSizeMode;
        activeCircleSequenceId = circles[index].sequenceId;
    }

    void SetCircleSizeMode(CircleSizeMode mode)
    {
        if (circleSizeMode == mode)
        {
            activeNumericInput = NumericInputTarget.CircleSize;
            StoreActiveCircleSizeState();
            return;
        }

        if (TryParseCircleSize(out float size))
        {
            float radius = ComputeCircleRadiusMm(size, circleSizeMode == CircleSizeMode.Diameter);
            circleSizeInput = FormatMm(mode == CircleSizeMode.Diameter ? radius * 2f : radius);
        }

        circleSizeMode = mode;
        activeNumericInput = NumericInputTarget.CircleSize;
        StoreActiveCircleSizeState();
    }

    void ProcessKeyboardEvent()
    {
        Event e = Event.current;
        if (e == null || e.type != EventType.KeyDown)
            return;

        if (e.keyCode >= KeyCode.Alpha0 && e.keyCode <= KeyCode.Alpha9)
        {
            HandleNumericKey(((int)e.keyCode - (int)KeyCode.Alpha0).ToString(CultureInfo.InvariantCulture));
            e.Use();
            return;
        }

        if (e.keyCode >= KeyCode.Keypad0 && e.keyCode <= KeyCode.Keypad9)
        {
            HandleNumericKey(((int)e.keyCode - (int)KeyCode.Keypad0).ToString(CultureInfo.InvariantCulture));
            e.Use();
            return;
        }

        if (e.keyCode == KeyCode.Period || e.keyCode == KeyCode.Comma || e.keyCode == KeyCode.KeypadPeriod)
        {
            HandleNumericKey(".");
            e.Use();
            return;
        }

        if (e.keyCode == KeyCode.Backspace)
        {
            HandleNumericKey("backspace");
            e.Use();
            return;
        }

        if (e.keyCode == KeyCode.Delete)
        {
            HandleNumericKey("C");
            e.Use();
            return;
        }

        if (e.keyCode == KeyCode.Tab)
        {
            if (IsCircleTargetActive(out _))
                activeNumericInput = activeNumericInput == NumericInputTarget.Depth
                    ? NumericInputTarget.CircleSize
                    : NumericInputTarget.Depth;
            e.Use();
            return;
        }

        if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
        {
            ConfirmExtrude(cutMode);
            e.Use();
        }
    }

    string ResolveBuildStatusText()
    {
        if (previewManager != null && previewManager.OperationCount > 0)
            return previewManager.RebuildStatusText;
        if (!string.IsNullOrEmpty(lastExtrudeMessage))
            return lastExtrudeMessage;
        return "";
    }

    public void UndoLast()
    {
        if (circleCenter != null)
        {
            circleCenter = null;
            return;
        }
        if (openLine.Count > 0)
        {
            openLine.RemoveAt(openLine.Count - 1);
            return;
        }
        if (TryResolveLatestExtrudeTarget(out ExtrudeTargetKind kind, out int index))
        {
            if (kind == ExtrudeTargetKind.Circle)
                circles.RemoveAt(index);
            else
                closedLoops.RemoveAt(index);
            NormalizeActiveNumericTarget();
            return;
        }
    }

    void ClearSketch()
    {
        openLine.Clear();
        closedLoops.Clear();
        circles.Clear();
        circleCenter = null;
        circleSizeInput = "0";
        activeNumericInput = NumericInputTarget.Depth;
        activeCircleSequenceId = -1;
        lastExtrudeMessage = "";
    }

    void InitStyles()
    {
        if (stylesReady)
            return;
        stylesReady = true;

        panelStyle = new GUIStyle(GUI.skin.box);
        panelStyle.normal.background = SolidColorTextureCache.Get(new Color(0.09f, 0.12f, 0.14f, 0.96f));
        panelStyle.padding = new RectOffset(12, 12, 10, 10);

        titleStyle = new GUIStyle(GUI.skin.label);
        titleStyle.fontSize = 14;
        titleStyle.fontStyle = FontStyle.Bold;
        titleStyle.normal.textColor = new Color(0.78f, 0.92f, 0.96f, 1f);

        labelStyle = new GUIStyle(GUI.skin.label);
        labelStyle.fontSize = 12;
        labelStyle.normal.textColor = new Color(0.72f, 0.82f, 0.86f, 1f);

        valueStyle = new GUIStyle(GUI.skin.label);
        valueStyle.fontSize = 22;
        valueStyle.fontStyle = FontStyle.Bold;
        valueStyle.alignment = TextAnchor.MiddleCenter;
        valueStyle.normal.textColor = Color.white;

        buttonStyle = new GUIStyle(GUI.skin.button);
        buttonStyle.fontSize = 12;
        buttonStyle.normal.textColor = Color.white;
        buttonStyle.normal.background = SolidColorTextureCache.Get(new Color(0.22f, 0.27f, 0.38f, 1f));
        buttonStyle.hover.background = SolidColorTextureCache.Get(new Color(0.30f, 0.36f, 0.50f, 1f));

        activeButtonStyle = new GUIStyle(buttonStyle);
        activeButtonStyle.normal.background = SolidColorTextureCache.Get(new Color(0.11f, 0.55f, 0.50f, 1f));
        activeButtonStyle.hover.background = SolidColorTextureCache.Get(new Color(0.14f, 0.68f, 0.62f, 1f));

        addButtonStyle = new GUIStyle(buttonStyle);
        addButtonStyle.normal.background = SolidColorTextureCache.Get(new Color(0.16f, 0.50f, 0.28f, 1f));
        addButtonStyle.hover.background = SolidColorTextureCache.Get(new Color(0.22f, 0.64f, 0.36f, 1f));

        cutButtonStyle = new GUIStyle(buttonStyle);
        cutButtonStyle.normal.background = SolidColorTextureCache.Get(new Color(0.68f, 0.16f, 0.18f, 1f));
        cutButtonStyle.hover.background = SolidColorTextureCache.Get(new Color(0.82f, 0.22f, 0.24f, 1f));

        confirmStyle = new GUIStyle(buttonStyle);
        confirmStyle.fontSize = 13;
        confirmStyle.normal.background = SolidColorTextureCache.Get(new Color(0.20f, 0.55f, 0.90f, 1f));
        confirmStyle.hover.background = SolidColorTextureCache.Get(new Color(0.30f, 0.65f, 1.00f, 1f));

        disabledButtonStyle = new GUIStyle(buttonStyle);
        disabledButtonStyle.normal.background = SolidColorTextureCache.Get(new Color(0.18f, 0.19f, 0.22f, 0.86f));
        disabledButtonStyle.normal.textColor = new Color(0.55f, 0.58f, 0.62f, 1f);

        dangerButtonStyle = new GUIStyle(buttonStyle);
        dangerButtonStyle.normal.background = SolidColorTextureCache.Get(new Color(0.48f, 0.16f, 0.18f, 1f));
        dangerButtonStyle.hover.background = SolidColorTextureCache.Get(new Color(0.62f, 0.20f, 0.24f, 1f));

        textFieldStyle = new GUIStyle(GUI.skin.textField);
        textFieldStyle.fontSize = 12;
        textFieldStyle.normal.textColor = Color.white;
        textFieldStyle.focused.textColor = Color.white;
        textFieldStyle.normal.background = SolidColorTextureCache.Get(new Color(0.16f, 0.19f, 0.25f, 1f));
        textFieldStyle.focused.background = SolidColorTextureCache.Get(new Color(0.22f, 0.26f, 0.34f, 1f));

        numBtnStyle = new GUIStyle(buttonStyle);
        numBtnStyle.fontSize = 20;
        numBtnStyle.fontStyle = FontStyle.Bold;
        numBtnStyle.normal.background = SolidColorTextureCache.Get(new Color(0.22f, 0.26f, 0.36f, 1f));
        numBtnStyle.hover.background = SolidColorTextureCache.Get(new Color(0.32f, 0.38f, 0.52f, 1f));

        numBtnSpecial = new GUIStyle(numBtnStyle);
        numBtnSpecial.normal.background = SolidColorTextureCache.Get(new Color(0.28f, 0.20f, 0.32f, 1f));
        numBtnSpecial.hover.background = SolidColorTextureCache.Get(new Color(0.40f, 0.28f, 0.46f, 1f));
    }
}
