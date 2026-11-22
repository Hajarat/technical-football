"""Authors the attacker's shot clips by kind and ball-out angle from hand-made key poses (tools/anim/shot_moves/*.toml) and
writes them into game/assets/anim/attacker/shots (*.kclip + clips.json), beside the two motion-captured kicks shots.py cuts.

    uv run --with numpy python3 tools/anim/shot_author.py          # every kind, entry and angle bin, both feet

There is no motion capture of a finesse, a chip, an outside-of-the-foot strike or a kick struck at an angle to the run, so each is
keyed by hand, as author.py's skill moves are, and solved by author.py (same key channels, same solver, same checks).

A shot file is one kind of kick (power, finesse, chip, outside) in right-foot form. It is written once and made into a clip per
entry (run, stand) and per angle bin; the mirror is the left foot's clip.

* kind, surface, notes; [ball] radius.
* [entry.run] / [entry.stand]: entry_speed (m/s), ball = [x, z] (where the ball lies at the contact, in the approach frame: he
  starts at the origin facing +Z), spans (seconds between the events below) and its own [[entry.<name>.key]] poses: the approach,
  in the ground frame, exactly as in an author.py move file.
  [entry.<name>.stance] (or a [stance] for both): the spans each foot stands, as in author.py (checked there).
* [[kick]]: the kick's poses, shared by both entries, in the kick frame: positions are from the point on the ground under the
  ball at the contact, z along the way the ball leaves, x to the left of that, y the height above the ground. Each has turn
  (0..1, default 1): how far the body has come round by then, and for the striking foot swing (0..1, default 1): how far that
  foot's path has come round beyond the body. Channels as in author.py; a pelvis or foot key that sets pos must set yaw too.
* [[bin]]: deg (the ball-out angle the clip is made for, against the approach: + toward the striking foot's own side, his right
  for the right foot), label (in the clip's name), range = [lo, hi] (the angles the clip covers), body (0..1: the share of the
  angle the body turns by; the rest is the leg swinging across or outward), entries (default both) and optionally spans by
  entry (a turn-and-shoot takes longer).

Events (frames; the same as shots.py's): start (the plant foot leaves the ground for its last step), takeoff (the striking foot
leaves the ground), backswing (its top), plant (the plant foot lands), contact, end (the follow-through ends: the striking foot
back down); the clip runs on to a last pose after `end` for the hand-back.

For a bin of D degrees the body's poses are turned about the ball by -D * body * turn (yaw is toward his left), the striking
foot's by -D * (body * turn + (1 - body) * swing): the kick itself is always the same kick along the ball-out direction, and the
body comes round to it over the last stride. The contact pose's distance behind the ball is then moved until the boot meets the
ball's surface (author.py's touch check), so the keys need not be exact.

Written per clip to clips.json: kind (power, finesse, chip, outside), foot, entry, bin_deg, bin_range, surface, the events,
kick_dir and kick_angle_deg (the striking ankle's direction of travel at the contact, measured; the angle + toward the striking
foot's side), approach speed, root (the hips' smoothed ground track, the hips over the origin at the contact, as shots.py), and the
ball's track for the gallery. Their set is "shot:<kind>"; the motion-captured kicks keep set "shot" and are kind "instep", the
power kick's 0-degree bin.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
import tomllib
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import author as au  # noqa: E402
import retarget as rt  # noqa: E402
import shots as sh  # noqa: E402

REPO = HERE.parent.parent
MOVES = HERE / "shot_moves"
OUT = REPO / "game/assets/anim/attacker/shots"
FPS = au.FPS
EVENTS = ("start", "takeoff", "backswing", "plant", "contact", "end")
# normalised times of the events on every shot's timeline, and of the last pose (the hand-back plays over the span after `end`)
EVENT_U = {"start": 0.0, "takeoff": 0.2, "backswing": 0.4, "plant": 0.5, "contact": 0.6, "end": 0.9, "tail": 1.0}
BODY = ("pelvis", "foot_l")      # turned with the body; foot_r (the striking foot of the right-foot form) also swings
TOUCH_TARGET = 0.004             # metres: the boot's nearest point is brought this far outside the ball's surface at the contact
BALL_AWAY = 0.45                 # metres the ball has gone one frame after the contact (27 m/s: it leaves ahead of the boot)
KINDS = ("power", "finesse", "chip", "outside")


def load_shot(path: Path) -> dict:
    with open(path, "rb") as f:
        m = tomllib.load(f)
    m["path"] = str(path.relative_to(REPO)) if path.is_relative_to(REPO) else str(path)
    if m.get("kind") not in KINDS:
        raise ValueError(f"{path}: kind must be one of {', '.join(KINDS)}")
    for b in m.get("bin", []):
        lo, hi = b["range"]
        if not lo <= b["deg"] <= hi:
            raise ValueError(f"{path}: bin {b['label']}: {b['deg']} is outside its range {b['range']}")
    for k in m.get("kick", []):
        for g in ("pelvis", "foot_l", "foot_r"):
            if g in k and "pos" in k[g] and "yaw" not in k[g]:
                raise ValueError(f"{path}: kick key at t={k['t']}: {g} sets pos without yaw")
    return m


def bins_of(m: dict, entry: str) -> list[dict]:
    return [b for b in m.get("bin", []) if entry in b.get("entries", ["run", "stand"])]


def to_move(m: dict, entry: str, b: dict, dz: float = 0.0) -> tuple[dict, dict]:
    """The author.py move (right-foot form) for one entry and bin, its contact pose moved dz along the ball-out direction, and
    its variant."""
    e = m["entry"][entry]
    r = float(m.get("ball", {}).get("radius", 0.115))
    bx, bz = (float(v) for v in e["ball"])
    B = np.array([bx, 0.0, bz])
    psi = -float(b["deg"])                       # yaw of the ball-out direction against the approach (toward his left)
    share = float(b.get("body", 1.0))
    spans = [float(s) for s in b.get("spans", {}).get(entry, e["spans"])]
    if len(spans) != len(EVENTS):
        raise ValueError(f"{m['path']}: {entry}: spans needs {len(EVENTS)} values")
    keys = json.loads(json.dumps(e.get("key", [])))
    for k0 in m["kick"]:
        k = json.loads(json.dumps(k0))
        turn = float(k.pop("turn", 1.0)); swing = float(k.pop("swing", 1.0))
        contact = abs(float(k["t"]) - EVENT_U["contact"]) < 1e-9
        for g in ("pelvis", "foot_l", "foot_r"):
            if g not in k or "pos" not in k[g]:
                continue
            a = psi * share * turn + (psi * (1 - share) * swing if g == "foot_r" else 0.0)
            o = np.array(k[g]["pos"], float)
            if g == "foot_r" and contact:
                o = o + np.array([0.0, 0.0, dz])
            p = B + au.ry(a) @ np.array([o[0], 0.0, o[2]])
            k[g]["pos"] = [float(p[0]), float(o[1]), float(p[2])]
            k[g]["yaw"] = float(k[g]["yaw"]) + a
            k[g].pop("frame", None)
        keys.append(k)
    out = au.ry(psi) @ np.array([0.0, 0.0, 1.0])
    c, end = EVENT_U["contact"], EVENT_U["end"]
    frame_u = (1.0 / FPS) / spans[4] * (end - c)      # one frame after the contact, in normalised time
    ball0 = [float(B[0]), r, float(B[2])]
    away = B + out * BALL_AWAY
    far = B + out * 12.0
    keys += [{"t": 0.0, "ball": {"pos": ball0, "ease": 1}}, {"t": c, "ball": {"pos": ball0, "ease": 1}},
             {"t": c + frame_u, "ball": {"pos": [float(away[0]), r + 0.03, float(away[2])], "ease": 0}},
             {"t": 1.0, "ball": {"pos": [float(far[0]), r + 0.5, float(far[2])], "ease": 0}}]
    move = {"name": f"shot_{m['kind']}_{entry}_{b['label']}", "move": m["kind"], "side": "none", "foot": "right", "entry": entry,
            "entry_speed": float(e.get("entry_speed", 0.0)), "notes": m.get("notes", ""), "path": m["path"],
            "events": dict(EVENT_U), "variant": [{"suffix": "", "spans": spans}],
            "ball": {"radius": r, "touch": {"contact": "right"}}, "key": keys,
            "stance": json.loads(json.dumps(e.get("stance", m.get("stance", {})))),
            "shot": {"kind": m["kind"], "surface": m.get("surface", "laces"), "bin_deg": float(b["deg"]),
                     "bin_range": [float(b["range"][0]), float(b["range"][1])], "label": b["label"], "body": share}}
    return move, move["variant"][0]


def solve_bin(m: dict, entry: str, b: dict, rig: au.Rig, mirror: bool = False) -> au.Clip:
    """The clip for one entry and bin: the contact pose is moved along the ball-out direction until the striking boot's nearest
    point is TOUCH_TARGET outside the ball (bisection: further forward is closer)."""
    def at(dz: float) -> au.Clip:
        move, variant = to_move(m, entry, b, dz)
        return au.solve(au.mirror_move(move) if mirror else move, variant, rig)

    def gap(c: au.Clip) -> float:
        return float(c.stats["touch_gap_m"]["contact"])

    lo, hi = -0.25, 0.25
    c = at(0.0)
    if abs(gap(c) - TOUCH_TARGET) <= 0.002:
        return c
    g_lo, g_hi = gap(at(lo)), gap(at(hi))
    if not g_lo > TOUCH_TARGET > g_hi:
        return c                      # the pose does not bracket the ball: left as keyed, the touch check reports it
    for _ in range(16):
        mid = 0.5 * (lo + hi)
        c = at(mid)
        g = gap(c)
        if abs(g - TOUCH_TARGET) <= 0.001:
            break
        if g > TOUCH_TARGET:
            lo = mid
        else:
            hi = mid
    return c


def finish(c: au.Clip, rig: au.Rig) -> dict:
    """The clip's shot facts, measured as shots.py measures the captured kicks; the hips moved over the origin at the contact."""
    ev = {e: c.events[e] for e in EVENTS}
    i = ev["contact"]
    shift = np.array([c.hips[i, 0], 0.0, c.hips[i, 2]])
    c.hips = c.hips - shift
    c.ball = c.ball - shift
    gr, gt = au.fk_points(rig, c.local_q, c.hips)
    kf = 1 if c.move["foot"] == "right" else 0
    ank = gt[:, rt.BONES.index(("Left", "Right")[kf] + "Foot")]
    kd = ank[i + 1] - ank[i - 1]
    speed = float(np.linalg.norm(kd) * FPS / 2)
    kd = np.array([kd[0], 0.0, kd[2]]); kd /= np.linalg.norm(kd)
    angle = math.degrees(math.atan2(kd[0], kd[2]))          # toward his left
    f0 = max(0, ev["plant"] - int(round(0.4 * FPS)))
    approach = float(np.linalg.norm((c.hips[ev["plant"]] - c.hips[f0])[[0, 2]]) / max(1, ev["plant"] - f0) * FPS)
    sole_y = 0.0
    return {"events": ev, "kick_dir": kd, "kick_angle_deg": -angle if kf == 1 else angle, "contact_speed": speed,
            "approach_speed": approach, "backswing_ankle_m": float(ank[ev["takeoff"]:i + 1, 1].max() - sole_y),
            "follow_peak_m": float(ank[i:ev["end"] + 1, 1].max() - sole_y), "root": sh.root_track(c.hips), "foot": kf}


