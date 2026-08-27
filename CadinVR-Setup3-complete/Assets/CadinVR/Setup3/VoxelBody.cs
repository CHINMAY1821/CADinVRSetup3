// VoxelBody.cs — VERSION 19
// Attach to: the root body GameObject (e.g. "test body cube")
//
// Changes from V18 — FIX STEP EXPORT FOR OBJ-VOXELISED SHAPES:
//   BUG: ExportSTEPFile used removedBodyCells as void list.
//        removedBodyCells is only populated by RemoveBodyBatch (manual user CUT).
//        For OBJ-voxelised shapes, exterior cells are never added to bodyCells —
//        they are NOT tracked in removedBodyCells → 0 voids sent → VoxelSTEP.exe
//        outputs a solid bounding box instead of the actual shape.
//   FIX: Compute voidCells = all cells within bodyCells bounding box NOT in bodyCells.
//        GreedyMergeSet(voidCells) → send as CUT operands.
//        This correctly handles OBJ-voxelised shapes AND manually CUT box bodies.
//        removedBodyCells field is retained but no longer used in ExportSTEPFile.
//   No Inspector re-wiring needed.
//
// Changes from V17 (retained from V18):
//   Element cuboid tracking: List<BoundsInt> elementCuboids — fast STEP export.
//   3-section STEP file: BBOX / CUT voids / ADD cuboids.

using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Diagnostics;

public class VoxelBody : MonoBehaviour
{
    [Header("Settings")]
    public GameObject elementPrefab;
    public float bodyVoxelSize = 1f;
    public float elementVoxelSize = 0.1f;
    public Transform solidRoot;

    [Header("Body Source")]
    public string sourceMeshPath = "";

    [Header("Box Fallback (used only when Source Mesh Path is empty)")]
    public Vector3Int bodySize = new Vector3Int(100, 100, 100);
    public bool registerBodyCells = true;
    public Transform bodyMeshTarget;

    // ── Undo stack ─────────────────────────────────────────────────────────
    private const int UndoStackDepth = 64;
    private enum UndoOpType { Add, Remove }

    private struct UndoCellRecord
    {
        public Vector3Int cell;
        public Vector3 worldPos;
        public bool wasBodyCell;
        public bool isFineGrid;
    }

    private struct UndoRecord
    {
        public UndoOpType op;
        public List<UndoCellRecord> cells;
        public BoundsInt elementCuboid;
        public bool hasCuboid;
    }

    private Stack<UndoRecord> undoStack = new Stack<UndoRecord>();

    // ── Voxel registries ───────────────────────────────────────────────────
    private Dictionary<Vector3Int, GameObject> bodyOccupied = new Dictionary<Vector3Int, GameObject>();
    private HashSet<Vector3Int> bodyCells = new HashSet<Vector3Int>();
    private HashSet<Vector3Int> removedBodyCells = new HashSet<Vector3Int>(); // kept for undo restore
    private Dictionary<Vector3Int, GameObject> elementOccupied = new Dictionary<Vector3Int, GameObject>();

    // ── Element cuboid list (V18) ──────────────────────────────────────────
    private List<BoundsInt> elementCuboids = new List<BoundsInt>();
    private bool elementCuboidsDirty = false;

    // ── Mesh rebuild ───────────────────────────────────────────────────────
    private bool bodyMeshDirty = false;
    private MeshFilter bodyMeshFilter;
    private MeshCollider bodyMeshCollider;
    private MeshCollider solidMeshCollider;
    private string lastImportedConfigKey = string.Empty;

    private static int CompareCellsByZyx(Vector3Int left, Vector3Int right)
    {
        int z = left.z.CompareTo(right.z);
        if (z != 0) return z;

        int y = left.y.CompareTo(right.y);
        if (y != 0) return y;

        return left.x.CompareTo(right.x);
    }

    // ── Paths ──────────────────────────────────────────────────────────────
    private string SavePath => Path.Combine(Application.persistentDataPath, "voxel_state.json");
    public string ExportPath => GetExportPath(".obj");
    public string StepExportPath => GetExportPath(".step");

    private string GetExportPath(string ext)
    {
        string baseName = string.IsNullOrEmpty(sourceMeshPath)
            ? "voxel_export" : Path.GetFileNameWithoutExtension(sourceMeshPath);
        return Path.Combine(@"D:\SRH\XR Technologies\Master Thesis folder\output objects", baseName + ext);
    }

    private string StepExePath =>
        Path.Combine(Application.streamingAssetsPath, "VoxelSTEP", "VoxelSTEP.exe");

    public string GetResolvedSourceMeshPath()
    {
        if (string.IsNullOrEmpty(sourceMeshPath))
            return sourceMeshPath;

        if (Path.GetExtension(sourceMeshPath).Equals(".obj", System.StringComparison.OrdinalIgnoreCase))
        {
            string siblingStl = Path.ChangeExtension(sourceMeshPath, ".stl");
            if (File.Exists(siblingStl))
                return siblingStl;
        }

        return sourceMeshPath;
    }

    // ── Unity messages ─────────────────────────────────────────────────────
    void Awake()
    {
        if (solidRoot == null) solidRoot = transform;
        Transform meshSource = bodyMeshTarget != null ? bodyMeshTarget : transform;
        bodyMeshFilter = meshSource.GetComponent<MeshFilter>();
        bodyMeshCollider = GetComponent<MeshCollider>();
        solidMeshCollider = meshSource.GetComponent<MeshCollider>();
        UnityEngine.Debug.Log($"[VoxelBody] Awake | bodyVoxelSize:{bodyVoxelSize} elementVoxelSize:{elementVoxelSize} meshFilter:{(bodyMeshFilter != null ? meshSource.name : "NULL")}");
    }

    void Start()
    {
        if (!registerBodyCells) return;
        if (File.Exists(SavePath))
        {
            string reason = "save could not be read";
            if (TryReadSaveData(out SaveData saveData) && SaveMatchesCurrentConfiguration(saveData, out reason))
            {
                UnityEngine.Debug.Log("[VoxelBody] Save file found — loading state.");
                LoadState(saveData);
                lastImportedConfigKey = GetCurrentImportConfigKey();
                return;
            }

            UnityEngine.Debug.Log($"[VoxelBody] Save file ignored — rebuilding source because {reason}");
        }

        ImportBodyFromSourceOrFallback();
        lastImportedConfigKey = GetCurrentImportConfigKey();
    }

