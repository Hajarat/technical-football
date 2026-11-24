"""Authors full-body skill-move clips from hand-made key poses (tools/anim/moves/*.toml) and writes
game/assets/anim/attacker/skills/*.kclip + clips.json.

    uv run --with numpy python3 tools/anim/author.py          # every move file, both sides, every timing variant

No motion capture: a move is a short list of key poses written in an animator's terms (pelvis and chest angles, where each foot and
hand is, which way the knees and elbows point), and this tool solves every 60 Hz frame of them onto the 22 bones the game drives.

Conventions are retarget.py's: model space faces +Z, the character's left is +X, Y is up, metres on the 1.80 m model, the ground at
y = 0 under the boots. Angles in the move files are degrees.

Move file (see moves/ball_roll.toml for a full example):

* name, move (the clip set is "skill:<move>"), side (the authored side, "left" or "right": the mirror is the other; "none" for a
  move without a side, such as the drag-back, whose clips are then named by the foot), foot (the playing foot), entry ("stand" or
  "run") and entry_speed (m/s), notes, and optional [sim] numbers the move was authored to (all copied into clips.json).
* [events]: named events at normalised times 0..1 in time order (start, contact, release, end for a skill move). The time between
  two consecutive events is a span; each [[variant]] gives every span's length in seconds (spans = [...]) and a name suffix, so one
  file makes several timings of the same poses (the keys stay where they are inside their spans).
* [ball]: radius; on = ["contact", "release"]: the playing foot's sole is on the ball between those events (checked), or a list
  of [from, to, foot] spans for moves whose soles take turns (the spin); touch = { contact = "right", contact2 = "left" }: at
  those events that foot's boot meets the ball (checked).
* [[key]]: a pose at normalised time t. Every channel is interpolated on its own over the keys that set it, so a key sets only what
  matters at that moment. Channels:
    pelvis  pos = [x, y, z] (the point between the hip joints, ground frame), yaw, pitch, roll
    chest   yaw, pitch, roll relative to the pelvis (spread over the three spine bones)
    head    yaw, pitch, roll relative to the chest (spread over neck and head)
    foot_l / foot_r  pos (the sole point, the boot's sole under mid-foot that the game puts on the ball; y = 0 is on the ground
            for a flat foot) in frame = "ground" (under the start position), "pelvis" or "ball" (from the ball's centre, moving
            with it); yaw (heading in the ground frame), pitch (toes up), roll, toe (toes bent up at the ball of the foot);
            knee = [x, y, z], the way the knee points, in the pelvis' heading
    hand_l / hand_r  pos in frame = "shoulder" (from the shoulder joint) or "chest" (from the top of the spine), both turned with the
            chest; elbow = the way the elbow points (chest axes); or, instead of pos, angles: swing (forward), abduct (outward),
            bend (elbow; the forearm bends forward, or with trail = 90 downward and 180 back); curl (finger curl 0..1: the clip has no finger bones, so it is written to clips.json for the runtime)
    ball    pos = [x, y, z] (the ball's centre, ground frame)
  Angles: yaw turns toward his left, pitch tips forward (a foot's pitch lifts the toes), roll tips the top toward his left.
* [stance]: left = [[from, to], ...], right = [...] (normalised times): the spans each foot stands on the pitch, written as its
  contact flags (the runtime holds a flagged foot where it landed) instead of the ones read off the solved motion, which miss a
  stance of a few frames. Checked: over each span the foot is on the ground and its sole point still.
* [[ending]]: another ending of the same move, written as its own clip: suffix (added to the clip's name, after the variant's),
  from (a normalised time: the move's keys up to it are kept, its later ones replaced), notes, its own [[ending.key]] tables
  (all after from) and optionally its own [ending.stance] (replacing the move's). clips.json names it in "ending" (the suffix without its underscore); the runtime picks it when the move's
  exit calls for it (a ball roll whose exit touch is made by the support foot).
  ease (0..1) on the key, or inside a channel's table for that channel: 0 passes through the key smoothly, 1 comes to rest on it;
  a position's ease may be one per axis (ease = [1, 1, 0]: a ball that stops across the body but keeps rolling along the run).
  Between keys the curve is a monotone cubic (it never overshoots a key), its slope at a key scaled by 1 - ease.

Solve, per frame: pelvis and spine rotations; each leg by two-bone IK from its hip joint to the ankle that puts the sole point where
the key says, bending toward the knee hint; the foot and toes turned as keyed; each arm by two-bone IK from its shoulder joint, the
hand keeping its rest angle to the forearm; neck and head. Foot contacts are labelled as retarget.py labels them.

The mirror (the other side) is made from the move file itself: left and right channels swapped, x and every yaw and roll negated.

Checks (after quantisation, as the game will see it); a clip that fails is not written and the run exits non-zero:
* no bone joint under the ground, and no foot point (ankle, toe joint, toe tip) more than 1 cm into it;
* no leg asked to reach past its length;
* no bone turning faster than 1,500 deg/s between two frames; a skill-move file (one with a [sheet] table) may raise its own
  limit with max_bone_deg_s, up to 2,500 deg/s (for exaggerated foot work read from the game camera);
* while the ball span says the sole is on the ball, the playing foot's sole point within 1 cm of the ball's surface;
* no boot vertex (every third, carried by the foot or toe bone) more than 1 cm inside the ball at any frame;
* at each [ball] touch event, that foot's boot within 1.5 cm of the ball's surface;
* over each [stance] span, the foot's lowest point within 1 cm of the ground and its sole point within 5 mm of where it landed.
"""
from __future__ import annotations

import argparse
import json
import math
import struct
import sys
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import retarget as rt  # noqa: E402

REPO = HERE.parent.parent
GLB = REPO / "game/assets/characters/player.glb"
MOVES = HERE / "moves"
OUT = REPO / "game/assets/anim/attacker/skills"
SHEETS = REPO / "data/moves/sheets.json"
FPS = 60
MAX_TURN_DEG_S = 1500.0
SKILL_MAX_TURN_DEG_S = 2500.0    # the most a skill-move file's own max_bone_deg_s may ask for
MAX_SOLE_OFF = 0.01
MAX_FOOT_UNDER = 0.01
MAX_BOOT_IN = 0.01
MAX_TOUCH_GAP = 0.015
MAX_STANCE_H = 0.01
MAX_STANCE_SLIP = 0.005
STRETCH_TOL = 1e-4
BONES = rt.BONES
SIDES = ("l", "r")
SIDE_NAME = {"l": "Left", "r": "Right"}
# shares of the chest's turn (relative to the pelvis) taken by Spine, Spine1, Spine2 (cumulative); of the head's by Neck, Head
SPINE_SHARE = (0.3, 0.65, 1.0)
NECK_SHARE = (0.4, 1.0)


# ---------------------------------------------------------------------------------------------------------------- rotations

def rx(deg: float) -> np.ndarray:
    a = math.radians(deg); c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def ry(deg: float) -> np.ndarray:
    return rt.yaw_mat(math.radians(deg))


def rz(deg: float) -> np.ndarray:
    return rt.rot_z(math.radians(deg))


def euler(yaw: float, pitch: float, roll: float) -> np.ndarray:
    """yaw toward his left (+X), then pitch forward (top toward +Z), then roll (top toward his left)."""
    return ry(yaw) @ rx(pitch) @ rz(-roll)


