"""Skill-move authoring: the IK, the spline, the mirror, the checks, and the ball roll file."""
import copy
import math

import numpy as np
import pytest

import author as A
import retarget as R


@pytest.fixture(scope="module")
def rig():
    return A.load_rig()


def still(rig, **over):
    """A one-key move standing at rest (feet where they stand at rest), with overrides for that key."""
    key = {"t": 0, "foot_l": {"pos": list(rig.sole[0])}, "foot_r": {"pos": list(rig.sole[1])}}
    key.update(over)
    return {"name": "still", "move": "still", "side": "left", "foot": "right", "path": "x", "events": {"start": 0, "end": 1},
            "variant": [{"spans": [0.2]}], "key": [key]}


def angle_deg(a, b):
    return math.degrees(2 * math.acos(min(1.0, abs(float(np.dot(a, b))))))


def test_rest_pose_solves_to_the_models_rest(rig):
    c = A.solve(still(rig), {"spans": [0.2]}, rig)
    rest = R.mats_to_quats(rig.t.rest_local_r)
    assert max(angle_deg(c.local_q[0, b], rest[b]) for b in range(22)) < 0.05
    assert np.allclose(c.hips[0], rig.rest_t[0], atol=1e-6)
    assert not c.problems


def test_two_bone_reaches_bends_toward_pole_and_reports_overreach():
    rng = np.random.default_rng(3)
    for _ in range(100):
        root, pole = rng.normal(size=3), rng.normal(size=3)
        d = rng.normal(size=3); d /= np.linalg.norm(d)
        target = root + d * rng.uniform(0.2, 0.85)
        mid, over, axis = A.two_bone(root, target, 0.45, 0.45, pole)
        assert over == 0
        assert abs(np.linalg.norm(mid - root) - 0.45) < 1e-9 and abs(np.linalg.norm(target - mid) - 0.45) < 1e-9
        w = pole - np.dot(pole, d) * d
        assert np.dot(mid - (root + d * np.dot(mid - root, d)), w) > 0     # the middle joint goes the pole's way
        assert abs(np.dot(axis, d)) < 1e-9
    mid, over, _ = A.two_bone(np.zeros(3), np.array([0, -1.0, 0]), 0.45, 0.45, np.array([0, 0, 1.0]))
    assert over == pytest.approx(0.1)
    assert np.allclose(mid, [0, -0.45, 0], atol=1e-4)


def test_spline_passes_keys_eases_to_rest_and_never_overshoots():
    keys = [A.Key(0.0, np.array([0.0]), 1.0), A.Key(0.1, np.array([1.0]), 0.0), A.Key(0.2, np.array([1.0]), 0.0),
            A.Key(0.5, np.array([3.0]), 1.0)]
    for k in keys:
        assert A.interp(keys, k.t)[0] == pytest.approx(k.v[0])
    ts = np.linspace(0, 0.5, 501)
    vs = np.array([A.interp(keys, t)[0] for t in ts])
    assert vs.min() >= -1e-12 and vs.max() <= 3 + 1e-12
    assert np.all(vs[(ts > 0.1) & (ts < 0.2)] <= 1 + 1e-12)        # a flat stretch between equal keys stays flat
    assert np.all(np.diff(vs) >= -1e-12)                             # monotone data, monotone curve
    # ease 1 at both ends of a span: smoothstep (zero slope at the keys)
    two = [A.Key(0.0, np.array([0.0]), 1.0), A.Key(1.0, np.array([1.0]), 1.0)]
    for u in (0.1, 0.25, 0.5, 0.8):
        assert A.interp(two, u)[0] == pytest.approx(u * u * (3 - 2 * u))
    # ease 0 passes through a key without stopping
    mid = [A.Key(0.0, np.array([0.0]), 1.0), A.Key(0.5, np.array([0.5]), 0.0), A.Key(1.0, np.array([1.0]), 1.0)]
    assert A.interp(mid, 0.51)[0] - A.interp(mid, 0.49)[0] > 0.01


