# Passing, receiving and control

A scenario with a team-mate (`"mate": {x, z}` and `team_size` 2 or more; the shipped one is `data/scenarios/two_v_one.json`)
puts a second attacker on the pitch. You always play the ball: your pad drives one attacker at a time, and control moves to the
receiver when a pass is struck and to whoever will reach a loose ball first. The other attacker is moved by the team-mate bot
([teammate.md](teammate.md)); he never plays the ball by himself. A scenario without a team-mate reads none of this and plays
exactly as before.

The code is in `src/FootballSim.Core`: `PassModel.cs` (who the pass is for, where it goes, how hard), `World.Pass.cs` (the
buttons, the strike, the pass record, the defender's reaction delay), `Offside.cs`, and the control jumps in `World.cs`
(`JumpTo`). The numbers are the `pass`, `control`, `receive` and `mate` sections of `data/tuning/default.json` and
`lock_on.reaction_*`. These sections are not part of a replay's tuning hash: a replay with a team-mate carries a second hash
of them (`team_hash`), so replays of the older scenarios stay valid when they change.

## Control follows the ball

- `World.Attacker` is the controlled attacker, `World.ControlledIndex` his index in team 0. The game draws a white ring under him.
- **On a pass.** On the tick a pass with a named receiver is struck, control jumps to that receiver. The passer keeps his
  strike stun (`pass.stun_seconds`, 0.15 s) and is the bot's from the next tick.
- **On a loose ball.** With the ball loose and no pass on its way, control jumps to the team's claim holder (see lock-on in
  [dribbling.md](dribbling.md)) once he has held the claim for `control.claim_jump_seconds` (0.1 s). Two attackers equally
  near a rebound do not flicker: the claim's takeover margin decides once.
- **What a jump resets.** A shot or pass the old player had under way is dropped. Charge, the button edges and the gesture
  reader start afresh for the new player, so a button held across the jump is not a fresh press. For
  `control.stick_settle_seconds` (0.15 s) the new player reads the left stick as at rest, unless it has turned more than 30
  degrees since the jump, so the stick that aimed the pass does not steer his first step.
- Every jump raises a `ControlChanged` event (team, from, to, and the reason: `pass` or `claim`). The game writes it to the
  session log (`control`), and `--trace-team` prints it.

There is no switch button and no bot with the ball.

## The pass

Two passes, both along the ground: a **ground pass** to a team-mate's feet (A / Cross, keyboard F) and a **through ball**
into the space he is running to (Y / Triangle, keyboard R). While a shot winds up, A is still the fake-shot cancel and no pass
is read. A pass button already down when the round starts is not a press.

1. **Press and hold.** The pass winds up like a shot: the same action, the striking-foot rules of a side-foot shot, the same
   soft touch with the ball at his feet. The hold does not set power. It sets how far along the stick you are pointing: the
   selection point is `ball + stick × lerp(pass.min_distance, pass.max_distance, charge)` (1 m at a tap; 22 m for a ground
   pass and 26 m for a through ball at a full hold), the facing standing in for a stick at rest. The hold fills in
   `pass.charge_seconds` (0.4 s) and is then released for you. The HUD's power bar shows it.
2. **Receiver.** Every tick until the strike the team-mates further than `pass.min_distance` from the ball are scored against
   the selection point and the best one is the receiver.
   - Ground: `1 / [(1 + 4·offside) · d · (a / 45°)² · reach]`, with d his distance from the point (six times as much when the
     point is short of him), a the angle between the stick and the line to him (at least 5°), and reach 0.05 when he is within
     0.5 m of the point, otherwise what is left of his distance to it after half a second of his run.
   - Through: `[2 + cos(angle between his velocity and the stick) + (his speed toward the point) / 8] / [(1 + 4·offside) · reach]`.
   - A man beyond the offside line scores a fifth. With nobody beyond the minimum distance the pass goes into space along the
     stick at the selection distance, kept 2 m inside the boards, and names no receiver.
3. **Target.** `PassModel.Plan` searches meeting points at the strike.
   - Ground: points on radii 0.85 to 1.25 of his distance from the ball, fanned ±12° round the bearing to where he will be
     when the ball arrives. A point scores on how much later than the ball the defender reaches the lane to it (a lane he is
     on first is rejected), on how early the receiver can be there, on cos³ of its angle to the stick, and on its closeness
     to where he will be. With none valid the pass goes to where he will be.
   - Through: points along his run `pass.through_ahead_seconds` (0.6 to 1.4 s) ahead of him (toward the goal when he
     stands), fanned up to 15° toward the stick, 2 m inside the boards and 3 m short of the goal line. A point scores on the
     defender's lateness, the angle to the stick, the ball getting there just before he does (about 0.15 s; he may not have
     to wait more than 0.1 s, nor the ball be there more than 0.6 s early), and on the ball arriving gently.
4. **Speed.** A pass leaves rolling, so its travel is the rolling law and nothing is drawn at random: the launch speed is
   the least that brings the ball to the target at `pass.arrival_speed` (5 m/s to feet, 6 m/s into space). A through ball
   into the path of a man running away from the passer is also struck hard enough to be there when he is. The speed is then
   raised in steps of `pass.speed_step` while the defender is still first on the lane, up to the cap:
   `lerp(pass.speed_cap.min, .max, rating / 99)` (14 to 24 m/s, by `ground_pass` or `through_pass`) times the shot's
   body-angle and weak-foot power. If the cap cannot beat him the pass is played at the cap anyway.
