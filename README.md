# Football Sim

A 1v1 football shooting game: a simulation core in C# (`src/`), game data in JSON (`data/`) and a Godot 4.7 (.NET) shell (`game/`).
World coordinates are metres: X toward the attacked goal (goal line 52.5), Y up, Z across (the camera stands on the +Z side).

## Commands (run from the repo root)

| What | Command |
| --- | --- |
| Play (needs a gamepad) | `godot-mono --path game` |
| Build the game project | `dotnet build game/FootballSim.Game.csproj` |
| Tests | `dotnet test FootballSim.slnx` |
| Batch run, 10 000 scripted rounds | `dotnet run -c Release --project src/FootballSim.Batch -- --scenario penalty --n 10000 --seed 1` |
| Save a scripted round as a replay | `dotnet run -c Release --project src/FootballSim.Batch -- --scenario breakaway --seed 6 --save-replay game/tests/smoke_replay.json` |
| Re-run a replay in the batch tool | `dotnet run -c Release --project src/FootballSim.Batch -- --replay game/tests/smoke_replay.json` |
| Skill-move probe (scripted gestures per move, side, entry pace and skill_moves 1/50/99; the not-built elastico, heel-to-heel and spin rows should read recognised 100, made 0) | `dotnet run -c Release --project src/FootballSim.Batch -- --skill-probe` |
| Write one replay per skill move (edge_of_box start) for screenshots | `dotnet run -c Release --project src/FootballSim.Batch -- --write-skill-replays out/skill-replays` |
| Write one replay per visible keeper action (shuffle, rush, planted then reacting, each dive kind, tip over the bar, smother, closing down, spread, a long sprint touch won, a backpedal under a chip, taken round, parry then grounded then getting up) for screenshots; prints each file with the tick range of its action | `dotnet run -c Release --project src/FootballSim.Batch -- --write-keeper-replays out/keeper-replays` |
| Print the keeper's state tick by tick for one scripted round | `dotnet run -c Release --project src/FootballSim.Batch -- --scenario tight_angle --trace-keeper 1` |
| Smoke run: replay through the game scene, no window | `godot-mono --headless --path game -- --replay tests/smoke_replay.json --quit-after-round` |
| Smoke comparison (game report vs batch report) | `game/tests/smoke.sh` |
| Screenshot run (opens a window briefly) | `godot-mono --path game -- --replay tests/smoke_replay.json --shots out/replay --shot-on setup,charge,flight,outcome --quit-after-round` |
| Screenshot run without the window taking focus (use this from tools and agents) | `game/tests/run-bg.sh -- --replay tests/smoke_replay.json --shots out/replay --shot-on setup,strike,outcome --quit-after-round` |
| Keeper gallery: every keeper state and dive kind from a script (both sides, side and front views), no simulation | `game/tests/run-bg.sh -- --keeper-gallery out/keeper-gallery` |

The smoke run prints the round report on one line prefixed `REPORT ` and exits 0. `smoke.sh` checks it is identical to the batch tool's report.
Screenshots go to `game/out/` (ignored by git).

Game options after `--`: `--data <dir>` (default `data/`), `--replay <file>`, `--quit-after-round`, `--quit-after-ticks N`,
`--shots <dir>` with `--shot-ticks a,b,c`, `--shot-on setup,live,charge,strike,flight,outcome,touch<N>` (the N-th touch) or `keeper` (a burst of frames whenever the keeper's action changes; pair with `--close-keeper`), or `--shot-burst <startTick>:<count>:<step>`, `--close-cam` / `--close-keeper` (close-up cameras for inspecting limbs), `--frame-times`, `--figure skinned|procedural` (default `skinned`: rigged human models; `procedural` keeps the primitive meshes; a missing or unloadable model falls back to procedural with a HUD warning), and `--background` (macOS: hide the app at startup and opt out of App Nap so a windowed run never takes focus; use it through `game/tests/run-bg.sh`, which also keeps the window from flashing in front while Godot starts and prints the game's output).

