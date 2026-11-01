"""Offline check of a character in the real engine (delirium_physics.dll): does it stand, take a shot, fall, settle.

    python harness.py body.json [-q]      # -q: summary line only; env H_IMP (push size), H_NOSHOT (push, no shot task)
"""
import ctypes as C
import json
import sys
from pathlib import Path

DLL = Path(__file__).resolve().parents[2] / "delirium-phys" / "target" / "release" / "delirium_physics.dll"
f3 = C.c_float * 3
f4 = C.c_float * 4


def load():
    d = C.CDLL(str(DLL))
    P, I, Fp, S = C.c_void_p, C.c_int, C.POINTER(C.c_float), C.c_char_p
    sig = {
        "nm_last_error": (I, [C.c_char_p, I]), "nm_init_character_file": (I, [S]), "nm_init": (I, [S]),
        "nm_build_from_character": (P, [S]), "nm_build_load": (P, [S]), "nm_load_task_params": (I, [S]),
        "nm_world_create": (P, []), "nm_world_add_floor": (I, [P, C.c_float, C.c_float, C.c_float, C.c_float]),
        "nm_agent_create": (P, [P, Fp, C.c_float, C.c_float]), "nm_agent_step": (I, [P, P, C.c_float]),
        "nm_agent_task_balance": (I, [P, P, S, Fp]), "nm_agent_get_com": (I, [P, Fp, Fp]),
        "nm_agent_has_fallen": (I, [P]), "nm_agent_is_balancing": (I, [P]), "nm_agent_is_settled": (I, [P]),
        "nm_agent_get_part_transform": (I, [P, I, Fp, Fp]), "nm_agent_apply_impulse": (I, [P, I, Fp, Fp]),
        "nm_agent_task_shot": (I, [P, P, S, I, Fp, Fp, C.c_float]), "nm_agent_part_count": (I, [P]),
        "nm_agent_get_mass": (C.c_float, [P]), "nm_build_part_count": (I, [P]),
    }
    for n, (res, args) in sig.items():
        fn = getattr(d, n)
        fn.restype, fn.argtypes = res, args
    return d


def err(d):
    b = C.create_string_buffer(512)
    d.nm_last_error(b, 512)
    return b.value.decode(errors="replace")


def run(d, build, label):
    w = d.nm_world_create()
    d.nm_world_add_floor(w, 0.0, 0.0, 0.0, 30.0)
    a = d.nm_agent_create(build, f3(0, 0, 0), 0.0, 0.3)
    if not a:
        print(label, "agent_create failed:", err(d)); return
    print(f"{label}: {d.nm_agent_part_count(a)} parts, {d.nm_agent_get_mass(a):.1f} kg")
    com, vel, pos, q = f3(), f3(), f3(), f4()

    sway, still = 0.0, None

    def report(t):
        nonlocal sway, still
        d.nm_agent_get_com(a, com, vel)
        if 1.0 <= t <= 3.01:
            sway = max(sway, (com[0] ** 2 + com[1] ** 2) ** 0.5)
            if still is None and d.nm_agent_is_settled(a):
                still = t
        if QUIET:
            return
        d.nm_agent_get_part_transform(a, 12, pos, q)
        head = list(pos)
        d.nm_agent_get_part_transform(a, 0, pos, q)
        print(f"  t={t:4.1f}s com=({com[0]:+.2f},{com[1]:+.2f},{com[2]:+.2f}) |v|={sum(v*v for v in vel)**.5:4.2f}"
              f" pelvis_h={pos[2] if abs(pos[2]) > abs(pos[1]) else pos[1]:+.2f} head=({head[0]:+.2f},{head[1]:+.2f},{head[2]:+.2f})"
              f" bal={d.nm_agent_is_balancing(a)} fallen={d.nm_agent_has_fallen(a)} settled={d.nm_agent_is_settled(a)}")

    d.nm_agent_task_balance(a, w, b"*", None)
    dt, t = 1 / 60, 0.0
    report(t)
    for phase, secs in (("stand", 3.0), ("shot", 5.0)):
        if phase == "shot":
            d.nm_agent_get_part_transform(a, 10, pos, q)
            at = f3(*pos)
            print("  shot in the chest from the front at", [round(x, 2) for x in pos])
            if "H_NOSHOT" not in __import__("os").environ: d.nm_agent_task_shot(a, w, b"*", 10, at, f3(0, 1, 0), 0.6)
            d.nm_agent_apply_impulse(a, 10, at, f3(0, float(__import__("os").environ.get("H_IMP", "60")), 0))
        for i in range(int(secs / dt)):
            d.nm_agent_step(a, w, dt)
            t += dt
            if (i + 1) % 30 == 0:
                report(t)
    d.nm_agent_get_com(a, com, vel)
    print(f"SUMMARY sway={sway:.3f}m settled_at={still} end_com_h={com[2]:.2f} fallen={d.nm_agent_has_fallen(a)}")


QUIET = "-q" in sys.argv


def main():
    d = load()
    if True:
        p = Path(sys.argv[1])
        rc = d.nm_init_character_file(str(p).encode())
        print("init_character", rc, err(d) if rc < 0 else "")
        b = d.nm_build_from_character(p.read_text().encode())
        label = p.name
    if not b:
        print("build failed:", err(d)); return
    print("build parts", d.nm_build_part_count(b))
    run(d, b, label)


if __name__ == "__main__":
    main()




