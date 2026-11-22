"""Authored shot clips: every kind, entry and angle bin for both feet; events in order, the boot on the ball at the contact, the
mirror swaps feet, the clips.json fields, and the bins of each kind and entry covering their angles without gaps."""
import json

import numpy as np
import pytest

import author as A
import retarget as R
import shot_author as SA
import shots as S

INDEX = SA.OUT / "clips.json"


@pytest.fixture(scope="module")
def rig():
    return A.load_rig()


@pytest.fixture(scope="module")
def shots():
    return [SA.load_shot(p) for p in sorted(SA.MOVES.glob("*.toml"))]


@pytest.fixture(scope="module")
def built(shots, rig):
    """One bin of each kind and entry, both feet (solving all 46 takes minutes; the committed folder is checked whole below)."""
    out = {}
    for m in shots:
        for entry in ("run", "stand"):
            b = SA.bins_of(m, entry)[-1]
            for mirror in (False, True):
                c = SA.solve_bin(m, entry, b, rig, mirror)
                f = SA.finish(c, rig)
                SA.check_shot(c, f)
                out[c.name] = (c, f)
    return out


@pytest.fixture(scope="module")
def index():
    return json.loads(INDEX.read_text())["clips"]


def test_every_kind_has_a_file_with_both_entries(shots):
    assert sorted(m["kind"] for m in shots) == ["chip", "finesse", "outside", "power"]
    for m in shots:
        assert set(m["entry"]) == {"run", "stand"}
        assert SA.bins_of(m, "run") and SA.bins_of(m, "stand")


def test_built_clips_pass_every_check(built):
    assert len(built) == 16
    for name, (c, f) in built.items():
        assert c.problems == [], f"{name}: {c.problems}"


