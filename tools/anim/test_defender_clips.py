"""The defender's clip list (tools/anim/defender_clips.toml) against the committed game/assets/anim/defender folder."""
import json
import tomllib
from pathlib import Path

import retarget as R

HERE = Path(__file__).resolve().parent
OUT = HERE.parent.parent / "game/assets/anim/defender"
SETS = {"jockey", "set", "lunge", "block", "barge", "slide", "getup", "stumble", "barged", "fall"}   # DefenderChooser's
NEEDS = {"lunge": "contact", "block": "contact", "getup": "up", "fall": "landing", "stumble": "recover", "barged": "recover"}                     # the event each set is timed by


def clips():
    with open(HERE / "defender_clips.toml", "rb") as f:
        return tomllib.load(f)["clip"]


def test_every_clip_names_a_chooser_set_a_side_and_a_verdict():
    cs = clips()
    assert len({c["name"] for c in cs}) == len(cs)
    for c in cs:
        assert c["set"] in SETS, c["name"]
        assert c.get("side", "none") in ("left", "right", "none"), c["name"]
        assert R.verdict(c) in R.VERDICTS
        assert c["name"].startswith(c["set"] + "_"), c["name"]


def test_timed_sets_carry_their_event():
    for c in clips():
        ev = NEEDS.get(c["set"])
        if ev and c.get("source") != "baked":
            assert ev in c.get("events", {}), f"{c['name']} needs a {ev} event"
            f0, f1 = c["frames"]
            assert f0 < c["events"][ev] < f1, c["name"]


def test_a_tackle_or_block_says_which_leg_leads():
    for c in clips():
        if c["set"] in ("lunge", "block", "slide"):
            assert c["side"] in ("left", "right"), f"{c['name']}: side is the leading leg"


def test_every_set_has_a_kept_clip():
    kept = {c["set"] for c in clips() if R.verdict(c) != "reject"}
    assert SETS - kept <= {"barge"}   # a rejected barge keeps the figure's own lean


def test_the_committed_folder_is_the_list():
    index = json.loads((OUT / "clips.json").read_text())
    listed = {e["name"]: e for e in index["clips"]}
    assert set(listed) == {c["name"] for c in clips()}
    for c in clips():
        e = listed[c["name"]]
        assert (OUT / e["file"]).exists()
        assert (e["set"], e["side"], e["verdict"]) == (c["set"], c.get("side", "none"), R.verdict(c))
        assert e["tags"] == list(c.get("tags", []))
    assert {p.name for p in OUT.glob("*.kclip")} == {e["file"] for e in index["clips"]}


def test_the_slide_is_the_keepers_baked_slide():
    c = next(c for c in clips() if c["set"] == "slide")
    assert c["source"] == "baked" and c["file_from"] == "keeper/slide_baked.kclip"
    assert (OUT / "slide_baked.kclip").read_bytes() == (OUT.parent / "keeper/slide_baked.kclip").read_bytes()
    k = R.read_kclip(OUT / "slide_baked.kclip")
    assert k["events"]["contact"] / k["fps"] == 17 / 60   # DefenderAnimator.SlideContactSeconds
