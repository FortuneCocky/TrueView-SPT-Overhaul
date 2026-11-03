# TrueView

**One pack that makes Tarkov feel physical.** Your gun has weight and lags behind your aim, your body moves like a person's, your eyes focus like eyes, and every hit bleeds in full 3D.

TrueView bundles mods that are built to work together:

| Mod | What it does |
|---|---|
| **TrueView** | Weapon handling and body movement: weight, deadzone, sway, momentum, a body camera, near misses |
| **Tactical Stances** | Low ready, high ready, active aim, canted aim and shoulder swap |
| **Dynamic DoF** | Eye-like depth of field that focuses on whatever you look at |
| **BloodFX** | Fully volumetric blood: spurts, drips, trails, pools, wounds, armor impacts and head dismemberment |
| **Euphoria Reactions** | Physics-driven hit reactions: bots stagger, catch themselves, buckle, fall and get back up |
| **HardcoreDMG** | A deadlier damage model: anatomical torso zones, reworked body HP, bullet physics, shotguns, wound reactions (server + client, optional) |

Each one has its own config file. Turn any of them off or tune it however you like.

---

## 🔫 TrueView — weapon handling

Your weapon stops being glued to the centre of the screen.

- **Weight you can feel.** Heavy guns aim slower, sway more and settle harder. Pistols snap around.
- **Aim deadzone.** The gun moves freely inside a small cone and the camera only follows when it reaches the edge.
- **Hip-fire deadzone.** When you aren't aiming, the muzzle drifts naturally and your wrist rolls.
- **Free aim.** "Return to center" (hip and ADS) sets how strongly the gun drifts back to the middle of the view. At 0 it stays where you left it inside the deadzone, and the camera never locks while you fire. After you stop firing, the camera settles back a set amount, and in hip fire it follows the gun up or down past a few degrees.
- **Sway and inertia.** Breathing, walking, fatigue and the weapon's own momentum all move the gun.
- **Camera lag.** Your head trails the weapon instead of being bolted to it.
- **Recoil that misaligns.** Every shot knocks the gun off target, and tired arms make it worse.
- **Hand kick.** In hip fire, each shot kicks the gun up and back around your grip, and it settles in about a tenth of a second.
- **One recoil switch.** "Disable TrueView recoil" turns off every recoil effect TrueView adds (shot jolt, hand kick, misalignment) and keeps only the game's own recoil.
- **Collision-aware ADS.** The gun pulls back near walls instead of clipping through them.
- **Barrel alignment (optional, off by default).** In hip fire, lines the barrel up with where the deadzone is aiming, so shots go where the gun is pointed on screen, the same on every weapon.
- **Magnified scopes.** Looking through a 2x or stronger optic hands the gun, view and sway back to the game, so the scope picture stays steady and attached to the lens. At the hip, a mounted scope rides a little lower so it doesn't cover your view.

## 🏃 TrueView — body and camera

- **Body camera.** The view rides your chest like a bodycam: it trails a little behind turns, rolls with each uneven footstep, jolts with every shot without climbing, knocks back on hits and near misses, drifts when you're hurt and rises and falls with your breathing. It only moves what you see, so your gun, aim and deadzone are never thrown off, and it fades out while aiming.
- **Momentum.** Speed builds up as your body pushes off: about 0.3 s to a walk, 0.45 s to a run and 0.65 s into a sprint, longer when you're loaded, tired or hurt. Changing direction brakes first. It never makes you faster than the game would.
- **Start, stop and strafe lean.** Your head leans into a move as you get going, tips into the brake when you stop and rolls into strafes. Turns bank your body, and Q/E lean is real.
- **Turn-in-place footwork.** Small turns twist your torso. Bigger ones make your feet step, with a weight-scaled dip. Heavy gear makes the step slower and heavier.
- **Landing and crouch dips.** The camera dips when you land from a fall (harder the further you fell) and when you crouch.
- **Focus zoom.** A subtle tunnel-vision zoom when you settle on a target while aiming (off while mounted).
- **Field of view (optional, off by default).** A field of view slider that goes past the game's limit of 75, up to 110. Sprinting, aiming and scopes still work from your new FOV. Camera pull-back sliders move your view back from the gun so you see more of it, at the hip and with iron sights or red dots.

