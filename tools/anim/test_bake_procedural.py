"""Baking the procedural keeper: twist flips are removed without moving a joint, smoothing keeps unit rotations, and the committed
baked clips carry their events."""
import math

import numpy as np

import bake_procedural as B
import retarget as R


def axis_angle(axis, ang):
    return B.axis_angle(np.array(axis, dtype=float), ang)


def test_a_half_turn_twist_flip_is_not_followed_and_the_bone_keeps_its_direction():
    names = R.BONES
    k = names.index("LeftForeArm")
    n = 6
    glob = np.zeros((n, 22, 4)); glob[..., 3] = 1.0
    base = axis_angle([0, 0, 1], 0.6)            # the forearm tipped over
    for i in range(n):
        twist = math.pi if i >= 3 else 0.0       # the solve spins it half a turn about its own axis at frame 3
        glob[i, k] = R.qmul(base, axis_angle([0, 1, 0], twist + 0.02 * i))
    out = B.unflip_twists(glob, names)
    y = np.array([0, 1.0, 0])
    for i in range(n):
        assert np.allclose(B.qrot(out[i, k], y), B.qrot(glob[i, k], y), atol=1e-6)   # same direction: no joint moves
    for i in range(1, n):
        d = abs(float(np.dot(out[i, k], out[i - 1, k])))
        assert 2 * math.degrees(math.acos(min(1.0, d))) <= B.TWIST_STEP_DEG + 1e-3


def test_smoothing_keeps_unit_rotations_and_a_still_pose_still():
    n = 20
    q = np.zeros((n, 22, 4)); q[..., 3] = 1.0
    q[:, 5] = axis_angle([1, 0, 0], 0.3)
    q[10:, 5] *= -1                               # the same rotation, other sign
    hips = np.tile([0.0, 0.9, 0.0], (n, 1))
    sq, sh = B.smooth(q, hips)
    assert np.allclose(np.linalg.norm(sq, axis=2), 1.0)
    assert np.allclose(np.abs(np.sum(sq[:, 5] * q[0, 5], axis=1)), 1.0, atol=1e-6)
    assert np.allclose(sh, hips)


def test_the_committed_baked_clips_carry_take_off_contact_and_landing():
    for c in R.load_clips():
        if c.get("source") != "baked":
            continue
        k = R.read_kclip(R.OUT / f"{c['name']}.kclip")
        ev = k["events"]
        assert 0 < ev["takeoff"] < ev["contact"] <= ev["landing"] < len(k["hips"]), (c["name"], ev)
