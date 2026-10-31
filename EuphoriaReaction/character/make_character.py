"""Builds Euphoria Reactions' physics character (nm-character-1 JSON) from Tarkov's own skeleton.

No GTA data: segment lengths and widths come from SPT's mapped bones (spt_joints.json), the total mass from EFT's
ragdoll (eft_ragdoll_dump.json); the neutral pose, segment mass split, joint ranges and muscle values are ours.

Engine space (what the build uses): Z up, +X the character's left, -Y forward, origin at the pelvis.
Asset space (joints[].pos/axes, parts[].frame): engine space with x and y negated.

    python make_character.py [spt_joints.json] [eft_ragdoll_dump.json] [out.json]
"""
import json
import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).parent
F = np.diag([-1.0, -1.0, 1.0])  # engine <-> asset
X, Y, Z = np.eye(3)

# ------------------------------------------------------------------ small maths


def unit(v):
    v = np.asarray(v, float)
    n = np.linalg.norm(v)
    return v / n if n > 1e-9 else v


def frame(x_hint, y, z_hint=None):
    """Right-handed frame with local y exactly along `y`; x as close to `x_hint` as it can be."""
    y = unit(y)
    x = unit(np.asarray(x_hint, float) - y * np.dot(x_hint, y))
    z = np.cross(x, y)
    return np.column_stack([x, y, z])


def joint_frame(twist, lean):
    """The engine's joint frame (nmrs::joints): z = twist axis, x = lean axis made orthogonal, y = z x x."""
    z = unit(twist)
    x = unit(np.asarray(lean, float) - z * np.dot(lean, z))
    return np.column_stack([x, np.cross(z, x), z])