## ❤️ TrueView — stress and injury

- **Adrenaline.** Taking hits or hearing gunfire nearby raises adrenaline. It causes a slight hand tremor and a tunnel-vision pinch, then decays over several seconds. Being shot at brings a soft, slow heartbeat you feel in your chest; getting hit spikes it to a loud, fast one that slows down as you calm.
- **Near misses.** A round passing close makes you flinch away from the side it passed, harder the closer it came, and raises adrenaline. Custom whiz sounds can replace the game's flyby sound.
- **Breathing.** You hear and feel breathing that matches your stamina, from calm nose breathing to heavy panting after a sprint. Holding your breath takes a breath in and silences it; letting go blows it out, harder after a long hold.
- **Calming down.** After a fight, heavy breathing slowly settles into nose breathing: a short calm-down after a distant shot, a long one after a real firefight. A fresh hit or nearby fire cuts straight back to heavy breathing.
- **Injuries matter.** Wounded arms shake and slow your handling. Crippled legs force a limp.
- **Tired arms.** Sway grows as arm stamina drains, steeply near empty, and below about a third your arms start to tremble. Holding your breath steadies them.
- **Stamina.** Exhaustion adds tremor and sway.
- **Reload efficiency.** Reload speed follows your weapon's ergonomics and the health of your arms.

---

## 🎯 Tactical Stances

*Inspired by Fontaine's [SPT Stance Overhaul](https://sp-mod.com/mod/3136/spt-stance-overhaul).*

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
- **Weapon swap speed.** Drawing a gun takes longer the heavier it is as carried, with its attachments, mag and ammo.
- **Arm stamina.** Holding a stance drains or restores stamina based on weapon weight. LMGs are heavy, pistols are cheap.
- **Stance bonuses.** Each stance has its own configurable perks (sprint acceleration, move speed, ADS move speed, recoil).
- **Your stance is remembered** between raids.
- **Scope clearance.** When you're not aiming, a big optic that would cover the middle of your screen pushes the gun down until your view is clear, in any stance. Prone, the scope is held against the game's turn-away pull.

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
- **Exit spurts and ribbons.** Blood pours from wounds as one continuous rope of liquid that sags, thins, pinches off into beads and peels off side strands. Where it lands it pools, streaks along the floor or runs down the wall.
- **Headshots** throw forked ribbons sideways across your view, with a dense cloud of drops. Close to a wall, they paint it with layered spatter, a dark core and drips that creep down for seconds.
- **Brain matter.** Headshots can throw glossy chunks and skull chips that tumble, splat and slide down walls.
- **Head dismemberment.** A killing headshot can take off half the face or the whole head, leaving a ragged wound or neck stump that pumps and drips. Helmets, masks and glasses fly off as separate pieces. It's visual only: the real items stay on the body.
- **Mist burst.** Every hit puffs a dense dark-red burst that tears into a web of fine strands and droplets within a fraction of a second.
- **Spray.** Drops in realistic sizes (many tiny, a few heavy) that fall and splatter wherever they hit.

**When it lands**
- **Drops stick where they hit**, as small domed beads with tails pointing the way they flew.
- **Wall splashes** burst across the surface and leave a stain made of real beads.
- **Ground trails.** Blood that hits the ground while moving drags out into a thick, ragged, glossy smear.
- **Drips.** Wounds keep forming hanging teardrops that let go and fall. Each one lands as a bead that merges with the ones before it into a growing blob. A bleeding character leaves a trail of real drops instead of the game's flat stains.
- **Pools.** Bodies leave a thick, glossy pool that oozes out from underneath over half a minute and stops at ledges instead of hanging in the air.
- **Decals follow surfaces.** Marks bend over steps and bumps, and nothing floats past edges or doorways.
- **Bloody footprints.** Step in a pool and your boots leave tread prints that fade as they dry, with a wet splash on each bloody step.

**Bodies**
- **Dead weight.** Corpses carry the weight of their gear, and bullets still move them after death.
- **Dragging.** Hold interact on a body to drag it. A bleeding body smears blood along the floor behind it, with a quiet scrape.

