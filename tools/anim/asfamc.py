"""Acclaim ASF (skeleton) and AMC (motion) reader for the CMU motion capture library.

Conventions (as the CMU files use them):

* Lengths are in ASF units; ``:units length 0.45`` means one unit is 1/0.45 inch, so metres = value / 0.45 * 0.0254.
  :func:`Skeleton.unit_m` gives the factor; :func:`Skeleton.global_transforms` returns metres.
* Y is up. At rest the subject faces +Z with its left on +X (the same axes as the glTF character models).
* Angles are degrees. A bone's ``axis`` (a, b, c, ORDER) is its local frame C relative to the world at rest; its AMC values are
  rotations about that frame's axes, applied in the order the ``dof`` line lists them (the first listed is applied first, so for
  ``rx ry rz`` the matrix is Rz @ Ry @ Rx). The root's ``order`` line works the same way for its translation and rotation.
* A bone's global rotation is ``parent_global @ C @ M @ C^-1`` (identity at rest), and its end point is its start (the parent's end)
  plus ``length * global_rotation @ direction``.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

INCH_M = 0.0254


def rot_axis(axis: str, deg: float) -> np.ndarray:
    a = np.deg2rad(deg)
    c, s = np.cos(a), np.sin(a)
    if axis == "x":
        return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])
    if axis == "y":
        return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])
    if axis == "z":
        return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])
    raise ValueError(axis)


def euler(order: str, values) -> np.ndarray:
    """Rotation for angles (degrees) about the axes in ``order`` (e.g. 'xyz'), the first applied first."""
    m = np.eye(3)
    for ax, v in zip(order, values):
        m = rot_axis(ax, v) @ m
    return m


def euler_batch(order: str, values: np.ndarray) -> np.ndarray:
    """(n, k) angles in degrees -> (n, 3, 3) rotations, vectorised :func:`euler`."""
    n = values.shape[0]
    m = np.tile(np.eye(3), (n, 1, 1))
    for i, ax in enumerate(order):
        a = np.deg2rad(values[:, i])
        c, s = np.cos(a), np.sin(a)
        r = np.zeros((n, 3, 3))
        if ax == "x":
            r[:, 0, 0] = 1; r[:, 1, 1] = c; r[:, 1, 2] = -s; r[:, 2, 1] = s; r[:, 2, 2] = c
        elif ax == "y":
            r[:, 1, 1] = 1; r[:, 0, 0] = c; r[:, 0, 2] = s; r[:, 2, 0] = -s; r[:, 2, 2] = c
        else:
            r[:, 2, 2] = 1; r[:, 0, 0] = c; r[:, 0, 1] = -s; r[:, 1, 0] = s; r[:, 1, 1] = c
        m = r @ m
    return m


@dataclass
class Bone:
    name: str
    direction: np.ndarray = field(default_factory=lambda: np.zeros(3))
    length: float = 0.0          # ASF units
    axis: np.ndarray = field(default_factory=lambda: np.eye(3))   # C
    dof: list[str] = field(default_factory=list)                   # e.g. ['rx', 'ry', 'rz']
    parent: str | None = None
    children: list[str] = field(default_factory=list)


@dataclass
class Skeleton:
    name: str
    length_unit: float           # the ':units length' value (0.45 for CMU)
    root_order: list[str]        # e.g. ['tx', 'ty', 'tz', 'rx', 'ry', 'rz']
    root_axis: np.ndarray        # C of the root
    root_position: np.ndarray
    bones: dict[str, Bone]       # includes 'root'
    order: list[str]             # parents before children, 'root' first

    @property
    def unit_m(self) -> float:
        """Metres per ASF length unit."""
        return INCH_M / self.length_unit

    def global_transforms(self, motion: "Motion", frames=None) -> tuple[dict[str, np.ndarray], dict[str, np.ndarray], dict[str, np.ndarray]]:
        """Per bone: global rotation (n,3,3), start point (n,3) and end point (n,3) in metres, for the motion's frames (or a subset)."""
        idx = np.arange(motion.count) if frames is None else np.asarray(frames)
        n = len(idx)
        um = self.unit_m
        rot: dict[str, np.ndarray] = {}
        start: dict[str, np.ndarray] = {}
        end: dict[str, np.ndarray] = {}
        # root
        rv = motion.channels["root"][idx]
        t = np.zeros((n, 3)); r = np.zeros((n, 3)); rorder = ""
        for k, ch in enumerate(self.root_order):
            if ch[0] == "t":
                t[:, "xyz".index(ch[1])] = rv[:, k]
            else:
                rorder += ch[1]
        rvals = np.stack([rv[:, k] for k, ch in enumerate(self.root_order) if ch[0] == "r"], axis=1)
        C = self.root_axis
        rot["root"] = C @ euler_batch(rorder, rvals) @ C.T
        start["root"] = (t + self.root_position) * um
        end["root"] = start["root"]
        for name in self.order[1:]:
            b = self.bones[name]
            p = b.parent
            if b.dof:
                vals = motion.channels.get(name)
                if vals is None:
                    m = np.tile(np.eye(3), (n, 1, 1))
                else:
                    m = euler_batch("".join(d[1] for d in b.dof), vals[idx])
                local = b.axis @ m @ b.axis.T
                g = rot[p] @ local
            else:
                g = rot[p].copy()
            rot[name] = g
            start[name] = end[p]
            end[name] = end[p] + (g @ (b.direction * b.length * um))
        return rot, start, end

    def rest_points(self) -> tuple[dict[str, np.ndarray], dict[str, np.ndarray]]:
        """Rest-pose start and end point (metres) of every bone, root at the origin."""
        start: dict[str, np.ndarray] = {"root": np.zeros(3)}
        end: dict[str, np.ndarray] = {"root": np.zeros(3)}
        for name in self.order[1:]:
            b = self.bones[name]
            start[name] = end[b.parent]
            end[name] = start[name] + b.direction * b.length * self.unit_m
        return start, end


