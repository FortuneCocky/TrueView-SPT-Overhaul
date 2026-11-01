"""Tunes our physics body and balance preset offline, in the real engine (delirium_physics.dll), no game needed.

A reference character is put through a fixed set of hits (where, from which side, how hard) and what it does is
measured: whether and when it falls, how far it staggers, how low it gets. Our body (make_character.py) and our
balance values are then searched until ours does the same. Only the behaviour is compared: nothing is read out
of the reference's files, and they are never copied, written or shipped. The output is our own values:

    python tune_balance.py ref  <dir>          # measure the reference (dir holds its .xrt, build and params)
    python tune_balance.py ours                # measure ours as it is (body.json + presets/balance_engine.txt)
    python tune_balance.py tune <dir> [-n N]   # search; writes tuned_body.json and tuned_balance.txt here

ref.json (the reference's measurements) stays next to this script, and is git-ignored like the reference.
"""
import ctypes as C
import json
import math
import multiprocessing as mp
import os
import random
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
DLL = Path(__file__).resolve().parents[2] / "delirium-phys" / "target" / "release" / "delirium_physics.dll"
f3 = C.c_float * 3
f4 = C.c_float * 4
DT, SECS = 1 / 60, 6.0
SAMPLE = (0.5, 1.0, 2.0, 4.0, 6.0)  # seconds at which the stagger and height are taken
SET = b"POWERFUL_WEAPON"            # the tuning set the mod's shots ask for

# the hits: height on the body (fraction of standing height), side it comes from, push (N*s, the mod's range)
HEIGHTS = {"chest": 0.72, "belly": 0.58, "thigh": 0.42}
SIDES = {"front": (0, 1), "back": (0, -1), "left": (-1, 0), "right": (1, 0)}  # travel of the bullet, engine x/y
PUSHES = (25.0, 50.0, 100.0)
SCENARIOS = [(h, s, p) for h in HEIGHTS for s in SIDES for p in PUSHES]
REPEATS = int(os.environ.get("TB_REPEATS", "8"))  # the engine's reactions are random: each hit is averaged over runs

# what we tune. Task values: (task, name, engine default, low, high). Body: muscle scales for make_character.py.
TASK = [
    ("NMShot_Standing", "shotMaxSteps", 6, 2, 12),
    ("NMShot_Standing", "shotBodyStiffness", 13.5, 8, 18),
    ("NMShot_Standing", "shotLooseness", 1.0, 0.2, 1.5),
    ("NMShot_Standing", "shotUprightStiffness", 3.0, 0.5, 8),
    ("NMShot_Standing", "shotLeanAmount", 0.3, 0.05, 0.8),
    ("NMShot_Standing", "shockSpinMin", 40, 5, 80),
    ("NMShot_Standing", "shockSpinMax", 80, 10, 160),
    ("NMShot_Standing", "shotTimeBeforeCollapseWoundLeg", 1.0, 0.2, 3),
    ("NMShot_IsMoving", "balanceBodyStiffness", 12, 7, 17),
    ("NMShot_IsMoving", "balanceMaxSteps", 3, 1, 8),
    ("NMShot_IsMoving", "balanceLeanAmount", 0.4, 0.05, 0.9),
    ("NMShot_IsMoving", "balanceBodyDamping", 0.9, 0.4, 1.5),
]
BODY = [("MC_STIFF", 1.0, 0.5, 2.0), ("MC_DAMP", 1.0, 0.5, 2.0), ("MC_STR", 1.0, 0.5, 2.0)]
INTS = {"shotMaxSteps", "balanceMaxSteps"}


def load():
    d = C.CDLL(str(DLL))
    P, I, Fp, S = C.c_void_p, C.c_int, C.POINTER(C.c_float), C.c_char_p
    sig = {
        "nm_last_error": (I, [C.c_char_p, I]), "nm_init_character_file": (I, [S]), "nm_init": (I, [S]),
        "nm_build_from_character": (P, [S]), "nm_build_load": (P, [S]), "nm_load_task_params": (I, [S]),
        "nm_set_task_param": (I, [S, S, S, C.c_float]),
        "nm_world_create": (P, []), "nm_world_add_floor": (I, [P, C.c_float, C.c_float, C.c_float, C.c_float]),
        "nm_agent_create": (P, [P, Fp, C.c_float, C.c_float]), "nm_agent_step": (I, [P, P, C.c_float]),
        "nm_agent_task_balance": (I, [P, P, S, Fp]), "nm_agent_get_com": (I, [P, Fp, Fp]),
        "nm_agent_has_fallen": (I, [P]), "nm_agent_is_settled": (I, [P]),
        "nm_agent_get_part_transform": (I, [P, I, Fp, Fp]), "nm_agent_part_count": (I, [P]),
        "nm_agent_task_shot": (I, [P, P, S, I, Fp, Fp, C.c_float]),
        "nm_agent_apply_push": (I, [P, Fp, Fp]), "nm_agent_nearest_part": (I, [P, Fp]),
    }
    for n, (res, args) in sig.items():
        fn = getattr(d, n)
        fn.restype, fn.argtypes = res, args
    return d


