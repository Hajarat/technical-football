"""Bakes the procedural keeper's dive flights, slide and smother into .kclip clips (spec "What CMU covers": until real dive recordings
exist, a dive takes off and lands on CMU motion and flies on today's dive pose, baked into a clip).

    game/tests/run-bg.sh is not needed (no window): the game records the keeper gallery's scripts headless:
    godot-mono --headless --path game -- --keeper-gallery out/bake-gallery --bake-keeper-pose out/bake --keeper-anim procedural
    uv run --with numpy python3 tools/anim/bake_procedural.py game/out/bake      # writes game/assets/anim/keeper/<clip>.kclip
    uv run --with numpy python3 tools/anim/retarget.py                           # rewrites clips.json (baked clips listed as they are)

Each clips.toml entry with source = "baked" names the gallery script it comes from (script = "dive_side_right"); the game wrote that
script's drawn bone rotations, 120 Hz, from the save's first tick to the end of the lying that follows, plus the save's contact time.
Here they are:

* turned so the facing the script gave him is +Z and moved to start over the origin, and kept at 60 Hz (every other tick);
* cleared of twist flips: the procedural arm solve can spin a nearly straight forearm or upper arm half a turn about its own axis from
  one tick to the next (its elbow pole flips). For every bone whose children all lie along its own axis (spine, spine1, head, shoulders,
  arms, forearms, hands, legs, feet, toes) the twist about that axis may change by at most TWIST_STEP_DEG per frame, and a jump of
  over FLIP_DEG in one frame (the solve flipping) is not followed at all; the bone's
  direction, so every joint position, is unchanged;
* cut, for a flight, to start FLIGHT_LEAD_IN frames before its take-off (the procedural dive's first ticks are the CMU take-off's
  part), and moved back over the origin;
* smoothed over time (a Gaussian of SMOOTH_SIGMA frames on every rotation and the hips): the procedural pose swings its limbs in a
  few ticks where its targets jump, faster than the gallery's 1,500 deg/s pop flag;
* labelled: events "takeoff" (for a flight the first frame its trunk leans TAKEOFF_TILT_DEG from upright, else the first frame both
  ankles are TAKEOFF_LIFT above where they started; no later than 0.1 s before the contact),
  "contact" (the script's contact time) and "landing" (the first frame after contact the hips are within 5 cm of their lowest); foot
  contacts as retarget.py labels them (the foot's lowest point under 4 cm, its speed under 0.35 m/s);
* checked: no joint below the ground (the clip is refused otherwise).
"""
from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import retarget as rt  # noqa: E402

SMOOTH_SIGMA = 2.5      # frames (42 ms): the procedural pose's quickest swings (its hand targets ease in over a few ticks) softened
TWIST_STEP_DEG = 12.0
FLIP_DEG = 60.0   # 720 deg/s: well under the gallery's 1,500 pop flag
TAKEOFF_LIFT = 0.05
TAKEOFF_TILT_DEG = 20.0
FLIGHT_LEAD_IN = 3     # frames a flight keeps before its take-off (the rest of the procedural dive's start is cut)
LANDING_BAND = 0.05
TWIST_BONES = ["Spine", "Spine1", "Head"] + [s + b for s in ("Left", "Right")
                                               for b in ("Shoulder", "Arm", "ForeArm", "Hand", "UpLeg", "Leg", "Foot", "ToeBase")]


def axis_angle(axis: np.ndarray, ang: float) -> np.ndarray:
    a = axis / max(np.linalg.norm(axis), 1e-12)
    s = math.sin(ang / 2)
    return np.array([a[0] * s, a[1] * s, a[2] * s, math.cos(ang / 2)])