Automated tools and agents must use `game/tests/run-bg.sh` for any windowed run (anything with `--shots`); headless runs need no wrapper.

In replay mode the game also prints `FOOT_EVENT` per touch/strike and, on quit, `FOOT_CONTACT n= mean_m= max_m=` (touching foot's instep to the ball surface at each Touch/Strike) and `FOOT_SLIDE n= mean_m= max_m=` (movement of a planted foot per stance). With `--figure skinned` it also prints `SKIN_CONTACT n= mean_m= max_m=` (the skinned boot's contact surface for the touch's surface — inside, laces, outside or sole — to the ball surface at each touch's contact tick; target max <= 0.03 m), `FOOT_JUMP ctx=<skill|exit|touch|shot|other|all>` lines give, per context of the attacker's tick (a skill move, from a move's end until the first touch after it has recovered, a dribble touch, a shot, anything else), the largest and the 99th-percentile world move of either foot (the larger of its ankle's and boot tip's) in one physics tick (`dt_s`, one rendered frame at 120 Hz) and over two ticks (`max60_m`, one frame at 60 Hz), with the tick and foot of the largest; and `POSE_LIMITS` carries the running-only `run_min_pelvis_ratio`, `run_lean_max_deg` and `run_bob_max_m`; the dribble probe behind these targets is `dotnet run -c Release --project src/FootballSim.Batch -- --probe dribble`.

`--skill-probe` prints, per move: gesture recognised, move made, possession kept 2 s after the move, the time from the move's end to 90% of the with-ball top speed with sprint held (`t90_s`), the same for the same turn without the move (`base_t90_s`, both medians), the ball's distance at the move's end, for a step-over or feint the ball's distance ahead of the player at the start, when its own motion begins and when the foot passes over it (`start_ahead_m`, `exec_ahead_m`, `mid_ahead_m`), and for a sole move the ball centre's distance from the foot's contact point when the sole lands (`sole_miss_m`), each as mean/max; `--seeds`, `--ratings` and `--only step_over,...` narrow it.
Player sheets carry a `skill_moves` rating (1-99): a skill move's exit burst grows with it, and each move chained within 2 s of the previous one uses 0.8 times the previous move's rating. A sheet without it uses its `dribbling`. Skill-move timings and the burst live in the `skill` section of `data/tuning/default.json`.

## Character models

The players are rigged MPFB2/MakeHuman bodies (`game/assets/characters/player.glb`, `keeper.glb`, Mixamo-compatible skeleton, CC0 assets only).
`PlayerFigure` still computes every joint procedurally each physics tick; `SkinnedBody` poses the skeleton from those joints (knees, ankles, elbows
and hands land on them), tints clothing and skin from `FigureKit` and puts the shirt number on the back. Foot-contact and pose measurements come
from the joints, so they are the same with either `--figure` value. Build steps, asset list and licences: `game/assets/characters/SOURCES.md`.
A fresh checkout needs `godot-mono --headless --path game --import` once.

## Gamepad controls

The pad follows GOALS' Standard preset (Xbox names). Inputs for moves not built yet are recognised and ignored, so they never
set off a different move.

