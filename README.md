# TrueView

**One pack that makes Tarkov feel physical.** Your gun has weight and lags behind your aim, your body moves like a person's, your eyes focus like eyes, and every hit bleeds in full 3D.

TrueView bundles client mods that are built to work together:

| Mod | What it does |
|---|---|
| **TrueView** | Weapon handling and body movement: weight, deadzone, sway, inertia, camera motion |
| **Tactical Stances** | Low ready, high ready, active aim, canted aim and shoulder swap |
| **Dynamic DoF** | Eye-like depth of field that focuses on whatever you look at |
| **BloodFX** | Fully volumetric blood: spurts, drips, trails, pools, wounds and armor impacts |
| **Euphoria Reactions** *(coming soon)* | Physics-driven hit reactions: bots stagger, catch themselves, buckle, fall and get back up |

> **Euphoria Reactions isn't in the release zip yet.** Its physics body is being rebuilt from Tarkov's own ragdoll so it needs no outside game data. Its source is here; it will ship in a later release.

Each one has its own config file. Turn any of them off or tune it however you like.

---

## 🔫 TrueView — weapon handling

Your weapon stops being glued to the centre of the screen.

- **Weight you can feel.** Heavy guns aim slower, sway more and settle harder. Pistols snap around.
- **Aim deadzone.** The gun moves freely inside a small cone and the camera only follows when it reaches the edge.
- **Hip-fire deadzone.** When you aren't aiming, the muzzle drifts naturally and your wrist rolls.
- **Sway and inertia.** Breathing, walking, fatigue and the weapon's own momentum all move the gun.
- **Camera lag.** Your head trails the weapon instead of being bolted to it.
- **Recoil that misaligns.** Every shot knocks the gun off target, and tired arms make it worse.
- **Collision-aware ADS.** The gun pulls back near walls instead of clipping through them.

## 🏃 TrueView — body and camera

- **Head-mounted camera.** The view moves with your character's head: steps, lean and body sway. It fades out while aiming so your sights stay steady.
- **Turn lean and real Q/E lean.** Your body banks into turns.
- **Turn-in-place footwork.** Small turns twist your torso. Bigger ones make your feet step, with a weight-scaled dip. Heavy gear makes the step slower and heavier.
- **Landing and crouch dips.** The camera dips when you land from a fall (harder the further you fell) and when you crouch.
- **Focus zoom.** A subtle tunnel-vision zoom when you settle on a target while aiming (off while mounted).

## ❤️ TrueView — stress and injury

- **Adrenaline.** Taking hits or hearing gunfire nearby raises adrenaline. It causes a slight hand tremor, a tunnel-vision pinch and a low heartbeat you can hear, then decays over several seconds.
- **Breathing.** You hear and feel breathing that matches your stamina, from calm nose breathing to heavy panting after a sprint. Holding your breath silences it.
- **Injuries matter.** Wounded arms shake and slow your handling. Crippled legs force a limp.
- **Stamina.** Exhaustion adds tremor and sway.
- **Reload efficiency.** Reload speed follows your weapon's ergonomics and the health of your arms.

---

## 🎯 Tactical Stances

Carry your weapon like an operator instead of always having it shouldered.

| Stance | Feel |
|---|---|
| **Low Ready** | Muzzle down, relaxed carry. Faster sprint spool-up. |
| **High Ready** | Muzzle up, alert. Tactical sprint, moves faster. |
| **Active Aim** | Gun raised and shouldered. Faster movement while aiming. |
| **Canted Aim** | Weapon rolled to the side. Switches to your side-mounted sight automatically if the gun has one. |

- **Hold Alt and scroll** to cycle stances, or bind a key to each stance.
- **Smooth transitions.** The weapon glides between stances, even mid-sprint.
- **Fire return.** Shooting kicks you into a firing pose, then you settle back into your stance after a short delay.
- **Trigger discipline (optional).** Blocks firing in Low or High Ready so stances actually matter.
- **Lean shoulder swap.** The weapon moves to your left shoulder when you lean left.
- **Pistol stances.** Sidearms get their own poses.
- **Arm stamina.** Holding a stance drains or restores stamina based on weapon weight. LMGs are heavy, pistols are cheap.
- **Stance bonuses.** Each stance has its own configurable perks (sprint acceleration, move speed, ADS move speed, recoil).
- **Your stance is remembered** between raids.

