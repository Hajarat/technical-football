"""Cuts one seamless stride loop from each retargeted attacker locomotion clip listed under [[cycle]] in tools/anim/attacker_clips.toml
and writes game/assets/anim/attacker/cycles/*.kclip + clips.json (the stride cycles the attacker's body plays, see
src/FootballSim.Anim/CycleMotion.cs).

    uv run --with numpy python3 tools/anim/cycles.py          # after retarget.py has written game/assets/anim/attacker

Method:

* A cycle is the clip's frames [start, end): one stride, from a foot strike to the same foot's next strike (the loop ranges of the
  2026-11-19 clip survey). Frame `end` is the first frame of the next stride, so it is where the loop must come back to.
* Root: the clip is turned about the vertical so the stride's travel (hips at `end` minus hips at `start`) points along +Z, and its
  travel is taken out: the hips are stored relative to a root moving at the stride's mean speed (their mean over the stride at the
  origin), so the cycle plays in place and the game moves it along the simulation's path. Its speed, steps per second and step
  length are written to clips.json.
* Closing the loop: every bone's rotation and the hips' height differ a little between `end` and `start` (stride-to-stride
  variation). That difference is spread over the cycle (frame i takes i/N of it), so the last frame runs into the first exactly; the
  mismatch before closing is reported (and a cycle whose mean mismatch exceeds MAX_CLOSE_DEG is refused).
* Events: per foot, the strike (first frame of its stance) and the toe-off (its last frame). A foot is in stance while its lowest
  point (ankle, toe joint or toe tip) is within STANCE_H of its lowest over the stride. Written as the cycle's events (strike_l,
  toeoff_l, strike_r, toeoff_r) and as its contact bits. The game's phase map puts them on the stride clock (CycleMotion).

The cycles are not mirrored here: the game mirrors them at load (FootballSim.Anim.Mirror) when it wants the other foot's version.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
import tomllib
from pathlib import Path

import numpy as np

import retarget as rt

HERE = Path(__file__).resolve().parent
CONFIG = HERE / "attacker_clips.toml"
SRC = rt.REPO / "game/assets/anim/attacker"
OUT = SRC / "cycles"
MODEL = rt.REPO / "game/assets/characters/player.glb"
STANCE_H = 0.025       # metres above the foot's lowest over the stride that still counts as on the ground
HEEL_BACK = 0.03      # the heel point sits this far behind the ankle joint, on the sole
MAX_CLOSE_DEG = 10.0   # mean bone mismatch between the loop's ends, degrees, above which a cycle is refused


def qlog(q: np.ndarray) -> np.ndarray:
    """(n,4) unit quaternions -> (n,3) scaled angle-axis (angle in [0, pi])."""
    q = np.where(q[:, 3:4] < 0, -q, q)
    v = q[:, :3]; s = np.linalg.norm(v, axis=1)
    ang = 2 * np.arctan2(s, q[:, 3])
    with np.errstate(invalid="ignore", divide="ignore"):
        k = np.where(s > 1e-9, ang / np.maximum(s, 1e-12), 2.0)
    return v * k[:, None]


def qexp(v: np.ndarray) -> np.ndarray:
    a = np.linalg.norm(v, axis=1)
    h = a * 0.5
    with np.errstate(invalid="ignore", divide="ignore"):
        k = np.where(a > 1e-9, np.sin(h) / np.maximum(a, 1e-12), 0.5)
    q = np.concatenate([v * k[:, None], np.cos(h)[:, None]], axis=1)
    return q / np.linalg.norm(q, axis=1, keepdims=True)


def angle_deg(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    d = np.clip(np.abs(np.sum(a * b, axis=-1)), 0, 1)
    return np.degrees(2 * np.arccos(d))


def yaw_quat(rad: float) -> np.ndarray:
    return np.array([0.0, math.sin(rad / 2), 0.0, math.cos(rad / 2)])


def stance_mask(t: rt.Target, q: np.ndarray, hips: np.ndarray) -> np.ndarray:
    """(n,2) bool: each foot (0 = left) on the ground, from its lowest point relative to its lowest over the frames."""
    n = len(hips)
    gr, gt = t.fk(rt.quats_to_mats(q.reshape(-1, 4)).reshape(n, 22, 3, 3), hips)
    out = np.zeros((n, 2), dtype=bool)
    for s in range(2):
        f = rt.BONES.index(("Left", "Right")[s] + "Foot")
        # the heel: under the ankle on the sole at rest, carried by the foot bone (a heel strike lands on it, the toes still up)
        heel_rest = np.array([t.rest_glob_t[f, 0], t.sole_y, t.rest_glob_t[f, 2] - HEEL_BACK])
        heel_local = t.rest_glob_r[f].T @ (heel_rest - t.rest_glob_t[f])
        heel = gt[:, f] + np.einsum("nij,j->ni", gr[:, f], heel_local)
        # each point (heel, ankle, toe joint, toe tip) is down while within STANCE_H of its own lowest; the foot while any one is
        h = np.concatenate([heel[:, None, 1], rt.foot_points(t, gr, gt, s)[:, :, 1]], axis=1)
        out[:, s] = one_stance((h <= h.min(axis=0) + STANCE_H).any(axis=1))
    return out


def runs(m: np.ndarray) -> list[tuple[int, int]]:
    """The runs of True going round the loop, as (first frame, length); a run may wrap past the end."""
    n = len(m)
    if m.all():
        return [(0, n)]
    out = []
    for i in range(n):
        if m[i] and not m[i - 1]:
            L = 0
            while m[(i + L) % n]:
                L += 1
            out.append((i, L))
    return out


def one_stance(m: np.ndarray, gap: int = 3) -> np.ndarray:
    """A loop's on-ground mask cleaned to one stance: gaps of up to `gap` frames between two ground spans closed, then only the
    longest span (going round the loop) kept, so a heel that dips back to the ground after toe-off is not a second touch-down."""
    n = len(m)
    if m.all() or not m.any():
        return m.copy()
    gaps = runs(~m)
    m = m.copy()
    for i, L in gaps:
        if L <= gap:
            for j in range(L):
                m[(i + j) % n] = True
    i, L = max(runs(m), key=lambda r: r[1])
    out = np.zeros(n, dtype=bool)
    for j in range(L):
        out[(i + j) % n] = True
    return out


def make_cycle(c: dict, t: rt.Target, src: Path = SRC) -> dict:
    """Cuts, turns, detrends and closes one cycle. Returns its data and stats (no files written)."""
    k = rt.read_kclip(src / f"{c['clip']}.kclip")
    a, b = c["frames"]
    if not (0 <= a < b < len(k["hips"])):
        raise ValueError(f"{c['name']}: frames {a}-{b} outside {c['clip']} ({len(k['hips'])} frames)")
    n = b - a
    q = k["q"][a:b + 1].copy()          # n + 1 frames: the last is the next stride's first
    hips = k["hips"][a:b + 1].copy()
    # turn so the stride travels along +Z, from over the origin
    d = hips[n] - hips[0]
    yaw = -math.atan2(d[0], d[2])
    ry = rt.yaw_mat(yaw); qy = yaw_quat(yaw)
    hips = (hips - hips[0] * np.array([1, 0, 1])) @ ry.T
    q[:, 0] = rt.qmul(np.broadcast_to(qy, q[:, 0].shape), q[:, 0])
    travel = hips[n, 2]
    speed = travel / (n / k["fps"])
    # the mismatch at the seam, before closing
    close_deg = angle_deg(q[n], q[0])
    close_hips_cm = abs(hips[n, 1] - hips[0, 1]) * 100
    # spread it: frame i takes i/n of the end-to-start difference, so frame n would equal frame 0 (the change of speed of a bone across
    # the seam stays: the stride's own variation, a small kink and no jump)
    nb = q.shape[1]
    out_q = np.empty((n, nb, 4))
    for j in range(nb):
        diff = qlog(rt.qmul(q[0:1, j], rt.qconj(q[n:n + 1, j])))[0]   # turns frame n onto frame 0
        for i in range(n):
            r = rt.qmul(qexp((diff * (i / n))[None])[0], q[i, j])
            out_q[i, j] = r / np.linalg.norm(r) * (1 if r[3] >= 0 else -1)
    root = np.stack([np.zeros(n + 1), np.zeros(n + 1), np.arange(n + 1) * travel / n], axis=1)
    rel = hips - root
    rel[:, 1] -= np.arange(n + 1) / n * (rel[n, 1] - rel[0, 1])
    rel = rel[:n]
    rel[:, [0, 2]] -= rel[:, [0, 2]].mean(axis=0)
    # stance of each foot on the closed cycle (the travel put back, so a planted foot stands still)
    world = rel + root[:n]
    st = stance_mask(t, out_q, world)
    events = {}
    for s, side in enumerate("lr"):
        m = st[:, s]
        if m.all() or not m.any():
            raise ValueError(f"{c['name']}: foot {side} has no stance and swing in the stride")
        # the stance is one run around the loop: its first frame follows a swing frame, its last precedes one
        starts = [i for i in range(n) if m[i] and not m[i - 1]]
        ends = [i for i in range(n) if m[i] and not m[(i + 1) % n]]
        if len(starts) != 1 or len(ends) != 1:
            raise ValueError(f"{c['name']}: foot {side} touches down {len(starts)} times in the stride")
        events[f"strike_{side}"] = starts[0]; events[f"toeoff_{side}"] = ends[0]
    fps = k["fps"]
    steps = 2 * fps / n
    return {"name": c["name"], "clip": c["clip"], "frames": [a, b], "fps": fps, "q": out_q, "hips": rel, "contacts": st, "events": events,
            "speed": float(speed), "steps_per_s": float(steps), "step_m": float(speed / steps), "blend_speed": float(c["blend_speed"]),
            "close_mean_deg": float(close_deg.mean()), "close_max_deg": float(close_deg.max()), "close_hips_cm": float(close_hips_cm),
            "notes": c.get("notes", "")}


def seam_deg(cy: dict) -> float:
    """Largest bone turn across the loop's seam (last frame to first) less the largest turn between two neighbouring frames inside it."""
    q = cy["q"]
    inner = max(float(angle_deg(q[i + 1], q[i]).max()) for i in range(len(q) - 1))
    return float(angle_deg(q[0], q[-1]).max()) - inner


