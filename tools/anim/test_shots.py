"""Shot clips: cut from the committed kicks, events found from the motion, mirrored for the other foot, reproducible."""
import json

import numpy as np
import pytest

import retarget as R
import shots as S


@pytest.fixture(scope="module")
def target():
    return R.load_target(S.MODEL)


@pytest.fixture(scope="module")
def made(target):
    return {c["name"]: c for c in S.make(target)}


def test_both_kicks_cut_for_both_feet(made):
    assert sorted(made) == ["shot_run_left", "shot_run_right", "shot_stand_left", "shot_stand_right"]
    for name, c in made.items():
        assert c["foot"] == (1 if name.endswith("right") else 0)
        assert c["entry"] == ("run" if "run" in name else "stand")


def test_events_in_order_and_on_the_motion(made, target):
    for name, c in made.items():
        e = c["events"]
        assert e["start"] == 0
        assert e["start"] <= e["takeoff"] < e["backswing"] < e["contact"]
        assert e["takeoff"] < e["plant"] <= e["contact"] < e["end"] < len(c["hips"]) - S.SETTLE + 1
        kf, pf = c["foot"], 1 - c["foot"]
        con = c["contacts"]
        # the plant foot lands on `plant` and is down through the contact; the striking foot is up from its takeoff to past the contact
        assert con[e["plant"], pf] and not con[e["plant"] - 1, pf] and con[e["plant"]:e["contact"] + 1, pf].all()
        assert not con[e["takeoff"]:e["contact"] + 3, kf].any() and con[e["takeoff"] - 1, kf]
        # the striking boot is near the ground at the contact and fastest about then
        gr, gt = S.fk(target, c["q"], c["hips"])
        low = R.foot_points(target, gr, gt, kf)[:, :, 1].min(axis=1) - target.sole_y
        assert low[e["contact"]] <= S.BALL_H
        assert c["contact_speed"] > 10.0
        # the hips are over the origin at the contact, the approach runs along +Z
        assert np.allclose(c["hips"][e["contact"], [0, 2]], 0.0, atol=1e-9)
        d = c["hips"][e["plant"]] - c["hips"][0]
        assert abs(d[0]) < 1e-6 and d[2] > 0.3


def test_run_clip_swings_big_and_stand_clip_short(made):
    run, stand = made["shot_run_right"], made["shot_stand_right"]
    # the running kick: the heel well up behind in the backswing, the boot over half a metre in the follow-through
    assert run["backswing_ankle_m"] > 0.5 and run["follow_peak_m"] > 0.5
    # the standing kick is the shorter, lower swing
    assert stand["follow_peak_m"] < run["follow_peak_m"]
    # no foot of the running clip is down between the striking foot's takeoff and the plant (its flight)
    e = run["events"]
    assert not run["contacts"][e["takeoff"]:e["plant"]].any()


def test_mirror_swaps_the_feet_and_comes_back(made, target):
    for base in ("shot_run", "shot_stand"):
        r, l = made[f"{base}_right"], made[f"{base}_left"]
        assert np.array_equal(r["contacts"][:, ::-1], l["contacts"])
        assert np.allclose(r["hips"] * [-1, 1, 1], l["hips"])
        rr, gr = S.fk(target, r["q"], r["hips"])
        rl, gl = S.fk(target, l["q"], l["hips"])
        mo = S.mirror_index(R.BONES)
        # every bone of the mirror turns from rest as its opposite bone does, reflected across x = 0 (F turn F, F = diag(-1, 1, 1))
        F = np.diag([-1.0, 1.0, 1.0])
        dr = rr @ np.swapaxes(target.rest_glob_r, -1, -2)
        dl = rl @ np.swapaxes(target.rest_glob_r, -1, -2)
        assert np.abs(F @ dl[:, mo] @ F - dr).max() < 1e-6
        # and its joints are the reflections within the rig's own asymmetry (about a centimetre)
        assert np.abs(gl[:, mo] * [-1, 1, 1] - gr).max() < 0.02
        back = S.mirror(target, l)
        assert np.abs(back["q"] - r["q"]).max() < 1e-6 or np.abs(np.abs((back["q"] * r["q"]).sum(-1)) - 1).max() < 1e-9


def test_written_files_match_a_fresh_cut(made, tmp_path):
    assert S.main(["--out", str(tmp_path)]) == 0
    index = json.loads((tmp_path / "clips.json").read_text())
    names = [c["name"] for c in index["clips"]]
    assert sorted(names) == sorted(made)
    committed = S.OUT / "clips.json"
    if committed.exists():
        # (the folder also holds the clips authored by shot_author.py, which a cut into an empty folder does not make)
        listed = json.loads(committed.read_text())
        listed["clips"] = [c for c in listed["clips"] if c.get("source") != "authored"]
        assert listed == index
        for n in names:
            assert (tmp_path / f"{n}.kclip").read_bytes() == (S.OUT / f"{n}.kclip").read_bytes()
    for c in index["clips"]:
        k = R.read_kclip(tmp_path / c["file"])
        assert k["events"] == c["events"] and len(k["hips"]) == c["frame_count"]
        assert len(c["root"]) == c["frame_count"]
