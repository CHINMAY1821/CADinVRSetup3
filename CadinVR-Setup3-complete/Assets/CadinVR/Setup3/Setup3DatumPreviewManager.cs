using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Diagnostics;
using System.Text;
using UnityEngine.Rendering;

[System.Serializable]
public struct Setup3DatumOperation
{
    public Vector3 origin;
    public Vector3 normalAxis;
    public Vector3 localX;
    public float width, height, depth;
    public bool isCut;
    public bool useMeshOperand;
    public bool usePolyPrism;
    public bool useRoundPrimitive;
    public float roundRadius;
    public List<Vector3> polygonPoints;
    public string meshOperandPath;
    public string meshOperandInlineObj;
    public bool hasRoundReference;
    public Vector3 roundReferenceSurfaceCenter;
    public Vector3 roundReferenceTopCenter;
    public float roundReferenceRequestedDepth;
}

[System.Serializable]
public class Setup3DatumOperationList
{
    public List<Setup3DatumOperation> ops = new List<Setup3DatumOperation>();
}

[System.Serializable]
public struct Setup3DatumRoundReferenceRecord
{
    public int opIndex;
    public string opKind;
    public float radiusMm;
    public float requestedDepthMm;
    public float booleanDepthMm;
    public Vector3 surfaceCenter;
    public Vector3 topCenter;
    public Vector3 axisDirection;
    public Vector3 localX;
}

[System.Serializable]
public class Setup3DatumRoundReferenceExport
{
    public List<Setup3DatumRoundReferenceRecord> rounds = new List<Setup3DatumRoundReferenceRecord>();
}

public class Setup3DatumPreviewManager : MonoBehaviour
{
    private const string PersistentSubfolder = "setup3";

    struct SourceRendererState
    {
        public Renderer renderer;
        public bool wasEnabled;
    }

    [Header("Setup 3 Source")]
    public GameObject sourceBodyPrefab;
    public bool followVoxelBodySource = true;
    public string sourceMeshPath = "";
    public Vector3Int bodySize = new Vector3Int(100, 100, 100);

    private static string StepExePath =>
        Path.Combine(Application.streamingAssetsPath, "VoxelSTEP", "VoxelSTEP.exe");

    private string baseStepPath;
    private string currentStepPath;
    private string previewObjPath;
    private string operationTxtPath;
    private string saveJsonPath;
    private string baseMeshStlPath;

    private GameObject runtimeBodyRoot;
    private GameObject runtimeMeshObject;
    private MeshFilter runtimeMeshFilter;
    private MeshRenderer runtimeMeshRenderer;
    private MeshCollider runtimeMeshCollider;
    private GameObject resolvedSourceBodyObject;
    private readonly List<Material> runtimePreviewMaterials = new List<Material>();
    private readonly List<SourceRendererState> hiddenSourceRenderers = new List<SourceRendererState>();

    private readonly List<Setup3DatumOperation> operationList = new List<Setup3DatumOperation>();
    private Mesh cachedBasePreviewMesh;
    private bool baseStepReady = false;
    private bool baseStepGenerating = false;
    private bool rebuildRunning = false;
    private bool rebuildQueued = false;
    private string activeSourceFingerprint = string.Empty;
    private string lastSuccessfulRebuildSignature;
    private Process activeVoxelStepProcess;
    private string activeRebuildOperationSignature;
    private float activeVoxelStepDeadlineRealtime = -1f;
    private float activeVoxelStepExitedRealtime = -1f;
    private bool activeVoxelStepCancelledByTimeout = false;
    private string lastRebuildFailureText;

    public int OperationCount => operationList.Count;
    public string ExportObjPath => GetExportPath("_setup3_datum.obj");
    public string ExportStepPath => GetExportPath("_setup3_datum.step");
    public bool IsPreparingBase => baseStepGenerating;
    public bool IsRebuildingPreview => rebuildRunning;
    public bool HasQueuedRebuild => rebuildQueued;
    public string RebuildStatusText
    {
        get
        {
            if (baseStepGenerating)
                return "Preparing base STEP";
            if (rebuildRunning)
                return "Rebuilding preview";
            if (rebuildQueued)
                return "Rebuild queued";
            if (operationList.Count > 0 && HasCurrentSuccessfulRebuild())
                return "Preview updated";
            if (operationList.Count > 0 && !string.IsNullOrEmpty(lastRebuildFailureText))
                return lastRebuildFailureText;
            if (operationList.Count > 0)
                return "Waiting for rebuild";
            return "Ready";
        }
    }

    void Awake()
    {
        string pd = Path.Combine(Application.persistentDataPath, PersistentSubfolder);
        Directory.CreateDirectory(pd);
        baseStepPath = Path.Combine(pd, "base_setup3_datum.step");
        currentStepPath = Path.Combine(pd, "current_setup3_datum.step");
        previewObjPath = Path.Combine(pd, "preview_setup3_datum.obj");
        operationTxtPath = Path.Combine(pd, "setup3_datum_ops.txt");
        saveJsonPath = Path.Combine(pd, "setup3_datum_ops.json");
        baseMeshStlPath = Path.Combine(pd, "base_setup3_datum_input.stl");
    }

    void OnDestroy()
    {
        CancelActiveVoxelStepProcess("manager destroyed");
        baseStepGenerating = false;
        rebuildRunning = false;
        rebuildQueued = false;
        RestoreSourceBodyVisuals();
        DestroyRuntimePreviewMaterials();
        if (cachedBasePreviewMesh != null)
            Destroy(cachedBasePreviewMesh);
        if (runtimeBodyRoot != null)
            Destroy(runtimeBodyRoot);
    }

    void Update()
    {
        RecoverCompletedRebuildIfCallbackWasLost();
        AdoptCompletedRebuildArtifactsIfIdle();
    }

    public void ActivateSetup()
    {
        RefreshSourceIfNeeded();
        EnsureRuntimeBodyInstance();
        HideSourceBodyVisuals();
        if (runtimeBodyRoot != null)
            runtimeBodyRoot.SetActive(true);
        CacheBasePreviewMesh();
        RestoreBaseMesh();
    }

    public void DeactivateSetup()
    {
        RestoreSourceBodyVisuals();
        if (runtimeBodyRoot != null)
            runtimeBodyRoot.SetActive(false);
    }

    public string GetResolvedSourceMeshPath()
    {
        string candidatePath = sourceMeshPath;

        if (followVoxelBodySource)
        {
            VoxelBody voxelBody = ResolveSourceVoxelBody();
            if (voxelBody != null)
            {
                string voxelBodyPath = voxelBody.GetResolvedSourceMeshPath();
                if (!string.IsNullOrEmpty(voxelBodyPath))
                    candidatePath = voxelBodyPath;
            }
        }
        else if (string.IsNullOrEmpty(candidatePath))
        {
            VoxelBody voxelBody = FindFirstObjectByType<VoxelBody>();
            if (voxelBody != null)
                candidatePath = voxelBody.GetResolvedSourceMeshPath();
        }

        if (string.IsNullOrEmpty(candidatePath))
            return candidatePath;

        if (Path.GetExtension(candidatePath).Equals(".obj", System.StringComparison.OrdinalIgnoreCase))
        {
            string siblingStl = Path.ChangeExtension(candidatePath, ".stl");
            if (File.Exists(siblingStl))
                return siblingStl;
        }

        return candidatePath;
    }