def rot_pow(m: np.ndarray, f: float) -> np.ndarray:
    """The rotation turned f of the way from identity to m (same axis)."""
    q = rt.mats_to_quats(m[None])[0]
    ang = 2 * math.acos(min(1.0, q[3]))
    if ang < 1e-9:
        return np.eye(3)
    axis = q[:3] / math.sin(ang / 2)
    a = ang * f
    qq = np.concatenate([axis * math.sin(a / 2), [math.cos(a / 2)]])
    return rt.quats_to_mats(qq[None])[0]


def frame_of(d: np.ndarray, a: np.ndarray) -> np.ndarray:
    """Orthonormal basis with first column d and second column a made perpendicular to it."""
    d = d / np.linalg.norm(d)
    a = a - np.dot(a, d) * d
    n = np.linalg.norm(a)
    if n < 1e-9:
        a = np.cross(d, [1.0, 0, 0]) if abs(d[0]) < 0.9 else np.cross(d, [0, 1.0, 0]); n = np.linalg.norm(a)
    a = a / n
    return np.stack([d, a, np.cross(d, a)], axis=1)


def two_bone(root: np.ndarray, target: np.ndarray, l1: float, l2: float, pole: np.ndarray) -> tuple[np.ndarray, float, np.ndarray]:
    """The middle joint of a two-bone chain from root reaching target, bent toward pole. Returns it, how far the target lies
    beyond the chain's length (0 when it is within reach; the chain then points straight at it) and the bend's hinge axis
    (root-to-target cross the way the middle joint points)."""
    v = target - root
    d = float(np.linalg.norm(v))
    over = max(0.0, d - (l1 + l2))
    d = min(max(d, abs(l1 - l2) + 1e-6, 1e-6), l1 + l2 - 1e-9)
    u = v / max(1e-12, np.linalg.norm(v))
    w = pole - np.dot(pole, u) * u
    if np.linalg.norm(w) < 1e-9:
        w = np.cross(u, [1.0, 0, 0])
    w = w / np.linalg.norm(w)
    a = (l1 * l1 - l2 * l2 + d * d) / (2 * d)
    h = math.sqrt(max(0.0, l1 * l1 - a * a))
    return root + u * a + w * h, over, np.cross(u, w)


def hinge_rest(root: np.ndarray, mid: np.ndarray, end: np.ndarray, fallback: np.ndarray) -> np.ndarray:
    """The rest chain's hinge axis, as two_bone gives it: the middle joint's offset from the root-end line, or fallback when the
    chain is nearly straight (the model's rest legs are)."""
    u = (end - root) / np.linalg.norm(end - root)
    w = mid - root - np.dot(mid - root, u) * u
    if np.linalg.norm(w) < 0.005:
        w = fallback - np.dot(fallback, u) * u
    return np.cross(u, w / np.linalg.norm(w))


# ---------------------------------------------------------------------------------------------------------------- spline

@dataclass
class Key:
    t: float           # seconds
    v: object          # a value, or (frame, value) for framed channels
    ease: object       # 0..1, or one per component (a ball path eased across but not along)


def slopes(ts: np.ndarray, vs: np.ndarray, ease: np.ndarray) -> np.ndarray:
    """Per-key slopes of a monotone cubic through (ts, vs) (Fritsch-Carlson, per component), scaled by 1 - ease."""
    n = len(ts)
    m = np.zeros_like(vs)
    if n < 2:
        return m
    h = np.diff(ts)
    d = np.diff(vs, axis=0) / h[:, None]
    m[0] = d[0]; m[-1] = d[-1]
    for i in range(1, n - 1):
        w = (d[i - 1] * h[i] + d[i] * h[i - 1]) / (h[i - 1] + h[i])
        same = d[i - 1] * d[i] > 0
        lim = 3 * np.minimum(np.abs(d[i - 1]), np.abs(d[i]))
        m[i] = np.where(same, np.clip(w, -lim, lim), 0.0)
    # end keys: never steeper than three times the first or last chord (no overshoot)
    return m * (1 - ease)


def hermite(ts: np.ndarray, vs: np.ndarray, ms: np.ndarray, t: float) -> np.ndarray:
    if t <= ts[0]:
        return vs[0].copy()
    if t >= ts[-1]:
        return vs[-1].copy()
    i = int(np.searchsorted(ts, t, side="right") - 1)
    h = ts[i + 1] - ts[i]
    s = (t - ts[i]) / h
    h00 = 2 * s ** 3 - 3 * s ** 2 + 1; h10 = s ** 3 - 2 * s ** 2 + s; h01 = -2 * s ** 3 + 3 * s ** 2; h11 = s ** 3 - s ** 2
    return h00 * vs[i] + h10 * h * ms[i] + h01 * vs[i + 1] + h11 * h * ms[i + 1]


def interp(keys: list[Key], t: float, resolve=None) -> np.ndarray:
    """A channel at t. resolve(value, t) turns a framed key's value into the common space at t (a ball-frame foot key follows the
    ball as it is at t), so a channel can pass between frames smoothly."""
    ts = np.array([k.t for k in keys])
    vs = np.array([np.atleast_1d(resolve(k.v, t) if resolve else k.v).astype(float) for k in keys])
    ease = np.array([np.broadcast_to(np.asarray(k.ease, float), vs.shape[1:]) for k in keys])
    return hermite(ts, vs, slopes(ts, vs, ease), t)


# ---------------------------------------------------------------------------------------------------------------- rig

@dataclass
class Rig:
    t: rt.Target
    rest_t: np.ndarray       # (22,3) rest joint positions, ground at 0
    sole: np.ndarray         # (2,3) the boots' sole points at rest (as SkinnedBody picks them), ground at 0
    leg: tuple[float, float]
    arm: tuple[float, float]
    rest_mid: np.ndarray     # mid hip joints at rest
    foot_rest_h: np.ndarray  # ankle, toe joint, toe tip heights above the ground at rest
    knee_rest: np.ndarray    # (2,3) the way each rest knee points (off the hip-ankle line; about forward)
    boot: list = field(default_factory=list)       # per foot: (bone index per vertex, offset in that bone's axes)

    def idx(self, b: str) -> int:
        return BONES.index(b)


def boot_vertices(path: Path, rest_t: np.ndarray, lift: float) -> tuple[np.ndarray, list[np.ndarray]]:
    """Per foot (left, right) the sole point SkinnedBody.BuildSurfaces picks (the lowest boot vertex that is central across the
    foot and between 40 and 70 % of its length), and the foot's boot vertices at rest (ground at 0)."""
    j, binchunk = rt.read_glb(path)
    verts = []
    for m in j["meshes"]:
        for prim in m["primitives"]:
            mat = j["materials"][prim["material"]]["name"] if "material" in prim else ""
            if mat != "boots":
                continue
            acc = j["accessors"][prim["attributes"]["POSITION"]]
            bv = j["bufferViews"][acc["bufferView"]]
            off = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
            stride = bv.get("byteStride", 12)
            raw = np.frombuffer(binchunk, dtype=np.uint8, count=stride * (acc["count"] - 1) + 12, offset=off)
            for i in range(acc["count"]):
                verts.append(np.frombuffer(raw[i * stride: i * stride + 12].tobytes(), dtype="<f4"))
    v = np.array(verts, dtype=float)
    v[:, 1] += lift
    feet = [rest_t[BONES.index("LeftFoot")], rest_t[BONES.index("RightFoot")]]
    left = np.abs(v[:, 0] - feet[0][0]) <= np.abs(v[:, 0] - feet[1][0])
    out = np.zeros((2, 3))
    boots = [v[left], v[~left]]
    for i, sel in enumerate((left, ~left)):
        p = v[sel]
        outward = 1.0 if i == 0 else -1.0
        z0, z1 = p[:, 2].min(), p[:, 2].max(); dz = z1 - z0
        l = (p[:, 0] - feet[i][0]) * outward
        half, lmid = (l.max() - l.min()) * 0.5, (l.max() + l.min()) * 0.5
        mid = (p[:, 2] > z0 + 0.4 * dz) & (p[:, 2] < z0 + 0.7 * dz)
        central = np.abs(l - lmid) < 0.25 * half
        c = p[mid & central]
        out[i] = c[np.argmin(c[:, 1])]
    return out, boots


