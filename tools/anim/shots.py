"""Cuts the attacker's shot clips from the two retargeted CMU kicks ("walk up and kick the ball") and writes
game/assets/anim/attacker/shots/*.kclip + clips.json (the shot layer the game draws a struck shot with, see game/ShotClipLayer.cs).

    uv run --with numpy python3 tools/anim/shots.py          # after retarget.py has written game/assets/anim/attacker

Both kicks strike with the right foot. Each is cut once as recorded (the right foot strikes) and once mirrored (the left foot
strikes), so the game never mirrors them:

* shot_run_*   from kick_soccer_11_01 (subject 11): three jogging steps (about 2.7 m/s) into a long backswing, the heel above the knee,
               and a follow-through that lifts the boot over 0.6 m with a hop on the plant foot. The shot at a run.
* shot_stand_* from kick_soccer_10_02 (subject 10): from standing, one long step of the plant foot while the striking foot stays down,
               then a shorter, lower swing. The shot from standing (and the shorter swing).

Events (frames of the cut clip), found from the motion and not typed in:

* start      the plant foot leaves the ground for its last step (the clip's first frame).
* takeoff    the striking foot leaves the ground before the backswing (its last stance before the contact ends). From here to the
             plant neither foot is down in the run clip: the game enters a running clip no earlier than this, so no stance foot of the
             clip is held while the simulation's body runs on at a sprint.
* backswing  the top of the backswing: the striking ankle's highest point between the takeoff and the contact.
* plant      the plant foot lands (the first frame of its stance that lasts through the contact).
* contact    the ball is met: the frame of the striking ankle's peak speed among the frames where its boot is within ball height
             (BALL_H) of the ground.
* end        the follow-through ends: the next landing of either foot after the contact (the plant foot back down after its hop, or
             the striking foot).

The clip runs from `start` to `end` + SETTLE frames (the hand-back to the stride cycles plays over them).

Root: the clip is turned about the vertical so the approach (the hips' travel from `start` to `plant`) runs along +Z, and moved so
the hips are over the origin at the contact. `root` in clips.json is the hips' ground track smoothed over ROOT_SMOOTH frames: the
game puts the pose relative to it on the simulation's position. `kick_dir` is the striking ankle's horizontal direction of travel at
the contact, `approach_speed` the hips' mean ground speed over the last stride before the plant.

The mirror is Mirror.Apply's (src/FootballSim.Anim/Mirror.cs): each bone takes its opposite bone's turn from rest, reflected across
x = 0; the hips' x is negated; contacts swap.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np

import retarget as rt

HERE = Path(__file__).resolve().parent
SRC = rt.REPO / "game/assets/anim/attacker"
OUT = SRC / "shots"
MODEL = rt.REPO / "game/assets/characters/player.glb"
BALL_H = 0.25      # metres: the striking boot's lowest point within this of the ground counts as at ball height (a ball is 0.22 m)
SETTLE = 12        # frames kept after `end` for the hand-back
ROOT_SMOOTH = 13   # frames: the root is the hips' ground track averaged over this window

SOURCES = [
    {"name": "shot_run", "clip": "kick_soccer_11_01", "entry": "run",
     "notes": "jog-up of three steps (subject 11): long backswing, high follow-through with a hop on the plant foot"},
    {"name": "shot_stand", "clip": "kick_soccer_10_02", "entry": "stand",
     "notes": "from standing (subject 10): one long plant step, the striking foot down until the backswing, a shorter and lower swing"},
]


def spans(m: np.ndarray) -> list[tuple[int, int]]:
    """Runs of True as (first, last) frames."""
    out, a = [], None
    for i, v in enumerate(m):
        if v and a is None:
            a = i
        if not v and a is not None:
            out.append((a, i - 1)); a = None
    if a is not None:
        out.append((a, len(m) - 1))
    return out


def fk(t: rt.Target, q: np.ndarray, hips: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    n = len(hips)
    return t.fk(rt.quats_to_mats(q.reshape(-1, 4)).reshape(n, 22, 3, 3), hips)


def find_events(t: rt.Target, k: dict) -> dict:
    """The kick's events (source frames), its striking foot (0 left, 1 right) and the facts they were found from."""
    q, hips, con, fps = k["q"], k["hips"], k["contacts"], k["fps"]
    n = len(hips)
    gr, gt = fk(t, q, hips)
    ank = [gt[:, rt.BONES.index(s + "Foot")] for s in ("Left", "Right")]
    low = [rt.foot_points(t, gr, gt, s)[:, :, 1].min(axis=1) - t.sole_y for s in (0, 1)]
    spd = [np.zeros(n), np.zeros(n)]
    for s in (0, 1):
        spd[s][1:-1] = np.linalg.norm(ank[s][2:] - ank[s][:-2], axis=1) * fps / 2
    kf = int(np.argmax([spd[0].max(), spd[1].max()]))
    pf = 1 - kf
    near = low[kf] <= BALL_H
    contact = int(np.argmax(np.where(near, spd[kf], -1.0)))
    # the plant foot's stance that lasts through the contact
    plant = next((a for a, b in spans(con[:, pf]) if a <= contact <= b), None)
    if plant is None:
        raise ValueError(f"no stance of the plant foot through the contact (frame {contact})")
    # its previous stance's end: the plant step's start
    prev = [b for a, b in spans(con[:, pf]) if b < plant]
    if not prev:
        raise ValueError("the plant foot has no stance before its plant step")
    start = prev[-1] + 1
    kst = [b for a, b in spans(con[:, kf]) if b < contact]
    if not kst:
        raise ValueError("the striking foot has no stance before the contact")
    takeoff = kst[-1] + 1
    if not (start <= takeoff < plant <= contact):
        raise ValueError(f"events out of order: start {start} takeoff {takeoff} plant {plant} contact {contact}")
    backswing = takeoff + int(np.argmax(ank[kf][takeoff:contact, 1]))
    later = [a for s in (0, 1) for a, b in spans(con[:, s]) if a > contact + 3 and not (s == pf and a == plant)]
    if not later:
        raise ValueError("no landing after the contact")
    end = min(later)
    if end + SETTLE >= n:
        raise ValueError(f"the clip ends {n - 1 - end} frames after the follow-through, {SETTLE} are kept")
    return {"foot": kf, "events": {"start": start, "takeoff": takeoff, "backswing": backswing, "plant": plant, "contact": contact,
                                   "end": end},
            "contact_speed": float(spd[kf][contact]), "backswing_ankle_m": float(ank[kf][backswing, 1] - t.sole_y),
            "follow_peak_m": float(ank[kf][contact:end + 1, 1].max() - t.sole_y)}


