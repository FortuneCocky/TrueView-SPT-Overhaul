# Findings: how the stashed VolumetricBloodFX setup works

Studied from `_stashed_mods/BepInEx/plugins/VolumetricBloodFX.dll`, the `ssh/Bundles/*.bundle`
files next to it, and `VisceralDismemberment.dll` (the mod that actually spawns the blood).
These notes describe the approach only. No code, shaders, textures or assets from it are used in BloodFX.

## It is really two mods

- **VolumetricBloodFX.dll** (15 KB, 2023) has a BepInEx entry point that does nothing. Its only job
  is to supply the MonoBehaviour types that the bundled prefabs reference, so they deserialize. These are
  the component scripts from a commercial Unity "volumetric blood" asset pack: settings, a manual
  vertex-animation driver, a decal driver, a shader-property animator, a depth-texture enabler, a
  decal-layer camera, and an editor orbit camera.
- **VisceralDismemberment.dll** has the game hook. It also embeds a small "BundleLoader" plugin that
  async-loads every `*.bundle` in `plugins/ssh/Bundles` at startup.

## Game hook

- Harmony postfix on `Player.ApplyDamageInfo(DamageInfo, EBodyPart, ...)`.
- It acts **only when the victim is dead** (`IsAlive == false`). No blood on non-lethal hits at all.
- Gate: explosion, landmine or fragment damage, or a bullet whose caliber is in a hard-coded table with a
  per-caliber chance. Looking up the caliber means a dictionary lookup on the item template
  (`SourceId` → `AmmoTemplate.Caliber`) on every hit.
- On a qualifying kill it dismembers a limb or the head (scales bones to zero and spawns gore cap meshes)
  and then calls a blood spawn at that limb.

## What it spawns

- **Volumetric fluid**: one of about 16 prefab variants (`bloodfx.bundle`, 48 MB). Each is a mesh whose
  vertices are animated by a vertex animation texture (VAT). A script steps `_TimeInFrames` through a
  baked fluid simulation of about 100 frames over about 3 seconds. That gives the thick, arcing, splashing
  streams.
- **Ground decal**: a child of the prefab. It is a screen-space/box-projected decal that reads the camera
  depth texture. A `_Cutout` value animates along a curve so the stain spreads and then erodes away.
  The decal snaps to a fixed "ground height" (hit height minus 2 m, not a raycast), with an optional
  raycast to tilt it.
- **Extra decal prefab** (index 18) spawned next to it.
- **Head-shot SFX**: one of three audio prefabs (`bloodsfx.bundle`).
- Gore caps from `gorecaps.bundle` (18 MB) for dismemberment.
- No wound particles, drips, mist or pools on live bots. No config entries at all.

## Where it is expensive

1. **Load time / memory**: about 67 MB of bundles are loaded from disk at startup, then
   `LoadAllAssets<GameObject>()` pulls every prefab (with its VAT textures and meshes) into memory.
   A prewarm coroutine then instantiates every prefab once at y = -2000 to warm shaders.
2. **Instantiate per kill, never destroyed**: each blood spawn does two `Object.Instantiate` calls (a full
   prefab hierarchy each) with **no Destroy and no pooling**. When the animation ends, the renderer is
   only disabled. The GameObjects and their scripts stay alive for the whole raid, so objects pile up
   and every kill adds permanent cost.
3. **Per-frame Update on every spawned object, forever**: the VAT driver and decal driver keep running
   `Update()` after they finish. The VAT driver does a `GetPropertyBlock`/`SetPropertyBlock` round trip
   with **string-named** `SetFloat` calls every frame. The decal driver writes `transform.position`
   every frame.
4. **Forced camera depth texture**: the depth-enabler script turns on `DepthTextureMode.Depth` for
   forward-path cameras. That costs an extra depth pass over the whole scene. The decal-layer variant
   even creates a **second camera** rendering a full-screen depth RT.
5. **Global shader keyword toggles** (`Shader.EnableKeyword`) from decal enable/disable. Every spawn
   flips global state.
6. **Overdraw**: big, close-up alpha-blended fluid meshes plus projected decal boxes, all transparent.
7. Minor: LINQ plus a queue over the whole player hierarchy to find limb bones (cached per player),
   closures in `All(...)`, string `ToLower().Contains` on bone names, and `GetComponentsInChildren` on the
   whole body to collect materials.

Takeaway: the **look** comes from fluid-sim VAT streams plus spreading ground stains. The **cost** comes
from loading huge bundles, never-freed instantiated hierarchies with per-frame scripts, and the forced
depth pass.
