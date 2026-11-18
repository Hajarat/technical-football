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
| Skill-move probe (scripted right-stick gestures per move, side, entry pace and skill_moves 1/50/99) | `dotnet run -c Release --project src/FootballSim.Batch -- --skill-probe` |
| Write one replay per skill move (edge_of_box start) for screenshots | `dotnet run -c Release --project src/FootballSim.Batch -- --write-skill-replays out/skill-replays` |
| Write one replay per visible keeper action (shuffle, rush, planted then reacting, each dive kind, tip over the bar, smother, parry then grounded then getting up) for screenshots; prints each file with the tick range of its action | `dotnet run -c Release --project src/FootballSim.Batch -- --write-keeper-replays out/keeper-replays` |
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

| Control | Action |
| --- | --- |
| Left stick | Run (stick right = toward the goal) and aim |
| Right trigger | Sprint |
| Left trigger (past half) | Close control |
| B / Circle (hold, release) | Shoot: power builds while held (full in 0.3 s) |
| Right bumper / left bumper / X / Square | Finesse / chip / driven shot (hold while shooting; driven + finesse is a low finesse) |
| Right stick flick (past 0.7) | Knock-on: a heavy touch ahead to sprint onto, caught about 1.3-1.8 s later (one per flick) |
| A / Cross | Start the round; at the report, next round (new seed) |
| Y / Triangle | At the report, retry with the same seed |
| D-pad left / right | Previous / next start spot |
| D-pad up / down | Next attacker / next keeper |
| Right stick click | Toggle the debug overlay (shot wedge, aimed and landed markers, keeper reach) |
| View / Share / Back | Flag the most recent finished round (a `flag` line in the session log) |
| Menu / Options / Start | Pause |
| Keyboard `B` | Swap face buttons (A with B, X with Y) for the active pad. On by default for Nintendo / Switch / Pro Controller pads; the choice is saved per pad name |

## Shooting

Five shot types share one model; the modifiers held at any point during the press pick the type at the release: chip if chip
was held, else driven + finesse is a low finesse, then driven, finesse, and power with none.

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

The keeper's decisions are in `src/FootballSim.Core/KeeperBrain.cs`, his rating-derived numbers and geometry in `Keeper.cs`,
and every number in the `keeper` section of `data/tuning/default.json`. Each tick he publishes `KeeperAction`, `KeeperDive`
(set only while Diving), `KeeperActionTime` (0 on the tick the action changes), `Velocity`, `Facing` and `HandPosition`.

- **Hold the arc.** He stands on the bisector of the angle the posts make at the ball, `arc_radius` plus `arc_depth` (by the
  ball's distance from the goal mouth: further out as it comes closer) plus `arc_depth_bearing` (by the ball's bearing from the
  goal's centre line: further out to close down a tight angle), both scaled by positioning, out from the mouth, never closer
  than `arc_min_from_line` to his line. His spot depends only on the ball, so he ends up in the same place wherever he started. `near_post_bias` can turn the line further toward the near post (0 by default: the bisector already sits on the
  near-post side of the ball-to-centre line). A new spot is taken only when the ideal one moves more than
  `reposition_threshold`, with a positioning error drawn then (`position_error`). He side-steps or jogs there with
  rating-based speeds and `move_accel`. Penalties (`keeper_hold_line` in the scenario) keep him on his line until the strike.
  Once an attacker running at goal is inside `hold_ground_range` of the mouth, a keeper already further out than his arc
  but within `hold_ground_max_out` (his rush zone) stops backpedalling, so he can still win a race for a long sprint touch;
  caught further out, he recovers to his arc.
- **Rush and smother.** Each tick he races for a loose ball: the earliest point on its predicted path he can reach (to within
  `smother_reach`) before it gets there and before the attacker's next planned touch, with a `rush_margin` (by distance,
  scaled by rushing_out) and a misjudgement drawn per attacker touch (`rush_misjudge`), only within `rush_max_from_goal`.
  Movement targets are capped at `move_target_cap`. Within a dive he smothers the ball and holds it if slower than
  `smother_hold_speed`.
- **Set.** In the last `plant_lead` seconds of a shot's wind-up (ball within `set_range`) he plants, braking at
  `plant_decel`. Planted close to the shooter he spreads (`spread_*`), which widens the body the ball can hit.
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
- **Recover.** After a dive or smother he lies for `grounded_seconds`, then gets up in `get_up_seconds`; a new save can start
  `get_up_save_after` into getting up.

The batch tool prints, under the scoring table, the keeper's distance off his line and the ball's distance from goal at the
strike (mean, p10, p90), what became of parried first shots, the share of rounds with a rush or a
smother before the shot, how often he was planted at the strike, his mean reaction, and the saves and dives he made. A first
shot touched by the keeper that carries on into the net counts as scored.

Player, tuning and scenario files are re-read every time a round is set up, so editing a JSON file in `data/` shows up next round.
A file that fails to load keeps its last good version and the HUD warning line names the file and field.

## Keyboard fallback

Works alongside a gamepad (the larger stick value wins) and when none is detected. Ignored in replay mode.

| Key | Action |
| --- | --- |
| Arrow keys or WASD | Run and aim (up = toward the far touchline, right = toward the goal) |
| Left Shift | Sprint |
| Left Ctrl or Alt | Close control |
| Space (hold, release) | Shoot |
| E / Q / C | Finesse / chip / driven (hold while shooting; C + E is a low finesse) |
| X | Knock-on |
| Enter | Start the round; at the report, next round |
| R | At the report, retry with the same seed |
| `[` / `]` | Previous / next start spot |
| `,` / `.` | Next attacker / next keeper |
| O | Toggle the debug overlay |
| F | Flag the most recent finished round |
| P or Esc | Pause |

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