def err(d):
    b = C.create_string_buffer(512)
    d.nm_last_error(b, 512)
    return b.value.decode(errors="replace")


_d = _build = None


def _setup(kind, src, params):
    """Load one character into this process: ('ref', dir) or ('ours', body json path), plus task values."""
    global _d, _build
    _d = _d or load()
    if kind == "ref":
        dirp = Path(src)
        xrt = next(dirp.glob("*.xrt"))
        build = next(p for p in dirp.glob("*.txt") if "build" in p.name)
        tp = next(p for p in dirp.glob("*.txt") if "param" in p.name)
        if _d.nm_init(str(xrt).encode()) < 0: raise RuntimeError("init: " + err(_d))
        _d.nm_load_task_params(str(tp).encode())
        _build = _d.nm_build_load(str(build).encode())
    else:
        if _d.nm_init_character_file(str(src).encode()) < 0: raise RuntimeError("init: " + err(_d))
        _d.nm_load_task_params(str(HERE / "empty_params.txt").encode())  # back to the engine's defaults
        _build = _d.nm_build_from_character(Path(src).read_text().encode())
        for (task, name, *_), v in zip(TASK, params or []):
            _d.nm_set_task_param(task.encode(), b"*", name.encode(), float(v))
    if not _build: raise RuntimeError("build: " + err(_d))


def run_one(scn):
    """One hit on a fresh standing character; returns its measurements."""
    d, (height, side, push), seed = _d, scn[:3], (scn[3] if len(scn) > 3 else 0) / max(1, REPEATS)
    w = d.nm_world_create()
    d.nm_world_add_floor(w, 0.0, 0.0, 0.0, 30.0)
    a = d.nm_agent_create(_build, f3(0, 0, 0), 0.0, seed)
    com, vel, pos, q = f3(), f3(), f3(), f4()
    d.nm_agent_task_balance(a, w, b"*", None)
    for _ in range(int(1.5 / DT)):  # let him settle into his stance
        d.nm_agent_step(a, w, DT)
    top = 0.0
    for k in range(d.nm_agent_part_count(a)):
        d.nm_agent_get_part_transform(a, k, pos, q)
        top = max(top, pos[2])
    d.nm_agent_get_com(a, com, vel)
    x0, y0, h0 = com[0], com[1], com[2]
    sx, sy = SIDES[side]
    at = f3(x0 - sx * 0.12, y0 - sy * 0.12, top * HEIGHTS[height])  # on the near side of the body
    part = d.nm_agent_nearest_part(a, at)
    d.nm_agent_apply_push(a, at, f3(sx * push, sy * push, 0))
    d.nm_agent_task_shot(a, w, SET, max(part, 0), at, f3(sx, sy, 0), 100.0)
    fell, settled, disp, low = SECS, SECS, [], []
    t = 0.0
    for i in range(int(SECS / DT)):
        d.nm_agent_step(a, w, DT)
        t += DT
        if fell == SECS and d.nm_agent_has_fallen(a): fell = t
        if i % 6 == 5:
            d.nm_agent_get_com(a, com, vel)
            if any(abs(t - s) < DT / 2 for s in SAMPLE):
                disp.append(math.hypot(com[0] - x0, com[1] - y0))
                low.append(com[2] / h0)
    d.nm_agent_get_com(a, com, vel)
    while len(disp) < len(SAMPLE):
        disp.append(math.hypot(com[0] - x0, com[1] - y0)); low.append(com[2] / h0)
    return {"fell": fell < SECS, "t_fall": fell, "disp": disp, "height": low}


def _task(job):
    kind, src, params, scn = job
    key = (kind, str(src), tuple(params or ()))
    if getattr(_task, "key", None) != key:
        _setup(kind, src, params)
        _task.key = key
    return run_one(scn)


def measure(pool, kind, src, params=None):
    """Each hit's measurements averaged over REPEATS runs (fell becomes the fall rate)."""
    runs = pool.map(_task, [(kind, src, params, s + (r,)) for s in SCENARIOS for r in range(REPEATS)], chunksize=4)
    out = []
    for i in range(len(SCENARIOS)):
        g = runs[i * REPEATS:(i + 1) * REPEATS]
        out.append({"fell": sum(x["fell"] for x in g) / len(g), "t_fall": sum(x["t_fall"] for x in g) / len(g),
                    "disp": [sum(x["disp"][j] for x in g) / len(g) for j in range(len(SAMPLE))],
                    "height": [sum(x["height"][j] for x in g) / len(g) for j in range(len(SAMPLE))]})
    return out


def loss(ours, ref):
    """How differently ours behaves from the reference, over every hit (0 = the same)."""
    total = 0.0
    for o, r in zip(ours, ref):
        total += 2.0 * abs(o["fell"] - r["fell"])
        total += abs(o["t_fall"] - r["t_fall"]) / SECS
        total += sum(abs(a - b) for a, b in zip(o["disp"], r["disp"])) / len(SAMPLE)
        total += 2.0 * sum(abs(a - b) for a, b in zip(o["height"], r["height"])) / len(SAMPLE)
    return total / len(ref)


