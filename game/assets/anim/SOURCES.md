# Keeper motion clips

`keeper/*.kclip` and `keeper/clips.json` are generated from the CMU Graphics Lab Motion Capture Database by the scripts in
`tools/anim/`. The motion files themselves are downloaded into the git-ignored `tools/anim/.cache/` and are not committed.
Five clips (`source = "baked"` in `clips.toml`) are not motion capture: they are the game's own procedural keeper, recorded from the
keeper gallery's scripts (see "Baked clips" below).

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
| 140_01 | frames 221–560 | `getup_front_140_01` | get up from lying face down |
| 140_03 | frames 231–760, 601–645 cut | `getup_side_140_03` | get up from lying on his side |
| 140_08 | frames 161–660, 495–545 cut | `getup_back_140_08` | get up from lying on his back |
| 139_18 | frames 141–600, 371–471 cut | `getup_back_139_18` | get up from his back, rolling over |

The get-ups are trimmed to the part where he rises (the core's get-up lasts 1 s): their lead-in lying still is dropped and, where
a subject sank back or half rose and fell back before getting up, that stretch is cut (clips.toml "cut"; the motion after a cut
goes on from the frame before it).

## Attacker locomotion clips

`attacker/*.kclip` and `attacker/clips.json` hold 20 CMU trials retargeted onto `game/assets/characters/player.glb`, listed in
`tools/anim/attacker_clips.toml` (same keys as `clips.toml`, plus `contact_speed`: the foot speed under which a low foot counts as
planted, default 0.35 m/s, raised for running feet). They are an offline feasibility study for driving the outfield player's stride
from motion capture; nothing in the game reads them yet. Same CMU licence and credit as above. `player.glb` and `keeper.glb` have the
same 52 joints with the same translations and rest rotations, so a clip retargeted onto one is byte for byte the clip retargeted onto
the other.

20 trials from subjects 9, 10, 11, 16, 78, 104, 127: 09_01, 10_02, 11_01, 16_21, 16_36, 16_37, 16_39, 16_42, 16_44, 16_46, 16_47, 16_48, 16_54, 78_12, 104_06, 104_48, 104_53, 104_56, 127_05, 127_13. Each clip is its whole trial (the trials are 0.6 to 5 s long).

| Trial | Source range | Clip | What it shows |
| --- | --- | --- | --- |
| 09_01 | whole trial | `run_09_01` | run, straight, about 3.6 m/s |
| 10_02 | whole trial | `kick_soccer_10_02` | soccer: walk up and kick the ball |
| 11_01 | whole trial | `kick_soccer_11_01` | soccer: walk up and kick the ball |
| 16_21 | whole trial | `walk_brisk_16_21` | straight walk, about 1.7 m/s |
| 16_36 | whole trial | `jog_16_36` | run/jog, straight, about 2.7 m/s |
| 16_37 | whole trial | `veer_jog_left_16_37` | jog veering left about 45 degrees |
| 16_39 | whole trial | `veer_jog_right_16_39` | jog veering right about 45 degrees |
| 16_42 | whole trial | `turn90_jog_left_16_42` | jog with a 90 degree left turn |
| 16_44 | whole trial | `turn90_jog_right_16_44` | jog with a 90 degree right turn |
| 16_46 | whole trial | `run_16_46` | run/jog, straight, about 4.1 m/s |
| 16_47 | whole trial | `walk_16_47` | straight walk, about 1.3 m/s |
| 16_48 | whole trial | `veer_run_left_16_48` | run veering left about 45 degrees, about 4 m/s |
| 16_54 | whole trial | `turn90_run_right_16_54` | run with a 90 degree right turn |
| 78_12 | whole trial | `run_78_12` | running without a ball, straight, about 4.5 m/s |
| 104_06 | whole trial | `start_jog_104_06` | standing start into a jog |
| 104_48 | whole trial | `run_104_48` | run through, straight, about 4.1 m/s |
| 104_53 | whole trial | `start_run_104_53` | standing start into a run |
| 104_56 | whole trial | `stop_run_104_56` | run to a stop |
| 127_05 | whole trial | `stop_run_127_05` | run to a quick stop, turning |
| 127_13 | whole trial | `sidestep_run_127_13` | run with a side step to his left |

Subjects 141 and 143 (trials 141_01 to 141_34 and 143_01 to 143_42) look sampled at half the stated 120 Hz: their walks step 3 to 3.6
times a second and their runs 5 to 6, so no clip of theirs is used (the keeper's `141_05`, `141_10` and `143_20` clips come from the same subjects and were not checked). The speeds
in the table are the subject's own; retargeting scales a clip's travel by the model's leg length over the subject's (1.03 to 1.22).

```sh
python3 tools/anim/cmu.py --config tools/anim/attacker_clips.toml
uv run --with numpy python3 tools/anim/retarget.py --config tools/anim/attacker_clips.toml --model game/assets/characters/player.glb --out game/assets/anim/attacker
game/tests/run-bg.sh -- --clip-gallery ../out/attacker --clip-folder res://assets/anim/attacker --clip-model res://assets/characters/player.glb --video
uv run --with imageio-ffmpeg python3 tools/anim/encode_video.py out/attacker
```

`cmu.py` and `retarget.py` default to the keeper's list, model and folder, so the commands above under "Regenerate" are unchanged.

### Stride cycles

`tools/anim/cycles.py` cuts the attacker's stride cycles (`--attacker-anim mocap`) from the retargeted clips above into
`game/assets/anim/attacker/cycles/` (4 `.kclip` + `clips.json`), from the `[[cycle]]` entries of `tools/anim/attacker_clips.toml`:

| Cycle | From | Frames (60 Hz) | Speed on the model | Steps/s | Shown alone at |
| --- | --- | --- | --- | --- | --- |
| `cycle_walk` | `walk_brisk_16_21` | 17-76 | 1.83 m/s | 2.03 | 1.8 m/s |
| `cycle_jog` | `jog_16_36` | 39-86 | 2.88 m/s | 2.55 | 4.0 m/s |
| `cycle_run` | `run_16_46` | 10-51 | 4.45 m/s | 2.93 | 5.8 m/s |
| `cycle_sprint` | `run_78_12` | 7-46 | 5.19 m/s | 3.08 | 10.0 m/s and up |

Each is one stride, from a foot strike to the same foot's next strike (the frame after the range is the next stride's first). It is turned to
travel along +Z and its travel taken out (the hips relative to a root moving at the stride's mean speed; speed, steps per second and step length
go to `clips.json`); the end-to-start difference of every bone and of the hips' height is spread over the loop (frame i takes i/N of it), so it
runs into its start exactly (the mismatch before closing, 1.6 to 6.3 degrees mean, is in `clips.json`); and each foot's strike and toe-off
(its first and last frame within 2.5 cm of its lowest, by heel, ankle, toe joint or toe tip) are its events `strike_l`, `toeoff_l`,
`strike_r`, `toeoff_r` and its contact bits. The game mirrors a cycle at load when it wants the other foot's version
(`StrideCycle.Load(..., mirror: true)`).

```sh
uv run --with numpy python3 tools/anim/cycles.py
game/tests/run-bg.sh -- --clip-gallery ../out/cycles --clip-folder res://assets/anim/attacker/cycles --clip-model res://assets/characters/player.glb --video
uv run --with imageio-ffmpeg python3 tools/anim/encode_video.py out/cycles
```

### Shot clips

`tools/anim/shots.py` cuts the attacker's struck shot (`--shot-anim mocap`, `game/ShotClipLayer.cs`) from the two retargeted kicks above into
`game/assets/anim/attacker/shots/` (4 `.kclip` + `clips.json`). Both subjects strike with the right foot; each clip is written as recorded
(`_right`) and mirrored (`_left`, the same reflection as `FootballSim.Anim.Mirror`), so the game never mirrors them:

| Clip | From | Frames (60 Hz) | Entry | Approach | Backswing ankle | Follow-through ankle |
| --- | --- | --- | --- | --- | --- | --- |
| `shot_run_*` | `kick_soccer_11_01` | 162-239 | run | 2.8 m/s, three jogging steps | 0.67 m | 0.69 m (a hop on the plant foot) |
| `shot_stand_*` | `kick_soccer_10_02` | 138-212 | stand | one long plant step from standing | 0.53 m | 0.33 m |

The events are found from the motion: `start` (the plant foot leaves the ground for its last step; the first frame), `takeoff` (the striking
foot's last stance before the contact ends), `backswing` (the striking ankle's highest point between the takeoff and the contact), `plant`
(the plant foot's stance that lasts through the contact begins), `contact` (the striking ankle's peak speed among the frames with its boot
within 0.25 m of the ground; 12.6 and 11.6 m/s) and `end` (the next landing of either foot after the contact); 12 frames after `end` are kept
for the hand-back. Each clip is turned so its approach (the hips from `start` to `plant`) runs along +Z, its hips over the origin at the
contact; `clips.json` adds the root (the hips' ground track averaged over 13 frames), the kick's direction at the contact, the approach speed,
and the swing's heights and contact speed. `tools/anim/test_shots.py` checks the events against the motion and that the committed files are
what the tool writes.

```sh
uv run --with numpy python3 tools/anim/shots.py
```

## Baked clips

CMU has no goalkeeper dives, so a dive (low, side, high) and the smother take off on CMU motion (the clips tagged `takeoff`) and fly
on the procedural keeper's pose, baked into a clip (tagged `flight`); the slide tackle is baked whole.

| Clip | Gallery script | What it shows |
| --- | --- | --- |
| `dive_low_flight_baked` | `dive_low_right` | the low dive to his right, flat along the ground, and the fall |
| `dive_side_flight_baked` | `dive_side_right` | the side dive to his right at mid height, and the fall |
| `dive_high_flight_baked` | `dive_high_right` | the high dive to his right, arms up, and the fall |
| `smother_flight_baked` | `smother_right` | the smother to his right: down on his side behind the ball, hands around it |
| `slide_baked` | `slide` | the slide tackle: sitting back, the right leg out along the ground |

`tools/anim/bake_procedural.py` turns each recording to face +Z, keeps 60 Hz, holds the arms' and legs' twist steady where the
procedural solve flips it, cuts a flight to start just before its take-off (where the trunk has tipped 20 degrees from upright;
the smother, which no CMU take-off precedes, 45), smooths it over 2.5 frames and labels its take-off, contact and landing;
`retarget.py` then lists the baked clips in `clips.json` as they are. The dive take-offs hand over to the flight at the last calm
frame of their wind-up, before the arms swing.

## Regenerate

```sh
python3 tools/anim/cmu.py                                    # fetch the ASF/AMC files clips.toml lists (skips cached ones)
godot-mono --headless --path game -- --keeper-gallery ../out/bake-gallery --bake-keeper-pose ../out/bake --keeper-anim procedural
uv run --with numpy python3 tools/anim/bake_procedural.py out/bake   # write the baked keeper/*.kclip
uv run --with numpy python3 tools/anim/retarget.py           # write the CMU keeper/*.kclip and keeper/clips.json
```

Then, as below, the tests and the gallery.

## Check

```sh
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

## Authored skill clips

`attacker/skills/*.kclip` and `attacker/skills/clips.json` are hand-made: no motion capture and no external source. Each move is
a file of key poses in `tools/anim/moves/` (pelvis, chest and head angles, feet as sole points on the ground, the pelvis or the
ball, hands, knee and elbow directions, the ball's path, events and timings), which `tools/anim/author.py` solves onto the 22
driven bones of `player.glb`, mirrors to the other side and checks (no joint under the ground, no leg past its length, no bone
over 1,500 deg/s, the sole within 1 cm of the ball while it is on it, no boot more than 1 cm inside the ball, a named foot's
boot on the ball at each touch event; each declared stance standing still on the ground). A move file may declare its feet's
stances (`[stance]`, written to clips.json as `stance`, in clip seconds) instead of having them read off the solved motion, and a second
ending (`[[ending]]`, its own clip named with the ending's suffix and `ending` in clips.json). The game plays all nine moves (`game/SkillClipLayer.cs`, `--skill-anim clips`, the
default): it reads `entry_speed` to blend each move's standing and running clips, the events to time the clip onto the core's ticks, `ball.on`
and `ball.touch` for where the playing foot is pinned to the live ball, `ball.track` for the contact direction on the ball, `contacts` for the
planted feet, and takes the clip's root (`SkillClip.Root`) as the line under its hips from the first frame to the last.

| Move file | Clips | What it shows |
| --- | --- | --- |
| `ball_roll.toml` | `ball_roll_left`, `ball_roll_right` | from standing, the far foot's sole rolls the ball 0.6 m across the body (sole lands 0.12 s in, rolls 0.40 s), support-foot hop, exit to a run; the body sinks over the ball and leans into the roll, travelling with it, head over the ball, arms out wide, the rolling knee swinging across |
| `ball_roll_run.toml` | `ball_roll_left_run`, `ball_roll_right_run`, `ball_roll_left_run_swap`, `ball_roll_right_run_swap` | the same at a run (3.9 m/s, the simulation's 0.30 s roll): the sole takes the ball across the body while he runs on and steps sideways with it, facing along the run; the support foot skips under him (three declared stances of 0.05-0.1 s, two hops of about 0.1 m with the pelvis rising off the ground); in the own ending the support foot stands under him through the rolling foot's exit touch, in `_swap` it leaves at the second hop's take-off to make the exit touch and the rolling foot steps down off the ball as the new support; the body low over the ball and leaning into the roll, the pelvis leading it sideways, arms out wide, the rolling knee swinging across (the stances as before) |
| `drag_back.toml` | `drag_back_right`, `drag_back_left` | from standing, the sole pulls the ball 0.65 m back to under the body over 0.40 s, rock back, hips open, neutral exit (named by the foot: the move has no side); he sits back on the standing leg as the sole pulls, arms forward, then opens to the exit side |
| `drag_back_run.toml` | `drag_back_right_run`, `drag_back_left_run` | the same braking from 3 m/s: hard plant, the sole stops the ball; sitting back on the brake with the arms forward, then opening to the exit side (stances as before) |
| `step_over.toml` | `step_over_left`, `step_over_right` | from standing, the foot circles over the front of the ball without touching it, shoulder drop and hip swivel to the side, push off the other way; the boot circles wide and high, the hip and shoulder drop deep to that side with the other arm swinging across |
| `step_over_run.toml` | `step_over_left_run`, `step_over_right_run`, `step_over_left_run_swap`, `step_over_right_run_swap` | the same at a run (the simulation keeps 89% of his pace, the ball rolling on 0.45 m ahead, untouched): the support foot skips twice under him (declared stances of about 0.07 s, hops of 0.07 m or more) while the other circles over the ball's front and plants wide; two endings for the exit touch's foot (`_swap`: the support foot's) |
| `body_feint.toml` | `body_feint_left`, `body_feint_right` | from standing, a hard plant and lunge to the side (pelvis shift, shoulder drop, eyes), push off the planted foot the other way; a wider plant, a deeper drop of hip and shoulder, the other arm across, then the push off |
| `body_feint_run.toml` | `body_feint_left_run`, `body_feint_right_run`, `body_feint_left_run_swap`, `body_feint_right_run_swap` | the same at a run (the simulation keeps 82-89% of his pace, the ball rolling on untouched): a short last step, the wide plant and lunge on a declared stance, off it to the other side; two endings for the exit touch's foot (`_swap`: the other foot's) |
| `fake_cut.toml` | `fake_cut_left`, `fake_cut_right` | fake shot from standing: a full shooting wind-up, then the inside of the shooting foot hooks the ball 60 deg across and he turns with it; from the hook the weight is thrown toward the cut, hip and shoulder dropping into it, the arms following |
| `fake_cut_run.toml` | `fake_cut_left_run`, `fake_cut_right_run` | the same at 3 m/s; from the hook the weight is thrown toward the cut |
| `fake_cut_out.toml` | `fake_cut_out_right`, `fake_cut_out_left` | the fake shot's cut toward the shooting foot's own side: the same wind-up with the plant foot wide, the leg comes through on the ball's inner side, foot turned in, and its outside hooks the ball 60 deg across; he turns with it |
| `fake_cut_out_run.toml` | `fake_cut_out_right_run`, `fake_cut_out_left_run` | the same at 3 m/s |
| `fake_stop.toml` | `fake_stop_right`, `fake_stop_left` | fake shot from standing: the same wind-up, the leg comes through short and the sole stops the ball under him (named by the shooting foot); from the sole's landing the body brakes over the ball, sitting back with the arms out, then settles over it |
| `fake_stop_run.toml` | `fake_stop_right_run`, `fake_stop_left_run` | the same at 3 m/s, the run dying over the stop; from the sole's landing the body brakes over the ball, sitting back with the arms out |
| `elastico.toml` | `elastico_left`, `elastico_right` | from standing, the outside pushes the ball away from the exit side, the foot wraps round the back of the ball, the inside snaps it back at 45 deg; the knee and hip go out with the push and back with the snap, the upper body selling the first push with the arm out |
| `elastico_run.toml` | `elastico_left_run`, `elastico_right_run` | the same at a run (the simulation keeps 80% of his pace; the inside snap is the exit touch): the support foot skips under him on declared stances of 0.03-0.07 s with two hops of 0.07 m or more, stands for the snap, then the playing foot comes down off the ball across toward the exit; the sell (side bend, shoulder turn, the arm out) as large as standing |
| `spin.toml` | `spin_left`, `spin_right` | from standing, a 360 roulette: one sole drags the ball round through 180 deg, the feet swap, the other completes the turn; the shoulders and head lead the turn, arms out, the body low over the ball |
| `spin_run.toml` | `spin_left_run`, `spin_right_run` | the same entered at 2 m/s |
| `heel_to_heel.toml` | `heel_to_heel_right`, `heel_to_heel_left` | from a walk, the leading foot swings past the ball on its outer side and its heel knocks it back and across between the feet, the foot steps out beside it, and the trailing foot, turned out behind the ball, pushes it on with its inside (named by the leading foot; spans at the core's 0.117 / 0.217 / 0.35 s); the lead knee lifts and the heel is cocked before the knock and flicks up after it, the trunk leaning hard into the burst |
| `heel_to_heel_run.toml` | `heel_to_heel_right_run`, `heel_to_heel_left_run` | the same at a run; the lead knee lifts and the heel flicks, the trunk leaning hard into the burst |

A move's standing and running files share their events at the same normalised times, so the two can be blended by entry speed.

Each entry has `set = "skill:<move>"`, `side` (the side the move goes or sells; `none` for the drag-back), `foot` (the playing
foot), `entry` and `entry_speed`, events `start`, `contact` (a foot meets the ball, or passes over it), any further contacts (`contact2`; the spin also has
`lift`, its first sole leaving), `release`, `end`, and beside the usual keys `ball` (radius, `on`: the spans a sole is on it as [from event, to event, foot], `touch`: event to foot, and its centre per frame relative to the
start, model space), `timing` (each span's seconds), `finger_curl` (per frame, left and right: the clip has no finger bones) and
`sim` (the simulation's numbers the move was authored to).

```sh
uv run --with numpy python3 tools/anim/author.py         # every move file, both sides, every timing
game/tests/run-bg.sh -- --clip-gallery ../out/skills --clip-folder res://assets/anim/attacker/skills --clip-model res://assets/characters/player.glb --video
uv run --with imageio-ffmpeg python3 tools/anim/encode_video.py out/skills
```
