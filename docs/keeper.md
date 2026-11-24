# The keeper

The keeper's decisions are in `src/FootballSim.Core/KeeperBrain.cs` (standing, closing down and the rush race in
`KeeperBrain.Standing.cs`, his home depth, setting on the carrier, mirroring, the pounce and the run back in
`KeeperBrain.Home.cs`, chips in `KeeperBrain.Chip.cs`, the save in `KeeperBrain.Save.cs`, his penalty area's hands rule
and the slide tackle in `KeeperBrain.Feet.cs`), his rating-derived numbers and
geometry in `Keeper.cs` (the save model's in `Keeper.Save.cs`), and every number in the `keeper` section of
`data/tuning/default.json`. Each tick he publishes `KeeperAction`, `KeeperDive`
(set only while Diving), `KeeperActionTime` (0 on the tick the action changes), `Velocity`, `Facing` and `HandPosition`.
`keeper.brain` picks the keeper: `home` (shipped; below) or `legacy` (the keeper before it; [below](#the-legacy-keeper)).

- **Hold the arc.** He stands on the bisector of the angle the posts make at the ball, `arc_radius` plus `arc_depth` (by the
  ball's distance from the goal mouth: further out as it comes closer) plus `arc_depth_bearing` (by the ball's bearing from the
  goal's centre line: further out to close down a tight angle), both scaled by positioning, out from the mouth, never closer
  than `arc_min_from_line` to his line. His spot depends only on the ball, so he ends up in the same place wherever he started. `near_post_bias` can turn the line further toward the near post (0 by default: the bisector already sits on the
  near-post side of the ball-to-centre line). A new spot is taken only when the ideal one moves more than
  `reposition_threshold`, with a positioning error drawn then (`position_error`). He side-steps or jogs there with
  rating-based speeds and `move_accel`. Penalties (`keeper_hold_line` in the scenario) keep him on his line until the strike.
- **Close down: his home depth.** Once an attacker comes at goal with the ball (within `close_down_range` of the mouth, at
  `close_down_min_speed` or more, which a close-control dribble reaches; from then on for the round) he comes off his arc along
  the cover line to `home_depth_m` (by the ball's distance from the mouth, its excess over the arc scaled by
  `close_down_positioning`), so his depth grows as the attacker comes closer. `home_depth_m` is also his limit: he comes no
  further than it from the mouth, nor than his chip-safe depth as he judges it, nor closer than `close_down_gap` to the ball;
  between what he wants and those limits he keeps the depth he has (no backpedalling while the attacker runs at him). Every spot
  he takes standing (on his arc or closing down, after the commitment rule below and his positioning error) is held to no deeper
  off his line than his home depth for the ball looked ahead, unless his arc for it is deeper. Before closing down starts, an
  attacker on the ball in front of him still makes him keep his depth within those limits rather than drop back to his arc.
  Moving off or toward his line, he does not turn round for a new spot less than `reverse_threshold` the other way from where
  his braking would stop him; he settles there instead. A reposition back toward goal close to it (inside `urgent_range`) is
  run, not jogged. Moving away from the ball faster than `backpedal_show_speed` shows as `Backpedal`. Only a rush race he
  wins (below) takes him beyond his home depth.
- **No charge at a ball on the attacker's foot.** He weighs no charge. Only an attacker on the ball in front of him within
  `engage_range` of him, while he is not set on him (below), makes him close in, and then only to the point on the cover line
  `set_gap_m` short of the ball (never shallower than his arc, never beyond his home depth or the area's front line), giving
  ground no faster than `jockey_retreat_speed`; shown as `Charge` while more than a metre beyond that, then `Jockey`. A
  defender covering the ball stops it (see below).
- **Set on the carrier.** With the attacker on the ball in front of him and closing on him (faster than `still_speed`), once the
  ball is within `set_gap_m` plus the gap that closes while he brakes to a stop at `plant_decel` (over his braking time, half
  his own speed toward the ball plus the attacker's), he brakes at `plant_decel` to under `set_speed` and holds his stance
  (`KeeperMode.Set`; shown as `Spread` within `pre_spread_range` of the ball, else `Set`), so a change of direction does not
  catch him running. He stays set while the attacker keeps the ball within `set_gap_m` + 2 m of him and has not taken it past
  him (below).
- **Mirror.** Set and under `set_speed`, each tick his spot is on the line from the ball to the goal centre at his own distance
  from the goal centre, less however much the ball's distance from it has fallen below his own plus `mirror_lead_m`: he holds
  his distance until the ball comes that close to level with him, then gives ground to stay `mirror_lead_m` nearer the goal
  centre than the ball. The spot is never deeper off his line than his home depth (or his arc) for the ball, never nearer the
  goal centre than `arc_min_from_line`, never behind the line `arc_min_from_line` in front of his goal line; a ball on the goal
  centre gives the line straight out. He moves there (`move_accel`, slowing to arrive) at up to `mirror_speed` (positioning)
  across the line from the goal centre to him and `backpedal_speed` toward goal (`KeeperMode.Mirror` while moving, shown as
  `Shuffle` or `Backpedal`).
- **Pounce.** Set (braking into it or mirroring), with the ball low in his penalty area, he pounces at the first point on the
  ball's predicted path (from the forecast published that tick, up to the attacker's next touch or `rush_lookahead`) that is
  below `slide_max_height`, in his area, within `pounce_reach_m` (diving) of his hull (`hull_radius` from his centre), and
  that his hands reach (at `smother_speed` over the distance beyond his hull) no later than the ball and at least
  `pounce_margin_s` (reflexes) before the attacker's next touch, as he judges it with `rush_misjudge` (drawn per attacker touch,
  as for the rush race). The pounce is a smother from then on: it holds, blocks or misses, and leaves him `Grounded`, then
  getting up. A skill move that changes the attacker's next touch after he goes is a feint that beats him. A ball at the
  attacker's foot (his next touch due now) is never pounced on.
- **Run back.** Taken round (the ball nearer his goal line than he is by more than `rounded_margin_m`) and standing (no save
  under way), he runs at rush speed (`move_accel`, slowing to arrive) to the point `recover_depth_m` off his line on the line
  from the ball to the goal centre (half way along it when the ball is nearer his line than that; a ball on the goal line: inside
  the post's `line_inset`, `arc_min_from_line` out), through a shot's wind-up too (a shot struck meanwhile is saved as any
  other, his reaction paying `still_moving_reaction_penalty`). `KeeperMode.Recover`, shown as `Rush`. On the ground or in a
  save nothing new starts until he is back on his feet.
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
- **Stay big.** Within `pre_spread_range` of an attacker on the ball in front of him that he cannot beat to it, neither set on
  him nor charging, he stops and spreads (`Spread`); once the ball is level with him or past, he goes
  back to covering the goal (and recovers toward it).
- **Plant for the shot.** In the last `plant_lead` seconds of a shot's wind-up (ball within `set_range`; earlier if he needs longer to brake
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

## The legacy keeper

`keeper.brain` = `legacy` (or `--brain legacy` on the batch tool) plays the keeper as he was before the home keeper, on the
same code path with the same draws and key values: no home depth, set, mirror, pounce or run back (`home_depth_m` to
`recover_depth_m` and `engage_range` are not read). Everything else above is his too, except closing down and the charge:

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
  Within `legacy_engage_range` of the ball he always charges. Charging, he runs at rush speed to the point on the cover line
  `jockey_distance` short of the ball (never beyond the area's front line), then jockeys there: low and spread, stepping with the
  attacker to stay on the cover line, giving ground no faster than `jockey_retreat_speed`, so an attacker who runs at him
  closes the gap. He drops the charge once the time to close exceeds `charge_drop_factor` times what he accepts (plus 0.25 s),
  for an attacker who stops or turns away, and recovers to his limits as when closing down. With the ball up to
  `charge_hold_margin` beyond the range and a charge he would make, he keeps his depth instead of giving ground first. Shown as
  `Charge` (the run cycle) while more than a metre beyond jockeying range, then `Jockey`.

## With a defender

In a scenario with a defender the keeper yields to him: while the defender is on his feet, within `defender.keeper_yield_cone_deg`
of the keeper's line to the ball, nearer to the ball than the keeper and not beaten, the keeper neither charges nor rushes
(the trace's race block reads `defender covers`). A ball within a dive of him is still his, and so is a pounce when he is set on
the carrier; he still sets and mirrors, and his saves are unchanged. See
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

The round report's `keeper` block is the last strike's numbers (with the round's flags before the first strike: rushed,
charged, smothered, slid, won the ball, rounded, the band depths and the reversals) and, measured over the whole round
(`World.Measure.cs`): `depth_max_m`, the furthest he stood off his goal line on any tick of play; `recover_s`, the seconds
from his first being taken round (`rounded`) until he was back on the line from the ball to the goal centre (goal-side of the
ball and within 0.75 m of it, `World.BackOnLine`), null when he was not taken round or not back before the outcome;
`off_line_at_strike_m`, his distance from the segment from the ball to the goal centre at each shot's strike, one per shot;
and `pounced` and `pounce_won`, whether he pounced before the first strike and whether that smother met the ball (always false
for the legacy keeper). Reports saved before these fields read them as 0, null, empty
and false.

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

## The human probe

`--probe human` ([tools.md](tools.md), [defending.md](defending.md#the-human-probe) for how its plans play and finish)
runs four breakaway plans, 2,000 rounds each for the even (`striker_a`) and the fast attacker (`pace_star`), first attacks
only:

- `shoot_set`: jogs at goal and shoots low (driven) at the far corner once the ball, carried on through the wind-up, will be
  11 to 13 m from the goal centre (drawn per round).
- `round`: sprints at goal; with the keeper within 7 m veers 45 degrees away from the side he leans to (the wider side when he
  is central) and sprints on until the ball is nearer the goal mouth than he is by 0.5 m (or 1.5 s, or 8.5 m out wide), then
  carries on at goal and taps in: a low shot at the middle of the open half of the goal (1.8 m from its centre) once he is more
  than 1.5 m off the line to the open post inside 9 m, or inside 6 m, or 2.5 s after going round.
- `round_close`: the same, under close control from 8 m out from him (no sprint).
- `feint_round`: sprints at goal, close control inside 8 m of him; within 4 m plays a step-over sold toward the side he leans
  to (the skill probe's trace) and goes out the other way at 60 degrees, then taps in as above.

Bands, constants in `HumanProbe.cs`: `round` and `round_close` at most `shoot_set`'s measured first-attack goals minus 3
points (same attacker); `feint_round` 25 to 40%; over all four plans the p90 of his depth off his line on the ticks the
carrier has the ball more than 8 m from him, at most 6.5 m, and the median time from taken round to back on the line, at most
1.5 s (a keeper taken round who is not back before the first attack ends counts as slower than any who is; the probe plays on
0.5 s after the first attack ends, then stops). `shoot_set` has no band. Measured with the shipped tuning (seed 1; `--seed 2`
gives the same table, since the rounds' seeds are the run's seed plus 0 to 1,999):

| Plan | Attacker | First attack: goal / miss / held | Band (goals) |
| --- | --- | --- | --- |
| shoot_set | even | 61.4 / 22.4 / 12.5% | - |
| round | even | 10.7 / 38.1 / 47.9% | at most 58.4% (ok) |
| round_close | even | 15.7 / 1.8 / 82.3% | at most 58.4% (ok) |
| feint_round | even | 39.1 / 18.7 / 40.6% | 25 to 40% (ok) |
| shoot_set | fast | 77.5 / 10.3 / 8.6% | - |
| round | fast | 11.9 / 18.3 / 65.6% | at most 74.6% (ok) |
| round_close | fast | 20.6 / 3.0 / 76.0% | at most 74.6% (ok) |
| feint_round | fast | 26.4 / 21.1 / 49.2% | 25 to 40% (ok) |

| All four plans | Even: measured (band) | Fast: measured (band) |
| --- | --- | --- |
| keeper depth p90, carrier on the ball more than 8 m from him | 4.2 m (at most 6.5 m, ok) | 4.3 m (at most 6.5 m, ok) |
| median taken round to back on the line | 0.61 s: back in 23 of 34 rounds (at most 1.5 s, ok) | 0.77 s: back in 4 of 8 rounds (ok) |

He sets about 6 to 7 m from the carrier, about 4 to 5 m off his line, and mirrors him, so going round him rarely works: in
traced `round` rounds (even, seeds 1 to 40) he pounced on a sprint touch in about a third, and in most of the rest the carrier
was run wide and shot from a tight angle. The shot from 11 to 13 m scores far more often than against the legacy keeper, who
met the shooter about 3 m out. A step-over inside 4 m that draws his pounce leaves him on the ground and the goal open. The fast
attacker's `feint_round` and his rounds taken round sit near their bands' edges (26.4%; 4 of 8 back).

The legacy keeper (`--brain legacy`, seed 1) charged a carrier he could reach and was then taken round with the goal open:
`shoot_set` 13.6 / 20.8% (even / fast), `round` 95.5 / 89.2%, `round_close` 39.7 / 9.6%, `feint_round` 97.0 / 92.8%; his
depth p90 7.3 / 8.1 m, and he was back on the line after being taken round in 13 of 3,803 and 105 of 3,866 rounds.
