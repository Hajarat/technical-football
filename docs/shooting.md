# Shooting

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

Any of them is struck with the outside of the foot instead when the foot chooser picks it (below): 15% wider spread, 8% slower
(`outside_accuracy`, `outside_speed`), and a finesse then curls the other way.

- Charge fills 0 to 1 in 0.3 s while shoot is held and is frozen at the release (or at 1). The strike comes no sooner than
  0.45 s after the press (the wind-up) and 0.12 s after the release (the earliest strike tick), and it takes the place of the
  attacker's next touch: from the press no dribble touch is committed (one already committed is played), and the strike is
  the touch controller's first eligible contact from the earliest strike tick on (the first tick of a swing's contact window
  that meets the ball) on a swing of the foot chosen for the shot. It waits for that foot: up to a step longer than the next
  touch would have taken. It is committed like a touch one windup before it and played in that window; from the commit its
  contact tick and foot are fixed. With the ball at the feet at the press (within foot reach, no touch committed) a soft touch
  keeps it there and the strike falls at the earliest strike tick, on the chosen foot, whatever the stride clock's swinging
  foot (the view poses the whole body for the shot itself); if the ball is not strikable then, the chosen foot's next contact
  strikes it. A shot in possession is never dropped: he keeps running on to the ball and strikes on arrival. A shot pressed
  without the ball, or one whose ball the keeper took, is struck as soon as the ball is in reach from the earliest tick, or
  dropped after `round.shoot_buffer`. From the press to the strike the left stick only aims: the legs run on to the ball (the
  go-get-it legs toward its intercept, holding at least the runner's own running speed), whichever way the stick is swung.
  The soft touch leaves the ball rolling along the shooter's velocity.
- The striking foot and the part of it are chosen once (`ShotModel.ChooseStrike`): at the press for the aim the stick has
  then, so the strike is forecast on that foot from the press, and again at the release for the aim it locks. From the
  release the foot does not change. (One exception, before the strike is committed: a foot chosen while a dribble touch was
  still to be played, or with the ball held at the feet and then lost from them, is chosen once more when each foot's next
  contact is known.) Each foot is scored on the ball-out angle, the intended direction against where he faces, counted
  toward that foot's own side, and the cheaper foot strikes, the strong foot on a tie:
  - A foot cannot strike a ball leaving more than `open_max_deg` (67.5 degrees; `finesse_open_max_deg`, 79, for a finesse or
    low finesse) toward its own side: beyond that only the other foot can. Across the body a foot reaches all the way round
    to 180 degrees. So for a right-footed player a shot more than 67.5 degrees to his right is the left foot's, and one more
    than 67.5 degrees to his left is the right foot's.
  - In between both feet are scored: opening the hips toward the foot's own side from `outside_min_deg` (22.5) up to its
    limit (`w_open`), the ball lying on the other foot's side of the body (`w_ball`, from `foot_ball_free_m` over
    `foot_ball_span_m`), the weak foot by its stars (`w_weak` at one star, nothing at five), and in a dribble the wait for
    that foot's swing beyond the other's (`w_wait` per `foot_wait_max_s`; a foot more than `foot_wait_max_s` later is not
    waited for while the other can strike, and a foot with no contact at all gives way to the other). With a three-star weak
    foot the weak foot takes over at about 43 degrees open, with a one-star at about 63.
  - A player with the `outside_foot` trait strikes the band from `outside_min_deg` to `outside_max_deg` (by the `curve`
    rating: 30 degrees at 1, 67.5 at 99) toward his strong foot's side with the outside of that foot, for the flat cost
    `w_outside`. Nobody else ever uses the outside for a shot.
- The angle a shot is struck at costs it. The body angle (the intended direction against where he faces, 0 to 180 degrees)
  multiplies the spread (`angle_accuracy`: free to 30 degrees, 1.5 at 60, 2.4 at 90, 3.4 at 120, 4.5 with his back
  to the shot), scales the launch speed down (`angle_power`: free to 45 degrees, 0.82 at 90, 0.55 at 180) and, as it stands
  at the release, lengthens the wind-up (`angle_extra_windup_s`: nothing to 45 degrees, 0.1 s at 90, 0.3 s at 180). The
  legs do not turn him toward the aim, so shooting across a run or with his back to goal comes off poorly.
- A closed goal mouth costs accuracy too (`goal_mouth_accuracy`): by the angle between the posts seen from the ball, as a
  share of the angle they would make seen square on from the same distance (1 in front of goal at any distance, about 0.56
  in the tight-angle scenario, toward 0 along the goal line; on or behind the goal line it counts as the tightest).
- A weak-foot strike has a wider spread (`weak_foot_step` per star below five), less speed (`weak_foot_power`: 0.85 at one
  star) and less curl (`weak_foot_curl`: 0.6 at one star).
- The attacker publishes `StrikeEta` and `StrikeFoot` from the press on, as the best estimate: the later of the earliest
  strike tick (while shoot is held, as if it were let go on the next tick with the aim as it is) and the controller's
  forecast contact on the chosen foot, then the committed strike; once published it may move later (by whole steps, while
  the run cannot reach the ball) but never more than a tick earlier. The shot action's `ContactTick`, `Foot`, `Surface` and
  `AimYaw` (the intended direction after the aim assist, following the stick until the release and fixed from then on)
  carry the same estimate, and the keeper reads the contact tick.
- Aim is the left stick at the release; a stick at rest then uses its last pushed direction if that was within 0.5 s. It is
  locked from the release to the contact. The stick is cast from the ball to the goal line and the aimed point clamped to
  `max_target_z` (8 m) either side of the centre, wider than the frame, so a wild aim misses. Aim assist still applies (within
  1.5 m outside a post).
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
- `--probe shots --scenario <id>` prints, for the scripted attacker's shots, how they ended, the share on target (the ball as
  struck, flying free of the keeper, goes in), the launch speed, the time from the press to the strike, the body angle, the
  foot and surface, and every spread and speed factor by name.
- Every number is in the `shot` section of `data/tuning/default.json`.
