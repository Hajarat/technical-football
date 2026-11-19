"""ASF/AMC reader against a CMU trial (needs the cache: python3 tools/anim/cmu.py)."""
from pathlib import Path

import numpy as np
import pytest

import asfamc

CACHE = Path(__file__).resolve().parent / ".cache"
needs_cache = pytest.mark.skipif(not (CACHE / "141_05.amc").exists(), reason="run tools/anim/cmu.py first")


def test_euler_order_applies_first_axis_first():
    m = asfamc.euler("xyz", [90, 0, 90])
    # x first: +Y -> +Z, then z: +Z stays +Z
    assert np.allclose(m @ [0, 1, 0], [0, 0, 1], atol=1e-9)
    assert np.allclose(asfamc.euler_batch("xyz", np.array([[90.0, 0, 90]]))[0], m)


@needs_cache
def test_parses_trial_with_known_values():
    sk, m = asfamc.load_trial(CACHE, "141_05")
    assert sk.length_unit == pytest.approx(0.45)
    assert sk.unit_m == pytest.approx(0.0254 / 0.45)
    assert m.count == 230 and m.first_frame == 1
    assert len(sk.bones) == 31 and sk.order[0] == "root"
    assert sk.bones["lfemur"].dof == ["rx", "ry", "rz"] and sk.bones["lfemur"].parent == "lhipjoint"
    # frame 1 of the AMC: "root 26.4887 16.0039 5.05251 -171.155 1.21282 -177.102"
    rot, start, end = sk.global_transforms(m, [0])
    assert np.allclose(start["root"][0], np.array([26.4887, 16.0039, 5.05251]) * sk.unit_m, atol=1e-9)
    assert np.allclose(m.channels["root"][0], [26.4887, 16.0039, 5.05251, -171.155, 1.21282, -177.102])
    # rotations stay rotations, and every bone hangs off its parent's end
    for b in sk.order:
        r = rot[b][0]
        assert np.allclose(r @ r.T, np.eye(3), atol=1e-9)
        if b != "root":
            assert np.allclose(start[b][0], end[sk.bones[b].parent][0])


@needs_cache
@pytest.mark.parametrize("subject", ["77", "90", "124", "141", "144"])
def test_units_give_a_human_height(subject):
    """Rest pose, ankle-sole to the end of the head bone: CMU's subjects come out between 1.4 and 1.9 m in metres."""
    sk = asfamc.parse_asf(CACHE / f"{subject}.asf")
    start, end = sk.rest_points()
    height = end["head"][1] - min(end["ltoes"][1], end["rtoes"][1])
    assert 1.4 < height < 1.9, height


@needs_cache
def test_standing_subject_feet_on_the_floor():
    """Frame 1 of 124_11 is a subject standing still: the root sits about 1 m up and both ankles a few cm above y = 0."""
    sk, m = asfamc.load_trial(CACHE, "124_11")
    _, start, _ = sk.global_transforms(m, [0])
    assert 0.85 < start["root"][0, 1] < 1.1
    for c in "lr":
        assert 0.03 < start[c + "foot"][0, 1] < 0.12
