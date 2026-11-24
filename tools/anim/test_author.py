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


def test_the_bone_speed_limit_is_1500_unless_a_skill_move_file_raises_it_to_at_most_2500(rig, monkeypatch):
    p = []
    assert A.turn_limit({}, p) == 1500 and not p
    assert A.turn_limit({"sheet": {}, "max_bone_deg_s": 2500}, p) == 2500 and not p
    assert A.turn_limit({"max_bone_deg_s": 2500}, p) == 1500 and "skill-move files" in p[-1]   # a shot file, say
    assert A.turn_limit({"sheet": {}, "max_bone_deg_s": 3000}, p) == 1500 and "outside 0 to 2500" in p[-1]
    # and the check uses it: a head turning about 1,800 deg/s is refused at the default ...
    m = still(rig)
    m["key"].append({"t": 0.5, "head": {"yaw": 120}})
    c = A.solve(m, {"spans": [0.1]}, rig)
    assert 1500 < c.stats["max_bone_deg_per_s"] < 2500
    assert any("limit 1500" in q for q in c.problems)
    # ... and passes a file whose limit is 2500 (the sheet itself is not what this test is about)
    m["max_bone_deg_s"] = 2500
    m["sheet"] = {}
    monkeypatch.setattr(A, "sheet_of", lambda clip: None)
    monkeypatch.setattr(A, "check_sheet", lambda clip, rig: None)
    c = A.solve(m, {"spans": [0.1]}, rig)
    assert not any("deg/s" in q for q in c.problems)


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
    assert [c.name for c in clips] == ["ball_roll_left", "ball_roll_right", "ball_roll_left_run", "ball_roll_right_run",
                                       "ball_roll_left_run_swap", "ball_roll_right_run_swap"]
    assert clips[0].events == clips[2].events                       # the runtime blends standing and running by entry speed
    for c in clips:
        assert not c.problems, (c.name, c.problems)
        assert list(c.events) == ["start", "contact", "release", "end"]
        assert c.stats["sole_off_ball_max_m"] <= A.MAX_SOLE_OFF
        lateral = abs(c.ball[c.events["release"], 0] - c.ball[c.events["contact"], 0])
        if "_run" in c.name:
            # at a run the sole takes it across the body against his root (the simulation's roll less his step with it)
            assert 0.7 <= lateral <= 0.85
        else:
            # from standing the ball moves only while the sole is on it, the simulation's 0.6 m
            assert np.allclose(c.ball[: c.events["contact"] + 1, 0], c.ball[0, 0])
            assert lateral == pytest.approx(0.6, abs=1e-3)   # events fall on the nearest 60 Hz frame
    # the running ball rolls on along the run throughout, ahead of the hips at the release
    z = clips[2].ball[:, 2]
    assert np.all(np.diff(z) > 0)
    assert z[clips[2].events["release"]] > clips[2].hips[clips[2].events["release"], 2]
    real = clips[0]
    assert real.tl.events["contact"] == pytest.approx(0.12) and real.tl.events["release"] - real.tl.events["contact"] == pytest.approx(0.4)
    # the written clip reads back as solved
    names = [n.replace(":", "_") for n in rig.t.names]
    entry = A.write(real, tmp_path, names)
    k = R.read_kclip(tmp_path / entry["file"])
    assert k["events"] == real.events and np.allclose(k["hips"], real.hips, atol=1e-6)
    assert entry["set"] == "skill:ball_roll" and entry["side"] == "left" and len(entry["ball"]["track"]) == len(real.hips)


def test_declared_stances_are_the_contacts_and_an_ending_shares_the_keys_before_it(rig):
    run = A.load_move(A.MOVES / "ball_roll_run.toml")
    own, swap = [c for c in A.build([run], rig) if c.name in ("ball_roll_left_run", "ball_roll_left_run_swap")]
    for c in (own, swap):
        for s, foot in enumerate(("left", "right")):
            want = np.zeros(len(c.hips), bool)
            for a, b in c.move["stance"][foot]:
                want[math.ceil(c.tl.sec(a) * A.FPS - 1e-6): math.floor(c.tl.sec(b) * A.FPS + 1e-6) + 1] = True
            assert np.array_equal(c.contacts[:, s], want), (c.name, foot)
    # the swap ending carries the move's own body up to its from: the same hips and the same stances (a channel's last span
    # before it bends toward the ending's next key)
    cut = int(A.timeline(run, run["variant"][0]).sec(run["ending"][0]["from"]) * A.FPS)
    assert np.allclose(own.hips[:cut - 4], swap.hips[:cut - 4], atol=0.005)
    assert np.array_equal(own.contacts[:cut + 1, 0], swap.contacts[:cut + 1, 0])
    mirrored = A.mirror_move(A.endings(run)[1])
    assert mirrored["stance"]["right"] == run["ending"][0]["stance"]["left"] and mirrored["ending"] == "_swap"


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


# ---------------------------------------------------------------------------------------------------------------- move sheets

