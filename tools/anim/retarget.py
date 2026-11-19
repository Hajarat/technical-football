"""Retargets the CMU trials listed in tools/anim/clips.toml onto the keeper model and writes game/assets/anim/keeper/*.kclip + clips.json.

    python3 tools/anim/cmu.py                                  # fetch the listed trials (once)
    uv run --with numpy python3 tools/anim/retarget.py         # all clips
    uv run --with numpy python3 tools/anim/retarget.py dive_side_takeoff_141_05 ...   # some clips

Method (spec appendix D):

* The keeper's rest pose is read from game/assets/characters/keeper.glb (the glTF JSON chunk and its node TRS). Both skeletons use the
  same axes at rest (Y up, facing +Z, the character's left on +X), and a bone's direction in the glTF is its local +Y.
* 22 CMU bones are mapped onto the 22 bones SkinnedBody drives (MAP below). Both rests are brought to a common pose: walking down the
  keeper's hierarchy, each bone's rest is swung (shortest arc, after its parent's alignment) onto the CMU rest direction of its source
  bone, except where the two segments are defined differently (ALIGN: hips, clavicles, feet, toes). Each frame, a keeper bone's global
  rotation is then the source bone's global rotation times that aligned rest, so every mapped bone points the way its source bone
  does; local rotations follow from the parent chain.
* Proportions: the hips' rest is rolled by a constant (at most 8 deg) so both feet stand level where the subject stands still on both;
  a subject whose hip joints are much wider than the model's has both legs turned outward by a constant (over 3 deg only) so the knees
  do not cross; a hand, foot, upper arm, neck or head whose CMU solve flips is held within a range and a turning speed (GUARD).
* Root: the hip joints are centred where the subject's are, scaled by the ratio of the keeper's leg length to the subject's (hip to
  ankle along the bones) about the subject's standing ankle height; the clip is turned to face +Z (its first frame, or the mean over
  the clip when it starts off its feet) and moved to start over the origin. Per frame the hips are then lifted or lowered (smoothly)
  so the lowest foot in contact is on the ground and no joint is within 4 cm of it. Source frames are AMC frame numbers (1-based, as
  in the files) at 120 Hz; clips are kept at 60 Hz (every other frame).
* Foot contacts: the foot's lowest point under 4 cm and its speed under 0.35 m/s, debounced over 3 frames.
* Checks: every mapped bone's direction (after quantisation) within 3 degrees of its source bone (the constant corrections above
  excepted), and the foot in every contact span within 2 cm of the ground at its lowest (a clip may set contact_tolerance_cm). A clip that fails is not written; it is
  reported and the run exits non-zero.

.kclip layout (little-endian), version 1:
    char[4] "KCLP"; u16 version; u16 fps; u32 frames; u16 bones; u16 flags (bit 0 = loop)
    bones x (u8 length, utf-8 name)
    frames x (f32 x3 hips position in model space, metres, ground at 0, model at 1.80 m; bones x i16 x3 local rotation, smallest-three)
    u16 events; events x (u8 length, utf-8 name, i32 frame at 60 Hz); frames x u8 contact bits (bit 0 = left foot, bit 1 = right)
Smallest-three: the largest |component| of (x, y, z, w) is dropped (the quaternion is negated so it is positive), the other three in
order are stored as round(v / sqrt(1/2) * 16383) shifted left by one; the dropped index sits in the low bits of the first two shorts.
"""
from __future__ import annotations

import json
import math
import struct
import sys
import tomllib
from dataclasses import dataclass
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import asfamc  # noqa: E402

REPO = HERE.parent.parent
GLB = REPO / "game/assets/characters/keeper.glb"
OUT = REPO / "game/assets/anim/keeper"
CACHE = HERE / ".cache"

SRC_FPS = 120
FPS = 60
CONTACT_H = 0.04
CONTACT_V = 0.35
DEBOUNCE = 3
MAX_DIR_DEG = 3.0
MAX_CONTACT_H = 0.02
MAX_ROLL = math.radians(8)
MIN_SPLAY = math.radians(3)
# flip guard (guard_bone): most degrees from rest, most degrees per 60 Hz frame (20 = 1,200 deg/s, under the gallery's 1,500 pop flag)
GUARD = {"LeftHand": (95.0, 20.0), "RightHand": (95.0, 20.0), "LeftFoot": (85.0, 20.0), "RightFoot": (85.0, 20.0),
         "LeftArm": (179.0, 22.0), "RightArm": (179.0, 22.0),
         # neck and head: CMU's lowerneck/head solves can swap a roll between the two in one frame (getup_back_140_08 at 0.98 s);
         # 16 deg per frame (960 deg/s) is above every real head turn in the clips
         "Neck": (60.0, 16.0), "Head": (90.0, 16.0)}
JOINT_CLEAR = 0.04   # no bone joint closer to the ground than this (the body around a joint is at least that thick)

# SkinnedBody's driven order: trunk, then per side (Left, Right) shoulder, arm, forearm, hand, up-leg, leg, foot, toe.
TRUNK = ["Hips", "Spine", "Spine1", "Spine2", "Neck", "Head"]
LIMB = ["Shoulder", "Arm", "ForeArm", "Hand", "UpLeg", "Leg", "Foot", "ToeBase"]
BONES = TRUNK + [s + b for s in ("Left", "Right") for b in LIMB]
MAP = {"Hips": "root", "Spine": "lowerback", "Spine1": "upperback", "Spine2": "thorax", "Neck": "lowerneck", "Head": "head"}
for side, c in (("Left", "l"), ("Right", "r")):
    MAP.update({side + "Shoulder": c + "clavicle", side + "Arm": c + "humerus", side + "ForeArm": c + "radius", side + "Hand": c + "hand",
                side + "UpLeg": c + "femur", side + "Leg": c + "tibia", side + "Foot": c + "foot", side + "ToeBase": c + "foot"})