def quat(m):
    """Rotation matrix -> quaternion [x, y, z, w] (glam order)."""
    m = np.asarray(m, float)
    t = np.trace(m)
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        q = [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, 0.25 * s]
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        q = [0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s]
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        q = [(m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s]
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        q = [(m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s, (m[1, 0] - m[0, 1]) / s]
    q = np.array(q)
    return (q / np.linalg.norm(q)).tolist()


def rot(axis, angle):
    a = unit(axis)
    c, s = math.cos(angle), math.sin(angle)
    k = np.array([[0, -a[2], a[1]], [a[2], 0, -a[0]], [-a[1], a[0], 0]])
    return np.eye(3) + s * k + (1 - c) * (k @ k)


def r(v, n=6):
    return [round(float(x), n) for x in v]


# ------------------------------------------------------------------ Tarkov measurements


def measurements(spt_path, eft_path):
    j = {k: np.array(v) for k, v in json.load(open(spt_path))["joints"].items()}
    d = lambda a, b: float(np.linalg.norm(j[a] - j[b]))
    m = {
        "hip_half": (j["Thigh_Left"][0] - j["Thigh_Right"][0]) * 0.5,
        "thigh": (d("Thigh_Left", "Shin_Left") + d("Thigh_Right", "Shin_Right")) * 0.5,
        "shin": (d("Shin_Left", "Foot_Left") + d("Shin_Right", "Foot_Right")) * 0.5,
        "upper_arm": (d("Upper_Arm_Left", "Lower_Arm_Left") + d("Upper_Arm_Right", "Lower_Arm_Right")) * 0.5,
        "forearm": (d("Lower_Arm_Left", "Hand_Left") + d("Lower_Arm_Right", "Hand_Right")) * 0.5,
        "shoulder_half": (j["Upper_Arm_Left"][0] - j["Upper_Arm_Right"][0]) * 0.5,
        "clavicle_half": (j["Clavicle_Left"][0] - j["Clavicle_Right"][0]) * 0.5,
        # heights above the pelvis bone of the spine chain, neck and head pivots
        "z": {k: float(j[k][2] - j["Buttocks"][2]) for k in
              ("Spine0", "Spine1", "Spine2", "Spine3", "Neck", "Head", "Clavicle_Left", "Upper_Arm_Left")},
    }
    eft = json.load(open(eft_path))
    m["mass"] = sum(b["mass"] for b in eft["rigidbodies"] if "Human" in b["path"])
    return m


# ------------------------------------------------------------------ the character

PARTS = ["pelvis", "thigh_l", "calf_l", "foot_l", "thigh_r", "calf_r", "foot_r",
         "spine0", "spine1", "spine2", "spine3", "neck", "head",
         "clavicle_l", "upper_arm_l", "forearm_l", "hand_l", "clavicle_r", "upper_arm_r", "forearm_r", "hand_r"]
PARENT = [None, 0, 1, 2, 0, 4, 5, 0, 7, 8, 9, 10, 11, 10, 13, 14, 15, 10, 17, 18, 19]
JOINTS = ["hip_l", "knee_l", "ankle_l", "hip_r", "knee_r", "ankle_r", "spine_0", "spine_1", "spine_2", "spine_3",
          "neck_lower", "neck_upper", "clavicle_l", "shoulder_l", "elbow_l", "wrist_l",
          "clavicle_r", "shoulder_r", "elbow_r", "wrist_r"]

# share of total body mass per part (segment split after published adult male anthropometry, our own rounding)
MASS = {"pelvis": 0.112, "thigh": 0.1415, "calf": 0.0435, "foot": 0.0135, "spine0": 0.054, "spine1": 0.054,
        "spine2": 0.055, "spine3": 0.118, "neck": 0.012, "head": 0.0575, "clavicle": 0.0095,
        "upper_arm": 0.027, "forearm": 0.016, "hand": 0.006}

# capsule radius per part (m): thighs, calves, upper arms and forearms are EFT's own HitCollider capsules
# (left/right averaged); the rest sized to a clothed Tarkov operator. Feet are boxes.
RADIUS = {"pelvis": 0.115, "thigh": 0.100, "calf": 0.091, "spine0": 0.11, "spine1": 0.115, "spine2": 0.12,
          "spine3": 0.125, "neck": 0.052, "head": 0.095, "clavicle": 0.048, "upper_arm": 0.074, "forearm": 0.045,
          "hand": 0.04}
# torso capsules lie sideways: half length of the straight part across the body
TORSO_HALF = {"pelvis": 0.06, "spine0": 0.055, "spine1": 0.06, "spine2": 0.065, "spine3": 0.07}
FOOT_BOX = [0.05, 0.035, 0.125]  # half extents: across, up, heel-toe

# Joint ranges (radians), our own, from typical adult ranges of motion, a little inside them.
#   hinge: (lo, hi) about the hinge axis.
#   ball: [(l1 lo, hi), (l2 lo, hi), (twist lo, hi)]; l1 turns about the joint frame's y, l2 about its -x.
# Muscles: (stiffness, damping, strength, max torque swing, max torque twist), ours.
LEG, SPINE, NECK, ARM, END = (0.5, 22.0, 120.0, 350.0, 350.0), (0.5, 20.0, 100.0, 300.0, 300.0), \
    (0.5, 12.0, 60.0, 150.0, 150.0), (0.5, 15.0, 80.0, 200.0, 200.0), (0.5, 10.0, 50.0, 120.0, 120.0)
# tuning sweeps: scale every muscle's stiffness / damping / strength (env MC_STIFF, MC_DAMP, MC_STR)
_k = lambda n: float(__import__("os").environ.get(n, "1"))
LEG, SPINE, NECK, ARM, END = [(m[0] * _k("MC_STIFF"), m[1] * _k("MC_DAMP"), m[2] * _k("MC_STR"), m[3], m[4])
                              for m in (LEG, SPINE, NECK, ARM, END)]


def build_pose(m):
    """Joint pivots and part centres/frames of the neutral standing pose (engine space)."""
    hw, lt, ls = m["hip_half"], m["thigh"], m["shin"]
    z = m["z"]
    spine = [z["Spine0"], z["Spine1"], z["Spine2"], z["Spine3"]]
    neck_lo, neck_hi = z["Neck"], z["Head"]
    abd = math.radians(12)  # arms hang a little out from the body
    piv, cen, fr = {}, {}, {}

    for s, sx in (("l", 1.0), ("r", -1.0)):
        hip = np.array([sx * hw, 0.0, 0.0])
        knee = hip + np.array([0.0, 0.0, -lt])
        ankle = knee + np.array([0.0, 0.0, -ls])
        piv["hip_" + s], piv["knee_" + s], piv["ankle_" + s] = hip, knee, ankle
        cen["thigh_" + s], cen["calf_" + s] = (hip + knee) / 2, (knee + ankle) / 2
        cen["foot_" + s] = ankle + np.array([0.0, -0.055 * _k("MC_FOOT_FWD"), -FOOT_BOX[1] - 0.025])
        for p in ("thigh_", "calf_", "foot_"):
            fr[p + s] = frame(-X, Z)  # y up the leg, x across (-X), z back

        clav = np.array([sx * 0.025, 0.0, z["Clavicle_Left"]])
        shoulder = np.array([sx * m["shoulder_half"], 0.0, z["Upper_Arm_Left"]])
        down = np.array([sx * math.sin(abd), 0.0, -math.cos(abd)])
        elbow = shoulder + down * m["upper_arm"]
        wrist = elbow + down * m["forearm"]
        piv["clavicle_" + s], piv["shoulder_" + s], piv["elbow_" + s], piv["wrist_" + s] = clav, shoulder, elbow, wrist
        cen["clavicle_" + s] = (clav + shoulder) / 2
        cen["upper_arm_" + s], cen["forearm_" + s] = (shoulder + elbow) / 2, (elbow + wrist) / 2
        cen["hand_" + s] = wrist + down * 0.085
        fr["clavicle_" + s] = frame(Z, -sx * X)  # y along the clavicle toward the centre, x up
        for p in ("upper_arm_", "forearm_", "hand_"):
            fr[p + s] = frame(-X, -down)

    cen["pelvis"] = np.array([0.0, 0.0, 0.0])
    fr["pelvis"] = frame(Z, X)  # x up, y across (capsule lies sideways), z back
    tops = spine[1:] + [neck_lo]
    for k in range(4):
        piv["spine_%d" % k] = np.array([0.0, 0.0, spine[k]])
        cen["spine%d" % k] = np.array([0.0, 0.0, (spine[k] + tops[k]) / 2])
        fr["spine%d" % k] = frame(Z, X)
    piv["neck_lower"], piv["neck_upper"] = np.array([0.0, 0.0, neck_lo]), np.array([0.0, 0.0, neck_hi])
    cen["neck"] = np.array([0.0, 0.0, (neck_lo + neck_hi) / 2])
    cen["head"] = np.array([0.0, -0.02, neck_hi + 0.1])
    fr["neck"], fr["head"] = frame(-X, Z), frame(-X, Z)
    return piv, cen, fr


def joint_specs():
    """twist axis, lean axis (engine space, neutral pose), ranges and muscles for each joint."""
    s = {}
    for side, sx in (("l", 1.0), ("r", -1.0)):
        out = lambda lo, hi: (lo, hi) if sx > 0 else (-hi, -lo)  # a mirrored range for the other side
        # hip: l1 = flex (+) / extend, l2 = adduct for the left (+), twist about the thigh
        s["hip_" + side] = (-Z, -Y, [(-0.35, 1.95), out(-0.75, 0.35), (-0.6, 0.6)], LEG)
        s["knee_" + side] = (-X, None, [(-2.35, _k("MC_KNEE_HI") - 1)], LEG)            # bends back (negative)
        # ankle: twist along the foot, l1 = toes up (+) / down, l2 = toes in / out
        s["ankle_" + side] = (-Y, Z, [(-0.75, 0.35), (-0.3, 0.3), (-0.35, 0.35)], END)
        # clavicle: twist along it, l1 = shoulder forward / back, l2 = shoulder down (L) / up
        s["clavicle_" + side] = (sx * X, -Y, [(-0.25, 0.35) if sx > 0 else (-0.35, 0.25), out(-0.5, 0.15),
                                               (-0.2, 0.2)], ARM)
        # shoulder: twist down the arm, l1 = arm forward (+) / back, l2 = in (L, +) / out
        down = np.array([sx * math.sin(math.radians(12)), 0.0, -math.cos(math.radians(12))])
        s["shoulder_" + side] = (down, -Y, [(-0.9, 2.4), out(-2.0, 0.6), (-1.1, 1.1)], ARM)
        s["elbow_" + side] = (-X, None, [(0.0, 2.5)], ARM)             # bends forward (positive)
        s["wrist_" + side] = (down, -Y, [(-0.5, 0.5), (-0.9, 0.9), (-1.0, 1.0)], END)
    for k, (flex, side_bend, tw) in enumerate([(0.3, 0.2, 0.12), (0.3, 0.2, 0.2), (0.3, 0.2, 0.25), (0.25, 0.2, 0.25)]):
        # l1 = bend forward (+) / back, l2 = bend to the left (+), twist about the spine
        s["spine_%d" % k] = (Z, -Y, [(-flex * 0.6, flex), (-side_bend, side_bend), (-tw, tw)], SPINE)
    s["neck_lower"] = (Z, -Y, [(-0.4, 0.6), (-0.4, 0.4), (-0.6, 0.6)], NECK)
    s["neck_upper"] = (Z, -Y, [(-0.4, 0.4), (-0.3, 0.3), (-0.5, 0.5)], NECK)
    return s


def part_kind(name):
    for k in ("thigh", "calf", "foot", "clavicle", "upper_arm", "forearm", "hand"):
        if name.startswith(k):
            return k
    return name


def make(m):
    piv, cen, fr = build_pose(m)
    specs = joint_specs()
    shares = sum(MASS[part_kind(p)] for p in PARTS)
    total = m["mass"] / shares  # MASS is relative: scaled so the parts weigh what EFT's ragdoll does

    joints, jframes = [], {}
    for i, jn in enumerate(JOINTS):
        twist, lean, limits, mus = specs[jn]
        three = lean is not None
        if three:
            jf = joint_frame(twist, lean)
        else:
            zz = unit(twist)
            xx = np.cross(zz, Z)
            xx = unit(xx if np.linalg.norm(xx) > 1e-6 else np.cross(zz, X))
            jf = np.column_stack([xx, np.cross(zz, xx), zz])
            limits = limits + [(0.0, 0.0), (0.0, 0.0)]
        jframes[i + 1] = (jf, limits, three)
        joints.append({
            "name": jn, "index": i, "three": three,
            "pos": r(F @ piv[jn]), "twist_axis": r(F @ unit(twist)), "lean_axis": r(F @ unit(lean)) if three else [0.0, 0.0, 0.0],
            "limits": [r(l) for l in limits],
            # right-side ball joints: lean 2 and twist are mirrored, so a behaviour can ask both sides for the same angle
            "flip": [False, three and jn.endswith("_r"), three and jn.endswith("_r")],
            "muscle_stiffness": mus[0], "damping": mus[1], "strength": mus[2],
            "max_torque": [mus[3], mus[4] if three else 0.0],
        })

    parts, build = [], []
    for k, pn in enumerate(PARTS):
        kind = part_kind(pn)
        R = fr[pn]
        mass = total * MASS[kind]
        parts.append({"name": pn, "frame": [r(row) for row in (F @ R)] + [r(F @ cen[pn])], "mass": round(mass, 4)})

        if kind in TORSO_HALF:
            radius, half = RADIUS[kind], TORSO_HALF[kind]
        elif kind == "foot":
            radius, half = FOOT_BOX[1], 0.0
        elif kind in ("head", "hand"):
            radius, half = RADIUS[kind], 0.02
        else:
            seg = {"thigh": m["thigh"], "calf": m["shin"], "upper_arm": m["upper_arm"], "forearm": m["forearm"],
                   "clavicle": m["shoulder_half"] - 0.025, "neck": m["z"]["Head"] - m["z"]["Neck"]}[kind]
            radius = RADIUS[kind]
            half = max(0.0, seg / 2 - radius * 0.6)

        pa = PARENT[k]
        if pa is None:
            limit, rest = "locked", [0.0, 0.0, 0.0, 1.0]
        else:
            jf, limits, three = jframes[k]
            Rp = fr[PARTS[pa]]
            rest = quat(R.T @ Rp)  # child -> parent at rest (ragdoll.rs Joint::new, locked/cone fallback)
            if three:
                mid = [(lo + hi) / 2 for lo, hi in limits]
                half_r = [(hi - lo) / 2 for lo, hi in limits]
                centre = rot(jf[:, 2], mid[2]) @ rot(jf[:, 1], mid[0]) @ rot(-jf[:, 0], mid[1])
                limit = {"bone": {"parent_frame": quat(Rp.T @ centre @ jf), "child_frame": quat(R.T @ jf),
                                  "lean": r([half_r[0], half_r[1]]), "twist": round(max(half_r[2], 0.01), 6)}}
            else:
                limit = {"bone_hinge": {"parent_frame": quat(Rp.T @ jf), "child_frame": quat(R.T @ jf),
                                        "lo": limits[0][0], "hi": limits[0][1]}}
        q = quat(R)
        entry = {
            "name": pn, "parent": pa, "mass": round(mass, 4), "radius": radius, "half": round(half, 4),
            "offset": {"pos": [0.0, 0.0, 0.0], "rot": [0.0, 0.0, 0.0, 1.0]}, "rest": r(rest),
            "anchor_parent": [0.0, 0.0, 0.0], "anchor": [0.0, 0.0, 0.0],  # nm_build_from_character recomputes these
            "limit": limit, "lower": k <= 6, "reach": kind == "upper_arm", "bind": r(q),
            "inertia": [0.0, 0.0, 0.0], "frame": {"pos": r(cen[pn]), "rot": r(q)},
        }
        if kind == "foot":
            entry["boxed"] = FOOT_BOX
        build.append(entry)

    # self-collision: parts more than two links apart, plus the two thighs
    def hops(a, b):
        up = lambda k: [k] + (up(PARENT[k]) if PARENT[k] is not None else [])
        pa, pb = up(a), up(b)
        common = next(x for x in pa if x in pb)
        return pa.index(common) + pb.index(common)
    pairs = [[a, b] for a in range(21) for b in range(a + 1, 21) if hops(a, b) > 2] + [[1, 4]]
    return {"format": "nm-character-1", "joints": joints, "parts": parts, "self_collision": pairs, "build": build}


def main():
    a = sys.argv[1:] + [None] * 3
    spt = a[0] or HERE / "spt_joints.json"
    eft = a[1] or HERE / "eft_ragdoll_dump.json"
    out = Path(a[2] or HERE.parent / "body.json")  # next to the plugin project
    m = measurements(spt, eft)
    c = make(m)
    out.write_text(json.dumps(c, indent=1))
    print(f"{out}: {len(c['joints'])} joints, {len(c['parts'])} parts, {len(c['self_collision'])} pairs, "
          f"{sum(p['mass'] for p in c['parts']):.1f} kg; thigh {m['thigh']:.3f} shin {m['shin']:.3f} "
          f"arm {m['upper_arm']:.3f}+{m['forearm']:.3f} hips {2 * m['hip_half']:.3f} m")


if __name__ == "__main__":
    main()