def load_rig(path: Path = GLB) -> Rig:
    t = rt.load_target(path)
    lift = -t.sole_y
    rest_t = t.rest_glob_t + np.array([0, lift, 0])
    sole, boots = boot_vertices(path, t.rest_glob_t, lift)
    k = BONES.index
    leg = (float(np.linalg.norm(rest_t[k("LeftLeg")] - rest_t[k("LeftUpLeg")])), float(np.linalg.norm(rest_t[k("LeftFoot")] - rest_t[k("LeftLeg")])))
    arm = (float(np.linalg.norm(rest_t[k("LeftForeArm")] - rest_t[k("LeftArm")])), float(np.linalg.norm(rest_t[k("LeftHand")] - rest_t[k("LeftForeArm")])))
    mid = (rest_t[k("LeftUpLeg")] + rest_t[k("RightUpLeg")]) * 0.5
    knee = []
    for S in ("Left", "Right"):
        h, kn, a = rest_t[k(S + "UpLeg")], rest_t[k(S + "Leg")], rest_t[k(S + "Foot")]
        u = (a - h) / np.linalg.norm(a - h)
        w = kn - h - np.dot(kn - h, u) * u
        knee.append(w / np.linalg.norm(w))
    boot = []
    for s, S in enumerate(("Left", "Right")):
        ft, toe = k(S + "Foot"), k(S + "ToeBase")
        vv = boots[s][::3]
        bone = np.where(vv[:, 2] > rest_t[toe][2], toe, ft)
        off = np.array([t.rest_glob_r[b].T @ (p - rest_t[b]) for b, p in zip(bone, vv)])
        boot.append((bone, off))
    return Rig(t, rest_t, sole, leg, arm, mid, rt.foot_rest_heights(t), np.array(knee), boot)


# ---------------------------------------------------------------------------------------------------------------- move files

GROUPS = ("pelvis", "chest", "head", "foot_l", "foot_r", "hand_l", "hand_r", "ball", "root")
VEC = {"pos", "knee", "elbow"}


def load_move(path: Path) -> dict:
    with open(path, "rb") as f:
        m = tomllib.load(f)
    m.setdefault("name", path.stem)
    m["path"] = str(path.relative_to(REPO)) if path.is_relative_to(REPO) else str(path)
    ev = list(m["events"].items())
    if [v for _, v in ev] != sorted(v for _, v in ev) or ev[0][1] != 0 or ev[-1][1] != 1:
        raise ValueError(f"{path}: events must run in time order from 0 to 1")
    for v in m.get("variant", [{}]):
        if len(v.get("spans", [])) != len(ev) - 1:
            raise ValueError(f"{path}: variant {v.get('suffix', '')!r} needs {len(ev) - 1} spans")
    for k in m["key"] + [k for e in m.get("ending", []) for k in e.get("key", [])]:
        for g in k:
            if g not in GROUPS and g not in ("t", "ease", "label"):
                raise ValueError(f"{path}: key at t={k.get('t')}: unknown channel group {g!r}")
    for e in m.get("ending", []):
        if not e.get("suffix") or any(float(k["t"]) <= float(e["from"]) for k in e.get("key", [])):
            raise ValueError(f"{path}: an ending needs a suffix and keys after its from")
    return m


def endings(m: dict) -> list[dict]:
    """The move and each of its [[ending]]s as a move of its own: the keys up to the ending's from, then the ending's keys."""
    base = {k: v for k, v in m.items() if k != "ending"}
    out = [base]
    for e in m.get("ending", []):
        alt = json.loads(json.dumps(base))
        cut = float(e["from"])
        alt["key"] = [k for k in alt["key"] if float(k["t"]) <= cut + 1e-9] + json.loads(json.dumps(e["key"]))
        alt["ending"] = e["suffix"]
        if "stance" in e:
            alt["stance"] = json.loads(json.dumps(e["stance"]))
        if "notes" in e:
            alt["notes"] = e["notes"]
        out.append(alt)
    return out


def _flip_vec(v):
    return [-v[0], v[1], v[2]]


def on_spans(m: dict) -> list[tuple[str, str, str]]:
    """[ball] on: the spans a sole is on the ball, as (from event, to event, foot): on = ["contact", "release"] (the move's foot)
    or on = [["contact", "lift", "right"], ["contact2", "release", "left"]]."""
    on = m.get("ball", {}).get("on", [])
    if on and isinstance(on[0], str):
        return [(on[0], on[1], m["foot"])]
    return [(a, b, f) for a, b, f in on]


def mirror_move(m: dict) -> dict:
    """The same move on the other side: left and right channels swapped, x negated, every yaw and roll negated."""
    out = json.loads(json.dumps(m))
    other = {"left": "right", "right": "left"}
    out["side"] = other.get(m["side"], m["side"])
    out["foot"] = other[m["foot"]]
    if "stance" in out:
        out["stance"] = {other[f]: v for f, v in out["stance"].items()}
    bl = out.get("ball", {})
    if bl.get("on") and not isinstance(bl["on"][0], str):
        bl["on"] = [[a, b, other[f]] for a, b, f in bl["on"]]
    if "touch" in bl:
        bl["touch"] = {e: other[f] for e, f in bl["touch"].items()}
    sh = out.get("sheet", {})
    for leg in sh.get("leg", []):
        if "foot" in leg:
            leg["foot"] = other[leg["foot"]]
    if "foot" in sh.get("exit", {}):
        sh["exit"]["foot"] = other[sh["exit"]["foot"]]
    keys = []
    for k in out["key"]:
        nk = {}
        for g, val in k.items():
            if not isinstance(val, dict):
                nk[g] = val; continue
            ng = g[:-1] + ("r" if g.endswith("_l") else "l") if g[-2:] in ("_l", "_r") else g
            nv = dict(val)
            for c in VEC:
                if c in nv:
                    nv[c] = _flip_vec(nv[c])
            for c in ("yaw", "roll"):
                if c in nv:
                    nv[c] = -nv[c]
            nk[ng] = nv
        keys.append(nk)
    out["key"] = keys
    return out


@dataclass
class Timeline:
    events: dict[str, float]     # seconds
    spans: list[float]
    seconds: float
    names: list[str]
    u: list[float]               # normalised event times

    def sec(self, u: float) -> float:
        """Normalised time -> seconds, piecewise linear between events."""
        for i in range(len(self.u) - 1):
            if u <= self.u[i + 1] or i == len(self.u) - 2:
                a, b = self.u[i], self.u[i + 1]
                s0 = sum(self.spans[:i])
                return s0 + (u - a) / (b - a) * self.spans[i]
        return self.seconds