---

## 👁️ Dynamic DoF

Depth of field that behaves like your eyes. It uses the game's own post-processing, so there are no extra shaders and the performance cost is small.

- **Focuses on what you're looking at.** It samples the centre of the screen and refocuses smoothly: quickly when pulling focus near, a little slower when relaxing to distance.
- **Minimum focus distance.** A wall right in your face stays soft, just like real eyes.
- **Your gun and arms blur gently** when you look past them.
- **Sights stay sharp.** While aiming, the near blur turns off so your irons and optics are crisp. You can also turn DoF off completely while aiming, or keep it the same as hip fire.
- **Subtle by default.** Blur strength, bokeh, focus speed and sharp range are all configurable.
- **Numpad 7** toggles it in raid. **Numpad 8** shows a debug view for tuning.

---

## 🩸 BloodFX

Blood that is actual 3D geometry, not flat sprites. Every drop collides with the world and stays where it lands. It was written from scratch for performance: each effect type is drawn as one shared mesh (a single draw call), with fixed budgets so nothing piles up.

**On the hit**
- **Exit spurts and ribbons.** Glossy ropes of blood burst from exit wounds, peel off thin strands and break into drops.
- **Headshots** throw forked ribbons sideways across your view, with a dense cloud of drops.
- **Mist burst.** Every hit puffs a dense dark-red burst that tears into a web of fine strands and droplets within a fraction of a second.
- **Spray.** Drops in realistic sizes (many tiny, a few heavy) that fall and splatter wherever they hit.

**When it lands**
- **Drops stick where they hit**, as small domed beads with tails pointing the way they flew.
- **Wall splashes** burst across the surface and leave a stain made of real beads.
- **Ground trails.** Blood that hits the ground while moving drags out into a thick, ragged, glossy smear.
- **Drips.** Wounds keep forming hanging teardrops that let go and fall. Each one lands as a bead that merges with the ones before it into a growing blob.
- **Pools.** Bodies leave a thick, glossy pool that oozes out from underneath over half a minute and stops at ledges instead of hanging in the air.
- **Decals follow surfaces.** Marks bend over steps and bumps, and nothing floats past edges or doorways.

**On the body**
- **Bullet wounds** are a dark wet hole with a ragged ring of torn flesh and a trickle running down.
- **Armor hits** leave a grey and black mark instead of blood: punch mark, cracks and grainy dust.
- **Wrapped onto the character.** Wounds and armor marks are cut from the character's own model, so they wrap over plates, pouches, folds and skin and move with the body.

**Performance and control**
- **Quality tiers** (Low, Medium, High) and hard caps on everything.
- **Distance culling.** Only hits you can see spawn effects.
- **Toggles** for each effect: wounds, drips, pools, streams, armor marks, and blood from hits on you.

---

## 💥 Euphoria Reactions *(coming soon)*

Bots react to being shot with their whole body, driven by a real-time physics simulation instead of canned animations. The bullet's push lands where it hit, the body's muscles and balance fight it, and what happens next is different every time.

**Getting hit**
- **Stumbles.** A solid hit hands the bot to physics. He takes catch-steps, buckles, staggers, and either rights himself or goes over.
- **He catches himself.** A bot that starts falling while still upright may plant a foot or a hand and recover. A wall or a knee already down sends him over anyway.
- **Hit nudges.** Every hit that doesn't start a stumble still rocks the body where it landed: the chest rocks, the head whips, a knee dips.
- **Heavy limb hits.** A massive arm or leg hit sends that limb limp for a moment. Legs kick out and the free arm drops. Hard enough hits tear the gun out of his hand.
- **Leg shots** with enough damage make the knee give out.
- **Shooting back.** A stumbling bot may swing his gun one-handed toward whoever shot him and fire a few rounds.

**What decides it**
- **Weapon and range.** Point-blank shotgun blasts shove hard. Sniper rifles always stumble, at any range, and marksman rifles can count too.
- **Armor.** Rounds stopped by armor give a smaller blunt shove. Unarmored hits (neck, face, pelvis, limbs) hit harder.
- **Sprinting bots** are easy to knock off balance.
- **Bosses** only stumble when they're nearly dead.