    VoxelBody ResolveSourceVoxelBody()
    {
        if (sourceBodyPrefab != null)
        {
            VoxelBody direct = sourceBodyPrefab.GetComponent<VoxelBody>();
            if (direct != null)
                return direct;

            VoxelBody parent = sourceBodyPrefab.GetComponentInParent<VoxelBody>();
            if (parent != null)
                return parent;

            VoxelBody child = sourceBodyPrefab.GetComponentInChildren<VoxelBody>(true);
            if (child != null)
                return child;
        }

        ModeManager[] managers = FindObjectsByType<ModeManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (ModeManager manager in managers)
        {
            if (manager != null && manager.sourceBodyPrefab != null)
            {
                VoxelBody setupBody = manager.sourceBodyPrefab.GetComponent<VoxelBody>();
                if (setupBody != null)
                    return setupBody;
            }
        }

        return FindFirstObjectByType<VoxelBody>();
    }

    public void PrepareBaseAsync()
    {
        RefreshSourceIfNeeded();
        EnsureRuntimeBodyInstance();
        if (baseStepReady || baseStepGenerating)
            return;

        StartCoroutine(GenerateBaseStepAsync());
    }

    IEnumerator GenerateBaseStepAsync()
    {
        string src = GetResolvedSourceMeshPath();
        CacheBasePreviewMesh();
        baseStepReady = false;
        baseStepGenerating = true;
        DeleteIfExists(baseStepPath);
        DeleteIfExists(currentStepPath);
        DeleteIfExists(previewObjPath);

        int exitCode = -1;
        bool usedAuthoritativeSource = false;
        if (!string.IsNullOrEmpty(src) && File.Exists(src) &&
            Path.GetExtension(src).ToLowerInvariant() == ".stl")
        {
            usedAuthoritativeSource = true;
            yield return RunVoxelSTEPAsync(
                $"--import \"{src}\" \"{baseStepPath}\"",
                code => exitCode = code);
        }
        else if (ExportBaseMeshAsAsciiStl(baseMeshStlPath))
        {
            UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Base mesh STL exported: {baseMeshStlPath}");
            yield return RunVoxelSTEPAsync(
                $"--import \"{baseMeshStlPath}\" \"{baseStepPath}\"",
                code => exitCode = code);
        }

        if (usedAuthoritativeSource && exitCode != 0)
        {
            baseStepGenerating = false;
            UnityEngine.Debug.LogError("[Setup3DatumPreviewManager] Authoritative STL source import failed — refusing box fallback");
            RestoreBaseMesh();
            yield break;
        }

        if (exitCode != 0)
        {
            UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] Base import failed — falling back to box base");
            yield return RunVoxelSTEPAsync(
                $"--box {bodySize.x} {bodySize.y} {bodySize.z} \"{baseStepPath}\"",
                code => exitCode = code);
        }

        baseStepReady = (exitCode == 0 && File.Exists(baseStepPath));
        if (!baseStepReady)
        {
            baseStepGenerating = false;
            UnityEngine.Debug.LogError("[Setup3DatumPreviewManager] Failed to generate datum base STEP");
            RestoreBaseMesh();
            yield break;
        }

        File.Copy(baseStepPath, currentStepPath, overwrite: true);
        baseStepGenerating = false;
        lastSuccessfulRebuildSignature = operationList.Count == 0 ? BuildOperationSignature() : null;