def timeline(m: dict, variant: dict) -> Timeline:
    names = list(m["events"].keys()); us = [float(m["events"][n]) for n in names]
    spans = [float(s) for s in variant["spans"]]
    tl = Timeline({}, spans, sum(spans), names, us)
    tl.events = {n: tl.sec(u) for n, u in zip(names, us)}
    return tl


def defaults(rig: Rig) -> dict[str, object]:
    """Each channel's neutral value: the rest pose (feet where they stand at rest, hands hanging, every angle 0)."""
    k = rig.idx
    d: dict[str, object] = {"pelvis.pos": rig.rest_mid.copy(), "ball.pos": np.array([0, 0.11, 0.0])}
    for s, side in enumerate(SIDES):
        S = SIDE_NAME[side]
        d[f"foot_{side}.pos"] = ("ground", rig.sole[s].copy())
        d[f"foot_{side}.knee"] = rig.knee_rest[s].copy()
        d[f"hand_{side}.pos"] = ("shoulder", rig.rest_t[k(S + "Hand")] - rig.rest_t[k(S + "Arm")])
    return d


def channels(m: dict, tl: Timeline, rig: Rig) -> dict[str, list[Key]]:
    """Every channel's keys: 'group.channel' -> keys in time order. Framed channels (a foot's or hand's pos) carry (frame, value).
    A channel whose first key comes after the start starts from its neutral value (the rest pose; angles 0), so a key that sets
    a channel for the first time later in the move does not reach back to the start."""
    ch: dict[str, list[Key]] = {}
    for k in sorted(m["key"], key=lambda k: k["t"]):
        t = tl.sec(float(k["t"]))
        for g in GROUPS:
            if g not in k:
                continue
            val = k[g]
            ease = np.array(val.get("ease", k.get("ease", 0.0)), float)
            if g.startswith("foot") and "pos" in val:
                ch.setdefault(g + ".pos", []).append(Key(t, (val.get("frame", "ground"), np.array(val["pos"], float)), ease))
            elif g.startswith("hand") and ("pos" in val or "swing" in val):
                if "pos" in val:
                    v = (val.get("frame", "shoulder"), np.array(val["pos"], float))
                else:
                    v = ("angles", np.array([val.get("swing", 0.0), val.get("abduct", 0.0), val.get("bend", 0.0), val.get("trail", 0.0)], float))
                ch.setdefault(g + ".pos", []).append(Key(t, v, ease))
            elif "pos" in val:
                ch.setdefault(g + ".pos", []).append(Key(t, np.array(val["pos"], float), ease))
            for c, v in val.items():
                if c in ("pos", "frame", "ease", "swing", "abduct", "bend", "trail"):
                    continue
                ch.setdefault(f"{g}.{c}", []).append(Key(t, np.array(v, float), ease))
    neutral = defaults(rig)
    for name, keys in ch.items():
        if keys[0].t > 1e-9 and not name.endswith(".elbow"):
            v = neutral.get(name, np.zeros(1))
            ch[name] = [Key(0.0, v, 1.0)] + keys
    return ch


# ---------------------------------------------------------------------------------------------------------------- solve

@dataclass
class Clip:
    name: str
    move: dict
    tl: Timeline
    hips: np.ndarray            # (n,3)
    local_q: np.ndarray         # (n,22,4)
    contacts: np.ndarray        # (n,2)
    events: dict[str, int]
    ball: np.ndarray            # (n,3)
    curl: np.ndarray            # (n,2)
    problems: list[str] = field(default_factory=list)
    stats: dict = field(default_factory=dict)
    pel: np.ndarray | None = None       # (n,3) the pelvis point (between the hip joints), ground frame
    root_yaw: np.ndarray | None = None  # (n,) the keyed root yaw, degrees
    leg_ext: np.ndarray | None = None   # (n,2) hip-to-ankle distance as a share of the leg's length
    sheet: dict | None = None           # the move sheet made from this clip (see sheet_of)
    sheet_owner: bool = False           # this clip's sheet is the file's (written to sheets.json)
    sheet_sha: str = ""                 # the hash of the sheet the simulation loads for it (the authored side's first variant)
    root: np.ndarray | None = None      # (n,3) the sheet root: x, z (metres, from under the hips at the start), yaw (degrees)
    measures: dict = field(default_factory=dict)


