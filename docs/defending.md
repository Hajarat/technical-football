# The defender

A scenario with `"defenders": 1` adds one outfield defender on the keeper's side: team 1, player 1, `World.Defender`. The
shipped one is `data/scenarios/defender_duel.json` (attacker at 4 m with the ball, defender at 16 m facing him, keeper on his
arc, 20 s), with the sheet `data/players/centre_back.json` (`default_defender`). A scenario without a defender plays exactly
as before: it reads none of this.

The code is in `src/FootballSim.Core`: `DefenderBot.cs` (what he decides), `Defending.cs` (the rules of every defending
action, for any outfield player), `Contacts.cs` (bodies), `World.Defender.cs` (how the world applies them) and the jockey in
`Movement.cs`. The bot's numbers are the `defender` section of `data/tuning/default.json`, the actions' the `defending`
section. Values marked (ref) in `Data/Tuning.cs` were read from reference data; the others are ours.

In a scenario with a team-mate (two v one) he also waits a reaction delay after a strike, anchors on where a pass will be met
and cuts it out only when he is first: see [teammate.md](teammate.md#against-two-the-defender-and-the-keeper).

## The bot writes an intent

The bot never moves the defender. Each tick it writes a `TickIntent` (stick, sprint, and the held buttons `Jockey`, `Tackle`,
`Slide`, `Barge`), and the world runs that intent through the same action code a human defender will use. A replay records
the bot's intents as the third player of every tick. `World.DefenderBotEnabled = false` makes the defender follow the intent
passed to `World.Step` instead. A replay that holds his intents plays them that way (`Replay.Feed`, in the batch tool and the
game alike), so a round recorded with either bot plays back unchanged; meanwhile the bot still reads the world
(`World.DefenderBotShadow`: its mode and its numbers in the report, its own draws) without moving him, and it plays him from
where the recording ends. A replay without his intents runs the bot as the round did. Both ways give the same round, because
the bot draws from its own generator and the actions from another (both salted from the round's seed).

Which bot plays him is the tuning key `defender.brain`: `contain` (shipped) or `legacy` (below). The containing bot
(`DefenderBot.Contain.cs`) is in one of six modes (`DefenderBot.Mode`). Distances "from goal" are to the goal centre; "the
line" runs from the ball (or, against a team with the ball loose, the anchor) to the goal centre. Ratings are read over
1 to 99.

- **The spot.** On the line, the gap goal-side of the carrier (his distance from goal less the gap), no further from goal than
  `engage_line_m` while the carrier is outside the gap and no nearer than `last_line_m`, then `shade_m` square to the line
  toward the arena's long centre line (scaled down with the ball within a metre of that line), inside the boards. With the
  ball on the goal centre the line runs straight out of the goal (`DefenderBot.SpotFor`).
- **The gap.** `contain_gap_m` by jockeying (4.65 m at 1, 2.25 m at 99; 2.91 m for `centre_back`) plus `gap_pace_s` for each
  m/s the carrier runs faster than his sprint-jockey pace, at most `gap_max_m` (`DefenderBot.GapFor`). The carrier is inside
  the gap when he is no more than the gap further from goal than the defender (half a metre more once containing, so he does
  not flicker).
- **Drop** (the carrier outside the gap). He goes to his spot jockeying, facing the carrier, easing in no faster than he can
  brake from at `jockey_decel`; the sprint-jockey pace only toward a spot nearer goal than he is, and a run (no jockey) only
  to such a spot further than `drop_sprint_distance_m` away. So he never sprints up the pitch, and while the carrier is far
  off he waits on his engage line. With the carrier within twice his gap and himself more than `drop_offline_m` off his
  shaded line (the carrier cutting across him), he contains instead of dropping; he drops again only once the carrier is
  outside the gap and he is back within half of `drop_offline_m`.
- **Contain** (inside the gap). The velocity he asks for is the carrier's velocity along the line plus `gap_gain` times the
  distance from him to his spot, jockeying at whatever share of the sprint-jockey pace that needs.
- **Run with.** Once that velocity has been above `run_with_factor` of his sprint-jockey pace for `run_with_after_s`, with
  the carrier's speed toward goal at least his jockey pace, he lets go of jockey and runs at a point `run_with_inside_m`
  goal-side of the carrier on the line, matching the carrier's velocity (the same gain). He contains again once the carrier's
  speed toward goal is under his jockey pace (so a carrier slower than that toward goal is contained however far off his spot
  the defender is, never run with for a tick and dropped).
- **Win.** He goes for the ball on any of three triggers, checked while he is on his feet:
  - *loose*: the dribbled ball, rolled on as it goes (the bot's own copy of the ball, stepped at 60 Hz), reaches a spot his
    foot (his lunge reach) gets to first, no later than `win_horizon_s` ahead and at least `win_margin_s` (by awareness:
    0.18 s at 1, 0.09 s at 50, 0.036 s at 99) before the carrier's next touch (`NextTouchEta`). He runs at that spot and
    presses tackle once it is inside his lunge; slide only when running on would not bring it inside his lunge in time, it
    is inside his slide (slide reach plus the slide's foot), and he is not behind the carrier (his bearing to the ball within
    `slide_behind_deg` of the carrier's heading with the carrier's body nearer him than the ball). The spell holds while the
    race still holds with no margin.
  - *slow*: the carrier under `press_speed` for `press_after_s`, or, with the carrier further from the goal centre than
    `engage_line_m` plus the gap, for `press_far_after_s` (so a carrier who sets off slowly from far out does not draw him up
    the pitch, while one who stands on the ball still does). He closes on the ball at the sprint-jockey pace, bending in from
    the ball's side close up, but no faster than he can stop from at `jockey_decel` 0.6 m short of the ball, so he arrives
    under control; with the attacker within `tackle_max_distance_m`, the ball between them and inside his lunge's reach, he
    presses tackle, or slide by a roll against `slide_preference` made once per spell (never from behind). It ends when the
    carrier speeds up past `press_speed`.
  - *last line*: on his last line (within 0.3 m of `last_line_m`) with the carrier inside the gap: he closes (under control)
    and tackles the same way, giving no more ground.
  A spell ends once a pressed button's action is over, the ball has gone, or its trigger no longer holds. No button is pressed
  while the carrier's pass will be struck within `stand.exec_max_s`.
- **Recover** (beaten: further from goal than the carrier by `overshoot_m`). He runs, easing in, at the nearest point to the
  carrier on the carrier's segment to goal that he reaches no later than the carrier, each at his top speed (the carrier's
  with the ball at the sprint style), never nearer goal than `recover_min_goal_m` (that point when there is none). He drops
  or contains again once he is no longer beaten.
- **Block.** While the attacker winds up a shot he steps onto the ball's line to the goal centre at the depth he is at,
  jockeying (in Drop, Contain or Run with).
- **Collect.** A ball that has been played and that nobody has: he runs to where it will be, at a velocity that closes on it
  and matches its own, so he arrives able to control it.
- **Barge.** In any mode but Collect, every `barge_roll_s` with an attacker running alongside, a roll against
  `barge_preference` (by strength: 0.1 at 1, 0.6 at 99) presses barge.
- **Not free.** Sliding, on the ground, getting up, in a lunge or stumbling, he presses no button and his mode does not
  change; he moves by the mode he is in (or to a loose ball he goes for) and takes up his triggers once free.

The view's action follows the mode when no action is under way: Drop shows Hold or Jockey, Contain Jockey, Run with and Win
Press, Recover Recover, Collect Collect. The report's `time_active` is his time in Win, `time_contain` and `time_run_with`
his time in those modes, and `wins_loose`, `wins_slow`, `wins_last_line` how many Win spells each trigger started.

**The legacy bot** (`defender.brain` = `legacy`, or `--brain legacy` on the batch tool) is the older hold-and-press bot,
kept for comparison on the same seeds. It reads the keys marked legacy only in `Data/Tuning.cs` (`hold_distance_m`, the
hold offsets, `passive_sprint_distance_m`, `goal_side_margin_m`, `to_active_after_s`, `active_max_s`, `extrapolate_s`,
`press_sprint_distance_m`, `press_jockey_distance_m`, `press_jockey_sprint`, `legacy_barge_preference` at 0 to 0.3): he
holds `hold_distance_m` off the ball on its line to the goal centre with a side offset drawn per spell (mode Drop), presses
(mode Win) once goal-side by the margin after his awareness wait, running at where the attacker will be, until his active
time runs out or he is beaten, and presses tackle with the ball between them (a slide by a roll per press). It draws the
same numbers as before the containing bot existed, so its rounds are unchanged.

## The actions

These are rules of the core for an outfield player without the ball. `PlayerState.DefenderAction` publishes what he is doing
(Hold, Recover, Jockey, Press, Lunge, Slide, Barge, Block, Collect, Grounded, GettingUp, Stumble) with `DefenderActionTime`
and `ActionSide`; `TackleFoot`, `TackleContactTime`, `BlockReach`, `Jockeying`, `StumbleUntil`, `TackledLockUntil` and
`PushVelocity` are there for the view.

- **Running and jockeying.** Without jockey he runs like any player, except that his heading turns no faster than
  `run_turn_accel` (by agility) over his speed: the faster he runs, the slower he comes round, so a defender who dives in is
  beaten by a cut. Holding jockey, his velocity goes to the stick at `jockey_speed` (by jockeying; `sprint_jockey_speed` with
  sprint), slowing at `jockey_decel`, while he turns to face the ball carrier at `jockey_turn_deg`.
- **Auto tackle.** No button. A dribbled ball within `auto.guarantee_m` of him and below `auto.max_height_m` is taken at once.
  Otherwise it must lie in his front half, inside `auto.radius_walk_m` or `auto.radius_jockey_m` for his speed times
  `auto.radius_factor` (by stand_tackle), inside `auto.angle_deg` of his facing (90 degrees standing, 45 at full speed), and
  not be shielded: the dribbler's body within `auto.lane_half_m` of the line from tackler to ball and within `auto.shield_m`
  of the ball. A ball dribbled into his body is taken the same way. `auto.cooldown_s` separates auto tackles, and follows any
  touch of the ball by the tackler.
- **A ball won** is poked away at `auto.poke_speed_min` to `auto.poke_speed_max` along the direction within
  `auto.poke_spread_deg` of his facing (or of the tackle) that passes furthest from the opponents ahead. The player who had
  it stumbles for `stumble_s` (by balance): no touches, his speed capped at `stumble_speed`, and he gives way in a body
  contact. For `tackled_lock_s` he cannot touch the ball at all. A shot he was winding up is dropped like any that lost the
  ball.
- **Lunge** (tackle pressed). Its reach is `stand.reach_m` (by stand_tackle) times `stand.reach_speed_factor` for his speed,
  halved with the ball more than `stand.off_run_deg` off his run. Over `stand.exec_min_s` to `stand.exec_max_s` after the
  press he looks for the first tick at which his foot, reaching from where he will be, meets the ball rolling on as it does
  now, and lunges for that; with none he lunges at the ball as far as he reaches. From just before that tick for
  `stand.active_s` the foot is a sphere of `stand.foot_radius_m` in the ball's path, and a ball within `stand.certain_m` of
  it below `stand.certain_height_m` is won outright. He is planted through the lunge (a lunge that has the ball ends at
  once). After it, `stand.cooldown_s`; a miss stuns him for `stand.miss_stun_s` by the speed he went in at.
- **Slide** (slide pressed). He slides along the line to where the ball will be when his foot gets there, from his speed
  (`slide.min_speed` at least) to a stop `slide.reach_m` on (by slide_tackle), the foot `slide.lunge_m` ahead of his body
  winning a ball within `slide.ball_radius_m`. Then he lies for `slide.recovery_won_s`, `slide.recovery_missed_s` or
  `slide.recovery_hit_s` and gets up in `slide.getup_s`.
- **Foul.** A lunge's or slide's foot that comes within the body radius (`slide.opponent_radius_m` for a slide) of the
  attacker's centre before it has won the ball ends the round as FOUL.
- **Barge** (barge pressed). It lands on an opponent within `barge.distance_m`, alongside (his bearing off the barger's
  facing between `barge.bearing_min_deg` and `barge.bearing_max_deg`) and facing within `barge.facing_deg` of him. The
  barger's strength over the other's, times his weight over the other's, decides it: at 1 or more the other is pushed
  sideways at `barge.impulse` times that ratio, stumbles and loses the ball if he had it; below 1 the barger is pushed back
  and stumbles.
- **Block.** While the attacker winds up a shot he can still strike (or a struck ball comes at him), with the ball within
  `block.set_range_m` in front of him, he is set: he moves at the jockey pace and his leg reach grows from the body radius to
  `block.reach_m` over `block.grow_s` (by blocking). His body is a capsule of `body_radius_m` and `body_height_m` in the
  ball's path, his leg a sphere of the leg reach around his feet for a ball below `block.max_height_m`. A ball that meets
  either leaves mirrored about the contact, keeping `block.exit_speed` of its speed (less of a faster ball), no steeper than
  `block.lift_deg`, and stays in play; one that goes into his own goal is a goal.
- **Bodies.** Attacker and defender are discs of `body_radius_m`. Overlapping, they are pushed apart along the line between
  them, the lighter one further, inside the boards, and each loses the part of his velocity into the other.

The game draws these from motion clips where it has them and with poses over the outfield figure otherwise; see [animation.md](animation.md#the-defender).

## How a round ends

A tackle never ends the round. The ball runs loose and both go for it: each holds his team's claim on the forecast ball
(lock-on, see [dribbling.md](dribbling.md)) and runs to his own meeting point with it, the defender at a velocity that
closes on the ball no faster than he can brake from, so he arrives able to control it. Whoever's foot arrives first plays
it: nobody is barred from the ball. The attacker's claim is a race (normal foot reach) when the defender's arrival is within
`regain_keeper_margin` of his own, and when the defender is the earlier he still runs and plays the ball if the defender
misses it. Only an exact tie keeps the coin: with both equally near (within 0.05 m) a ball inside the defender's
`control_distance_m`, a coin from the defending generator, drawn once per loose ball, says whether the attacker may touch it.
The defender needs the ball at his feet for `control_s`, so the attacker can nick it in that window. The
round ends as **DISPOSSESSED** once a loose ball has been within `control_distance_m` of the defender's feet, on the ground
and no faster than `control_speed` against his own velocity, for `control_s`. It ends as **FOUL** on a foul. `Outcome` has
both, after the existing values. The round's report carries a `defender` block (auto tackles won, lunges and slides tried
and won, blocks, barges, fouls, seconds in Win, whether he collected the ball and how it was won); a round without a
defender has none. It also says when the ball first got past him: `beaten_s`, seconds from the round's start to the first
tick the ball was at least 0.5 m nearer the goal centre than he was while the attackers had it (one of them controls it, or a
pass they struck is on its way; a shot in flight is not theirs, so a shot struck from in front of him never makes him past by
itself) and he did not win, touch or control it in the 0.5 s after (a tackle, a block or his control of it; a shot, a goal or
the keeper's save in that time does not undo it, and the round ending in that time confirms it unless he controlled the ball),
or null; a rebound an attacker controls goal-side of him counts from that tick; and
`beaten_by`, `pass` when that tick came within 1.5 s of a pass, `dribble` otherwise, `none` when never (`World.IsPast`,
`World.PastTick`, in `World.Measure.cs`). `time_contain`, `time_run_with`, `wins_loose`, `wins_slow` and `wins_last_line`
are in the block (see the modes above); the legacy bot leaves them at 0. Reports saved before these fields read them as null,
`none` and 0.

## The keeper yields

While the defender is on his feet, within `keeper_yield_cone_deg` of the keeper's line to the ball, nearer to it than the
keeper and not beaten (no further from the goal centre than the ball by more than `overshoot_m`), the keeper neither charges
nor rushes: he holds his arc. A ball within a dive of him is still his, and his saves are unchanged. When the keeper's hands
and the defender's foot meet the ball at the same moment, the keeper has it inside his penalty area and the defender outside.

## The probe

`dotnet run -c Release --project src/FootballSim.Batch -- --probe defender` plays 2,000 scripted rounds of the duel per
attacker style and holds the results to bands. The scripted attacker (`ScriptedAttacker.Style`) plays:

- `straight`: runs on at goal, through him;
- `cut`: with the defender in front within 4 m of the ball, turns 50 degrees off the line to goal on the open side until past;
- `skill`: with him within 3 m, plays a step-over to the open side and leaves the same way;
- `shoot`: shoots through him as soon as he is in front within 6 m.

The scripted attacker stands still after its shot, so a round that ends DISPOSSESSED after a shot says little about the
duel. The bands are therefore on the ball won from the dribble (DISPOSSESSED with no shot struck), with the totals printed
beside it. They are regression rows set about five points either side of what the containing bot measures, not a measure of
difficulty (that is the human probe's); the legacy bot is held to its own (straight 65 to 85%, cut 30 to 45%, skill 20 to
35%, shots blocked 10 to 25%):

| | band | measured (seed 1, 2,000 rounds each) |
| --- | --- | --- |
| straight: ball won from the dribble | 85 to 95% | 90.4% |
| cut: ball won from the dribble | 17 to 27% | 23.5% |
| skill: ball won from the dribble | 10 to 20% | 15.4% |
| shots blocked, all four styles' shots together | 29 to 39% | 37.4% |
| fouls, each style | under 5% of rounds | 0.8 to 1.9% |

The blocked share comes mostly from `shoot` (about 71% of its shots are blocked; the other styles shoot after they
are past him). `--sweep awareness=1,50,99` (any defending rating) repeats the table per value without the bands;
`--styles`, `--n`, `--seed`, `--attacker`, `--keeper`, `--defender` and `--scenario` choose what is played.

`--trace-defender <seed> [--style cut] [--every 12]` prints one round: the bot's mode, the action, both players, the ball,
who has it and the events. `--write-defender-replays <dir>` searches scripted rounds for one showing each action and ending
and writes a replay of each. `--set defender.overshoot_m=0.6,...` overrides plain tuning numbers for an experiment.

## The human probe

`--probe human` ([tools.md](tools.md)) plays the duel like a person: one intent per tick from what a player sees, through
the same intents as a pad, never the scripted attacker. Each plan runs 2,000 rounds for the even attacker (`striker_a`) and
the fast one (`data/players/pace_star.json`, the play-test Custom player's sheet), and counts the first attack only. Every
plan finishes as a person would: a low driven shot at the far corner (the ideal power for the distance plus a person's error)
once within 12 m of the goal centre (11 to 13 m by round), or from inside the penalty area (16.5 m) once past the defender;
a loose ball he runs at. A little jitter from the probe's own generator, seeded from the round's seed, moves each trigger
distance (±10%), angle (±5 degrees), wait (±20%) and the aim (±0.3 m). "The wider side" is the side away from the defender's
offset off the ball-to-goal line, or with him on it the side with more room (a coin when central).

- `wide_sprint`: sprints at goal; with him within 6 m veers 35 degrees to the wider side and sprints on until level with him
  or past (or 2 s, or 8.5 m out wide), then goes for goal and shoots.
- `stop_go`: sprints at goal; with him within 5 m slows to a walk under close control (the stick at 0.35) for 0.4 s, then
  sprints at 30 degrees to the side he is not shading until level or past, then finishes.
- `cut_inside`: sprints 30 degrees wide of goal from the start (the side with more room); with him within 3 m cuts back across
  him at 50 degrees to the other side until level or past (or 1.2 s), then finishes.
- `skill_wide`: jogs at goal; with him within 3.5 m plays a step-over (sold away from the exit) or an elastico (toward it),
  by the round's coin, with the right-stick traces of the skill probe and sprint let go, then sprints out at 45 degrees on the
  wider side until level or past (or 1 s), then finishes.
- `jog_cut`: 0.6 s at a third of the stick, then jogs at goal; with him 4 m in front along the line bursts at 45 degrees with
  sprint for 0.4 s to the side away from his offset (a coin when he is on the line), then sprints on at 15 degrees that side
  until past, then finishes.
- `stand_burst`: stands on the ball until he is 2.5 m in front along the line, then bursts at 60 to 77 degrees (by round)
  with sprint for 0.4 s to the side away from his offset, then on at 15 degrees until past, and finishes.
- `sprint_cut`: sprints at goal from the start; with him 5 m in front along the line bursts at 60 degrees for 0.25 s, then on
  at 15 degrees until past, and finishes.

The last three are the ways past him a play-tester found (a slow start then a jog and a cut; standing until he arrives and
bursting away; a sprint and a sharp cut); they are held to their past bands only, not pooled in "all four".

"Past" is the report's `beaten_s` up to the first attack's end (see [How a round ends](#how-a-round-ends): a shot in flight is not
the attackers' ball, so an unblocked shot struck from in front of him is not past him). The bands are the targets set for the
defender on 2026-11-24, constants in `HumanProbe.cs`; "all four" pools the four plans of one attacker. Measured with the
containing bot (seed 1, 2,000 rounds per row; seed 2 gives the same to within 0.1 point, the rows sharing 1,999 of their seeds):

| Plan | Attacker | First attack: goal / miss / held / dispossessed / foul | Past | Band (past) |
| --- | --- | --- | --- | --- |
| wide_sprint | even | 26.1 / 16.2 / 9.6 / 46.3 / 1.7% | 23.8% | at most 30% (ok) |
| stop_go | even | 27.5 / 20.5 / 5.6 / 43.5 / 2.9% | 37.1% | at most 35% (MISS) |
| cut_inside | even | 18.2 / 20.8 / 6.3 / 51.6 / 2.9% | 47.5% | 25 to 45% (MISS) |
| skill_wide | even | 11.7 / 9.8 / 3.4 / 71.3 / 3.8% | 29.0% | 35 to 55% (MISS) |
| jog_cut | even | 29.7 / 18.6 / 10.7 / 35.5 / 5.1% | 65.9% | at most 25% (MISS) |
| stand_burst | even | 14.1 / 9.3 / 2.0 / 73.7 / 0.7% | 30.5% | at most 25% (MISS) |
| sprint_cut | even | 28.1 / 14.0 / 10.2 / 40.5 / 6.8% | 66.8% | at most 25% (MISS) |
| wide_sprint | fast | 40.3 / 12.1 / 9.4 / 37.2 / 0.9% | 45.5% | 40 to 60% (ok) |
| stop_go | fast | 38.2 / 23.8 / 5.0 / 31.4 / 1.5% | 36.2% | at most 60% (ok) |
| cut_inside | fast | 24.6 / 21.4 / 5.7 / 47.1 / 1.1% | 45.5% | at most 60% (ok) |
| skill_wide | fast | 21.6 / 9.2 / 2.0 / 64.7 / 2.2% | 36.3% | 45 to 65% (MISS) |
| jog_cut | fast | 41.0 / 12.3 / 10.1 / 30.6 / 5.8% | 83.7% | at most 45% (MISS) |
| stand_burst | fast | 17.6 / 4.8 / 1.9 / 75.4 / 0.1% | 27.5% | at most 45% (ok) |
| sprint_cut | fast | 43.8 / 10.1 / 6.9 / 28.9 / 10.2% | 88.2% | at most 45% (MISS) |

| All four plans | Even: measured (band) | Fast: measured (band) |
| --- | --- | --- |
| first-attack goals of the best plan | 27.5%, stop_go (12 to 22%, MISS) | 40.3%, wide_sprint (25 to 38%, MISS) |
| median time to get past, rounds that got past | 2.61 s (3.0 s or more, MISS) | 2.32 s (2.5 s or more, MISS) |
| tackles (lunges and slides) tried per round | 0.75 (0.5 or more, ok) | 0.67 (0.5 or more, ok) |
| fouls | 2.8% (under 6%, ok) | 1.5% (under 6%, ok) |

The burst plans still get past a defender who waits on his engage line: the carrier reaches him at 6 to 7 m/s and turns
sideways while he is still flat-footed. Making him give ground earlier (with the carrier a fraction of a second further off)
holds those bursts but leaves the 12 m shot free in front of him and stops pace beating him on the outside, which the other
rows do not allow.

With `--brain legacy` (the same seeds) the legacy bot measures: past 77.8 / 25.6 / 71.0 / 52.7 / 67.4 / 43.3 / 67.1% (even) and 78.7 / 27.1 / 65.3 /
57.8 / 47.8 / 24.7 / 88.4% (fast) for the seven plans in this order, goals of the best plan 32.0% and 43.5%, median time past 2.03 s and 2.06 s,
tackles tried 0.60 and 0.58 per round, no fouls.

A "past" also counts a round where his auto tackle pokes the ball back up the pitch, he chases it beyond the ball and the
attacker gets there first: the ball is then goal-side of him.