    private void ImportBodyFromSourceOrFallback()
    {
        string meshPath = GetResolvedSourceMeshPath();
        if (!string.Equals(meshPath, sourceMeshPath, System.StringComparison.OrdinalIgnoreCase))
        {
            UnityEngine.Debug.Log($"[VoxelBody] Source OBJ has sibling STL — using STL instead: {meshPath}");
            sourceMeshPath = meshPath;
        }

        if (!string.IsNullOrEmpty(meshPath) && File.Exists(meshPath))
        {
            string ext = Path.GetExtension(meshPath).ToLowerInvariant();
            if (ext == ".stl")
            { UnityEngine.Debug.Log($"[VoxelBody] VoxeliseFromSTL: {meshPath}"); VoxeliseFromSTL(meshPath); }
            else
            { UnityEngine.Debug.Log($"[VoxelBody] Voxelising from OBJ: {meshPath}"); VoxeliseFromOBJ(meshPath); }
        }
        else
        { if (!string.IsNullOrEmpty(sourceMeshPath)) UnityEngine.Debug.LogWarning($"[VoxelBody] Source mesh not found: {sourceMeshPath} — using box fallback."); RegisterAllBodyCells(); }
    }

    void Update()
    {
        if (UnityEngine.Input.GetKeyDown(KeyCode.S))
            SaveState();

        string currentConfigKey = GetCurrentImportConfigKey();
        if (currentConfigKey != lastImportedConfigKey)
            ReimportCurrentConfiguration("Inspector source/grid settings changed");
    }
    void LateUpdate() { if (bodyMeshDirty) { bodyMeshDirty = false; RebuildBodyMesh(); } }

