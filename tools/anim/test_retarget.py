"""Retarget pipeline: keeper rest pose, quaternion encoding, .kclip round trip, and a real clip's checks."""
import math
from pathlib import Path

import numpy as np
import pytest

import retarget as R

CACHE = Path(__file__).resolve().parent / ".cache"
needs_cache = pytest.mark.skipif(not (CACHE / "77_03.amc").exists(), reason="run tools/anim/cmu.py first")


def random_quats(n, seed=1):
    rng = np.random.default_rng(seed)
    q = rng.normal(size=(n, 4))
    q /= np.linalg.norm(q, axis=1, keepdims=True)
    return q


def test_target_rest_pose():
    t = R.load_target()
    assert [n.split(":")[1] for n in t.names] == R.BONES
    assert t.parent[0] == -1 and all(p >= 0 for p in t.parent[1:])
    assert 0.95 < t.rest_glob_t[0, 1] < 1.1           # hips about 1 m up on a 1.80 m model
    assert -0.03 < t.sole_y <= 0.0                      # boots reach the ground
    for k in range(22):
        r = t.rest_glob_r[k]
        assert np.allclose(r @ r.T, np.eye(3), atol=1e-6)


def test_smallest_three_error_under_a_milliradian():
    q = np.concatenate([random_quats(5000), np.eye(4), -np.eye(4), [[0.5, 0.5, 0.5, 0.5]]])
    back = R.decode_quats(R.encode_quats(q))
    err = 2 * np.arccos(np.clip(np.abs(np.sum(q * back, axis=1)), 0, 1))
    assert err.max() < 0.001


def test_swing_maps_a_onto_b():
    rng = np.random.default_rng(4)
    for _ in range(50):
        a, b = rng.normal(size=3), rng.normal(size=3)
        m = R.swing(a, b)
        assert np.allclose(m @ (a / np.linalg.norm(a)), b / np.linalg.norm(b), atol=1e-9)
        assert np.allclose(m @ m.T, np.eye(3), atol=1e-9)
    assert np.allclose(R.swing(np.array([0, 1.0, 0]), np.array([0, -1.0, 0])) @ [0, 1, 0], [0, -1, 0])


def test_contact_labels_debounce():
    h = np.array([0.0] * 10 + [0.1, 0.0] + [0.0] * 5 + [0.1] * 10)
    v = np.zeros_like(h)
    c = R.label_contacts(h, v)
    assert c[:17].all()          # a one-frame lift is ignored
    assert not c[-5:].any()      # a lasting lift ends the contact


def test_guard_limits_a_flip():
    rest = np.array([0, 0, 0, 1.0])
    ang = np.radians([0, 5, 10, 190, 195, 15])   # a solver flip in the middle
    q = np.stack([np.array([0, 0, math.sin(a / 2), math.cos(a / 2)]) for a in ang])
    out, hit = R.guard_bone(q, rest, 95.0, 20.0)
    steps = np.degrees(2 * np.arccos(np.clip(np.abs(np.sum(out[1:] * out[:-1], axis=1)), 0, 1)))
    assert steps.max() <= 20.0 + 1e-6 and hit[3]


def test_kclip_round_trip(tmp_path):
    n = 12
    r = R.Result("t", np.arange(n * 3, dtype=float).reshape(n, 3) * 0.01, random_quats(n * 22).reshape(n, 22, 4),
                 np.stack([np.arange(n) % 2 == 0, np.arange(n) % 3 == 0], axis=1), {"contact": 5}, True, [], {})
    names = [f"mixamorig_{b}" for b in R.BONES]
    R.write_kclip(tmp_path / "t.kclip", r, names)
    back = R.read_kclip(tmp_path / "t.kclip")
    assert back["names"] == names and back["loop"] and back["fps"] == 60 and back["events"] == {"contact": 5}
    assert np.allclose(back["hips"], r.hips, atol=1e-6)
    assert (back["contacts"] == r.contacts).all()
    err = 2 * np.arccos(np.clip(np.abs(np.sum(back["q"] * r.local_q, axis=2)), 0, 1))
    assert err.max() < 0.001
    assert (tmp_path / "t.kclip").stat().st_size == 16 + sum(1 + len(s) for s in names) + n * 144 + 2 + 1 + 7 + 4 + n


@needs_cache
def test_ready_stance_retargets_cleanly():
    clip = next(c for c in R.load_clips() if c["name"] == "stance_ready_77_03")
    t = R.load_target()
    r = R.retarget(clip, t)
    assert r.problems == []
    assert r.stats["max_dir_err_deg"] < R.MAX_DIR_DEG
    L = np.stack([R.quats_to_mats(r.local_q[:, k]) for k in range(22)], axis=1)
    gr, gt = t.fk(L, r.hips)
    rest_h = R.foot_rest_heights(t)
    for s in range(2):
        h = (R.foot_points(t, gr, gt, s)[:, :, 1] - rest_h).min(axis=1)
        assert r.contacts[:, s].mean() > 0.5               # a stance stands on its feet
        assert np.abs(h[r.contacts[:, s]]).max() < 0.04    # contact feet are on the ground
    assert gt[:, :, 1].min() > 0.0                         # nothing below the ground
    # the clip faces +Z and starts over the origin
    assert abs(r.hips[0, 0]) < 0.1 and abs(r.hips[0, 2]) < 0.1
    assert 0.8 < r.hips[:, 1].mean() < 1.05


@needs_cache
def test_every_mapped_bone_follows_its_source_direction():
    """Pure check of the mapping on a jump (fast, airborne): the 3-degree rule holds on every frame."""
    clip = next(c for c in R.load_clips() if c["name"] == "dive_high_takeoff_124_11")
    r = R.retarget(clip, R.load_target())
    assert r.stats["max_dir_err_deg"] < R.MAX_DIR_DEG
    assert set(r.events) == {"takeoff", "contact", "landing"}
    assert r.events["takeoff"] < r.events["contact"] < r.events["landing"] < len(r.hips)


def test_every_clip_has_a_known_verdict():
    clips = R.load_clips()
    assert all(R.verdict(c) in R.VERDICTS for c in clips)
    rejected = {c["name"] for c in clips if R.verdict(c) == "reject"}
    assert rejected == {"body_block_144_07", "body_block_144_26", "back_leap_jump_81_12", "smother_roll_90_34"}
    with pytest.raises(ValueError):
        R.verdict({"name": "x", "verdict": "maybe"})


def test_guard_spreads_a_neck_head_roll_swap():
    """getup_back_140_08's neck and head swapped a roll in one frame; the guard turns them at most 16 deg per frame."""
    most, step = R.GUARD["Head"]
    rest = np.array([0, 0, 0, 1.0])
    ang = np.radians([30, 31, 32, 0, 1, 2, 3, 4])
    q = np.stack([np.array([0, 0, math.sin(a / 2), math.cos(a / 2)]) for a in ang])
    out, hit = R.guard_bone(q, rest, most, step)
    steps = np.degrees(2 * np.arccos(np.clip(np.abs(np.sum(out[1:] * out[:-1], axis=1)), 0, 1)))
    assert steps.max() <= step + 1e-6 and hit[3]