def yaw_quat(rad: float) -> np.ndarray:
    return np.array([0.0, math.sin(rad / 2), 0.0, math.cos(rad / 2)])


def cut(t: rt.Target, src: dict, k: dict) -> dict:
    """The clip cut, turned and moved (see the module docstring), striking foot as recorded."""
    found = find_events(t, k)
    ev = found["events"]
    a, b = ev["start"], ev["end"] + SETTLE
    q = k["q"][a:b + 1].copy()
    hips = k["hips"][a:b + 1].copy()
    con = k["contacts"][a:b + 1].copy()
    rel = {e: f - a for e, f in ev.items()}
    d = hips[rel["plant"]] - hips[0]
    yaw = -math.atan2(d[0], d[2])
    ry = rt.yaw_mat(yaw)
    hips = hips @ ry.T
    q[:, 0] = rt.qmul(np.broadcast_to(yaw_quat(yaw), q[:, 0].shape), q[:, 0])
    c = hips[rel["contact"]].copy()
    hips[:, 0] -= c[0]; hips[:, 2] -= c[2]
    fps = k["fps"]
    _, gt = fk(t, q, hips)
    kank = gt[:, rt.BONES.index(("Left", "Right")[found["foot"]] + "Foot")]
    i = rel["contact"]
    kd = kank[i + 1] - kank[i - 1]
    kd = np.array([kd[0], 0.0, kd[2]]); kd /= np.linalg.norm(kd)
    # the approach speed: the hips' ground speed over the stride before the plant (from the striking foot's takeoff, or the start)
    f0 = max(0, rel["plant"] - int(round(0.4 * fps)))
    v = np.linalg.norm((hips[rel["plant"]] - hips[f0])[[0, 2]]) / ((rel["plant"] - f0) / fps)
    return {"name": src["name"], "clip": src["clip"], "entry": src["entry"], "notes": src["notes"], "frames": [a, b], "fps": fps,
            "foot": found["foot"], "q": q, "hips": hips, "contacts": con, "events": rel, "kick_dir": kd, "approach_speed": float(v),
            "contact_speed": found["contact_speed"], "backswing_ankle_m": found["backswing_ankle_m"], "follow_peak_m": found["follow_peak_m"]}


def mirror_index(names: list[str]) -> list[int]:
    out = []
    for nm in names:
        s = nm.replace("Left", "@").replace("Right", "Left").replace("@", "Right")
        out.append(names.index(s))
    return out