def solve(m: dict, variant: dict, rig: Rig) -> Clip:
    tl = timeline(m, variant)
    ch = channels(m, tl, rig)
    t_ = rig.t
    n = int(round(tl.seconds * FPS)) + 1
    k = rig.idx
    G = np.zeros((n, 22, 3, 3)); hips = np.zeros((n, 3)); ball = np.zeros((n, 3)); curl = np.zeros((n, 2))
    pels = np.zeros((n, 3)); root_yaw = np.zeros(n); leg_ext = np.zeros((n, 2))
    stretch = []
    R0 = t_.rest_glob_r

    def get(name, t, default):
        return interp(ch[name], t) if name in ch else np.atleast_1d(np.array(default, float))

    for i in range(n):
        t = i / FPS
        b = get("ball.pos", t, [0, 0.11, 0])
        ball[i] = b
        pel = get("pelvis.pos", t, rig.rest_mid)
        pels[i] = pel; root_yaw[i] = get("root.yaw", t, 0)[0]
        Rp = euler(get("pelvis.yaw", t, 0)[0], get("pelvis.pitch", t, 0)[0], get("pelvis.roll", t, 0)[0])
        Rc = euler(get("chest.yaw", t, 0)[0], get("chest.pitch", t, 0)[0], get("chest.roll", t, 0)[0])
        Rh = euler(get("head.yaw", t, 0)[0], get("head.pitch", t, 0)[0], get("head.roll", t, 0)[0])
        G[i, 0] = Rp @ R0[0]
        hips[i] = pel - Rp @ (rig.rest_mid - rig.rest_t[0])
        for j, bn in enumerate(("Spine", "Spine1", "Spine2")):
            G[i, k(bn)] = Rp @ rot_pow(Rc, SPINE_SHARE[j]) @ R0[k(bn)]
        Rchest = Rp @ Rc
        for j, bn in enumerate(("Neck", "Head")):
            G[i, k(bn)] = Rchest @ rot_pow(Rh, NECK_SHARE[j]) @ R0[k(bn)]
        # joint positions of the trunk so far (the hip joints, shoulders)
        def place(bone):
            """World position of a bone's joint given the globals of its ancestors (rest translations)."""
            chain = []
            c = k(bone)
            while c >= 0:
                chain.append(c); c = t_.parent[c]
            pos = hips[i].copy()
            for a, c in zip(chain[::-1][:-1], chain[::-1][1:]):
                pos = pos + G[i, a] @ t_.rest_local_t[c]
            return pos
        Ryaw = ry(get("pelvis.yaw", t, 0)[0])

        def frame_point(fv, tt):
            fr, v = fv
            if fr == "ground":
                return v
            if fr == "ball":
                return get("ball.pos", tt, [0, 0.11, 0]) + v
            if fr == "pelvis":
                pp = get("pelvis.pos", tt, rig.rest_mid)
                R = euler(get("pelvis.yaw", tt, 0)[0], get("pelvis.pitch", tt, 0)[0], get("pelvis.roll", tt, 0)[0])
                return pp + R @ v
            raise ValueError(f"{m['name']}: unknown foot frame {fr!r}")

        # legs
        for s, side in enumerate(SIDES):
            S = SIDE_NAME[side]; g = "foot_" + side
            up, lo, ft, toe = k(S + "UpLeg"), k(S + "Leg"), k(S + "Foot"), k(S + "ToeBase")
            Rf = euler(get(g + ".yaw", t, 0)[0], -get(g + ".pitch", t, 0)[0], get(g + ".roll", t, 0)[0])
            sole_off = rig.sole[s] - rig.rest_t[ft]
            if g + ".pos" in ch:
                sole_pt = interp(ch[g + ".pos"], t, frame_point)
            else:
                sole_pt = rig.sole[s].copy()
            ankle = sole_pt - Rf @ sole_off
            hip = place(S + "UpLeg")
            knee_hint = Ryaw @ get(g + ".knee", t, rig.knee_rest[s])
            knee, over, ax1 = two_bone(hip, ankle, rig.leg[0], rig.leg[1], knee_hint)
            leg_ext[i, s] = float(np.linalg.norm(ankle - hip)) / (rig.leg[0] + rig.leg[1])
            if over > STRETCH_TOL:
                stretch.append((i, S, over))
            # bone frames: (direction, hinge axis) at rest -> now
            h0, k0, a0 = rig.rest_t[up], rig.rest_t[lo], rig.rest_t[ft]
            ax0 = hinge_rest(h0, k0, a0, np.array([0, 0, 1.0]))
            G[i, up] = frame_of(knee - hip, ax1) @ frame_of(k0 - h0, ax0).T @ R0[up]
            G[i, lo] = frame_of(ankle - knee, ax1) @ frame_of(a0 - k0, ax0).T @ R0[lo]
            G[i, ft] = Rf @ R0[ft]
            G[i, toe] = Rf @ rx(-get(g + ".toe", t, 0)[0]) @ R0[toe]
        # arms
        for s, side in enumerate(SIDES):
            S = SIDE_NAME[side]; g = "hand_" + side
            sh, ar, fo, ha = k(S + "Shoulder"), k(S + "Arm"), k(S + "ForeArm"), k(S + "Hand")
            G[i, sh] = Rchest @ R0[sh]
            G[i, ar] = Rchest @ R0[ar]
            shoulder = place(S + "Arm")
            chest_pt = place("Spine2")
            s0, e0, w0 = rig.rest_t[ar], rig.rest_t[fo], rig.rest_t[ha]
            pole0 = e0 - s0 - np.dot(e0 - s0, (w0 - s0) / np.linalg.norm(w0 - s0)) * (w0 - s0) / np.linalg.norm(w0 - s0)
            sign = 1.0 if side == "l" else -1.0

            def hand_point(fv, tt, sign=sign, shoulder=shoulder, chest_pt=chest_pt):
                fr, v = fv
                if fr == "shoulder":
                    return Rchest @ v
                if fr == "chest":
                    return Rchest @ v + chest_pt - shoulder
                if fr == "angles":
                    sw, ab, bend, trail = v
                    d = rx(-sw) @ rz(sign * ab) @ np.array([0, -1.0, 0])
                    tr = math.radians(trail)
                    f = np.array([0, -math.sin(tr), math.cos(tr)])
                    f = f - np.dot(f, d) * d
                    f = f / np.linalg.norm(f) if np.linalg.norm(f) > 1e-6 else np.array([0, 1.0, 0])
                    c, sn = math.cos(math.radians(bend)), math.sin(math.radians(bend))
                    return Rchest @ (d * rig.arm[0] + (c * d + sn * f) * rig.arm[1])
                raise ValueError(f"{m['name']}: unknown hand frame {fr!r}")
            if g + ".pos" in ch:
                wrist = shoulder + interp(ch[g + ".pos"], t, hand_point)
            else:
                wrist = shoulder + Rchest @ (w0 - s0)
            elbow_hint = Rchest @ (get(g + ".elbow", t, pole0) if g + ".elbow" in ch else pole0)
            elbow, _, ax1 = two_bone(shoulder, wrist, rig.arm[0], rig.arm[1], elbow_hint)
            ax0 = hinge_rest(s0, e0, w0, pole0)
            G[i, ar] = frame_of(elbow - shoulder, ax1) @ frame_of(e0 - s0, ax0).T @ R0[ar]
            Df = frame_of(wrist - elbow, ax1) @ frame_of(w0 - e0, ax0).T
            G[i, fo] = Df @ R0[fo]
            G[i, ha] = Df @ R0[ha]
            curl[i, s] = float(get(g + ".curl", t, 0)[0])

    L = rt.locals_from(t_, G)
    q = np.stack([rt.mats_to_quats(L[:, b]) for b in range(22)], axis=1)
    # one hemisphere per bone along the clip (the game slerps the shorter way anyway; this keeps the speed check honest)
    for b in range(22):
        for f in range(1, n):
            if np.dot(q[f, b], q[f - 1, b]) < 0:
                q[f, b] = -q[f, b]
    qd = np.stack([rt.decode_quats(rt.encode_quats(q[:, b])) for b in range(22)], axis=1)
    events = {nm: int(round(s * FPS)) for nm, s in tl.events.items()}
    suffix = variant.get("suffix", "")
    clip = Clip(f"{m['name']}_{m['side'] if m['side'] != 'none' else m['foot']}{suffix}{m.get('ending', '')}", m, tl, hips, qd, np.zeros((n, 2), bool), events, ball, curl,
                pel=pels, root_yaw=root_yaw, leg_ext=leg_ext)
    clip.root = sheet_root(pels, root_yaw)
    check(clip, rig, stretch)
    if "sheet" in m:
        clip.sheet = sheet_of(clip)
        check_sheet(clip, rig)
    return clip