**On the body**
- **Bullet wounds** are real size: a dark wet hole with a ragged ring of torn flesh, some with a trickle running down.
- **Armor hits** leave a grey and black mark instead of blood: punch mark, cracks and grainy dust.
- **Wrapped onto the character.** Wounds and armor marks are cut from the character's own model, so they wrap over plates, pouches, folds and skin and move with the body.

**Performance and control**
- **Quality tiers** (Low, Medium, High) and hard caps on everything.
- **Adapts to your frame rate.** When frames get slow, it spawns fewer drops and strands until they recover. The heavy gore meshing runs on a background thread.
- **Distance culling.** Only hits you can see spawn effects.
- **Toggles** for each effect: wounds, drips, pools, streams, armor marks, and blood from hits on you.

---

## 💥 Euphoria Reactions

Bots react to being shot with their whole body, driven by a real-time physics simulation instead of canned animations. The bullet's push lands where it hit, the body's muscles and balance fight it, and what happens next is different every time.

**Getting hit**
- **Stumbles.** A solid hit hands the bot to physics. He takes real steps (his feet don't skate), buckles, staggers, and either rights himself or goes over. How long a stumble can last is a preset (Short, Natural, Long) or your own range.
- **Balance presets.** Engine, Tough, Classic or Natural set how hard he fights to stay up.
- **The gun stays in his hand.** It's held by its own grip point the whole time, so it never floats off while he stumbles or falls.
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
- **He recovers by health.** A healthy bot gets back to standing, a hurt one to a crouch or deep crouch, and a badly hurt one stays prone. Getting up into a crouch rises straight into it instead of standing and sinking.
- **Smooth hand-back.** The return to his normal animation keeps the motion he was in and eases out, with no frozen pose sliding into place.
- **Fall damage.** Crashing into the ground or a wall hurts a living bot.

**Deaths**
- **Death falls.** Lethal hits carry the body down limp, under physics, until it settles.
- **Agony.** Some lethal torso hits become a long death: stagger, collapse, writhing on the ground. Then the normal ragdoll takes over.

**Fair and stable**
- **Bots don't act while reacting.** No shooting (except an aim-back), moving, healing or AI until they've fully recovered.
- **Fail-safes.** Any bot stuck, wedged or draped too long is put back into the nearest stance.
- **Smooth at any frame rate.** The physics runs at 60 Hz and the body is interpolated between steps.
- **Light on performance.** Every reacting bot's physics runs side by side on worker threads, and the map scan around a bot is cast in one batch.
- **Every chance, force and timing is configurable.**

---

## 🩻 HardcoreDMG

A deadlier, more anatomical damage model. It has a server part (body HP) and a client part (how damage lands), and it is optional: the rest of the pack works without it.

**Body**
- **Reworked HP.** Every bot group is rebuilt from one base body (head 22, chest 115, stomach 105, arms 58, legs 68). Bosses, the Goons, Killa, Tagilla and friends get their own builds on top.
- **Toughness per tier.** Scale regulars, elites (guards, raiders, rogues, cultists) and bosses separately, and your own HP too.

**Damage**
- **Anatomical torso zones.** Heart, spine, flanks and pelvis each have their own damage, lethality and bleeding.
- **Damage scales** for head, chest, stomach, arms and legs, plus heavy and light bleed odds.
- **Bullet physics.** Fragmentation, penetration, energy lost through armor and ricochets.
- **Shotgun rework.** Pellets carry real energy: devastating point blank, fading with spread and range, and tighter clusters hit harder. Buckshot and slugs each have their own blunt trauma through armor.

**Wounds**
- **Falls on leg wounds.** A badly hurt leg can drop a bot to the ground.
- **Stance lock.** A bot with shattered legs can't get up from crouch or prone until they're healed.
- **Leg cripple.** Bots with ruined legs crawl.
- **Optional for you:** the leg effects can apply to you too, and a bad arm wound can make you drop your weapon. Both are off by default.

**Config Editor.** `SPT/user/mods/HardcoreDMG/HardcoreDMG Config Editor.exe` edits the server and client settings in one window, with built-in presets and presets you can share.

---

## 📦 Install

1. Download the latest zip from [Releases](https://github.com/FortuneCocky/TrueView-SPT-Overhaul/releases).
2. Extract it into your **SPT root folder** (the one with `EscapeFromTarkov.exe`) and merge the `BepInEx` and `SPT` folders. HardcoreDMG's server mod goes to `SPT/user/mods/HardcoreDMG`; if your server folder has another name, put it in that folder's `user/mods`.
3. Restart the SPT server and launch the game once to generate the configs:
   - `BepInEx/config/com.trueview.cfg`
   - `BepInEx/config/com.devin.tacticalstances.cfg`
   - `BepInEx/config/com.dynamicdof.prism.cfg`
   - `BepInEx/config/com.irl.bloodfx.cfg`
   - `BepInEx/config/com.euphoria.reaction.cfg`
   - `BepInEx/config/com.devin.hardcoredmg.client.cfg`

Every client setting can also be changed in game through the **BepInEx Configuration Manager (F12)**. HardcoreDMG's server settings are in `SPT/user/mods/HardcoreDMG/config.jsonc` or its Config Editor.

To play without HardcoreDMG, delete `SPT/user/mods/HardcoreDMG` and `BepInEx/plugins/HardcoreDMG`.

## ✅ Compatibility

- **SPT 4.1.x.** Everything is client only except HardcoreDMG, which also has a server mod.
- Other damage or health mods (SPT Realism's health system, for example) will likely conflict with HardcoreDMG.
- All the mods are designed to stack. Tactical Stances layers on top of TrueView's weapon motion, and Dynamic DoF keeps your sights sharp while TrueView moves the gun.
- **Fontaine's [SPT Stance Overhaul](https://sp-mod.com/mod/3136/spt-stance-overhaul)** works with TrueView (it needs Fontaine's Realism common lib). While you hold one of its stances at hip, TrueView hands the gun to the stance and takes it back when the stance ends; aiming down sights stays TrueView's. Pick either Stance Overhaul or Tactical Stances, not both: two stance mods will fight over the gun.
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
| `HardcoreDMG/Client/` | HardcoreDMG-Client.dll | `com.devin.hardcoredmg.client.cfg` |
| `HardcoreDMG/Server/` | HardcoreDMG.dll (server mod) | `config.jsonc` |
| `HardcoreDMG/ConfigEditor/` | HardcoreDMG Config Editor.exe | |

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) and have an SPT 4.1.x install.
2. Point the `SPTDir` (or `SPT_INSTALL_DIR`) property in each `.csproj` at your SPT folder. The game and BepInEx assemblies are referenced from there and are not included here.
3. Run `dotnet build -c Release` in a mod's folder, or its `build_*.bat`, which also copies the DLL into your SPT plugins folder.

Euphoria Reactions also needs `delirium_physics.dll` from the [Delirium physics engine](https://github.com/YoucefNabil/delirium-physics) and `getups.bin` (the get-up clips) next to its DLL. Its `body.json` and `presets/` are in this repo and are copied by `build_euphoria.bat`.

## 🙏 Credits

- **adishee**: author of [TarkovIRL](https://forge.sp-tarkov.com/mod/1459/tarkovirl-weapons-handling-mod). TrueView was inspired by TarkovIRL: the ideas behind its weapon deadzone system and several handling features came from it (no TarkovIRL code is used). Full credit to adishee for those ideas.
- **Fontaine**: author of [SPT Stance Overhaul](https://sp-mod.com/mod/3136/spt-stance-overhaul) and the stance system in SPT Realism Mod. Tactical Stances was inspired by Fontaine's stances: its feature set (high ready, low ready, active aim, canted aim and the mechanics around them) follows that work. Full credit to Fontaine for those ideas.
- **YoucefNabil**: author of the [Delirium physics engine](https://github.com/YoucefNabil/delirium-physics), which Euphoria Reactions runs on (`delirium_physics.dll`, GPL-3.0, under its own license).
- **Get-up animations** generated with NVIDIA MotionBricks.
- **Audio Library: Sound Effects** ([YouTube](https://www.youtube.com/@AudioLibrarySoundEffects)): breathing recordings.
- **SPT team**, **BepInEx**, **HarmonyX**.

## 📜 License

Free for personal use. **Modification and redistribution require written permission.**
The Delirium physics engine (`delirium_physics.dll`) is not covered by this license; it stays under GPL-3.0, with its source at the link above.
Contact: Discord `Isaiahnohart`.