def test_framed_key_follows_its_frame():
    # a key in the ball frame is resolved against the ball as it is at the time asked
    keys = [A.Key(0.0, ("ball", np.array([0, 0.11, 0.0])), 0.0)]
    resolve = lambda fv, t: np.array([t, 0.11, 0]) + fv[1]
    assert np.allclose(A.interp(keys, 0.3, resolve), [0.3, 0.22, 0])


def test_channel_first_keyed_later_starts_from_neutral(rig):
    m = still(rig)
    m["key"].append({"t": 1.0, "pelvis": {"pitch": 20}})
    c = A.solve(m, {"spans": [0.2]}, rig)
    rest = R.mats_to_quats(rig.t.rest_local_r)
    assert angle_deg(c.local_q[0, 0], rest[0]) < 0.05          # pitch 0 at the start, not 20
    assert angle_deg(c.local_q[-1, 0], rest[0]) == pytest.approx(20, abs=0.1)


def test_timeline_maps_spans():
    m = {"events": {"start": 0, "contact": 0.2, "release": 0.7, "end": 1.0}}
    tl = A.timeline(m, {"spans": [0.1, 0.3, 0.25]})
    assert tl.events == pytest.approx({"start": 0, "contact": 0.1, "release": 0.4, "end": 0.65})
    assert tl.sec(0.45) == pytest.approx(0.25)


def test_mirror_reflects_the_solved_pose(rig):
    m = A.load_move(A.MOVES / "ball_roll.toml")
    v = m["variant"][0]
    a, b = A.solve(m, v, rig), A.solve(A.mirror_move(m), v, rig)
    assert A.mirror_move(A.mirror_move(m))["key"] == m["key"]
    assert b.name == "ball_roll_right" and b.move["foot"] == "left"
    _, ga = A.fk_points(rig, a.local_q, a.hips)
    _, gb = A.fk_points(rig, b.local_q, b.hips)
    swap = [A.BONES.index(n.replace("Left", "@").replace("Right", "Left").replace("@", "Right")) for n in A.BONES]
    reflected = gb[:, swap] * np.array([-1, 1, 1])
    # the model's rest is not quite symmetric (hips 2 mm off centre, the clavicle joints 1 cm apart in height, so the head and the
    # clavicles' own joints differ); every limb joint mirrors within 2 mm
    limbs = [k for k, n in enumerate(A.BONES) if n not in ("Hips", "Head", "LeftShoulder", "RightShoulder")]
    assert np.abs(reflected - ga)[:, limbs].max() < 0.002
    assert np.allclose(b.ball * [-1, 1, 1], a.ball)
    assert a.events == b.events


def test_checks_refuse_a_foot_under_the_ground(rig):
    m = still(rig, pelvis={"pos": [0, 0.92, 0]}, foot_l={"pos": [0.2, -0.05, 0.14]})
    c = A.solve(m, {"spans": [0.2]}, rig)
    assert any("into the ground" in p for p in c.problems)


def test_checks_refuse_a_stretched_leg(rig):
    m = still(rig, foot_l={"pos": [0.6, 0.0, 0.6]})
    c = A.solve(m, {"spans": [0.2]}, rig)
    assert any("stretched" in p for p in c.problems)


def test_checks_refuse_a_bone_turning_too_fast(rig):
    m = still(rig)
    m["key"].append({"t": 0.1, "chest": {"yaw": 90}})
    m["key"].append({"t": 0.2, "chest": {"yaw": -90}})
    c = A.solve(m, {"spans": [0.2]}, rig)
    assert any("deg/s" in p for p in c.problems)


def test_checks_refuse_a_sole_off_the_ball(rig):
    m = still(rig, ball={"pos": [-0.1, 0.11, 0.35]}, foot_r={"pos": [-0.1, 0.24, 0.35]})  # 2 cm above the ball
    m["ball"] = {"radius": 0.11, "on": ["start", "end"]}
    c = A.solve(m, {"spans": [0.2]}, rig)
    assert any("off the ball" in p for p in c.problems)
    m["key"][0]["foot_r"] = {"pos": [0, 0.11, 0], "frame": "ball"}
    assert not A.solve(m, {"spans": [0.2]}, rig).problems


