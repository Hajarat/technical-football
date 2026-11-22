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

Any of them is struck with the outside of the foot instead when the foot rules give it (below): 15% wider spread, 8% slower
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
  release the foot does not change, with one exception below. The choice is geometry: the angle from where he faces to the
  intended direction, positive toward his right, and where he and the ball are. The same facing, aim, positions and (in a
  dribble) contact ticks give the same foot and surface every time; nothing is weighed, and the weak foot's rating has no
  say. "Own side" is his strong foot's side, and the type's own surface is the one in the table above. The geometry
  (`ShotModel.ChooseByGeometry`) gives the foot, the surface and the rule that decided (`StrikeRule`); the first that
  holds, in order:
  1. Behind: straight behind (within half a degree of 180): the strong foot, the type's own surface.
  2. Wide: more than `foot_by_side_deg` (150 degrees) either way: the foot on the side the ball leaves, the type's own
     surface, whatever his preference.
  3. Ahead: dead ahead, within `dead_ahead_deg` (22.5 degrees) of where he faces, either way: the strong foot, the type's
     own surface.
  4. Across: across his body: the strong foot, the type's own surface.
  5. Weak: toward his own side beyond his outside cap: the weak foot, the type's own surface.
  6. FarPost: within the cap, where the far-post rule below applies: the strong foot, the type's own surface.
  7. Outside: within the cap otherwise: the outside of the strong foot.
  - The outside cap (`ShotModel.OutsideCapDeg`) is `outside_max_deg` by the `curve` rating (30 degrees at 1, 67.5 at 99; 57
    at 72), wider by `outside_foot_bonus_deg` (12) with the `outside_foot` trait, never past the type's ceiling
    (`open_max_deg`, 67.5; `finesse_open_max_deg`, 79, for a finesse or low finesse). Everyone has one.
  - The far-post rule changes one case: an inverted shooter (a right-footer on the left flank, Z below 0, or a
    left-footer on the right) aiming at the far half of the goal line toward his own side within his cap keeps the type's
    own surface instead of the outside, the cut-in curl with the instep. It holds inside the type's distance band from the
    ball to the goal centre (`far_post_band`: power and driven up to 50 m, a chip within 2.5 m, finesse and low finesse at
    any distance; -1 means no limit). The aimed point is the intended direction cast from the ball to the goal line,
    clamped to `max_target_z`; it must lie across the centre line (Z = 0) from the shooter. A shooter within
    `far_post_flank_min_m` (0.3 m) of the centre line is on neither flank and an aim at the centre is at neither post: the
    rule is off for both.
  - An angle on a limit counts as inside it: at the cap it is still the outside, at 150 degrees it is not yet wide, at
    22.5 degrees it is still dead ahead.
  - The contact check (`ShotModel.WithContact`), in a dribble only, hands the shot to the other foot, with the type's own
    surface, in two cases. No contact: the chosen foot has no strike contact at all and the other has one. The dead-ahead
    swap: the Ahead rule decided (either foot is natural there), both feet have a contact, and the weak foot's comes at
    least `dead_ahead_swap_s` (0.1 s, counted in whole ticks: 12) sooner than the strong foot's. The weak foot's rating
    does not gate the swap; its weak-foot spread, speed and curl apply as on any weak-foot strike. The stride never chooses
    the foot otherwise: outside the dead-ahead zone a strike waits for the chosen foot however much sooner the other would
    swing.
  - The swap is taken only when the foot is chosen fresh: the forecast while shoot is held, and the choice at the release.
  - The exception: a foot chosen while a dribble touch was still to be played, or with the ball held at the feet and then
    lost from them, goes through the contact check once more when each foot's next contact is known, unless the strike is
    already committed. After the release the decision made there stands (`World.StrikeGeometry`, the geometry's choice and
    its rule) and the swap is no longer taken, so this can change the foot only in the no-contact case; while shoot is
    still held the choice is a forecast and is made afresh.
- The angle a shot is struck at costs it. The body angle (the intended direction against where he faces, 0 to 180 degrees)
  multiplies the spread (`angle_accuracy`: free to 30 degrees, 1.5 at 60, 2.4 at 90, 3.4 at 120, 4.5 with his back
  to the shot), scales the launch speed down (`angle_power`: free to 45 degrees, 0.82 at 90, 0.55 at 180) and, as it stands
  at the release, lengthens the wind-up (`angle_extra_windup_s`: nothing to 45 degrees, 0.1 s at 90, 0.3 s at 180). The
  legs do not turn him toward the aim, so shooting across a run or with his back to goal comes off poorly.
- A closed goal mouth costs accuracy too (`goal_mouth_accuracy`): by the angle between the posts seen from the ball, as a
  share of the angle they would make seen square on from the same distance (1 in front of goal at any distance, about 0.56
  in the tight-angle scenario, toward 0 along the goal line; on or behind the goal line it counts as the tightest).
- A weak-foot strike has a wider spread, less speed and less curl, each a curve over the `weak_foot` rating (1 to 99, like
  every rating; 50 when a player file leaves it out): `weak_foot_accuracy` multiplies the spread (1.72 at 1, 1.36 at 50, 1 at
  99), `weak_foot_power` the launch speed (0.85 at 1) and `weak_foot_curl` the side spin (0.6 at 1). A weak-foot dribble
  touch is less accurate the same way (`touch.weak_foot_error`: 1.4 at 1).
- Running speed scales the launch speed (`run_speed_power` over speed / top speed: 0.97 standing, 1 at half speed, 1.03
  flat out), the last of the speed factors: angle, weak_foot, surface, run_speed.
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
  foot and surface, how often the far-post rule decided the strike (`far_post`: yes only for the FarPost rule), the
  `run_speed` factor, the share of strikes each rule decided (`rule:`, read at the strike from `World.StrikeGeometry`), the
  share handed to the other foot (`handed over:` `swap`, the Ahead rule decided and the other foot struck; `no contact`,
  the foot differs for any other reason), and every spread and speed factor by name. `--write-shot-replays` prints the
  rule and `far_post` for each replay it writes.
- Every number is in the `shot` section of `data/tuning/default.json`.
