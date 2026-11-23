# The keeper

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
  there and before the attacker's next planned touch (without the ball, the attacker's meeting with it when he holds his
  team's claim, see lock-on in [dribbling.md](dribbling.md)), with a `rush_margin` (by distance, scaled by rushing_out; none for a
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
  by his distance from the ball: fully within `ready_full` (3.5 m, from reference data), not at all from
  `ready_range`. His standing body, the capsule the ball can hit from the shins to the shoulders until he moves, is his hull:
  `body_radius` not ready, growing to `hull_radius` (0.42 m: his torso with spread legs and arms, averaged) fully ready, so a
  set keeper close to the shooter is bigger low and high. The rest of his close-range cover is the reflex. Still at the strike, he also holds his hands `hand_span` out to each side at
  `hand_span_height_fraction` of his height (a ball reaching them before he moves is stopped); his reach grows from the span,
  or from his hull's edge when that is further, rather than from zero.
- **Reflex and reaction.** From the contact he has a reflex (`reflex_time` by reflexes) and, later, his full reaction
  (`reaction`; both with `reaction_noise`; moving at the strike adds `still_moving_reaction_penalty` to the reaction only). At
  the reflex he plans both saves on the ball's predicted path: if his reaction and dive get to it in time he waits for them;
  otherwise, if a reflex limb gets to it, he makes a reflex save now (`SaveType.Reflex`); if neither does, a reflex attempt
  (`ReflexAttempt`) when the ball passes inside his save hull (`save_hull_radius`, 0.75 m, from reference data), or when the
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
  to `_hard` by reflexes, from reference data), the start delay's spread, and what happens at contact.
- **Contact.** At the hands he holds a ball within his catch range for its speed (`catch_range_standing_min`..`_max` or
  `catch_range_diving_min`..`_max` by handling; at the feet or after backpedalling as for a ball `feet_catch_factor` or
  `backpedal_catch_factor` slower in effect) scaled by `catch_range_time` for the seconds since the strike, and no harder than
  `catch_max_difficulty`; his body or a smother holds a ball slower than `body_hold_speed` or `smother_hold_speed`. A reflex
  limb's touch that is not held can be loose (an uncontrolled deflection), with a chance of `reflex_loose_edge` by
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

## With a defender

In a scenario with a defender the keeper yields to him: while the defender is on his feet, within `defender.keeper_yield_cone_deg`
of the keeper's line to the ball, nearer to the ball than the keeper and not beaten, the keeper neither charges nor rushes
(the trace's race block reads `defender covers`). A ball within a dive of him is still his and his saves are unchanged. See
[defending.md](defending.md#the-keeper-yields).

## With a team-mate

In a scenario with a second attacker the attacker the keeper reads is the controlled one, so from the strike of a pass it is
the receiver. While the pass is on its way he sets himself (his arc, his depth) for where it will be met, the receiver's
meeting point, instead of where the ball is; his rush and his saves still read the ball itself, and a pass winding up is not
a shot, so he does not plant for it. See [passing.md](passing.md).

## Diagnostics

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