def test_checks_refuse_a_boot_through_the_ball(rig):
    # the right boot standing where the ball is
    m = still(rig, ball={"pos": [list(rig.sole[1])[0], 0.11, list(rig.sole[1])[2]]})
    m["ball"] = {"radius": 0.11}
    c = A.solve(m, {"spans": [0.2]}, rig)
    assert any("inside the ball" in p for p in c.problems)


def test_per_axis_ease():
    keys = [A.Key(0.0, np.array([0.0, 0.0]), np.array([1.0, 0.0])), A.Key(1.0, np.array([1.0, 1.0]), np.array([1.0, 0.0])),
            A.Key(2.0, np.array([1.0, 2.0]), np.array([1.0, 0.0]))]
    v = A.interp(keys, 0.25)
    assert v[0] == pytest.approx(0.25 * 0.25 * (3 - 0.5)) and v[1] == pytest.approx(0.25)


def test_ball_roll_passes_its_checks_and_keeps_the_sole_on_the_ball(rig, tmp_path):
    m = A.load_move(A.MOVES / "ball_roll.toml")
    run = A.load_move(A.MOVES / "ball_roll_run.toml")
    clips = A.build([m, run], rig)
    assert [c.name for c in clips] == ["ball_roll_left", "ball_roll_right", "ball_roll_left_run", "ball_roll_right_run"]
    assert clips[0].events == clips[2].events                       # the runtime blends standing and running by entry speed
    for c in clips:
        assert not c.problems, (c.name, c.problems)
        assert list(c.events) == ["start", "contact", "release", "end"]
        assert c.stats["sole_off_ball_max_m"] <= A.MAX_SOLE_OFF
        # the ball moves only while the sole is on it, the simulation's 0.6 m from standing
        assert np.allclose(c.ball[: c.events["contact"] + 1, 0], c.ball[0, 0])
        lateral = abs(c.ball[c.events["release"], 0] - c.ball[0, 0])
        assert lateral == pytest.approx(0.75 if c.name.endswith("_run") else 0.6, abs=1e-3)   # events fall on the nearest 60 Hz frame
    # the running ball rolls on along the run at 1.5 m/s throughout
    z = clips[2].ball[:, 2]
    assert np.allclose(np.diff(z) * A.FPS, 1.5, atol=1e-6)
    real = clips[0]
    assert real.tl.events["contact"] == pytest.approx(0.12) and real.tl.events["release"] - real.tl.events["contact"] == pytest.approx(0.4)
    # the written clip reads back as solved
    names = [n.replace(":", "_") for n in rig.t.names]
    entry = A.write(real, tmp_path, names)
    k = R.read_kclip(tmp_path / entry["file"])
    assert k["events"] == real.events and np.allclose(k["hips"], real.hips, atol=1e-6)
    assert entry["set"] == "skill:ball_roll" and entry["side"] == "left" and len(entry["ball"]["track"]) == len(real.hips)


def test_every_move_file_passes_and_pairs_its_entries(rig):
    moves = [A.load_move(p) for p in sorted(A.MOVES.glob("*.toml"))]
    for c in A.build(moves, rig):
        assert not c.problems, (c.name, c.problems)
        ev = list(c.events)
        # start, the first contact, any further contacts (contact2, ...), the last contact's end, end
        assert ev[:2] == ["start", "contact"] and ev[-2:] == ["release", "end"], c.name
    # a move's standing and running files share their event layout, so the runtime can blend them by entry speed
    by = {}
    for m in moves:
        by.setdefault(m["move"], []).append(m)
    for name, ms in by.items():
        assert len({tuple(m["events"].items()) for m in ms}) == 1, name
