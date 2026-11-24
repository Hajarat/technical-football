# The team-mate

In a scenario with a team-mate, every attacker the pad does not drive is moved by the team-mate bot
(`src/FootballSim.Core/MateBot.cs`). He writes a move-and-sprint intent each tick and never touches the ball, shoots or
tackles; when he holds his team's claim on a loose ball his legs run to it and control jumps to him
([passing.md](passing.md)). Other players cannot stand in him; the ball does not bounce off him. His numbers are the `mate`
section of `data/tuning/default.json`; the shipped sheet is `data/players/team_mate.json`.

## Fields, not spots

No target spot is ever chosen. A sum of fields F(x, z) is sampled `mate.sample_m` (2 m) either side of him on both axes and
he moves up the slope. The fields are read every third tick.

| Field | Shape | Weight |
| --- | --- | --- |
| Spread | his distance out of a capsule from the carrier to where the carrier will be in 0.2 s, radius `spread_radius_m` (4 m), clamped at 3 m either way | `spread_weight` 1 |
| Opponents | his distance into a circle of `opponent_radius_m` (2.5 m) round each outfield opponent, zero outside it | `opponent_weight` 0.1 |
| Free space onside | his distance to the nearest opponent or board, no more than `space_cap_m` (4 m), and zero beyond the offside line | `space_weight` 1 |
| Pass lane | 1 where a ground pass from the ball, as hard as the carrier can strike one, gets there before an opponent reaches its lane (the pass's own test) | `lane_weight` 1 |
| Gap | closeness to a point 2 m beyond the line, in the middle of the wider gap between the defender and the boards | `gap_weight_armed` 3, only while a run is armed or on |
| Offside line | armed: closeness to a spot `armed_onside_m` (0.8 m) short of the line; on go: his depth beyond the line | `line_weight_armed` 5; `line_weight_go` −3 (it pushes him on) |
| Goal mouth | closeness to the goal centre | `goal_weight` 0.3 |

The drop in free space at the line is what holds him onside until a run. The line he holds to is where the offside line will
be `line_lead_seconds` (0.8 s) from now while it comes back up the pitch at him (a defender stepping up), less a quarter of a
second of his own speed toward it, so he gives ground before it reaches him.

**Speed.** A slope flatter than `still_gradient` leaves him standing, turned to face the ball. Away from the goal he moves at
`backward_jog_share` (0.6) of the jog pace, or as fast as the line is coming when he is within 3 m of it; toward the goal
between the jog and his top speed by attack progress (0 with the ball at the back wall, 1 with it `attack_full_from_goal_m`
from the goal line); on a run flat out. Below `full_gradient` the speed is eased in, so he settles on a ridge instead of
crossing it back and forth. The intent is sprint when the speed is above the jog, the stick the speed over that mode's top.

**Edges and bodies.** He does not move toward a board within `board_margin_m` (1.5 m) of it, nor toward the goal line within
`goal_line_margin_m` (3 m). With a player within `body_avoid_m` of his path he curves round on one side until nobody is
within `body_clear_m`; for `bump_steer_ticks` after a bump he steers at least `bump_steer_deg` off the body he bumped.

**Turnover.** With the ball in the other team's hands or at their feet he walks back toward his own end.

## The run in behind

`PlayerState.MateRun` is where he is in the cycle:

- **Support.** The fields only.
- **Armed.** With the ball within `arm_ball_to_line_m` (10 m) of the line, and either attack progress no more than
  `arm_max_progress` (0.5) or the carrier within `arm_carrier_to_defender_m` (12 m) of the defender, he is pulled onto a spot
  just onside of the line and toward the gap. Not while a pass is under way, and not within `rearm_seconds` (4 s) of his
  last run.
- **Go.** Within `go_within_m` (0.5 m) of that spot, and while the line is not coming back at him faster than
  `go_max_line_speed` (0.75 m/s), the line's pull flips and he runs past it flat out. This is the moment for the through ball.
- **Back.** Past the line with no pass (`go_beyond_seconds`, 0: at once), or caught beyond it by a defender stepping up, he
  sprints back; more than `rearm_onside_m` (2 m) onside he is in support again.
- **Cancelled** to support (or back, if he is beyond the line): a pass under way while he is less than
  `pass_cancel_onside_m` (3.5 m) onside, so the wind-up of your through ball holds him onside; the ball within
  `run_cancel_goal_m` (8 m) of the goal line; his being the pass's named receiver.

## The team-mate probe

`dotnet run -c Release --project src/FootballSim.Batch -- --mate-probe [--seeds 1000]` plays two_v_one with a scripted carrier
who jogs at the defender, stops 7 m short of him and moves off across the pitch when he closes in, and never passes. A round
is measured until the carrier loses the ball. `--mate-probe --trace run --seed n` prints one round (run state, line, slope).

| Measure | Measured (1,000 seeds) | Target |
| --- | --- | --- |
| Onside, of ticks | 90.0% | ≥ 90% |
| A ground-pass lane open, of the ticks the carrier is within 12 m of the defender | 90.7% | ≥ 70% |
| 4 to 12 m from the carrier, of ticks | 86.9% | ≥ 85% |
| Direction reversals per second | 0.34 | < 1 |
| A run in behind, of rounds longer than 6 s | 98.1% | ≥ 60% |

The onside share sits on its target. The defender presses the carrier from the start, so these rounds last 2.9 s on average
and one run that is not rewarded with a pass costs about a second beyond the line; nearly all his offside ticks are the way
back from a run.

## Against two: the defender and the keeper

The defender's bot is the one in [defending.md](defending.md), with three changes that apply only with a team-mate:

- **Reaction delay.** After any strike by the attackers he makes no claim run until the ball has travelled his distance to
  it at the strike times `lock_on.reaction_ratio` (0.35 at awareness 1, 0.05 at 99), or `lock_on.reaction_max_seconds`
  (0.5 s) have passed. The pass's lane test allows him the same delay.
- **Anchor.** With the ball loose he holds, anchored on where the ball was struck while he has not reacted, then on the
  attackers' meeting point with it. He goes for the ball himself only when his own meeting with it is no later than theirs
  (he cuts the pass out), or they have none.
- **The carrier** he presses is the controlled attacker, so it is the receiver from the strike on.

The keeper reads the same carrier, and while a pass is on its way he sets himself for where it will be met instead of where
the ball is ([keeper.md](keeper.md#with-a-team-mate)). The time both need to get across is what a square pass buys.

## Scripted styles and bands

The batch tool's scripted attacker has three styles with a team-mate (`--style solo|square|through`): solo never passes;
square plays one ground pass once the defender is in front within 8 m of the ball; through holds the ball up short of the
defender and plays one through ball as the team-mate's run reaches the line (or when the defender is within 6 m). After the
pass the script plays on as the receiver. `--probe team [--n 2000] [--speed 1]` runs the three and holds them to bands:

| Style | Goal | Held | Dispossessed | Band (goal) |
| --- | --- | --- | --- | --- |
| solo | 6.3% | 7.5% | 86.0% | 3–10% |
| square | 18.3% | 44.0% | 36.1% | 13–24% |
| through | 21.4% | 28.9% | 45.2% | 15–28% |

Square has to beat solo by at least 10 points (measured 12.0). With `--speed 1` it also times 10,000 rounds beside
defender_duel: played alone (the same script) two_v_one takes 1.00 times as long, and a second of play costs 1.2 to 1.7 times
as much in the three styles (the bound is 2). The passing styles' rounds last about 9 s of play against 6, so their 10,000
rounds take 2.1 to 2.3 times as long.

`--scenario two_v_one --trace-team <seed> --style square` prints one scripted round with its passes, control changes and
offside calls.

## The human probe

`--probe human` ([tools.md](tools.md), [defending.md](defending.md#the-human-probe) for how its plans play and finish) plays
two_v_one with `pass_round`, 2,000 rounds per attacker, first attacks only: the carrier jogs at goal and draws the defender;
he presses pass (a 6-tick tap, the stick on the team-mate) so that the pass leaves as the defender comes inside the gap + 1 m
(a gap by the defender's jockeying, a constant in `HumanProbe.cs`: 4.0 m at 1 to 2.5 m at 99, 2.9 m for
`centre_back`), allowing for the defender's closing speed through the pass's wind-up, and only with the team-mate onside by
0.5 m. The receiver shoots first time (shoot pressed while the pass comes) inside 16 m of the goal centre, else after his
first touch, low at the far corner. The band, a constant in `HumanProbe.cs`: first-attack goals 40 to 55% for the even
attacker; the fast attacker's row has none. Measured with `--brain legacy` (seed 1): today's defender measured against a band he is not
expected to meet:

| Attacker | First attack: goal / miss / held / dispossessed | Past the defender | Band (goals) |
| --- | --- | --- | --- |
| even | 38.6 / 21.1 / 8.3 / 31.9% | 93.8% | 40 to 55% (MISS) |
| fast | 45.6 / 21.3 / 7.2 / 25.6% | 93.3% | - |