def make_body(scales, out):
    env = dict(os.environ, **{k: str(v) for (k, *_), v in zip(BODY, scales)})
    subprocess.run([sys.executable, str(HERE / "make_character.py"), "", "", str(out)], env=env, check=True,
                   stdout=subprocess.DEVNULL)


def summary(m):
    fell = sum(x["fell"] for x in m) / len(m)
    return (f"falls {fell:.0%}, mean time to fall {sum(x['t_fall'] for x in m) / len(m):.2f}s (6 = stayed up), "
            f"stagger at 1s {sum(x['disp'][1] for x in m) / len(m):.2f}m / 4s {sum(x['disp'][3] for x in m) / len(m):.2f}m")


def per_push(m):
    rows = []
    for p in PUSHES:
        sel = [x for x, s in zip(m, SCENARIOS) if s[2] == p]
        rows.append(f"{p:.0f} N*s: falls {sum(x['fell'] for x in sel) / len(sel):.0%}, "
                    f"stagger 1s {sum(x['disp'][1] for x in sel) / len(sel):.2f}m")
    return "; ".join(rows)


def clip(v, lo, hi): return min(hi, max(lo, v))


def main():
    (HERE / "empty_params.txt").write_text("; no values: the engine's defaults\n")
    mode = sys.argv[1] if len(sys.argv) > 1 else "ours"
    n = int(sys.argv[sys.argv.index("-n") + 1]) if "-n" in sys.argv else 300
    ref_file = HERE / "ref.json"
    with mp.Pool(max(2, os.cpu_count() - 2)) as pool:
        if mode == "ref":
            ref = measure(pool, "ref", sys.argv[2])
            ref_file.write_text(json.dumps(ref))
            print("reference:", summary(ref)); print("  ", per_push(ref)); return
        ref = json.loads(ref_file.read_text()) if ref_file.exists() else None
        if mode == "ours":
            m = measure(pool, "ours", HERE.parent / "body.json")
            print("ours:", summary(m)); print("  ", per_push(m))
            if ref: print(f"difference from the reference: {loss(m, ref):.3f}")
            return
        # tune: a simple evolution search over our task values and muscle scales, in normalised space
        if not ref: ref = measure(pool, "ref", sys.argv[2]); ref_file.write_text(json.dumps(ref))
        lo = [t[3] for t in TASK] + [b[2] for b in BODY]
        hi = [t[4] for t in TASK] + [b[3] for b in BODY]
        x = [t[2] for t in TASK] + [b[1] for b in BODY]
        tmp = Path(tempfile.mkdtemp())

        def score(v):
            body = tmp / f"b_{abs(hash(tuple(v[len(TASK):]))) % 10**9}.json"
            if not body.exists(): make_body(v[len(TASK):], body)
            m = measure(pool, "ours", body, [round(a) if t[1] in INTS else a for a, t in zip(v[:len(TASK)], TASK)])
            return loss(m, ref), m

        best, best_m = score(x)
        print(f"start: difference {best:.3f}; {summary(best_m)}")
        print(f"reference: {summary(ref)}")
        step = 0.25
        for it in range(n):
            k = random.sample(range(len(x)), random.randint(1, 4))
            y = list(x)
            for i in k:
                y[i] = clip(y[i] + random.gauss(0, step) * (hi[i] - lo[i]), lo[i], hi[i])
            s, m = score(y)
            if s < best:
                x, best, best_m = y, s, m
                print(f"  {it}: difference {best:.3f}; {summary(m)}", flush=True)
                write_out(x, best, best_m, ref)  # saved as it goes: the search can be stopped any time
            if it % 40 == 39: step = max(0.04, step * 0.8)
            if it % 25 == 24:  # the runs are random: re-score the best so one lucky score can't hold it
                s2, _ = score(x)
                best = (best + s2) / 2
                print(f"  re-scored the best: {s2:.3f} -> {best:.3f}", flush=True)
        write_out(x, best, best_m, ref)


def write_out(x, best, m, ref):
    lines = ["; Euphoria Reaction balance preset: Natural (search output)",
             "; Our own values, found by tune_balance.py: searched until our body staggers and falls the way the",
             "; reference does under the same hits. Nothing in here is taken from the reference's files.",
             f"; difference from the reference {best:.3f}; ours: {summary(m)}", ""]
    for task in dict.fromkeys(t[0] for t in TASK):
        lines.append(f"{task} * {{")
        for v, (t, name, dflt, *_) in zip(x, TASK):
            if t == task:
                val = f"{round(v)}" if name in INTS else f"{v:.3f}"
                lines.append(f"  {name:<32}{val:<8}; engine {dflt}")
        lines += ["}", ""]
    (HERE / "tuned_balance.txt").write_text("\n".join(lines))
    make_body(x[len(TASK):], HERE / "tuned_body.json")
    (HERE / "tuned_scales.json").write_text(json.dumps({b[0]: v for b, v in zip(BODY, x[len(TASK):])}))
    print("    saved; muscle scales", {b[0]: round(v, 3) for b, v in zip(BODY, x[len(TASK):])}, flush=True)


if __name__ == "__main__":
    main()
