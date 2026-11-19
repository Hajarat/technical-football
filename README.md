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
| Play (needs a gamepad) | `godot-mono --path game` |
| Build the game project | `dotnet build game/FootballSim.Game.csproj` |
| Tests | `dotnet test FootballSim.slnx` |
| Batch run, 10 000 scripted rounds | `dotnet run -c Release --project src/FootballSim.Batch -- --scenario penalty --n 10000 --seed 1` |
| Save a scripted round as a replay | `dotnet run -c Release --project src/FootballSim.Batch -- --scenario breakaway --seed 6 --save-replay game/tests/smoke_replay.json` |
| Re-run a replay in the batch tool | `dotnet run -c Release --project src/FootballSim.Batch -- --replay game/tests/smoke_replay.json` |
| Dribble probe: scripted stick traces per dribbling rating, the idealised ones and human-like ones at a pad's 60 Hz (a wandering stick, 45 and 100 degree sweeps, a reversal flick through neutral at a jog and in close control): possession kept, touches per second, ball ahead, feet, steering, contact miss, touches dropped at contact, stretched touches, the time from a stick turn past the execution angle to the turn touch, the time for the ball to move along a flicked stick, the lowest speed of the wandering jog, and the speed steps (`steps`: ticks from 1 s on with the ball whose ground speed changes faster than 15 m/s^2, shots excluded, skill-move ticks included; `skill_steps`: those during a move or a turn at the feet); a pooled summary follows the table. `--dtrace <probe> --ratings R [--index i --seeds n --from T --to T --steps-only 1]` prints one probe tick by tick with its speed steps, drops and slowdown plans | `dotnet run -c Release --project src/FootballSim.Batch -- --probe dribble` |
| Session probe: replays the play-test rounds kept in `src/FootballSim.Core.Tests/TestData/playtest` (or `--replays <dir\|file>`; a folder is searched with its subfolders, so a folder of session folders such as `sessions` gives every round in them) with the current code and counts touches dropped at contact, slowdown plans with the stick pushed, and the speed steps (as the dribble probe counts them) and largest per-tick acceleration in possession, with a total per 1000 possession ticks | `dotnet run -c Release --project src/FootballSim.Batch -- --probe sessions` |
| Feel probe: speed surges, per-tick acceleration, touch cadence, ball distance and facing wobble of straight dribbles, and the heading response to a 45/90/180 degree stick turn with and without the ball, each row with its speed steps counted as the dribble probe counts them; `--replays sessions` gives the acceleration and speed-step numbers over recorded play-test rounds, `--trace sprint45` (`jog90`, `sprint90off`, ...) prints one turn tick by tick, `--rtrace <replay> [--from T --to T --steps-only 1]` one recorded round tick by tick with each speed step attributed to the planner's state | `dotnet run -c Release --project src/FootballSim.Batch -- --probe feel` |
| Skill-move probe (scripted gestures per move, side, entry pace and skill_moves 1/50/99; the not-built elastico, heel-to-heel and spin rows should read recognised 100, made 0; `--list-failed 1` prints the ball and the planned touch after the gesture for every run whose move was not made) | `dotnet run -c Release --project src/FootballSim.Batch -- --skill-probe` |
| Write one replay per skill move (edge_of_box start) for screenshots | `dotnet run -c Release --project src/FootballSim.Batch -- --write-skill-replays out/skill-replays` |
| Write one replay per visible keeper action (shuffle, rush, planted then reacting, each dive kind, tip over the bar, each reflex save, a block by the ready keeper's hull, smother, closing down, charging, jockeying, spread, a long sprint touch won, a backpedal under a chip, taken round, parry then grounded then getting up) for screenshots; prints each file with the tick range of its action | `dotnet run -c Release --project src/FootballSim.Batch -- --write-keeper-replays out/keeper-replays` |
| Covered-angle table: the angle a set, planted keeper covers by shot distance (2-20 m), speed (20/25/30 m/s) and height | `dotnet test src/FootballSim.Core.Tests --filter KeeperCoverTests --logger "console;verbosity=detailed"` |
| Print the keeper's state tick by tick for one scripted round | `dotnet run -c Release --project src/FootballSim.Batch -- --scenario tight_angle --trace-keeper 1` |
| Smoke run: replay through the game scene, no window | `godot-mono --headless --path game -- --replay tests/smoke_replay.json --quit-after-round` |
| Smoke comparison (game report vs batch report) | `game/tests/smoke.sh` |
| Screenshot run (opens a window briefly) | `godot-mono --path game -- --replay tests/smoke_replay.json --shots out/replay --shot-on setup,charge,flight,outcome --quit-after-round` |
| Screenshot run without the window taking focus (use this from tools and agents) | `game/tests/run-bg.sh -- --replay tests/smoke_replay.json --shots out/replay --shot-on setup,strike,outcome --quit-after-round` |
| Keeper gallery: every keeper state and dive kind from a script (both sides, side and front views), no simulation; the keeper is animated from motion clips by default, `--keeper-anim procedural` shows the hand-built poses | `game/tests/run-bg.sh -- --keeper-gallery out/keeper-gallery` |

The smoke run prints the round report on one line prefixed `REPORT ` and exits 0. `smoke.sh` checks it is identical to the batch tool's report.
Screenshots go to `game/out/` (ignored by git).

Game options after `--`: `--data <dir>` (default `data/`), `--replay <file>`, `--quit-after-round`, `--quit-after-ticks N`,
`--shots <dir>` with `--shot-ticks a,b,c`, `--shot-on setup,live,charge,strike,flight,outcome,touch<N>` (the N-th touch) or `keeper` (a burst of frames whenever the keeper's action changes; pair with `--close-keeper`), or `--shot-burst <startTick>:<count>:<step>`, `--close-cam` / `--close-keeper` (close-up cameras for inspecting limbs), `--frame-times`, `--figure skinned|procedural` (default `skinned`: rigged human models; `procedural` keeps the primitive meshes; a missing or unloadable model falls back to procedural with a HUD warning), and `--background` (macOS: hide the app at startup and opt out of App Nap so a windowed run never takes focus; use it through `game/tests/run-bg.sh`, which also keeps the window from flashing in front while Godot starts and prints the game's output).

Automated tools and agents must use `game/tests/run-bg.sh` for any windowed run (anything with `--shots`); headless runs need no wrapper.

In replay mode the game also prints `FOOT_EVENT` per touch/strike and, on quit, `FOOT_CONTACT n= mean_m= max_m=` (touching foot's instep to the ball surface at each Touch/Strike) and `FOOT_SLIDE n= mean_m= max_m=` (movement of a planted foot per stance). With `--figure skinned` it also prints `SKIN_CONTACT n= mean_m= max_m=` (the skinned boot's contact surface for the touch's surface — inside, laces, outside or sole — to the ball surface at each touch's contact tick; target max <= 0.03 m), `FOOT_JUMP ctx=<skill|exit|touch|shot|other|all>` lines give, per context of the attacker's tick (a skill move, from a move's end until the first touch after it has recovered, a dribble touch, a shot, anything else), the largest and the 99th-percentile world move of either foot (the larger of its ankle's and boot tip's) in one physics tick (`dt_s`, one rendered frame at 120 Hz) and over two ticks (`max60_m`, one frame at 60 Hz), with the tick and foot of the largest; `STRIDE n= step_s_mean= step_s_cv= step_m_mean= step_m_cv=` gives the attacker's running steps (plant to plant of alternate feet above 2 m/s, no skill move or shot: time, ground distance and their spread),
and `POSE_LIMITS` carries the running-only `run_min_pelvis_ratio`, `run_lean_max_deg` and `run_bob_max_m`; the dribble probe behind these targets is `dotnet run -c Release --project src/FootballSim.Batch -- --probe dribble`.