5. **The defender on the lane.** At points 1 m apart along the lane, the earliest opponent's arrival (the gap beyond 0.9 m of
   reach, run from his own speed toward it with the movement law's acceleration, after his reaction delay) is compared with
   the ball's. The keeper counts only for points inside his penalty area.
6. **Strike.** The time from press to strike is `pass.execution_seconds` by the charge (0.3 s at a tap, 0.4 s at a full
   hold) plus `pass.angle_extra_seconds` by the body angle to the pass; the strike itself is the touch controller's next
   contact on the chosen foot from then on. There is no direction error on either pass. A poor rating, a wide body angle and
   the weak foot only lower the speed the pass can reach and lengthen the wind-up.

The strike writes the pass record (`World.Pass`: passer, kind, receiver, target, expected arrival tick), raises a `Pass`
event, takes the offside positions and moves control to the receiver.

## Receiving is lock-on

- While the record names a receiver, he is his team's only candidate for the claim on the ball. He meets the pass where it
  was played to, no sooner than its expected arrival, at his feet (no toe-poke from a stride off).
- **Late.** The claim opens to the whole team again when the expected arrival has passed and he is still more than 0.5 s
  from the ball, or his solved arrival is more than 1 s after it (for instance, he was stumbling when the pass was struck).
- **The record ends** when the ball leaves the path the pass was struck on (anyone's touch, a save, a deflection off the
  defender) or is controlled. After a deflection nobody is exclusive and control follows the claim as with any loose ball.
- **First touch.** The stick chooses it, as for any loose ball: at rest a trap, pushed a touch that way. On a team-mate's
  pass the touch's speed and direction spreads are multiplied by `receive.error` at the ball's ground speed relative to him
  (1 at rest, 1.3 at 10 m/s, 1.8 at 20 m/s) and by `receive.rating` (1.3 at `first_touch` 1, 0.7 at 99). His next touch waits
  `receive.cooldown` at that speed (0.1 to 0.35 s) times the same rating factor, or `receive.queued_cooldown` (0.1 s) when a
  shot, pass or gesture is queued.
- **First time.** Shoot or a pass button pressed while the pass is on its way is queued, as on any loose ball (see
  [shooting.md](shooting.md)): it is struck at the first ball he can strike from its own tick on. A pass queued with no time
  for its wind-up and the ball within 150° of his facing is a flick: struck at the first contact, at `pass.flick_speed`
  (0.7) of the speed cap. A pass rolling on toward the goal is still claimed by its receiver (a shot is not).

## Offside

A scenario turns it on with `"offside": true`; the game draws the line across the pitch in yellow.

- **The line** is `x = max(second-last opponent's x, ball x)`. With the keeper and one defender the second-last opponent is
  whichever is further from their goal line, normally the defender. A carrier past the defender makes the ball the line, so a
  square pass is onside. A player's position is his body centre; level is onside.
- **Positions are taken at each strike** (pass or shot) by the attackers: every team-mate beyond the line is flagged until an
  attacker who is not flagged next touches the ball. A shot's rebound therefore cannot be tapped in by a man who was beyond
  the line when it was struck.
- **The offence.** A flagged named receiver: the round ends OFFSIDE on the strike tick, with no control jump. Otherwise it
  is the offence when a flagged player touches or strikes the ball. The claim on a loose ball skips flagged players, so
  control never jumps to one.

Either attacker scoring is a goal. The other endings are as before. In a round with a team-mate the report has a `team` block
(passes, how many a first touch received, offside, control changes) and the report card a line such as `Team: 1 pass (received)`.

## The pass probe

`dotnet run -c Release --project src/FootballSim.Batch -- --pass-probe [--seeds 1000] [--only ground,lane_off,...]` plays one
case per rule with a standing team-mate (the bot off), the seed also moving the start a little, and holds each number to its
target. `--pass-probe --trace <case> --seed n` prints one round.

| Case | Measured (1,000 seeds) | Target |
| --- | --- | --- |
| `ground`: an 8 m ground pass to a standing team-mate, no defender | received 100%; first touch within 0.1 s of the expected arrival 100% | ≥ 99%; ≥ 95% |
| `lane_off`, `lane_on`: the defender standing 1 m off the lane, then on it | cut out 0%; 100% | ≤ 10%; ≥ 80% |
| `jump`: shoot pressed on the tick after the strike | control jumps on the strike tick 100%; the press is the receiver's 100% | 100%; 100% |
| `one_two`: a pass and a first-time pass back | completed 100% | ≥ 90% |
| `square_shot`: a square pass 9 m out, shoot pressed on the way | struck within 2 ticks of the meeting 100% | ≥ 95% |
| `through`: a through ball to a team-mate sprinting level with the standing defender | first touch at 80% of his top speed or more 98%; offside 0% | ≥ 85%; 0% |
| `offside`: the receiver standing 1 m beyond the line | OFFSIDE on the strike tick 100% | 100% |

In `through` the carrier stands over the ball until the press, so the wind-up is the same length every round; a through
ball's receiver takes the ball while it is still moving faster than he is (the rolling ball slows by only 1.2 m/s each
second), which is why it has to be played to arrive with him.
