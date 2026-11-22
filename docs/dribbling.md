# Dribbling, movement and skill moves

Every number is in the `movement` and `touch` sections of `data/tuning/default.json`; where reference data gives one it is used.


- Speeds: a jog is 5.3 m/s for every player (`jog_speed`, from reference data), a sprint 6.8-8.5 m/s by `sprint_speed`
  (from reference data); with the ball both are lowered by dribbling, each dribbling style on its own range
  (`top_speed_with_ball.close` for a dribble with no sprint input: close control, a walk, a jog; `top_speed_with_ball.speed`
  at a full sprint; the sprint input blends the one into the other, so the cap never steps), and close control runs at 0.35
  of the close style's lowered top speed.
- Turning: with the ball up to 450 degrees per second by agility (from reference data), less with weight and halved
  at top speed; a heading change smaller than the rate covers in `turn_ease` (0.05 s) eases in instead of snapping, so the
  small corrections between touches do not jerk the body round.
- A sharp turn or a cut costs the balance percentage of speed, shed rather than at once: the running speed never falls faster
  than `balance_deceleration` (12 m/s^2), the legs easing off and the balance loss sharing that one budget.
- Running at the boards a player brakes by himself (at `balance_deceleration`, at most 1.5 m/s in a tick when he turns onto
  boards close by), so he comes to them moving into them at `wall_arrive_speed` (1.2 m/s) or less, with the ball or without;
  along them he runs on.
- Without the ball the turn rate tops out at 360 deg/s (`possession.off_ball_turn_bonus` -0.2, from reference data), and a stick more than
  `reverse_turn_deg` (135) from the facing above `reverse_min_speed` plants the runner: `reverse_speed_loss` (0.65) of his
  speed is shed while he turns about.
- Touches: the stick pushed no further than `walk_max_input` (0.7, from reference data) dribbles with
  walking touches, played as close control (the close style's cadence, `steps_per_touch.close` by dribbling: a touch every
  step from about 26, every second step below). A jog pushes the ball two steps on, and every
  `foot_switch_every`-th (4th) push takes one step more so the other foot takes over; a sprint 4 steps at dribbling 1, 3 from
  about 27 and 2 from about 71 (`steps_per_touch.sprint`, 4 to 1.8 by rating). While the stick turns smoothly faster than
  `turning_stick_rate_deg` a push goes at most `turning_max_steps` steps on (2 at a jog, 3 at a sprint), and so does a sprint
  push within 0.35 s of the stick changing by more than 10 degrees or of sprint pressed or let go. No push is solved for
  more than 1.2 s (one cut short there is solved for the last step inside it that keeps the foot its steps would have
  played, and the next touch is played on that step), nor faster than the player can catch within 1.5 s (at his running
  speed, or 0.8 of his top speed). A sprint push runs out and
  comes back: its step is kept while the full speed trim can still reach the ball; a jog's or close control's while a
  quarter of it can, else an earlier step plays it.
- The touch controller (a closed loop, `TouchPlanner`): every tick in possession it predicts the stick's legs and the free
  ball and looks for the earliest step, past a cooldown of the last push's steps, whose contact window (swing progress 0.3 to
  0.8 of the playing foot) can meet the ball; of that window's ticks it takes the one that meets it best. The legs follow the
  stick, the miss trimmed by a sideways step (eased in and out at `steer_accel`, 6 m/s^2, at most `steer_max` 0.6 m/s) and a
  change of the target speed held to the contact, which reaches the running speed through the speed law and keeps it within
  15% of the pace (as much either way near the off-ball top speed). When the stick's legs cannot reach the ball, the
  go-get-it legs run to where it will be, at the stick's pace or faster up to the off-ball top speed (first within 15% of the
  pace, the leg stretching if need be, and only then at any speed). The steering stops
  correcting within `reach_tolerance` (0.08 m) of the contact point; the foot reaches the rest.
- A demand (the stick turned past the execution angle off the ball's travel and off the run, the stick released for a stop,
  a skill gesture waiting, a shot's strike) shortens the cooldown to `early_execution` of the cadence (by the angle and
  dribbling), never sooner than `early_min_interval` (0.22 s); the go-get-it legs then take over when they play the ball a few
  ticks sooner, and the stride may be quickened by up to a quarter to bring a step's window onto the ball.