`--skill-probe` prints, per move: gesture recognised, move made, possession kept 2 s after the move, the time from the move's end to 90% of the with-ball top speed with sprint held (`t90_s`), the same for the same turn without the move (`base_t90_s`, both medians), the ball's distance at the move's end, for a step-over or feint the ball's distance ahead of the player at the start, when its own motion begins and when the foot passes over it (`start_ahead_m`, `exec_ahead_m`, `mid_ahead_m`), and for a sole move the ball centre's distance from the foot's contact point when the sole lands (`sole_miss_m`), each as mean/max; `--seeds`, `--ratings` and `--only step_over,...` narrow it.
Player sheets carry a `skill_moves` rating (1-99): a skill move's exit burst grows with it, and each move chained within 2 s of the previous one uses 0.8 times the previous move's rating. A sheet without it uses its `dribbling`. Skill-move timings and the burst live in the `skill` section of `data/tuning/default.json`.

## Dribbling and movement

Every number is in the `movement` and `touch` sections of `data/tuning/default.json`; where GOALS' shipped data gives one it is used.

- Speeds: a jog is 5.3 m/s for every player (`jog_speed`, GOALS' MaxJogSpeed), a sprint 6.8-8.5 m/s by `sprint_speed`
  (GOALS' MaxSprintSpeed); with the ball both are lowered by dribbling (`top_speed_with_ball`), and close control runs at 0.35
  of that lowered top speed.
- Turning: with the ball up to 450 degrees per second by agility (GOALS' dribbling RotationRate), less with weight and halved
  at top speed; a heading change smaller than the rate covers in `turn_ease` (0.05 s) eases in instead of snapping, so the
  small corrections between touches do not jerk the body round.
- A sharp turn or a cut costs the balance percentage of speed, shed at `balance_deceleration` (12 m/s^2) rather than at once.
- Turning with the ball: a stick turned past the execution angle plans the turn touch at once on the next swing that can play
  the ball up to `turn_reach` (0.45 m at rest to 0.6 m at a sprint, after GOALS' 0.6 m execution radius) from the foot, with
  the body stepped toward it (`turn_steer_max` 1.5 m/s at `turn_steer_accel` 15 m/s^2), not a stretched leg; for
  `post_turn_seconds` (0.3 s) after a turn or cut touch the body turns at least `post_turn_rotation_rate` (540 deg/s, after
  GOALS' post-execution rotation rate) onto the ball's new line.
- Without the ball the turn rate tops out at GOALS' 360 deg/s (`possession.off_ball_turn_bonus` -0.2), and a stick more than
  `reverse_turn_deg` (135) from the facing above `reverse_min_speed` plants the runner: `reverse_speed_loss` (0.65) of his
  speed is shed while he turns about.
- Touches: the stick pushed no further than `walk_max_input` (0.7, GOALS' MaximumInputSizeForWalkTouch) dribbles with
  walking touches, played as close control (a touch every step). A jog touches every two steps, and every
  `foot_switch_every`-th (4th) touch takes one step more so the other foot takes over: an even cadence on both feet.
- Steering into the contact zone: a touch is planned at least `min_plan_ahead` (0.3 s) before its contact and the player is
  steered during the last `pre_windup` (0.4 s, GOALS' PreWindupTime). The steering correction eases in and out at
  `steer_accel` (6 m/s^2, at most `steer_max` 0.6 m/s) and only brings the ball to within `reach_tolerance` (0.08 m) of the
  swinging foot's contact point; the foot reaches the rest, and the contact point is where the ball is.
- A waiting touch is checked again whenever the stick has drifted `drift_recheck_deg` (3) since the last check, and every
  `recheck_interval` (0.05 s) in the pre-windup: if the legs no longer bring the ball within the steering's reach it is planned
  again at once (inside the windup the run holds instead, and a change of touch mode waits for the next touch). Predictions
  carry the stick's turn rate on only over `stick_lead_time` (0.15 s), easing out, and never longer than the stick has been
  turning. A replan with the stick pushed off the ball's travel takes the next swing within `turn_reach`, with the turn's
  steering; one after a push whose meet was lost may use a swing as close as the windup.
- At contact a ball up to `stretch_reach` (0.25 m) from the contact point is still played, stretched, with the rated errors
  (with the contact point 0.25-0.45 m ahead of the body that keeps it within about GOALS' 0.6 m execution radius); only a
  ball further off is dropped.
- With the stick pushed a slowdown (easing off) is planned only on a swing at most `slowdown_max_ahead` (0.6 s, about a jog
  cadence) ahead and a step sooner than running on would play the ball; a recovering player never eases off.
- A stick released for no longer than `release_debounce` (0.04 s; a pad polled at 60 Hz can read neutral for a tick or two
  in a flick) reads as still held; a longer release is a stop. Not while shoot is held.
- Turning at the feet: a stick at least `feet_turn_deg` (120) from the ball's travel (the facing for a still ball) while
  running, with the ball low and within `feet_turn_reach` (0.6 m, GOALS' execution radius) of the body and no touch already
  winding up, turns the ball at once with the drag-back's sole motion, pulled onto the stick's line instead of straight back
  (the drag-back's timings and distance; no chain, exit burst or gesture), and the exit touch sends it along the stick.

## Character models

The players are rigged MPFB2/MakeHuman bodies (`game/assets/characters/player.glb`, `keeper.glb`, Mixamo-compatible skeleton, CC0 assets only).
`PlayerFigure` still computes every joint procedurally each physics tick; `SkinnedBody` poses the skeleton from those joints (knees, ankles, elbows
and hands land on them), tints clothing and skin from `FigureKit` and puts the shirt number on the back. Foot-contact and pose measurements come
from the joints, so they are the same with either `--figure` value. Build steps, asset list and licences: `game/assets/characters/SOURCES.md`.
A fresh checkout needs `godot-mono --headless --path game --import` once.

## Controls panel

The game shows the controls for the device in use: keyboard and mouse or gamepad, switching live to whichever produced the last
meaningful input (a key, a mouse button or more than a few pixels of mouse movement; a pad button or a stick or trigger past 0.3).
A small hint in the top-right corner names the toggle: `H: controls` on the keyboard, `Menu: controls` on the pad (`Options` on PlayStation, `+` on Nintendo). The
full list opens on `H` and, on either device, while the game is paused (`P` / `Esc` / Menu), and stays out of the play area otherwise. It lists
movement, shooting, skill moves (the right-stick gesture in words for the pad, the keys for the keyboard; elastico, heel-to-heel and
spin are marked not built yet) and round / menu actions. Pad buttons use the pad's own names (Xbox, PlayStation or Nintendo, picked
from the name the OS reports; Xbox names when unknown), and the face-button swap keeps the shoot button the right-hand one, so the
labels match what is pressed. Every label is read from `game/Bindings.cs`, where `InputReader` also takes its keys and buttons, so
changing a binding there changes the panel. `-- --controls-panel keyboard|xbox|playstation|nintendo` opens the panel for that
device without a pad, for screenshots.

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
  wind-up by a soft touch. From the press to the strike the left stick only aims: the legs run on to the ball (the recovery
  run toward its intercept, at the ball's own pace away plus the gap over the speed time constant), whichever way the stick
  is swung, so the striking foot meets it. The soft touch leaves the ball rolling along the shooter's velocity.
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
| H | Show or hide the controls panel | Hero select (not built) |

GOALS keys not used here: R (its left stick click action), F (switch set-piece taker), Tab (match menu),
middle mouse (hold to use the mouse as the right stick); its H (hero select) opens the controls panel. There is no driven-shot key: as on the pad, the low shot is shoot
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