def test_events_in_order_and_on_the_motion(built):
    for name, (c, f) in built.items():
        e = f["events"]
        assert list(e) == list(SA.EVENTS)
        assert e["start"] == 0
        assert e["start"] < e["takeoff"] < e["backswing"] < e["plant"] <= e["contact"] < e["end"] < len(c.hips) - 1
        kf, pf = f["foot"], 1 - f["foot"]
        # the plant foot is down from its landing through the contact; the striking foot is in the air from the backswing to it
        assert c.contacts[e["plant"] + 1:e["contact"] + 1, pf].all()
        assert not c.contacts[e["backswing"]:e["contact"] + 2, kf].any()
        # the striking foot stands until its takeoff and is back down by the end
        assert c.contacts[1:e["takeoff"] // 2, kf].all() and c.contacts[e["end"] + 1:-1, kf].all(), name
        # the hips are over the origin at the contact (as the captured kicks are)
        assert np.allclose(c.hips[e["contact"], [0, 2]], 0.0, atol=1e-9)
        assert f["contact_speed"] > 3.0


def test_the_boot_meets_the_ball_at_the_contact(built, rig):
    for name, (c, f) in built.items():
        gap = c.stats["touch_gap_m"]["contact"]
        assert -A.MAX_BOOT_IN <= gap <= A.MAX_TOUCH_GAP, f"{name}: boot {gap * 100:.1f} cm from the ball"
        assert c.stats["boot_in_ball_max_m"] <= A.MAX_BOOT_IN
        # measured again from the clip as written: the striking boot's nearest vertex to the ball's centre at the contact frame
        gr, gt = A.fk_points(rig, c.local_q, c.hips)
        bone, off = rig.boot[f["foot"]]
        i = f["events"]["contact"]
        pts = gt[i, bone] + np.einsum("kij,kj->ki", gr[i, bone], off)
        r = c.move["ball"]["radius"]
        assert abs(float((np.linalg.norm(pts - c.ball[i], axis=1) - r).min()) - gap) < 1e-3
        # and the ball leaves ahead of the boot along the ball-out direction
        d = c.ball[i + 1] - c.ball[i]
        out = A.ry(-c.move["shot"]["bin_deg"] if f["foot"] == 1 else c.move["shot"]["bin_deg"]) @ np.array([0.0, 0.0, 1.0])
        assert np.dot(d[[0, 2]], out[[0, 2]]) > 0.3


def test_the_ankle_travels_along_the_bins_angle(built):
    for name, (c, f) in built.items():
        sm = c.move["shot"]
        off = (f["kick_angle_deg"] - sm["bin_deg"] + 180.0) % 360.0 - 180.0
        assert abs(off) <= 25.0, f"{name}: ankle at {f['kick_angle_deg']:.0f} degrees for the {sm['bin_deg']:g} bin"


def test_the_mirror_swaps_the_feet(built):
    rights = [n for n in built if n.endswith("_right")]
    assert len(rights) == 8
    for n in rights:
        (c, f), (m, g) = built[n], built[n[:-5] + "left"]
        assert (f["foot"], g["foot"]) == (1, 0)
        assert f["events"] == g["events"]
        assert np.array_equal(c.contacts, m.contacts[:, ::-1])
        assert np.allclose(c.hips * [-1, 1, 1], m.hips, atol=1e-2)      # within the rig's own asymmetry
        assert np.allclose(f["kick_dir"] * [-1, 1, 1], g["kick_dir"], atol=2e-2)
        # the angle is counted toward the striking foot's own side, so it is the same number for both feet
        assert abs(f["kick_angle_deg"] - g["kick_angle_deg"]) < 2.0
        assert np.allclose(f["root"] * [-1, 1], g["root"], atol=1e-2)


def test_every_listed_clip_has_its_kind_and_bin(index):
    names = {e["name"] for e in index}
    assert len(names) == len(index) == 50
    for e in index:
        assert e["kind"] in ("instep",) + SA.KINDS, e["name"]
        assert e["foot"] in ("left", "right") and e["entry"] in ("run", "stand")
        lo, hi = e["bin_range"]
        assert lo <= e["bin_deg"] <= hi
        assert list(e["events"]) == list(SA.EVENTS)
        assert len(e["kick_dir"]) == 3 and len(e["root"]) == e["frame_count"]
        assert (SA.OUT / e["file"]).exists()
        assert e["entry_speed"] >= 0 and "kick_angle_deg" in e
        if e["kind"] == "instep":
            assert e["set"] == "shot" and e["bin_deg"] == 0
        else:
            assert e["source"] == "authored" and e["set"] == f"shot:{e['kind']}"
            assert e["name"] == f"shot_{e['kind']}_{e['entry']}_{e['bin']}_{e['foot']}"
            assert e["checks"]["touch_gap_m"]["contact"] <= A.MAX_TOUCH_GAP


def test_the_bins_of_each_kind_and_entry_tile_their_angles(index):
    # the captured kicks are the power kick's 0-degree bin
    groups = {}
    for e in index:
        kind = "power" if e["kind"] == "instep" else e["kind"]
        groups.setdefault((kind, e["entry"], e["foot"]), []).append(tuple(e["bin_range"]))
    want = {"power": (-180.0, 67.5), "finesse": (-135.0, 79.0), "chip": (-67.5, 22.5), "outside": (22.5, 67.5)}
    assert len(groups) == 16
    for (kind, entry, foot), ranges in groups.items():
        ranges.sort()
        assert (ranges[0][0], ranges[-1][1]) == want[kind], f"{kind} {entry} {foot}: {ranges}"
        for (_, hi), (lo, _) in zip(ranges, ranges[1:]):
            assert hi == lo, f"{kind} {entry} {foot}: a gap or overlap between {hi} and {lo}"


def test_the_listed_clips_are_what_the_shot_files_make(index, shots):
    made = {f"shot_{m['kind']}_{entry}_{b['label']}_{foot}" for m in shots for entry in ("run", "stand")
            for b in SA.bins_of(m, entry) for foot in ("left", "right")}
    assert made == {e["name"] for e in index if e["kind"] != "instep"}
    assert len(made) == 46


def test_a_built_clip_is_the_one_in_the_folder(built, index):
    by = {e["name"]: e for e in index}
    for name, (c, f) in built.items():
        e = by[name]
        assert e["events"] == f["events"] and e["frame_count"] == len(c.hips)
        assert e["kick_angle_deg"] == round(f["kick_angle_deg"], 1)
        k = R.read_kclip(SA.OUT / e["file"])
        assert np.allclose(k["hips"], c.hips, atol=2e-3)


def test_the_captured_kicks_keep_their_place(index):
    cap = [e for e in index if e["kind"] == "instep"]
    assert sorted(e["name"] for e in cap) == ["shot_run_left", "shot_run_right", "shot_stand_left", "shot_stand_right"]
    for e in cap:
        assert e["bin_range"] == [-22.5, 22.5] and abs(e["kick_angle_deg"]) < 22.5
        assert e == {**S.entry({"name": e["name"], "clip": e["source_clip"], "frames": e["source_frames"], "foot": 1 if e["foot"] == "right" else 0,
                                "entry": e["entry"], "approach_speed": e["entry_speed"], "events": e["events"], "fps": e["fps"],
                                "hips": np.zeros((e["frame_count"], 3)), "contacts": np.zeros((e["frame_count"], 2), bool),
                                "kick_dir": np.array(e["kick_dir"]), "contact_speed": e["contact_speed_mps"],
                                "backswing_ankle_m": e["backswing_ankle_m"], "follow_peak_m": e["follow_peak_ankle_m"],
                                "root": np.array(e["root"]), "notes": e["notes"]}), "contacts": e["contacts"]}