def swing_quat(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    """Shortest rotation (x, y, z, w) taking unit direction a onto b."""
    c = np.cross(a, b)
    d = float(np.dot(a, b))
    if d < -1 + 1e-9:
        perp = np.cross(a, [1.0, 0, 0])
        if np.linalg.norm(perp) < 1e-6:
            perp = np.cross(a, [0, 1.0, 0])
        return axis_angle(perp, math.pi)
    q = np.array([c[0], c[1], c[2], 1 + d])
    return q / np.linalg.norm(q)


def qrot(q: np.ndarray, v: np.ndarray) -> np.ndarray:
    return rt.quat_to_mat(q) @ v


def unflip_twists(glob: np.ndarray, names: list[str]) -> np.ndarray:
    """glob (n,22,4) global rotations -> the same with each listed bone's twist about its own +Y axis rate-limited."""
    out = glob.copy()
    step = math.radians(TWIST_STEP_DEG)
    y = np.array([0.0, 1.0, 0.0])
    for b in TWIST_BONES:
        k = names.index(b)
        for i in range(1, len(glob)):
            prev = out[i - 1, k]
            want = glob[i, k]
            a_prev = qrot(prev, y)
            a_now = qrot(want, y)
            moved = rt.qmul(swing_quat(a_prev, a_now), prev)     # carried along without any twist of its own
            d = rt.qmul(want, rt.qconj(moved))                    # what is left: a turn about a_now
            if d[3] < 0:
                d = -d
            phi = 2 * math.atan2(float(np.dot(d[:3], a_now)), float(d[3]))
            # a jump of over FLIP_DEG in one frame is the solve flipping: the twist stays where it was (until the solve comes back)
            phi = 0.0 if abs(phi) > math.radians(FLIP_DEG) else max(-step, min(step, phi))
            q = rt.qmul(axis_angle(a_now, phi), moved)
            out[i, k] = q / np.linalg.norm(q)
    return out


def first_tilted(q: np.ndarray, hips: np.ndarray, t: rt.Target, deg: float) -> int:
    """The first frame whose trunk (hips to the upper spine) leans <deg> or more from upright (clips.toml takeoff_tilt_deg, default
    TAKEOFF_TILT_DEG)."""
    n = len(q)
    gr, gt = t.fk(rt.quats_to_mats(q.reshape(-1, 4)).reshape(n, 22, 3, 3), hips)
    sp = gt[:, rt.BONES.index("Spine2")] - gt[:, 0]
    tilt = np.degrees(np.arccos(np.clip(sp[:, 1] / np.linalg.norm(sp, axis=1), -1, 1)))
    hit = np.flatnonzero(tilt >= deg)
    return int(hit[0]) if len(hit) else 0


def smooth(local: np.ndarray, hips: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """A Gaussian over time (SMOOTH_SIGMA frames, the ends held) on every local rotation (sign-aligned, averaged, renormalised) and the hips."""
    n = len(local)
    r = int(math.ceil(3 * SMOOTH_SIGMA))
    w = np.exp(-0.5 * (np.arange(-r, r + 1) / SMOOTH_SIGMA) ** 2)
    out_q = np.zeros_like(local); out_h = np.zeros_like(hips)
    for i in range(n):
        idx = np.clip(np.arange(i - r, i + r + 1), 0, n - 1)
        qs = local[idx]                                            # (m,22,4)
        sgn = np.sign(np.sum(qs * local[i][None], axis=2, keepdims=True))
        sgn[sgn == 0] = 1
        avg = np.sum(qs * sgn * w[:, None, None], axis=0)
        out_q[i] = avg / np.linalg.norm(avg, axis=1, keepdims=True)
        out_h[i] = np.sum(hips[idx] * w[:, None], axis=0) / w.sum()
    return out_q, out_h


def bake(dump: dict, clip: dict, t: rt.Target) -> rt.Result:
    names = [rt.BONES[k] for k in range(22)]
    raw = np.array(dump["frames"], dtype=float)[::2]           # 120 Hz ticks -> 60 Hz frames
    n = len(raw)
    hips = raw[:, :3].copy()
    q = raw[:, 3:].reshape(n, 22, 4)
    # globals
    glob = np.zeros_like(q)
    for k in range(22):
        p = t.parent[k]
        glob[:, k] = q[:, k] if p < 0 else np.array([rt.qmul(glob[i, p], q[i, k]) for i in range(n)])
    # face +Z, start over the origin
    # the way the script had him face (the game's facing: 0 = world +X; the model's yaw is its negative), not the hips' (a smother
    # turns his front along the ground from its first ticks)
    heading = -float(dump["facing"])
    yq = axis_angle(np.array([0, 1.0, 0]), -heading)
    start = np.array([hips[0, 0], 0.0, hips[0, 2]])
    hips = np.array([qrot(yq, h - start) for h in hips])
    for i in range(n):
        for k in range(22):
            glob[i, k] = rt.qmul(yq, glob[i, k])
    glob = unflip_twists(glob, names)
    local = np.zeros_like(glob)
    for k in range(22):
        p = t.parent[k]
        local[:, k] = glob[:, k] if p < 0 else np.array([rt.qmul(rt.qconj(glob[i, p]), glob[i, k]) for i in range(n)])
    for i in range(n):
        for k in range(22):
            if local[i, k, 3] < 0:
                local[i, k] = -local[i, k]
    # a flight starts a little before its take-off: the procedural dive's first ticks (the body thrown from the stance as its weight
    # jumps toward the dive pose) are the CMU take-off's part, and their swings are too fast to keep
    cut = 0
    tilt_frame = None
    if "flight" in clip.get("tags", []) and dump.get("contact_s") is not None:
        tilt_frame = first_tilted(q, hips, t, float(clip.get("takeoff_tilt_deg", TAKEOFF_TILT_DEG)))
        cut = max(0, tilt_frame - FLIGHT_LEAD_IN)
        local, hips = local[cut:], hips[cut:] - np.array([hips[cut, 0], 0.0, hips[cut, 2]])
        n = len(local)
    local, hips = smooth(local, hips)
    gr, gt = t.fk(rt.quats_to_mats(local.reshape(-1, 4)).reshape(n, 22, 3, 3), hips)

    # foot contacts as retarget.py labels them
    # (each foot point's height above where it sits standing on the ground; the speed of their middle)
    contacts = np.zeros((n, 2), dtype=bool)
    rest_h = rt.foot_rest_heights(t)
    for s in range(2):
        pts = rt.foot_points(t, gr, gt, s)
        contacts[:, s] = rt.label_contacts((pts[:, :, 1] - rest_h).min(axis=1), rt.speed(pts.mean(axis=1)))
    events = {}
    fps = rt.FPS
    contact = dump.get("contact_s")
    if contact is not None:
        c = int(round(contact * fps)) - cut
        events["contact"] = min(c, n - 1)
        ank = gt[:, [rt.BONES.index("LeftFoot"), rt.BONES.index("RightFoot")], 1]
        lifted = np.where((ank[:, 0] > ank[0, 0] + TAKEOFF_LIFT) & (ank[:, 1] > ank[0, 1] + TAKEOFF_LIFT))[0]
        tk = int(lifted[0]) if len(lifted) else c // 3
        # a flight's take-off is where the body has tipped TAKEOFF_TILT_DEG from upright: the CMU take-off hands over to it there, so
        # the switch is from an upright body to one just leaning in (not to one already flying at 45 deg, whose arms, set on the
        # tilted trunk, would point straight up while the blend brought the trunk over)
        if tilt_frame is not None:
            tk = tilt_frame - cut
        events["takeoff"] = max(1, min(tk, c - int(0.1 * fps)))
        hy = gt[:, 0, 1]
        after = np.arange(c, n)
        low = hy[c:].min()
        land = after[hy[c:] <= low + LANDING_BAND]
        events["landing"] = int(land[0]) if len(land) else n - 1
    events = {k: events[k] for k in ("takeoff", "contact", "landing") if k in events}
    problems = []
    lowest = float(gt[:, :, 1].min())
    if lowest < -0.005:
        problems.append(f"joint {lowest * 100:.1f} cm below the ground at frame {int(gt[:, :, 1].min(axis=1).argmin())}")
    stats = {"frames": n, "seconds": round(n / fps, 3), "lowest_joint_m": round(lowest, 3), "scale": 1.0, "max_dir_err_deg": 0.0}
    return rt.Result(clip["name"], hips, local, contacts, events, False, problems, stats)


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        return 2
    src = Path(argv[0])
    t = rt.load_target()
    names = [nm.replace(":", "_") for nm in t.names]
    bad = 0
    for c in rt.load_clips():
        if c.get("source") != "baked":
            continue
        path = src / f"{c['script']}.json"
        if not path.exists():
            print(f"MISSING {c['name']}: {path}")
            bad += 1
            continue
        dump = json.loads(path.read_text())
        if dump["bones"] != names:
            print(f"REFUSED {c['name']}: the dump's bones differ from keeper.glb's")
            bad += 1
            continue
        r = bake(dump, c, t)
        if r.problems:
            print(f"REFUSED {c['name']}: " + "; ".join(r.problems))
            bad += 1
            continue
        rt.write_kclip(rt.OUT / f"{c['name']}.kclip", r, names)
        print(f"wrote {c['name']}: {r.stats['frames']} frames, events {r.events}, lowest joint {r.stats['lowest_joint_m']} m, "
              f"contacts L{len(rt.spans(r.contacts[:, 0]))} R{len(rt.spans(r.contacts[:, 1]))}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