def check_shot(c: au.Clip, f: dict) -> None:
    """Checks beyond author.py's: the events as the game reads them."""
    ev, kf = f["events"], f["foot"]
    pf = 1 - kf
    con = c.contacts
    p = c.problems
    if not ev["start"] <= ev["takeoff"] < ev["backswing"] < ev["contact"] or not ev["takeoff"] < ev["plant"] <= ev["contact"] < ev["end"]:
        p.append(f"events out of order: {ev}")
        return
    # (a landing keyed between two frames is down from the frame after it)
    if not con[ev["plant"] + 1:ev["contact"] + 1, pf].all():
        p.append(f"the plant foot is not down from the plant (frame {ev['plant']}) through the contact")
    if con[ev["backswing"]:ev["contact"] + 2, kf].any():
        p.append("the striking foot is on the ground between the backswing and the contact")
    sm = c.move["shot"]
    lo, hi = sm["bin_range"]
    # (against the bin's own angle, the short way round: -180 and 180 are the same direction)
    off = (f["kick_angle_deg"] - sm["bin_deg"] + 180.0) % 360.0 - 180.0
    if not lo - 12.0 <= sm["bin_deg"] + off <= hi + 12.0:
        p.append(f"the striking ankle travels at {f['kick_angle_deg']:.0f} degrees at the contact, the bin is {lo:g} to {hi:g}")