    // ── OBJ Voxeliser ──────────────────────────────────────────────────────
    void VoxeliseFromOBJ(string path)
    {
        var verts = new List<Vector3>(); var triList = new List<int>();
        foreach (var rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("v ") || line.StartsWith("v\t"))
            {
                string[] p = line.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4)
                    verts.Add(new Vector3(float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture)));
            }
            else if (line.StartsWith("f ") || line.StartsWith("f\t"))
            {
                string[] p = line.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                var idx = new List<int>();
                for (int i = 1; i < p.Length; i++) { string vi = p[i].Split('/')[0]; if (int.TryParse(vi, out int raw)) idx.Add(raw > 0 ? raw - 1 : verts.Count + raw); }
                for (int i = 1; i < idx.Count - 1; i++) { triList.Add(idx[0]); triList.Add(idx[i]); triList.Add(idx[i + 1]); }
            }
        }
        if (verts.Count == 0 || triList.Count == 0) { UnityEngine.Debug.LogError("[VoxelBody] VoxeliseFromOBJ: no geometry — using box fallback."); RegisterAllBodyCells(); return; }
        int triCount = triList.Count / 3; float vs = bodyVoxelSize;
        Vector3 meshMin = verts[0], meshMax = verts[0];
        foreach (var v in verts) { meshMin = Vector3.Min(meshMin, v); meshMax = Vector3.Max(meshMax, v); }
        int gMinX = Mathf.FloorToInt(meshMin.x / vs), gMaxX = Mathf.FloorToInt(meshMax.x / vs);
        int gMinY = Mathf.FloorToInt(meshMin.y / vs), gMaxY = Mathf.FloorToInt(meshMax.y / vs);
        int gMinZ = Mathf.FloorToInt(meshMin.z / vs), gMaxZ = Mathf.FloorToInt(meshMax.z / vs);
        var tv0 = new Vector3[triCount]; var tv1 = new Vector3[triCount]; var tv2 = new Vector3[triCount];
        for (int t = 0; t < triCount; t++) { tv0[t] = verts[triList[t * 3]]; tv1[t] = verts[triList[t * 3 + 1]]; tv2[t] = verts[triList[t * 3 + 2]]; }
        float rayStartY = meshMin.y - vs, dedupThresh = vs * 0.01f;
        var hitYs = new List<float>(32); var deduped = new List<float>(32); int total = 0;
        removedBodyCells.Clear(); elementCuboids.Clear(); elementCuboidsDirty = false;
        for (int xi = gMinX; xi <= gMaxX; xi++) for (int zi = gMinZ; zi <= gMaxZ; zi++)
            {
                float rayX = (xi + 0.5f) * vs, rayZ = (zi + 0.5f) * vs; hitYs.Clear();
                for (int t = 0; t < triCount; t++) if (RayTriangleIntersectY(rayX, rayZ, rayStartY, tv0[t], tv1[t], tv2[t], out float hy)) hitYs.Add(hy);
                if (hitYs.Count == 0) continue; hitYs.Sort(); deduped.Clear(); deduped.Add(hitYs[0]);
                for (int i = 1; i < hitYs.Count; i++) if (hitYs[i] - deduped[deduped.Count - 1] > dedupThresh) deduped.Add(hitYs[i]);
                for (int p = 0; p + 1 < deduped.Count; p += 2)
                {
                    int y0 = Mathf.FloorToInt(deduped[p] / vs), y1 = Mathf.FloorToInt(deduped[p + 1] / vs);
                    for (int yi = y0; yi <= y1; yi++) { var cell = new Vector3Int(xi, yi, zi); if (!bodyOccupied.ContainsKey(cell)) { bodyOccupied[cell] = null; bodyCells.Add(cell); total++; } }
                }
            }
        bodyMeshDirty = true;
        UnityEngine.Debug.Log($"[VoxelBody] VoxeliseFromOBJ complete | cells:{total}");
    }

    // ── STL Voxeliser ──────────────────────────────────────────────────────
    void VoxeliseFromSTL(string path)
    {
        if (!TryReadStlTriangles(path, out var allVerts, out var tv0, out var tv1, out var tv2))
        {
            UnityEngine.Debug.LogError("[VoxelBody] VoxeliseFromSTL: no geometry — using box fallback.");
            RegisterAllBodyCells();
            return;
        }

        int triCount = tv0.Length;
        float vs = bodyVoxelSize;
        Vector3 meshMin = allVerts[0], meshMax = allVerts[0];
        foreach (var v in allVerts) { meshMin = Vector3.Min(meshMin, v); meshMax = Vector3.Max(meshMax, v); }
        int gMinX = Mathf.FloorToInt(meshMin.x / vs), gMaxX = Mathf.FloorToInt(meshMax.x / vs);
        int gMinZ = Mathf.FloorToInt(meshMin.z / vs), gMaxZ = Mathf.FloorToInt(meshMax.z / vs);
        float rayStartY = meshMin.y - vs, dedupThresh = vs * 0.01f;
        var hitYs = new List<float>(32); var deduped = new List<float>(32); int total = 0;
        removedBodyCells.Clear(); elementCuboids.Clear(); elementCuboidsDirty = false;
        for (int xi = gMinX; xi <= gMaxX; xi++) for (int zi = gMinZ; zi <= gMaxZ; zi++)
            {
                float rayX = (xi + 0.5f) * vs, rayZ = (zi + 0.5f) * vs; hitYs.Clear();
                for (int t = 0; t < triCount; t++) if (RayTriangleIntersectY(rayX, rayZ, rayStartY, tv0[t], tv1[t], tv2[t], out float hy)) hitYs.Add(hy);
                if (hitYs.Count == 0) continue; hitYs.Sort(); deduped.Clear(); deduped.Add(hitYs[0]);
                for (int i = 1; i < hitYs.Count; i++) if (hitYs[i] - deduped[deduped.Count - 1] > dedupThresh) deduped.Add(hitYs[i]);
                for (int p = 0; p + 1 < deduped.Count; p += 2)
                {
                    int y0 = Mathf.FloorToInt(deduped[p] / vs), y1 = Mathf.FloorToInt(deduped[p + 1] / vs);
                    for (int yi = y0; yi <= y1; yi++) { var cell = new Vector3Int(xi, yi, zi); if (!bodyOccupied.ContainsKey(cell)) { bodyOccupied[cell] = null; bodyCells.Add(cell); total++; } }
                }
            }
        bodyMeshDirty = true;
        UnityEngine.Debug.Log($"[VoxelBody] VoxeliseFromSTL complete | cells:{total}");
    }

    static bool TryReadStlTriangles(string path, out List<Vector3> allVerts, out Vector3[] tv0, out Vector3[] tv1, out Vector3[] tv2)
    {
        allVerts = new List<Vector3>();
        tv0 = null; tv1 = null; tv2 = null;

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (fs.Length < 6)
                return false;

            bool isBinary = LooksLikeBinaryStl(fs);
            fs.Position = 0;
            return isBinary
                ? TryReadBinaryStl(fs, out allVerts, out tv0, out tv1, out tv2)
                : TryReadAsciiStl(path, out allVerts, out tv0, out tv1, out tv2);
        }
    }

    static bool LooksLikeBinaryStl(FileStream fs)
    {
        if (fs.Length < 84)
            return false;

        long start = fs.Position;
        using (var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true))
        {
            br.ReadBytes(80);
            uint triCount = br.ReadUInt32();
            fs.Position = start;
            return fs.Length == 84L + (long)triCount * 50L;
        }
    }

    static bool TryReadAsciiStl(string path, out List<Vector3> allVerts, out Vector3[] tv0, out Vector3[] tv1, out Vector3[] tv2)
    {
        allVerts = new List<Vector3>();
        var tv0List = new List<Vector3>();
        var tv1List = new List<Vector3>();
        var tv2List = new List<Vector3>();
        int vertexIndex = 0;
        Vector3 p0 = default, p1 = default;

        foreach (var rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("vertex", System.StringComparison.OrdinalIgnoreCase))
                continue;

            string[] parts = line.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                continue;

            var pt = new Vector3(
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                float.Parse(parts[2], CultureInfo.InvariantCulture),
                float.Parse(parts[3], CultureInfo.InvariantCulture));

            allVerts.Add(pt);
            if (vertexIndex == 0) p0 = pt;
            else if (vertexIndex == 1) p1 = pt;
            else
            {
                tv0List.Add(p0);
                tv1List.Add(p1);
                tv2List.Add(pt);
            }

            vertexIndex = (vertexIndex + 1) % 3;
        }

        tv0 = tv0List.ToArray();
        tv1 = tv1List.ToArray();
        tv2 = tv2List.ToArray();
        return tv0.Length > 0;
    }

    static bool TryReadBinaryStl(FileStream fs, out List<Vector3> allVerts, out Vector3[] tv0, out Vector3[] tv1, out Vector3[] tv2)
    {
        allVerts = new List<Vector3>();
        var tv0List = new List<Vector3>();
        var tv1List = new List<Vector3>();
        var tv2List = new List<Vector3>();

        using (var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true))
        {
            br.ReadBytes(80);
            uint triCount = br.ReadUInt32();

            for (uint i = 0; i < triCount; i++)
            {
                br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // normal

                Vector3 p0 = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                Vector3 p1 = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                Vector3 p2 = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                allVerts.Add(p0);
                allVerts.Add(p1);
                allVerts.Add(p2);
                tv0List.Add(p0);
                tv1List.Add(p1);
                tv2List.Add(p2);

                br.ReadUInt16(); // attribute byte count
            }
        }

        tv0 = tv0List.ToArray();
        tv1 = tv1List.ToArray();
        tv2 = tv2List.ToArray();
        return tv0.Length > 0;
    }

    private static bool RayTriangleIntersectY(float rayX, float rayZ, float startY, Vector3 v0, Vector3 v1, Vector3 v2, out float hitY)
    {
        hitY = 0f; const float EPS = 1e-6f;
        float e1x = v1.x - v0.x, e1y = v1.y - v0.y, e1z = v1.z - v0.z, e2x = v2.x - v0.x, e2z = v2.z - v0.z;
        float hx = e2z, hz = -e2x, a = e1x * hx + e1z * hz;
        if (a > -EPS && a < EPS) return false;
        float f = 1f / a, sx = rayX - v0.x, sy = startY - v0.y, sz = rayZ - v0.z, u = f * (sx * hx + sz * hz);
        if (u < 0f || u > 1f) return false;
        float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x, v = f * qy;
        if (v < 0f || u + v > 1f) return false;
        float e2y = v2.y - v0.y, tVal = f * (e2x * qx + e2y * qy + e2z * qz);
        if (tVal < EPS) return false;
        hitY = startY + tVal; return true;
    }

    // ── Save / Load ────────────────────────────────────────────────────────
    [System.Serializable] private struct CellEntry { public int x, y, z; }
    [System.Serializable] private struct CuboidEntry { public int x, y, z, sx, sy, sz; }
    [System.Serializable]
    private class SaveData
    {
        public string resolvedSourceMeshPath = "";
        public float bodyVoxelSize = 1f;
        public float elementVoxelSize = 0.1f;
        public int fallbackSizeX = 100;
        public int fallbackSizeY = 100;
        public int fallbackSizeZ = 100;
        public List<CellEntry> bodyCells = new List<CellEntry>();
        public List<CellEntry> elementCells = new List<CellEntry>();
        public List<CellEntry> removedCells = new List<CellEntry>();
        public List<CuboidEntry> elementCuboids = new List<CuboidEntry>();
    }

    private static string NormaliseSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path).Trim();
        }
        catch
        {
            return path.Trim();
        }
    }

    private string GetCurrentResolvedSourcePath()
    {
        return NormaliseSourcePath(GetResolvedSourceMeshPath());
    }

    private string GetCurrentImportConfigKey()
    {
        return string.Join("|",
            GetCurrentResolvedSourcePath(),
            bodyVoxelSize.ToString("R", CultureInfo.InvariantCulture),
            elementVoxelSize.ToString("R", CultureInfo.InvariantCulture),
            bodySize.x.ToString(CultureInfo.InvariantCulture),
            bodySize.y.ToString(CultureInfo.InvariantCulture),
            bodySize.z.ToString(CultureInfo.InvariantCulture));
    }

    private bool TryReadSaveData(out SaveData data)
    {
        data = null;
        if (!File.Exists(SavePath))
            return false;

        try
        {
            data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath));
            return data != null;
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[VoxelBody] Failed to read save metadata — rebuilding source. {ex.Message}");
            return false;
        }
    }

    private bool SaveMatchesCurrentConfiguration(SaveData data, out string reason)
    {
        string currentSourcePath = GetCurrentResolvedSourcePath();
        string savedSourcePath = NormaliseSourcePath(data.resolvedSourceMeshPath);

        if (!string.Equals(savedSourcePath, currentSourcePath, System.StringComparison.OrdinalIgnoreCase))
        {
            reason = $"source changed (saved:'{savedSourcePath}' current:'{currentSourcePath}')";
            return false;
        }

        if (!Mathf.Approximately(data.bodyVoxelSize, bodyVoxelSize))
        {
            reason = $"body voxel size changed (saved:{data.bodyVoxelSize} current:{bodyVoxelSize})";
            return false;
        }

        if (!Mathf.Approximately(data.elementVoxelSize, elementVoxelSize))
        {
            reason = $"element voxel size changed (saved:{data.elementVoxelSize} current:{elementVoxelSize})";
            return false;
        }

        if (string.IsNullOrEmpty(currentSourcePath))
        {
            if (data.fallbackSizeX != bodySize.x || data.fallbackSizeY != bodySize.y || data.fallbackSizeZ != bodySize.z)
            {
                reason = $"fallback box size changed (saved:{data.fallbackSizeX}x{data.fallbackSizeY}x{data.fallbackSizeZ} current:{bodySize.x}x{bodySize.y}x{bodySize.z})";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private void ReimportCurrentConfiguration(string reason)
    {
        UnityEngine.Debug.Log($"[VoxelBody] Reimporting runtime body — {reason}");
        ClearRuntimeState();
        ImportBodyFromSourceOrFallback();
        lastImportedConfigKey = GetCurrentImportConfigKey();
    }

    public void ReloadFromPath(string path)
    {
        sourceMeshPath = path;
        ClearRuntimeState();
        ImportBodyFromSourceOrFallback();
        lastImportedConfigKey = GetCurrentImportConfigKey();
    }

    private void ClearRuntimeState()
    {
        var spawnedObjects = new HashSet<GameObject>();

        foreach (var go in bodyOccupied.Values)
            if (go != null)
                spawnedObjects.Add(go);

        foreach (var go in elementOccupied.Values)
            if (go != null)
                spawnedObjects.Add(go);

        foreach (var go in spawnedObjects)
            Destroy(go);

        bodyOccupied.Clear();
        bodyCells.Clear();
        removedBodyCells.Clear();
        elementOccupied.Clear();
        elementCuboids.Clear();
        elementCuboidsDirty = false;
        undoStack.Clear();
        bodyMeshDirty = false;

        if (bodyMeshFilter != null)
            bodyMeshFilter.mesh = new Mesh { name = "BodyMesh" };

        if (bodyMeshCollider != null)
            bodyMeshCollider.sharedMesh = null;

        if (solidMeshCollider != null && solidMeshCollider != bodyMeshCollider)
            solidMeshCollider.sharedMesh = null;
    }

    public void SaveState()
    {
        var data = new SaveData();
        data.resolvedSourceMeshPath = GetCurrentResolvedSourcePath();
        data.bodyVoxelSize = bodyVoxelSize;
        data.elementVoxelSize = elementVoxelSize;
        data.fallbackSizeX = bodySize.x;
        data.fallbackSizeY = bodySize.y;
        data.fallbackSizeZ = bodySize.z;
        foreach (var c in bodyCells) data.bodyCells.Add(new CellEntry { x = c.x, y = c.y, z = c.z });
        foreach (var kvp in elementOccupied) data.elementCells.Add(new CellEntry { x = kvp.Key.x, y = kvp.Key.y, z = kvp.Key.z });
        foreach (var c in removedBodyCells) data.removedCells.Add(new CellEntry { x = c.x, y = c.y, z = c.z });
        var cuboidsToSave = GetElementCuboids();
        foreach (var b in cuboidsToSave) data.elementCuboids.Add(new CuboidEntry { x = b.xMin, y = b.yMin, z = b.zMin, sx = b.size.x, sy = b.size.y, sz = b.size.z });
        File.WriteAllText(SavePath, JsonUtility.ToJson(data));
        UnityEngine.Debug.Log($"[VoxelBody] Saved → body:{data.bodyCells.Count} elements:{data.elementCells.Count} removed:{data.removedCells.Count} cuboids:{data.elementCuboids.Count}");
    }

    public void LoadState()
    {
        if (!TryReadSaveData(out SaveData data))
        {
            UnityEngine.Debug.LogWarning("[VoxelBody] LoadState requested but save could not be read.");
            return;
        }

        LoadState(data);
    }

    private void LoadState(SaveData data)
    {
        bodyOccupied.Clear(); bodyCells.Clear(); elementOccupied.Clear();
        removedBodyCells.Clear(); elementCuboids.Clear(); elementCuboidsDirty = false;
        foreach (var e in data.bodyCells) { var c = new Vector3Int(e.x, e.y, e.z); bodyOccupied[c] = null; bodyCells.Add(c); }
        foreach (var e in data.elementCells) { var c = new Vector3Int(e.x, e.y, e.z); if (!elementOccupied.ContainsKey(c)) elementOccupied[c] = SpawnElement(FineToWorld(c)); }
        if (data.removedCells != null) foreach (var e in data.removedCells) removedBodyCells.Add(new Vector3Int(e.x, e.y, e.z));
        if (data.elementCuboids != null && data.elementCuboids.Count > 0)
            foreach (var e in data.elementCuboids) elementCuboids.Add(new BoundsInt(e.x, e.y, e.z, e.sx, e.sy, e.sz));
        else
            elementCuboidsDirty = true;
        bodyMeshDirty = true;
        UnityEngine.Debug.Log($"[VoxelBody] Loaded → body:{bodyCells.Count} elements:{data.elementCells.Count} removed:{removedBodyCells.Count} cuboids:{elementCuboids.Count}");
    }

    public void DeleteSave()
    { if (File.Exists(SavePath)) { File.Delete(SavePath); UnityEngine.Debug.Log("[VoxelBody] Save deleted."); } }

    // ── Box fallback ───────────────────────────────────────────────────────
    void RegisterAllBodyCells()
    {
        int sx = bodySize.x, sy = bodySize.y, sz = bodySize.z;
        int ox = Mathf.FloorToInt(-sx * 0.5f), oy = Mathf.FloorToInt(-sy * 0.5f), oz = Mathf.FloorToInt(-sz * 0.5f);
        bodyOccupied.EnsureCapacity(sx * sy * sz); bodyCells.EnsureCapacity(sx * sy * sz);
        removedBodyCells.Clear(); elementCuboids.Clear(); elementCuboidsDirty = false;
        for (int xi = 0; xi < sx; xi++) for (int yi = 0; yi < sy; yi++) for (int zi = 0; zi < sz; zi++)
                { var c = new Vector3Int(ox + xi, oy + yi, oz + zi); bodyOccupied[c] = null; bodyCells.Add(c); }
        UnityEngine.Debug.Log($"[VoxelBody] Box registered: {bodyCells.Count} cells | origin:({ox},{oy},{oz})");
    }

    // ── Procedural mesh rebuild ────────────────────────────────────────────
    void RebuildBodyMesh()
    {
        if (bodyMeshFilter == null) { UnityEngine.Debug.LogWarning("[VoxelBody] bodyMeshFilter null — assign Body Mesh Target."); return; }
        var mv = new List<Vector3>(); var mt = new List<int>(); var mn = new List<Vector3>();
        Vector3Int[] dirs = { Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down, new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1) };
        Vector3[] nv = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        Vector3[][] fc ={
            new[]{new Vector3(.5f,-.5f,-.5f),new Vector3(.5f,.5f,-.5f),new Vector3(.5f,.5f,.5f),new Vector3(.5f,-.5f,.5f)},
            new[]{new Vector3(-.5f,-.5f,.5f),new Vector3(-.5f,.5f,.5f),new Vector3(-.5f,.5f,-.5f),new Vector3(-.5f,-.5f,-.5f)},
            new[]{new Vector3(-.5f,.5f,-.5f),new Vector3(-.5f,.5f,.5f),new Vector3(.5f,.5f,.5f),new Vector3(.5f,.5f,-.5f)},
            new[]{new Vector3(-.5f,-.5f,.5f),new Vector3(-.5f,-.5f,-.5f),new Vector3(.5f,-.5f,-.5f),new Vector3(.5f,-.5f,.5f)},
            new[]{new Vector3(.5f,-.5f,.5f),new Vector3(.5f,.5f,.5f),new Vector3(-.5f,.5f,.5f),new Vector3(-.5f,-.5f,.5f)},
            new[]{new Vector3(-.5f,-.5f,-.5f),new Vector3(-.5f,.5f,-.5f),new Vector3(.5f,.5f,-.5f),new Vector3(.5f,-.5f,-.5f)},
        };
        float vs = bodyVoxelSize;
        foreach (var cell in bodyCells)
        { Vector3 center = CoarseToWorld(cell); for (int f = 0; f < 6; f++) { if (bodyOccupied.ContainsKey(cell + dirs[f])) continue; int b = mv.Count; foreach (var c in fc[f]) mv.Add(center + c * vs); mt.Add(b); mt.Add(b + 1); mt.Add(b + 2); mt.Add(b); mt.Add(b + 2); mt.Add(b + 3); for (int i = 0; i < 4; i++) mn.Add(nv[f]); } }
        var mesh = new Mesh { name = "BodyMesh", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(mv); mesh.SetTriangles(mt, 0); mesh.SetNormals(mn); mesh.RecalculateBounds();
        bodyMeshFilter.mesh = mesh;
        if (bodyMeshCollider != null) { bodyMeshCollider.sharedMesh = null; bodyMeshCollider.sharedMesh = mesh; }
        if (solidMeshCollider != null && solidMeshCollider != bodyMeshCollider) { solidMeshCollider.sharedMesh = null; solidMeshCollider.convex = false; solidMeshCollider.sharedMesh = mesh; }
        UnityEngine.Debug.Log($"[VoxelBody] Mesh rebuilt | verts:{mv.Count} tris:{mt.Count / 3} bodyCells:{bodyCells.Count}");
    }

    // ── Element spawn helper ───────────────────────────────────────────────
    private GameObject SpawnElement(Vector3 worldPos)
    { var v = Instantiate(elementPrefab, worldPos, Quaternion.identity, solidRoot); v.transform.localScale = Vector3.one * elementVoxelSize; foreach (var r in v.GetComponentsInChildren<Renderer>()) r.material.renderQueue = 2001; return v; }

    // ── Grid math ──────────────────────────────────────────────────────────
    public Vector3 CoarseToWorld(Vector3Int cell) => CoarseToWorld(cell, bodyVoxelSize);
    public static Vector3 CoarseToWorld(Vector3Int cell, float bvs) => new Vector3((cell.x + 0.5f) * bvs, (cell.y + 0.5f) * bvs, (cell.z + 0.5f) * bvs);

    public Vector3Int WorldToCoarseCell(Vector3 pos) => WorldToCoarseCell(pos, bodyVoxelSize);
    public static Vector3Int WorldToCoarseCell(Vector3 pos, float bvs) => new Vector3Int(Mathf.FloorToInt(pos.x / bvs), Mathf.FloorToInt(pos.y / bvs), Mathf.FloorToInt(pos.z / bvs));

    public Vector3 FineToWorld(Vector3Int cell) => FineToWorld(cell, elementVoxelSize);
    public static Vector3 FineToWorld(Vector3Int cell, float evs) => new Vector3((cell.x + 0.5f) * evs, (cell.y + 0.5f) * evs, (cell.z + 0.5f) * evs);

    public Vector3Int WorldToFineCell(Vector3 pos) => WorldToFineCell(pos, elementVoxelSize);
    public static Vector3Int WorldToFineCell(Vector3 pos, float evs) => new Vector3Int(Mathf.FloorToInt(pos.x / evs), Mathf.FloorToInt(pos.y / evs), Mathf.FloorToInt(pos.z / evs));

    public Vector3Int FineOriginOfCoarse(Vector3Int coarse) => FineOriginOfCoarse(coarse, bodyVoxelSize, elementVoxelSize);
    public static Vector3Int FineOriginOfCoarse(Vector3Int coarse, float bvs, float evs) { int r = Mathf.RoundToInt(bvs / evs); return coarse * r; }
    public int FinePerCoarse => Mathf.Max(1, Mathf.RoundToInt(bodyVoxelSize / elementVoxelSize));
    public Vector3 CellToWorld(Vector3Int cell) => CoarseToWorld(cell);
    public Vector3Int WorldToCell(Vector3 pos) => WorldToCoarseCell(pos);
    public static Vector3 SnapNormal(Vector3 n)
    { float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z); if (ax > ay && ax > az) return new Vector3(Mathf.Sign(n.x), 0, 0); if (ay > ax && ay > az) return new Vector3(0, Mathf.Sign(n.y), 0); return new Vector3(0, 0, Mathf.Sign(n.z)); }

    // ── Occupancy queries ──────────────────────────────────────────────────
    public bool IsBodyOccupied(Vector3Int coarseCell) => bodyOccupied.ContainsKey(coarseCell);
    public bool IsElementOccupied(Vector3Int fineCell) => elementOccupied.ContainsKey(fineCell);
    public bool IsOccupied(Vector3Int cell) => bodyOccupied.ContainsKey(cell);
    public bool IsBodyCell(Vector3Int cell) => bodyCells.Contains(cell);

    // ── Element cuboid helpers (V18) ───────────────────────────────────────
    private List<BoundsInt> GetElementCuboids()
    {
        if (elementCuboidsDirty)
        {
            var keys = new HashSet<Vector3Int>(elementOccupied.Keys);
            elementCuboids = GreedyMergeSet(keys);
            elementCuboidsDirty = false;
            UnityEngine.Debug.Log($"[VoxelBody] elementCuboids rebuilt: {elementCuboids.Count}");
        }
        return elementCuboids;
    }

    private static BoundsInt CellListBounds(List<Vector3Int> cells)
    {
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        foreach (var c in cells) { if (c.x < minX) minX = c.x; if (c.x > maxX) maxX = c.x; if (c.y < minY) minY = c.y; if (c.y > maxY) maxY = c.y; if (c.z < minZ) minZ = c.z; if (c.z > maxZ) maxZ = c.z; }
        return new BoundsInt(minX, minY, minZ, maxX - minX + 1, maxY - minY + 1, maxZ - minZ + 1);
    }

    // ── Element add/remove (fine grid) ────────────────────────────────────
    public int AddElementBatch(IEnumerable<Vector3Int> fineCells)
    {
        var cellList = new List<Vector3Int>(fineCells);
        var added = new List<UndoCellRecord>();
        var actuallyAdded = new List<Vector3Int>();
        foreach (var cell in cellList)
        { if (elementOccupied.ContainsKey(cell)) continue; Vector3 wp = FineToWorld(cell); elementOccupied[cell] = SpawnElement(wp); added.Add(new UndoCellRecord { cell = cell, worldPos = wp, wasBodyCell = false, isFineGrid = true }); actuallyAdded.Add(cell); }
        if (added.Count > 0)
        { BoundsInt cuboid = CellListBounds(actuallyAdded); elementCuboids.Add(cuboid); PushUndo(UndoOpType.Add, added, cuboid, true); }
        UnityEngine.Debug.Log($"[VoxelBody] Element batch added {added.Count}");
        return added.Count;
    }

    public int RemoveElementBatch(IEnumerable<Vector3Int> fineCells)
    {
        var removed = new List<UndoCellRecord>();
        foreach (var cell in fineCells)
        { if (!elementOccupied.TryGetValue(cell, out GameObject go)) continue; removed.Add(new UndoCellRecord { cell = cell, worldPos = FineToWorld(cell), wasBodyCell = false, isFineGrid = true }); elementOccupied.Remove(cell); if (go != null) Destroy(go); }
        if (removed.Count > 0) { elementCuboidsDirty = true; PushUndo(UndoOpType.Remove, removed, default, false); }
        UnityEngine.Debug.Log($"[VoxelBody] Element batch removed {removed.Count}");
        return removed.Count;
    }

    // ── Body remove (coarse grid) ──────────────────────────────────────────
    public int RemoveBodyBatch(IEnumerable<Vector3Int> coarseCells)
    {
        var removed = new List<UndoCellRecord>();
        foreach (var cell in coarseCells)
        { if (!bodyCells.Contains(cell)) continue; removed.Add(new UndoCellRecord { cell = cell, worldPos = CoarseToWorld(cell), wasBodyCell = true, isFineGrid = false }); bodyOccupied.Remove(cell); bodyCells.Remove(cell); removedBodyCells.Add(cell); }
        if (removed.Count > 0) { PushUndo(UndoOpType.Remove, removed, default, false); bodyMeshDirty = true; }
        UnityEngine.Debug.Log($"[VoxelBody] Body batch removed {removed.Count}");
        return removed.Count;
    }

    public GameObject AddVoxel(Vector3Int cell)
    { if (bodyOccupied.ContainsKey(cell)) return null; Vector3 wp = CoarseToWorld(cell); var go = SpawnElement(wp); bodyOccupied[cell] = go; PushUndo(UndoOpType.Add, new List<UndoCellRecord> { new UndoCellRecord { cell = cell, worldPos = wp, wasBodyCell = false, isFineGrid = false } }, default, false); return go; }

    // ── Undo ───────────────────────────────────────────────────────────────
    public int UndoCount => undoStack.Count;

    public bool UndoLastOperation()
    {
        if (undoStack.Count == 0) return false;
        UndoRecord rec = undoStack.Pop();
        bool anyBody = false;
        if (rec.op == UndoOpType.Add)
        {
            foreach (var cr in rec.cells)
            {
                if (cr.isFineGrid) { if (elementOccupied.TryGetValue(cr.cell, out GameObject go)) { elementOccupied.Remove(cr.cell); if (go != null) Destroy(go); } }
                else { if (bodyOccupied.TryGetValue(cr.cell, out GameObject go)) { bodyOccupied.Remove(cr.cell); bodyCells.Remove(cr.cell); if (!cr.wasBodyCell && go != null) Destroy(go); if (cr.wasBodyCell) anyBody = true; } }
            }
            if (rec.hasCuboid && elementCuboids.Count > 0) elementCuboids.RemoveAt(elementCuboids.Count - 1);
        }
        else
        {
            foreach (var cr in rec.cells)
            {
                if (cr.isFineGrid) { if (!elementOccupied.ContainsKey(cr.cell)) { elementOccupied[cr.cell] = SpawnElement(cr.worldPos); elementCuboidsDirty = true; } }
                else if (cr.wasBodyCell) { if (!bodyOccupied.ContainsKey(cr.cell)) { bodyOccupied[cr.cell] = null; bodyCells.Add(cr.cell); anyBody = true; removedBodyCells.Remove(cr.cell); } }
                else { if (!bodyOccupied.ContainsKey(cr.cell)) bodyOccupied[cr.cell] = SpawnElement(cr.worldPos); }
            }
        }
        if (anyBody) bodyMeshDirty = true;
        UnityEngine.Debug.Log($"[VoxelBody] Undo {rec.op} — {rec.cells.Count} cell(s)");
        return true;
    }

    private void PushUndo(UndoOpType op, List<UndoCellRecord> cells, BoundsInt cuboid, bool hasCuboid)
    {
        if (undoStack.Count >= UndoStackDepth) { var arr = undoStack.ToArray(); undoStack.Clear(); for (int i = UndoStackDepth - 2; i >= 0; i--) undoStack.Push(arr[i]); }
        undoStack.Push(new UndoRecord { op = op, cells = cells, elementCuboid = cuboid, hasCuboid = hasCuboid });
    }

    // ── Read-only access ───────────────────────────────────────────────────
    public int VoxelCount => bodyOccupied.Count + elementOccupied.Count;
    public int BodyCellCount => bodyCells.Count;
    public IEnumerable<Vector3Int> AllBodyCells => bodyCells;

    // ── Greedy Merge (works on any HashSet) ───────────────────────────────
    private List<BoundsInt> GreedyMergeSet(HashSet<Vector3Int> cells)
    {
        var result = new List<BoundsInt>(); var visited = new HashSet<Vector3Int>();
        var sorted = new List<Vector3Int>(cells);
        sorted.Sort(CompareCellsByZyx);
        foreach (var origin in sorted)
        {
            if (visited.Contains(origin)) continue;
            int dx = 1; while (cells.Contains(origin + new Vector3Int(dx, 0, 0)) && !visited.Contains(origin + new Vector3Int(dx, 0, 0))) dx++;
            int dy = 1; while (true) { bool ok = true; for (int xi = 0; xi < dx; xi++) { var c = origin + new Vector3Int(xi, dy, 0); if (!cells.Contains(c) || visited.Contains(c)) { ok = false; break; } } if (!ok) break; dy++; }
            int dz = 1; while (true) { bool ok = true; for (int xi = 0; xi < dx && ok; xi++) for (int yi = 0; yi < dy && ok; yi++) { var c = origin + new Vector3Int(xi, yi, dz); if (!cells.Contains(c) || visited.Contains(c)) ok = false; } if (!ok) break; dz++; }
            for (int xi = 0; xi < dx; xi++) for (int yi = 0; yi < dy; yi++) for (int zi = 0; zi < dz; zi++) visited.Add(origin + new Vector3Int(xi, yi, zi));
            result.Add(new BoundsInt(origin, new Vector3Int(dx, dy, dz)));
        }
        return result;
    }

    public List<BoundsInt> GreedyMerge()
    { var r = GreedyMergeSet(bodyCells); UnityEngine.Debug.Log($"[VoxelBody] GreedyMerge | bodyCells:{bodyCells.Count} → cuboids:{r.Count}"); return r; }

    // ── OBJ Export ────────────────────────────────────────────────────────
    public void ExportOBJ()
    {
        List<BoundsInt> merged = GreedyMerge();
        var sb = new StringBuilder(); int vBase = 1;
        System.Action<float, float, float, float, float, float> wc = (x0, y0, z0, x1, y1, z1) =>
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", x0, y0, z0));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", x1, y0, z0));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", x1, y1, z0));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", x0, y1, z0));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", x0, y0, z1));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", x1, y0, z1));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", x1, y1, z1));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", x0, y1, z1));
            int b = vBase;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "f {0} {1} {2} {3}", b + 0, b + 3, b + 2, b + 1));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "f {0} {1} {2} {3}", b + 4, b + 5, b + 6, b + 7));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "f {0} {1} {2} {3}", b + 0, b + 1, b + 5, b + 4));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "f {0} {1} {2} {3}", b + 2, b + 3, b + 7, b + 6));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "f {0} {1} {2} {3}", b + 0, b + 4, b + 7, b + 3));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "f {0} {1} {2} {3}", b + 1, b + 2, b + 6, b + 5));
            vBase += 8;
        };
        float bvs = bodyVoxelSize;
        foreach (var b in merged) wc(b.xMin * bvs, b.yMin * bvs, b.zMin * bvs, b.xMax * bvs, b.yMax * bvs, b.zMax * bvs);
        float evs = elementVoxelSize;
        foreach (var kvp in elementOccupied) wc(kvp.Key.x * evs, kvp.Key.y * evs, kvp.Key.z * evs, (kvp.Key.x + 1) * evs, (kvp.Key.y + 1) * evs, (kvp.Key.z + 1) * evs);
        Directory.CreateDirectory(Path.GetDirectoryName(ExportPath));
        File.WriteAllText(ExportPath, sb.ToString());
        UnityEngine.Debug.Log($"[VoxelBody] OBJ exported → {ExportPath}");
    }

    // ── STEP Export (V19) ─────────────────────────────────────────────────
    // Voids = all cells in bodyCells bbox NOT in bodyCells.
    // This correctly handles OBJ-voxelised shapes (exterior = never in bodyCells)
    // AND manually CUT box bodies (removed cells already absent from bodyCells).
    public void ExportSTEPFile()
    {
        if (bodyCells.Count == 0) { UnityEngine.Debug.LogError("[VoxelBody] STEP aborted — no body cells."); return; }
        string F6(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

        // Bounding box from bodyCells only
        Vector3Int bMin = new Vector3Int(int.MaxValue, int.MaxValue, int.MaxValue);
        Vector3Int bMax = new Vector3Int(int.MinValue, int.MinValue, int.MinValue);
        foreach (var c in bodyCells)
        {
            if (c.x < bMin.x) bMin.x = c.x; if (c.x > bMax.x) bMax.x = c.x;
            if (c.y < bMin.y) bMin.y = c.y; if (c.y > bMax.y) bMax.y = c.y;
            if (c.z < bMin.z) bMin.z = c.z; if (c.z > bMax.z) bMax.z = c.z;
        }

        float bvs = bodyVoxelSize, evs = elementVoxelSize;
        float bbX = bMin.x * bvs, bbY = bMin.y * bvs, bbZ = bMin.z * bvs;
        float bbSX = (bMax.x - bMin.x + 1) * bvs, bbSY = (bMax.y - bMin.y + 1) * bvs, bbSZ = (bMax.z - bMin.z + 1) * bvs;

        // Compute voids: every cell in bbox that is NOT a body cell
        var voidCells = new HashSet<Vector3Int>();
        for (int xi = bMin.x; xi <= bMax.x; xi++)
            for (int yi = bMin.y; yi <= bMax.y; yi++)
                for (int zi = bMin.z; zi <= bMax.z; zi++)
                {
                    var c = new Vector3Int(xi, yi, zi);
                    if (!bodyCells.Contains(c)) voidCells.Add(c);
                }

        List<BoundsInt> voids = GreedyMergeSet(voidCells);
        List<BoundsInt> addCubs = GetElementCuboids();

        string tempInput = Path.Combine(Path.GetTempPath(), "voxel_cuboids.txt");
        var sb = new StringBuilder();
        sb.Append("BBOX ");
        sb.Append(F6(bbX)); sb.Append(' ');
        sb.Append(F6(bbY)); sb.Append(' ');
        sb.Append(F6(bbZ)); sb.Append(' ');
        sb.Append(F6(bbSX)); sb.Append(' ');
        sb.Append(F6(bbSY)); sb.Append(' ');
        sb.Append(F6(bbSZ)); sb.Append('\n');

        sb.Append("CUT");
        foreach (var v in voids)
        {
            sb.Append(' '); sb.Append(F6(v.xMin * bvs));
            sb.Append(' '); sb.Append(F6(v.yMin * bvs));
            sb.Append(' '); sb.Append(F6(v.zMin * bvs));
            sb.Append(' '); sb.Append(F6(v.size.x * bvs));
            sb.Append(' '); sb.Append(F6(v.size.y * bvs));
            sb.Append(' '); sb.Append(F6(v.size.z * bvs));
        }
        sb.Append('\n');

        sb.Append("ADD");
        foreach (var a in addCubs)
        {
            sb.Append(' '); sb.Append(F6(a.xMin * evs));
            sb.Append(' '); sb.Append(F6(a.yMin * evs));
            sb.Append(' '); sb.Append(F6(a.zMin * evs));
            sb.Append(' '); sb.Append(F6(a.size.x * evs));
            sb.Append(' '); sb.Append(F6(a.size.y * evs));
            sb.Append(' '); sb.Append(F6(a.size.z * evs));
        }
        sb.Append('\n');

        File.WriteAllText(tempInput, sb.ToString());
        UnityEngine.Debug.Log($"[VoxelBody] STEP export | bbox:{bbSX}x{bbSY}x{bbSZ} voids:{voids.Count} addCuboids:{addCubs.Count}");

        if (!File.Exists(StepExePath)) { UnityEngine.Debug.LogError($"[VoxelBody] VoxelSTEP.exe not found: {StepExePath}"); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(StepExportPath));
        var psi = new ProcessStartInfo(StepExePath, $"\"{tempInput}\" \"{StepExportPath}\"") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        int timeoutMs = ComputeStepExportTimeoutMilliseconds(voids.Count, addCubs.Count);
        UnityEngine.Debug.Log($"[VoxelBody] STEP timeout budget: {timeoutMs / 1000f:0.#}s");
        using (var proc = Process.Start(psi))
        {
            bool exited = proc.WaitForExit(timeoutMs);
            if (!exited)
            {
                UnityEngine.Debug.LogError($"[VoxelBody] STEP failed — VoxelSTEP.exe timed out after {timeoutMs / 1000f:0.#}s");
                try { proc.Kill(); } catch { }
                return;
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            int code = proc.ExitCode;
            if (!string.IsNullOrWhiteSpace(stdout))
                UnityEngine.Debug.Log($"[VoxelSTEP] {stdout.Trim()}");
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                if (code == 0)
                    UnityEngine.Debug.Log($"[VoxelSTEP] {stderr.Trim()}");
                else
                    UnityEngine.Debug.LogWarning($"[VoxelSTEP] {stderr.Trim()}");
            }
            switch (code)
            {
                case 0: UnityEngine.Debug.Log($"[VoxelBody] STEP exported → {StepExportPath}"); break;
                case -1: UnityEngine.Debug.LogError("[VoxelBody] STEP failed — null/empty input (code -1)"); break;
                case -2: UnityEngine.Debug.LogError("[VoxelBody] STEP failed — Boolean operation failed (code -2)"); break;
                case -3: UnityEngine.Debug.LogError("[VoxelBody] STEP failed — file write failed (code -3)"); break;
                case -4: UnityEngine.Debug.LogError("[VoxelBody] STEP failed — OCCT exception (code -4)"); break;
                default: UnityEngine.Debug.LogError($"[VoxelBody] STEP failed — code:{code}"); break;
            }
        }
    }

    public static int ComputeStepExportTimeoutMilliseconds(int voidCount, int addCuboidCount)
    {
        float timeoutSeconds = 60f;
        timeoutSeconds += Mathf.Max(0, voidCount) * 0.75f;
        timeoutSeconds += Mathf.Max(0, addCuboidCount) * 10f;
        timeoutSeconds = Mathf.Clamp(timeoutSeconds, 60f, 900f);
        return Mathf.CeilToInt(timeoutSeconds * 1000f);
    }
}
