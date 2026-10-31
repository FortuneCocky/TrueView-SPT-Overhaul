# BloodFX design

Goal: blood that reads as heavy and volumetric, like arcing streams, mist, splats that land where the
blood flies, and pools that spread under bodies, at a fixed and predictable cost. Everything is
generated in code at startup. There are no asset bundles, no custom shaders and nothing borrowed.

## Effects

| Effect | When | How it's made |
|---|---|---|
| **Volumetric spurts** | every visible hit | 3D liquid tubes made in code (see "Streams" below). There are 8 shapes: exit jet, exit burst, back-spatter, pulsing arterial spurt, gush that pours to the floor, head-shot radial burst, melee fan and slow dribble. They are mixed at random per hit type. Each one's landing point is solved from its arc and gets a splat on time. |
| **3D blobs** | same | Mesh-mode particles using a low-poly sphere with wet shading baked into its vertex colors. |
| **Exit spray** | bullet/sniper/melee hit | Stretched-billboard droplets fired along the bullet direction in a cone. They have gravity and a velocity-stretch, so they read as streams. On High, a fraction of them get short ribbon trails. |
| **Entry back-spray** | same | A smaller, slower cone back toward the shooter. |
| **Mist** | same (stronger on head hits) | A few soft, noisy puffs that grow and fade over 0.3–0.9 s. They have a max on-screen size so they can't fill the screen. |
| **Wall splats** | when the spray path hits geometry within 2.5 m | One raycast along the bullet direction. The decal appears when the droplets would arrive (distance ÷ speed). |
| **Floor splats** | every hit | Raycasts down near the victim. Small splats appear after the fall time. |
| **Wound drips** | non-lethal hits | A wound slot remembers the hit collider plus a local offset. It drips droplets every 0.3–1 s for a few seconds and leaves small floor drops. |
| **Death pool** | first lethal hit | After 2.5 s (once the ragdoll has settled) it raycasts down from the pelvis. A pool decal then grows from 0.2 m to about 1 m over 20–40 s. |
| **Explosion/fragments** | explosion, grenade fragment, landmine | Spray in random directions, extra mist, more floor splats. |

Armor-stopped hits (`BlockedBy` / `DeflectedBy` set) and zero-damage hits make no blood. Fall, bleed,
dehydration and similar damage make no blood.

## Rendering

- **Shader**: the built-in `Sprites/Default` (always shipped in Unity builds; unlit, alpha-blended,
  vertex color, no ZWrite). Fallbacks are `Legacy Shaders/Particles/Alpha Blended` and
  `Mobile/Particles/Alpha Blended`. One cheap pass, no lighting cost.
- **Lighting without lights**: when something spawns, we sample the scene light probes once at that
  point (`LightProbes.GetInterpolatedProbe`) and darken the blood color by the probe's brightness.
  Blood in a dark basement is dark and blood in sunlight is bright. There are no realtime lights.
- **Procedural textures**, built once at load (about 10 ms), then made non-readable so the CPU copy is freed:
  - a droplet sprite (soft round, with a bright core for a wet look)
  - a mist puff (layered value noise, soft falloff)
  - a 4×2 decal atlas: 4 splat variants (a noisy-edged blob plus satellite droplets pulled out along
    rays) and 4 pool/drip variants. RGB holds wet shading (dark rim, lighter center) and alpha holds
    coverage. The red tint comes from vertex color.
- **Particles**: two shared `ParticleSystem`s (droplets, mist) in world space, emitted with
  `Emit(EmitParams, 1)`. There's no per-spawn object, and simulation runs on Unity's worker threads.
  Particle collision is **off**. Landing is predicted with the raycasts above instead.
- **Streams**: each stream is a tube of rings (6 sides, 10–18 rings by tier) running through fluid elements fired from the wound over an emission window. Every element follows its own ballistic arc, with pressure decay and optional heart-rate pulses for arterial spurts. With age the tube thins and pinches into beads. It flattens where it meets the floor height or the wall plane (found with one raycast per stream that splats). Shading is done per vertex on the CPU (diffuse from above, rim darkening, a sharp glint) and drawn with Unity's built-in `Hidden/Internal-Colored` shader, opaque and depth-writing. All streams share **one mesh and one draw call**, capped at 12/24/48 live streams by tier, and the oldest is reused. Arterial spurts, gushes and dribbles stay anchored to the wound bone while emitting.
- **Decals**: **all decals live in one Mesh** as a ring buffer of quads, so there is one draw call for
  everything. When it's full, the oldest decal is overwritten. Vertex positions and colors are only
  re-uploaded when something grew or faded. Updates run at a fixed rate (15 Hz), not every frame.

## Performance budget

| | Low | Medium | High |
|---|---|---|---|
| Max live particles (droplets + mist) | 600 | 1500 | 3000 |
| Max decals (single mesh) | 96 | 192 | 384 |
| Droplets per hit (before intensity and LOD) | 10 | 22 | 40 |
| Mist puffs per hit | 1 | 3 | 5 |
| Hits processed per frame | 3 | 6 | 10 |
| Wound drip slots | 6 | 12 | 24 |
| Volumetric streams (rings) | 12 (10) | 24 (14) | 48 (18) |
| 3D blobs per hit | 3 | 6 | 10 |
| Trails | no | no | yes (25% of droplets) |

Rules:

- **No per-hit GC allocations**: there's no LINQ, no closures and no strings in the hot path. Pending
  decals, wounds and pools are fixed-size struct arrays. `EmitParams` is reused. Raycasts use the
  non-allocating `Physics.Raycast(..., out hit)`.
- **Object pooling**: there's nothing to instantiate per hit. Particle systems and the decal mesh are
  created once.
- **Hard caps**: ParticleSystem `maxParticles` per system, the decal ring size, wound slots, pending
  slots and the per-frame hit budget. Extra work is dropped, never queued without bound.
- **Distance culling and LOD**: no particles past `MaxDistance` (default 60 m). Droplet count scales
  from 100% at 10 m down to 25% at max distance. Hits off-screen and more than 5 m away skip particles
  and only leave decals.
- **Overdraw cap**: the mist renderer's `maxParticleSize` is 0.35 of the screen, and droplets are small.
- **Raycasts**: at most 3 per hit (wall, 2 floor), on the static world mask
  (`HighPolyWithTerrainNoGrassMask`), only for hits within decal range.
- No realtime lights, no shadow casting/receiving, no light/reflection probe blending on the renderers,
  and no camera depth texture requested.

## Config (BepInEx, `com.irl.bloodfx.cfg`)

- `Enabled` turns everything on or off.
- `Intensity` (0.1–3) multiplies droplet and mist counts and decal sizes.
- `Quality` (Low/Medium/High) picks the tier table above.
- `MaxParticles` and `MaxDecals` override the tier caps (0 = tier default).
- `MaxDistance` is the particle culling distance.
- `DecalLifetime` is how many seconds a splat stays (pools stay twice as long), fading over the last 10 s.
- `WoundDrips`, `DeathPools`, `PlayerHits` toggle those features. `PlayerHits` is off by default so
  your own hits don't spray mist in your face.
- `Brightness` scales the light-probe tint.
- `VolumetricStreams` and `MaxStreams` control the 3D spurts.
- `DebugLog` (on for now) logs a hit summary every 10 s while there is activity.