def test_low_pass_keeps_a_straight_run_and_its_end_pace():
    t = np.arange(40)[:, None] / A.FPS
    line = np.concatenate([0.0 * t, 4.6 * t], axis=1)
    out = A.low_pass(line, A.ROOT_SIGMA_S * A.FPS)
    assert np.allclose(out, line, atol=1e-9)
    # a quick sway on top of the run (6 Hz, 3 cm) is mostly taken out of the root, and the root still comes in at the run's pace
    sway = line + np.concatenate([0.03 * np.sin(2 * np.pi * 6.0 * t), 0.0 * t], axis=1)
    root = A.sheet_root(np.concatenate([sway[:, :1], 0.9 + 0 * t, sway[:, 1:]], axis=1), np.zeros(len(t)))
    assert np.abs(root[5:-5, 0] - root[5:-5, 0].mean()).max() < 0.01
    assert (root[1, 1] - root[0, 1]) * A.FPS == pytest.approx(4.6, abs=0.01)
    assert np.allclose(root[0], 0)


def test_every_move_file_has_a_sheet_whose_hash_its_clips_carry(rig, tmp_path):
    moves = [A.load_move(p) for p in sorted(A.MOVES.glob("*.toml"))]
    clips = A.build(moves, rig)
    sheets = A.sheets_of(clips)
    assert len(sheets) == len(moves)
    names = [n.replace(":", "_") for n in rig.t.names]
    by = {(s["name"], s["entry"]): s for s in sheets}
    for c in clips:
        s = by[(c.move["name"], c.move.get("entry", "stand"))]
        assert c.sheet_sha == s["sha"] == A.sheet_sha(s)
        entry = A.write(c, tmp_path, names)
        assert entry["sheet"] == {"name": s["name"], "entry": s["entry"], "sha": s["sha"]}
        assert len(entry["root"]) == len(c.hips) and entry["root"][0] == [0.0, 0.0, entry["root"][0][2]]
    for s in sheets:
        ev = s["events"]
        assert list(ev)[0] == "start" and list(ev)[-1] == "end" and "contact" in ev and "release" in ev
        assert len(s["root"]) == round(ev["end"] * A.FPS) + 1
        for leg in s["legs"]:
            frames = round((ev[leg["to"]] - ev[leg["from"]]) * A.FPS) + 1
            assert len(leg.get("track", [])) == (frames if leg["anchor"] in A.DRIVEN else 0)
            # a pitch leg is measured from where the ball is as it starts
            if leg["anchor"] == "pitch":
                assert leg["track"][0][0] == 0 and leg["track"][0][2] == 0
    # the hash is the content's
    changed = dict(sheets[0], steer_deg=sheets[0]["steer_deg"] + 1)
    assert A.sheet_sha(changed) != sheets[0]["sha"]


def test_the_mirrored_clips_root_is_the_mirror(rig):
    m = A.load_move(A.MOVES / "elastico_run.toml")
    left, right = A.build([m], rig)[:2]
    assert np.allclose(left.root[:, 0], -right.root[:, 0], atol=1e-9) and np.allclose(left.root[:, 1], right.root[:, 1], atol=1e-9)
    assert np.allclose(left.root[:, 2], -right.root[:, 2])
    # the sheet is the authored side's; the mirror's feet are swapped
    assert left.sheet_owner and not right.sheet_owner
    assert [l.get("foot") for l in left.sheet["legs"]] == ["right", "right"] and [l.get("foot") for l in right.sheet["legs"]] == ["left", "left"]


def test_a_card_band_that_is_missed_refuses_the_clip(rig):
    m = A.load_move(A.MOVES / "elastico.toml")
    m["card"] = dict(m.get("card", {}), lift_m=[0.5, 0.6])
    c = A.solve(m, m["variant"][0], rig)
    assert any("card lift_m" in p for p in c.problems), c.problems
    m["card"] = {"drop_m": [0.0, 1.0], "snap_deg": [0, 999], "drive_m": [0, 9], "no_such": [0, 1]}
    c = A.solve(m, m["variant"][0], rig)
    assert any("unknown measure 'no_such'" in p for p in c.problems), c.problems


def test_a_root_that_brakes_too_hard_is_refused_outside_a_plant_span(rig):
    def move(plant):
        key = lambda t, z: {"t": t, "pelvis": {"pos": [0, float(rig.rest_mid[1]) - 0.06 * (t == 0.5), z]},
                            "foot_l": {"pos": [float(rig.sole[0][0]), 0.3, z]}, "foot_r": {"pos": [float(rig.sole[1][0]), 0.3, z]}}
        # 0.6 s: he comes in at 3 m/s and is stopped 0.2 s later (15 m/s^2)
        zs = [(0.0, 0.0), (1 / 3, 0.3), (0.5, 0.3), (1.0, 0.3)]
        keys = [key(t, z) for t, z in zs]
        keys[0]["ease"] = 0
        return {"name": "brake", "move": "brake", "side": "left", "foot": "right", "path": "x", "entry": "run",
                "events": {"start": 0, "contact": 0.5, "release": 0.75, "end": 1}, "variant": [{"spans": [0.3, 0.15, 0.15]}], "key": keys,
                "sheet": {"entry_speed": 3.0, "plant": plant, "leg": [], "exit": {"kind": "rest"}},
                "card": {"drop_m": [0, 1], "snap_deg": [0, 999], "drive_m": [0, 9]}}
    hard = A.solve(move([]), {"spans": [0.3, 0.15, 0.15]}, rig)
    assert any("root accelerates" in p for p in hard.problems), hard.problems
    planted = A.solve(move([[0.0, 0.75]]), {"spans": [0.3, 0.15, 0.15]}, rig)
    assert not any("root accelerates" in p for p in planted.problems), planted.problems