def mirror(t: rt.Target, c: dict) -> dict:
    """The clip with the other foot striking (Mirror.Apply)."""
    q = c["q"]; n = len(q)
    rest_g = rt.mats_to_quats(t.rest_glob_r)          # (22,4)
    rest_inv = rt.qconj(rest_g)
    mo = mirror_index(rt.BONES)
    g = np.empty_like(q)
    for kk in range(22):
        p = t.parent[kk]
        g[:, kk] = q[:, kk] if p < 0 else rt.qmul(g[:, p], q[:, kk])
    gm = np.empty_like(q)
    refl = np.array([1.0, -1.0, -1.0, 1.0])
    for kk in range(22):
        o = mo[kk]
        d = rt.qmul(g[:, o], np.broadcast_to(rest_inv[o], (n, 4))) * refl
        gm[:, kk] = rt.qmul(d, np.broadcast_to(rest_g[kk], (n, 4)))
    out = np.empty_like(q)
    for kk in range(22):
        p = t.parent[kk]
        l = gm[:, kk] if p < 0 else rt.qmul(rt.qconj(gm[:, p]), gm[:, kk])
        l /= np.linalg.norm(l, axis=1, keepdims=True)
        l[l[:, 3] < 0] *= -1
        out[:, kk] = l
    m = dict(c)
    m["q"] = out
    m["hips"] = c["hips"] * np.array([-1.0, 1.0, 1.0])
    m["contacts"] = c["contacts"][:, ::-1].copy()
    m["foot"] = 1 - c["foot"]
    m["kick_dir"] = c["kick_dir"] * np.array([-1.0, 1.0, 1.0])
    return m


def root_track(hips: np.ndarray) -> np.ndarray:
    """(n,2): the hips' ground track (x, z) averaged over ROOT_SMOOTH frames (the window shrinks at the ends)."""
    n = len(hips); h = ROOT_SMOOTH // 2
    out = np.empty((n, 2))
    for i in range(n):
        a, b = max(0, i - h), min(n, i + h + 1)
        out[i] = hips[a:b][:, [0, 2]].mean(axis=0)
    return out


def side_name(foot: int) -> str:
    return ("left", "right")[foot]


def make(t: rt.Target, src: Path = SRC) -> list[dict]:
    out = []
    for s in SOURCES:
        k = rt.read_kclip(src / f"{s['clip']}.kclip")
        c = cut(t, s, k)
        for v in (c, mirror(t, c)):
            v = dict(v)
            v["name"] = f"{s['name']}_{side_name(v['foot'])}"
            v["root"] = root_track(v["hips"])
            out.append(v)
    return out


def write(c: dict, out: Path, names: list[str]) -> None:
    r = rt.Result(c["name"], c["hips"], c["q"], c["contacts"], c["events"], False, [], {})
    rt.write_kclip(out / f"{c['name']}.kclip", r, names)


def entry(c: dict) -> dict:
    n = len(c["hips"]); fps = c["fps"]
    sp = {side_name(s): [list(p) for p in spans(c["contacts"][:, s])] for s in (0, 1)}
    return {"name": c["name"], "file": f"{c['name']}.kclip", "source_clip": c["clip"], "source_frames": c["frames"],
            "mirrored": c["foot"] != 1, "set": "shot", "side": side_name(c["foot"]), "foot": side_name(c["foot"]), "entry": c["entry"],
            "entry_speed": round(c["approach_speed"], 3), "verdict": "keep", "loop": False, "events": c["events"], "fps": fps,
            "frame_count": n, "seconds": round(n / fps, 4),
            "event_seconds": {e: round(f / fps, 4) for e, f in c["events"].items()},
            "kick_dir": [round(float(x), 4) for x in c["kick_dir"]],
            "contact_speed_mps": round(c["contact_speed"], 2), "backswing_ankle_m": round(c["backswing_ankle_m"], 3),
            "follow_peak_ankle_m": round(c["follow_peak_m"], 3), "contacts": sp,
            "root": [[round(float(x), 4), round(float(z), 4)] for x, z in c["root"]], "notes": c["notes"]}


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="Cut the attacker's shot clips from the retargeted kicks.")
    ap.add_argument("--src", type=Path, default=SRC)
    ap.add_argument("--out", type=Path, default=OUT)
    ap.add_argument("--model", type=Path, default=MODEL)
    args = ap.parse_args(argv)
    t = rt.load_target(args.model)
    names = [nm.replace(":", "_") for nm in t.names]
    args.out.mkdir(parents=True, exist_ok=True)
    clips = make(t, args.src)
    index = []
    for c in clips:
        write(c, args.out, names)
        e = entry(c)
        index.append(e)
        print(f"wrote {c['name']} from {c['clip']} {c['frames']}: {e['frame_count']} frames, events {c['events']}, "
              f"approach {e['entry_speed']:.2f} m/s, contact {e['contact_speed_mps']:.1f} m/s, backswing ankle {e['backswing_ankle_m']:.2f} m, "
              f"follow-through ankle {e['follow_peak_ankle_m']:.2f} m, kick_dir {e['kick_dir']}")
    skeleton = {"parents": t.parent, "offsets": [[round(float(v), 6) for v in o] for o in t.rest_local_t],
                "rest": [[round(float(v), 6) for v in qq] for qq in rt.mats_to_quats(t.rest_local_r)]}
    (args.out / "clips.json").write_text(json.dumps({"fps": rt.FPS, "bones": names, "skeleton": skeleton, "clips": index}, indent=1) + "\n")
    keep = {e["file"] for e in index}
    for old in args.out.glob("*.kclip"):
        if old.name not in keep:
            old.unlink()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
