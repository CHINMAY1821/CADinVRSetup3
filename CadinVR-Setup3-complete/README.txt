CADINVR — SETUP3-ONLY COMPLETE PACKAGE
==========================================
Unzip into your Unity project folder so Assets/ merges with your existing
Assets/ folder (overwrite files when asked). Then open
Assets/CAD_in_VR_scene.unity and press Play.

WHAT'S INCLUDED
  Assets/CadinVR/Setup3/            13 scripts + .meta (ModeManager auto-attaches
                                    the 4 Setup3 components at Play)
  Assets/CadinVR/Setup1-assets/     element-cube, test-body-cube, Ghost material
  Assets/CAD_in_VR_scene.unity      ready scene: Main Camera (ModeManager) +
                                    Directional Light + Global Volume + body cube
  Assets/StreamingAssets/VoxelSTEP/ put VoxelSTEP.exe HERE (needed for STEP export)

BEFORE PLAY — file paths were machine-specific, so they're BLANK:
  1. Select "test body cube" -> VoxelBody:  sourceMeshPath = your .obj/.stl
     (elementPrefab = element cube is pre-wired; solidRoot/bodyMeshTarget = self)
  2. Select "Main Camera"  -> ModeManager: sourceMeshPath = same file
     (sourceBodyPrefab = test body cube is pre-wired, bodySize 100/100/100,
      followVoxelBodySource = on)
  OR skip both and press Play, then click "Load CAD File (.obj/.stl)"
  (FileLoaderHUD auto-finds the body).

CONTROLS
  Alt + Left-click   body surface  = place datum plane
  Left-click         on plane      = add sketch points
  Ctrl+Z             undo          HUD buttons: save / load / reset / export

REQUIREMENTS
  Unity 2021.3+ with URP and the Input System package installed
  (CadinVR.asmdef references Unity.InputSystem). No Setup1/Setup2 scripts exist
  anywhere in this package — it is self-contained.