| Control | Action |
| --- | --- |
| Left stick | Run (stick right = toward the goal) and aim |
| Right trigger | Sprint |
| Left trigger (past half) | Shield (close control) |
| B / Circle (hold, release) | Power shot: power builds while held (full in 0.3 s) |
| Right bumper + B | Curved (finesse) shot |
| Left bumper + B | Chip |
| B (hold for power, release), then B again before the strike | Low (driven) shot; right bumper + B, B is a low curved shot; left bumper + B, B (GOALS' low chip, not built) is a chip |
| B, then A / Cross within 0.1 s | Fake shot: left stick at rest a fake stop, pushed a fake cut that way |
| Right stick: forward, then straight on to a side | Step-over to that side |
| Right stick: a side flicked from rest and released | Body feint to that side |
| Right stick: a side held 0.1 s | Ball roll to that side |
| Right stick: forward flick while sprinting | Knock-on: a heavy touch ahead to sprint onto |
| Left stick back with left and right bumpers held | Drag-back |
| Right stick: back, then straight on to a side | Elastico: recognised, not built (nothing happens) |
| Right stick: forward flick, rest, back | Heel-to-heel: recognised, not built |
| Right stick: forward flick, rest, a side | Spin: recognised, not built |
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
either side. The right stick counts from half deflection; the timings are in the `skill` section of
`data/tuning/default.json`:

- Step-over: the side within 0.25 s of forward, within 30 degrees of square, without passing the stick through rest
  (forward, rest, side is a spin).
- Body feint: from rest, a side held at least a tick and back at rest within 0.1 s. Ball roll: a side held 0.1 s.
- Knock-on: forward past 0.85 deflection while sprinting; it fires 0.15 s after the flick unless the stick goes to a side or
  back first, and a flick released to rest waits until a spin or heel-to-heel can no longer follow (0.125 s after the release).
- Heel-to-heel and spin: from rest a forward flick of at most 0.125 s, rest, then back (heel-to-heel) or a side (spin), within
  0.125 s of the release (`combo_step_window`). Elastico: back, then a side as for the step-over.
- Low shot: hold B for the power wanted, release, and press B again any time after the release and before the strike (the
  contact comes no sooner than 0.45 s after the first press and 0.12 s after the release). The charge is the first press's,
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

- Charge fills 0 to 1 in 0.3 s while shoot is held and is frozen at the release (or at 1). Contact comes no sooner than 0.45 s
  after the press (the wind-up) and 0.12 s after the release; a ball still out of reach then is struck as soon as it is in
  reach, or the shot is dropped after `round.shoot_buffer`. In possession the ball is kept at the striking foot through the
  wind-up by a soft touch.
- Aim is the left stick at the release; a stick at rest then uses its last pushed direction if that was within 0.5 s. It is
  locked from the release to the contact. Aim assist still applies.
- Each distance to the goal centre has an ideal charge (`shot.ideal_charge`; the HUD marks it from `World.IdealCharge`).
  Charging past it lifts the shot and charging short keeps it low, `shot.charge_miss_pitch_deg` degrees per unit of charge;
  a chip's miss changes how far it carries instead (`shot.chip_charge_miss_speed`). Speed grows with charge and `shot_power`.
- Running spoils accuracy: the spread is multiplied by 1 + `shot.speed_multiplier_slope` x (speed / top speed), calibrated so a
  finishing-78 striker at full sprint puts 10-15 m shots on target about 60-75% of the time.
- Side spin grows with launch speed and the `curve` rating (`shot.spin_reference_speed`, `*_spin_rev`).
- After the strike the shooter is stunned for 0.15 + 0.35 x charge seconds: speed capped at 40% of top speed, no touches.
- The shot is the attacker's `Action` from the press to the end of its 0.25 s follow-through (kind Power, Finesse, Chip,
  Driven or LowFinesse; Windup, Contact, Recovery; `Released` once shoot is let go). The soft touch made inside the wind-up
  is on `TouchAction`.
- The scripted attacker charges like a human: the ideal charge for the ball's distance at the press plus a normal error of
  0.12, drawn from its own generator seeded from the round's seed.
- Every number is in the `shot` section of `data/tuning/default.json`.

## The keeper

The keeper's decisions are in `src/FootballSim.Core/KeeperBrain.cs` (standing, closing down and the rush race in
`KeeperBrain.Standing.cs`, chips in `KeeperBrain.Chip.cs`), his rating-derived numbers and geometry in `Keeper.cs`,
and every number in the `keeper` section of `data/tuning/default.json`. Each tick he publishes `KeeperAction`, `KeeperDive`
(set only while Diving), `KeeperActionTime` (0 on the tick the action changes), `Velocity`, `Facing` and `HandPosition`.