- Commit: its execution time before its contact (`execution_time`, by the ball's speed relative to the player: the `windup`,
  0.12 s, for a ball up to 4 m/s relative to him, down to 0.05 s at 8 m/s, so a fast ball coming at or across him is met by a
  touch thrown at it on an earlier swing instead of the legs going after it; a strike keeps the windup) the touch is committed, with its foot, kind (a dribble touch, a turn past
  the execution angle, a cut past `cut_deg`, a stop with the stick released, a knock-on) and surface
  fixed; the stride clock is held so the contact falls in the window. At contact the push follows the stick within 30 degrees
  of the stick at the commit; a bigger change is answered by the next touch. During a committed turn or cut the body opens
  toward the committed direction as far as the contact holds, and for `post_turn_seconds` (0.3 s) after a turn or cut touch
  the body turns at least `post_turn_rotation_rate` (540 deg/s, from reference data).
- Reach: a plain touch plays a ball within `stretch_reach` (0.25 m) of its contact point; a turn, cut, stop or close-control
  touch one anywhere within `turn_reach` (0.45 m at rest to 0.6 m at a sprint) of the body. Beyond that but within
  `feet_turn_reach` (0.6 m, from reference data) the leg stretches for it, with 1.5 times the rated errors. A ball further
  off at the contact is not played: the legs keep going to it and the next step plays it. The rated speed and direction errors
  are cut at two sigma.