def write(cy: dict, out: Path, names: list[str]) -> None:
    r = rt.Result(cy["name"], cy["hips"], cy["q"], cy["contacts"], cy["events"], True, [], {})
    rt.write_kclip(out / f"{cy['name']}.kclip", r, names)


def load(path: Path = CONFIG) -> list[dict]:
    with open(path, "rb") as f:
        return tomllib.load(f).get("cycle", [])


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="Cut the attacker's stride cycles from the retargeted clips.")
    ap.add_argument("--config", type=Path, default=CONFIG)
    ap.add_argument("--src", type=Path, default=SRC)
    ap.add_argument("--out", type=Path, default=OUT)
    ap.add_argument("--model", type=Path, default=MODEL)
    args = ap.parse_args(argv)
    t = rt.load_target(args.model)
    names = [nm.replace(":", "_") for nm in t.names]
    args.out.mkdir(parents=True, exist_ok=True)
    index, failed = [], []
    for c in load(args.config):
        try:
            cy = make_cycle(c, t, args.src)
        except ValueError as e:
            failed.append(c["name"]); print(f"REFUSED {e}"); continue
        if cy["close_mean_deg"] > MAX_CLOSE_DEG:
            failed.append(c["name"]); print(f"REFUSED {c['name']}: loop ends {cy['close_mean_deg']:.1f} deg apart (mean)"); continue
        write(cy, args.out, names)
        n = len(cy["hips"])
        st = {s: round(float(cy["contacts"][:, i].mean()), 3) for i, s in enumerate(("left", "right"))}
        index.append({"name": cy["name"], "file": f"{cy['name']}.kclip", "source_clip": cy["clip"], "source_frames": cy["frames"],
                      "set": "cycle", "side": "none", "verdict": "keep", "loop": True, "events": cy["events"], "fps": cy["fps"],
                      "frame_count": n, "seconds": round(n / cy["fps"], 4), "speed": round(cy["speed"], 4),
                      "steps_per_s": round(cy["steps_per_s"], 4), "step_m": round(cy["step_m"], 4), "blend_speed": cy["blend_speed"],
                      "stance_share": st, "close_mean_deg": round(cy["close_mean_deg"], 2), "close_max_deg": round(cy["close_max_deg"], 2),
                      "close_hips_cm": round(cy["close_hips_cm"], 2), "notes": cy["notes"]})
        print(f"wrote {cy['name']} from {cy['clip']} {cy['frames']}: {n} frames, {cy['speed']:.2f} m/s, {cy['steps_per_s']:.3f} steps/s, "
              f"step {cy['step_m']:.3f} m, ends {cy['close_mean_deg']:.2f}/{cy['close_max_deg']:.2f} deg {cy['close_hips_cm']:.2f} cm apart, "
              f"events {cy['events']}")
    skeleton = {"parents": t.parent, "offsets": [[round(float(v), 6) for v in o] for o in t.rest_local_t],
                "rest": [[round(float(v), 6) for v in qq] for qq in rt.mats_to_quats(t.rest_local_r)]}
    (args.out / "clips.json").write_text(json.dumps({"fps": rt.FPS, "bones": names, "skeleton": skeleton, "clips": index}, indent=1) + "\n")
    keep = {e["file"] for e in index}
    for old in args.out.glob("*.kclip"):
        if old.name not in keep:
            old.unlink()
    if failed:
        print(f"{len(failed)} cycle(s) refused: {', '.join(failed)}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