def entry_of(c: au.Clip, f: dict) -> dict:
    m = c.move
    sm = m["shot"]
    n = len(c.hips)
    cs = {("left", "right")[s]: [list(x) for x in rt.spans(c.contacts[:, s])] for s in range(2)}
    return {"name": c.name, "file": f"{c.name}.kclip", "source": "authored", "move_file": m["path"], "set": f"shot:{sm['kind']}",
            "kind": sm["kind"], "surface": sm["surface"], "side": m["foot"], "foot": m["foot"], "entry": m["entry"],
            "bin_deg": sm["bin_deg"], "bin_range": sm["bin_range"], "bin": sm["label"], "body_share": sm["body"],
            "entry_speed": round(f["approach_speed"], 3), "verdict": "keep", "loop": False, "events": f["events"], "fps": FPS,
            "frame_count": n, "seconds": round(n / FPS, 4),
            "event_seconds": {e: round(fr / FPS, 4) for e, fr in f["events"].items()},
            "kick_dir": [round(float(x), 4) for x in f["kick_dir"]], "kick_angle_deg": round(f["kick_angle_deg"], 1),
            "contact_speed_mps": round(f["contact_speed"], 2), "backswing_ankle_m": round(f["backswing_ankle_m"], 3),
            "follow_peak_ankle_m": round(f["follow_peak_m"], 3), "contacts": cs,
            "root": [[round(float(x), 4), round(float(z), 4)] for x, z in f["root"]],
            "ball": {"radius": float(m["ball"]["radius"]), "on": [], "touch": dict(m["ball"]["touch"]),
                     "track": [[round(float(x), 4) for x in b] for b in c.ball]},
            "notes": m.get("notes", ""), "checks": {k: v for k, v in c.stats.items() if k not in ("frames", "seconds")}}


