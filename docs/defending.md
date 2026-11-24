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
passed to `World.Step` instead; playing a recorded round back that way gives the same round, because the bot draws from its
own generator and the actions from another (both salted from the round's seed).

The bot is in one of three modes (`DefenderBot.Mode`):

- **Hold.** He stands `hold_distance_m` off the ball on its line to the goal centre (2.5 m with the ball 8 m from goal, 5 m at
  20 m), plus a side offset drawn once per hold spell: uniform between `hold_offset_min_m` and `hold_offset_max_m` by his
  awareness (3 to 4 m at 1, 1.5 to 2 m at 50, 0.5 to 1 m at 99), with a random sign. Further than `passive_sprint_distance_m`
  from the spot he sprints to it; nearer he jockeys onto it. While the attacker winds up a shot he steps onto the ball's line
  to the goal centre instead.
- **Press.** He switches once he is at least `goal_side_margin_m` nearer the goal centre than the attacker, the attacker has
  the ball and he has held for `to_active_after_s` (6 s at awareness 1, 2 s at 50, 1 s at 99). A round starts in the middle of
  play, so his first hold counts as served. Pressing, he runs at where the attacker will be `extrapolate_s` on (the attacker's
  feet if that point is behind him; the ball itself once it is more than `tackle_max_distance_m` from the attacker), sprinting
  beyond `press_sprint_distance_m` and coming in at the jockey pace inside `press_jockey_distance_m`. Inside the tackle
  distance and running straight on he bends in from the ball's side, and for `bump_steer_s` after a body contact he steers at
  least `bump_steer_deg` off the line to the attacker.
- **Buttons.** With the attacker within `tackle_max_distance_m` and the ball between them (the direction attacker to ball
  within `tackle_max_angle_deg` of the direction ball to defender) he presses a tackle button for `tackle_hold_s`: the lunge,
  or the slide if a roll made once per press came up (`slide_preference` by slide_tackle). Holding or pressing, every
  `barge_roll_s` with an attacker running alongside he may press barge (`barge_preference` by strength).
- **Back to Hold.** After `active_max_s` of pressing (3 s at awareness 1, 5 s at 50, 8 s at 99), or once he is further from the
  goal centre than the attacker by `overshoot_m`, or when the attacker no longer has the ball, he holds again with a new
  offset and a new wait.
- **Collect.** A ball that has been played and that nobody has: he runs to where it will be, at a velocity that closes on it
  and matches its own, so he arrives able to control it.

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
and won, blocks, barges, fouls, seconds pressing, whether he collected the ball and how it was won); a round without a
defender has none. It also says when the ball first got past him: `beaten_s`, seconds from the round's start to the first
tick the ball was at least 0.5 m nearer the goal centre than he was with the attacking side still on the ball 0.5 s later
(they control it or played it last, and no tackle, block or save came in that time; a goal inside it counts), or null; and
`beaten_by`, `pass` when that tick came within 1.5 s of a pass, `dribble` otherwise, `none` when never (`World.IsPast`,
`World.PastTick`, in `World.Measure.cs`). `time_contain`, `time_run_with`, `wins_loose`, `wins_slow` and `wins_last_line`
are in the block and stay 0: today's bot has no such states or triggers. Reports saved before these fields read them as null,
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
beside it:

| | band | measured (seed 1, 2,000 rounds each) |
| --- | --- | --- |
| straight: ball won from the dribble | 65 to 85% | 75.4% |
| cut: ball won from the dribble | 30 to 45% | 34.0% |
| skill: ball won from the dribble | 20 to 35% | 20.8% |
| shots blocked, all four styles' shots together | 10 to 25% | 19.6% |
| fouls, each style | under 5% of rounds | 0.0 to 1.8% |

The blocked share comes almost wholly from `shoot` (about 92% of its shots are blocked; the other styles shoot after they
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

"Past" is the report's `beaten_s` up to the first attack's end. The bands are the targets set for the defender on 2026-11-24, constants in
`HumanProbe.cs`; "all four" pools the four plans of one attacker. Measured with `--brain legacy` (seed 1, 2,000 rounds per
row): this is today's bot measured against bands it does not meet.

| Plan | Attacker | First attack: goal / miss / held / dispossessed | Past | Band (past) |
| --- | --- | --- | --- | --- |
| wide_sprint | even | 32.7 / 19.4 / 6.9 / 40.8% | 77.8% | at most 30% (MISS) |
| stop_go | even | 10.0 / 7.2 / 3.2 / 79.5% | 25.7% | at most 35% (ok) |
| cut_inside | even | 28.7 / 19.1 / 8.4 / 43.5% | 71.0% | 25 to 45% (MISS) |
| skill_wide | even | 19.9 / 14.4 / 4.7 / 60.9% | 52.6% | 35 to 55% (ok) |
| wide_sprint | fast | 42.9 / 11.8 / 6.3 / 38.8% | 78.7% | 40 to 60% (MISS) |
| stop_go | fast | 16.1 / 4.8 / 1.9 / 77.1% | 27.1% | at most 60% (ok) |
| cut_inside | fast | 33.0 / 14.2 / 6.1 / 46.2% | 65.3% | at most 60% (MISS) |
| skill_wide | fast | 28.9 / 11.8 / 4.0 / 55.0% | 57.8% | 45 to 65% (ok) |

| All four plans | Even: measured (band) | Fast: measured (band) |
| --- | --- | --- |
| first-attack goals of the best plan | 32.7%, wide_sprint (12 to 22%, MISS) | 42.9%, wide_sprint (25 to 38%, MISS) |
| median time to get past, rounds that got past | 2.03 s (3.0 s or more, MISS) | 2.06 s (2.5 s or more, MISS) |
| tackles (lunges and slides) tried per round | 0.60 (0.5 or more, ok) | 0.58 (0.5 or more, ok) |
| fouls | 0.0% (under 6%, ok) | 0.0% (under 6%, ok) |

A "past" also counts a round where his auto tackle pokes the ball back up the pitch, he chases it beyond the ball and the
attacker gets there first: the ball is then goal-side of him.