# How each keeper bone's rest is brought onto the source's rest:
#   dir   - swung onto the source bone's rest direction (the segments join the same two joints: spine, neck, head, arms, legs);
#   carry - keeps its parent's alignment (the clavicles: CMU's runs from the sternum up to the shoulder, the model's from beside the
#           neck down to it, so their directions are not comparable; both rests have the shoulders at ease);
#   world - kept as is (the hips);
#   flat  - the feet: the model's rest foot is flat on the ground, CMU's zero pose is not (its foot segment runs from a low ankle to
#           a marker on top of the ball of the foot, and a standing subject's foot sits some 20 deg off it). The tilt (pitch and roll,
#           not heading) of the source foot while it stands still on the ground in this clip is taken as "flat", so a flat CMU foot
#           gives a flat model foot. The toes follow the CMU foot: CMU toe rotations are marker noise (curled 40 deg up standing).
ALIGN = {b: "dir" for b in BONES}
ALIGN.update({"Hips": "world", "LeftShoulder": "carry", "RightShoulder": "carry",
              "LeftFoot": "flat", "RightFoot": "flat", "LeftToeBase": "flat", "RightToeBase": "flat"})
TOE_TIP = 0.08   # ToeBase has no child: its tip is taken this far along the bone (the boot reaches ~8.5 cm past the toe joint)


# ---------------------------------------------------------------------------------------------------------------- math

def quat_to_mat(q) -> np.ndarray:
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def mats_to_quats(m: np.ndarray) -> np.ndarray:
    """(n,3,3) rotations -> (n,4) quaternions (x, y, z, w), w >= 0."""
    n = m.shape[0]
    q = np.zeros((n, 4))
    tr = m[:, 0, 0] + m[:, 1, 1] + m[:, 2, 2]
    for i in range(n):
        a = m[i]
        if tr[i] > 0:
            s = math.sqrt(tr[i] + 1.0) * 2
            q[i] = [(a[2, 1] - a[1, 2]) / s, (a[0, 2] - a[2, 0]) / s, (a[1, 0] - a[0, 1]) / s, 0.25 * s]
        elif a[0, 0] > a[1, 1] and a[0, 0] > a[2, 2]:
            s = math.sqrt(1.0 + a[0, 0] - a[1, 1] - a[2, 2]) * 2
            q[i] = [0.25 * s, (a[0, 1] + a[1, 0]) / s, (a[0, 2] + a[2, 0]) / s, (a[2, 1] - a[1, 2]) / s]
        elif a[1, 1] > a[2, 2]:
            s = math.sqrt(1.0 + a[1, 1] - a[0, 0] - a[2, 2]) * 2
            q[i] = [(a[0, 1] + a[1, 0]) / s, 0.25 * s, (a[1, 2] + a[2, 1]) / s, (a[0, 2] - a[2, 0]) / s]
        else:
            s = math.sqrt(1.0 + a[2, 2] - a[0, 0] - a[1, 1]) * 2
            q[i] = [(a[0, 2] + a[2, 0]) / s, (a[1, 2] + a[2, 1]) / s, 0.25 * s, (a[1, 0] - a[0, 1]) / s]
    q /= np.linalg.norm(q, axis=1, keepdims=True)
    q[q[:, 3] < 0] *= -1
    return q


def quats_to_mats(q: np.ndarray) -> np.ndarray:
    x, y, z, w = q[:, 0], q[:, 1], q[:, 2], q[:, 3]
    m = np.empty((q.shape[0], 3, 3))
    m[:, 0, 0] = 1 - 2 * (y * y + z * z); m[:, 0, 1] = 2 * (x * y - z * w); m[:, 0, 2] = 2 * (x * z + y * w)
    m[:, 1, 0] = 2 * (x * y + z * w); m[:, 1, 1] = 1 - 2 * (x * x + z * z); m[:, 1, 2] = 2 * (y * z - x * w)
    m[:, 2, 0] = 2 * (x * z - y * w); m[:, 2, 1] = 2 * (y * z + x * w); m[:, 2, 2] = 1 - 2 * (x * x + y * y)
    return m


