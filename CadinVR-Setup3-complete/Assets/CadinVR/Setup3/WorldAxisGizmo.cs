using UnityEngine;

[RequireComponent(typeof(Camera))]
public class WorldAxisGizmo : MonoBehaviour
{
    [SerializeField] float armLength   = 45f;
    [SerializeField] Vector2 corner    = new Vector2(52f, 52f); // from bottom-left, pixels

    Material _mat;
    Camera   _cam;

    static readonly Color ColX = new Color(0.88f, 0.18f, 0.18f);
    static readonly Color ColY = new Color(0.18f, 0.78f, 0.18f);
    static readonly Color ColZ = new Color(0.18f, 0.38f, 0.88f);

    void Awake()
    {
        _cam = GetComponent<Camera>();
        var shader = Shader.Find("Hidden/Internal-Colored");
        _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _mat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
        _mat.SetInt("_ZWrite", 0);
        _mat.SetInt("_ZTest",  (int)UnityEngine.Rendering.CompareFunction.Always);
    }

    void OnPostRender()
    {
        if (_mat == null) return;
        GL.PushMatrix();
        GL.LoadPixelMatrix();
        _mat.SetPass(0);
        GL.Begin(GL.LINES);
        DrawAxisGL(Vector3.right,   ColX);
        DrawAxisGL(Vector3.up,      ColY);
        DrawAxisGL(Vector3.forward, ColZ);
        GL.End();
        GL.PopMatrix();
    }

    void DrawAxisGL(Vector3 worldDir, Color col)
    {
        Vector3 v = _cam.transform.InverseTransformDirection(worldDir);
        float alpha = Mathf.Clamp01(v.x * v.x + v.y * v.y + 0.15f);
        col.a = alpha;
        GL.Color(col);
        GL.Vertex3(corner.x, corner.y, 0f);
        GL.Vertex3(corner.x + v.x * armLength, corner.y + v.y * armLength, 0f);
    }

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || _cam == null) return;
        DrawLabel(Vector3.right,   "X", ColX);
        DrawLabel(Vector3.up,      "Y", ColY);
        DrawLabel(Vector3.forward, "Z", ColZ);
    }

    void DrawLabel(Vector3 worldDir, string label, Color col)
    {
        Vector3 v = _cam.transform.InverseTransformDirection(worldDir);
        float sx = corner.x + v.x * (armLength + 12f);
        float sy = corner.y + v.y * (armLength + 12f);
        // OnGUI y is top-down; GL pixel matrix y is bottom-up — flip
        GUI.color = col;
        GUI.Label(new Rect(sx - 6f, Screen.height - sy - 8f, 20f, 20f), label);
        GUI.color = Color.white;
    }
}