**Going down and getting up**
- **Falls.** Heavy hits and leg shots can knock a living bot down. He stays down for a moment, then gets up.
- **Physical get-ups.** He rises from his belly, back or side, with his hands and knees pushing on the real floor, so he gets up with weight instead of a canned animation. Hit him while he's rising and he goes back down.
- **Badly hurt bots stay prone** instead of getting up.
- **Fall damage.** Crashing into the ground or a wall hurts a living bot.

**Deaths**
- **Death falls.** Lethal hits carry the body down limp, under physics, until it settles.
- **Agony.** Some lethal torso hits become a long death: stagger, collapse, writhing on the ground. Then the normal ragdoll takes over.

**Fair and stable**
- **Bots don't act while reacting.** No shooting (except an aim-back), moving, healing or AI until they've fully recovered.
- **Fail-safes.** Any bot stuck, wedged or draped too long is put back into the nearest stance.
- **Smooth at any frame rate.** The physics runs at 60 Hz and the body is interpolated between steps.
- **Every chance, force and timing is configurable.**

---

## 📦 Install

1. Download the latest zip from [Releases](https://github.com/FortuneCocky/TrueView-SPT-Rework/releases).
2. Extract it into your **SPT root folder** and merge the `BepInEx` folder.
3. Launch the game once to generate the configs:
   - `BepInEx/config/com.trueview.cfg`
   - `BepInEx/config/com.devin.tacticalstances.cfg`
   - `BepInEx/config/com.dynamicdof.prism.cfg`
   - `BepInEx/config/com.irl.bloodfx.cfg`

Every setting can also be changed in game through the **BepInEx Configuration Manager (F12)**.

## ✅ Compatibility

- **SPT 4.1.x**, client only. No server mod needed.
- All the mods are designed to stack. Tactical Stances layers on top of TrueView's weapon motion, and Dynamic DoF keeps your sights sharp while TrueView moves the gun.
- Other weapon-handling or procedural-animation mods will likely conflict with TrueView and Tactical Stances.
- Other hit-reaction or ragdoll mods will likely conflict with Euphoria Reactions.
- Other blood or gore mods can run alongside BloodFX, but you'll get two sets of blood. Turn one of them off for the cleanest look.

## 🛠️ Building from source

Each mod is its own project in this repo:

| Folder | Plugin | Config |
|---|---|---|
| `TrueView/` | TrueView.dll | `com.trueview.cfg` |
| `TacticalStances/` | TacticalStances.dll | `com.devin.tacticalstances.cfg` |
| `DynamicDOF/` | DynamicDOF.dll | `com.dynamicdof.prism.cfg` |
| `BloodFX/` | BloodFX.dll | `com.irl.bloodfx.cfg` |
| `EuphoriaReaction/` | EuphoriaReaction.dll | `com.euphoria.reaction.cfg` |

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) and have an SPT 4.1.x install.
2. Point the `SPTDir` (or `SPT_INSTALL_DIR`) property in each `.csproj` at your SPT folder. The game and BepInEx assemblies are referenced from there and are not included here.
3. Run `dotnet build -c Release` in a mod's folder, or its `build_*.bat`, which also copies the DLL into your SPT plugins folder.

Euphoria Reactions also needs `delirium_physics.dll` from the [Delirium physics engine](https://github.com/YoucefNabil/delirium-physics) and its character and animation data files next to the DLL. It is still in development and not in the release zip yet.

## 🙏 Credits

- **adishee**: author of [TarkovIRL](https://forge.sp-tarkov.com/mod/1459/tarkovirl-weapons-handling-mod). TrueView's weapon deadzone system and several handling ideas come from TarkovIRL. Full credit for the original work.
- **YoucefNabil**: author of the [Delirium physics engine](https://github.com/YoucefNabil/delirium-physics), which Euphoria Reactions runs on (`delirium_physics.dll`, GPL-3.0, under its own license).
- **Get-up animations** generated with NVIDIA MotionBricks.
- **Audio Library: Sound Effects** ([YouTube](https://www.youtube.com/@AudioLibrarySoundEffects)): breathing recordings.
- **SPT team**, **BepInEx**, **HarmonyX**.

## 📜 License

Free for personal use. **Modification and redistribution require written permission.**
The Delirium physics engine (`delirium_physics.dll`) is not covered by this license; it stays under GPL-3.0, with its source at the link above.
Contact: Discord `Isaiahnohart`.
