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


def card_measures(events: dict[str, int], ball: np.ndarray, root: np.ndarray, pel: np.ndarray, gr: np.ndarray, gt: np.ndarray,
                  t: rt.Target, side: str, foot: str, radius: float, entry_speed: float, fps: int) -> dict[str, float]:
    """What a move file's [card] bands are checked against (author.py), measured on the solved frames. "His frame" is the root with
    its axes along and across the run at the start (it does not turn with his facing); "toward the side" is toward the move's
    side, or the playing foot's side for a move without one.

    * out_m: how far the ball goes away from the side from where it was against him at the contact (his frame), up to the last
      contact; out_s: the seconds from the contact until it is within 2 cm of that.
    * contact2_s: the second contact's time after the first.
    * pull_m: how far the ball is pulled back along the run on the pitch from the contact to the release.
    * end_behind_m, end_side_m: at the release, the ball behind his hip line and toward the side of him (his frame).
    * hips_m: the root's largest shift away from the side between the contact and the last contact.
    * exit_speed: the root's speed toward the side at the release (m/s).
    * hop_m: how far back the root comes from its furthest point along the run by the release.
    * stop_share: the share of the move (start to release) by which his pace along the run has first come to nothing.
    * keep: his lowest pace from the start to the release as a share of the entry speed.
    * open_deg: the root's yaw at the release, toward the side.
    * lift_m: the ball's highest lift above rest.
    * drop_m: the pelvis' height range (the sell beat); snap_deg: the shoulder line's peak turn against the hip line (the snap
      beat); drive_m: the path extent of the hand on the non-playing side relative to the chest, in the chest's axes (the drive beat).
    """
    k = rt.BONES.index
    sgn = 1.0 if (side == "left" or (side == "none" and foot == "left")) else -1.0
    c1, rel = events["contact"], events["release"]
    last = events.get("contact2", rel)
    bx = ball[:, 0] - root[:, 0]; bz = ball[:, 2] - root[:, 1]
    out = -sgn * (bx[c1:last + 1] - bx[c1])
    reached = np.nonzero(out >= out.max() - 0.02)[0]
    vel = np.diff(root[:, :2], axis=0) * fps
    pace = np.linalg.norm(vel[: max(rel, 1)], axis=1)
    fwd = vel[: max(rel, 1), 1]
    stopped = np.nonzero(fwd <= 0.05)[0]
    sh = gt[:, k("LeftArm")] - gt[:, k("RightArm")]; hp = gt[:, k("LeftUpLeg")] - gt[:, k("RightUpLeg")]
    turn = np.degrees(np.arctan2(sh[:, 2], sh[:, 0]) - np.arctan2(hp[:, 2], hp[:, 0]))
    turn = (turn + 180) % 360 - 180
    s2 = k("Spine2")
    hand = k("LeftHand" if foot == "right" else "RightHand")
    chest = gr[:, s2] @ t.rest_glob_r[s2].T
    hrel = np.einsum("nji,nj->ni", chest, gt[:, hand] - gt[:, s2])
    return {
        "out_m": float(out.max()),
        "out_s": float(reached[0] / fps) if len(reached) else 0.0,
        "contact2_s": float((events["contact2"] - c1) / fps) if "contact2" in events else 0.0,
        "pull_m": float((ball[c1, 2] - ball[c1:rel + 1, 2]).max()),
        "end_behind_m": float(-bz[rel]),
        "end_side_m": float(sgn * bx[rel]),
        "hips_m": float((-sgn * (root[c1:last + 1, 0] - root[0, 0])).max()),
        "exit_speed": float(sgn * vel[min(rel, len(vel)) - 1, 0]),
        "hop_m": float(root[: rel + 1, 1].max() - root[rel, 1]),
        "stop_share": float(stopped[0] / max(rel, 1)) if len(stopped) else 1.0,
        "keep": float(pace.min() / entry_speed) if entry_speed > 1e-6 else 1.0,
        "open_deg": float(sgn * root[rel, 2]),
        "lift_m": float(ball[:, 1].max() - radius),
        "drop_m": float(pel[:, 1].max() - pel[:, 1].min()),
        "snap_deg": float(np.abs(turn).max()),
        "drive_m": float(np.linalg.norm(hrel.max(axis=0) - hrel.min(axis=0))),
    }


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