def swing(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    """Shortest-arc rotation taking direction a onto direction b."""
    a = a / np.linalg.norm(a); b = b / np.linalg.norm(b)
    v = np.cross(a, b); c = float(np.dot(a, b))
    if c < -0.999999:
        axis = np.cross(a, [1, 0, 0])
        if np.linalg.norm(axis) < 1e-6:
            axis = np.cross(a, [0, 1, 0])
        axis /= np.linalg.norm(axis)
        return 2 * np.outer(axis, axis) - np.eye(3)
    k = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
    return np.eye(3) + k + k @ k / (1 + c)


def yaw_mat(rad: float) -> np.ndarray:
    c, s = math.cos(rad), math.sin(rad)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


SQ = math.sqrt(0.5)
QMAX = 16383


def encode_quats(q: np.ndarray) -> np.ndarray:
    """(n,4) -> (n,3) int16, smallest-three (see the module docstring)."""
    q = q.copy()
    idx = np.argmax(np.abs(q), axis=1)
    neg = q[np.arange(len(q)), idx] < 0
    q[neg] *= -1
    out = np.zeros((len(q), 3), dtype=np.int16)
    for i in range(len(q)):
        rest = [q[i, k] for k in range(4) if k != idx[i]]
        v = [int(round(max(-1.0, min(1.0, r / SQ)) * QMAX)) for r in rest]
        s = [(v[0] << 1) | (idx[i] & 1), (v[1] << 1) | ((idx[i] >> 1) & 1), v[2] << 1]
        out[i] = s
    return out


def decode_quats(s: np.ndarray) -> np.ndarray:
    s = s.astype(np.int32)
    idx = (s[:, 0] & 1) | ((s[:, 1] & 1) << 1)
    vals = (s >> 1).astype(np.float64) / QMAX * SQ
    q = np.zeros((len(s), 4))
    for i in range(len(s)):
        rest = list(vals[i])
        big = math.sqrt(max(0.0, 1.0 - sum(r * r for r in rest)))
        rest.insert(int(idx[i]), big)
        q[i] = rest
    return q


# ---------------------------------------------------------------------------------------------------------------- target

@dataclass
class Target:
    names: list[str]                 # the 22 driven bones' glTF names (mixamorig:*), in BONES order
    parent: list[int]                # index into the 22, -1 for the hips
    rest_local_t: np.ndarray         # (22,3) local translation
    rest_local_r: np.ndarray         # (22,3,3)
    rest_glob_r: np.ndarray          # (22,3,3)
    rest_glob_t: np.ndarray          # (22,3)
    sole_y: float                    # lowest boot vertex at rest

    @property
    def leg(self) -> float:
        return float(self.rest_glob_t[0, 1] - self.sole_y)

    def fk(self, local_r: np.ndarray, hips: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        """local_r (n,22,3,3), hips (n,3) -> global rotations (n,22,3,3) and positions (n,22,3)."""
        n = local_r.shape[0]
        gr = np.empty_like(local_r); gt = np.empty((n, 22, 3))
        for k in range(22):
            p = self.parent[k]
            if p < 0:
                gr[:, k] = local_r[:, k]; gt[:, k] = hips
            else:
                gr[:, k] = gr[:, p] @ local_r[:, k]
                gt[:, k] = gt[:, p] + gr[:, p] @ self.rest_local_t[k]
        return gr, gt


def read_glb(path: Path = GLB) -> tuple[dict, bytes]:
    b = path.read_bytes()
    if b[:4] != b"glTF":
        raise ValueError(f"{path}: not a binary glTF")
    off, js, binchunk = 12, None, b""
    while off < len(b):
        ln, kind = struct.unpack_from("<I4s", b, off)
        chunk = b[off + 8: off + 8 + ln]
        if kind == b"JSON":
            js = json.loads(chunk)
        elif kind == b"BIN\x00":
            binchunk = chunk
        off += 8 + ln
    return js, binchunk


def load_target(path: Path = GLB) -> Target:
    j, _ = read_glb(path)
    nodes = j["nodes"]
    by_short = {}
    for i, n in enumerate(nodes):
        nm = n.get("name", "")
        cut = max(nm.rfind(":"), nm.rfind("_"))
        by_short[nm[cut + 1:]] = i
    parent_of = {}
    for i, n in enumerate(nodes):
        for c in n.get("children", []):
            parent_of[c] = i
    idx = []
    for b in BONES:
        if b not in by_short:
            raise ValueError(f"{path}: no bone {b}")
        idx.append(by_short[b])
    lt = np.zeros((22, 3)); lr = np.zeros((22, 3, 3)); par = []
    for k, i in enumerate(idx):
        n = nodes[i]
        lt[k] = n.get("translation", [0, 0, 0])
        lr[k] = quat_to_mat(n.get("rotation", [0, 0, 0, 1]))
        if any(abs(s - 1) > 1e-4 for s in n.get("scale", [1, 1, 1])):
            raise ValueError(f"bone {n['name']} is scaled")
        p = parent_of.get(i)
        par.append(idx.index(p) if p in idx else -1)
        if par[-1] < 0 and k != 0:
            raise ValueError(f"bone {n['name']}: parent is not driven")
    # the hips' ancestors (the rig node) must have no transform: skeleton space is the glTF scene space
    p = parent_of.get(idx[0])
    while p is not None:
        n = nodes[p]
        if any(k in n for k in ("translation", "rotation", "scale", "matrix")):
            raise ValueError(f"node {n.get('name')} above the hips has a transform")
        p = parent_of.get(p)
    t = Target([nodes[i]["name"] for i in idx], par, lt, lr, np.zeros((22, 3, 3)), np.zeros((22, 3)), 0.0)
    gr, gt = t.fk(lr[None], lt[0][None])
    t.rest_glob_r = gr[0]; t.rest_glob_t = gt[0]
    # the boots' lowest vertex: the sole at rest
    sole = 0.0
    for m in j["meshes"]:
        for prim in m["primitives"]:
            mat = j["materials"][prim["material"]]["name"] if "material" in prim else ""
            if mat == "boots":
                sole = min(sole, j["accessors"][prim["attributes"]["POSITION"]]["min"][1])
    t.sole_y = sole
    return t


def foot_points(t: Target, gr: np.ndarray, gt: np.ndarray, side: int) -> np.ndarray:
    """(n,3,3): ankle, toe joint and toe tip of a foot (0 = left)."""
    f = BONES.index(("Left", "Right")[side] + "Foot"); toe = f + 1
    tip = gt[:, toe] + gr[:, toe][:, :, 1] * TOE_TIP
    return np.stack([gt[:, f], gt[:, toe], tip], axis=1)


def foot_rest_heights(t: Target) -> np.ndarray:
    gr, gt = t.rest_glob_r[None], t.rest_glob_t[None]
    return foot_points(t, gr, gt, 0)[0, :, 1] - t.sole_y


# ---------------------------------------------------------------------------------------------------------------- retarget

@dataclass
class Result:
    name: str
    hips: np.ndarray        # (n,3)
    local_q: np.ndarray     # (n,22,4)
    contacts: np.ndarray    # (n,2) bool
    events: dict[str, int]  # 60 Hz frames
    loop: bool
    problems: list[str]
    stats: dict


def align_rests(t: Target, sk: asfamc.Skeleton, flat: list[np.ndarray] | None = None) -> np.ndarray:
    """(22,3,3) world-space alignment P per bone: P @ rest_glob_r is the keeper's bone in the source's rest direction."""
    P = np.zeros((22, 3, 3))
    for k, b in enumerate(BONES):
        p = t.parent[k]
        if ALIGN[b] in ("world", "flat"):
            P[k] = np.eye(3) if flat is None or ALIGN[b] == "world" else flat[b.startswith("Right")]
            continue
        if ALIGN[b] == "carry":
            P[k] = P[p]
            continue
        d_t = P[p] @ t.rest_glob_r[k][:, 1]
        d_s = sk.bones[MAP[b]].direction
        P[k] = swing(d_t, d_s) @ P[p]
    return P


def source_leg(sk: asfamc.Skeleton) -> float:
    """Hip joint to ankle along the bones (metres), mean of both legs."""
    return float(np.mean([(sk.bones[c + "femur"].length + sk.bones[c + "tibia"].length) * sk.unit_m for c in "lr"]))


def target_leg(t: Target) -> float:
    k = BONES.index("LeftUpLeg")
    return float(np.linalg.norm(t.rest_local_t[k + 1]) + np.linalg.norm(t.rest_local_t[k + 2]))


def orthonormal(m: np.ndarray) -> np.ndarray:
    u, _, vt = np.linalg.svd(m)
    r = u @ vt
    if np.linalg.det(r) < 0:
        u[:, -1] *= -1; r = u @ vt
    return r


def flat_feet(rot: dict, st: dict) -> tuple[list[np.ndarray], list[float], list[np.ndarray]]:
    """Per foot, while it stands still on the ground (ankle within 2 cm of its lowest, slow): the inverse of the source foot's mean
    tilt (heading removed), the ankle's median height, and those frames (under 3: identity, lowest height)."""
    out, heights, counts = [], [], []
    for c in "lr":
        a = st[c + "foot"]
        v = speed(a)
        sel = (a[:, 1] < a[:, 1].min() + 0.025) & (v < CONTACT_V)
        counts.append(sel)
        heights.append(float(np.median(a[sel, 1])) if sel.sum() >= 3 else float(a[:, 1].min()))
        if sel.sum() < 3:
            out.append(np.eye(3)); continue
        g = orthonormal(rot[c + "foot"][sel].mean(axis=0))
        f = g[:, 2]
        tilt = yaw_mat(-math.atan2(f[0], f[2])) @ g
        out.append(tilt.T)
    return out, heights, counts


def heading(root: np.ndarray) -> float:
    """Yaw of the pelvis: where its front points; lying (front up or down), where its up axis (toward the head) points."""
    f = root[:, 2]
    if math.hypot(f[0], f[2]) < 0.3:
        f = root[:, 1]
    return math.atan2(f[0], f[2])


def qmul(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    ax, ay, az, aw = a[..., 0], a[..., 1], a[..., 2], a[..., 3]
    bx, by, bz, bw = b[..., 0], b[..., 1], b[..., 2], b[..., 3]
    return np.stack([aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
                     aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz], axis=-1)


def qconj(a: np.ndarray) -> np.ndarray:
    return a * np.array([-1, -1, -1, 1])


def qslerp(a: np.ndarray, b: np.ndarray, u: float) -> np.ndarray:
    if np.dot(a, b) < 0:
        b = -b
    d = min(1.0, float(np.dot(a, b)))
    th = math.acos(d)
    if th < 1e-6:
        return b
    return (math.sin((1 - u) * th) * a + math.sin(u * th) * b) / math.sin(th)


def guard_bone(q: np.ndarray, rest: np.ndarray, most_deg: float, step_deg: float) -> tuple[np.ndarray, np.ndarray]:
    """CMU's hand and foot solves flip now and then (a hand turning 178 deg between two frames, a lying subject's foot 180 deg
    from rest): a local rotation is kept within most_deg of the bone's rest and turns at most step_deg per frame. Returns the
    rotations and the frames that were changed."""
    out = q.copy()
    hit = np.zeros(len(q), dtype=bool)
    lim = math.radians(most_deg)
    for i in range(len(q)):
        d = qmul(qconj(rest), q[i])
        if d[3] < 0:
            d = -d
        ang = 2 * math.acos(min(1.0, d[3]))
        if ang > lim:
            axis = d[:3] / max(1e-9, np.linalg.norm(d[:3]))
            d = np.concatenate([axis * math.sin(lim / 2), [math.cos(lim / 2)]])
            out[i] = qmul(rest, d); hit[i] = True
        if i > 0:
            prev = out[i - 1]
            dd = abs(float(np.dot(prev, out[i])))
            step = 2 * math.acos(min(1.0, dd))
            if step > math.radians(step_deg):
                out[i] = qslerp(prev, out[i], math.radians(step_deg) / step); hit[i] = True
        out[i] /= np.linalg.norm(out[i])
        if out[i][3] < 0:
            out[i] = -out[i]
    return out, hit


def rot_z(rad: float) -> np.ndarray:
    c, s_ = math.cos(rad), math.sin(rad)
    return np.array([[c, -s_, 0], [s_, c, 0], [0, 0, 1]])


def locals_from(t: Target, G: np.ndarray) -> np.ndarray:
    L = np.zeros_like(G)
    for k in range(22):
        p = t.parent[k]
        L[:, k] = G[:, k] if p < 0 else np.transpose(G[:, p], (0, 2, 1)) @ G[:, k]
    return L


def source_contacts(st: dict, flat_h: list[float]) -> np.ndarray:
    """(n,2) contact labels on the subject's feet: ankle or ball of the foot within CONTACT_H of where it sits standing, and slow."""
    out = []
    for i, c in enumerate("lr"):
        ank, ball = st[c + "foot"], st[c + "toes"]
        hb = ball[:, 1] - (np.median(ball[:, 1][ank[:, 1] < flat_h[i] + 0.01]) if (ank[:, 1] < flat_h[i] + 0.01).any() else ball[:, 1].min())
        h = np.minimum(ank[:, 1] - flat_h[i], hb)
        out.append(label_contacts(h, speed((ank + ball) * 0.5)))
    return np.stack(out, axis=1)


def ground_curve(h: np.ndarray, contact: np.ndarray, floor: np.ndarray, smooth: int = 9) -> np.ndarray:
    """Per-frame vertical correction putting the lowest contact foot (h: keeper foot heights above the ground) on the ground,
    interpolated between contacts, never below floor (the lift that keeps every point above the ground), smoothed."""
    n = len(h)
    any_c = contact.any(axis=1)
    if not any_c.any():
        corr = floor.copy()
    else:
        corr = np.full(n, np.nan)
        for i in np.nonzero(any_c)[0]:
            corr[i] = -float(h[i][contact[i]].min())
        known = np.nonzero(~np.isnan(corr))[0]
        corr = np.interp(np.arange(n), known, corr[known])
    corr = np.maximum(corr, floor)
    k = np.ones(smooth) / smooth
    pad = np.pad(corr, smooth // 2, mode="edge")
    return np.convolve(pad, k, mode="valid")


def speed(p: np.ndarray, half: int = 2) -> np.ndarray:
    """Speed (m/s) at 60 Hz from a central difference over +-half frames: the joint positions of a planted CMU foot jitter by a
    centimetre from frame to frame, which a one-frame difference reads as 0.6 m/s."""
    n = len(p)
    a = np.clip(np.arange(n) - half, 0, n - 1); b = np.clip(np.arange(n) + half, 0, n - 1)
    return np.linalg.norm(p[b] - p[a], axis=1) / np.maximum(b - a, 1) * FPS


def label_contacts(h: np.ndarray, v: np.ndarray) -> np.ndarray:
    raw = (h < CONTACT_H) & (v < CONTACT_V)
    out = raw.copy()
    state = bool(raw[0])
    run = 0
    for i in range(len(raw)):
        if raw[i] != state:
            run += 1
            if run >= DEBOUNCE:
                state = bool(raw[i])
                out[i - DEBOUNCE + 1: i + 1] = state
                run = 0
                continue
        else:
            run = 0
        out[i] = state
    return out


def spans(mask: np.ndarray) -> list[tuple[int, int]]:
    out, start = [], None
    for i, m in enumerate(mask):
        if m and start is None:
            start = i
        if not m and start is not None:
            out.append((start, i - 1)); start = None
    if start is not None:
        out.append((start, len(mask) - 1))
    return out


def retarget(clip: dict, t: Target, cache: Path = CACHE) -> Result:
    sk, motion = asfamc.load_trial(cache, clip["trial"])
    src_fps = int(clip.get("fps", SRC_FPS))
    step = max(1, src_fps // FPS)
    f0, f1 = clip.get("frames", [motion.first_frame, motion.first_frame + motion.count - 1])
    f1 = min(f1, motion.first_frame + motion.count - 1)
    idx = np.arange(f0 - motion.first_frame, f1 - motion.first_frame + 1, step)
    rot, st, en = sk.global_transforms(motion, idx)
    n = len(idx)

    # facing: turn the clip so its first frame faces +Z, and move it to start over the origin
    # facing: an upright first frame sets it; a clip that starts off its feet (a fall, lying) takes the mean over the clip, since the
    # pelvis of a body in the air or on the ground says little about which way it is going
    if rot["root"][0][1, 1] > 0.8:
        yaw = -heading(rot["root"][0])
    else:
        hs = np.array([heading(r_) for r_ in rot["root"]])
        yaw = -math.atan2(np.sin(hs).mean(), np.cos(hs).mean())
    Y = yaw_mat(yaw)
    scale = target_leg(t) / source_leg(sk)
    flat, flat_h, flat_sel = flat_feet(rot, st)
    flat_n = [int(x.sum()) for x in flat_sel]
    ankle_h = foot_rest_heights(t)[0]
    P = align_rests(t, sk, flat)
    G = np.zeros((n, 22, 3, 3))
    for k, b in enumerate(BONES):
        G[:, k] = Y @ rot[MAP[b]] @ (P[k] @ t.rest_glob_r[k])
    # The hip joints are centred where the subject's are, scaled about the subject's standing ankle height and lifted to the keeper's,
    # so a foot flat on the subject's floor lands near the keeper's floor whatever the two skeletons' proportions.
    floor = float(np.mean(flat_h))
    hipmid_s = ((st["lfemur"] + st["rfemur"]) * 0.5) @ Y.T
    start = hipmid_s[0].copy()
    hipmid = (hipmid_s - np.array([start[0], floor, start[2]])) * scale + np.array([0, ankle_h, 0])
    ku, kr = BONES.index("LeftUpLeg"), BONES.index("RightUpLeg")
    rest_mid = (t.rest_glob_t[ku] + t.rest_glob_t[kr]) * 0.5 - t.rest_glob_t[0]

    def hips_for(G_):
        return hipmid - np.einsum("nij,j->ni", G_[:, 0] @ t.rest_glob_r[0].T, rest_mid)

    # Pelvis roll: the subject stands level on legs that differ in length (by up to 2 cm) with his pelvis rolled to suit; the keeper's
    # legs are equal and his hips wider, so the copied roll would leave one foot a few cm above the other. The hips' rest is rolled
    # (a constant, like the feet's) until the two feet are level where the subject stands still on both.
    upright = (Y @ rot["root"])[:, 1, 1] > 0.9
    both = flat_sel[0] & flat_sel[1] & upright
    roll = 0.0
    if both.sum() >= 3:
        w = float(np.linalg.norm(t.rest_glob_t[ku] - t.rest_glob_t[kr]))
        for _ in range(3):
            G[:, 0] = Y @ rot["root"] @ (rot_z(roll) @ t.rest_glob_r[0])
            _, gt_ = t.fk(locals_from(t, G), hips_for(G))
            ankles = gt_[:, [BONES.index("LeftFoot"), BONES.index("RightFoot")], 1]
            diff = float(np.mean(ankles[both, 1] - ankles[both, 0]))
            roll = max(-MAX_ROLL, min(MAX_ROLL, roll + math.asin(max(-0.5, min(0.5, diff / w)))))
    P[0] = rot_z(roll)
    G[:, 0] = Y @ rot["root"] @ (P[0] @ t.rest_glob_r[0])
    # Hip width: thighs copied from a subject with wider hip joints than the model's point the knees inward far enough to cross on
    # the model (subject 141's hip joints are 43 cm apart scaled, the model's 22 cm). Each leg (thigh and shin, not the foot) is
    # turned outward about the pelvis' forward axis by the constant angle that puts the ankles as far apart as the subject's,
    # when that angle is over MIN_SPLAY.
    w_t = float(np.linalg.norm(t.rest_glob_t[ku] - t.rest_glob_t[kr]))
    _, rest_end = sk.rest_points()
    w_s = float(np.linalg.norm(rest_end["lhipjoint"] - rest_end["rhipjoint"])) * scale
    splay = math.asin(max(-0.3, min(0.3, (w_s - w_t) / 2 / target_leg(t))))
    if abs(splay) < MIN_SPLAY:   # most subjects' hips are within a few cm of the model's: leave their legs as they are
        splay = 0.0
    pel = G[:, 0] @ t.rest_glob_r[0].T
    splay_rot = {}
    for side, sign in (("Left", 1.0), ("Right", -1.0)):
        S = pel @ rot_z(sign * splay) @ np.transpose(pel, (0, 2, 1))
        splay_rot[side] = S
        for b in ("UpLeg", "Leg"):
            k = BONES.index(side + b)
            G[:, k] = S @ G[:, k]
    hips = hips_for(G)
    L = locals_from(t, G)
    q = np.stack([mats_to_quats(L[:, k]) for k in range(22)], axis=1)
    rest_q = mats_to_quats(t.rest_local_r)
    guarded = np.zeros((n, 22), dtype=bool)
    for b, (most, step_) in GUARD.items():
        k = BONES.index(b)
        q[:, k], hit = guard_bone(q[:, k], rest_q[k], most, step_)
        guarded[:, k] |= hit
        for kc in range(22):   # a guarded bone moves its children too
            if t.parent[kc] == k:
                guarded[:, kc] |= hit
    # quantise now so every check sees what the game will
    qd = np.stack([decode_quats(encode_quats(q[:, k])) for k in range(22)], axis=1)
    Lq = np.stack([quats_to_mats(qd[:, k]) for k in range(22)], axis=1)

    # contacts and grounding
    rest_h = foot_rest_heights(t)
    def feet(hips_):
        gr, gt = t.fk(Lq, hips_)
        out_h, out_v = np.zeros((n, 2)), np.zeros((n, 2))
        for s in range(2):
            pts = foot_points(t, gr, gt, s)
            hh = pts[:, :, 1] - rest_h   # each point's height above where it sits when standing on the ground
            out_h[:, s] = hh.min(axis=1)
            mid = pts.mean(axis=1)
            out_v[:, s] = speed(mid)
        return out_h, out_v, gr, gt
    # Ground: the keeper's foot is longer than the subject's and his ankle higher, so the same rotations leave a planted foot a few
    # cm above or below the floor (more so on a raised heel). Contacts are labelled on the subject's own feet (against his floor),
    # and the hips are lifted or lowered per frame so the keeper's lowest planted foot is on the ground; between contacts the
    # correction is interpolated, then smoothed over 0.15 s so it never pops.
    sc = source_contacts(st, flat_h)
    base = hips[:, 1].copy()
    h, v, gr, gt = feet(hips)
    for _ in range(3):
        # contacts: the subject's, plus any the keeper's own feet show; nothing may go through the floor
        c = sc | np.stack([label_contacts(h[:, s], v[:, s]) for s in range(2)], axis=1)
        lowest = np.minimum(h.min(axis=1), (gt[:, :, 1] - JOINT_CLEAR).min(axis=1))
        hips[:, 1] += ground_curve(h, c, -lowest)
        h, v, gr, gt = feet(hips)
    ground_lift = hips[:, 1] - base
    c = np.stack([label_contacts(h[:, s], v[:, s]) for s in range(2)], axis=1)

    problems = []
    tol = float(clip.get("contact_tolerance_cm", MAX_CONTACT_H * 100)) / 100   # per-clip override (clips.toml)
    # direction check: each mapped bone's direction against its source bone
    worst, worst_at = 0.0, ""
    for k, b in enumerate(BONES):
        if ALIGN[b] == "dir":
            d_t = gr[:, k][:, :, 1]
            d_s = (Y @ rot[MAP[b]]) @ sk.bones[MAP[b]].direction
            if b.endswith("Leg"):   # thigh and shin: against the hip-width turn
                d_s = np.einsum("nij,nj->ni", splay_rot["Left" if b.startswith("Left") else "Right"], d_s)
            cosang = np.clip(np.sum(d_t * d_s, axis=1) / np.linalg.norm(d_s, axis=1), -1, 1)
            ang = np.degrees(np.arccos(cosang))
        else:
            # the bone's turn from its aligned rest against the source bone's turn from its rest
            d = np.transpose(Y @ rot[MAP[b]], (0, 2, 1)) @ gr[:, k] @ (P[k] @ t.rest_glob_r[k]).T
            ang = np.degrees(np.arccos(np.clip((np.trace(d, axis1=1, axis2=2) - 1) / 2, -1, 1)))
        ang = np.where(guarded[:, k], 0.0, ang)
        a = float(ang.max())
        if a > worst:
            worst, worst_at = a, f"{b}@{int(ang.argmax())}"
    if worst > MAX_DIR_DEG:
        problems.append(f"bone direction off by {worst:.2f} deg ({worst_at})")
    for s in range(2):
        for a, b in spans(c[:, s]):
            lo = float(h[a:b + 1, s].min())
            if abs(lo) > tol:
                problems.append(f"{'LR'[s]} foot contact {a}-{b} is {lo * 100:.1f} cm off the ground")

    events = {}
    for name, fr in clip.get("events", {}).items():
        if not f0 <= fr <= f1:
            problems.append(f"event {name}={fr} outside frames {f0}-{f1}")
        events[name] = int(round((fr - f0) / step))
    stats = {"frames": n, "seconds": round(n / FPS, 3), "scale": round(scale, 4), "max_dir_err_deg": round(worst, 3),
             "lowest_joint_m": round(float(gt[:, :, 1].min()), 3),
             "guarded_frames": {b: int(guarded[:, BONES.index(b)].sum()) for b in GUARD if guarded[:, BONES.index(b)].any()}, "flat_foot_frames": flat_n, "hips_roll_deg": round(math.degrees(roll), 2), "leg_splay_deg": round(math.degrees(splay), 2),
             "ground_lift_cm": [round(float(ground_lift.min()) * 100, 1), round(float(ground_lift.max()) * 100, 1)], "lowest_joint_at": int(gt[:, :, 1].min(axis=1).argmin())}
    if clip.get("cut"):
        hips, qd, c, events = apply_cuts(clip, hips, qd, c, events, f0, step, problems)
        stats["frames"] = len(hips); stats["seconds"] = round(len(hips) / FPS, 3)
    return Result(clip["name"], hips, qd, c, events, bool(clip.get("loop", False)), problems, stats)


CUT_BLEND = 6   # frames (0.1 s) over which the motion after a cut catches up from the frame before it


def apply_cuts(clip: dict, hips: np.ndarray, q: np.ndarray, c: np.ndarray, events: dict, f0: int, step: int, problems: list):
    """Drops the source frame ranges in clips.toml "cut" (a pause in the middle of a clip, [first, last] inclusive, 120 Hz AMC
    frames). After each cut the hips go on from where they were (horizontally; their height and every rotation catch up from the
    frame before the cut over CUT_BLEND frames, slerped), contacts and later events close up. An event inside a cut is refused."""
    keep = np.ones(len(hips), dtype=bool)
    seams = []
    for a, b in clip["cut"]:
        ia, ib = -((f0 - a) // step), (b - f0) // step + 1   # the first kept 60 Hz frame at or after a, past the last at or before b
        if not (0 < ia < ib < len(hips)):
            problems.append(f"cut {a}-{b} outside the clip")
            continue
        for name, fr in events.items():
            if ia <= fr < ib:
                problems.append(f"event {name} inside cut {a}-{b}")
        keep[ia:ib] = False
        seams.append((ia, ib))
    hips = hips.copy(); q = q.copy()
    for ia, ib in seams:
        d = hips[ia - 1] - hips[ib]
        hips[ib:, 0] += d[0]; hips[ib:, 2] += d[2]
        for k in range(CUT_BLEND):
            if ib + k >= len(hips):
                break
            u = (k + 1) / (CUT_BLEND + 1)
            hips[ib + k, 1] = hips[ia - 1, 1] + (hips[ib + k, 1] - hips[ia - 1, 1]) * u
            for b in range(q.shape[1]):
                q[ib + k, b] = qslerp(q[ia - 1, b], q[ib + k, b], u)
    old = np.flatnonzero(keep)
    remap = {int(o): i for i, o in enumerate(old)}
    new_events = {}
    for name, fr in events.items():
        nxt = old[old >= fr]
        new_events[name] = remap[int(nxt[0])] if len(nxt) else len(old) - 1
    return hips[keep], q[keep], c[keep], new_events


def write_kclip(path: Path, r: Result, names: list[str]) -> None:
    n = len(r.hips)
    buf = bytearray()
    buf += b"KCLP" + struct.pack("<HHIHH", 1, FPS, n, len(names), 1 if r.loop else 0)
    for nm in names:
        e = nm.encode()
        buf += struct.pack("<B", len(e)) + e
    enc = np.stack([encode_quats(r.local_q[:, k]) for k in range(len(names))], axis=1)   # (n,22,3)
    for i in range(n):
        buf += struct.pack("<3f", *r.hips[i])
        buf += enc[i].astype("<i2").tobytes()
    buf += struct.pack("<H", len(r.events))
    for nm, fr in r.events.items():
        e = nm.encode()
        buf += struct.pack("<B", len(e)) + e + struct.pack("<i", fr)
    bits = (r.contacts[:, 0].astype(np.uint8) | (r.contacts[:, 1].astype(np.uint8) << 1))
    buf += bits.tobytes()
    path.write_bytes(bytes(buf))


def read_kclip(path: Path) -> dict:
    b = path.read_bytes()
    assert b[:4] == b"KCLP"
    ver, fps, n, nb, flags = struct.unpack_from("<HHIHH", b, 4)
    off = 16
    names = []
    for _ in range(nb):
        ln = b[off]; names.append(b[off + 1: off + 1 + ln].decode()); off += 1 + ln
    hips = np.zeros((n, 3)); enc = np.zeros((n, nb, 3), dtype=np.int16)
    for i in range(n):
        hips[i] = struct.unpack_from("<3f", b, off); off += 12
        enc[i] = np.frombuffer(b, dtype="<i2", count=nb * 3, offset=off).reshape(nb, 3); off += nb * 6
    (ne,) = struct.unpack_from("<H", b, off); off += 2
    events = {}
    for _ in range(ne):
        ln = b[off]; nm = b[off + 1: off + 1 + ln].decode(); off += 1 + ln
        (events[nm],) = struct.unpack_from("<i", b, off); off += 4
    bits = np.frombuffer(b, dtype=np.uint8, count=n, offset=off)
    q = np.stack([decode_quats(enc[:, k]) for k in range(nb)], axis=1)
    return {"version": ver, "fps": fps, "names": names, "loop": bool(flags & 1), "hips": hips, "q": q, "events": events,
            "contacts": np.stack([(bits & 1) > 0, (bits & 2) > 0], axis=1)}


def load_clips(path: Path = HERE / "clips.toml") -> list[dict]:
    with open(path, "rb") as f:
        return tomllib.load(f)["clip"]


VERDICTS = ("keep", "trim", "reject")


def verdict(clip: dict) -> str:
    """The audition verdict (clips.toml "verdict"): keep, trim (kept after its frames were narrowed) or reject (written, never matched).
    A clip without one has not been auditioned yet and counts as keep."""
    v = clip.get("verdict", "keep")
    if v not in VERDICTS:
        raise ValueError(f"{clip['name']}: verdict must be one of {', '.join(VERDICTS)}, got {v!r}")
    return v


def main(argv: list[str]) -> int:
    clips = load_clips()
    only = set(argv)
    t = load_target()
    names = [nm.replace(":", "_") for nm in t.names]   # Godot imports mixamorig:X as mixamorig_X
    OUT.mkdir(parents=True, exist_ok=True)
    index, failed = [], []
    for c in clips:
        if only and c["name"] not in only:
            continue
        if c.get("source") == "baked":
            # recorded from the procedural keeper (tools/anim/bake_procedural.py): listed as it is, never retargeted
            path = OUT / f"{c['name']}.kclip"
            if not path.exists():
                failed.append(c["name"])
                print(f"MISSING {c['name']}: run tools/anim/bake_procedural.py first")
                continue
            k = read_kclip(path)
            nf = len(k["hips"])
            cs = {("left", "right")[s]: spans(k["contacts"][:, s]) for s in range(2)}
            index.append({"name": c["name"], "file": path.name, "source": "baked", "script": c.get("script", ""), "set": c["set"],
                          "tags": list(c.get("tags", [])), "match_bias": float(c.get("match_bias", 1.0)), "verdict": verdict(c),
                          "side": c.get("side", "none"), "kind": c.get("kind", ""), "loop": k["loop"], "events": k["events"], "fps": k["fps"],
                          "frame_count": nf, "seconds": round(nf / k["fps"], 3), "contacts": {kk: [list(x) for x in v] for kk, v in cs.items()},
                          "notes": c.get("notes", "")})
            print(f"listed {c['name']} (baked): {nf} frames, events {k['events']}")
            continue
        r = retarget(c, t)
        if r.problems:
            failed.append(c["name"])
            print(f"REFUSED {c['name']}: " + "; ".join(r.problems))
            continue
        write_kclip(OUT / f"{c['name']}.kclip", r, names)
        cs = {("left", "right")[s]: spans(r.contacts[:, s]) for s in range(2)}
        index.append({"name": c["name"], "file": f"{c['name']}.kclip", "trial": c["trial"], "frames": c.get("frames"),
                      "set": c["set"], "cut": c.get("cut", []), "tags": list(c.get("tags", [])), "match_bias": float(c.get("match_bias", 1.0)), "verdict": verdict(c), "side": c.get("side", "none"), "kind": c.get("kind", ""), "loop": r.loop,
                      "events": r.events, "fps": FPS, "frame_count": r.stats["frames"], "seconds": r.stats["seconds"],
                      "contacts": {k: [list(s) for s in v] for k, v in cs.items()},
                      "notes": c.get("notes", ""), "contact_tolerance_cm": float(c.get("contact_tolerance_cm", MAX_CONTACT_H * 100)), "checks": {k: v for k, v in r.stats.items() if k not in ("frames", "seconds")}})
        print(f"wrote {c['name']}: {r.stats['frames']} frames, scale {r.stats['scale']}, dir err {r.stats['max_dir_err_deg']} deg, "
              f"lowest joint {r.stats['lowest_joint_m']} m, contacts L{len(cs['left'])} R{len(cs['right'])}")
    if not only:
        # the skeleton the clips were made for, so the game's pose database can run forward kinematics without Godot
        skeleton = {"parents": t.parent, "offsets": [[round(float(v), 6) for v in o] for o in t.rest_local_t],
                    "rest": [[round(float(v), 6) for v in q] for q in mats_to_quats(t.rest_local_r)]}
        (OUT / "clips.json").write_text(json.dumps({"fps": FPS, "bones": names, "skeleton": skeleton, "clips": index}, indent=1) + "\n")
        keep = {e["file"] for e in index}
        for old in OUT.glob("*.kclip"):
            if old.name not in keep:
                old.unlink()
    if failed:
        print(f"{len(failed)} clip(s) refused: {', '.join(failed)}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
