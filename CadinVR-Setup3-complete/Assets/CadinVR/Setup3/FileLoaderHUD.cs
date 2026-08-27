// FileLoaderHUD.cs — Runtime CAD file loader (OBJ / STL)
// Attach to any active GameObject in the scene.
// Inspector: wire VoxelBody and (optionally) Setup3DatumPreviewManager.
using UnityEngine;
using System;
using System.Runtime.InteropServices;

public class FileLoaderHUD : MonoBehaviour
{
    public VoxelBody voxelBody;
    public Setup3DatumPreviewManager previewManager;

    private bool _openDialogRequested;
    private string _statusMessage;

    // ── Windows system file dialog via comdlg32.dll ──────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class OpenFileName
    {
        public int    lStructSize    = Marshal.SizeOf(typeof(OpenFileName));
        public IntPtr hwndOwner      = IntPtr.Zero;
        public IntPtr hInstance      = IntPtr.Zero;
        public string lpstrFilter    = null;
        public string lpstrCustomFilter = null;
        public int    nMaxCustFilter = 0;
        public int    nFilterIndex   = 0;
        public string lpstrFile      = null;
        public int    nMaxFile       = 0;
        public string lpstrFileTitle = null;
        public int    nMaxFileTitle  = 0;
        public string lpstrInitialDir = null;
        public string lpstrTitle     = null;
        public int    Flags          = 0;
        public short  nFileOffset    = 0;
        public short  nFileExtension = 0;
        public string lpstrDefExt    = null;
        public IntPtr lCustData      = IntPtr.Zero;
        public IntPtr lpfnHook       = IntPtr.Zero;
        public string lpTemplateName = null;
    }

    [DllImport("Comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_NOCHANGEDIR   = 0x00000008;
    // ────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!_openDialogRequested) return;
        _openDialogRequested = false;

        if (voxelBody == null)
            voxelBody = FindFirstObjectByType<VoxelBody>();
        if (previewManager == null)
            previewManager = GetComponent<Setup3DatumPreviewManager>()
                          ?? FindFirstObjectByType<Setup3DatumPreviewManager>();

        var ofn = new OpenFileName();
        ofn.lpstrFilter   = "CAD Files\0*.obj;*.stl\0OBJ Files\0*.obj\0STL Files\0*.stl\0\0";
        ofn.lpstrFile     = new string('\0', 260);
        ofn.nMaxFile      = 260;
        ofn.lpstrFileTitle = new string('\0', 260);
        ofn.nMaxFileTitle = 260;
        ofn.lpstrTitle    = "Select CAD File";
        ofn.Flags         = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;

        if (!GetOpenFileName(ofn))
        {
            _statusMessage = "Cancelled.";
            return;
        }

        string path = ofn.lpstrFile.TrimEnd('\0');
        if (string.IsNullOrEmpty(path)) { _statusMessage = "No file selected."; return; }

        if (voxelBody != null)
            voxelBody.ReloadFromPath(path);

        if (previewManager != null)
            previewManager.PrepareBaseAsync();

        _statusMessage = System.IO.Path.GetFileName(path);
    }

    void OnGUI()
    {
        float btnW = 180f, btnH = 36f;
        var rect = new Rect(Screen.width - btnW - 10, 10, btnW, btnH + 24);
        GUILayout.BeginArea(rect);

        if (GUILayout.Button("Load CAD File (.obj/.stl)", GUILayout.Height(btnH)))
            _openDialogRequested = true;

        if (!string.IsNullOrEmpty(_statusMessage))
            GUILayout.Label(_statusMessage);

        GUILayout.EndArea();
    }
}
