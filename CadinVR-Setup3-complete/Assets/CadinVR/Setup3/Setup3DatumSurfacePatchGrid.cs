// Attach to: the runtime DatumPlane GameObject created by Setup3DatumPlaneController.
//
// Purpose:
//   Draw a small curved surface patch grid for Setup 3 without taking ownership
//   away from Setup3DatumPlaneController, Setup3DatumPlaneVisual, Setup3SketchTool, or
//   Setup3DatumPreviewManager.
//
// Current implementation:
//   - initialize from the same surface hit that spawned the datum plane
//   - sample a dense square lattice in local datum-plane coordinates
//   - raycast each lattice node directly back onto the mesh collider
//   - reject samples that drift too close to the silhouette/backside
//   - connect valid neighboring hits into a draped "fabric" patch
//   - provide curved patch cell picking on the body surface
//
// Still out of scope here:
//   - emboss/deboss authoring
//   - export logic

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class Setup3DatumSurfacePatchGrid : MonoBehaviour
{
    public enum SurfaceInteractionMode
    {
        Auto,
        PlaneOnly,
        SurfaceOnly
    }

    public struct SurfacePatchCell
    {
        public int ix;
        public int iy;
        public Vector2 chart;
        public Vector3 world;
        public Vector3 normalWorld;
        public int triIndex;
    }

    public struct SurfacePatchFrame
    {
        public Vector3 center;
        public Vector3 localX;
        public Vector3 localY;
        public Vector3 normal;
        public float cellWidth;
        public float cellHeight;
    }

    struct PatchSeed
    {
        public RaycastHit hit;
        public MeshCollider collider;
        public Mesh mesh;
        public Transform meshTransform;
        public int triangleIndex;
        public Vector3 pointWorld;
        public Vector3 normalWorld;
    }

    struct ChartSample
    {
        public int ix;
        public int iy;
        public Vector2 chart;
        public Vector3 surfaceWorld;
        public Vector3 world;
        public Vector3 normalWorld;
        public int triIndex;
        public bool valid;
    }

    struct EdgeSegment
    {
        public int a;
        public int b;
    }

    private const float SurfaceLiftMin = 0.05f;
    private const float SurfaceLiftFactor = 0.08f;
    private const float CastStartMin = 4f;
    private const float CastStartPatchFactor = 0.75f;
    private const float CastDistanceMargin = 8f;
    private const float EdgeLowerTolerance = 0.45f;
    private const float EdgeUpperTolerance = 2.40f;
    private const float MaxNeighborNormalDeviationDeg = 35f;
    private const float MaxGuidedNormalDeviationDeg = 45f;
    private const float MaxCellCornerNormalSpreadDeg = 40f;
    private const float MaxSampleNormalDeviationFromSeedDeg = 60f;
    private const bool EnableNeighborGuidedPatchGrowth = false;
    private const float MaxPickDistanceMultiplier = 1.55f;
    private const float MinNormalDotFromSeed = -0.1f;
    private const float DisplayHalfSizePaddingCells = 2f;
    private const float FlatFallbackNormalAngleDeg = 2.5f;
    private const float FlatFallbackPlaneDistanceFactor = 0.20f;
    private const float LocalPlaneOffsetMin = 0.35f;
    private const float LocalPlaneOffsetPerRadius = 2.0f;
    private const float CurvedPatchDisplayOffsetMm = 0f;

    public bool HasValidPatch => hasValidPatch;
    public float CellSizeMm => cellSizeMm;
    public float PatchRadiusMm => patchRadiusMm;
    public SurfaceInteractionMode InteractionMode => interactionMode;

    public bool TryGetChartOrigin(out Vector3 origin)
    {
        if (plane != null)
        {
            origin = plane.Origin;
            return initialized;
        }
        origin = seed.pointWorld;
        return initialized;
    }

    public static bool IsPatchNormalWithinSeedLimit(Vector3 normalWorld, Vector3 seedNormalWorld)
    {
        if (normalWorld.sqrMagnitude <= 1e-8f || seedNormalWorld.sqrMagnitude <= 1e-8f)
            return true;
        return Vector3.Angle(normalWorld.normalized, seedNormalWorld.normalized) <= MaxSampleNormalDeviationFromSeedDeg;
    }

    private Setup3DatumPlaneVisual plane;
    private PatchSeed seed;

    private float cellSizeMm = 1f;
    private float initialPatchRadiusMm = 12f;
    private float patchRadiusMm = 12f;
    private bool initialized = false;
    private bool hasValidPatch = false;

    private Material glMat;

    private readonly List<ChartSample> samples = new List<ChartSample>();
    private readonly List<EdgeSegment> edges = new List<EdgeSegment>();
    private readonly Dictionary<Vector2Int, int> sampleIndexByGrid = new Dictionary<Vector2Int, int>();
    private SurfaceInteractionMode interactionMode = SurfaceInteractionMode.Auto;

    public void Initialize(Setup3DatumPlaneVisual activePlane, RaycastHit seedHit, float cellSize, float patchRadius)
    {
        plane = activePlane;
        cellSizeMm = Mathf.Max(0.01f, cellSize);
        initialPatchRadiusMm = Mathf.Max(cellSizeMm * 2f, patchRadius);
        patchRadiusMm = initialPatchRadiusMm;

        if (!(seedHit.collider is MeshCollider meshCollider) || meshCollider.sharedMesh == null)
        {
            ClearPatch();
            return;
        }

        seed = new PatchSeed
        {
            hit = seedHit,
            collider = meshCollider,
            mesh = meshCollider.sharedMesh,
            meshTransform = meshCollider.transform,
            triangleIndex = seedHit.triangleIndex,
            pointWorld = seedHit.point,
            normalWorld = ResolveSeedNormal(seedHit)
        };

        RebuildPatch();
        initialized = true;
    }

    public void SetInteractionMode(SurfaceInteractionMode mode)
    {
        interactionMode = mode;
        if (!initialized || plane == null)
            return;

        RebuildPatch();
    }

    public bool EnsurePatchRadius(float minRadiusMm)
    {
        float targetRadius = Mathf.Max(initialPatchRadiusMm, Mathf.Max(cellSizeMm * 2f, minRadiusMm));
        if (Mathf.Abs(targetRadius - patchRadiusMm) <= 1e-4f)
            return hasValidPatch;

        patchRadiusMm = targetRadius;
        if (initialized && plane != null)
            RebuildPatch();

        return hasValidPatch;
    }

    public bool EnsureCellRangeCoverage(int minCellX, int maxCellX, int minCellY, int maxCellY)
    {
        if (minCellX > maxCellX || minCellY > maxCellY)
            return false;

        float baseRadius = ComputeRequestedPatchRadiusMm(initialPatchRadiusMm, cellSizeMm, minCellX, maxCellX, minCellY, maxCellY);
        float growthStep = Mathf.Max(cellSizeMm * 2f, 1f);

        for (int attempt = 0; attempt < 6; attempt++)
        {
            float targetRadius = baseRadius + growthStep * attempt;
            bool radiusChanged = Mathf.Abs(targetRadius - patchRadiusMm) > 1e-4f;
            patchRadiusMm = targetRadius;
            if ((radiusChanged || !hasValidPatch) && initialized && plane != null)
                RebuildPatch();

            if (HasCellRangeCoverage(minCellX, maxCellX, minCellY, maxCellY))
                return true;
        }

        return false;
    }

    public static float ComputeRequestedPatchRadiusMm(float minimumRadiusMm, float cellSizeMm, int minCellX, int maxCellX, int minCellY, int maxCellY)
    {
        int maxAbs = Mathf.Max(
            Mathf.Abs(minCellX),
            Mathf.Abs(maxCellX + 1),
            Mathf.Abs(minCellY),
            Mathf.Abs(maxCellY + 1));

        float minRadius = Mathf.Max(Mathf.Max(0.01f, cellSizeMm) * 2f, minimumRadiusMm);
        return Mathf.Max(minRadius, (maxAbs + 2) * Mathf.Max(0.01f, cellSizeMm));
    }

    public void OnPlaneFrameChanged()
    {
        if (!initialized || plane == null)
            return;

        RebuildPatch();
    }

    public void ClearPatch()
    {
        samples.Clear();
        edges.Clear();
        sampleIndexByGrid.Clear();
        hasValidPatch = false;
        initialized = false;
        if (plane != null)
        {
            plane.SetDisplayOffset(Setup3DatumPlaneController.ResolveDefaultDisplayPlaneOffsetMm(cellSizeMm));
            plane.SetSurfacePatchVisualMode(false);
        }
    }

    public bool TryPickSurfaceCell(Ray ray, out SurfacePatchCell cell)
    {
        return TryPickSurfaceCell(ray, out cell, out _, out _);
    }

    public bool TryPickSurfaceCell(Ray ray, out SurfacePatchCell cell, out Vector3 hitWorld, out Vector3 hitNormalWorld)
    {
        cell = default;
        hitWorld = Vector3.zero;
        hitNormalWorld = seed.normalWorld;

        if (!hasValidPatch || seed.collider == null || samples.Count == 0)
            return false;

        if (!seed.collider.Raycast(ray, out RaycastHit hit, Mathf.Infinity))
            return false;

        hitWorld = hit.point;
        hitNormalWorld = ResolveHitNormalInSeedHemisphere(hit, seed.normalWorld);

        if (TryResolveCellFromWorldHit(hit.point, hit.normal, out cell))
            return true;

        if (TryResolveNearestSampleByWorld(hit.point, out int nearestIndex) &&
            TryResolveCellNearSample(samples[nearestIndex], hit.point, out cell))
        {
            return true;
        }

        return false;
    }

    public bool TryGetCellFrame(int ix, int iy, out SurfacePatchFrame frame)
    {
        frame = default;

        if (!TryGetCellCorners(ix, iy, true, out Vector3 c00, out Vector3 c10, out Vector3 c11, out Vector3 c01))
            return false;

        Vector3 center = (c00 + c10 + c11 + c01) * 0.25f;
        Vector3 xVec = ((c10 - c00) + (c11 - c01)) * 0.5f;
        Vector3 yVec = ((c01 - c00) + (c11 - c10)) * 0.5f;
        float cellWidth = xVec.magnitude;
        float cellHeight = yVec.magnitude;
        if (cellWidth <= 1e-4f || cellHeight <= 1e-4f)
            return false;

        Vector3 localX = xVec / cellWidth;
        Vector3 normal = Vector3.Cross(xVec, yVec);
        if (normal.sqrMagnitude <= 1e-8f)
            normal = seed.normalWorld;
        else
            normal.Normalize();

        if (Vector3.Dot(normal, seed.normalWorld) < 0f)
            normal = -normal;

        Vector3 localY = Vector3.Cross(normal, localX);
        if (localY.sqrMagnitude <= 1e-8f)
            localY = yVec / cellHeight;
        else
            localY.Normalize();

        frame = new SurfacePatchFrame
        {
            center = center,
            localX = localX,
            localY = localY,
            normal = normal,
            cellWidth = cellWidth,
            cellHeight = cellHeight
        };
        return true;
    }

    public bool TryGetPatchNode(int ix, int iy, bool useSurfaceWorld, out Vector3 world, out Vector3 normalWorld)
    {
        world = Vector3.zero;
        normalWorld = Vector3.up;

        if (!sampleIndexByGrid.TryGetValue(new Vector2Int(ix, iy), out int sampleIndex))
            return false;

        ChartSample sample = samples[sampleIndex];
        world = useSurfaceWorld ? sample.surfaceWorld : sample.world;
        normalWorld = sample.normalWorld.sqrMagnitude > 1e-6f ? sample.normalWorld.normalized : seed.normalWorld;
        return true;
    }

    public bool TryGetPatchChartPoint(float gridX, float gridY, bool useSurfaceWorld, out Vector3 world, out Vector3 normalWorld)
    {
        world = Vector3.zero;
        normalWorld = seed.normalWorld.sqrMagnitude > 1e-6f ? seed.normalWorld.normalized : Vector3.up;

        if (!hasValidPatch)
            return false;

        int ix = Mathf.FloorToInt(gridX);
        int iy = Mathf.FloorToInt(gridY);
        float tx = gridX - ix;
        float ty = gridY - iy;
        const float eps = 1e-4f;

        bool onNodeX = Mathf.Abs(tx) <= eps || Mathf.Abs(1f - tx) <= eps;
        bool onNodeY = Mathf.Abs(ty) <= eps || Mathf.Abs(1f - ty) <= eps;
        if (onNodeX && onNodeY)
        {
            int nodeX = Mathf.RoundToInt(gridX);
            int nodeY = Mathf.RoundToInt(gridY);
            return TryGetPatchNode(nodeX, nodeY, useSurfaceWorld, out world, out normalWorld);
        }

        if (Mathf.Abs(tx) <= eps) tx = 0f;
        else if (Mathf.Abs(1f - tx) <= eps) tx = 1f;

        if (Mathf.Abs(ty) <= eps) ty = 0f;
        else if (Mathf.Abs(1f - ty) <= eps) ty = 1f;

        if (TrySamplePatchCell(ix, iy, tx, ty, useSurfaceWorld, out world, out normalWorld))
            return true;

        if (tx <= eps && TrySamplePatchCell(ix - 1, iy, 1f, ty, useSurfaceWorld, out world, out normalWorld))
            return true;
        if (ty <= eps && TrySamplePatchCell(ix, iy - 1, tx, 1f, useSurfaceWorld, out world, out normalWorld))
            return true;
        if (tx <= eps && ty <= eps && TrySamplePatchCell(ix - 1, iy - 1, 1f, 1f, useSurfaceWorld, out world, out normalWorld))
            return true;

        return false;
    }

    public bool TryResolveCellFromWorldPoint(Vector3 worldPoint, Vector3 normalWorld, out SurfacePatchCell cell)
    {
        return TryResolveCellFromWorldHit(worldPoint, normalWorld, out cell);
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

    void OnDestroy()
    {
        if (glMat != null)
            Destroy(glMat);
    }

    void OnRenderObject()
    {
        if (!hasValidPatch || glMat == null)
            return;

        GL.PushMatrix();
        glMat.SetPass(0);
        DrawPatchGrid();
        GL.PopMatrix();
    }

    void RebuildPatch()
    {
        samples.Clear();
        edges.Clear();
        sampleIndexByGrid.Clear();

        if (plane == null || seed.collider == null)
        {
            hasValidPatch = false;
            if (plane != null)
            {
                plane.SetDisplayOffset(Setup3DatumPlaneController.ResolveDefaultDisplayPlaneOffsetMm(cellSizeMm));
                plane.SetSurfacePatchVisualMode(false);
            }
            return;
        }

        if (interactionMode == SurfaceInteractionMode.PlaneOnly)
        {
            hasValidPatch = false;
            plane.SetDisplayOffset(Setup3DatumPlaneController.ResolveDefaultDisplayPlaneOffsetMm(cellSizeMm));
            plane.SetSurfacePatchVisualMode(false);
            return;
        }

        if (!BuildPatchSamples())
        {
            hasValidPatch = false;
            plane.SetDisplayOffset(Setup3DatumPlaneController.ResolveDefaultDisplayPlaneOffsetMm(cellSizeMm));
            plane.SetSurfacePatchVisualMode(false);
            return;
        }

        if (!PruneToSeedConnectedComponent())
        {
            hasValidPatch = false;
            plane.SetDisplayOffset(Setup3DatumPlaneController.ResolveDefaultDisplayPlaneOffsetMm(cellSizeMm));
            plane.SetSurfacePatchVisualMode(false);
            return;
        }

        if (interactionMode != SurfaceInteractionMode.SurfaceOnly && IsLocallyFlatEnoughForPlanarMode())
        {
            hasValidPatch = false;
            plane.SetDisplayOffset(Setup3DatumPlaneController.ResolveDefaultDisplayPlaneOffsetMm(cellSizeMm));
            plane.SetSurfacePatchVisualMode(false);
            return;
        }

        BuildPatchEdges();
        hasValidPatch = samples.Count > 0 && edges.Count > 0;
        plane.SetDisplayOffset(hasValidPatch ? CurvedPatchDisplayOffsetMm : Setup3DatumPlaneController.ResolveDefaultDisplayPlaneOffsetMm(cellSizeMm));
        plane.SetSurfacePatchVisualMode(hasValidPatch, ResolveDisplayPlaneHalfSize());

        if (!hasValidPatch)
            return;
    }

    bool BuildPatchSamples()
    {
        int maxSteps = Mathf.CeilToInt(patchRadiusMm / cellSizeMm);
        float surfaceLift = ResolveSurfaceLift();

        Vector3 delta = seed.pointWorld - plane.Origin;
        float chartOffsetX = Vector3.Dot(delta, plane.LocalX);
        float chartOffsetY = Vector3.Dot(delta, plane.LocalY);
        int centerGridX = Mathf.RoundToInt(chartOffsetX / cellSizeMm);
        int centerGridY = Mathf.RoundToInt(chartOffsetY / cellSizeMm);

        int minGridX = centerGridX - maxSteps;
        int maxGridX = centerGridX + maxSteps;
        int minGridY = centerGridY - maxSteps;
        int maxGridY = centerGridY + maxSteps;

        // Pass 1: direct tangent-plane projection from the seed frame.
        for (int iy = minGridY; iy <= maxGridY; iy++)
        {
            for (int ix = minGridX; ix <= maxGridX; ix++)
            {
                Vector2 chartPoint = new Vector2(ix * cellSizeMm, iy * cellSizeMm);
                if (!TryRaycastSample(chartPoint, out RaycastHit hit))
                    continue;

                AddSample(ix, iy, chartPoint, hit, surfaceLift);
            }
        }

        if (EnableNeighborGuidedPatchGrowth)
        {
            // Neighbor walking can drift around silhouette edges. Keep it disabled
            // for the Setup 3 sketch grid unless a later surface mode explicitly
            // needs wrapped sampling.
            int guidedPasses = Mathf.Clamp(maxSteps + 2, 2, 12);
            for (int pass = 0; pass < guidedPasses; pass++)
            {
                bool addedAny = false;
                for (int iy = minGridY; iy <= maxGridY; iy++)
                {
                    for (int ix = minGridX; ix <= maxGridX; ix++)
                    {
                        Vector2Int gridKey = new Vector2Int(ix, iy);
                        if (sampleIndexByGrid.ContainsKey(gridKey))
                            continue;

                        Vector2 chartPoint = new Vector2(ix * cellSizeMm, iy * cellSizeMm);
                        if (TryRaycastSampleFromNeighbor(ix, iy, chartPoint, out RaycastHit hit))
                        {
                            AddSample(ix, iy, chartPoint, hit, surfaceLift);
                            addedAny = true;
                        }
                    }
                }

                if (!addedAny)
                    break;
            }
        }

        return samples.Count >= 4;
    }

    void AddSample(int ix, int iy, Vector2 chartPoint, RaycastHit hit, float surfaceLift)
    {
        Vector2Int key = new Vector2Int(ix, iy);
        if (sampleIndexByGrid.ContainsKey(key))
            return;

        Vector3 normalWorld = ResolveHitNormalInSeedHemisphere(hit, seed.normalWorld);
        Vector3 surfaceWorldPoint = hit.point;
        Vector3 worldPoint = surfaceWorldPoint + normalWorld * surfaceLift;

        var sample = new ChartSample
        {
            ix = ix,
            iy = iy,
            chart = chartPoint,
            surfaceWorld = surfaceWorldPoint,
            world = worldPoint,
            normalWorld = normalWorld,
            triIndex = hit.triangleIndex,
            valid = true
        };

        sampleIndexByGrid[key] = samples.Count;
        samples.Add(sample);
    }

    bool TryRaycastSample(Vector2 chartPoint, out RaycastHit hit)
    {
        hit = default;

        Vector3 planePoint = plane.Origin
            + plane.LocalX * chartPoint.x
            + plane.LocalY * chartPoint.y;

        float castStart = ResolveCastStart();
        float castDistance = castStart * 2f + patchRadiusMm + CastDistanceMargin;

        Ray primary = new Ray(planePoint + plane.LocalZ * castStart, -plane.LocalZ);
        if (seed.collider.Raycast(primary, out hit, castDistance) && IsUsableHit(hit) && IsLocalSurfaceHit(chartPoint, hit))
            return true;

        Ray secondary = new Ray(planePoint - plane.LocalZ * castStart, plane.LocalZ);
        if (seed.collider.Raycast(secondary, out hit, castDistance) && IsUsableHit(hit) && IsLocalSurfaceHit(chartPoint, hit))
            return true;

        return false;
    }

    bool TryRaycastSampleFromNeighbor(int ix, int iy, Vector2 chartPoint, out RaycastHit hit)
    {
        hit = default;

        // Optional wrapped-surface fallback. Setup 3 keeps this disabled for
        // sketching so the visible grid remains a local seed-frame projection.
        if (TryRaycastFromNeighbor(ix - 1, iy, ix, iy, chartPoint, out hit)) return true;
        if (TryRaycastFromNeighbor(ix + 1, iy, ix, iy, chartPoint, out hit)) return true;
        if (TryRaycastFromNeighbor(ix, iy - 1, ix, iy, chartPoint, out hit)) return true;
        if (TryRaycastFromNeighbor(ix, iy + 1, ix, iy, chartPoint, out hit)) return true;
        return false;
    }

    bool TryRaycastFromNeighbor(int neighborX, int neighborY, int targetX, int targetY, Vector2 chartPoint, out RaycastHit hit)
    {
        hit = default;

        if (!sampleIndexByGrid.TryGetValue(new Vector2Int(neighborX, neighborY), out int neighborIndex))
            return false;

        ChartSample neighbor = samples[neighborIndex];
        Vector3 chartStep = plane.LocalX * ((targetX - neighborX) * cellSizeMm)
            + plane.LocalY * ((targetY - neighborY) * cellSizeMm);
        Vector3 tangentStep = Vector3.ProjectOnPlane(chartStep, neighbor.normalWorld);
        if (tangentStep.sqrMagnitude <= 1e-8f)
            tangentStep = chartStep;

        Vector3 castBase = neighbor.surfaceWorld + tangentStep;
        float castStart = Mathf.Max(cellSizeMm * 2.5f, CastStartMin * 0.5f);
        float castDistance = castStart * 2f + cellSizeMm * 5f;

        Ray primary = new Ray(castBase + neighbor.normalWorld * castStart, -neighbor.normalWorld);
        if (seed.collider.Raycast(primary, out hit, castDistance) &&
            IsUsableHit(hit) &&
            IsNeighborGuidedHit(neighbor, castBase, chartPoint, hit))
        {
            return true;
        }

        Ray secondary = new Ray(castBase - neighbor.normalWorld * castStart, neighbor.normalWorld);
        if (seed.collider.Raycast(secondary, out hit, castDistance) &&
            IsUsableHit(hit) &&
            IsNeighborGuidedHit(neighbor, castBase, chartPoint, hit))
        {
            return true;
        }

        return false;
    }

    bool IsUsableHit(RaycastHit hit)
    {
        Vector3 normalWorld = ResolveHitNormalInSeedHemisphere(hit, seed.normalWorld);
        return Vector3.Dot(normalWorld, seed.normalWorld) >= MinNormalDotFromSeed &&
               IsPatchNormalWithinSeedLimit(normalWorld, seed.normalWorld);
    }

    bool IsLocalSurfaceHit(Vector2 chartPoint, RaycastHit hit)
    {
        Vector3 delta = hit.point - seed.pointWorld;
        float planeOffset = Mathf.Abs(Vector3.Dot(delta, seed.normalWorld));
        float allowedOffset = Mathf.Max(LocalPlaneOffsetMin, chartPoint.magnitude * LocalPlaneOffsetPerRadius);
        return planeOffset <= allowedOffset;
    }

    bool IsNeighborGuidedHit(ChartSample neighbor, Vector3 castBase, Vector2 chartPoint, RaycastHit hit)
    {
        Vector3 normalWorld = ResolveHitNormalInSeedHemisphere(hit, neighbor.normalWorld);
        if (Vector3.Angle(neighbor.normalWorld, normalWorld) > MaxGuidedNormalDeviationDeg)
            return false;

        float castBaseDist = Vector3.Distance(hit.point, castBase);
        float allowedBaseDist = Mathf.Max(cellSizeMm * 2.0f, chartPoint.magnitude * 0.45f);
        if (castBaseDist > allowedBaseDist)
            return false;

        Vector3 travel = hit.point - neighbor.surfaceWorld;
        Vector3 chartDelta = plane.LocalX * (chartPoint.x - neighbor.chart.x)
            + plane.LocalY * (chartPoint.y - neighbor.chart.y);
        Vector3 tangentDelta = Vector3.ProjectOnPlane(chartDelta, neighbor.normalWorld);
        if (tangentDelta.sqrMagnitude > 1e-8f)
        {
            float forward = Vector3.Dot(Vector3.ProjectOnPlane(travel, neighbor.normalWorld), tangentDelta.normalized);
            if (forward < -cellSizeMm * 0.35f)
                return false;
        }

        return true;
    }

    Vector3 ResolveSeedNormal(RaycastHit seedHit)
    {
        Vector3 normal = seedHit.normal.sqrMagnitude > 1e-6f ? seedHit.normal.normalized : Vector3.up;
        Camera cam = Camera.main;
        if (cam == null)
            return normal;

        Vector3 viewDir = seedHit.point - cam.transform.position;
        if (viewDir.sqrMagnitude <= 1e-8f)
            return normal;

        viewDir.Normalize();
        if (Vector3.Dot(normal, viewDir) > 0f)
            normal = -normal;
        return normal;
    }

    Vector3 ResolveHitNormalInSeedHemisphere(RaycastHit hit, Vector3 fallbackNormal)
    {
        Vector3 normal = hit.normal.sqrMagnitude > 1e-6f ? hit.normal.normalized : fallbackNormal;
        Vector3 reference = fallbackNormal.sqrMagnitude > 1e-6f ? fallbackNormal.normalized : Vector3.up;
        if (Vector3.Dot(normal, reference) < 0f)
            normal = -normal;
        return normal;
    }

    void BuildPatchEdges()
    {
        edges.Clear();
        for (int i = 0; i < samples.Count; i++)
        {
            ChartSample sample = samples[i];
            TryAddEdge(i, sample.ix + 1, sample.iy);
            TryAddEdge(i, sample.ix, sample.iy + 1);
        }
    }

    void TryAddEdge(int sourceIndex, int neighborX, int neighborY)
    {
        if (!sampleIndexByGrid.TryGetValue(new Vector2Int(neighborX, neighborY), out int targetIndex))
            return;

        ChartSample a = samples[sourceIndex];
        ChartSample b = samples[targetIndex];
        if (!CanConnectSamples(a, b))
            return;

        edges.Add(new EdgeSegment { a = sourceIndex, b = targetIndex });
    }

    bool CanConnectSamples(ChartSample a, ChartSample b)
    {
        float dist = Vector3.Distance(a.surfaceWorld, b.surfaceWorld);
        if (dist < cellSizeMm * EdgeLowerTolerance || dist > cellSizeMm * EdgeUpperTolerance)
            return false;

        if (Vector3.Angle(a.normalWorld, b.normalWorld) > MaxNeighborNormalDeviationDeg)
            return false;

        return true;
    }

    bool PruneToSeedConnectedComponent()
    {
        if (samples.Count < 4)
            return false;

        if (!TryGetSeedSampleIndex(out int seedIndex))
            return false;

        bool[] keep = new bool[samples.Count];
        var queue = new Queue<int>();
        keep[seedIndex] = true;
        queue.Enqueue(seedIndex);

        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            ChartSample sample = samples[current];
            TryQueueNeighbor(sample.ix + 1, sample.iy, current, keep, queue);
            TryQueueNeighbor(sample.ix - 1, sample.iy, current, keep, queue);
            TryQueueNeighbor(sample.ix, sample.iy + 1, current, keep, queue);
            TryQueueNeighbor(sample.ix, sample.iy - 1, current, keep, queue);
        }

        int keptCount = 0;
        for (int i = 0; i < keep.Length; i++)
            if (keep[i]) keptCount++;

        if (keptCount < 4)
            return false;

        var oldSamples = new List<ChartSample>(samples);
        samples.Clear();
        sampleIndexByGrid.Clear();
        edges.Clear();

        for (int i = 0; i < oldSamples.Count; i++)
        {
            if (!keep[i])
                continue;

            ChartSample sample = oldSamples[i];
            sampleIndexByGrid[new Vector2Int(sample.ix, sample.iy)] = samples.Count;
            samples.Add(sample);
        }

        return true;
    }

    bool IsLocallyFlatEnoughForPlanarMode()
    {
        if (samples.Count == 0)
            return true;

        float maxAngle = 0f;
        float maxPlaneDistance = 0f;
        float planeTolerance = Mathf.Max(0.05f, cellSizeMm * FlatFallbackPlaneDistanceFactor);

        for (int i = 0; i < samples.Count; i++)
        {
            ChartSample sample = samples[i];
            maxAngle = Mathf.Max(maxAngle, Vector3.Angle(seed.normalWorld, sample.normalWorld));

            Vector3 delta = sample.surfaceWorld - seed.pointWorld;
            float planeDistance = Mathf.Abs(Vector3.Dot(delta, seed.normalWorld));
            maxPlaneDistance = Mathf.Max(maxPlaneDistance, planeDistance);

            if (maxAngle > FlatFallbackNormalAngleDeg || maxPlaneDistance > planeTolerance)
                return false;
        }

        return true;
    }

    bool TryGetSeedSampleIndex(out int seedIndex)
    {
        seedIndex = -1;
        float bestDistSq = float.PositiveInfinity;
        for (int i = 0; i < samples.Count; i++)
        {
            float distSq = (samples[i].surfaceWorld - seed.pointWorld).sqrMagnitude;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                seedIndex = i;
            }
        }

        return seedIndex >= 0;
    }

    void TryQueueNeighbor(int ix, int iy, int sourceIndex, bool[] keep, Queue<int> queue)
    {
        if (!sampleIndexByGrid.TryGetValue(new Vector2Int(ix, iy), out int neighborIndex))
            return;

        if (keep[neighborIndex])
            return;

        if (!CanConnectSamples(samples[sourceIndex], samples[neighborIndex]))
            return;

        keep[neighborIndex] = true;
        queue.Enqueue(neighborIndex);
    }

    bool TryResolveCellFromWorldHit(Vector3 worldPoint, Vector3 normalWorld, out SurfacePatchCell cell)
    {
        cell = default;

        Vector2 chartPoint = ProjectWorldToChart(worldPoint);
        int baseIx = Mathf.FloorToInt(chartPoint.x / cellSizeMm);
        int baseIy = Mathf.FloorToInt(chartPoint.y / cellSizeMm);

        float bestScore = float.PositiveInfinity;
        bool found = false;
        SurfacePatchCell bestCell = default;
        float maxDistSq = cellSizeMm * cellSizeMm * MaxPickDistanceMultiplier * MaxPickDistanceMultiplier;

        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                if (!TryBuildCell(baseIx + dx, baseIy + dy, normalWorld, out SurfacePatchCell candidate))
                    continue;

                float worldDistSq = (candidate.world - worldPoint).sqrMagnitude;
                float chartDistSq = (candidate.chart - chartPoint).sqrMagnitude;
                float score = worldDistSq + chartDistSq * 0.15f;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestCell = candidate;
                    found = true;
                }
            }
        }

        if (found && (bestCell.world - worldPoint).sqrMagnitude <= maxDistSq)
        {
            cell = bestCell;
            return true;
        }

        return false;
    }

    bool TryResolveNearestSampleByWorld(Vector3 worldPoint, out int sampleIndex)
    {
        sampleIndex = -1;
        float maxDistSq = cellSizeMm * cellSizeMm * 1.5f * 1.5f;
        float bestDistSq = maxDistSq;

        for (int i = 0; i < samples.Count; i++)
        {
            float distSq = (samples[i].surfaceWorld - worldPoint).sqrMagnitude;
            if (distSq <= bestDistSq)
            {
                bestDistSq = distSq;
                sampleIndex = i;
            }
        }

        return sampleIndex >= 0;
    }

    bool TryResolveCellNearSample(ChartSample sample, Vector3 worldPoint, out SurfacePatchCell cell)
    {
        cell = default;
        SurfacePatchCell bestCell = default;
        bool found = false;
        float bestDistSq = float.PositiveInfinity;

        int[,] candidates =
        {
            { sample.ix,     sample.iy     },
            { sample.ix - 1, sample.iy     },
            { sample.ix,     sample.iy - 1 },
            { sample.ix - 1, sample.iy - 1 }
        };

        for (int i = 0; i < 4; i++)
        {
            if (!TryBuildCell(candidates[i, 0], candidates[i, 1], sample.normalWorld, out SurfacePatchCell candidate))
                continue;

            float distSq = (candidate.world - worldPoint).sqrMagnitude;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestCell = candidate;
                found = true;
            }
        }

        if (found)
            cell = bestCell;
        return found;
    }

    bool TryBuildCell(int ix, int iy, Vector3 fallbackNormalWorld, out SurfacePatchCell cell)
    {
        cell = default;

        if (!TryGetCellSampleIndices(ix, iy, out int i00, out int i10, out int i01, out int i11))
            return false;

        if (!IsSmoothCell(i00, i10, i01, i11))
            return false;

        Vector3 worldCenter = (samples[i00].surfaceWorld + samples[i10].surfaceWorld + samples[i01].surfaceWorld + samples[i11].surfaceWorld) * 0.25f;
        Vector3 averagedNormal = samples[i00].normalWorld + samples[i10].normalWorld + samples[i01].normalWorld + samples[i11].normalWorld;
        Vector3 normalWorld = averagedNormal.sqrMagnitude > 1e-6f ? averagedNormal.normalized : fallbackNormalWorld;
        if (Vector3.Dot(normalWorld, seed.normalWorld) < 0f)
            normalWorld = -normalWorld;

        cell = new SurfacePatchCell
        {
            ix = ix,
            iy = iy,
            chart = new Vector2((ix + 0.5f) * cellSizeMm, (iy + 0.5f) * cellSizeMm),
            world = worldCenter,
            normalWorld = normalWorld,
            triIndex = samples[i00].triIndex
        };
        return true;
    }

    bool TryGetCellCorners(int ix, int iy, bool useSurfaceWorld, out Vector3 c00, out Vector3 c10, out Vector3 c11, out Vector3 c01)
    {
        c00 = c10 = c11 = c01 = Vector3.zero;

        if (!TryGetCellSampleIndices(ix, iy, out int i00, out int i10, out int i01, out int i11))
            return false;

        if (!IsSmoothCell(i00, i10, i01, i11))
            return false;

        c00 = useSurfaceWorld ? samples[i00].surfaceWorld : samples[i00].world;
        c10 = useSurfaceWorld ? samples[i10].surfaceWorld : samples[i10].world;
        c11 = useSurfaceWorld ? samples[i11].surfaceWorld : samples[i11].world;
        c01 = useSurfaceWorld ? samples[i01].surfaceWorld : samples[i01].world;
        return true;
    }

    bool TryGetCellSampleIndices(int ix, int iy, out int i00, out int i10, out int i01, out int i11)
    {
        i00 = i10 = i01 = i11 = -1;
        return sampleIndexByGrid.TryGetValue(new Vector2Int(ix, iy), out i00) &&
               sampleIndexByGrid.TryGetValue(new Vector2Int(ix + 1, iy), out i10) &&
               sampleIndexByGrid.TryGetValue(new Vector2Int(ix, iy + 1), out i01) &&
               sampleIndexByGrid.TryGetValue(new Vector2Int(ix + 1, iy + 1), out i11);
    }

    bool TrySamplePatchCell(int ix, int iy, float tx, float ty, bool useSurfaceWorld, out Vector3 world, out Vector3 normalWorld)
    {
        world = Vector3.zero;
        normalWorld = seed.normalWorld.sqrMagnitude > 1e-6f ? seed.normalWorld.normalized : Vector3.up;

        if (!TryGetCellCorners(ix, iy, useSurfaceWorld, out Vector3 c00, out Vector3 c10, out Vector3 c11, out Vector3 c01))
            return false;

        tx = Mathf.Clamp01(tx);
        ty = Mathf.Clamp01(ty);
        Vector3 bottom = Vector3.Lerp(c00, c10, tx);
        Vector3 top = Vector3.Lerp(c01, c11, tx);
        world = Vector3.Lerp(bottom, top, ty);

        if (!TryGetPatchNode(ix, iy, true, out _, out Vector3 n00) ||
            !TryGetPatchNode(ix + 1, iy, true, out _, out Vector3 n10) ||
            !TryGetPatchNode(ix + 1, iy + 1, true, out _, out Vector3 n11) ||
            !TryGetPatchNode(ix, iy + 1, true, out _, out Vector3 n01))
        {
            return true;
        }

        Vector3 normalBottom = Vector3.Lerp(n00, n10, tx);
        Vector3 normalTop = Vector3.Lerp(n01, n11, tx);
        Vector3 normal = Vector3.Lerp(normalBottom, normalTop, ty);
        if (normal.sqrMagnitude > 1e-6f)
            normalWorld = normal.normalized;
        if (Vector3.Dot(normalWorld, seed.normalWorld) < 0f)
            normalWorld = -normalWorld;

        return true;
    }

    bool IsSmoothCell(int i00, int i10, int i01, int i11)
    {
        float maxAngle = 0f;
        maxAngle = Mathf.Max(maxAngle, Vector3.Angle(samples[i00].normalWorld, samples[i10].normalWorld));
        maxAngle = Mathf.Max(maxAngle, Vector3.Angle(samples[i00].normalWorld, samples[i01].normalWorld));
        maxAngle = Mathf.Max(maxAngle, Vector3.Angle(samples[i00].normalWorld, samples[i11].normalWorld));
        maxAngle = Mathf.Max(maxAngle, Vector3.Angle(samples[i10].normalWorld, samples[i01].normalWorld));
        maxAngle = Mathf.Max(maxAngle, Vector3.Angle(samples[i10].normalWorld, samples[i11].normalWorld));
        maxAngle = Mathf.Max(maxAngle, Vector3.Angle(samples[i01].normalWorld, samples[i11].normalWorld));
        return maxAngle <= MaxCellCornerNormalSpreadDeg;
    }

    bool HasCellRangeCoverage(int minCellX, int maxCellX, int minCellY, int maxCellY)
    {
        if (!hasValidPatch)
            return false;

        for (int iy = minCellY; iy <= maxCellY; iy++)
        {
            for (int ix = minCellX; ix <= maxCellX; ix++)
            {
                if (!TryBuildCell(ix, iy, seed.normalWorld, out _))
                    return false;
            }
        }

        return true;
    }

    void DrawPatchGrid()
    {
        GL.Begin(GL.LINES);
        GL.Color(new Color(0f, 0f, 0f, 0.98f));
        for (int i = 0; i < edges.Count; i++)
        {
            EdgeSegment edge = edges[i];
            GL.Vertex(samples[edge.a].world);
            GL.Vertex(samples[edge.b].world);
        }
        GL.End();
    }

    Vector2 ProjectWorldToChart(Vector3 worldPoint)
    {
        Vector3 delta = worldPoint - plane.Origin;
        return new Vector2(Vector3.Dot(delta, plane.LocalX), Vector3.Dot(delta, plane.LocalY));
    }

    float ResolveCastStart()
    {
        float displayOffset = plane != null ? plane.DisplayOffsetMm : 0f;
        return Mathf.Max(CastStartMin, Mathf.Max(displayOffset + cellSizeMm * 3f, patchRadiusMm * CastStartPatchFactor));
    }

    float ResolveSurfaceLift()
    {
        return Mathf.Max(SurfaceLiftMin, cellSizeMm * SurfaceLiftFactor);
    }

    float ResolveDisplayPlaneHalfSize()
    {
        return patchRadiusMm + cellSizeMm * DisplayHalfSizePaddingCells;
    }
}
