# Keeper motion clips

`keeper/*.kclip` and `keeper/clips.json` are generated from the CMU Graphics Lab Motion Capture Database by the scripts in
`tools/anim/`. The motion files themselves are downloaded into the git-ignored `tools/anim/.cache/` and are not committed.

## Credit and licence

The data used in this project was obtained from mocap.cs.cmu.edu. The database was created with funding from NSF EIA-0196217.

Licence (mocap.cs.cmu.edu, as read on 2026-11-18): the data is free for all uses. It may be copied, modified and redistributed,
and may be included in commercial products, but the data itself may not be resold directly as motion capture data.

## Trials used

The clips `clips.toml` lists; `keeper/clips.json` names the ones the checks let through.

33 trials from subjects 16, 76, 77, 81, 83, 90, 111, 118, 124, 127, 136, 139, 140, 141, 143, 144: 16_35, 76_11, 77_03, 81_12, 83_01, 83_13, 90_16, 90_18, 90_34, 111_21, 118_01, 124_11, 127_03, 127_05, 127_13, 127_23, 136_09, 136_11, 139_18, 140_01, 140_03, 140_08, 141_05, 141_10, 143_20, 144_07, 144_11, 144_17, 144_22, 144_23, 144_24, 144_25, 144_26.

| Trial | Source range (AMC frames, 120 Hz) | Clip | What it shows |
| --- | --- | --- | --- |
| 77_03 | frames 241–600 | `stance_ready_77_03` | crouched ready stance with small weight shifts |
| 83_01 | frames 260–520 | `stance_shuffle_83_01` | one sidestep to his right from standing (subject stands on a raised floor) |
| 83_13 | frames 121–420 | `stance_shuffle_83_13` | wide sidestep to his right into a low stance |
| 136_09 | frames 121–600 | `stance_crouchwalk_136_09` | walking forward crouched (spread / jockey) |
| 76_11 | frames 61–420 | `backpedal_quick_76_11` | quick large steps backwards |
| 136_11 | frames 181–660 | `backpedal_crouch_136_11` | walking backwards crouched |
| 16_35 | frames 1–162 | `run_jog_16_35` | run, about 2.8 m/s |
| 127_03 | frames 121–332 | `run_start_127_03` | from standing into a run |
| 127_05 | frames 1–300 | `run_stop_127_05` | run to a quick stop, turning |
| 127_13 | frames 1–185 | `run_sidestep_127_13` | run with a side step to his left |
| 143_20 | frames 61–240 | `catch_chest_143_20` | catch in front of the chest (then the throw starts) |
| 143_20 | frames 340–480 | `catch_high_143_20` | catch above the head |
| 141_10 | frames 91–240 | `catch_step_141_10` | step forward into a catch |
| 144_22 | frames 150–330 | `reflex_hand_high_144_22` | left hand reaches high to his left |
| 144_23 | frames 2380–2580 | `reflex_hand_low_144_23` | crouch and reach low to his left |
| 144_25 | frames 330–500 | `reflex_hand_high_144_25` | right hand reaches high to his right |
| 144_24 | frames 2300–2500 | `reflex_hand_low_144_24` | crouch and reach low to his right |
| 144_11 | frames 1050–1250 | `reflex_foot_lunge_144_11` | deep lunge forward-left |
| 144_17 | frames 760–960 | `reflex_foot_lunge_144_17` | deep lunge forward-right |
| 144_07 | frames 1340–1540 | `body_block_144_07` | arms up, block |
| 144_26 | frames 1460–1620 | `body_block_144_26` | step and block with both arms |
| 141_05 | frames 30–140 | `dive_side_takeoff_141_05` | jump sideways to his left (a small hop: take-off and landing only) |
| 124_11 | frames 121–300 | `dive_high_takeoff_124_11` | two-foot jump forward |
| 118_01 | frames 301–520 | `dive_high_takeoff_118_01` | crouch and jump |
| 144_17 | frames 2160–2340 | `dive_low_takeoff_144_17` | low lunge to his right, hands to the ground |
| 81_12 | frames 61–340 | `back_leap_jump_81_12` | jump backwards off a ledge and land |
| 90_16 | frames 400–620 | `lie_front_landing_90_16` | fall on his face onto a mat, lying |
| 90_18 | frames 61–300 | `lie_back_landing_90_18` | rug-pull fall onto his back, lying |
| 111_21 | frames 851–1250 | `lie_side_roll_111_21` | lying, rolling over |
| 127_23 | frames 1–300 | `smother_diveroll_127_23` | run, dive forward over, roll and get up |
| 90_34 | frames 241–570 | `smother_roll_90_34` | forward roll from standing, wide legs |
| 140_01 | frames 121–600 | `getup_front_140_01` | get up from lying face down |
| 140_03 | frames 91–760 | `getup_side_140_03` | get up from lying on his side |
| 140_08 | frames 61–660 | `getup_back_140_08` | get up from lying on his back |
| 139_18 | frames 121–600 | `getup_back_139_18` | get up from his back, rolling over |

## Regenerate

```sh
python3 tools/anim/cmu.py                                    # fetch the ASF/AMC files clips.toml lists (skips cached ones)
uv run --with numpy python3 tools/anim/retarget.py           # write keeper/*.kclip and keeper/clips.json
uv run --with numpy --with pytest pytest tools/anim          # pipeline tests
game/tests/run-bg.sh -- --clip-gallery ../out/clips --video  # contact sheets and frame dumps (renders; devbox: Xvfb)
uv run --with imageio-ffmpeg python3 tools/anim/encode_video.py out/clips
```

`tools/anim/clips.toml` is the only file edited to add, trim or drop a clip. `retarget.py` refuses to write a clip whose mapped
bones point more than 3 degrees away from their source bones, or whose feet in a contact span stay more than 2 cm off the
ground; it names such clips and exits non-zero, and the rest are still written.

Per clip, `clips.toml` also carries:

- `verdict`: the audition's verdict, `keep`, `trim` (frames narrowed, then kept) or `reject`. A rejected clip is still written
  and shown by `--clip-gallery`, but never enters the game's pose database. Rejected after the 2026-11-18 audition:
  `body_block_144_07`, `body_block_144_26`, `back_leap_jump_81_12`, `smother_roll_90_34`.
- `tags`: labels the chooser narrows a set by. `idle` marks a stance clip for standing still (Set, Planted, Reacting play only
  idle-tagged stance clips when there are any); `stance_ready_77_03` carries it.
- `match_bias`: a cost factor for the matcher (default 1); above 1 the clip is picked only when it fits clearly better.
  `stance_shuffle_83_01` (its subject steps off a raised floor) has 2, so shuffles prefer `stance_shuffle_83_13`.

`retarget.py`'s flip guard holds the hands, feet, upper arms, neck and head within a range of their rest and a turning speed
(16 degrees per 60 Hz frame for the neck and head). The neck and head entries remove a one-frame roll swap between the two in
`getup_back_140_08` at 0.98 s.
