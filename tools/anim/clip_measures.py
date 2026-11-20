"""Body-language measures of the authored skill clips, read from the .kclip files as the game plays them.

    uv run --with numpy python3 tools/anim/clip_measures.py [--dir <skills dir>] [--before <skills dir>] [name regex]

Per clip (model space: he faces +Z, his left is +X; the root is the line from the first frame's hips to the last's, which the game
puts on the simulation's position, so what is measured against it is what the clip adds to the simulated motion):

* pel_x: the pelvis' sideways travel (range of its x, m); pel_off: its largest distance off the root line (m).
* lean, back, bend: the chest's (Spine2) peak forward lean, backward lean and side bend (either way) from the vertical, in the
  hips' heading (deg).
* sh_turn: the shoulder line's peak turn against the hip line (deg, about the vertical).
* wrist: the larger of the two wrists' path extents (bounding-box diagonal, root frame, m).
* arm_out: the peak sideways reach of either wrist from the chest (Spine2), across the hips' heading (m).
* foot: the playing foot's ankle path extent (root frame, m).
* foot_h: the playing foot's ankle at its highest above where it stands at rest (m).
* drop: how far the pelvis comes down from its highest point (m).
* deg_s: the fastest bone turn between two frames (deg/s).

With --before, each value is printed as before -> after (and the ratio).
"""
from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import retarget as rt  # noqa: E402

OUT = HERE.parent.parent / "game/assets/anim/attacker/skills"
KEYS = ("pel_x", "pel_off", "lean", "back", "bend", "sh_turn", "wrist", "arm_out", "foot", "foot_h", "drop", "deg_s")


def measure(path: Path, foot: str, t: rt.Target) -> dict[str, float]:
    c = rt.read_kclip(path)
    q, hips, fps = c["q"], c["hips"], c["fps"]
    n = len(hips)
    L = np.stack([rt.quats_to_mats(q[:, b]) for b in range(q.shape[1])], axis=1)
    gr, gt = t.fk(L, hips)
    k = rt.BONES.index
    u = np.linspace(0.0, 1.0, n)[:, None]
    root = hips[0, [0, 2]] * (1 - u) + hips[-1, [0, 2]] * u
    rel = gt.copy(); rel[:, :, 0] -= root[:, 0:1]; rel[:, :, 2] -= root[:, 1:2]
    pel = (gt[:, k("LeftUpLeg")] + gt[:, k("RightUpLeg")]) * 0.5
    off = np.linalg.norm(pel[:, [0, 2]] - root, axis=1)
    # the hips' heading per frame, and the chest's up axis against it
    fwd = np.einsum("nij,j->ni", gr[:, 0] @ t.rest_glob_r[0].T, [0, 0, 1.0]); fwd[:, 1] = 0
    fwd /= np.linalg.norm(fwd, axis=1, keepdims=True)
    left = np.stack([fwd[:, 2], np.zeros(n), -fwd[:, 0]], axis=1)
    s2 = k("Spine2")
    up = np.einsum("nij,j->ni", gr[:, s2] @ t.rest_glob_r[s2].T, [0, 1.0, 0])
    lean = np.degrees(np.arctan2(np.sum(up * fwd, axis=1), up[:, 1]))
    bend = np.degrees(np.arctan2(np.sum(up * left, axis=1), up[:, 1]))
    sh = gt[:, k("LeftArm")] - gt[:, k("RightArm")]; hp = gt[:, k("LeftUpLeg")] - gt[:, k("RightUpLeg")]
    turn = np.degrees(np.arctan2(sh[:, 2], sh[:, 0]) - np.arctan2(hp[:, 2], hp[:, 0]))
    turn = (turn + 180) % 360 - 180

    def extent(p: np.ndarray) -> float:
        return float(np.linalg.norm(p.max(axis=0) - p.min(axis=0)))

    wrist = max(extent(rel[:, k("LeftHand")]), extent(rel[:, k("RightHand")]))
    arm_out = max(float(np.abs(np.sum((gt[:, k(h)] - gt[:, s2]) * left, axis=1)).max()) for h in ("LeftHand", "RightHand"))
    pf = rel[:, k(("Left" if foot == "left" else "Right") + "Foot")]
    d = np.abs(np.sum(q[1:] * q[:-1], axis=2))
    deg_s = float(np.degrees(2 * np.arccos(np.clip(d, 0, 1))).max() * fps)
    return {"pel_x": float(np.ptp(pel[:, 0])), "pel_off": float(off.max()), "lean": float(max(0.0, lean.max())), "back": float(max(0.0, -lean.min())),
            "bend": float(np.abs(bend).max()), "sh_turn": float(np.abs(turn).max()), "wrist": wrist, "arm_out": arm_out, "foot": extent(pf),
            "foot_h": float(pf[:, 1].max() - t.rest_glob_t[k("LeftFoot"), 1] + t.sole_y), "drop": float(pel[:, 1].max() - pel[:, 1].min()), "deg_s": deg_s}


def fmt(k: str, v: float) -> str:
    return f"{v:.0f}" if k in ("lean", "back", "bend", "sh_turn", "deg_s") else f"{v:.3f}"


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--dir", type=Path, default=OUT)
    ap.add_argument("--before", type=Path)
    ap.add_argument("pattern", nargs="?", default=".")
    a = ap.parse_args(argv)
    t = rt.load_target()
    clips = json.loads((a.dir / "clips.json").read_text())["clips"]
    print("| clip | " + " | ".join(KEYS) + " |"); print("|---" * (len(KEYS) + 1) + "|")
    for c in clips:
        if not re.search(a.pattern, c["name"]):
            continue
        now = measure(a.dir / c["file"], c["foot"], t)
        was = measure(a.before / c["file"], c["foot"], t) if a.before and (a.before / c["file"]).exists() else None
        cells = []
        for k in KEYS:
            if was is None or fmt(k, was[k]) == fmt(k, now[k]):
                cells.append(fmt(k, now[k]))
            else:
                r = now[k] / was[k] if was[k] > 1e-9 else math.inf
                cells.append(f"{fmt(k, was[k])} -> {fmt(k, now[k])} ({r:.1f}x)")
        print(f"| {c['name']} | " + " | ".join(cells) + " |")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
