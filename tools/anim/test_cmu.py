"""cmu.py: which files a clip list needs (no network)."""
import cmu


def test_urls_pad_subjects_below_ten_and_cache_names_do_not():
    files = dict(cmu.needed_files([{"trial": "9_01"}, {"trial": "127_03"}, {"trial": "16_35"}]))
    assert files["9.asf"] == "https://mocap.cs.cmu.edu/subjects/09/09.asf"
    assert files["9_01.amc"] == "https://mocap.cs.cmu.edu/subjects/09/09_01.amc"
    assert files["127_03.amc"] == "https://mocap.cs.cmu.edu/subjects/127/127_03.amc"
    assert files["16.asf"] == "https://mocap.cs.cmu.edu/subjects/16/16.asf"


def test_baked_clips_need_no_files():
    assert cmu.needed_files([{"name": "slide_baked"}]) == []
