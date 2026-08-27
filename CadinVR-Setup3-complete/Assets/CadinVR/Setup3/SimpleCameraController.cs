// SimpleCameraController.cs — VERSION 5
// Attach to: Main Camera
//
// Controls: Orbit (right mouse), Pan (middle mouse), Zoom (scroll wheel)
//
// IMPORTANT — Active Input Handling must be set to "Both" in:
// Project Settings → Player → Other Settings → Active Input Handling
// This allows scroll to use the old UnityEngine.Input (which works without
// Game view focus) while orbit/pan use the New Input System.
//
// Inspector values confirmed working for 100-unit scene:
//   Orbit Sensitivity : 0.2
//   Zoom Step         : 50
//   Pan Sensitivity   : 0.1
//
// Version history:
//   V1: basic scroll zoom — worked but too slow (zoomStep=0.1)
//   V2: adaptive zoom with distanceFactor — broke scroll
//   V3: added scroll.x fallback + debug — scroll still (0,0) in editor
//   V4: reverted to exact V1 scroll method — still broken (focus issue confirmed)
//   V5: scroll uses UnityEngine.Input.mouseScrollDelta (old Input System)
//       fixes scroll not registering — New Input System needs Game view focus for scroll
//       requires Active Input Handling = Both

using UnityEngine;
using UnityEngine.InputSystem;

public class SimpleCameraController : MonoBehaviour
{
    [Header("Speeds")]
    public float orbitSensitivity = 0.2f;
    public float zoomStep         = 50f;
    public float panSensitivity   = 0.1f;

    [Header("Target")]
    public Vector3 pivotPoint = Vector3.zero;

    void Update()
    {
        // ── Orbit (right mouse) ──
        if (Mouse.current.rightButton.isPressed)
        {
            Vector2 mouseDelta = Mouse.current.delta.ReadValue();
            transform.RotateAround(pivotPoint, Vector3.up,    mouseDelta.x *  orbitSensitivity);
            transform.RotateAround(pivotPoint, transform.right, mouseDelta.y * -orbitSensitivity);
        }

        // ── Pan (middle mouse) ──
        if (Mouse.current.middleButton.isPressed)
        {
            Vector2 mouseDelta   = Mouse.current.delta.ReadValue();
            float distanceFactor = Vector3.Distance(transform.position, pivotPoint) * 0.01f;
            Vector3 pan = (transform.right * -mouseDelta.x + transform.up * -mouseDelta.y)
                          * panSensitivity * distanceFactor;
            transform.position += pan;
            pivotPoint         += pan;
        }

        // ── Zoom (scroll) — uses old Input System so it works without Game view focus ──
        float scrollDelta = UnityEngine.Input.mouseScrollDelta.y;
        if (scrollDelta != 0)
        {
            float amount = scrollDelta * zoomStep;
            Ray ray = Camera.main.ScreenPointToRay(
                new Vector2(UnityEngine.Input.mousePosition.x,
                            UnityEngine.Input.mousePosition.y));
            Vector3 move = ray.direction * amount;
            transform.position += move;
            pivotPoint         += move;
            Debug.Log($"[Camera] Zoom | delta:{scrollDelta} | amount:{amount}");
        }
    }
}