- At the boards: a ball closer to a board than his body's radius, the contact zone's lead at rest and its radius (0.72 m) may
  be played by any touch or by the strike anywhere within `turn_reach` of the body, from beside the feet too (he cannot always
  get behind it). When such a ball is about still (under 1 m/s) and within 0.8 m of him (`turn_reach` at rest plus his
  body's radius), the go-get-it legs turn him to it where he stands rather than running at it at the stick's pace. A turn at
  the feet whose pull along the stick would run into a board within `feet_turn_distance` is not made: a touch plays the ball
  instead. A skill move (or a turn at the feet) carries the ball no closer than 2 cm to a board: its path into a board is cut
  short or bent along it. Its step into a board slows as he nears it, as his run does, to `wall_arrive_speed` at the board.
- A stick released for no longer than `release_debounce` (0.04 s; a pad polled at 60 Hz can read neutral for a tick or two
  in a flick) reads as still held; a longer release is a stop (a touch already committed is played as committed).
- Reversals (the stick at least `feet_turn_deg`, 120 degrees, from the ball's travel): up to `feet_turn_max_speed` (3 m/s),
  with the ball within `feet_turn_reach` of the body the sole turns it at once (the drag-back's motion with its own `feet_turn_*` timing, pulled onto the stick's line; no chain, exit burst or
  gesture) while the body comes round onto the stick at the post-turn rate, the player moved by his run alone, slowing within
  `balance_deceleration`. Faster, the stick that far from his travel arms a braking arc: he runs on to the ball (a touch
  already committed along his run plays it on no faster than he runs), and from the turn or cut touch that hooks it toward
  where his predicted path comes round (slowly enough to meet it there) he brakes at `turn_arc_brake` (13.5 m/s^2) to
  `turn_arc_keep` (40%) of the speed he had when the stick turned, never below `turn_arc_min_speed` (2.4 m/s), and holds that
  speed (no other balance loss) while his body comes round at `turn_arc_rotation` (600 deg/s at dribbling 1 to 900 at 99) or
  faster, until he faces within `turn_arc_done_deg` (15 degrees) of the stick (or, within 45 degrees of it, of the ball's
  line), or the ball he goes after runs away from him faster than that speed; then he speeds up along the stick, at first no
  faster than `turn_arc_brake`.
- Skill gestures in possession wait up to `skill.meet_wait_seconds` (1.5 s) on the controller's forecast, which treats them as
  a demand and commits no touch meanwhile; the move starts once the ball is in its radius. A move's own step eases in, and
  after it the controller eases it out (the drag-back's step itself rises and falls to rest).
- The keeper and the view read the next expected contact (`NextTouchEta`, `NextTouchFoot`, `NextTouchPoint`): the committed
  touch's, else the one the controller steers for.
- A lost ball (no one controls it, someone has controlled or struck it this round, play is live, no shot under way) in front of the player is gone for eagerly (`Regain`,
  the `regain_*` keys in `possession`): within `regain_cone_deg` (75) either side of his facing and `regain_max_distance`
  (10 m) of him, or anywhere within foot reach, the go-get-it legs run on to where it will be at the off-ball top speed,
  whatever the stick and sprint button say, and the first touch takes it as soon as it is in reach, even with the stick
  released. When the keeper contests it (within `regain_keeper_distance`, 3 m, of the ball, or reaching it no more than
  `regain_keeper_margin`, 0.3 s, after the attacker, each timed as the gap beyond foot reach over his top speed: the keeper's
  rush speed, the attacker's sprint speed) it is a race at normal foot reach; uncontested he also takes it from as far as
  `regain_reach` (1.2 m) of his body, a toe-poke at 1.5 times the rated errors. Right bumper (the finesse modifier) held
  with the left stick pushed more than 90 degrees away from the ball lets it run (see [controls.md](controls.md)). The trace's legs column
  shows `Race` or `Regain`.

## Skill moves

The gestures that start each move are in [controls.md](controls.md); how a move is drawn is in [animation.md](animation.md); `--skill-probe` is in [tools.md](tools.md).

Move timings (`skill` section): step-over 0.42 s (0.35 s over a still ball), the foot over the ball at the middle; body feint 0.35 s, the plant at the middle; at a run (`step_over_run_blend`: blended between 1 and 2.5 m/s of entry speed) both keep him running, easing no faster than `step_over_run_brake` (6 m/s^2) to `step_over_run_keep` of the entry speed (90% at 4 m/s, 82% at 7.5 m/s), the run turning toward the left stick by at most `step_over_run_turn_deg` (45 degrees), while the ball, untouched, rolls on with him: within the leg's reach at the middle, 0.45 m straight ahead of him along his travel at the end and released 0.15 m/s faster than he runs, so the next touch is an ordinary dribble touch; ball roll: the sole on the ball 0.40 s from standing and `ball_roll_run_seconds` (0.30 s) at a run (`ball_roll_run_blend`: blended between 1 and 2.5 m/s of entry speed; 0.27 s later in a chain), the running speed held through the reach and then easing down no faster than `ball_roll_brake` (6 m/s^2) to `ball_roll_keep` of the entry speed (85% at 4 m/s, 75% at 7.5 m/s) while the player steps sideways with the ball (a quick step that rises over the first half of the roll and eases off toward the release; from standing `ball_roll_follow` of the roll's distance), sized so that at the release the ball is 0.45 m ahead of him along his direction of travel and released at his ground speed along the run, or toward the left stick when it points up to 60 degrees toward the roll's side (held to 60 beyond that; a stick toward the other side keeps the run's line), so the exit touch is an ordinary one; from standing it ends 0.3 m ahead of him; its own exit burst (`ball_roll_burst` 0.5-1.8, `ball_roll_burst_duration` 0.4-1.1 s by rating); drag-back 0.40 s, the sole pulling the ball 0.65 m back; the fake shot: cancel pressed any time from the shoot press until `fake_shot_cutoff` (0.05 s) before the shot's planned strike, its contact on the tick the strike was planned for when the cancel came (at least `fake_contact_after_cancel`, 0.10 s, after the cancel), the shot winding up as a real one until then and never struck (the same legs and the same shot action, so the keeper reads it exactly as a real wind-up until the contact), the fake stop's sole on the ball `fake_stop_seconds` (0.15 s) leaving it at rest `fake_stop_ahead` (0.15 m) ahead of where he would stop, the fake cut's hook `fake_seconds` (0.1 s); the fake's skill action is published from that contact with its `StartTick` at the shoot press, and as `PendingSkill` from the cancel.
Elastico: a reach of at most `elastico_reach_max` (0.10 s), then from the first contact the outside of the foot away from the exit side pushes the ball `elastico_push_distance` (0.30 m) away from it over `elastico_push_seconds` (0.11 s), the foot wraps round it, and at `elastico_contact2_seconds` (0.22 s, the second contact) the inside plays it back across him as the exit touch: toward the left stick turned into 30-90 degrees off the run toward the exit side, pushed as a dribble touch made at once that way would be, onto a swing at least a windup after the move's end at `elastico_seconds` (0.26 s), where no exit touch is made and the controller plans the next touch as usual (with the stick released it is set across in front of him at 45 degrees as a close-control touch); the exit burst starts at the move's end; the run eases down to `elastico_forward_speed` (2.0 m/s) within `balance_deceleration` and over the second half he starts driving toward the exit side (2 m/s); at a run (`elastico_run_blend`: blended between 1 and 2.5 m/s of entry speed) it eases only to `elastico_run_keep` (80%) of the entry speed, no faster than `elastico_run_brake` (6 m/s^2), and he drives toward the side at half that pace; it sells the other side (its exit burst is largest going away from that one). Spin (a roulette toward its side): a reach of at most `spin_reach_max` (0.12 s); then `spin_seconds` (0.60 s) during which the facing turns a full circle toward the side (half way at `spin_contact2_seconds`, 0.30 s, when the other sole takes the ball; the first lifts at `spin_lift_seconds`, 0.25 s), the ball turning with the body in front of him (0.3 m ahead of his facing once settled, so it is dragged back and round), his own travel planned to end `spin_side` (0.5 m) toward the side and `spin_forward` (0.6 m) ahead of where the ball was first taken, the run eased to at most `spin_forward_speed` (2.0 m/s) within `balance_deceleration` (from a run he cannot shed his speed that quickly, so he travels further); at a run (`spin_run_blend`: blended between 1 and 2.5 m/s of entry speed) his pace along the run eases only to `spin_run_keep` (70%) of the entry speed, no faster than `spin_run_brake` (6 m/s^2), so he travels on through the turn; out of it facing and running his original way with the ball ahead (at a run rolling 0.15 m/s faster than he runs); while he spins the keeper reads the ball as at his foot. McGeady spin (the spin's action with its `McGeady` flag set; `mcgeady_*` keys): a reach of at most `mcgeady_reach_max` (0.12 s) by the sole of the foot on the side he comes out to; then `mcgeady_seconds` (0.55 s) during which the facing turns `mcgeady_turn_deg` (270) away from that side, so he ends facing it, the ball turning with the body as in the spin (dragged behind him and round), the other foot's outside taking it at `mcgeady_contact2_seconds` (0.32 s; the first foot lifts at `mcgeady_lift_seconds`, 0.22 s); his pace along the old run is shed evenly to nothing over the turn, and toward the side it rises to the pace he comes out with, `mcgeady_exit_speed` (2.5 m/s) from standing, where he ends at least `mcgeady_side` (0.6 m) to the side of where the ball was first taken; at a run (`mcgeady_run_blend`: blended between 1 and 2.5 m/s of entry speed) he comes out at `mcgeady_run_keep` (55%) of the entry pace, and the move lasts as much longer than 0.55 s as it takes to shed his pace along the run no faster than `mcgeady_run_brake` (12 m/s^2; about 0.6 s from a jog, 0.8 s from a sprint, its contacts at the same shares of it); out of it he faces and runs square to his old run with the ball ahead of him, and its exit burst is largest out to its side. It has no clip of its own yet: the attacker draws it with the spin's clip for the other side (the same turn direction and feet). Heel-to-heel (straight on, led by the foot nearer the ball): `heel_to_heel_seconds` (0.35 s); at `heel_to_heel_contact_seconds` (0.12 s) the leading heel knocks the ball `heel_to_heel_back` (0.15 m) back against the trailing foot, which at `heel_to_heel_contact2_seconds` (0.22 s) flicks it on straight ahead at `heel_to_heel_flick_ratio` (1.3) times his running speed, at least `heel_to_heel_flick_min_speed` (5 m/s) and never faster than he can catch; at a run (`heel_to_heel_run_blend`: blended between 1 and 2.5 m/s of entry speed) it is a short flick paced so that, with him keeping his pace, the ball is `heel_to_heel_run_flick_ahead` (0.45 m) ahead of him 0.2 s after the move's end, where the next dribble touch meets it; he keeps his speed (at a run his entry pace, above the dribble cap too), the flick is the exit touch (the exit burst starts at the move's end) and he runs on to the ball as after a knock-on. Each of the three raises a touch on both contacts. A sole move (ball roll, drag-back, fake stop) reaches for the ball for at most `sole_reach_max` (0.12 s) before the sole lands, its step planned as eased in within `balance_deceleration`. The ball roll, fake stop and fake cut are kept to a smooth speed as the turn at the feet is (the running speed and the step together change the ground speed by no more than `balance_deceleration`), so their built-in stops are shed within it; from standing the step-over and body feint pace themselves; the drag-back keeps its own, faster stop (shed within the budget, a sprinter would run on 2-3 m past the ball he pulls back), a known exception: about 31-34 speed steps per run from a jog or sprint in the skill probe. After any move its step eases out within the run's deceleration budget. The turn at the feet has its own keys (`feet_turn_seconds` 0.25, `feet_turn_distance` 1, `feet_turn_step_back` 0.3, `feet_turn_cooldown` 0.2, `feet_turn_reach_max` 0.1).
Player sheets carry a `skill_moves` rating (1-99): a skill move's exit burst grows with it, and each move chained within 2 s of the previous one uses 0.8 times the previous move's rating. Every move has a cooldown: no move starts until that long after it ends (`step_over_cooldown` and `body_feint_cooldown` 0.10 s, `ball_roll_cooldown` 0.05, `fake_stop_cooldown` and `fake_cut_cooldown` 0.15, `elastico_cooldown` and `heel_to_heel_cooldown` 0.20, `spin_cooldown`, `mcgeady_cooldown` 0.25, `drag_back_cooldown` 0.20), times `cooldown_scale` for the move's effective rating (1.5 at rating 1 to 1 at 99); a gesture made meanwhile waits for it. The exit burst scales with the exit angle: after a step-over or feint by `sided_exit_burst` (most away from the sold side), after an elastico or McGeady spin most out to its own side, after the others by `straight_exit_burst` (0.7 straight on, 1 square to the run, 0.85 turning back). A sheet without it uses its `dribbling`. Skill-move timings and the burst live in the `skill` section of `data/tuning/default.json`.