@dataclass
class Motion:
    channels: dict[str, np.ndarray]   # bone -> (frames, values)
    count: int
    first_frame: int                  # the AMC's own number of its first frame (1 for CMU)


def parse_asf(path: str | Path) -> Skeleton:
    lines = Path(path).read_text(errors="replace").splitlines()
    i = 0
    name = ""
    unit = 1.0
    root_order: list[str] = []
    root_axis = np.eye(3)
    root_pos = np.zeros(3)
    bones: dict[str, Bone] = {"root": Bone("root")}
    hierarchy: list[tuple[str, list[str]]] = []
    section = ""
    cur: Bone | None = None
    in_limits = False
    while i < len(lines):
        raw = lines[i]
        i += 1
        s = raw.strip()
        if not s or s.startswith("#"):
            continue
        if s.startswith(":"):
            parts = s.split()
            section = parts[0][1:]
            if section == "name" and len(parts) > 1:
                name = parts[1]
            continue
        tok = s.split()
        if section == "units":
            if tok[0] == "length":
                unit = float(tok[1])
            elif tok[0] == "angle" and tok[1].lower() != "deg":
                raise ValueError("only degrees are supported")
        elif section == "root":
            if tok[0] == "order":
                root_order = [t.lower() for t in tok[1:]]
            elif tok[0] == "axis":
                axis_order = tok[1].lower()
                root_axis = np.eye(3)
                if axis_order != "xyz":
                    raise ValueError("root axis must be XYZ")
            elif tok[0] == "position":
                root_pos = np.array([float(x) for x in tok[1:4]])
        elif section == "bonedata":
            if tok[0] == "begin":
                cur = Bone("")
                in_limits = False
            elif tok[0] == "end":
                assert cur is not None
                bones[cur.name] = cur
                cur = None
            elif cur is not None:
                if tok[0] == "name":
                    cur.name = tok[1]
                elif tok[0] == "direction":
                    cur.direction = np.array([float(x) for x in tok[1:4]])
                elif tok[0] == "length":
                    cur.length = float(tok[1])
                elif tok[0] == "axis":
                    cur.axis = euler(tok[4].lower(), [float(x) for x in tok[1:4]])
                elif tok[0] == "dof":
                    cur.dof = [t.lower() for t in tok[1:]]
                elif tok[0] == "limits":
                    in_limits = True
        elif section == "hierarchy":
            if tok[0] in ("begin", "end"):
                continue
            hierarchy.append((tok[0], tok[1:]))
    for parent, kids in hierarchy:
        for k in kids:
            bones[k].parent = parent
            bones[parent].children.append(k)
    order: list[str] = []
    stack = ["root"]
    while stack:
        b = stack.pop(0)
        order.append(b)
        stack.extend(bones[b].children)
    return Skeleton(name, unit, root_order, root_axis, root_pos, bones, order)


def parse_amc(path: str | Path, skeleton: Skeleton | None = None) -> Motion:
    rows: dict[str, list[list[float]]] = {}
    count = 0
    first = None
    with open(path, errors="replace") as f:
        for raw in f:
            s = raw.strip()
            if not s or s.startswith("#") or s.startswith(":"):
                continue
            tok = s.split()
            if len(tok) == 1 and tok[0].isdigit():
                count += 1
                if first is None:
                    first = int(tok[0])
                continue
            vals = [float(x) for x in tok[1:]]
            lst = rows.setdefault(tok[0], [])
            # a bone missing in an earlier frame is padded with the first value it gets
            while len(lst) < count - 1:
                lst.append(vals)
            lst.append(vals)
    channels = {k: np.array(v[:count]) for k, v in rows.items()}
    if skeleton is not None:
        for k, v in channels.items():
            want = len(skeleton.root_order) if k == "root" else len(skeleton.bones[k].dof)
            if v.shape[1] != want:
                raise ValueError(f"{path}: bone {k} has {v.shape[1]} values, the skeleton says {want}")
    return Motion(channels, count, first or 1)


def load_trial(cache: Path, trial: str) -> tuple[Skeleton, Motion]:
    s, t = trial.split("_")
    s = str(int(s)); t = f"{int(t):02d}"
    sk = parse_asf(cache / f"{s}.asf")
    return sk, parse_amc(cache / f"{s}_{t}.amc", sk)
