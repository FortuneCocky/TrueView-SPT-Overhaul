# Euphoria Reactions' physics body

`../body.json` is the character the Delirium engine simulates: 21 body parts, 20 joints, their ranges and
muscles, in Delirium's `nm-character-1` format (loaded with `nm_init_character` and `nm_build_from_character`).
It contains no data from any other game. `make_character.py` builds it:

- **From Tarkov:** segment lengths, hip and shoulder widths (`spt_joints.json`, SPT's mapped bones), limb
  thickness (EFT's HitCollider capsules) and total weight (`eft_ragdoll_dump.json`, EFT's ragdoll).
- **Ours:** the neutral standing pose, how the weight splits over the 21 parts, every joint range and every
  muscle value.

```bash
python make_character.py            # writes ../body.json; it ships next to EuphoriaReaction.dll
python harness.py ../body.json      # optional: stand / shot / push test in the engine, no game needed
```

`harness.py` needs `delirium_physics.dll` (path at the top of the file).

## Format notes

- Engine space: Z up, +X the character's left, -Y forward, origin at the pelvis. The joints' `pos`/axes and
  the parts' `frame` are stored in asset space: engine space with x and y negated.
- A part's `frame` rows are `diag(-1,-1,1) · R`, where R's columns are the part's local axes in engine space;
  the build's `frame.rot`/`bind` are the same rotation. The capsule's long axis is local Y.
- The parts' frames are the zero pose, and joint ranges are centred on it, so it has to be a neutral pose.
- Joint frame: z = `twist_axis`, x = `lean_axis` made orthogonal, y = z × x. Hinges turn about z, range in
  `limits[0]`. Ball joints: `limits[0]` turns about y, `limits[1]` about -x, `limits[2]` is the twist.
- Right-side ball joints carry `flip: [false, true, true]` so a behaviour can ask both sides for the same angle.
  Without it the body drifted sideways and never settled.
- `build[].limit` is the physical fallback for a limp body; anchors are recomputed by the engine.

