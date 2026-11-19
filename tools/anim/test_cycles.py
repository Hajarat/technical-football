"""Stride cycles: cut from the committed attacker clips, closed, travel taken out, one stance per foot."""
import numpy as np
import pytest

import cycles as C
import retarget as R


@pytest.fixture(scope="module")
def made():
    t = R.load_target(C.MODEL)
    return [C.make_cycle(c, t) for c in C.load()]


def test_every_listed_cycle_is_made(made):
    assert [c["name"] for c in made] == ["cycle_walk", "cycle_jog", "cycle_run", "cycle_sprint"]
    speeds = [c["blend_speed"] for c in made]
    assert speeds == sorted(speeds)


def test_loop_is_seamless(made):
    for c in made:
        # the turn across the seam (last frame back to the first) is no bigger than the cycle's own largest frame-to-frame turn
        # (a degree or two for the change of speed across it)
        assert C.seam_deg(c) <= 3.0, c["name"]
        # the hips come back to where they started, in place (the travel taken out): the seam step is an ordinary step (the speed of
        # the hips may differ a little across it)
        h = c["hips"]
        step = np.abs(np.diff(h, axis=0)).max(axis=0)
        assert np.all(np.abs(h[0] - h[-1]) <= 2 * step + 1e-6), c["name"]
        assert abs(h[:, 0].mean()) < 1e-6 and abs(h[:, 2].mean()) < 1e-6


def test_one_strike_and_toe_off_per_foot(made):
    for c in made:
        n = len(c["hips"])
        for i, s in enumerate("lr"):
            m = c["contacts"][:, i]
            a, b = c["events"][f"strike_{s}"], c["events"][f"toeoff_{s}"]
            assert m[a] and m[b] and not m[(a - 1) % n] and not m[(b + 1) % n]
            assert ((b - a) % n) + 1 == m.sum()


def test_running_cycles_have_flight_and_walk_double_support(made):
    by = {c["name"]: c for c in made}
    w = by["cycle_walk"]["contacts"]
    assert (w[:, 0] & w[:, 1]).any() and (w[:, 0] | w[:, 1]).all()
    for name in ("cycle_jog", "cycle_run", "cycle_sprint"):
        r = by[name]["contacts"]
        assert not (r[:, 0] & r[:, 1]).any() and not (r[:, 0] | r[:, 1]).all(), name


def test_speeds_match_the_survey(made):
    by = {c["name"]: c for c in made}
    assert by["cycle_sprint"]["speed"] == pytest.approx(5.19, abs=0.05)
    assert by["cycle_sprint"]["steps_per_s"] == pytest.approx(3.077, abs=0.01)
    assert by["cycle_walk"]["speed"] == pytest.approx(1.83, abs=0.05)


def test_stance_one_run_closes_gaps_and_keeps_the_longest():
    m = np.array([0, 1, 1, 0, 1, 1, 0, 0, 0, 0, 1, 0], dtype=bool)
    out = C.one_stance(m)
    assert out.tolist() == [True, True, True, True, True, True, False, False, False, False, True, True]   # 11-0 closed, 6-9 not
    wrap = np.array([1, 1, 0, 0, 0, 0, 0, 1], dtype=bool)
    assert C.one_stance(wrap).tolist() == wrap.tolist()