def fk_points(rig: Rig, q: np.ndarray, hips: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    L = np.stack([rt.quats_to_mats(q[:, b]) for b in range(22)], axis=1)
    gr, gt = rig.t.fk(L, hips)
    return gr, gt


def sole_points(rig: Rig, gr: np.ndarray, gt: np.ndarray, s: int) -> np.ndarray:
    ft = BONES.index(SIDE_NAME[SIDES[s]] + "Foot")
    off = rig.t.rest_glob_r[ft].T @ (rig.sole[s] - rig.rest_t[ft])   # in the foot bone's own axes
    return gt[:, ft] + np.einsum("nij,j->ni", gr[:, ft], off)


def turn_limit(m: dict, problems: list) -> float:
    """The fastest a bone of this move's clips may turn, deg/s: 1,500, or a skill-move file's own max_bone_deg_s (a file with a
    [sheet] table), at most SKILL_MAX_TURN_DEG_S. A value it may not have is reported and the default kept."""
    if "max_bone_deg_s" not in m:
        return MAX_TURN_DEG_S
    v = float(m["max_bone_deg_s"])
    if "sheet" not in m:
        problems.append("max_bone_deg_s is for skill-move files (with a [sheet] table) only")
        return MAX_TURN_DEG_S
    if not (0 < v <= SKILL_MAX_TURN_DEG_S):
        problems.append(f"max_bone_deg_s {v:.0f} is outside 0 to {SKILL_MAX_TURN_DEG_S:.0f}")
        return MAX_TURN_DEG_S
    return v


def check(c: Clip, rig: Rig, stretch: list) -> None:
    gr, gt = fk_points(rig, c.local_q, c.hips)
    n = len(c.hips)
    p = c.problems
    low = float(gt[:, :, 1].min()); at = np.unravel_index(int(gt[:, :, 1].argmin()), gt[:, :, 1].shape)
    if low < 0:
        p.append(f"joint {BONES[at[1]]} under the ground ({low * 100:.1f} cm) at frame {at[0]}")
    h = np.zeros((n, 2)); v = np.zeros((n, 2))
    for s in range(2):
        pts = rt.foot_points(rig.t, gr, gt, s)
        hh = pts[:, :, 1] - rig.foot_rest_h
        h[:, s] = hh.min(axis=1)
        v[:, s] = rt.speed(pts.mean(axis=1))
        if h[:, s].min() < -MAX_FOOT_UNDER:
            p.append(f"{'LR'[s]} foot {-h[:, s].min() * 100:.1f} cm into the ground at frame {int(h[:, s].argmin())}")
    c.contacts = np.stack([rt.label_contacts(h[:, s], v[:, s]) for s in range(2)], axis=1)
    # declared stances replace the labels read off the motion for that foot, and are checked
    for s, foot in enumerate(("left", "right")):
        spans_ = c.move.get("stance", {}).get(foot)
        if spans_ is None:
            continue
        c.contacts[:, s] = False
        sp = sole_points(rig, gr, gt, s)
        for a_u, b_u in spans_:
            # (the frames inside the span: a stance keyed between frames starts on the frame after its landing key)
            a, b = math.ceil(c.tl.sec(float(a_u)) * FPS - 1e-6), math.floor(c.tl.sec(float(b_u)) * FPS + 1e-6)
            c.contacts[a:b + 1, s] = True
            hi = float(h[a:b + 1, s].max()); slip = float(np.linalg.norm(sp[a:b + 1] - sp[a], axis=1).max())
            if hi > MAX_STANCE_H or slip > MAX_STANCE_SLIP:
                p.append(f"{foot} stance {a_u}-{b_u} (frames {a}-{b}) not standing: {hi * 100:.1f} cm up, sole moves {slip * 100:.1f} cm")
    if stretch:
        i, side, over = max(stretch, key=lambda x: x[2])
        p.append(f"{side} leg stretched {over * 100:.1f} cm past its length at frame {i} ({len(stretch)} frames)")
    turn = 0.0; turn_at = ""
    for b in range(22):
        d = np.abs(np.sum(c.local_q[1:, b] * c.local_q[:-1, b], axis=1))
        ang = np.degrees(2 * np.arccos(np.clip(d, 0, 1))) * FPS
        if len(ang) and ang.max() > turn:
            turn = float(ang.max()); turn_at = f"{BONES[b]}@{int(ang.argmax()) + 1}"
    limit = turn_limit(c.move, p)
    if turn > limit:
        p.append(f"bone turning {turn:.0f} deg/s ({turn_at}; limit {limit:.0f})")
    sole_err = 0.0
    bl = c.move.get("ball", {})
    for a_ev, b_ev, foot in on_spans(c.move):
        s = SIDES.index(foot[0])
        a, b = c.events[a_ev], c.events[b_ev]
        sp = sole_points(rig, gr, gt, s)
        r = float(bl.get("radius", 0.11))
        err = np.abs(np.linalg.norm(sp[a:b + 1] - c.ball[a:b + 1], axis=1) - r)
        if float(err.max()) > sole_err:
            sole_err = float(err.max())
        if float(err.max()) > MAX_SOLE_OFF:
            p.append(f"{foot} sole {float(err.max()) * 100:.1f} cm off the ball at frame {a + int(err.argmax())}")
    # the boots never pass through the ball (more than MAX_BOOT_IN inside its sphere), on the ball or off it
    deep = 0.0; deep_at = ""
    if "ball" in c.move:
        r = float(c.move["ball"].get("radius", 0.11))
        for s in range(2):
            bone, off = rig.boot[s]
            pts = gt[:, bone] + np.einsum("nkij,kj->nki", gr[:, bone], off)
            d = r - np.linalg.norm(pts - c.ball[:, None, :], axis=2)
            if d.max() > deep:
                deep = float(d.max()); deep_at = f"{'LR'[s]} boot@{int(d.max(axis=1).argmax())}"
        if deep > MAX_BOOT_IN:
            p.append(f"{deep_at.split('@')[0]} {deep * 100:.1f} cm inside the ball at frame {deep_at.split('@')[1]}")
    # a touch event: the named foot's boot meets the ball's surface there (its nearest point within MAX_TOUCH_GAP outside it)
    touches = {}
    if "ball" in c.move:
        r = float(c.move["ball"].get("radius", 0.11))
        for ev, foot in c.move["ball"].get("touch", {}).items():
            s = SIDES.index(foot[0]); f = c.events[ev]
            bone, off = rig.boot[s]
            pts = gt[f, bone] + np.einsum("kij,kj->ki", gr[f, bone], off)
            gap = float((np.linalg.norm(pts - c.ball[f], axis=1) - r).min())
            touches[ev] = round(gap, 4)
            if gap > MAX_TOUCH_GAP:
                p.append(f"{foot} boot {gap * 100:.1f} cm short of the ball at its {ev} (frame {f})")
    c.stats = {"frames": n, "seconds": round(n / FPS, 3), "lowest_joint_m": round(low, 3), "lowest_joint": BONES[at[1]],
               "max_bone_deg_per_s": round(turn), "max_turn_at": turn_at, "sole_off_ball_max_m": round(sole_err, 4),
               "lowest_foot_m": round(float(h.min()), 3), "boot_in_ball_max_m": round(deep, 4), "boot_in_ball_at": deep_at,
               "touch_gap_m": touches}


# ---------------------------------------------------------------------------------------------------------------- move sheets

ROOT_SIGMA_S = 0.05          # the sheet root is the pelvis' ground point low-passed with a Gaussian of this width
ROOT_ACCEL = 12.0            # m/s^2: balance_deceleration
ROOT_ACCEL_PLANT = 20.0      # inside the spans the file marks plant
MAX_LEG_JOIN = 0.02
MAX_LEG_REACH = 0.96
MAX_KEY_GAP_S = 0.05
SNAP_DEG = 20.0
DRIVE_M = 0.25
ANCHORS = ("body", "pitch", "facing", "free", "launch")
DRIVEN = ("body", "pitch", "facing")


def low_pass(v: np.ndarray, sigma_frames: float) -> np.ndarray:
    """A Gaussian low-pass along the first axis. Before the first frame and after the last the motion is continued at the pace
    it has there (the first and last frame steps), so he comes in and leaves at his own velocity."""
    r = max(1, int(math.ceil(3 * sigma_frames)))
    n = len(v)
    if n < 3:
        return v.astype(float).copy()
    k = np.exp(-0.5 * (np.arange(-r, r + 1) / sigma_frames) ** 2); k /= k.sum()
    steps = np.arange(1, r + 1)[:, None]
    before = v[0] - steps[::-1] * (v[1] - v[0])
    after = v[-1] + steps * (v[-1] - v[-2])
    ext = np.concatenate([before, v, after]).astype(float)
    return np.stack([np.convolve(ext[:, c], k, mode="valid") for c in range(v.shape[1])], axis=1)


def sheet_root(pel: np.ndarray, yaw: np.ndarray) -> np.ndarray:
    """The sheet root per frame: x, z (the pelvis' ground point low-passed, from under the hips at the first frame) and the keyed
    root yaw (degrees). Stride sway stays in the clip and out of the simulation."""
    xz = low_pass(pel[:, [0, 2]], ROOT_SIGMA_S * FPS)
    xz = xz - xz[0]
    return np.concatenate([xz, yaw[:, None]], axis=1)


def _in_anchor(anchor: str, ball: np.ndarray, root: np.ndarray, a: int, b: int, radius: float) -> list[list[float]]:
    """The ball's centre over frames a..b in a leg's anchor frame: x to his left, y the lift above rest, z along the run."""
    rows = []
    for i in range(a, b + 1):
        d = np.array([ball[i, 0], ball[i, 1] - radius, ball[i, 2]])
        if anchor == "pitch":
            d = d - np.array([ball[a, 0], 0.0, ball[a, 2]])
        else:
            d = d - np.array([root[i, 0], 0.0, root[i, 1]])
            if anchor == "facing":
                d = ry(-root[i, 2]) @ d
        rows.append([round(float(x), 4) for x in d])
    return rows


def _from_anchor(leg: dict, k: int, ball0: np.ndarray, root_row: np.ndarray, radius: float) -> np.ndarray:
    """A leg's track row k back in the ground frame (the inverse of _in_anchor), for the join check."""
    d = np.array(leg["track"][k], float)
    if leg["anchor"] == "pitch":
        p = d + np.array([ball0[0], 0.0, ball0[2]])
    else:
        if leg["anchor"] == "facing":
            d = ry(root_row[2]) @ d
        p = d + np.array([root_row[0], 0.0, root_row[1]])
    return p + np.array([0.0, radius, 0.0])


def sheet_of(c: Clip) -> dict:
    """The move sheet of a solved clip: its events (seconds), root, ball legs (each track in its anchor's frame), exit rule and
    steer, with a content hash. Its frame is the move file's ground frame from under the hips at the start."""
    m = c.move; sh = m["sheet"]
    radius = float(m.get("ball", {}).get("radius", 0.11))
    ev = {k: round(v / FPS, 6) for k, v in c.events.items()}
    legs = []
    for leg in sh.get("leg", []):
        if leg["anchor"] not in ANCHORS:
            raise ValueError(f"{m['name']}: unknown leg anchor {leg['anchor']!r}")
        out = {"from": leg["from"], "to": leg["to"], "anchor": leg["anchor"]}
        for k in ("foot", "surface"):
            if k in leg:
                out[k] = leg[k]
        if leg["anchor"] in DRIVEN:
            out["track"] = _in_anchor(leg["anchor"], c.ball, c.root, c.events[leg["from"]], c.events[leg["to"]], radius)
        legs.append(out)
    at = c.events["contact"]
    sheet = {"name": m["name"], "move": m["move"], "entry": m.get("entry", "stand"),
             "entry_speed": float(sh.get("entry_speed", m.get("entry_speed", 0.0))),
             "side": m["side"], "foot": m["foot"], "events": ev,
             "root": [[round(float(r[0]), 4), round(float(r[1]), 4), round(float(r[2]), 2)] for r in c.root],
             "ball0": [round(float(c.ball[at, 0]), 4), round(float(c.ball[at, 1] - radius), 4), round(float(c.ball[at, 2]), 4)],
             "legs": legs, "exit": dict(sh.get("exit", {"kind": "touch"})), "steer_deg": float(sh.get("steer_deg", 0.0))}
    sheet["sha"] = sheet_sha(sheet)
    return sheet


def sheet_sha(sheet: dict) -> str:
    import hashlib
    body = {k: v for k, v in sheet.items() if k != "sha"}
    return hashlib.sha256(json.dumps(body, sort_keys=True, separators=(",", ":")).encode()).hexdigest()[:16]


def check_sheet(c: Clip, rig: Rig) -> None:
    """The sheet's checks. Leg joins always; a file with a [card] table (one authored to the clip standard) also has its root's
    start and acceleration, the leg's reach on the ball, its key density and every band of its card checked."""
    m = c.move; sh = c.sheet; p = c.problems
    radius = float(m.get("ball", {}).get("radius", 0.11))
    driven = [l for l in sh["legs"] if l["anchor"] in DRIVEN]
    for a, b in zip(driven, driven[1:]):
        if a["to"] != b["from"]:
            continue
        f = c.events[a["to"]]
        end = _from_anchor(a, len(a["track"]) - 1, c.ball[c.events[a["from"]]], c.root[f], radius)
        start = _from_anchor(b, 0, c.ball[f], c.root[f], radius)
        gap = float(np.linalg.norm(end - start))
        if gap > MAX_LEG_JOIN:
            p.append(f"the ball jumps {gap * 100:.1f} cm from the leg ending at {a['to']} to the next")
    if "card" not in m:
        return
    import clip_measures as cm
    rel = c.events["release"]
    entry = sh["entry_speed"]
    # the root at the start: on the run's line, running along it at the entry speed
    v0 = (c.root[1, :2] - c.root[0, :2]) * FPS
    if abs(v0[0]) > 0.1 or abs(v0[1] - entry) > max(0.15, 0.08 * entry):
        p.append(f"the root starts at {v0[1]:.2f} m/s along the run and {v0[0]:.2f} m/s across it; the entry speed is {entry:.2f}")
    # its acceleration up to the release (after it the run takes over)
    xz = c.root[: rel + 1, :2]
    acc = np.linalg.norm(np.diff(xz, 2, axis=0), axis=1) * FPS * FPS if rel >= 2 else np.zeros(0)
    plant = np.zeros(len(acc), bool)
    for a_u, b_u in m["sheet"].get("plant", []):
        fa, fb = c.tl.sec(float(a_u)) * FPS, c.tl.sec(float(b_u)) * FPS
        plant |= (np.arange(len(acc)) + 1 >= fa - 1e-6) & (np.arange(len(acc)) + 1 <= fb + 1e-6)
    # (plant_accel: a file's own limit inside its plant spans, for a move that must stop harder than a plant does: the drag-back)
    plant_limit = float(m["sheet"].get("plant_accel", ROOT_ACCEL_PLANT))
    over = acc - np.where(plant, plant_limit, ROOT_ACCEL)
    c.stats["root_accel_max"] = round(float(acc.max()), 1) if len(acc) else 0.0
    if len(over) and over.max() > 1e-6:
        i = int(over.argmax())
        p.append(f"the root accelerates at {acc[i]:.1f} m/s^2 at frame {i + 1} (limit {plant_limit if plant[i] else ROOT_ACCEL:.0f})")
    # a driven leg's foot is within reach of the ball
    for leg in driven:
        if "foot" not in leg:
            continue
        s = SIDES.index(leg["foot"][0])
        a, b = c.events[leg["from"]], c.events[leg["to"]]
        ext = float(c.leg_ext[a:b + 1, s].max())
        if ext > MAX_LEG_REACH + 1e-6:
            p.append(f"the {leg['foot']} leg is at {ext:.3f} of its length on the ball (limit {MAX_LEG_REACH}) at frame {a + int(c.leg_ext[a:b + 1, s].argmax())}")
    # key density between the first and the last contact
    last = "contact2" if "contact2" in c.events else "release"
    t0, t1 = c.tl.events["contact"], c.tl.events[last]
    times = sorted({round(c.tl.sec(float(k["t"])), 5) for k in m["key"]})
    inside = [t for t in times if t0 - 1e-6 <= t <= t1 + 1e-6]
    gaps = np.diff([t0] + inside + [t1]) if inside else np.array([t1 - t0])
    if len(gaps) and gaps.max() > MAX_KEY_GAP_S + 1e-6:
        p.append(f"no key for {gaps.max():.3f} s between the contact and the {last} (at most {MAX_KEY_GAP_S})")
    # the card's bands and the three beats
    gr, gt = fk_points(rig, c.local_q, c.hips)
    c.measures = cm.card_measures(c.events, c.ball, c.root, c.pel, gr, gt, rig.t, m["side"], m["foot"], radius, entry, FPS)
    bands = dict(m["card"])
    bands.setdefault("snap_deg", [SNAP_DEG, 1e9])
    bands.setdefault("drive_m", [DRIVE_M, 1e9])
    if "drop_m" not in bands:
        p.append("the card has no drop_m band (the sell beat)")
    for name, (lo, hi) in bands.items():
        if name not in c.measures:
            p.append(f"the card names an unknown measure {name!r}")
            continue
        v = c.measures[name]
        if not (lo - 1e-6 <= v <= hi + 1e-6):
            p.append(f"card {name} = {v:.3f}, outside {lo} to {hi}")


# ---------------------------------------------------------------------------------------------------------------- output

def write(c: Clip, out: Path, names: list[str]) -> dict:
    r = rt.Result(c.name, c.hips, c.local_q, c.contacts, c.events, False, c.problems, c.stats)
    rt.write_kclip(out / f"{c.name}.kclip", r, names)
    cs = {("left", "right")[s]: [list(x) for x in rt.spans(c.contacts[:, s])] for s in range(2)}
    m = c.move
    entry = {"name": c.name, "file": f"{c.name}.kclip", "source": "authored", "move_file": m["path"], "set": f"skill:{m['move']}",
             "tags": ["skill"], "match_bias": 1.0, "verdict": m.get("verdict", "keep"), "side": m["side"], "foot": m["foot"],
             "kind": m["move"], "loop": False, "events": c.events, "fps": FPS, "frame_count": c.stats["frames"],
             "seconds": c.stats["seconds"], "contacts": cs, "notes": m.get("notes", ""),
             "timing": {"spans": c.tl.spans, "event_seconds": {k: round(v, 4) for k, v in c.tl.events.items()}},
             "ball": {"radius": float(m.get("ball", {}).get("radius", 0.11)), "on": [list(x) for x in on_spans(m)], "touch": dict(m.get("ball", {}).get("touch", {})),
                      "track": [[round(float(x), 4) for x in b] for b in c.ball]},
             "finger_curl": [[round(float(x), 3) for x in cc] for cc in c.curl],
             "entry": m.get("entry", "stand"), "entry_speed": float(m.get("entry_speed", 0.0)),
             "sim": m.get("sim", {}),
             "checks": {k: v for k, v in c.stats.items() if k not in ("frames", "seconds")}}
    if c.sheet is not None:
        entry["sheet"] = {"name": c.sheet["name"], "entry": c.sheet["entry"], "sha": c.sheet_sha}
        entry["root"] = [[round(float(r[0]), 4), round(float(r[1]), 4), round(float(r[2]), 2)] for r in c.root]
    if m.get("ending"):
        entry["ending"] = m["ending"].lstrip("_")
    if "stance" in m:
        entry["stance"] = {f: [[c.tl.sec(float(a)), c.tl.sec(float(b))] for a, b in v] for f, v in m["stance"].items()}
    return entry


def build(moves: list[dict], rig: Rig) -> list[Clip]:
    """Every clip of every move file. A file with a [sheet] table has one move sheet (see sheets_of): from its authored side, its
    own ending and its first variant (the other side is mirrored when the simulation loads it); each clip of the file carries
    that sheet's hash."""
    clips = []
    for m0 in moves:
        mine = []
        for m in endings(m0):
            for side_m in (m, mirror_move(m)):
                for v in m.get("variant", []):
                    mine.append(solve(side_m, v, rig))
        if mine[0].sheet is not None:
            mine[0].sheet_owner = True
            for c in mine:
                c.sheet_sha = mine[0].sheet["sha"]
        clips += mine
    return clips


def sheets_of(clips: list[Clip]) -> list[dict]:
    """The move sheets of built clips, one per move file that has one."""
    return [c.sheet for c in clips if c.sheet_owner]


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description="Author skill-move clips from key poses (tools/anim/moves/*.toml).")
    ap.add_argument("--moves-dir", type=Path, default=MOVES)
    ap.add_argument("--model", type=Path, default=GLB)
    ap.add_argument("--out", type=Path, default=OUT)
    ap.add_argument("--sheets", type=Path, help="where the move sheets are written (default data/moves/sheets.json; beside --out when that is given)")
    args = ap.parse_args(argv)
    sheets_path = args.sheets or (SHEETS if args.out == OUT else args.out / "sheets.json")
    rig = load_rig(args.model)
    names = [nm.replace(":", "_") for nm in rig.t.names]
    args.out.mkdir(parents=True, exist_ok=True)
    moves = [load_move(p) for p in sorted(args.moves_dir.glob("*.toml"))]
    index, failed = [], []
    clips = build(moves, rig)
    sheets = sheets_of(clips)
    for c in clips:
        if c.problems:
            failed.append(c.name)
            print(f"REFUSED {c.name}: " + "; ".join(c.problems))
            continue
        index.append(write(c, args.out, names))
        s = c.stats
        print(f"wrote {c.name}: {s['frames']} frames ({s['seconds']} s), events {c.events}, lowest joint {s['lowest_joint_m']} m, "
              f"fastest bone {s['max_bone_deg_per_s']} deg/s ({s['max_turn_at']}), sole off ball {s['sole_off_ball_max_m'] * 100:.2f} cm")
    t = rig.t
    skeleton = {"parents": t.parent, "offsets": [[round(float(v), 6) for v in o] for o in t.rest_local_t],
                "rest": [[round(float(v), 6) for v in q] for q in rt.mats_to_quats(t.rest_local_r)]}
    if failed:
        print(f"{len(failed)} clip(s) refused: {', '.join(failed)}; clips.json left as it was")
        return 1
    (args.out / "clips.json").write_text(json.dumps({"fps": FPS, "bones": names, "skeleton": skeleton, "clips": index}, indent=1) + "\n")
    sheets_path.parent.mkdir(parents=True, exist_ok=True)
    sheets_path.write_text(json.dumps({"fps": FPS, "sheets": sheets}, separators=(",", ":")).replace('{"name"', '\n{"name"') + "\n")
    print(f"wrote {len(sheets)} move sheets to {sheets_path}")
    keep = {e["file"] for e in index}
    for old in args.out.glob("*.kclip"):
        if old.name not in keep:
            old.unlink()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
