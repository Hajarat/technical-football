# Football Sim

A 1v1 football shooting game: a simulation core in C# (`src/`), game data in JSON (`data/`) and a Godot 4.7 (.NET) shell (`game/`).
World coordinates are metres: X toward the attacked goal (goal line 52.5), Y up, Z across (the camera stands on the +Z side).
The pitch (`src/FootballSim.Core/Pitch.cs`) is a walled arena whose lengths come from the scenario's `pitch` block: `length`,
`width`, the goal's `goal_width` and `goal_height`, and the penalty area's `penalty_area_depth` (16.5 m from the goal line) and
`penalty_area_width` (40.32 m; in the scenarios' 20 m wide arena the area runs from wall to wall, so its sides are the boards).
`Pitch.InPenaltyArea` says whether a ball is in the area (any part of it on or over the line). The game draws the outline, the
5.5 m goal area, the penalty area's line and the penalty spot.

## Commands (run from the repo root)

| What | Command |
| --- | --- |
| Play (needs a gamepad); builds the C# first, since `godot-mono --path game` alone runs the last compiled build, which a pull does not update | `./play.sh` |
| Build the game project | `dotnet build game/FootballSim.Game.csproj` |
| Tests | `dotnet test FootballSim.slnx` |
| Batch run, 10 000 scripted rounds | `dotnet run -c Release --project src/FootballSim.Batch -- --scenario penalty --n 10000 --seed 1` |
| Save a scripted round as a replay | `dotnet run -c Release --project src/FootballSim.Batch -- --scenario breakaway --seed 6 --save-replay game/tests/smoke_replay.json` |
| Re-run a replay in the batch tool | `dotnet run -c Release --project src/FootballSim.Batch -- --replay game/tests/smoke_replay.json` |
| Dribble probe: scripted stick traces per dribbling rating, the idealised ones and human-like ones at a pad's 60 Hz (a wandering stick, 45 and 100 degree sweeps, a reversal flick through neutral at a jog and in close control): possession kept, touches per second, ball ahead, feet, steering, contact miss, committed touches not played at their contact (`drops`), stretched touches (played beyond the reach, with heavier errors), touches whose push was clamped to 30 degrees of the stick at the commit (`clamped`), the turn touches' contact miss (p50/p90/max), the time from a stick turn past the execution angle to the turn touch, the time for the ball to move along a flicked stick, the lowest speed of the wandering jog, and the speed steps (`steps`: ticks from 1 s on with the ball whose ground speed changes faster than 15 m/s^2, shots excluded, skill-move ticks included; `skill_steps`: those during a move or a turn at the feet; the session probe counts ticks where the arena boards stop the
player apart, in `wall`, not as steps); a pooled summary follows the table. `--dtrace <probe> --ratings R [--index i --seeds n --from T --to T --steps-only 1]` prints one probe tick by tick (the legs: the stick's or the go-get-it legs, and the forecast contact) with its speed steps and committed touches not played | `dotnet run -c Release --project src/FootballSim.Batch -- --probe dribble` |
| Session probe: replays the play-test rounds kept in `src/FootballSim.Core.Tests/TestData/playtest` (or `--replays <dir\|file>`; a folder is searched with its subfolders, so a folder of session folders such as `sessions` gives every round in them) with the current code and counts committed touches not played at their contact, and the speed steps (as the dribble probe counts them) and largest per-tick acceleration in possession, with a total per 1000 possession ticks | `dotnet run -c Release --project src/FootballSim.Batch -- --probe sessions` |
| Feel probe: speed surges, per-tick acceleration, touch cadence, ball distance and facing wobble of straight dribbles, and the heading response to a 45/90/180 degree stick turn with and without the ball, each row with its speed steps counted as the dribble probe counts them; `--replays sessions` gives the acceleration and speed-step numbers over recorded play-test rounds, `--trace sprint45` (`jog90`, `sprint90off`, ...) prints one turn tick by tick, `--rtrace <replay> [--from T --to T --steps-only 1]` one recorded round tick by tick with each speed step attributed to the touch controller's state | `dotnet run -c Release --project src/FootballSim.Batch -- --probe feel` |
| Shot diagnostic for the scoring calibration: per scripted round the distance, speed, charge and ball offset at the strike, the shot's accuracy factors, how it ended, why wide shots were wide and why rounds had no shot | `dotnet run -c Release --project src/FootballSim.Batch -- --probe shots --scenario breakaway --n 1000` |
| Shot probe: scripted shots pressed in possession (standing, a straight jog and sprint, a jog at 45 degrees to the aim, right after a sprint push with the ball about 1 m ahead, 0.1 s into a 90 degree turn) at dribbling 1/50/99: press to strike, ball distance at the press and the strike, dribble touches committed or played after the press, strikes made, strikes at the earliest tick with the ball at the feet, the others on the swinging foot in its window, the strike against the estimate published at the press and against the touch of the same run without the press, and the `shot` speed steps (from a press to 0.5 s after the strike; also in the session probe, which follows every shot pressed in possession, and the feel probe's `--replays` table). `--list 1` prints every run, `--trace <case> --ratings R --index i` one run tick by tick | `dotnet run -c Release --project src/FootballSim.Batch -- --probe shot` |
| Skill-move probe (scripted gestures per move, side, entry pace and skill_moves 1/50/99, every row recognised, made and kept 100; `--list-failed 1` prints the ball and the planned touch after the gesture for every run whose move was not made) | `dotnet run -c Release --project src/FootballSim.Batch -- --skill-probe` |
| Write one replay per skill move and side (edge_of_box start; the elastico, spin and heel-to-heel included) for screenshots | `dotnet run -c Release --project src/FootballSim.Batch -- --write-skill-replays out/skill-replays` |
| Write one replay per visible keeper action (shuffle, rush, planted then reacting, each dive kind, tip over the bar, each reflex save, a block by the ready keeper's hull, smother, closing down, charging, jockeying, spread, a long sprint touch won, a backpedal under a chip, taken round, parry then grounded then getting up) for screenshots; prints each file with the tick range of its action | `dotnet run -c Release --project src/FootballSim.Batch -- --write-keeper-replays out/keeper-replays` |
| Covered-angle table: the angle a set, planted keeper covers by shot distance (2-20 m), speed (20/25/30 m/s) and height | `dotnet test src/FootballSim.Core.Tests --filter KeeperCoverTests --logger "console;verbosity=detailed"` |
| Print the keeper's state tick by tick for one scripted round | `dotnet run -c Release --project src/FootballSim.Batch -- --scenario tight_angle --trace-keeper 1` |
| Smoke run: replay through the game scene, no window | `godot-mono --headless --path game -- --replay tests/smoke_replay.json --quit-after-round` |
| Smoke comparison (game report vs batch report) | `game/tests/smoke.sh` |
| Screenshot run (opens a window briefly) | `godot-mono --path game -- --replay tests/smoke_replay.json --shots out/replay --shot-on setup,charge,flight,outcome --quit-after-round` |
| Screenshot run without the window taking focus (use this from tools and agents) | `game/tests/run-bg.sh -- --replay tests/smoke_replay.json --shots out/replay --shot-on setup,strike,outcome --quit-after-round` |
| Keeper gallery: every keeper state and dive kind from a script (both sides, side and front views), no simulation; the keeper is animated from motion clips by default, `--keeper-anim procedural` shows the hand-built poses | `game/tests/run-bg.sh -- --keeper-gallery out/keeper-gallery` |
| A replay with the attacker's running body from the procedural run cycle instead of the motion-captured stride cycles, the default (see "Character models"); measurements as below | `godot-mono --headless --path game -- --replay tests/dribble_replay.json --quit-after-round --attacker-anim procedural` |
| A replay with the skill moves drawn procedurally instead of from the authored full-body clips, the default (see "Character models"); measurements as below | `godot-mono --headless --path game -- --replay tests/dribble_replay.json --quit-after-round --skill-anim procedural` |
| A replay with the struck shot drawn procedurally instead of from the motion-captured kicks, the default (see "Character models"); measurements as below | `godot-mono --headless --path game -- --replay tests/smoke_replay.json --quit-after-round --shot-anim procedural` |
| Regenerate the attacker's shot clips from the retargeted kicks | `uv run --with numpy python3 tools/anim/shots.py` |
| The authored skill clips' gallery (each clip with its authored ball, side, front and game-camera views, a sheet every 0.05 s, a per-tick strip around the contact; `--video` adds MP4 frames) | `game/tests/run-bg.sh -- --clip-gallery ../out/skills --clip-folder res://assets/anim/attacker/skills --clip-model res://assets/characters/player.glb --video` then `uv run --with imageio-ffmpeg python3 tools/anim/encode_video.py out/skills` |
| Regenerate the attacker's stride cycles (after `retarget.py` has written the attacker clips) and their gallery sheets | `uv run --with numpy python3 tools/anim/cycles.py` then `game/tests/run-bg.sh -- --clip-gallery ../out/cycles --clip-folder res://assets/anim/attacker/cycles --clip-model res://assets/characters/player.glb` |

The game stamps the commit it was built from: it prints `BUILD commit=... head=... stale=...` at start, warns on screen in interactive play when its build is behind the checkout, and records `build_commit`, `head_commit` and `build_stale` in the session log's `start` line.

The smoke run prints the round report on one line prefixed `REPORT ` and exits 0. `smoke.sh` checks it is identical to the batch tool's report.
Screenshots go to `game/out/` (ignored by git).

Game options after `--`: `--data <dir>` (default `data/`), `--replay <file>`, `--quit-after-round`, `--quit-after-ticks N`,
`--shots <dir>` with `--shot-ticks a,b,c`, `--shot-on setup,live,charge,strike,flight,outcome,touch<N>` (the N-th touch) or `keeper` (a burst of frames whenever the keeper's action changes; pair with `--close-keeper`), or `--shot-burst <startTick>:<count>:<step>`, `--close-cam` / `--close-keeper` (close-up cameras for inspecting limbs), `--side-cam` (the close camera on the attacker until his first skill move starts, then fixed where it is, side-on to his run 1 m along it from where the move starts, from the side he does not move toward (the +Z side for a move without one), framing about 4 m of pitch: a planted foot stays on one spot of the frame while the body passes over it; pair with `--shot-burst`), `--frame-times`, `--figure skinned|procedural` (default `skinned`: rigged human models; `procedural` keeps the primitive meshes; a missing or unloadable model falls back to procedural with a HUD warning), `--attacker-anim procedural|mocap` (default `mocap`: his running body from the stride cycles, needs the skinned figure, falls back to procedural with a warning when the cycles cannot load; `procedural` is the attacker's procedural run cycle), `--skill-anim clips|procedural` (default `clips`: a skill move with authored clips is drawn from them, in `--attacker-anim mocap` only; `procedural` draws every move as before, and it is what a move without clips gets), `--shot-anim mocap|procedural` (default `mocap`; `procedural`: the struck shot drawn by the figure; `mocap` draws it from the motion-captured kicks, in `--attacker-anim mocap` only, falling back to procedural with a warning when the clips cannot load), and `--background` (macOS: hide the app at startup and opt out of App Nap so a windowed run never takes focus; use it through `game/tests/run-bg.sh`, which also keeps the window from flashing in front while Godot starts and prints the game's output).

Automated tools and agents must use `game/tests/run-bg.sh` for any windowed run (anything with `--shots`); headless runs need no wrapper.

In replay mode the game also prints `FOOT_EVENT` per touch/strike and, on quit, `FOOT_CONTACT n= mean_m= max_m=` (touching foot's instep to the ball surface at each Touch/Strike), `FOOT_SLIDE n= mean_m= max_m=` (the attacker's planted feet: the largest movement of a foot per stance, flat on the ground; a foot rolling onto its toes is no longer planted) and `FOOT_SLIDE_KEEPER n= mean_m= max_m=` (the same for the keeper's planted feet while his rush and charge use the run cycle). With `--figure skinned` it also prints `SKIN_CONTACT n= mean_m= max_m=` (the skinned boot's contact surface for the touch's surface — inside, laces, outside or sole — to the ball surface at each touch's contact tick; target max <= 0.03 m), `FOOT_JUMP ctx=<skill|exit|touch|shot|other|all>` lines give, per context of the attacker's tick (a skill move, from a move's end until the first touch after it has recovered, a dribble touch, a shot, anything else), the largest and the 99th-percentile world move of either foot (the larger of its ankle's and boot tip's) in one physics tick (`dt_s`, one rendered frame at 120 Hz) and over two ticks (`max60_m`, one frame at 60 Hz), with the tick and foot of the largest, `over_m` (the largest move over its limit: 0.10 m per tick for the procedural attacker; with `--attacker-anim mocap` 0.10 m + 1.2 x speed x dt, as a running swing foot travels two steps per step of the body, about 0.14 m at a sprint) and `dv_max_mps` / `dv_p99_mps` with `dv_tick` / `dv_foot` (the largest change of a foot's velocity, ankle or boot tip, from one tick to the next, m/s per tick: a pop whatever the speed); `STRIDE n= step_s_mean= step_s_cv= step_m_mean= step_m_cv=` gives the attacker's running steps (plant to plant of alternate feet above 2 m/s, no skill move or shot: time, ground distance and their spread),

`FOOT_SKATE ctx=<skill|shot|touch|other> n= max_mps= p95_mps= over_0.5= worst_tick= worst_foot=` (printed with the FOOT_JUMP lines) measures foot skate: on every tick an attacker foot's sole is within 0.03 m of the ground (its boot tip, or its heel taken as the ankle less the ankle's rest height) and the foot is not pinned to the ball by a skill clip, its speed over the ground (of the tip and heel, the slower of those on the ground, so a foot rolling over its toes or heel is not counted), m/s; per context (a skill move; a shot; a touch, the exit after a move included; anything else) the ticks counted, the largest, the 95th percentile, the ticks over 0.5 m/s and the worst tick and foot. A swinging foot that skims the ground counts too, so the maximum is a swing's speed; compare `p95_mps` and `over_0.5` against plain running (a jog or sprint replay: about 0.15-0.2 m/s and 4% of the grounded ticks). With `ATTACKER_ANIM_LOG=1`, `MOCAP_SKATE` prints each counted tick over 0.5 m/s and `MOCAP_SOLE` both feet's tip and heel heights and speeds, the tick's context, the shown pelvis height (`pel_y`) and whether the foot is held planted, on every skill-move tick and while a skill clip plays or hands back.
and `POSE_LIMITS` carries the running-only `run_min_pelvis_ratio`, `run_lean_max_deg` and `run_bob_max_m`; the dribble probe behind these targets is `dotnet run -c Release --project src/FootballSim.Batch -- --probe dribble`.
With the skinned attacker a `MOCAP_POSE` line follows `BODY_POSE`: the fastest turn of any bone's local rotation on one tick outside shots (`bone_max_deg_s`, the bone and tick, and `bone_over_1500` ticks above 1,500 deg/s, a pop), leaving out the foot and toes of a foot while it plays the ball (a touch's pose, a skill move, a kick), which a `PLAYING_FOOT_TURN max_deg_s= at= over_1500=` line gives apart, and in `--attacker-anim mocap` the lowest boot tip (`min_toe_y_m`) and the ticks measured and those drawn wholly from the cycles. With `--attacker-anim procedural` the attacker's measurements come from the figure's joints; with `mocap` the ankles, knees, boot tips, pelvis (between the hip joints) and lean (hips to neck) are read from the skinned body as shown, and a stance is a foot held where it landed (`FOOT_SLIDE`, `STRIDE`; `STRIDE` counts only steps drawn wholly by one body, none across a procedural/cycle hand-over); `BODY_POSE`'s elbow range is still the procedural arm's; its `lean_close_deg` is the mean steady lean in close control. `ATTACKER_ANIM_LOG=1` in the environment prints the mocap attacker's per-tick diagnostics (`MOCAP_FOOT` weight, phase, stance, hold and ankles per foot; `MOCAP_CYCLE` each cycle's weight, stance share and frame; `MOCAP_KNEE`; `MOCAP_FAST` bones over 1,500 deg/s; `MOCAP_JUMP` foot moves over 0.09 m or velocity changes over 6 m/s per tick, with the touch state, for either body; `MOCAP_SLIDE`; `MOCAP_TOE`; `MOCAP_STEP`;
`SKILL_LAYER` each change of the skill-clip layer's state, clip and clip time; `SKILL_SOLE` the sole-to-ball distance on each tick of a sole move's span).
In `--attacker-anim mocap` the body-language layers (see "Character models") are measured on the shown body: a `BODY_LANG_TURN` line per turn (the smoothed sideways acceleration over 1.5 m/s^2 for at least 0.15 s while running freely above 2 m/s: no skill move or shot, the cycles' full weight) gives its ticks, the largest bank toward the inside (`bank_in_max_deg`: the trunk line from between the hip joints to the neck, rolled about the facing) and the largest bank less the cycle's own roll at that phase and speed (`bank_in_net_deg`: the stride's sway taken out), the layer's own bank, and on that net peak's tick the speed, the shoulders' turn ahead of the hips toward the inside (`shoulder_lead_deg`, the line between the upper arms against the line between the hip joints; `shoulder_lead_net_deg` less the cycle's own counter-rotation at that phase) and the head's ahead of the shoulders (`head_lead_deg`, the head bone's forward), with the largest of each over the turn. The `BODY_LANG` line gives the turn with the largest net bank, the largest bank 0.5-1 s after a turn ended and while running straight (no turn for 1 s, sideways acceleration under 0.5 m/s^2), each raw and net; the steady trunk lean less the cycles' own steady lean at the same speed (`steady_lean_vs_cycles_deg`, the cycles sampled over a stride at the blend the speed asks for; steady: both accelerations under 0.5 m/s^2), with the ball within 1.2 m and without; the largest lean forward of that steady lean while speeding up (`lean_acc_peak_deg`, the speed and acceleration there) and back of it while braking (`lean_brake_peak_deg`, with the pelvis drop below the cycles' there, and `lean_brake_at_6_min_deg` the least on ticks braking at 6 m/s^2 or more); the posture with the ball against without (`posture_lean_deg`, `posture_pelvis_drop_m`, NaN without steady ticks of both, and `pelvis_below_cycles_ball_m`); the head pitched down with the ball within 1 m (`head_down_deg_mean`, `min`); each dribble touch's follow-through (`follow_m_min`, `mean`, `n`: the touching ankle's move along the ball's launch direction relative to the pelvis over the 0.08 s after the contact, for touches made with the cycles' full weight; `standing_n` counts the others); and the fastest bone turn outside the whole leg of a foot playing the ball (`off_leg_bone_max_deg_s`, `at`, `off_leg_over_1500`; shots left out, as `MOCAP_POSE`'s, which leaves out only that foot and its toes). `BODY_LANG=off` in the environment leaves the layers out (the measurements still run); `BODY_LANG_LOG=1` prints a `BODY_LANG_TICK` line per tick.
A skill move prints `SKILL_FOOT kind= contact|arrived t= err_m=` (the move's surface to the ball surface at the contact tick, and 0.04 s after it) and, in
clips mode, `SKILL_CLIP start` (the move, foot, the two clips and the running clip's weight, the entry speed, the clip time it starts at and the
core's contact and end times); on quit `SKILL_CONTACT n= mean_m= max_m=` (the contact ticks) with `sole_max_err_m` (a sole move's sole to the ball
from its landing until its hand-back would begin), `on_ball_max_err_m` (the same over the whole span to the end tick, leaving out the ticks on which
the same foot already winds up for the core's exit touch, which `on_ball_exit_touch_ticks` and `on_ball_exit_touch_max_err_m` give apart) and
`step_over_clear_min_m` (the nearest any surface of a step-over's circling boot came to the ball surface, ticks on which the exit touch has that foot at a touch weight of 0.9 or more left out; negative is inside it). With the skinned
attacker drawn from his bones (`--attacker-anim mocap`) every one of these surface points is the skinned boot's own, as shown.

`--skill-probe` prints, per move: gesture recognised, move made, possession kept 2 s after the move, the time from the move's end to 90% of the with-ball top speed with sprint held (`t90_s`), the same for the same turn without the move (`base_t90_s`, both medians), the ball's distance at the move's end, for a step-over or feint the ball's distance ahead of the player at the start, when its own motion begins and when the foot passes over it (`start_ahead_m`, `exec_ahead_m`, `mid_ahead_m`), and for a sole move the ball centre's distance from the foot's contact point when the sole lands (`sole_miss_m`), each as mean/max; then the move's length from its start, the sole's reach included (`dur_s`; a fake's from its contact), its speed steps (`steps`: ticks inside it whose ground speed changes faster than 15 m/s^2, all seeds) and those in the 0.3 s after its end (`exit_steps`, a speed-up no quicker than the speed law under the exit burst excepted), for the ball roll its largest running speed from 0.15 s after its start (`fwd_late_max`), the player's travel along and across the run, the ball's travel along and across it and the ball ahead of and to the side of the player at the end (`p_fwd_m`, `p_side_m`, `ball_fwd_m`, `ball_side_m`, `ball_end_ahead_m`, `ball_end_side_m`, means; across is positive toward the move's side), the largest forward ground speed from the first contact and the forward speed on the move's last tick (`fwd_max/release`), for a two-contact move the ball's largest distance from the move's path at the second contact (`c2_path_miss_max`), the ball's largest distance from the player from the first contact (`ball_dist_max`), the facing's turn toward the side (`turn_deg`, a spin's full circle) and the heel-to-heel's flick speed (`flick_mps`). It runs on at most 4 threads; `--seeds`, `--ratings` and `--only step_over,...` narrow it.
Move timings (`skill` section): step-over 0.42 s (0.35 s over a still ball), the foot over the ball at the middle; body feint 0.35 s, the plant at the middle; at a run (`step_over_run_blend`: blended between 1 and 2.5 m/s of entry speed) both keep him running, easing no faster than `step_over_run_brake` (6 m/s^2) to `step_over_run_keep` of the entry speed (90% at 4 m/s, 82% at 7.5 m/s), the run turning toward the left stick by at most `step_over_run_turn_deg` (45 degrees), while the ball, untouched, rolls on with him: within the leg's reach at the middle, 0.45 m straight ahead of him along his travel at the end and released 0.15 m/s faster than he runs, so the next touch is an ordinary dribble touch; ball roll: the sole on the ball 0.40 s from standing and `ball_roll_run_seconds` (0.30 s) at a run (`ball_roll_run_blend`: blended between 1 and 2.5 m/s of entry speed; 0.27 s later in a chain), the running speed held through the reach and then easing down no faster than `ball_roll_brake` (6 m/s^2) to `ball_roll_keep` of the entry speed (85% at 4 m/s, 75% at 7.5 m/s) while the player steps sideways with the ball (a quick step that rises over the first half of the roll and eases off toward the release; from standing `ball_roll_follow` of the roll's distance), sized so that at the release the ball is 0.45 m ahead of him along his direction of travel and released at his ground speed along the run, or toward the left stick when it points up to 60 degrees toward the roll's side (held to 60 beyond that; a stick toward the other side keeps the run's line), so the exit touch is an ordinary one; from standing it ends 0.3 m ahead of him; its own exit burst (`ball_roll_burst` 0.5-1.8, `ball_roll_burst_duration` 0.4-1.1 s by rating); drag-back 0.40 s, the sole pulling the ball 0.65 m back; the fake shot: cancel pressed any time from the shoot press until `fake_shot_cutoff` (0.05 s) before the shot's planned strike, its contact on the tick the strike was planned for when the cancel came (at least `fake_contact_after_cancel`, 0.10 s, after the cancel), the shot winding up as a real one until then and never struck (the same legs and the same shot action, so the keeper reads it exactly as a real wind-up until the contact), the fake stop's sole on the ball `fake_stop_seconds` (0.15 s) leaving it at rest `fake_stop_ahead` (0.15 m) ahead of where he would stop, the fake cut's hook `fake_seconds` (0.1 s); the fake's skill action is published from that contact with its `StartTick` at the shoot press, and as `PendingSkill` from the cancel.
Elastico: a reach of at most `elastico_reach_max` (0.10 s), then from the first contact the outside of the foot away from the exit side pushes the ball `elastico_push_distance` (0.30 m) away from it over `elastico_push_seconds` (0.11 s), the foot wraps round it, and at `elastico_contact2_seconds` (0.22 s, the second contact) the inside plays it back across him as the exit touch: toward the left stick turned into 30-90 degrees off the run toward the exit side, pushed as a dribble touch made at once that way would be, onto a swing at least a windup after the move's end at `elastico_seconds` (0.26 s), where no exit touch is made and the controller plans the next touch as usual (with the stick released it is set across in front of him at 45 degrees as a close-control touch); the exit burst starts at the move's end; the run eases down to `elastico_forward_speed` (2.0 m/s) within `balance_deceleration` and over the second half he starts driving toward the exit side (2 m/s); at a run (`elastico_run_blend`: blended between 1 and 2.5 m/s of entry speed) it eases only to `elastico_run_keep` (80%) of the entry speed, no faster than `elastico_run_brake` (6 m/s^2), and he drives toward the side at half that pace; it sells the other side (its exit burst is largest going away from that one). Spin (a roulette toward its side): a reach of at most `spin_reach_max` (0.12 s); then `spin_seconds` (0.60 s) during which the facing turns a full circle toward the side (half way at `spin_contact2_seconds`, 0.30 s, when the other sole takes the ball; the first lifts at `spin_lift_seconds`, 0.25 s), the ball turning with the body in front of him (0.3 m ahead of his facing once settled, so it is dragged back and round), his own travel planned to end `spin_side` (0.5 m) toward the side and `spin_forward` (0.6 m) ahead of where the ball was first taken, the run eased to at most `spin_forward_speed` (2.0 m/s) within `balance_deceleration` (from a run he cannot shed his speed that quickly, so he travels further); at a run (`spin_run_blend`: blended between 1 and 2.5 m/s of entry speed) his pace along the run eases only to `spin_run_keep` (70%) of the entry speed, no faster than `spin_run_brake` (6 m/s^2), so he travels on through the turn; out of it facing and running his original way with the ball ahead (at a run rolling 0.15 m/s faster than he runs); while he spins the keeper reads the ball as at his foot. Heel-to-heel (straight on, led by the foot nearer the ball): `heel_to_heel_seconds` (0.35 s); at `heel_to_heel_contact_seconds` (0.12 s) the leading heel knocks the ball `heel_to_heel_back` (0.15 m) back against the trailing foot, which at `heel_to_heel_contact2_seconds` (0.22 s) flicks it on straight ahead at `heel_to_heel_flick_ratio` (1.3) times his running speed, at least `heel_to_heel_flick_min_speed` (5 m/s) and never faster than he can catch; at a run (`heel_to_heel_run_blend`: blended between 1 and 2.5 m/s of entry speed) it is a short flick paced so that, with him keeping his pace, the ball is `heel_to_heel_run_flick_ahead` (0.45 m) ahead of him 0.2 s after the move's end, where the next dribble touch meets it; he keeps his speed (at a run his entry pace, above the dribble cap too), the flick is the exit touch (the exit burst starts at the move's end) and he runs on to the ball as after a knock-on. Each of the three raises a touch on both contacts. A sole move (ball roll, drag-back, fake stop) reaches for the ball for at most `sole_reach_max` (0.12 s) before the sole lands, its step planned as eased in within `balance_deceleration`. The ball roll, fake stop and fake cut are kept to a smooth speed as the turn at the feet is (the running speed and the step together change the ground speed by no more than `balance_deceleration`), so their built-in stops are shed within it; from standing the step-over and body feint pace themselves; the drag-back keeps its own, faster stop (shed within the budget, a sprinter would run on 2-3 m past the ball he pulls back), a known exception: about 31-34 speed steps per run from a jog or sprint in the skill probe. After any move its step eases out within the run's deceleration budget. The turn at the feet has its own keys (`feet_turn_seconds` 0.25, `feet_turn_distance` 1, `feet_turn_step_back` 0.3, `feet_turn_cooldown` 0.2, `feet_turn_reach_max` 0.1).
Player sheets carry a `skill_moves` rating (1-99): a skill move's exit burst grows with it, and each move chained within 2 s of the previous one uses 0.8 times the previous move's rating. A sheet without it uses its `dribbling`. Skill-move timings and the burst live in the `skill` section of `data/tuning/default.json`.

## Dribbling and movement

Every number is in the `movement` and `touch` sections of `data/tuning/default.json`; where GOALS' shipped data gives one it is used.

- Speeds: a jog is 5.3 m/s for every player (`jog_speed`, GOALS' MaxJogSpeed), a sprint 6.8-8.5 m/s by `sprint_speed`
  (GOALS' MaxSprintSpeed); with the ball both are lowered by dribbling (`top_speed_with_ball`), and close control runs at 0.35
  of that lowered top speed.
- Turning: with the ball up to 450 degrees per second by agility (GOALS' dribbling RotationRate), less with weight and halved
  at top speed; a heading change smaller than the rate covers in `turn_ease` (0.05 s) eases in instead of snapping, so the
  small corrections between touches do not jerk the body round.
- A sharp turn or a cut costs the balance percentage of speed, shed rather than at once: the running speed never falls faster
  than `balance_deceleration` (12 m/s^2), the legs easing off and the balance loss sharing that one budget.
- Running at the boards a player brakes by himself (at `balance_deceleration`, at most 1.5 m/s in a tick when he turns onto
  boards close by), so he comes to them moving into them at `wall_arrive_speed` (1.2 m/s) or less, with the ball or without;
  along them he runs on.
- Without the ball the turn rate tops out at GOALS' 360 deg/s (`possession.off_ball_turn_bonus` -0.2), and a stick more than
  `reverse_turn_deg` (135) from the facing above `reverse_min_speed` plants the runner: `reverse_speed_loss` (0.65) of his
  speed is shed while he turns about.
- Touches: the stick pushed no further than `walk_max_input` (0.7, GOALS' MaximumInputSizeForWalkTouch) dribbles with
  walking touches, played as close control (a touch every step). A jog pushes the ball two steps on, and every
  `foot_switch_every`-th (4th) push takes one step more so the other foot takes over; a sprint 4 steps at dribbling 1, 3 from
  about 27 and 2 from about 71 (`steps_per_touch.sprint`, 4 to 1.8 by rating). While the stick turns smoothly faster than
  `turning_stick_rate_deg` a push goes at most `turning_max_steps` steps on (2 at a jog, 3 at a sprint), and so does a sprint
  push within 0.35 s of the stick changing by more than 10 degrees or of sprint pressed or let go. No push is solved for
  more than 1.2 s (one cut short there is solved for the last step inside it that keeps the foot its steps would have
  played, and the next touch is played on that step), nor faster than the player can catch within 1.5 s (at his running
  speed, or 0.8 of his top speed). A sprint push runs out and
  comes back: its step is kept while the full speed trim can still reach the ball; a jog's or close control's while a
  quarter of it can, else an earlier step plays it.
- The touch controller (a closed loop, `TouchPlanner`): every tick in possession it predicts the stick's legs and the free
  ball and looks for the earliest step, past a cooldown of the last push's steps, whose contact window (swing progress 0.3 to
  0.8 of the playing foot) can meet the ball; of that window's ticks it takes the one that meets it best. The legs follow the
  stick, the miss trimmed by a sideways step (eased in and out at `steer_accel`, 6 m/s^2, at most `steer_max` 0.6 m/s) and a
  change of the target speed held to the contact, which reaches the running speed through the speed law and keeps it within
  15% of the pace (as much either way near the off-ball top speed). When the stick's legs cannot reach the ball, the
  go-get-it legs run to where it will be, at the stick's pace or faster up to the off-ball top speed (first within 15% of the
  pace, the leg stretching if need be, and only then at any speed). The steering stops
  correcting within `reach_tolerance` (0.08 m) of the contact point; the foot reaches the rest.
- A demand (the stick turned past the execution angle off the ball's travel and off the run, the stick released for a stop,
  a skill gesture waiting, a shot's strike) shortens the cooldown to `early_execution` of the cadence (by the angle and
  dribbling), never sooner than `early_min_interval` (0.22 s); the go-get-it legs then take over when they play the ball a few
  ticks sooner, and the stride may be quickened by up to a quarter to bring a step's window onto the ball.
- Commit: one `windup` (0.12 s) before its contact the touch is committed, with its foot, kind (a dribble touch, a turn past
  the execution angle, a cut past `cut_deg`, a stop with the stick released, a knock-on) and surface
  fixed; the stride clock is held so the contact falls in the window. At contact the push follows the stick within 30 degrees
  of the stick at the commit; a bigger change is answered by the next touch. During a committed turn or cut the body opens
  toward the committed direction as far as the contact holds, and for `post_turn_seconds` (0.3 s) after a turn or cut touch
  the body turns at least `post_turn_rotation_rate` (540 deg/s, after GOALS' post-execution rotation rate).
- Reach: a plain touch plays a ball within `stretch_reach` (0.25 m) of its contact point; a turn, cut, stop or close-control
  touch one anywhere within `turn_reach` (0.45 m at rest to 0.6 m at a sprint) of the body. Beyond that but within
  `feet_turn_reach` (0.6 m, GOALS' execution radius) the leg stretches for it, with 1.5 times the rated errors. A ball further
  off at the contact is not played: the legs keep going to it and the next step plays it. The rated speed and direction errors
  are cut at two sigma.
- At the boards: a ball closer to a board than his body's radius, the contact zone's lead at rest and its radius (0.72 m) may
  be played by any touch or by the strike anywhere within `turn_reach` of the body, from beside the feet too (he cannot always
  get behind it). When such a ball is about still (under 1 m/s) and within 0.8 m of him (`turn_reach` at rest plus his
  body's radius), the go-get-it legs turn him to it where he stands rather than running at it at the stick's pace. A turn at
  the feet whose pull along the stick would run into a board within `feet_turn_distance` is not made: a touch plays the ball
  instead.
- A stick released for no longer than `release_debounce` (0.04 s; a pad polled at 60 Hz can read neutral for a tick or two
  in a flick) reads as still held; a longer release is a stop (a touch already committed is played as committed).
- Reversals (the stick at least `feet_turn_deg`, 120 degrees, from the ball's travel): up to `feet_turn_max_speed` (3 m/s),
  with the ball within `feet_turn_reach` of the body the sole turns it at once (the drag-back's motion with its own `feet_turn_*` timing, pulled onto the stick's line; no chain, exit burst or
  gesture) while the body comes round onto the stick at the post-turn rate, the player moved by his run alone, slowing within
  `balance_deceleration`. Faster, the stick that far from his travel arms a braking arc: he runs on to the ball, and from the
  turn or cut touch that hooks it onto the stick's line (slowly enough to meet his predicted path) he brakes within
  `balance_deceleration` to `turn_arc_keep` (40%) of the speed he had when the stick turned, never below `turn_arc_min_speed`
  (2.4 m/s), and holds that speed (no other balance loss) while he comes round, until he faces within `turn_arc_done_deg` (15
  degrees) of the stick (or, within 45 degrees of it, of the ball's line); then he speeds up along the stick as usual.
- Skill gestures in possession wait up to `skill.meet_wait_seconds` (1.5 s) on the controller's forecast, which treats them as
  a demand and commits no touch meanwhile; the move starts once the ball is in its radius. A move's own step eases in, and
  after it the controller eases it out (the drag-back's step itself rises and falls to rest).
- The keeper and the view read the next expected contact (`NextTouchEta`, `NextTouchFoot`, `NextTouchPoint`): the committed
  touch's, else the one the controller steers for.
- A lost ball (no one controls it, someone has controlled or struck it this round, play is live, no shot under way) in front of the player is gone for eagerly (`Regain`,
  the `regain_*` keys in `possession`): within `regain_cone_deg` (75) either side of his facing and `regain_max_distance`
  (10 m) of him, or anywhere within foot reach, the go-get-it legs run on to where it will be at the off-ball top speed,
  whatever the stick and sprint button say, and the first touch takes it as soon as it is in reach, even with the stick
  released. When the keeper contests it (within `regain_keeper_distance`, 3 m, of the ball, or reaching it no more than
  `regain_keeper_margin`, 0.3 s, after the attacker, each timed as the gap beyond foot reach over his top speed: the keeper's
  rush speed, the attacker's sprint speed) it is a race at normal foot reach; uncontested he also takes it from as far as
  `regain_reach` (1.2 m) of his body, a toe-poke at 1.5 times the rated errors. Right bumper (the finesse modifier) held
  with the left stick pushed more than 90 degrees away from the ball lets it run (FC's Let Ball Run). The trace's legs column
  shows `Race` or `Regain`.
- Loose-ball start spots (`data/scenarios/loose_*.json`, D-pad left / right in the game) begin with the ball already lost
  between the attacker and the keeper: a scenario's `ball_loose` makes the ball count as lost from the first tick, and
  `ball_speed` / `ball_heading_deg` (degrees, 0 toward the goal, positive toward +Z) start it rolling. Heavy touch (rolling on
  ahead of him, uncontested), 50-50 (a still ball halfway to the keeper, 5 m off his line), the keeper's ball (nearer the
  keeper), parry rebound (coming back off the keeper at 9 m/s across the attacker) and rolling across (crossing in front of
  him at 45 degrees).

Scripted rounds: the batch tool's scripted attacker runs at the goal and, in a scenario with `shoot_distance`, presses shoot
inside it once the ball is strikable or its next touch comes early enough in the wind-up; with `strike_distance` (breakaway
11.3, halfway run 12.4, angled run 11.1 m, the distances the scoring bands were first measured at) it presses only once the
ball, carried on at his speed to the contact, will be that close at the strike. Within 4 m of the keeper it stops sprinting
and within 6 m takes close control.

## Character models

The players are rigged MPFB2/MakeHuman bodies (`game/assets/characters/player.glb`, `keeper.glb`, Mixamo-compatible skeleton, CC0 assets only).
`PlayerFigure` still computes every joint procedurally each physics tick; `SkinnedBody` poses the skeleton from those joints (knees, ankles, elbows
and hands land on them), tints clothing and skin from `FigureKit` and puts the shirt number on the back. Foot-contact and pose measurements come
from the joints, so they are the same with either `--figure` value.

By default (`--attacker-anim mocap`; `--attacker-anim procedural` keeps the procedural run cycle above) the attacker's running body comes from motion-captured stride cycles instead (`game/AttackerAnimator.cs`,
`src/FootballSim.Anim/CycleMotion.cs`): four seamless one-stride loops in `game/assets/anim/attacker/cycles/` (walk 1.8 m/s, jog 2.9, upright
run 4.5, athletic run 5.2; see `game/assets/anim/SOURCES.md`). Their playback position is a function of the simulation's stride clock: each foot's
mid-stance falls on the other foot's swing middle, so the drawn swinging foot is the simulation's (a step in the clock's rate, the touch
controller nudging the stride, is eased: the drawn phase stays within 0.06 of the clock's). The two cycles either side of the smoothed speed
are blended at the same phase (walk 1.8, jog 4.0, upright run 5.8, athletic run 10.0 m/s: close control at 2.6 m/s is walk and jog, the
jog at 4.85 jog and upright run, the sprint at 7.4 the upright run and the athletic run; the shown trunk leans about 7, 13 and 20 degrees); a step shorter than the cycle's
shortens the feet's fore-aft travel, a longer one shortens its stance (more flight) instead of stretching the leg; a landed foot is held where
it landed until its heel rises. On top come the simulation's facing, the acceleration lean, the bank into turns, the head
looking along the travel and partly at the ball, the arms a little wider in close control, and a foot about to play the ball swings low.
Body language is laid over the cycles as layers (`src/FootballSim.Anim/BodyLanguage.cs`, `game/PlayerFigure.BodyLanguage.cs`), each eased by a
critically damped spring and faded out while a skill move or a shot owns the body. Turning: the body banks toward the inside by
atan(sideways acceleration / g) x 1.15, from 0 below 0.6 m/s^2 of it to all of it from 1.6 (so the stride's own sway and a steering wobble stay
upright), capped at 17 degrees at a jog and 24 at a sprint; the pelvis drops 0.12 m per radian of bank and shifts inside; the shoulders turn
ahead of the hips by the turn rate (sideways acceleration over speed) x 0.11 s (at most 22 degrees) and the head ahead of the shoulders by
x 0.15 s (at most 24 degrees); the figure's look follows the facing, which turns ahead of the run, aimed ahead by the turn rate x 0.16 s plus the
facing's own turn over the look's easing, and leaves the ball for it; the outside arm swings out 1.1 radians per radian of bank.
Visible touches (with the cycles in, by their weight; a touch from standing keeps the procedural swing): the windup's foot arrives moving
through the ball, from 0.18 m behind the contact along the ball's launch direction on an x(2 - x) curve (flat at its start, steepest at the
contact), and follows through along it relative to the body, up to 0.20 m eased out over 0.125 s and lifted 7 cm, the touch pose held whole
for 0.09 s (at most half the recovery), never further from the hip than 0.96 of the leg unless the contact itself is, and handed over to
the procedural recovery from 0.085 s to 0.125 s, so a skill move or shot that starts then takes over the pose it always did; a laces
touch points its toe down through the follow-through (0.35 rad); the playing leg's knee turns toward the boot's heading plus half the
launch direction (0.8 of the way, at most 8 of its weight a second, so a withdrawn forecast never twists the leg); and a touch more than
0.2 rad off the facing turns the trunk toward it by 0.3 of its angle (at most 10 degrees) by its weight.
Speeding up and slowing down: on top of the acceleration lean the pelvis already takes (12 degrees at 4 m/s^2), the trunk leans forward a
further 0.035 rad per m/s^2 of forward acceleration (at most 0.22 rad) and back 0.03 rad per m/s^2 of braking (at most 0.24 rad), faded in
from 0.4 to 0.8 m/s^2 so a steady run's jitter stays upright, eased with a 0.06 s half-life; braking also drops the pelvis 1 cm per m/s^2
(at most 6 cm: deeper knees).
Dribbling posture: with the ball in his possession and within 1.2 m (fading out by 1.5 m, eased with a 0.12 s half-life) the trunk leans
0.11 rad further forward and the pelvis sits 3.5 cm lower, the head pitches 0.12 rad further down (plus 0.6 of any braking lean back, so the
eyes stay on the ball) and looks down at a ball right at his feet as at one 0.15 m away, both elbows open by up to 0.45 rad (keeping at
least 0.35 rad of bend: the hands carried lower) and both arms go 0.12 rad wider.
Touches stay the figure's: its touch pose is the swinging leg's target, so the boot's surface meets the ball on the contact tick as before.
Standing and idle steps, starts from rest and stops below a walking pace and kicks stay procedural: the procedural pose is
still solved every tick, the cycles' share of it rises to 1 while stepping above a walking pace with no skill or kick, eased over 0.1 s,
and the foot pitch is rate-limited, so a hand-over does not pop. A shot runs on the cycles (`PlanShot`, `game/PlayerFigure.Shot.cs`): from the
press he runs on as he would without it and dribble touches are drawn as touches (the shot's soft touch, which the core makes on the press
tick, is not drawn: it only keeps the ball rolling with him). The strike sequence is timed backwards from the published contact estimate
and starts when that comes within its length (0.28 s for a full-power shot, 0.22-0.26 s for the other kinds, times 0.6 + 0.4 x the
charge): the striking foot is drawn back from wherever the stride has it, swings through like a pendulum and meets the ball with the
action's surface at its contact point on the contact tick, then follows through relative to the body at the swing's own speed and hands
back to the cycles. If the estimate moves later before the commit, the sequence eases out from where it was over 0.12 s and starts again.
The plant is the stride's own stance foot when the stride has the other foot down at the strike (the core's strike that takes the place of
a touch is on the swinging foot); otherwise (standing, or an at-feet strike on the foot the press chose) that foot steps, from one stride
step before, to beside the ball and is held where it lands until the body has run on (the hip 0.9 of a leg from it, or 0.1 s after the
contact). The upper body takes half of the procedural shot lean and shoulder turn over the cycles during the strike.

By default (`--shot-anim mocap`, in `--attacker-anim mocap` only; `--shot-anim procedural` keeps the computed shot above) the struck shot is drawn from motion capture instead (`game/ShotClipLayer.cs`, the
clips in `game/assets/anim/attacker/shots/` cut by `tools/anim/shots.py`, see `game/assets/anim/SOURCES.md`): a CMU kick from a jog and one
from standing, for either foot, blended by his speed at the press (all standing below 1.2 m/s, all running from 3 m/s; a power or driven
shot keeps at least 0.4 of the running kick's longer swing) on one timeline through the clips' events (`start`, `takeoff`, `backswing`,
`plant`, `contact`, `end`). The clip is entered as late in its wind-up as the published time to the contact asks for (a running kick no
earlier than its striking foot's takeoff, so no foot of the clip stands on the ground while he runs on), never played fast to fit; it then
runs on that estimate so `contact` falls on the strike tick (a later estimate slows it; one that would slow it below 0.45 before the plant
hands back and enters again). The pose goes on his position and facing turned toward the aim (at most 0.6 rad, fixed from the plant); from
the entry to the plant it is drawn toward where the clip has the body relative to the ball at the core's contact point (at most 0.6 m), and
back to his position through the follow-through, the plant foot reaching the rest of the way to its place beside the ball. Feet the clip
has down are held where they land (`PlantLock`; the plant foot through its heel rising, up to 9 cm), the striking foot's surface (instep
for power, inside for finesse and chip, as the core says) is pinned to the ball on the strike tick (ramping in over the last 0.07 s, out
over 0.06 s), with the boot's own surface on the ball and the contact check's surface point (the same offset turned by the foot's heading
only) on it too. Charge and kind scale the striking leg's swing about its contact pose (power and driven 0.8 + 0.2 x charge of the clip's,
the other kinds 0.6 + 0.2 x charge); a finesse shot turns the leg out onto the inside of the foot, a chip leans the trunk back. After the
contact the follow-through plays on to its `end` (up to 1.3 times as fast at a sprint) even when the shot's recovery ends sooner. The entry
and the hand-back to the stride are inertialised as the skill clips' are. A fake read during the wind-up hands back to the procedural shot
drawing on that tick. `SHOT_CLIP` lines report each shot's entry, contact (`skin_err_m`: the boot's own surface to the ball) and hand-back;
`SHOT_CLIP_DBG=1` prints the layer's state per tick.

Skill moves (`--skill-anim clips`, the default) are drawn from hand-authored full-body clips (`game/SkillClipLayer.cs`,
`src/FootballSim.Anim/SkillClips.cs`, the clips in `game/assets/anim/attacker/skills/`, see `game/assets/anim/SOURCES.md`) for all nine moves
the simulation makes, each foot (and side), from standing and from a run; the quick turn at the feet (a drag-back kind with its own short
timing, told apart by its sole span under 0.3 s) stays procedural. When a move starts, its standing and running clips are blended by the
speed he came in at (0 at the standing clip's entry speed, 1 at the running clip's, smoothstep between; fixed for the move). The clip's time
follows the core's ticks: its `start`→`contact` span plays over the move's start→contact tick (a step-over's or feint's contact is its middle
tick; a fake shot is drawn as the real shot's wind-up until 0.10 s before its contact, and its clip starts there, part way in), `contact`→`release` over contact→end tick (with the
spin's `lift` and the second contact `contact2` of the elastico, spin and heel-to-heel on their ticks), and `release`→`end` in real time after the end tick (the settle), through a monotone cubic, so
each event lands on its tick and the rate changes smoothly from one span to the next. A reach the core makes more than 1.5 times shorter than
the clip's starts the clip part way in (a fake shot counts its wind-up from the shot's press). The pose relative to the clip's root (a straight
line under its hips from first frame to last, heading fixed at its first frame, so a turn the clip makes is kept; for the spin the clip's
own heading is taken out, since the simulation's facing makes the 360 degrees) is put on the simulation's position and facing. The hand-in from the cycles or the standing pose and the hand-back at the clip's end are inertialised (each bone's
offset and its velocity die away, half-life 0.08 s); at the hand-back each foot keeps its offset from the figure's ankle, dying away while the
foot swings and held while the figure has it planted. On top of the clip: the playing foot's surface (the sole, or the inside or outside the
core names, the heel for heel-to-heel's first contact; the second span's foot and surface are the core's second contact's) is brought onto
the live ball by leg IK over the clip's on-ball spans (and at each touch the core also makes: a fake cut's hook, an elastico's two contacts), the weight rising through the reach
and falling over 0.08 s after it, with the contact direction the clip has on its own ball; on the end tick it holds where the ball was, as the
core's exit touch moves it; the hips come up to 0.18 m toward a ball the leg cannot reach. A step-over's circling foot keeps its path relative
to the live ball, lifted over it (1 cm clear, the lift changing at most 1.5 m/s) where the hand-in from the stride would carry the boot through it. A foot the clip plants is held where it landed (`PlantLock`), let go when the clip lifts it. A clip whose move file
declares its stances (`[stance]` in `tools/anim/moves/`, written to clips.json; the ball roll at a run) has its planted feet from those
spans, not read off its motion: such a foot is taken from the hand-in's offset onto the clip's own foot as its first stance begins
(stepping into place at up to 6 m/s, lifted on the way), held from its first stance tick where the clip's foot stands then, and follows
the clip's own foot from then on, its hops and landings as authored (at the take-off the drift between the simulation's travel and the
clip's root, carried from the spot it stood on, dies away over 0.14 s). A running clip with a second ending (the clip of the same name
with `_swap`: the ball roll's support foot swinging into the exit touch instead of standing under it) is crossed over to it in 0.05 s
while the exit touch the core forecasts is the support foot's, and back when it is not. A touch the figure draws after
the move began (the exit touch) takes its leg by the touch's weight, so its boot meets the ball on the contact tick as any touch; made by
the foot that made a touch of the move's own (a fake cut's exit touch, 0.1 s after the hook), its leg moves at most 0.06 m a tick against the
hips while it winds up and reaches at most 0.96 of its length, its foot turns toward the touch's at most 0.24 rad a tick, and the hips come
toward the figure's foot when it is out of reach. A fake cut
whose hook is the outside of the foot (the cut toward the shooting foot's side) has no clip and is drawn procedurally, as everything is with
`--skill-anim procedural` or `--attacker-anim procedural`. Build steps, asset list and licences: `game/assets/characters/SOURCES.md`.
A fresh checkout needs `godot-mono --headless --path game --import` once.

## Controls panel

The game shows the controls for the device in use: keyboard and mouse or gamepad, switching live to whichever produced the last
meaningful input (a key, a mouse button or more than a few pixels of mouse movement; a pad button or a stick or trigger past 0.3).
A small hint in the top-right corner names the toggle: `H: controls` on the keyboard, `Menu: controls` on the pad (`Options` on PlayStation, `+` on Nintendo). The
full list opens on `H` and, on either device, while the game is paused (`P` / `Esc` / Menu), and stays out of the play area otherwise. It lists
movement, shooting, skill moves (the right-stick gesture in words for the pad, the keys for the keyboard) and round / menu actions. Pad buttons use the pad's own names (Xbox, PlayStation or Nintendo, picked
from the name the OS reports; Xbox names when unknown), and the face-button swap keeps the shoot button the right-hand one, so the
labels match what is pressed. Every label is read from `game/Bindings.cs`, where `InputReader` also takes its keys and buttons, so
changing a binding there changes the panel. `-- --controls-panel keyboard|xbox|playstation|nintendo` opens the panel for that
device without a pad, for screenshots.

## Gamepad controls

The pad follows GOALS' Standard preset (Xbox names).

| Control | Action |
| --- | --- |
| Left stick | Run (stick right = toward the goal) and aim |
| Right trigger | Sprint |
| Left trigger (past half) | Shield (close control) |
| B / Circle (hold, release) | Power shot: power builds while held (full in 0.3 s) |
| Right bumper + B | Curved (finesse) shot |
| Left bumper + B | Chip |
| B (hold for power, release), then B again before the strike | Low (driven) shot; right bumper + B, B is a low curved shot; left bumper + B, B (GOALS' low chip, not built) is a chip |
| B, then A / Cross before the strike | Fake shot: left stick at rest a fake stop, pushed a fake cut that way; the shot winds up as a real one and the fake's contact comes when the strike would have (at least 0.10 s after A; A in the last 0.05 s before the strike is too late) |
| Right stick: forward, then straight on to a side | Step-over to that side |
| Right stick: a side flicked from rest and released | Body feint to that side |
| Right stick: a side held 0.1 s | Ball roll to that side |
| Right stick: forward flick while sprinting | Knock-on: a heavy touch ahead to sprint onto |
| Right bumper + left stick away from a loose ball | Let it run: no eager regain (see Dribbling and movement) |
| Left stick back with left and right bumpers held | Drag-back |
| Right stick: back, then straight on to a side | Elastico toward that side |
| Right stick: forward flick, rest, back | Heel-to-heel: the ball flicked on straight ahead |
| Right stick: forward flick, rest, a side | Spin (a roulette) toward that side |
| Right stick back alone | Nothing (the start of an elastico) |
| A, X, Y, right bumper + A | GOALS' passes (ground, lofted, through, driven): nothing during play |
| A / Cross | Start the round; at the report, next round (new seed) |
| Y / Triangle | At the report, retry with the same seed |
| D-pad left / right | Previous / next start spot |
| D-pad up / down | Next attacker / next keeper |
| Right stick click | Toggle the debug overlay (shot wedge, aimed and landed markers, keeper reach) |
| View / Share / Back | Flag the most recent finished round (a `flag` line in the session log) |
| Menu / Options / Start | Pause |
| Keyboard `B` | Swap face buttons (A with B, X with Y) for the active pad. On by default for Nintendo / Switch / Pro Controller pads; the choice is saved per pad name |

Skill-move directions are taken against the running direction (the facing when standing), and every side move works to
either side (the attacker draws them from authored clips, see "Character models"). The right stick counts from half deflection; the timings are in the `skill` section of
`data/tuning/default.json`:

- Step-over: the side within 0.25 s of forward, within 30 degrees of square, without passing the stick through rest
  (forward, rest, side is a spin).
- Body feint: from rest, a side held at least a tick and back at rest within 0.1 s. Ball roll: a side held 0.1 s.
- Knock-on: forward past 0.85 deflection while sprinting; it fires 0.15 s after the flick unless the stick goes to a side or
  back first, and a flick released to rest waits until a spin or heel-to-heel can no longer follow (0.125 s after the release).
- Heel-to-heel and spin: from rest a forward flick of at most 0.125 s, rest, then back (heel-to-heel) or a side (spin), within
  0.125 s of the release (`combo_step_window`). Elastico: back, then a side as for the step-over.
- Drag-back: the left stick `drag_back_stick_deg` (120) or more from forward with both bumpers held, on the tick the three come
  together; it replaces the touch controller's own turn at the feet if that began within `drag_back_over_feet_turn` (0.1 s).
- Low shot: hold B for the power wanted, release, and press B again any time after the release and before the strike (the
  strike comes no sooner than 0.45 s after the first press and 0.12 s after the release; see Shooting). The charge is the first press's,
  so a low shot can have full power; bumpers held on either press count. There is no minimum gap between the release and
  the second press.

## Shooting

Five shot types share one model; the modifiers held at any point during the press pick the type at the release: chip if chip
was held, else driven + finesse is a low finesse, then driven, finesse, and power with none. Shoot pressed again after the release and
before the strike (the gamepad's low shot) counts as driven held.

| Type | Flight | Foot |
| --- | --- | --- |
| Power | Loft by charge, a light curl to a random side | Laces |
| Finesse | Curls away from the striking foot, slower | Inside |
| Driven | Flat (about 3.5-5 degrees), 5% faster, a little less accurate, little spin | Laces |
| Low finesse | Finesse curl kept low (1-4 degrees) | Inside |
| Chip | High loft with backspin | Inside |

- Charge fills 0 to 1 in 0.3 s while shoot is held and is frozen at the release (or at 1). The strike comes no sooner than
  0.45 s after the press (the wind-up) and 0.12 s after the release (the earliest strike tick), and it takes the place of the
  attacker's next touch: from the press no dribble touch is committed (one already committed is played), and the strike is
  the touch controller's first eligible contact from the earliest strike tick on (the first tick of a swing's contact window
  that meets the ball), on whichever foot is swinging there (a weak-foot strike, with its wider spread, is the cost of a
  press timed badly). It is committed like a touch one windup before it, on the foot swinging there, and played in that window; from the commit its
  contact tick is fixed. With the ball at the feet at the press (within foot reach, no touch committed) a soft touch keeps it
  there and the strike falls at the earliest strike tick, on the foot chosen at the press, whatever the stride clock's
  swinging foot (the view poses the whole body for the shot itself); if the ball is not strikable then, the next contact
  strikes it. A shot in possession is never dropped: he keeps running on to the ball and strikes on arrival. A shot pressed
  without the ball, or one whose ball the keeper took, is struck as soon as the ball is in reach from the earliest tick, or
  dropped after `round.shoot_buffer`. From the press to the strike the left stick only aims: the legs run on to the ball (the
  go-get-it legs toward its intercept, holding at least the runner's own running speed), whichever way the stick is swung.
  The soft touch leaves the ball rolling along the shooter's velocity.
- The attacker publishes `StrikeEta` and `StrikeFoot` from the press on, as the best estimate: the later of the earliest
  strike tick (while shoot is held, as if it were let go on the next tick) and the controller's forecast contact, then the
  committed strike; once published it may move later (by whole steps, while the run cannot reach the ball) but never more
  than a tick earlier. The shot action's `ContactTick` and `Foot` carry the same estimate, and the keeper reads it.
- Aim is the left stick at the release; a stick at rest then uses its last pushed direction if that was within 0.5 s. It is
  locked from the release to the contact. Aim assist still applies.
- Each distance to the goal centre has an ideal charge (`shot.ideal_charge`; the HUD marks it from `World.IdealCharge`).
  Charging past it lifts the shot and charging short keeps it low, `shot.charge_miss_pitch_deg` degrees per unit of charge;
  a chip's miss changes how far it carries instead (`shot.chip_charge_miss_speed`). Speed grows with charge and `shot_power`.
- Running spoils accuracy: the spread is multiplied by 1 + `shot.speed_multiplier_slope` x (speed / top speed), calibrated so a
  finishing-78 striker at full sprint puts 10-15 m shots on target about 60-75% of the time.
- Side spin grows with launch speed and the `curve` rating (`shot.spin_reference_speed`, `*_spin_rev`).
- After the strike the shooter is stunned for 0.15 + 0.35 x charge seconds: no touches, and his running speed is brought down
  to 40% of top speed no faster than `balance_deceleration` (no speed step); the steering under way eases out.
- The shot is the attacker's `Action` from the press to the end of its 0.25 s follow-through (kind Power, Finesse, Chip,
  Driven or LowFinesse; Windup, Contact, Recovery; `Released` once shoot is let go). The soft touch made inside the wind-up
  is on `TouchAction`.
- The scripted attacker charges like a human: the ideal charge for the ball's distance at the press plus a normal error of
  0.12, drawn from its own generator seeded from the round's seed.
- Every number is in the `shot` section of `data/tuning/default.json`.

## The keeper

The keeper's decisions are in `src/FootballSim.Core/KeeperBrain.cs` (standing, closing down and the rush race in
`KeeperBrain.Standing.cs`, chips in `KeeperBrain.Chip.cs`, the save in `KeeperBrain.Save.cs`, his penalty area's hands rule
and the slide tackle in `KeeperBrain.Feet.cs`), his rating-derived numbers and
geometry in `Keeper.cs` (the save model's in `Keeper.Save.cs`), and every number in the `keeper` section of
`data/tuning/default.json`. Each tick he publishes `KeeperAction`, `KeeperDive`
(set only while Diving), `KeeperActionTime` (0 on the tick the action changes), `Velocity`, `Facing` and `HandPosition`.

- **Hold the arc.** He stands on the bisector of the angle the posts make at the ball, `arc_radius` plus `arc_depth` (by the
  ball's distance from the goal mouth: further out as it comes closer) plus `arc_depth_bearing` (by the ball's bearing from the
  goal's centre line: further out to close down a tight angle), both scaled by positioning, out from the mouth, never closer
  than `arc_min_from_line` to his line. His spot depends only on the ball, so he ends up in the same place wherever he started. `near_post_bias` can turn the line further toward the near post (0 by default: the bisector already sits on the
  near-post side of the ball-to-centre line). A new spot is taken only when the ideal one moves more than
  `reposition_threshold`, with a positioning error drawn then (`position_error`). He side-steps or jogs there with
  rating-based speeds and `move_accel`. Penalties (`keeper_hold_line` in the scenario) keep him on his line until the strike.
- **Close down.** Once an attacker comes at goal with the ball (within `close_down_range` of the mouth, at
  `close_down_min_speed` or more, which a close-control dribble reaches; from then on for the round) he comes off his arc along the cover line to
  `close_down_depth` (by the ball's distance from the mouth, its excess over the arc scaled by `close_down_positioning`), so his
  depth grows as the attacker comes closer. He comes no further than his chip-safe depth as he judges it, no closer than
  `close_down_gap` to the ball and no further than `hold_ground_max_out` from the mouth; between what he wants and those
  limits he keeps the depth he has (no backpedalling while the attacker runs at him). Before closing down starts, an attacker
  on the ball in front of him still makes him keep his depth within those limits rather than drop back to his arc. Moving off
  or toward his line, he does not turn round for a new spot less than `reverse_threshold` the other way from where his
  braking would stop him; he settles there instead. A reposition back toward goal close to it (inside `urgent_range`: drawn
  out or taken round) is run, not jogged. Moving away from the ball faster than `backpedal_show_speed` shows as `Backpedal`.
- **Charge and jockey.** With the attacker on the ball in front of him, once closing down has started and the ball is within
  `charge_range` of the goal centre, he weighs a charge each tick: the time to get to `jockey_distance` of the ball at rush
  speed while the attacker comes on toward him (`Keeper.CloseTime`) against the time he accepts, `charge_time` by rushing_out
  divided by 1 + `charge_chip_weight` (by rushing_out) x the metres the meeting point would take him beyond his judged
  chip-safe depth for the ball's distance now. Beyond that depth by more than `charge_chip_allowance` (by rushing_out;
  `charge_chip_hysteresis` more once going) he does not go: the chip is a risk he weighs, and a brave keeper takes more of it.
  Within `engage_range` of the ball he always charges. Charging, he runs at rush speed to the point on the cover line
  `jockey_distance` short of the ball (never beyond the area's front line), then jockeys there: low and spread, stepping with the
  attacker to stay on the cover line, giving ground no faster than `jockey_retreat_speed`, so an attacker who runs at him
  closes the gap. He drops the charge once the time to close exceeds `charge_drop_factor` times what he accepts (plus 0.25 s),
  for an attacker who stops or turns away, and recovers to his limits as when closing down. With the ball up to
  `charge_hold_margin` beyond the range and a charge he would make, he keeps his depth instead of giving ground first. Shown as
  `Charge` (the run cycle) while more than a metre beyond jockeying range, then `Jockey`.
- **Chip-safe depth.** The furthest he can stand off his line and still recover from a chip struck from the ball's distance:
  the attacker's chip at the ideal charge for that distance (`ShotModel`, mean backspin, straight at goal) either passes
  within his leap (`chip_reach_height_fraction` of his height) or he can backpedal (`backpedal_speed` by positioning, from his
  mean reaction) to within `chip_tip_reach` of where it has dropped to `chip_take_height_fraction` of his height,
  `chip_leap_lead` before it gets there. From close in an ideal chip cannot rise over him, so the limit only binds from
  about 13 m out. He judges it with an error drawn once a round (`chip_judge_error`, by positioning).
- **His penalty area.** He may use his hands only on a ball in his penalty area, judged by where the ball is at the contact
  (any part of it on or over the line). A hand touch on a ball outside it is refused: his hands are pulled away for the rest of
  that save and the ball carries on untouched; his body, a foot block or a smother outside the area block the ball and never
  hold it. He never plans a hand save outside the area: a point on a shot's path outside it counts only for a foot block or a
  reflex foot or body block, and a save that would end up outside it is played as one of those. For a loose ball outside the
  area he plays with his feet (`Slide`): he races for it as for any loose ball (below), up to `slide_beyond_area` beyond the
  area's line, and within `slide_reach` he goes into a slide tackle, feet first along the ground, the leading foot moving from
  `slide_foot_start` ahead of him at `slide_speed`, the body following `slide_body_follow` of it. A bouncing ball (above
  `slide_max_height`) or one within `poke_reach` he pokes at standing instead (shown as `Diving` with `KeeperDive.Feet`, at
  `poke_speed`, set again after `poke_seconds`). A slide or poke that meets the ball knocks it on along the slide's direction,
  within `slide_knock_angle_deg`, at `slide_knock_speed_min`..`_max` (`SaveKind.Slide`, or `Block` for a poke); a slide ends
  Grounded and he gets up as after a dive (after `slide_max_seconds` at most). A close race for a ball outside the area
  (within his anticipation) brings him to the area's line and no further (`KeeperMode.HoldArea`); a race he cannot win sends him
  back to his spot. A smother whose ball leaves the area before he has it becomes a slide or poke at it when it is low and
  within `slide_reach`, else he abandons it on the ground. Inside the area he plays as below. `KeeperReport.Slid` records a
  slide or poke before the first strike. Inside the area his rushes and charges reach out to the area's front line (its depth from the goal
  centre), so he uses all of it.
- **Rush and smother.** Each tick he races for the ball whenever it is off the attacker's foot (between touches, a long sprint
  touch or a loose ball): the earliest point on its predicted path he can reach (to within `smother_reach`) before it gets
  there and before the attacker's next planned touch, with a `rush_margin` (by distance, scaled by rushing_out; none for a
  ball already within a dive, so he lunges at a ball pushed past him, unless the attacker is within `contested_distance` of
  it) and a misjudgement drawn per attacker touch
  (`rush_misjudge`), only within his area's depth of the goal centre (outside it, `slide_beyond_area` past its line). The longer the ball runs ahead of the attacker, the further out he wins
  it. A race short of the margin by less than `rush_anticipation` (by rushing_out) starts him toward the ball at
  `anticipate_speed_fraction` of his rush speed; he aborts back to his spot if it turns against him. A close-control dribbler
  (a touch every step) gives him no such race. Movement targets are capped at `move_target_cap`. Within a dive he smothers the
  ball and holds it if slower than `smother_hold_speed`.
- **Stay big.** Within `pre_spread_range` of an attacker on the ball in front of him that he cannot beat to it and is not
  charging (closing down has not started), he stops and spreads (`Spread`); once the ball is level with him or past, he goes
  back to covering the goal (and recovers toward it).
- **Set.** In the last `plant_lead` seconds of a shot's wind-up (ball within `set_range`; earlier if he needs longer to brake
  to a stop) he plants, braking at `plant_decel`, so he stops advancing. Charging into a wind-up, he keeps coming to block it
  if, braking only in the last `charge_plant_lead` seconds, he would be within `charge_block_distance` of the ball at the
  strike (he may be caught still moving); otherwise he pulls up at once. Within `pre_spread_range` of the shooter it shows as
  `Spread` (and stays `Spread`, not `Reacting`, after the strike while he is at least half ready).
- **Ready stance.** In his stance for the shot at the strike (planted, still braking into it, spread or jockeying) he is ready
  by his distance from the ball: fully within `ready_full` (GOALS MaxDistanceForFullyReadyStance 3.5 m), not at all from
  `ready_range`. His standing body, the capsule the ball can hit from the shins to the shoulders until he moves, is his hull:
  `body_radius` not ready, growing to `hull_radius` (0.42 m: his torso with spread legs and arms, averaged) fully ready, so a
  set keeper close to the shooter is bigger low and high. The rest of his close-range cover is the reflex. Still at the strike, he also holds his hands `hand_span` out to each side at
  `hand_span_height_fraction` of his height (a ball reaching them before he moves is stopped); his reach grows from the span,
  or from his hull's edge when that is further, rather than from zero.
- **Reflex and reaction.** From the contact he has a reflex (`reflex_time` by reflexes) and, later, his full reaction
  (`reaction`; both with `reaction_noise`; moving at the strike adds `still_moving_reaction_penalty` to the reaction only). At
  the reflex he plans both saves on the ball's predicted path: if his reaction and dive get to it in time he waits for them;
  otherwise, if a reflex limb gets to it, he makes a reflex save now (`SaveType.Reflex`); if neither does, a reflex attempt
  (`ReflexAttempt`) when the ball passes inside his save hull (`save_hull_radius`, GOALS CapsuleRadius 0.75 m), or when the
  reflex gets closer than the dive, within `reflex_attempt_margin` of its range. A reflex limb starts where his
  stance holds it (a hand at its span, a foot at `stance_foot_offset`, the body at his hull's edge), moves out after its
  execution time (`reflex_execution_easy`..`_hard` by the save's difficulty) at `reflex_speed` to at most `reflex_range`
  (reach, by height), aimed no higher than `reflex_height_fraction` of his height, the body following `reflex_body_follow` of
  it: a foot for a ball below `reflex_foot_height` (`KeeperDive.ReflexFoot`), the body when he was at least half ready and it is
  below `reflex_body_height` (`BodyBlock`), else a hand (`ReflexHand`). He stays on his feet.
- **Dive.** At his reaction he dives at the first point on the ball's predicted path inside his dive wedge (`wedge_*`) that his
  reach gets to in time (`arm_sweep` over `arm_sweep_time`, then `dive_speed` once the save's execution time has passed, up to
  `stretch`), after a random delay up to `save_time_jitter_easy`..`_hard` by its difficulty; if none, at the point he gets
  closest to (`FailedAttempt`). A ball that will hit his standing body anyway is blocked standing. He goes for a catch when the
  ball's speed and the time he will have put the target inside his catch range, else a parry.
- **Difficulty.** A save's difficulty is the lateral range it needs as a fraction of his stretch through `difficulty_low`,
  `difficulty_mid` or `difficulty_high` by the ball's height (linear between `difficulty_*_height`), plus
  `counter_momentum_penalty` by his speed away from it (moving the wrong way). It sets the execution time (`save_execution_easy`
  to `_hard` by reflexes, GOALS SaveExecutionTimeEasy/Hard), the start delay's spread, and what happens at contact.
- **Contact.** At the hands he holds a ball within his catch range for its speed (`catch_range_standing_min`..`_max` or
  `catch_range_diving_min`..`_max` by handling; at the feet or after backpedalling as for a ball `feet_catch_factor` or
  `backpedal_catch_factor` slower in effect) scaled by `catch_range_time` for the seconds since the strike, and no harder than
  `catch_max_difficulty`; his body or a smother holds a ball slower than `body_hold_speed` or `smother_hold_speed`. A reflex
  limb's touch that is not held can be loose (GOALS' uncontrolled reflex deflections), with a chance of `reflex_loose_edge` by
  how off-centre the ball meets the limb (0 head-on, 1 a graze) plus `reflex_loose_difficulty` by the save's difficulty, times
  `reflex_loose_speed` by the ball's speed and `reflex_loose_rating` by the mean of handling and reflexes: the ball keeps
  `reflex_loose_speed_min`..`_max` of its speed and bends away from the limb by a half to the whole of
  `reflex_loose_max_bend_deg` (down to `reflex_loose_graze_bend` of it for a graze), so it can still go in. Otherwise
  his body, feet, a smother, a reflex foot or body block, or a ball that meets him before he moves blocks it (a quarter to
  near half the speed, at least `block_speed`); high near his line he tips it over the bar (above `tip_over_bar_height` of the
  goal height within `tip_over_bar_distance` of his line); a dive at least `fingertip_difficulty` hard fingertips it
  (`fingertip_speed_kept`, bent away from goal by up to `fingertip_max_bend_deg`; it can still go in); else he parries it
  (40-60% of the speed, at least `min_parry_speed`, steered wide by handling, less so and with a wider error the harder the
  save).
- **Chips.** After his reaction he reads the struck ball's flight (never the shot type): on target and passing over him above
  his leap, he backpedals (`Backpedal`) facing back up the pitch, to the first point on its path where it has dropped to his
  take height that he can reach in time (else to where it crosses the line), and leaps for it once his reach gets there in
  time: a backward leap (`KeeperDive.Back`: springing up and back, arms over his head toward the ball, falling back toward his
  goal) that tips it over the bar or, below `backpedal_catch_factor` of his diving catch speed, catches it. No dive target is
  above his leap.
- **Recover.** After a dive or smother he lies for `grounded_seconds`, then gets up in `get_up_seconds`; a new save can start
  `get_up_save_after` into getting up.

The batch tool prints, under the scoring table, the keeper's distance off his line and the ball's distance from goal at the
strike (mean, p10, p90), what became of parried first shots, the share of rounds with a charge, a rush or a
smother before the shot, how often he was planted at the strike, his action at the strike (with his speed band), his mean
reaction, the saves and dives he made, the first shot's save counts (catch, parry, block, tip, fingertip, smother, loose reflex, beaten)
and save types (catch, parry, reflex, reflex attempt, failed attempt, none: met before he moved), with the mean difficulty and
how often and how ready he was at the strike; then his
mean depth off his line as the attacker on the ball first came within 20, 15, 10 and 6 m of the goal centre, the rounds he won
the ball from a dribble (touched it before any shot), the rounds he was taken round, how often his movement off or toward
his line changed direction before the shot (above 0.3 m/s held 0.1 s; mean, p95 and max, over the round and its first 2 s),
and the chips tried, scored and tipped over. `--counters off` makes the scripted attacker ignore the keeper; `--close-down off`
runs the shallow keeper (`close_down_range` 0: he holds his arc and never charges). A first shot touched by the keeper that carries on into the net counts as scored.

The scripted attacker (batch and tests) reacts to the keeper as a player would see him: jogging touches once he is within 9 m
of the ball and close control within 6 m, taking round a keeper rushing or charging out at it within 5 m (holding the shot until past
him), and, with a chance that grows with composure, chipping a keeper standing further off his line than his chip-safe depth
(plus 0.5 m) from within 25 m.

The keeper probes drive the attacker like a human, through the replay path (one intent per tick from what a player sees, no
scripted attacker), over many seeds: `dotnet run -c Release --project src/FootballSim.Batch -- --keeper-probe [--seeds 40]
[--rushing 1,50,99] [--only sprint,jog,close,...] [--timeline 1] [--every 30]`. Plans: `sprint`, `jog` and `close_straight`
run straight at goal from the back of the arena and never shoot; `close` approaches under close control and, with the keeper
within 3 m, shifts the ball across him and shoots low at the open side; `angled` sprints in from wide; `sprint_slow` sprints
then jogs under close control within 8 m of him; `knock` adds a knock-on 20 m out; `step_over`, `drag_back` and `ball_roll`
make the move when he commits (charging or rushing within 4 m, or within 2.5 m) and shoot once past him; `chip` jogs in and
chips once he comes out. Each row prints how the rounds ended (won = he touched the ball before any shot), how close he was to
the ball when the attacker was 10 m out, the seconds spent 6-8, 3-5 and 1.5-3 m from the ball, his depth at 20/15/10/6 m and
the share of his decisions by mode, depth limit and rush race (`World.KeeperThought`, a per-tick diagnostic); `--timeline N`
prints the first N seeds every 0.25 s. `--probe-replays <dir|file>` prints that timeline for play-test replays
(`sessions/<folder>/round-*.json`), and `--write-probe-replays <dir>` writes showcase replays (a sprint he charges and wins, a
close-control approach he jockeys and pounces on, a step-over that beats him, a chip over him) for screenshots.

Player, tuning and scenario files are re-read every time a round is set up, so editing a JSON file in `data/` shows up next round.
A file that fails to load keeps its last good version and the HUD warning line names the file and field.

## Keyboard fallback

Works alongside a gamepad (the larger stick value wins) and when none is detected. Ignored in replay mode.

GOALS steers with the mouse on PC and uses W/A/S/D as buttons; this keyboard runs on WASD instead and keeps every other GOALS
default that does not clash with that (taken from GOALS' `IMC_Playing` mapping, build 2.01.07). Keys marked "ours" are not
GOALS bindings.

| Key | Action | GOALS default |
| --- | --- | --- |
| WASD | Run and aim (W = toward the far touchline, D = toward the goal) | Mouse steers; W/A/S/D are buttons |
| Ctrl | Sprint (either Ctrl; GOALS: Left Ctrl) | Left Ctrl |
| Q | Shield (close control) | W |
| Left mouse (hold, release) | Shoot; pressed again after the release, before the strike, a low shot | Same |
| E | Curved (finesse) shot, held while shooting | D |
| Left Shift | Chip, held while shooting | Same |
| Right mouse | Cancel: pressed after shoot and before the strike, a fake shot | Same (also the ground pass) |
| Back against the run + E + Left Shift | Drag-back, as the pad's left stick back with both bumpers | Back + D + Left Shift |
| A / S | GOALS' through / lofted pass: run left / back here | Through / lofted pass |
| Space, G | Shoot and cancel again, for keyboard-only play (ours) | Space toggles the camera |
| X | Knock-on (ours; GOALS uses the right stick, middle mouse + mouse on PC) | None |
| 1 / 2 / 3 / 4 | Step-over / body feint / ball roll / drag-back, side from A/D, right by default (ours) | None |
| Enter | Start the round; at the report, next round | |
| Backspace | At the report, retry with the same seed | |
| Left / Right arrow | Previous / next start spot (the pad's D-pad) | D-pad |
| Up / Down arrow | Next attacker / next keeper (the pad's D-pad) | D-pad |
| O | Toggle the debug overlay | |
| M | Flag the most recent finished round | |
| P or Esc | Pause | Esc opens the menu |
| B | Swap the pad's face buttons (see above) | |
| H | Show or hide the controls panel | Hero select (not built) |

GOALS keys not used here: R (its left stick click action), F (switch set-piece taker), Tab (match menu),
middle mouse (hold to use the mouse as the right stick); its H (hero select) opens the controls panel. There is no driven-shot key: as on the pad, the low shot is shoot
pressed twice.

## Session logs

Every launch except replay mode (unless it is given `--session-log`) creates `sessions/<yyyyMMdd-HHmmss>/` at the repo root (ignored by git):

- `session.jsonl`: one JSON object per line, flushed after each line, each with `t` (seconds since launch), `wall` (local time) and `type`:
  `start`, `pads`, `input_mode`, `round_setup`, `round_begin`, `round_end` (with the full round report), `flag`, `selection`, `warning`, `pause`, `quit`,
  and `skill` each time the attacker's figure starts drawing a skill move (round, world `tick`, `move`, `foot`, `side`, `stand_clip` and `run_clip`, `w_run`,
  `entry_speed`, and `drawing`: `clips` or `procedural`), so a play-test shows what was drawn.
- `round-<NNN>-<scenario>-<outcome>.json`: the replay of every finished round, saved automatically. Re-run one with
  `dotnet run -c Release --project src/FootballSim.Batch -- --replay sessions/<folder>/round-007-penalty-goal.json`.

The HUD shows the round number ("Round 7 · Penalty · ..."). A retry gets a new round number; its `round_setup` line has `retry_of` and the same seed.
Each finished round also prints a line such as `ROUND 7 GOAL penalty` to the console. Flag marks a round to look at later; it writes a `flag` line and shows "Flagged round 7".

## Controller not detected on macOS

If the HUD says "No gamepad detected", macOS is not listing the controller as a game controller. A pad in XInput / "Xbox 360 for Windows" mode is not
recognised by macOS. Switch the controller to its Bluetooth, Switch, PlayStation or Apple mode. The `pads` lines in `session.jsonl` record what Godot saw.

## Players

Attacker and keeper are jointed low-poly figures built from primitives (pelvis, torso, head, arms, legs with two-bone IK) and animated procedurally from the simulation's state and events only (`game/PlayerFigure.cs`): run cycle with planted stance feet, dribble touches, shot wind-up, strike and follow-through, keeper set stance, dive and hold.
A ring under the attacker is solid green while in possession and thin grey otherwise; the ball's ground ring is green while a player is in possession and orange while it is loose.