- **Hold the arc.** He stands on the bisector of the angle the posts make at the ball, `arc_radius` plus `arc_depth` (by the
  ball's distance from the goal mouth: further out as it comes closer) plus `arc_depth_bearing` (by the ball's bearing from the
  goal's centre line: further out to close down a tight angle), both scaled by positioning, out from the mouth, never closer
  than `arc_min_from_line` to his line. His spot depends only on the ball, so he ends up in the same place wherever he started. `near_post_bias` can turn the line further toward the near post (0 by default: the bisector already sits on the
  near-post side of the ball-to-centre line). A new spot is taken only when the ideal one moves more than
  `reposition_threshold`, with a positioning error drawn then (`position_error`). He side-steps or jogs there with
  rating-based speeds and `move_accel`. Penalties (`keeper_hold_line` in the scenario) keep him on his line until the strike.
- **Close down.** Once an attacker comes at goal with the ball (within `close_down_range` of the mouth, at
  `close_down_min_speed` or more; from then on for the round) he comes off his arc along the cover line to
  `close_down_depth` (by the ball's distance from the mouth, its excess over the arc scaled by `close_down_positioning`), so his
  depth grows as the attacker comes closer. He comes no further than his chip-safe depth as he judges it, no closer than
  `close_down_gap` to the ball and no further than `hold_ground_max_out` from the mouth; between what he wants and those
  limits he keeps the depth he has (no backpedalling while the attacker runs at him). Before closing down starts, an attacker
  on the ball in front of him still makes him keep his depth within those limits rather than drop back to his arc. Moving off
  or toward his line, he does not turn round for a new spot less than `reverse_threshold` the other way from where his
  braking would stop him; he settles there instead. With the attacker on the ball within
  `engage_range` of him and in front, he pushes up to just inside `pre_spread_range` of the ball at rush speed. A reposition
  back toward goal close to it (inside `urgent_range`: drawn out or taken round) is run, not jogged. Moving away from the ball
  faster than `backpedal_show_speed` shows as `Backpedal`.
- **Chip-safe depth.** The furthest he can stand off his line and still recover from a chip struck from the ball's distance:
  the attacker's chip at the ideal charge for that distance (`ShotModel`, mean backspin, straight at goal) either passes
  within his leap (`chip_reach_height_fraction` of his height) or he can backpedal (`backpedal_speed` by positioning, from his
  mean reaction) to within `chip_tip_reach` of where it has dropped to `chip_take_height_fraction` of his height,
  `chip_leap_lead` before it gets there. From close in an ideal chip cannot rise over him, so the limit only binds from
  about 13 m out. He judges it with an error drawn once a round (`chip_judge_error`, by positioning).
- **Rush and smother.** Each tick he races for the ball whenever it is off the attacker's foot (between touches, a long sprint
  touch or a loose ball): the earliest point on its predicted path he can reach (to within `smother_reach`) before it gets
  there and before the attacker's next planned touch, with a `rush_margin` (by distance, scaled by rushing_out; none for a
  ball already within a dive, so he lunges at a ball pushed past him) and a misjudgement drawn per attacker touch
  (`rush_misjudge`), only within `rush_max_from_goal`. The longer the ball runs ahead of the attacker, the further out he wins
  it. A race short of the margin by less than `rush_anticipation` (by rushing_out) starts him toward the ball at
  `anticipate_speed_fraction` of his rush speed; he aborts back to his spot if it turns against him. A close-control dribbler
  (a touch every step) gives him no such race. Movement targets are capped at `move_target_cap`. Within a dive he smothers the
  ball and holds it if slower than `smother_hold_speed`.
- **Stay big.** Within `pre_spread_range` of an attacker on the ball in front of him that he cannot beat to it, he stops and
  spreads (`Spread`); once the ball is level with him or past, he goes back to covering the goal (and recovers toward it).
- **Set.** In the last `plant_lead` seconds of a shot's wind-up (ball within `set_range`; earlier if he needs longer to brake
  to a stop) he plants, braking at `plant_decel`, so he stops advancing. Planted (or spread) close to the shooter he spreads
  (`spread_*`), which widens the body the ball can hit; within `pre_spread_range` it shows as `Spread`.
- **Set stance.** Still at the strike, he holds his hands `hand_span` out to each side at `hand_span_height_fraction` of his
  height: a ball reaching them before he reacts is stopped, and his reach grows from the span rather than from zero.
