using UnityEngine;
using UnityEngine.InputSystem;

public class Setup3DatumSetupHUD : MonoBehaviour
{
    [Header("References")]
    public Setup3DatumPreviewManager previewManager;
    public Setup3SketchTool sketchTool;

    private GUIStyle btnActive, btnDisabled, btnSave, btnReset, btnExport, btnStep;
    private bool stylesReady = false;

    void Update()
    {
        if (previewManager == null) return;

        bool ctrlHeld = UnityEngine.Input.GetKey(KeyCode.LeftControl)
                     || UnityEngine.Input.GetKey(KeyCode.RightControl);
        bool zPressed = Keyboard.current != null
                     && Keyboard.current.zKey.wasPressedThisFrame;

        if (ctrlHeld && zPressed)
            DoUndo();
    }

    void OnGUI()
    {
        if (previewManager == null) return;
        InitStyles();

        int sketchUndoCount = sketchTool != null ? sketchTool.UndoableItemCount : 0;
        int bodyUndoCount = previewManager.OperationCount;
        int count = sketchUndoCount > 0 ? sketchUndoCount : bodyUndoCount;
        bool canUndo = count > 0;
        string undoLabel = sketchUndoCount > 0
            ? $"↩ Sketch Undo  ({count})"
            : (canUndo ? $"↩ Body Undo  ({count})" : "↩ Undo  (0)");
        GUIStyle undoStyle = canUndo ? btnActive : btnDisabled;

        if (GUI.Button(new Rect(16, 16, 140, 32), undoLabel, undoStyle) && canUndo)
            DoUndo();

        if (GUI.Button(new Rect(16, 56, 140, 32), "💾  Save", btnSave))
            previewManager.Save();

        if (GUI.Button(new Rect(16, 96, 140, 32), "↺  Reset Datum", btnReset))
            previewManager.Reset();

        if (GUI.Button(new Rect(16, 136, 140, 32), "📤  Export OBJ", btnExport))
            previewManager.ExportCurrentOBJ();

        if (GUI.Button(new Rect(16, 176, 140, 32), "📐  Export STEP", btnStep))
            previewManager.ExportCurrentSTEP();
    }

    void DoUndo()
    {
        if (sketchTool != null && sketchTool.UndoableItemCount > 0)
        {
            sketchTool.UndoLast();
            return;
        }

        previewManager?.Undo();
    }

    void InitStyles()
    {
        if (stylesReady) return;
        stylesReady = true;

        btnActive = new GUIStyle(GUI.skin.button);
        btnActive.normal.background = MakeTex(2, 2, new Color(0.65f, 0.42f, 0.10f, 1f));
        btnActive.hover.background = MakeTex(2, 2, new Color(0.78f, 0.52f, 0.14f, 1f));
        btnActive.normal.textColor = Color.white;
        btnActive.fontSize = 14;

        btnDisabled = new GUIStyle(GUI.skin.button);
        btnDisabled.normal.background = MakeTex(2, 2, new Color(0.25f, 0.25f, 0.25f, 0.8f));
        btnDisabled.normal.textColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        btnDisabled.fontSize = 14;

        btnSave = new GUIStyle(GUI.skin.button);
        btnSave.normal.background = MakeTex(2, 2, new Color(0.15f, 0.50f, 0.25f, 1f));
        btnSave.hover.background = MakeTex(2, 2, new Color(0.20f, 0.65f, 0.35f, 1f));
        btnSave.normal.textColor = Color.white;
        btnSave.fontSize = 14;

        btnReset = new GUIStyle(GUI.skin.button);
        btnReset.normal.background = MakeTex(2, 2, new Color(0.45f, 0.20f, 0.10f, 1f));
        btnReset.hover.background = MakeTex(2, 2, new Color(0.60f, 0.28f, 0.14f, 1f));
        btnReset.normal.textColor = Color.white;
        btnReset.fontSize = 12;

        btnExport = new GUIStyle(GUI.skin.button);
        btnExport.normal.background = MakeTex(2, 2, new Color(0.35f, 0.15f, 0.55f, 1f));
        btnExport.hover.background = MakeTex(2, 2, new Color(0.48f, 0.20f, 0.72f, 1f));
        btnExport.normal.textColor = Color.white;
        btnExport.fontSize = 13;

        btnStep = new GUIStyle(GUI.skin.button);
        btnStep.normal.background = MakeTex(2, 2, new Color(0.10f, 0.45f, 0.45f, 1f));
        btnStep.hover.background = MakeTex(2, 2, new Color(0.15f, 0.62f, 0.62f, 1f));
        btnStep.normal.textColor = Color.white;
        btnStep.fontSize = 13;
    }

    Texture2D MakeTex(int w, int h, Color col)
    {
        return SolidColorTextureCache.Get(col);
    }
}
