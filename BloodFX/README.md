# BloodFX

A BepInEx plugin for SPT 4.1.6 that adds volumetric-looking blood (sprays, mist, splats, wound drips and
death pools) at a fixed, capped cost. All textures are generated in code, and it uses no asset bundles or custom shaders.

- `docs/FINDINGS.md`: how the old VolumetricBloodFX setup worked and where its cost came from.
- `docs/DESIGN.md`: our effects, rendering approach and performance budget.

Build and deploy (with the game closed): `build_bloodfx.bat`. It copies `BloodFX.dll` to
`J:\Games\SPT 4.1.6\BepInEx\plugins\BloodFX\`. Settings are in `BepInEx/config/com.irl.bloodfx.cfg`
and the F12 configuration manager.

## What to check in game

1. **Log**: `BepInEx/LogOutput.log` should show `BloodFX loaded` and
   `BloodFX: using shader Sprites/Default, volumetric shader Hidden/Internal-Colored`. After shooting, a line
   `BloodFX: hits seen N, spawned S; skipped ...` appears every 10 s. If "seen" stays 0, the hook isn't firing.
   If most hits are "skipped", the reason is listed.
2. **Volumetric spurts**: thick, glossy 3D streams of blood should arc out of the exit side and fall to the
   floor, breaking into beads. Some hits pump a pulsing arterial jet for about 2 s, some pour a gush down the
   body, and head shots throw a radial burst. Splats appear where the streams land or hit a wall.
3. **Body shot on a bot at 5–20 m**: dark red stretched droplets fly out along the bullet path, a smaller
   puff comes back toward you, and a short mist cloud appears. Head shots make more mist.
4. **Splats**: a splat appears on a wall behind the bot (if one is within about 2.5 m) and on the floor near
   it, a moment after the hit, as if the droplets landed.
5. **Wound drips**: a wounded bot drips for a few seconds and leaves small drops on the floor.
6. **Death pool**: about 2.5 s after a kill, a pool starts under the pelvis and spreads over about 30 s.
7. **Lighting**: blood should look darker in dark interiors and at night, and brighter in daylight. If it
   looks too bright or too dark everywhere, adjust `Brightness`.
8. **Armor**: plate or helmet hits that the armor stops should make no blood.
9. **Performance**: in a big firefight, FPS should stay flat. Try `Quality = High` and `Low` and compare.
   Decals are one draw call, so they shouldn't cost more as they pile up. The oldest gets reused after `MaxDecals`.
10. **Raid end**: going back to the menu and starting a new raid should leave no blood from the last raid.

Known limits: the blood is unlit and tinted by light probes, so a flashlight won't brighten it. Splats are flat
quads and can hang over edges or corners. Fog doesn't tint the blood.