        if (rebuildQueued || operationList.Count > 0)
        {
            rebuildQueued = false;
            TriggerRebuild();
        }
    }

    public void AddOperation(Setup3DatumOperation op)
    {
        if (ContainsEquivalentOperation(op))
        {
            UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Skipped duplicate op: {DescribeOperation(op)}");
            return;
        }

        operationList.Add(op);
        InvalidateSuccessfulRebuild();
        UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Enqueued op#{operationList.Count - 1}: {DescribeOperation(op)}");
        TriggerRebuild();
    }

    public void AddOperations(IEnumerable<Setup3DatumOperation> ops)
    {
        if (ops == null)
            return;

        int before = operationList.Count;
        var seen = new HashSet<string>();
        for (int i = 0; i < operationList.Count; i++)
            seen.Add(BuildSingleOperationSignature(operationList[i]));

        foreach (Setup3DatumOperation op in ops)
        {
            string signature = BuildSingleOperationSignature(op);
            if (!seen.Add(signature))
            {
                UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Skipped duplicate op: {DescribeOperation(op)}");
                continue;
            }

            operationList.Add(op);
            UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Enqueued op#{operationList.Count - 1}: {DescribeOperation(op)}");
        }

        if (operationList.Count != before)
        {
            InvalidateSuccessfulRebuild();
            TriggerRebuild();
        }
    }

    bool ContainsEquivalentOperation(Setup3DatumOperation op)
    {
        string signature = BuildSingleOperationSignature(op);
        for (int i = 0; i < operationList.Count; i++)
            if (BuildSingleOperationSignature(operationList[i]) == signature)
                return true;
        return false;
    }

    public void Undo()
    {
        if (operationList.Count == 0) return;
        operationList.RemoveAt(operationList.Count - 1);
        InvalidateSuccessfulRebuild();
        TriggerRebuild();
    }

    public void Save()
    {
        var data = new Setup3DatumOperationList { ops = new List<Setup3DatumOperation>(operationList.Count) };
        for (int i = 0; i < operationList.Count; i++)
        {
            Setup3DatumOperation op = operationList[i];
            op.meshOperandPath = NormalizeMeshOperandPath(op.meshOperandPath);
            op.meshOperandInlineObj = null;

            if (op.useMeshOperand)
            {
                op.meshOperandInlineObj = TryReadMeshOperandText(op.meshOperandPath);
                if (string.IsNullOrEmpty(op.meshOperandInlineObj))
                {
                    UnityEngine.Debug.LogWarning(
                        $"[Setup3DatumPreviewManager] Save op #{i} could not inline mesh operand '{op.meshOperandPath}' — load recovery will depend on the file still existing");
                }
            }

            data.ops.Add(op);
        }

        File.WriteAllText(saveJsonPath, JsonUtility.ToJson(data, true));
        UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Saved {operationList.Count} ops");
    }

    static string DescribeOperation(Setup3DatumOperation op)
    {
        if (op.usePolyPrism)
        {
            string kind = op.isCut ? "CUT_POLYPRISM" : "ADD_POLYPRISM";
            int pointCount = op.polygonPoints != null ? op.polygonPoints.Count : 0;
            return $"{kind} points={pointCount} normal=({op.normalAxis.x:F3},{op.normalAxis.y:F3},{op.normalAxis.z:F3}) depth={op.depth:F3}";
        }

        if (op.useMeshOperand)
        {
            string kind = op.isCut ? "CUT_MESH" : "ADD_MESH";
            string meshName = string.IsNullOrEmpty(op.meshOperandPath) ? "<missing>" : Path.GetFileName(op.meshOperandPath);
            return $"{kind} path={meshName}";
        }

        if (op.useRoundPrimitive)
        {
            string roundOpName = op.isCut ? "CUT_CYLINDER" : "ADD_CYLINDER";
            return $"{roundOpName} origin=({op.origin.x:F3},{op.origin.y:F3},{op.origin.z:F3}) normal=({op.normalAxis.x:F3},{op.normalAxis.y:F3},{op.normalAxis.z:F3}) radius={op.roundRadius:F3} depth={op.depth:F3}";
        }

        string orientedOpName = op.isCut ? "CUT_ORIENTED" : "ADD_ORIENTED";
        return $"{orientedOpName} origin=({op.origin.x:F3},{op.origin.y:F3},{op.origin.z:F3}) normal=({op.normalAxis.x:F3},{op.normalAxis.y:F3},{op.normalAxis.z:F3}) size=({op.width:F3},{op.height:F3},{op.depth:F3})";
    }

    public void Load()
    {
        if (!File.Exists(saveJsonPath)) return;
        InvalidateSuccessfulRebuild();
        Setup3DatumOperationList data = null;
        try
        {
            data = JsonUtility.FromJson<Setup3DatumOperationList>(File.ReadAllText(saveJsonPath));
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[Setup3DatumPreviewManager] Load skipped — save file is invalid: {ex.Message}");
            return;
        }

        operationList.Clear();
        if (data?.ops != null)
        {
            for (int i = 0; i < data.ops.Count; i++)
            {
                Setup3DatumOperation op = data.ops[i];
                if (op.useMeshOperand && !TryRecoverMeshOperandForLoad(ref op, i))
                {
                    UnityEngine.Debug.LogWarning($"[Setup3DatumPreviewManager] Skipping mesh op #{i} during load — operand could not be recovered");
                    continue;
                }

                operationList.Add(op);
            }
        }
        TriggerRebuild();
    }

    public void Reset()
    {
        CancelActiveVoxelStepProcess("Setup 3 reset was requested");
        operationList.Clear();
        baseStepGenerating = false;
        rebuildRunning = false;
        rebuildQueued = false;
        activeRebuildOperationSignature = null;
        activeVoxelStepCancelledByTimeout = false;
        lastRebuildFailureText = null;
        if (baseStepReady && File.Exists(baseStepPath))
            File.Copy(baseStepPath, currentStepPath, overwrite: true);
        DeleteIfExists(previewObjPath);
        lastSuccessfulRebuildSignature = BuildOperationSignature();
        RestoreBaseMesh();
        UnityEngine.Debug.Log("[Setup3DatumPreviewManager] Reset — operations cleared");
    }

    public void ExportCurrentOBJ()
    {
        EnsureRuntimeBodyInstance();
        Directory.CreateDirectory(Path.GetDirectoryName(ExportObjPath));

        if (!CanUseRuntimeMeshObjExportSource(operationList.Count, HasCurrentSuccessfulRebuild()))
        {
            UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] OBJ export skipped — current incremental rebuild is stale or failed");
            return;
        }

        if (WriteCurrentMeshAsObj(ExportObjPath))
        {
            WriteRoundReferenceSidecars(ExportObjPath);
            UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] OBJ exported → {ExportObjPath}");
            return;
        }

        if (CanUsePreviewObjExportFallback(operationList.Count, HasCurrentSuccessfulRebuild(), File.Exists(previewObjPath)))
        {
            File.Copy(previewObjPath, ExportObjPath, overwrite: true);
            WriteRoundReferenceSidecars(ExportObjPath);
            UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] OBJ exported → {ExportObjPath}");
            return;
        }

        UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] OBJ export skipped — no runtime body mesh available");
    }

    public void ExportCurrentSTEP()
    {
        if (!EnsureBaseStepReady())
        {
            UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] STEP export deferred — base STEP still preparing");
            return;
        }

        bool canUseCurrentStep = CanUseCurrentStepExportSource(operationList.Count, HasCurrentSuccessfulRebuild(), File.Exists(currentStepPath));
        string source = canUseCurrentStep
            ? currentStepPath
            : baseStepPath;

        if (operationList.Count > 0 && !canUseCurrentStep)
        {
            UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] STEP export skipped — current incremental rebuild is stale or failed");
            return;
        }

        if (!File.Exists(source))
        {
            UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] STEP export skipped — no STEP body available");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ExportStepPath));
        File.Copy(source, ExportStepPath, overwrite: true);
        WriteRoundReferenceSidecars(ExportStepPath);
        UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] STEP exported → {ExportStepPath}");
    }

    void TriggerRebuild()
    {
        if (rebuildRunning)
        {
            rebuildQueued = true;
            return;
        }

        if (!EnsureBaseStepReady())
        {
            rebuildQueued = true;
            return;
        }

        if (operationList.Count == 0)
        {
            File.Copy(baseStepPath, currentStepPath, overwrite: true);
            DeleteIfExists(previewObjPath);
            lastSuccessfulRebuildSignature = BuildOperationSignature();
            lastRebuildFailureText = null;
            WriteCurrentRoundReferenceSidecars();
            RestoreBaseMesh();
            return;
        }

        DeleteIfExists(currentStepPath);
        DeleteIfExists(previewObjPath);
        lastRebuildFailureText = null;
        WriteOperationTxt();

        string args = $"--incremental \"{baseStepPath}\" \"{operationTxtPath}\" \"{currentStepPath}\" \"{previewObjPath}\"";
        activeRebuildOperationSignature = BuildOperationSignature();
        rebuildRunning = true;
        StartCoroutine(RunIncrementalAsync(args, activeRebuildOperationSignature));
    }

    void WriteOperationTxt()
    {
        File.WriteAllText(operationTxtPath, BuildOperationTxtContent());
    }

    string BuildOperationTxtContent()
    {
        var sb = new StringBuilder(operationList.Count * 160);
        foreach (var op in operationList)
        {
            if (op.usePolyPrism)
            {
                string kw = op.isCut ? "CUT_POLYPRISM" : "ADD_POLYPRISM";
                List<Vector3> points = op.polygonPoints;
                if (points == null || points.Count < 3)
                    continue;

                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "{0} {1}  {2:F4} {3:F4} {4:F4}  {5:F4}",
                    kw,
                    points.Count,
                    op.normalAxis.x, op.normalAxis.y, op.normalAxis.z,
                    op.depth));
                for (int i = 0; i < points.Count; i++)
                {
                    Vector3 p = points[i];
                    sb.Append(string.Format(CultureInfo.InvariantCulture,
                        "  {0:F4} {1:F4} {2:F4}",
                        p.x, p.y, p.z));
                }
                sb.AppendLine();
            }
            else if (op.useMeshOperand)
            {
                string kw = op.isCut ? "CUT_MESH" : "ADD_MESH";
                string meshPath = NormalizeMeshOperandPath(op.meshOperandPath);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} \"{1}\"",
                    kw,
                    meshPath.Replace("\"", "\\\"")));
            }
            else if (op.useRoundPrimitive)
            {
                string kw = op.isCut ? "CUT_CYLINDER" : "ADD_CYLINDER";
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:F4} {2:F4} {3:F4}  {4:F4} {5:F4} {6:F4}  {7:F4} {8:F4} {9:F4}  {10:F4} {11:F4}",
                    kw,
                    op.origin.x, op.origin.y, op.origin.z,
                    op.normalAxis.x, op.normalAxis.y, op.normalAxis.z,
                    op.localX.x, op.localX.y, op.localX.z,
                    op.roundRadius, op.depth));
            }
            else
            {
                string kw = op.isCut ? "CUT_ORIENTED" : "ADD_ORIENTED";
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} {1:F4} {2:F4} {3:F4}  {4:F4} {5:F4} {6:F4}  {7:F4} {8:F4} {9:F4}  {10:F4} {11:F4} {12:F4}",
                    kw,
                    op.origin.x, op.origin.y, op.origin.z,
                    op.normalAxis.x, op.normalAxis.y, op.normalAxis.z,
                    op.localX.x, op.localX.y, op.localX.z,
                    op.width, op.height, op.depth));
            }
        }

        return sb.ToString();
    }

    static string NormalizeMeshOperandPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        return path.Replace('\\', '/');
    }

    string BuildOperationSignature()
    {
        if (operationList.Count == 0)
            return string.Empty;

        var sb = new StringBuilder(operationList.Count * 96);
        foreach (Setup3DatumOperation op in operationList)
        {
            if (op.usePolyPrism)
            {
                sb.Append(op.isCut ? "CUT_POLYPRISM|" : "ADD_POLYPRISM|");
                sb.Append(op.normalAxis.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.normalAxis.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.normalAxis.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.depth.ToString("F4", CultureInfo.InvariantCulture));
                if (op.polygonPoints != null)
                {
                    for (int i = 0; i < op.polygonPoints.Count; i++)
                    {
                        Vector3 p = op.polygonPoints[i];
                        sb.Append('|').Append(p.x.ToString("F4", CultureInfo.InvariantCulture));
                        sb.Append('|').Append(p.y.ToString("F4", CultureInfo.InvariantCulture));
                        sb.Append('|').Append(p.z.ToString("F4", CultureInfo.InvariantCulture));
                    }
                }
            }
            else if (op.useMeshOperand)
            {
                sb.Append(op.isCut ? "CUT_MESH|" : "ADD_MESH|");
                sb.Append(NormalizeMeshOperandPath(op.meshOperandPath));
            }
            else if (op.useRoundPrimitive)
            {
                sb.Append(op.isCut ? "CUT_CYLINDER|" : "ADD_CYLINDER|");
                sb.Append(op.origin.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.origin.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.origin.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.normalAxis.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.normalAxis.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.normalAxis.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.localX.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.localX.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.localX.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.roundRadius.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.depth.ToString("F4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(op.isCut ? "CUT_ORIENTED|" : "ADD_ORIENTED|");
                sb.Append(op.origin.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.origin.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.origin.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.normalAxis.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.normalAxis.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.normalAxis.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.localX.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.localX.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.localX.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.width.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.height.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(op.depth.ToString("F4", CultureInfo.InvariantCulture));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    static string BuildSingleOperationSignature(Setup3DatumOperation op)
    {
        var sb = new StringBuilder(96);
        if (op.usePolyPrism)
        {
            sb.Append(op.isCut ? "CUT_POLYPRISM|" : "ADD_POLYPRISM|");
            sb.Append(op.normalAxis.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.normalAxis.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.normalAxis.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.depth.ToString("F4", CultureInfo.InvariantCulture));
            if (op.polygonPoints != null)
            {
                for (int i = 0; i < op.polygonPoints.Count; i++)
                {
                    Vector3 p = op.polygonPoints[i];
                    sb.Append('|').Append(p.x.ToString("F4", CultureInfo.InvariantCulture));
                    sb.Append('|').Append(p.y.ToString("F4", CultureInfo.InvariantCulture));
                    sb.Append('|').Append(p.z.ToString("F4", CultureInfo.InvariantCulture));
                }
            }
        }
        else if (op.useMeshOperand)
        {
            sb.Append(op.isCut ? "CUT_MESH|" : "ADD_MESH|");
            sb.Append(NormalizeMeshOperandPath(op.meshOperandPath));
        }
        else if (op.useRoundPrimitive)
        {
            sb.Append(op.isCut ? "CUT_CYLINDER|" : "ADD_CYLINDER|");
            sb.Append(op.origin.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.origin.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.origin.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.normalAxis.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.normalAxis.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.normalAxis.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.localX.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.localX.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.localX.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.roundRadius.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.depth.ToString("F4", CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append(op.isCut ? "CUT_ORIENTED|" : "ADD_ORIENTED|");
            sb.Append(op.origin.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.origin.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.origin.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.normalAxis.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.normalAxis.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.normalAxis.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.localX.x.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.localX.y.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.localX.z.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.width.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.height.ToString("F4", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(op.depth.ToString("F4", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    bool HasCurrentSuccessfulRebuild()
    {
        return lastSuccessfulRebuildSignature != null &&
               string.Equals(lastSuccessfulRebuildSignature, BuildOperationSignature(), System.StringComparison.Ordinal);
    }

    public static bool CanUseCurrentStepExportSource(int operationCount, bool rebuildMatchesOperations, bool currentStepExists)
    {
        return operationCount > 0 && rebuildMatchesOperations && currentStepExists;
    }

    public static bool CanUsePreviewObjExportFallback(int operationCount, bool rebuildMatchesOperations, bool previewObjExists)
    {
        return operationCount > 0 && rebuildMatchesOperations && previewObjExists;
    }

    public static bool CanUseRuntimeMeshObjExportSource(int operationCount, bool rebuildMatchesOperations)
    {
        return operationCount == 0 || rebuildMatchesOperations;
    }

    void InvalidateSuccessfulRebuild()
    {
        lastSuccessfulRebuildSignature = null;
    }

    static string TryReadMeshOperandText(string meshPath)
    {
        if (string.IsNullOrEmpty(meshPath))
            return null;

        try
        {
            return File.Exists(meshPath) ? File.ReadAllText(meshPath) : null;
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[Setup3DatumPreviewManager] Failed to inline mesh operand '{meshPath}': {ex.Message}");
            return null;
        }
    }

    bool TryRecoverMeshOperandForLoad(ref Setup3DatumOperation op, int opIndex)
    {
        op.meshOperandPath = NormalizeMeshOperandPath(op.meshOperandPath);
        if (!string.IsNullOrEmpty(op.meshOperandPath) && File.Exists(op.meshOperandPath))
            return true;

        if (string.IsNullOrEmpty(op.meshOperandInlineObj))
            return false;

        string recoveredPath = BuildRecoveredMeshOperandPath(Path.Combine(Application.persistentDataPath, PersistentSubfolder), opIndex);
        try
        {
            string dir = Path.GetDirectoryName(recoveredPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(recoveredPath, op.meshOperandInlineObj);
            op.meshOperandPath = NormalizeMeshOperandPath(recoveredPath);
            return true;
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[Setup3DatumPreviewManager] Failed to recover mesh operand #{opIndex}: {ex.Message}");
            return false;
        }
    }

    public static string BuildRecoveredMeshOperandPath(string persistentRoot, int opIndex)
    {
        string safeRoot = string.IsNullOrEmpty(persistentRoot) ? "." : persistentRoot;
        return Path.Combine(safeRoot, $"setup3_loaded_meshop_{opIndex:D3}.obj");
    }

    bool LoadPreviewMesh()
    {
        if (!File.Exists(previewObjPath)) return false;
        EnsureRuntimeBodyInstance();
        if (runtimeMeshFilter == null) return false;

        Mesh mesh = ParseSimpleObj(previewObjPath);
        if (mesh == null)
        {
            long fileBytes = 0;
            try { fileBytes = new FileInfo(previewObjPath).Length; } catch { }
            UnityEngine.Debug.LogWarning($"[Setup3DatumPreviewManager] OBJ parse returned null: {previewObjPath} ({fileBytes} bytes)");
            return false;
        }

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        SetRuntimeMesh(mesh);

        UnityEngine.Debug.Log("[Setup3DatumPreviewManager] Preview mesh loaded");
        return true;
    }

    IEnumerator RunIncrementalAsync(string args, string expectedSignature)
    {
        int exitCode = -1;
        yield return RunVoxelSTEPAsync(args, code => exitCode = code);

        rebuildRunning = false;
        activeRebuildOperationSignature = null;
        activeVoxelStepExitedRealtime = -1f;

        if (exitCode == 0)
        {
            if (!TryCompleteRebuildFromArtifacts(expectedSignature, "VoxelSTEP exit code 0"))
            {
                lastRebuildFailureText = "Rebuild produced no preview";
                UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] Incremental rebuild produced no usable preview artifacts");
            }
        }
        else
        {
            lastRebuildFailureText = "Rebuild failed";
            UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] Incremental rebuild failed");
        }

        if (rebuildQueued)
        {
            rebuildQueued = false;
            TriggerRebuild();
        }
    }

    void RecoverCompletedRebuildIfCallbackWasLost()
    {
        if (!rebuildRunning || string.IsNullOrEmpty(activeRebuildOperationSignature))
            return;

        if (IsActiveVoxelStepProcessStillRunning())
            return;

        if (IsActiveVoxelStepExitGracePending())
            return;

        if (!TryCompleteRebuildFromArtifacts(activeRebuildOperationSignature, "native callback was lost"))
        {
            if (activeVoxelStepCancelledByTimeout)
            {
                activeVoxelStepCancelledByTimeout = false;
                activeRebuildOperationSignature = null;
                rebuildRunning = false;
                lastRebuildFailureText = "Rebuild timed out";
                UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] Incremental rebuild timed out and produced no usable preview artifacts");

                if (rebuildQueued)
                {
                    rebuildQueued = false;
                    TriggerRebuild();
                }
            }

            return;
        }

        UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] Recovered completed Setup 3 preview rebuild after the coroutine callback was lost");
        activeVoxelStepCancelledByTimeout = false;
        activeRebuildOperationSignature = null;
        rebuildRunning = false;
        ClearActiveVoxelStepProcess();

        if (rebuildQueued)
        {
            rebuildQueued = false;
            TriggerRebuild();
        }
    }

    void AdoptCompletedRebuildArtifactsIfIdle()
    {
        if (baseStepGenerating || rebuildRunning || operationList.Count == 0 || HasCurrentSuccessfulRebuild())
            return;

        if (!OperationTxtMatchesCurrentOperations())
            return;

        if (TryCompleteRebuildFromArtifacts(BuildOperationSignature(), "completed artifacts found while idle"))
            UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] Adopted completed Setup 3 preview artifacts after rebuild state was lost");
    }

    bool OperationTxtMatchesCurrentOperations()
    {
        if (!File.Exists(operationTxtPath))
            return false;

        try
        {
            return string.Equals(
                File.ReadAllText(operationTxtPath),
                BuildOperationTxtContent(),
                System.StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    bool TryCompleteRebuildFromArtifacts(string expectedSignature, string reason)
    {
        if (!HasUsableIncrementalArtifacts(GetFileLengthSafe(currentStepPath), GetFileLengthSafe(previewObjPath)))
            return false;

        if (!LoadPreviewMesh())
            return false;

        lastSuccessfulRebuildSignature = string.IsNullOrEmpty(expectedSignature)
            ? BuildOperationSignature()
            : expectedSignature;
        lastRebuildFailureText = null;
        WriteCurrentRoundReferenceSidecars();
        UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Incremental rebuild complete ({reason})");
        return true;
    }

    static long GetFileLengthSafe(string path)
    {
        if (string.IsNullOrEmpty(path))
            return 0;

        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static bool HasUsableIncrementalArtifacts(long currentStepBytes, long previewObjBytes)
    {
        return currentStepBytes > 0 && previewObjBytes > 0;
    }

    IEnumerator RunVoxelSTEPAsync(string args, System.Action<int> onComplete)
    {
        if (!File.Exists(StepExePath))
        {
            UnityEngine.Debug.LogError($"[Setup3DatumPreviewManager] VoxelSTEP.exe not found: {StepExePath}");
            onComplete?.Invoke(-1);
            yield break;
        }

        float timeoutSeconds = ResolveVoxelStepTimeoutSeconds();
        UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Running VoxelSTEP.exe {args}");
        UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] VoxelSTEP timeout budget: {timeoutSeconds:0.#}s");

        Process proc = null;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            var psi = new ProcessStartInfo(StepExePath, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    stdout.AppendLine(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    stderr.AppendLine(e.Data);
            };
            proc.Start();
            activeVoxelStepProcess = proc;
            activeVoxelStepCancelledByTimeout = false;
            activeVoxelStepExitedRealtime = -1f;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogError($"[Setup3DatumPreviewManager] Process.Start failed: {ex.Message}");
            proc?.Dispose();
            onComplete?.Invoke(-1);
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + timeoutSeconds;
        activeVoxelStepDeadlineRealtime = deadline;
        while (Time.realtimeSinceStartup < deadline && !HasProcessExited(proc))
            yield return null;

        int finalCode = -1;
        if (!HasProcessExited(proc))
        {
            try { proc.Kill(); } catch { }
            UnityEngine.Debug.LogWarning($"[Setup3DatumPreviewManager] VoxelSTEP timed out ({timeoutSeconds:0.#}s)");
        }
        else
        {
            try
            {
                if (!proc.WaitForExit(1000))
                    UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] VoxelSTEP output stream drain exceeded 1s; continuing with exit code");
                finalCode = proc.ExitCode;
                UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Exit code: {finalCode}");
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Setup3DatumPreviewManager] Could not read VoxelSTEP exit code: {ex.Message}");
            }
        }

        if (stdout.Length > 0)
            UnityEngine.Debug.Log($"[VoxelSTEP] {stdout.ToString().Trim()}");
        if (stderr.Length > 0)
        {
            string err = stderr.ToString().Trim();
            if (finalCode == 0)
                UnityEngine.Debug.Log($"[VoxelSTEP] {err}");
            else
                UnityEngine.Debug.LogWarning($"[VoxelSTEP] {err}");
        }

        proc.Dispose();
        if (ReferenceEquals(activeVoxelStepProcess, proc))
        {
            activeVoxelStepProcess = null;
            activeVoxelStepDeadlineRealtime = -1f;
            activeVoxelStepExitedRealtime = -1f;
        }
        onComplete?.Invoke(finalCode);
    }

    static bool HasProcessExited(Process proc)
    {
        if (proc == null)
            return true;

        try
        {
            return proc.HasExited;
        }
        catch
        {
            return true;
        }
    }

    bool IsActiveVoxelStepProcessStillRunning()
    {
        Process proc = activeVoxelStepProcess;
        if (proc == null)
            return false;

        if (!HasProcessExited(proc))
        {
            activeVoxelStepExitedRealtime = -1f;
            if (activeVoxelStepDeadlineRealtime > 0f && Time.realtimeSinceStartup >= activeVoxelStepDeadlineRealtime)
            {
                activeVoxelStepCancelledByTimeout = true;
                CancelActiveVoxelStepProcess("the timeout monitor expired");
                return false;
            }

            return true;
        }

        if (activeVoxelStepExitedRealtime < 0f)
            activeVoxelStepExitedRealtime = Time.realtimeSinceStartup;
        return false;
    }

    bool IsActiveVoxelStepExitGracePending()
    {
        const float processExitGraceSeconds = 1.5f;
        return activeVoxelStepProcess != null &&
               activeVoxelStepExitedRealtime >= 0f &&
               Time.realtimeSinceStartup - activeVoxelStepExitedRealtime < processExitGraceSeconds;
    }

    void ClearActiveVoxelStepProcess()
    {
        activeVoxelStepProcess = null;
        activeVoxelStepDeadlineRealtime = -1f;
        activeVoxelStepExitedRealtime = -1f;
    }

    void CancelActiveVoxelStepProcess(string reason)
    {
        Process proc = activeVoxelStepProcess;
        activeVoxelStepProcess = null;
        activeVoxelStepDeadlineRealtime = -1f;
        activeVoxelStepExitedRealtime = -1f;

        if (proc == null)
            return;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill();
                UnityEngine.Debug.LogWarning($"[Setup3DatumPreviewManager] Cancelled VoxelSTEP process because {reason}");
            }
        }
        catch (System.Exception ex)
        {
            UnityEngine.Debug.LogWarning($"[Setup3DatumPreviewManager] Failed to cancel VoxelSTEP process: {ex.Message}");
        }
        finally
        {
            try { proc.Dispose(); } catch { }
        }
    }

    float ResolveVoxelStepTimeoutSeconds()
    {
        int meshCount = 0;
        int cutCount = 0;
        for (int i = 0; i < operationList.Count; i++)
        {
            if (operationList[i].useMeshOperand)
                meshCount++;
            if (operationList[i].isCut)
                cutCount++;
        }

        return ComputeVoxelStepTimeoutSecondsForCounts(meshCount, cutCount);
    }

    public static float ComputeVoxelStepTimeoutSecondsForCounts(int meshCount, int cutCount)
    {
        float timeout = 120f;

        // Mesh operands import through a temporary STEP conversion and can
        // take significantly longer than oriented box booleans, especially
        // for curved-surface CUT runs.
        timeout += Mathf.Max(0, meshCount) * 120f;
        timeout += Mathf.Max(0, cutCount) * 30f;
        return Mathf.Max(timeout, 120f);
    }

    bool EnsureBaseStepReady()
    {
        RefreshSourceIfNeeded();
        if (baseStepReady && File.Exists(baseStepPath))
            return true;

        PrepareBaseAsync();
        return false;
    }

    void EnsureRuntimeBodyInstance()
    {
        if (runtimeBodyRoot != null) return;

        runtimeBodyRoot = new GameObject("Setup3DatumBody");
        runtimeBodyRoot.name = "Setup3DatumBody";
        runtimeBodyRoot.transform.position = Vector3.zero;
        runtimeBodyRoot.transform.rotation = Quaternion.identity;
        runtimeBodyRoot.transform.localScale = Vector3.one;

        runtimeMeshObject = new GameObject("Setup3DatumBodyMesh");
        runtimeMeshObject.transform.SetParent(runtimeBodyRoot.transform, false);
        runtimeMeshFilter = runtimeMeshObject.AddComponent<MeshFilter>();
        runtimeMeshRenderer = runtimeMeshObject.AddComponent<MeshRenderer>();
        runtimeMeshRenderer.shadowCastingMode = ShadowCastingMode.On;
        runtimeMeshRenderer.receiveShadows = true;

        runtimeMeshCollider = runtimeMeshObject.AddComponent<MeshCollider>();

        SeedRuntimeBodyFromSource();

        runtimeBodyRoot.SetActive(false);
    }

    string GetCurrentSourceFingerprint()
    {
        string resolvedPath = GetResolvedSourceMeshPath() ?? string.Empty;
        return $"{resolvedPath}|{bodySize.x},{bodySize.y},{bodySize.z}";
    }

    void RefreshSourceIfNeeded()
    {
        string currentFingerprint = GetCurrentSourceFingerprint();
        if (currentFingerprint == activeSourceFingerprint)
            return;

        RestoreSourceBodyVisuals();
        if (rebuildRunning || baseStepGenerating)
            CancelActiveVoxelStepProcess("the Setup 3 source changed");
        activeSourceFingerprint = currentFingerprint;
        resolvedSourceBodyObject = null;
        baseStepReady = false;
        baseStepGenerating = false;
        rebuildRunning = false;
        rebuildQueued = false;
        activeRebuildOperationSignature = null;
        activeVoxelStepCancelledByTimeout = false;
        lastRebuildFailureText = null;

        DeleteIfExists(baseStepPath);
        DeleteIfExists(currentStepPath);
        DeleteIfExists(previewObjPath);
        DeleteIfExists(baseMeshStlPath);

        if (cachedBasePreviewMesh != null)
        {
            Destroy(cachedBasePreviewMesh);
            cachedBasePreviewMesh = null;
        }

        if (runtimeBodyRoot != null)
        {
            SeedRuntimeBodyFromSource();
            CacheBasePreviewMesh();
            if (operationList.Count == 0)
                RestoreBaseMesh();
        }

        UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Source refresh → {GetResolvedSourceMeshPath()}");
    }

    void HideSourceBodyVisuals()
    {
        RestoreSourceBodyVisuals();

        GameObject sourceObject = ResolveSourceBodyObject();
        if (sourceObject == null)
            return;

        if (runtimeBodyRoot != null &&
            (sourceObject == runtimeBodyRoot || sourceObject.transform.IsChildOf(runtimeBodyRoot.transform)))
            return;

        Renderer[] renderers = sourceObject.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
                continue;

            if (runtimeMeshObject != null &&
                (renderer.gameObject == runtimeMeshObject || renderer.transform.IsChildOf(runtimeMeshObject.transform)))
                continue;

            hiddenSourceRenderers.Add(new SourceRendererState
            {
                renderer = renderer,
                wasEnabled = renderer.enabled
            });
            renderer.enabled = false;
        }

        if (hiddenSourceRenderers.Count > 0)
            UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Hid {hiddenSourceRenderers.Count} source renderer(s) while Setup 3 is active");
    }

    void RestoreSourceBodyVisuals()
    {
        for (int i = 0; i < hiddenSourceRenderers.Count; i++)
        {
            SourceRendererState state = hiddenSourceRenderers[i];
            if (state.renderer != null)
                state.renderer.enabled = state.wasEnabled;
        }
        hiddenSourceRenderers.Clear();
    }

    void SeedRuntimeBodyFromSource()
    {
        GameObject sourceObject = ResolveSourceBodyObject();
        Mesh sourceMesh = LoadAuthoritativeSourceMesh();
        Material[] sourceMaterials = null;

        if (sourceObject != null)
        {
            if (sourceMesh == null)
            {
                var sourceFilter = sourceObject.GetComponentInChildren<MeshFilter>(true);
                if (sourceFilter != null)
                    sourceMesh = sourceFilter.sharedMesh;
            }

            var sourceRenderer = sourceObject.GetComponentInChildren<MeshRenderer>(true);
            if (sourceRenderer != null && sourceRenderer.sharedMaterials != null && sourceRenderer.sharedMaterials.Length > 0)
                sourceMaterials = sourceRenderer.sharedMaterials;
        }

        if (sourceMaterials != null && sourceMaterials.Length > 0)
            ApplyRuntimePreviewMaterials(sourceMaterials);

        if (sourceMesh == null)
        {
            UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] Source body prefab did not expose a MeshFilter; Setup 3 runtime body will wait for preview mesh output");
            return;
        }

        SetRuntimeMesh(sourceMesh);
    }

    void ApplyRuntimePreviewMaterials(Material[] sourceMaterials)
    {
        if (runtimeMeshRenderer == null || sourceMaterials == null || sourceMaterials.Length == 0)
            return;

        DestroyRuntimePreviewMaterials();

        var previewMaterials = new Material[sourceMaterials.Length];
        for (int i = 0; i < sourceMaterials.Length; i++)
        {
            Material src = sourceMaterials[i];
            if (src == null)
                continue;

            Material copy = new Material(src);
            copy.name = src.name + "_DatumPreview";
            copy.hideFlags = HideFlags.DontSave;
            MakeMaterialDoubleSided(copy);
            TunePreviewMaterialForReadability(copy);
            runtimePreviewMaterials.Add(copy);
            previewMaterials[i] = copy;
        }

        runtimeMeshRenderer.sharedMaterials = previewMaterials;
    }

    void DestroyRuntimePreviewMaterials()
    {
        for (int i = 0; i < runtimePreviewMaterials.Count; i++)
        {
            if (runtimePreviewMaterials[i] != null)
                Destroy(runtimePreviewMaterials[i]);
        }
        runtimePreviewMaterials.Clear();
    }

    static void MakeMaterialDoubleSided(Material material)
    {
        if (material == null)
            return;

        if (material.HasProperty("_Cull"))
            material.SetInt("_Cull", (int)CullMode.Off);
        if (material.HasProperty("_CullMode"))
            material.SetInt("_CullMode", (int)CullMode.Off);
        if (material.HasProperty("_DoubleSidedEnable"))
            material.SetFloat("_DoubleSidedEnable", 1f);
        if (material.HasProperty("_RenderFace"))
            material.SetFloat("_RenderFace", 2f);
    }

    static void TunePreviewMaterialForReadability(Material material)
    {
        if (material == null)
            return;

        if (material.HasProperty("_Smoothness"))
            material.SetFloat("_Smoothness", 0f);
        if (material.HasProperty("_Glossiness"))
            material.SetFloat("_Glossiness", 0f);
        if (material.HasProperty("_Metallic"))
            material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_SpecularHighlights"))
            material.SetFloat("_SpecularHighlights", 0f);
        if (material.HasProperty("_EnvironmentReflections"))
            material.SetFloat("_EnvironmentReflections", 0f);

        if (material.HasProperty("_BaseColor"))
        {
            Color c = material.GetColor("_BaseColor");
            material.SetColor("_BaseColor", new Color(c.r * 0.92f, c.g * 0.92f, c.b * 0.92f, c.a));
        }
        if (material.HasProperty("_Color"))
        {
            Color c = material.GetColor("_Color");
            material.SetColor("_Color", new Color(c.r * 0.92f, c.g * 0.92f, c.b * 0.92f, c.a));
        }
    }

    Mesh LoadAuthoritativeSourceMesh()
    {
        string src = GetResolvedSourceMeshPath();
        if (string.IsNullOrEmpty(src) || !File.Exists(src))
            return null;

        string ext = Path.GetExtension(src).ToLowerInvariant();
        Mesh mesh = null;

        if (ext == ".stl")
            mesh = ParseSimpleStl(src);
        else if (ext == ".obj")
            mesh = ParseSimpleObj(src);

        if (mesh == null)
            return null;

        mesh.name = Path.GetFileNameWithoutExtension(src) + "_Authoritative";
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        int boundaryEdges = CountBoundaryEdges(mesh);
        if (boundaryEdges > 0)
        {
            UnityEngine.Debug.LogWarning(
                $"[Setup3DatumPreviewManager] Source mesh appears open/non-watertight ({boundaryEdges} boundary edges): {src}. " +
                "Setup 3 exact ADD/CUT booleans may fail on non-solid STL meshes.");
        }
        UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Visible Setup 3 body seeded from source mesh path: {src}");
        return mesh;
    }

    static int CountBoundaryEdges(Mesh mesh)
    {
        if (mesh == null)
            return 0;

        Vector3[] verts = mesh.vertices;
        int[] tris = mesh.triangles;
        if (verts == null || tris == null || tris.Length < 3)
            return 0;

        var weldedIndexByVertex = new int[verts.Length];
        var weldedByPosition = new Dictionary<Vector3Int, int>();
        int nextWeldedIndex = 0;

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 v = verts[i];
            Vector3Int key = new Vector3Int(
                Mathf.RoundToInt(v.x * 1000f),
                Mathf.RoundToInt(v.y * 1000f),
                Mathf.RoundToInt(v.z * 1000f));

            if (!weldedByPosition.TryGetValue(key, out int welded))
            {
                welded = nextWeldedIndex++;
                weldedByPosition[key] = welded;
            }

            weldedIndexByVertex[i] = welded;
        }

        var edgeUseCounts = new Dictionary<long, int>();
        for (int i = 0; i + 2 < tris.Length; i += 3)
        {
            int a = weldedIndexByVertex[tris[i]];
            int b = weldedIndexByVertex[tris[i + 1]];
            int c = weldedIndexByVertex[tris[i + 2]];

            CountEdge(edgeUseCounts, a, b);
            CountEdge(edgeUseCounts, b, c);
            CountEdge(edgeUseCounts, c, a);
        }

        int boundaryEdges = 0;
        foreach (int count in edgeUseCounts.Values)
        {
            if (count == 1)
                boundaryEdges++;
        }

        return boundaryEdges;
    }

    static void CountEdge(Dictionary<long, int> edgeUseCounts, int a, int b)
    {
        int min = Mathf.Min(a, b);
        int max = Mathf.Max(a, b);
        long key = ((long)min << 32) | (uint)max;

        if (edgeUseCounts.TryGetValue(key, out int count))
            edgeUseCounts[key] = count + 1;
        else
            edgeUseCounts[key] = 1;
    }

    GameObject ResolveSourceBodyObject()
    {
        if (resolvedSourceBodyObject != null)
            return resolvedSourceBodyObject;

        if (sourceBodyPrefab != null)
        {
            resolvedSourceBodyObject = sourceBodyPrefab;
            return resolvedSourceBodyObject;
        }

        ModeManager[] managers = FindObjectsByType<ModeManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (managers.Length > 0 && managers[0] != null && managers[0].sourceBodyPrefab != null)
        {
            UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] Source Body Prefab was missing or mismatched; falling back to ModeManager.sourceBodyPrefab");
            resolvedSourceBodyObject = managers[0].sourceBodyPrefab;
            return resolvedSourceBodyObject;
        }

        VoxelBody[] bodies = FindObjectsByType<VoxelBody>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (bodies.Length > 0 && bodies[0] != null)
        {
            UnityEngine.Debug.LogWarning("[Setup3DatumPreviewManager] Source Body Prefab was missing or mismatched; falling back to the first VoxelBody root");
            resolvedSourceBodyObject = bodies[0].gameObject;
            return resolvedSourceBodyObject;
        }

        return null;
    }

    void CacheBasePreviewMesh()
    {
        if (cachedBasePreviewMesh != null) return;
        EnsureRuntimeBodyInstance();

        Mesh src = GetCurrentMesh();
        if (src == null || src.vertexCount == 0) return;

        cachedBasePreviewMesh = Instantiate(src);
        cachedBasePreviewMesh.name = src.name + "_DatumBase";
    }

    void RestoreBaseMesh()
    {
        if (cachedBasePreviewMesh == null || runtimeMeshFilter == null) return;
        SetRuntimeMesh(cachedBasePreviewMesh);
    }

    Mesh GetCurrentMesh()
    {
        if (runtimeMeshFilter == null) return null;
        return runtimeMeshFilter.sharedMesh != null
            ? runtimeMeshFilter.sharedMesh
            : runtimeMeshFilter.mesh;
    }

    void SetRuntimeMesh(Mesh mesh)
    {
        if (mesh == null || runtimeMeshFilter == null)
            return;

        if (runtimeMeshFilter.sharedMesh != mesh)
            runtimeMeshFilter.sharedMesh = mesh;

        if (runtimeMeshCollider != null)
        {
            if (runtimeMeshCollider.sharedMesh != mesh)
            {
                runtimeMeshCollider.sharedMesh = null;
                runtimeMeshCollider.sharedMesh = mesh;
            }
            runtimeMeshCollider.enabled = true;
        }
    }

    bool ExportBaseMeshAsAsciiStl(string path)
    {
        CacheBasePreviewMesh();
        Mesh mesh = cachedBasePreviewMesh;
        return WriteMeshAsAsciiStl(mesh, path, "setup3_datum_base");
    }

    static bool WriteMeshAsAsciiStl(Mesh mesh, string path, string solidName)
    {
        if (mesh == null || mesh.vertexCount == 0)
            return false;

        Vector3[] verts = mesh.vertices;
        int[] tris = mesh.triangles;
        if (tris == null || tris.Length < 3)
            return false;

        using (var sw = new StreamWriter(path, append: false, Encoding.ASCII))
        {
            sw.WriteLine($"solid {solidName}");
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 v0 = verts[tris[i]];
                Vector3 v1 = verts[tris[i + 1]];
                Vector3 v2 = verts[tris[i + 2]];
                Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                if (normal.sqrMagnitude < 1e-8f)
                    normal = Vector3.up;

                sw.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    " facet normal {0} {1} {2}", normal.x, normal.y, normal.z));
                sw.WriteLine("  outer loop");
                WriteStlVertex(sw, v0);
                WriteStlVertex(sw, v1);
                WriteStlVertex(sw, v2);
                sw.WriteLine("  endloop");
                sw.WriteLine(" endfacet");
            }
            sw.WriteLine($"endsolid {solidName}");
        }

        return true;
    }

    bool WriteCurrentMeshAsObj(string path)
    {
        Mesh mesh = GetCurrentMesh();
        if (mesh == null || mesh.vertexCount == 0)
            return false;

        var sb = new StringBuilder();
        foreach (Vector3 v in mesh.vertices)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", v.x, v.y, v.z));

        int[] tris = mesh.triangles;
        for (int i = 0; i + 2 < tris.Length; i += 3)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "f {0} {1} {2}",
                tris[i] + 1, tris[i + 1] + 1, tris[i + 2] + 1));

        File.WriteAllText(path, sb.ToString());
        return true;
    }

    static void WriteStlVertex(StreamWriter sw, Vector3 v)
    {
        sw.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "   vertex {0} {1} {2}", v.x, v.y, v.z));
    }

    string GetExportPath(string ext)
    {
        string src = GetResolvedSourceMeshPath();
        string baseName = string.IsNullOrEmpty(src)
            ? "setup3_datum_export"
            : Path.GetFileNameWithoutExtension(src);
        return Path.Combine(@"D:\SRH\XR Technologies\Master Thesis folder\output objects", baseName + ext);
    }

    void WriteRoundReferenceSidecars(string exportPath)
    {
        var records = CollectRoundReferenceRecords();
        string csvPath = BuildRoundReferenceExportPath(exportPath, ".csv");
        string jsonPath = BuildRoundReferenceExportPath(exportPath, ".json");

        if (records.Count == 0)
        {
            DeleteIfExists(csvPath);
            DeleteIfExists(jsonPath);
            return;
        }

        WriteRoundReferenceCsv(csvPath, records);
        WriteRoundReferenceJson(jsonPath, records);
        UnityEngine.Debug.Log($"[Setup3DatumPreviewManager] Round refs exported → {csvPath}");
    }

    void WriteCurrentRoundReferenceSidecars()
    {
        if (string.IsNullOrEmpty(currentStepPath))
            return;

        WriteRoundReferenceSidecars(currentStepPath);
    }

    List<Setup3DatumRoundReferenceRecord> CollectRoundReferenceRecords()
    {
        var records = new List<Setup3DatumRoundReferenceRecord>();
        for (int i = 0; i < operationList.Count; i++)
        {
            Setup3DatumOperation op = operationList[i];
            if (!TryBuildRoundReferenceRecord(op, i, out Setup3DatumRoundReferenceRecord record))
                continue;
            records.Add(record);
        }

        return records;
    }

    public static bool TryBuildRoundReferenceRecord(Setup3DatumOperation op, int opIndex, out Setup3DatumRoundReferenceRecord record)
    {
        record = default;
        if (!op.useRoundPrimitive || !op.hasRoundReference || op.roundRadius <= 0f)
            return false;

        Vector3 axisDirection = op.normalAxis.sqrMagnitude > 1e-8f ? op.normalAxis.normalized : Vector3.up;
        record = new Setup3DatumRoundReferenceRecord
        {
            opIndex = opIndex,
            opKind = op.isCut ? "CUT_CYLINDER" : "ADD_CYLINDER",
            radiusMm = op.roundRadius,
            requestedDepthMm = op.roundReferenceRequestedDepth,
            booleanDepthMm = op.depth,
            surfaceCenter = op.roundReferenceSurfaceCenter,
            topCenter = op.roundReferenceTopCenter,
            axisDirection = axisDirection,
            localX = op.localX.sqrMagnitude > 1e-8f ? op.localX.normalized : Vector3.right
        };
        return true;
    }

    public static string BuildRoundReferenceExportPath(string exportPath, string extension)
    {
        string dir = Path.GetDirectoryName(exportPath);
        string baseName = Path.GetFileNameWithoutExtension(exportPath);
        return Path.Combine(dir, baseName + "_round_refs" + extension);
    }

    static void WriteRoundReferenceCsv(string path, List<Setup3DatumRoundReferenceRecord> records)
    {
        var sb = new StringBuilder(records.Count * 192 + 128);
        sb.AppendLine("op_index,op_kind,radius_mm,requested_depth_mm,boolean_depth_mm,surface_center_x,surface_center_y,surface_center_z,top_center_x,top_center_y,top_center_z,axis_dir_x,axis_dir_y,axis_dir_z,local_x_x,local_x_y,local_x_z");
        foreach (Setup3DatumRoundReferenceRecord record in records)
        {
            sb.Append(record.opIndex.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(record.opKind).Append(',');
            sb.Append(record.radiusMm.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(record.requestedDepthMm.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(record.booleanDepthMm.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
            AppendVectorCsv(sb, record.surfaceCenter);
            sb.Append(',');
            AppendVectorCsv(sb, record.topCenter);
            sb.Append(',');
            AppendVectorCsv(sb, record.axisDirection);
            sb.Append(',');
            AppendVectorCsv(sb, record.localX);
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    static void AppendVectorCsv(StringBuilder sb, Vector3 v)
    {
        sb.Append(v.x.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(v.y.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(v.z.ToString("F6", CultureInfo.InvariantCulture));
    }

    static void WriteRoundReferenceJson(string path, List<Setup3DatumRoundReferenceRecord> records)
    {
        var export = new Setup3DatumRoundReferenceExport { rounds = records };
        File.WriteAllText(path, JsonUtility.ToJson(export, true));
    }

    static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    static Mesh ParseSimpleObj(string path)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("v "))
            {
                string[] p = line.Split(new char[] { ' ', '\t' },
                    System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4 &&
                    float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    verts.Add(new Vector3(x, y, z));
            }
            else if (line.StartsWith("f "))
            {
                string[] p = line.Split(new char[] { ' ', '\t' },
                    System.StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 4)
                {
                    int i0 = ParseFaceIdx(p[1]) - 1;
                    int i1 = ParseFaceIdx(p[2]) - 1;
                    int i2 = ParseFaceIdx(p[3]) - 1;
                    if (i0 >= 0 && i1 >= 0 && i2 >= 0 &&
                        i0 < verts.Count && i1 < verts.Count && i2 < verts.Count)
                    {
                        tris.Add(i0); tris.Add(i1); tris.Add(i2);
                    }
                    if (p.Length >= 5)
                    {
                        int i3 = ParseFaceIdx(p[4]) - 1;
                        if (i3 >= 0 && i3 < verts.Count)
                        {
                            tris.Add(i0); tris.Add(i2); tris.Add(i3);
                        }
                    }
                }
            }
        }

        if (verts.Count == 0 || tris.Count == 0) return null;

        var mesh = new Mesh();
        mesh.indexFormat = verts.Count > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        return mesh;
    }

    static Mesh ParseSimpleStl(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (fs.Length < 6)
                return null;

            bool isBinary = LooksLikeBinaryStl(fs);
            fs.Position = 0;
            return isBinary ? ParseBinaryStl(fs) : ParseAsciiStl(path);
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

    static Mesh ParseAsciiStl(string path)
    {
        var verts = new List<Vector3>();
        var tris = new List<int>();

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("vertex ", System.StringComparison.OrdinalIgnoreCase))
                continue;

            string[] p = line.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4)
                continue;

            if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                !float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                continue;

            verts.Add(new Vector3(x, y, z));
            tris.Add(verts.Count - 1);
        }

        if (verts.Count == 0 || tris.Count < 3)
            return null;

        var mesh = new Mesh();
        mesh.indexFormat = verts.Count > 65535
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        return mesh;
    }

    static Mesh ParseBinaryStl(FileStream fs)
    {
        using (var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true))
        {
            br.ReadBytes(80);
            uint triCount = br.ReadUInt32();

            var verts = new List<Vector3>((int)triCount * 3);
            var tris = new List<int>((int)triCount * 3);

            for (uint i = 0; i < triCount; i++)
            {
                br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // facet normal

                for (int v = 0; v < 3; v++)
                {
                    float x = br.ReadSingle();
                    float y = br.ReadSingle();
                    float z = br.ReadSingle();
                    verts.Add(new Vector3(x, y, z));
                    tris.Add(verts.Count - 1);
                }

                br.ReadUInt16(); // attribute byte count
            }

            if (verts.Count == 0)
                return null;

            var mesh = new Mesh();
            mesh.indexFormat = verts.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            return mesh;
        }
    }

    static int ParseFaceIdx(string s)
    {
        int slash = s.IndexOf('/');
        string num = slash >= 0 ? s.Substring(0, slash) : s;
        return int.TryParse(num, out int v) ? v : -1;
    }
}