- **React and save.** From the contact he reacts after a reflexes-based delay (`reaction`, `reaction_noise`), plus
  `still_moving_reaction_penalty` if he was still moving. He then dives at the first point on the ball's predicted path inside
  his dive wedge (`wedge_*`) that his reach (`arm_sweep`, `dive_speed`, `stretch`) gets to in time, after a random
  `save_time_jitter_*` delay; if none, at the point he gets closest to. A ball that will hit his body anyway is blocked
  standing. He catches below the catch speed (handling; `diving_catch_factor` in a dive) inside `catch_zone_fraction` of his
  stretch, otherwise parries (40-60% of the speed, at least `min_parry_speed`), blocks at the feet (at least `block_speed`),
  tips over the bar (above `tip_over_bar_height` of the goal height within `tip_over_bar_distance` of his line) or, at the
  outer `fingertip_zone_fraction` of his stretch, fingertips it (`fingertip_speed_kept`, bent away from goal by up to
  `fingertip_max_bend_deg`; it can still go in).
- **Chips.** After his reaction he reads the struck ball's flight (never the shot type): on target and passing over him above
  his leap, he backpedals (`Backpedal`) facing back up the pitch, to the first point on its path where it has dropped to his
  take height that he can reach in time (else to where it crosses the line), and leaps for it once his reach gets there in
  time: a backward leap (`KeeperDive.Back`: springing up and back, arms over his head toward the ball, falling back toward his
  goal) that tips it over the bar or, below `backpedal_catch_factor` of his diving catch speed, catches it. No dive target is
  above his leap.
- **Recover.** After a dive or smother he lies for `grounded_seconds`, then gets up in `get_up_seconds`; a new save can start
  `get_up_save_after` into getting up.

The batch tool prints, under the scoring table, the keeper's distance off his line and the ball's distance from goal at the
strike (mean, p10, p90), what became of parried first shots, the share of rounds with a rush or a
smother before the shot, how often he was planted at the strike, his mean reaction, and the saves and dives he made; then his
mean depth off his line as the attacker on the ball first came within 20, 15, 10 and 6 m of the goal centre, the rounds he won
the ball from a dribble (touched it before any shot), the rounds he was taken round, how often his movement off or toward
his line changed direction before the shot (above 0.3 m/s held 0.1 s; mean, p95 and max, over the round and its first 2 s),
and the chips tried, scored and tipped over. `--counters off` makes the scripted attacker ignore the keeper. A first shot touched by the keeper that carries on into the net counts as scored.

The scripted attacker (batch and tests) reacts to the keeper as a player would see him: jogging touches once he is within 9 m
of the ball and close control within 6 m, taking round a keeper rushing out at it within 5 m (holding the shot until past
him), and, with a chance that grows with composure, chipping a keeper standing further off his line than his chip-safe depth
(plus 0.5 m) from within 25 m.

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
| Left Ctrl | Sprint | Same |
| Q | Shield (close control) | W |
| Left mouse (hold, release) | Shoot; pressed again after the release, before the strike, a low shot | Same |
| E | Curved (finesse) shot, held while shooting | D |
| Left Shift | Chip, held while shooting | Same |
| Right mouse | Cancel: pressed within 0.1 s of shoot, a fake shot | Same (also the ground pass) |
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

GOALS keys not used here: R (its left stick click action), F (switch set-piece taker), Tab (match menu), H (hero select),
middle mouse (hold to use the mouse as the right stick). There is no driven-shot key: as on the pad, the low shot is shoot
pressed twice.

## Session logs

Every launch except replay mode creates `sessions/<yyyyMMdd-HHmmss>/` at the repo root (ignored by git):

- `session.jsonl`: one JSON object per line, flushed after each line, each with `t` (seconds since launch), `wall` (local time) and `type`:
  `start`, `pads`, `input_mode`, `round_setup`, `round_begin`, `round_end` (with the full round report), `flag`, `selection`, `warning`, `pause`, `quit`.
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