def build(shots: list[dict], rig: au.Rig, only: str | None = None) -> list[tuple[au.Clip, dict]]:
    out = []
    for m in shots:
        for entry in ("run", "stand"):
            if entry not in m.get("entry", {}):
                continue
            for b in bins_of(m, entry):
                if only and only not in f"shot_{m['kind']}_{entry}_{b['label']}":
                    continue
                for mirror in (False, True):
                    c = solve_bin(m, entry, b, rig, mirror)
                    f = finish(c, rig)
                    check_shot(c, f)
                    out.append((c, f))
    return out


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="Author the attacker's shot clips by kind and angle (tools/anim/shot_moves/*.toml).")
    ap.add_argument("--moves-dir", type=Path, default=MOVES)
    ap.add_argument("--model", type=Path, default=au.GLB)
    ap.add_argument("--out", type=Path, default=OUT)
    ap.add_argument("--only", help="solve only the clips whose name contains this, print them and write nothing")
    args = ap.parse_args(argv)
    rig = au.load_rig(args.model)
    names = [nm.replace(":", "_") for nm in rig.t.names]
    shots = [load_shot(p) for p in sorted(args.moves_dir.glob("*.toml"))]
    index, failed = [], []
    built = build(shots, rig, args.only)
    for c, f in built:
        s = c.stats
        line = (f"{c.name}: {s['frames']} frames, events {f['events']}, kick {f['kick_angle_deg']:.0f} deg at {f['contact_speed']:.1f} m/s, "
                f"approach {f['approach_speed']:.2f} m/s, boot gap {s['touch_gap_m'].get('contact', float('nan')) * 100:.1f} cm, "
                f"in ball {s['boot_in_ball_max_m'] * 100:.1f} cm, fastest bone {s['max_bone_deg_per_s']} deg/s ({s['max_turn_at']})")
        if c.problems:
            failed.append(c.name)
            print(f"REFUSED {line}\n    " + "; ".join(c.problems))
        else:
            print("ok " + line)
    if args.only:
        return 1 if failed else 0
    if failed:
        print(f"{len(failed)} clip(s) refused: {', '.join(failed)}; nothing written")
        return 1
    args.out.mkdir(parents=True, exist_ok=True)
    old = json.loads((args.out / "clips.json").read_text())
    # the motion-captured kicks (shots.py) stay as they are in the folder
    index = [e for e in old["clips"] if e.get("source") != "authored"]
    for c, f in built:
        r = rt.Result(c.name, c.hips, c.local_q, c.contacts, f["events"], False, c.problems, c.stats)
        rt.write_kclip(args.out / f"{c.name}.kclip", r, names)
        index.append(entry_of(c, f))
    old["clips"] = index
    (args.out / "clips.json").write_text(json.dumps(old, indent=1) + "\n")
    keep = {e["file"] for e in index}
    for stale in args.out.glob("*.kclip"):
        if stale.name not in keep:
            stale.unlink()
    print(f"wrote {len(built)} authored clips beside {len(index) - len(built)} captured ones")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
