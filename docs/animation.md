# Character models and animation

The players are rigged MPFB2/MakeHuman bodies (`game/assets/characters/player.glb`, `keeper.glb`, Mixamo-compatible skeleton, CC0 assets only).
`PlayerFigure` still computes every joint procedurally each physics tick; `SkinnedBody` poses the skeleton from those joints (knees, ankles, elbows
and hands land on them), tints clothing and skin from `FigureKit` and puts the shirt number on the back. Foot-contact and pose measurements come
from the joints, so they are the same with either `--figure` value.

In `--attacker-anim mocap` the running pose itself is chosen by motion matching by default (`--attacker-loco match`, `src/FootballSim.Anim/AttackerMotion.cs`): every 12 ticks, and when a recording runs out or falls out of step, `Matcher.Search` picks a frame of the locomotion set (`AttackerChooser`: the walk, jog, run and turn recordings of `game/assets/anim/attacker` with their mirrors) by the playing frame's feet and hips, the trajectory, the simulation's stride phase and the turn demand (the stick's angle off the facing); the recording is then played at the rate that keeps its stride on the simulation's stride clock, changes blended by the inertializer, and its pose goes through the same feet, pelvis and layer steps as the cycles' below. `--attacker-loco cycles` keeps the stride cycles. Skill moves and shots take the share of their running clip against their standing one from the chooser too: the matcher's cost of entering each clip (`AttackerChooser.EntryShare`: the pose shown, his speed held to the recorded range, the ball relative to him for a skill move, the stick's angle, the time to the contact for a shot), all of the cheaper clip unless the two are nearly tied; the game prints a `SKILL_CHOOSER` / `SHOT_CHOOSER` line with that share and the old speed-only one. By default (`--attacker-anim mocap`; `--attacker-anim procedural` keeps the procedural run cycle above) the attacker's running body comes from motion-captured stride cycles instead (`game/AttackerAnimator.cs`,
`src/FootballSim.Anim/CycleMotion.cs`): four seamless one-stride loops in `game/assets/anim/attacker/cycles/` (walk 1.8 m/s, jog 2.9, upright
run 4.5, athletic run 5.2; see `game/assets/anim/SOURCES.md`). Their playback position is a function of the simulation's stride clock: each foot's
mid-stance falls on the other foot's swing middle, so the drawn swinging foot is the simulation's (a step in the clock's rate, the touch
controller nudging the stride, is eased: the drawn phase stays within 0.06 of the clock's). The two cycles either side of the smoothed speed
are blended at the same phase (walk 1.8, jog 4.0, upright run 5.8, athletic run 10.0 m/s: close control at 2.6 m/s is walk and jog, the
jog at 4.85 jog and upright run, the sprint at 7.4 the upright run and the athletic run; the shown trunk leans about 7, 13 and 20 degrees); a step shorter than the cycle's
shortens the feet's fore-aft travel, a longer one shortens its stance (more flight) instead of stretching the leg; a landed foot is held where
it landed until its heel rises. On top come the simulation's facing, the acceleration lean, the bank into turns, the head
looking along the travel and partly at the ball, the arms a little wider in close control, and a foot about to play the ball swings low.
Body language is laid over the cycles as layers (`src/FootballSim.Anim/BodyLanguage.cs`, `game/PlayerFigure.BodyLanguage.cs`), each eased by a
critically damped spring and faded out while a skill move or a shot owns the body. Turning: the body banks toward the inside by
atan(sideways acceleration / g) x 1.15, from 0 below 0.6 m/s^2 of it to all of it from 1.6 (so the stride's own sway and a steering wobble stay
upright), capped at 17 degrees at a jog and 24 at a sprint; the pelvis drops 0.12 m per radian of bank and shifts inside; the shoulders turn
ahead of the hips by the turn rate (sideways acceleration over speed) x 0.13 s (at most 22 degrees) and the head ahead of the shoulders by
x 0.15 s (at most 24 degrees) plus the facing's own turn rate x 0.05 s (at most 17 degrees: it leads as the body starts to turn); the figure's look follows the facing, which turns ahead of the run, aimed ahead by the turn rate x 0.16 s plus the
facing's own turn over the look's easing, and leaves the ball for it; the outside arm swings out 1.1 radians per radian of bank.
Visible touches (with the cycles in, by their weight; a touch from standing keeps the procedural swing): the windup's foot arrives moving
through the ball, from 0.18 m behind the contact along the ball's launch direction on an x(2 - x) curve (flat at its start, steepest at the
contact), and follows through along it relative to the body, up to 0.20 m eased out over 0.125 s and lifted 7 cm, the touch pose held whole
for 0.09 s (at most half the recovery), never further from the hip than 0.96 of the leg unless the contact itself is, and handed over to
the procedural recovery from 0.085 s to 0.125 s, so a skill move or shot that starts then takes over the pose it always did; a laces
touch points its toe down through the follow-through (0.35 rad); the playing leg's knee turns toward the boot's heading plus half the
launch direction (0.8 of the way, at most 8 of its weight a second, so a withdrawn forecast never twists the leg); and a touch more than
0.2 rad off the facing turns the trunk toward it by 0.3 of its angle (at most 10 degrees) by its weight.
Speeding up and slowing down: on top of the acceleration lean the pelvis already takes (12 degrees at 4 m/s^2), the trunk leans forward a
further 0.035 rad per m/s^2 of forward acceleration (at most 0.22 rad) and back 0.03 rad per m/s^2 of braking (at most 0.24 rad), faded in
from 0.4 to 0.8 m/s^2 so a steady run's jitter stays upright, eased with a 0.06 s half-life; braking also drops the pelvis 1 cm per m/s^2
(at most 6 cm: deeper knees).
Dribbling posture: with the ball in his possession and within 1.2 m (fading out by 1.5 m, eased with a 0.12 s half-life) the trunk leans
0.11 rad further forward and the pelvis sits 3.5 cm lower, the head pitches 0.12 rad further down (plus 0.6 of any braking lean back, so the
eyes stay on the ball) and looks down at a ball right at his feet as at one 0.15 m away, both elbows open by up to 0.45 rad (keeping at
least 0.35 rad of bend: the hands carried lower) and both arms go 0.12 rad wider.
Touches stay the figure's: its touch pose is the swinging leg's target, so the boot's surface meets the ball on the contact tick as before.
Standing and idle steps, starts from rest and stops below a walking pace and kicks stay procedural: the procedural pose is
still solved every tick, the cycles' share of it rises to 1 while stepping above a walking pace with no skill or kick, eased over 0.1 s,
and the foot pitch is rate-limited, so a hand-over does not pop. A shot runs on the cycles (`PlanShot`, `game/PlayerFigure.Shot.cs`): from the
press he runs on as he would without it and dribble touches are drawn as touches (the shot's soft touch, which the core makes on the press
tick, is not drawn: it only keeps the ball rolling with him). The strike sequence is timed backwards from the published contact estimate
and starts when that comes within its length (0.28 s for a full-power shot, 0.22-0.26 s for the other kinds, times 0.6 + 0.4 x the
charge): the striking foot is drawn back from wherever the stride has it, swings through like a pendulum and meets the ball with the
action's surface at its contact point on the contact tick, then follows through relative to the body at the swing's own speed and hands
back to the cycles. If the estimate moves later before the commit, the sequence eases out from where it was over 0.12 s and starts again.
The plant is the stride's own stance foot when the stride has the other foot down at the strike (the core's strike that takes the place of
a touch is on the swinging foot); otherwise (standing, or an at-feet strike on the foot chosen for the shot) that foot steps, from one stride
step before, to beside the ball and is held where it lands until the body has run on (the hip 0.9 of a leg from it, or 0.1 s after the
contact). The upper body takes half of the procedural shot lean and shoulder turn over the cycles during the strike.

By default (`--shot-anim mocap`, in `--attacker-anim mocap` only; `--shot-anim procedural` keeps the computed shot above) the struck shot is drawn from clips instead (`game/ShotClipLayer.cs`, the
clips in `game/assets/anim/attacker/shots/`, see `game/assets/anim/SOURCES.md`). There are two sources: two motion-captured instep kicks
cut by `tools/anim/shots.py` (a CMU kick from a jog and one from standing), and 46 kicks keyed by hand and solved by
`tools/anim/shot_author.py` from one kick per kind in `tools/anim/shot_moves/*.toml`: power, finesse, chip and outside of the foot, each for
a run and from standing, for either foot, and for each ball-out angle bin (the angle the ball leaves at against his facing, positive toward
the striking foot's own side). Power has bins at 45 degrees open and 45, 90, 135 and 180 across the body at a run (45 open, 45, 90 and 180
across from standing), and the captured kicks are its 0-degree bin; finesse has 0, 45 open, 45 and 90 across; chip 0 and 45 across; outside
45 open. In an authored clip the body comes round the ball over the last stride by a share of the bin's angle and the striking leg swings
the rest, so the kick itself is the same kick along the ball's flight; the tool moves the contact pose until the boot meets the ball.

The clip is picked (`ShotClipLibrary.Pick`, `src/FootballSim.Anim/ShotClips.cs`) by the kind of kick (the outside of the foot when the core
chose it, else a chip, a finesse for finesse and low finesse, a power kick for power and driven), the entry, the striking foot and the bin
holding the ball-out angle: the core's aim (`BallAction.AimYaw`) against his facing. The bin behind him (180 across) holds the same angles
from the foot's own side too, so a wide shot struck by the foot on the side the ball leaves is drawn with it. A kind with no clip for that angle is drawn with the
power kick's nearest bin and logged `SHOT_CLIP fallback`. While shoot is held the aim follows the stick, so the pick is made again when the
kind, foot or bin changes, and once more on the release, which fixes them; the core does not change the foot after the release, and if it
ever does `SHOT_CLIP foot_changed` is logged as an error and the shot drawn again on that foot. The standing and the running clip of the
same kind and bin are blended by his speed at the press (all standing below 1.5 m/s, all running from 2.5 m/s; the captured pair by the
chooser's entry costs, a power or driven shot keeping at least 0.4 of the running kick's longer swing); when they are not the same bin, the
entry is the standing clip below 2 m/s and the running one from there. The blend runs on one timeline through the clips' events (`start`,
`takeoff`, `backswing`, `plant`, `contact`, `end`). The clip is entered as late in its wind-up as the published time to the contact asks
for (a running kick no earlier than its striking foot's takeoff, so no foot of the clip stands on the ground while he runs on), never played
fast to fit; it then runs on that estimate so `contact` falls on the strike tick (a later estimate slows it; one that would slow it below
0.45 before the plant hands back and enters again). The pose goes on his position at the heading that puts the clip's ball-out direction on
the aim (the aim less the bin's angle), turned from his facing by at most half the bin's width and fixed from the plant; from
the entry to the plant it is drawn toward where the clip has the body relative to the ball at the core's contact point (at most 0.6 m), and
back to his position through the follow-through, the plant foot reaching the rest of the way to its place beside the ball. Feet the clip
has down are held where they land (`PlantLock`; the plant foot through its heel rising, up to 9 cm), the striking foot's surface (as the
core says: laces, inside or outside) is pinned to the ball on the strike tick (ramping in over the last 0.07 s, out
over 0.06 s), with the boot's own surface on the ball and the contact check's surface point (the same offset turned by the foot's heading
only) on it too. Charge and kind scale the striking leg's swing about its contact pose (power and driven 0.8 + 0.2 x charge of the clip's,
the other kinds 0.6 + 0.2 x charge). A finesse or chip drawn with a power kick (a stand-in) has the leg turned out onto the inside of the
foot, or the trunk leant back, by hand; a clip of its own kind has that in its poses. After the
contact the follow-through plays on to its `end` (up to 1.3 times as fast at a sprint) even when the shot's recovery ends sooner. The entry
and the hand-back to the stride are inertialised as the skill clips' are. A fake read during the wind-up hands back to the procedural shot
drawing on that tick. `SHOT_CLIP` lines report each shot's pick (`clip_kind`, `angle`, `bin`, `clip`), entry, contact (`skin_err_m`: the
boot's own surface to the ball; `hips_off_aim_deg`: where the hips face against the aim) and hand-back, and on quit `SHOT_CLIP summary`
counts the shots drawn, stand-in picks, foot changes after the release, hand-backs before the contact and the timeline's jump onto the
contact (`max_snap_frames`, and how many were over 2 clip frames).
`SHOT_CLIP_DBG=1` prints the layer's state per tick.

Skill moves (`--skill-anim clips`, the default) are drawn from hand-authored full-body clips (`game/SkillClipLayer.cs`,
`src/FootballSim.Anim/SkillClips.cs`, the clips in `game/assets/anim/attacker/skills/`, see `game/assets/anim/SOURCES.md`) for all nine moves
the simulation makes, each foot (and side), from standing and from a run; the quick turn at the feet (a drag-back kind with its own short
timing, told apart by its sole span under 0.3 s) stays procedural. When a move starts, its standing and running clips are blended by the
speed he came in at (0 at the standing clip's entry speed, 1 at the running clip's, smoothstep between; fixed for the move). The clip's time
follows the core's ticks: its `start`→`contact` span plays over the move's start→contact tick (a step-over's or feint's contact is its middle
tick; a fake shot is drawn as the real shot's wind-up until 0.10 s before its contact, and its clip starts there, part way in), `contact`→`release` over contact→end tick (with the
spin's `lift` and the second contact `contact2` of the elastico, spin and heel-to-heel on their ticks), and `release`→`end` in real time after the end tick (the settle), through a monotone cubic, so
each event lands on its tick and the rate changes smoothly from one span to the next; the standing and running clips each have their own map (a running clip may have its own spans, as the running spin's swap at the core's 0.05 s), so the two are blended at the same event on the same tick. A reach the core makes more than 1.5 times shorter than
the clip's starts the clip part way in (a fake shot counts its wind-up from the shot's press). The pose relative to the clip's root (a straight
line under its hips from first frame to last, heading fixed at its first frame, so a turn the clip makes is kept; for the spin the clip's
own heading is taken out, since the simulation's facing makes the 360 degrees) is put on the simulation's position and facing. The hand-in from the cycles or the standing pose and the hand-back at the clip's end are inertialised (each bone's
offset and its velocity die away, half-life 0.08 s); at the hand-back each foot keeps its offset from the figure's ankle, dying away while the
foot swings and held while the figure has it planted. On top of the clip: the playing foot's surface (the sole, or the inside or outside the
core names, the heel for heel-to-heel's first contact; the second span's foot and surface are the core's second contact's) is brought onto
the live ball by leg IK over the clip's on-ball spans (and at each touch the core also makes: a fake cut's hook, an elastico's two contacts), the weight rising through the reach
and falling over 0.08 s after it, with the contact direction the clip has on its own ball; on the end tick it holds where the ball was, as the
core's exit touch moves it; the hips come up to 0.18 m toward a ball the leg cannot reach. A step-over's circling foot keeps its path relative
to the live ball, lifted over it (1 cm clear, the lift changing at most 1.5 m/s) where the hand-in from the stride would carry the boot through it. A foot the clip plants is held where it landed (`PlantLock`), let go when the clip lifts it. A clip whose move file
declares its stances (`[stance]` in `tools/anim/moves/`, written to clips.json; the ball roll at a run) has its planted feet from those
spans, not read off its motion: such a foot is taken from the hand-in's offset onto the clip's own foot as its first stance begins
(stepping into place at up to 6 m/s, lifted on the way), held from its first stance tick where the clip's foot stands then, and follows
the clip's own foot from then on, its hops and landings as authored (at the take-off the drift between the simulation's travel and the
clip's root, carried from the spot it stood on, dies away over 0.14 s). A running clip with a second ending (the clip of the same name
with `_swap`: the ball roll's support foot swinging into the exit touch instead of standing under it; the step-over's and spin's) is crossed over to it in 0.05 s
while the exit touch the core forecasts is the support foot's, and back when it is not. A touch the figure draws after
the move began (the exit touch) takes its leg by the touch's weight, so its boot meets the ball on the contact tick as any touch; made by
the foot that made a touch of the move's own (a fake cut's exit touch, 0.1 s after the hook), its leg moves at most 0.06 m a tick against the
hips while it winds up and reaches at most 0.96 of its length, its foot turns toward the touch's at most 0.24 rad a tick, and the hips come
toward the figure's foot when it is out of reach. A fake cut
whose hook is the outside of the foot (the cut toward the shooting foot's side) has no clip and is drawn procedurally, as everything is with
`--skill-anim procedural` or `--attacker-anim procedural`. Build steps, asset list and licences: `game/assets/characters/SOURCES.md`.

## The defender

In a round with a defender the game draws him with the outfield figure in blue (`Match.Defender`, a `PlayerView`): his
running comes from the same stride clock and run cycles as the attacker's body without the ball, and
`game/PlayerFigure.Defender.cs` lays procedural poses over that body from what the core publishes (`FigureFrame.Defender`:
the action, its seconds, its side, the leading foot of a lunge or slide, the leg reach while set): a crouch for the jockey,
the leading foot put out to the core's foot for the lunge, feet first along the ground for the slide, lying and getting up
after it, a leg out for the block, a lean for the barge and a pitched, wobbling trunk for the stumble.

By default (`--defender-anim mocap`; `procedural` keeps those poses alone) the defending itself is drawn from motion clips
in `game/assets/anim/defender` (`game/DefenderAnimator.cs`), after the figure has posed the body, so the pose above is both
the fallback and what every clip is blended from and back to. The chooser (`src/FootballSim.Anim/DefenderChooser.cs`)
turns the action into a clip set:

| Action | Set | How it is played |
| --- | --- | --- |
| Hold, Jockey or Press, standing (under 0.35 m/s; stepping from 0.6) | `set:idle` | the stance clips, searched every 12 ticks |
| Jockey, and Hold or Press while he holds jockey, stepping | `jockey` | the side shuffles, the dropping off and the closing down in one set: the matcher picks by his velocity against his facing, and the clip's rate follows his speed |
| Lunge | `lunge` | the clip whose leading foot is on the lunge's side (`ActionSide`; the other side is the mirror), entered where its own time to `contact` is the core's time to the contact and played 0.6 to 2.2 times as fast so the two land together; the root is free while he is planted (the clip steps in and back) and returns after it |
| Block | `block` | the clip with the leg of the ball's side out, its `contact` (the widest frame) landing when the core's leg reach has grown |
| Slide, then Grounded | `slide` | the baked slide with the leg of the slide's side leading, played through the lying that follows |
| GettingUp | `getup` | entered as early as its `up` can still land 0.8 of the way through the core's get-up at up to 2.6 times its speed |
| Stumble | `stumble` | played through once from the frame nearest his pose |
| Barge | `barge` | no kept clip: the figure's lean |
| Hold or Press running, Recover, Collect | none | the figure's run cycles |

`KeeperMotion`, the keeper's engine, plays the sets (the search, the blend at every switch, the planted feet held where they
landed, the chest kept square to his facing in the stance sets). The tackling foot is brought onto the core's foot point
(`TackleFoot`) by leg IK, its weight rising over the last 0.2 s before the contact, held while the foot is live and let go
over 0.2 s; the blocking foot goes out to the side by the core's leg reach the same way. The attacker's figure has the same
layer for his reactions (`FigureDefender.Reaction`): `stumble` while the core says he stumbles, `barged` when a barge's
push carries him (the audition kept no clip of that, so he plays the stumble clips while the push carries him), `fall` when the
round ended in a foul. A set with no kept clip
keeps the procedural pose. `--defender-anim-log` prints a `DEFENDER_MM` line at every switch (who, action, set, clip, frame,
rate); `--defender-anim-drop-set a,b` leaves sets empty to check the fallback. The clips, their sources and the audition's
verdicts are in `game/assets/anim/SOURCES.md`.

`--defender-gallery <dir>` (`game/DefenderGallery.cs`) plays every action and reaction from a script, without the
simulation, and writes a contact sheet per script (side view over front view, each tile labelled with the action and the
clip drawn) and a `DEFENDER_GALLERY` line: the clips that played, the fastest bone turn (`POP` above 1,500 deg/s), the
lowest joint and, for a lunge, the tackling ankle's distance from the core's foot point on the contact tick.

## Players

Attacker and keeper are the rigged bodies above. `PlayerFigure` (`game/PlayerFigure.cs`) computes every joint procedurally from the simulation's state and events only (run cycle with planted stance feet, dribble touches, shot wind-up, strike and follow-through, keeper set stance, dive and hold); the procedural pose is the fallback (`--figure procedural` draws the primitive-mesh figure) and the base that the motion clips are laid over.

The keeper (`--keeper-anim mocap`, the default; `procedural` is the fallback) is drawn from motion-captured clips in `game/assets/anim/keeper` (`game/KeeperAnimator.cs`): in every state that has a clip set (stance and movement, saves, lying and getting up) the pose is searched, blended and foot-locked from the clips, with the save's contact timed onto the simulation's contact tick and the gloves or reaching foot brought onto the simulation's hand point by IK. A state with no kept clips keeps the procedural pose (`game/PlayerFigure.Keeper.cs`). The clips come from the CMU motion capture database, and the five `baked` ones from the game's own procedural keeper (`game/assets/anim/SOURCES.md`).

A ring under the attacker is solid green while in possession and thin grey otherwise; the ball's ground ring is green while a player is in possession and orange while it is loose.

## Measurements printed by the game

In replay mode the game also prints `FOOT_EVENT` per touch/strike and, on quit, `FOOT_CONTACT n= mean_m= max_m=` (touching foot's instep to the ball surface at each Touch/Strike), `FOOT_SLIDE n= mean_m= max_m=` (the attacker's planted feet: the largest movement of a foot per stance, flat on the ground; a foot rolling onto its toes is no longer planted) and `FOOT_SLIDE_KEEPER n= mean_m= max_m=` (the same for the keeper's planted feet while his rush and charge use the run cycle). With `--figure skinned` it also prints `SKIN_CONTACT n= mean_m= max_m=` (the skinned boot's contact surface for the touch's surface — inside, laces, outside or sole — to the ball surface at each touch's contact tick; target max <= 0.03 m), `FOOT_JUMP ctx=<skill|exit|touch|shot|other|all>` lines give, per context of the attacker's tick (a skill move, from a move's end until the first touch after it has recovered, a dribble touch, a shot, anything else), the largest and the 99th-percentile world move of either foot (the larger of its ankle's and boot tip's) in one physics tick (`dt_s`, one rendered frame at 120 Hz) and over two ticks (`max60_m`, one frame at 60 Hz), with the tick and foot of the largest, `over_m` (the largest move over its limit: 0.10 m per tick for the procedural attacker; with `--attacker-anim mocap` 0.10 m + 1.2 x speed x dt, as a running swing foot travels two steps per step of the body, about 0.14 m at a sprint) and `dv_max_mps` / `dv_p99_mps` with `dv_tick` / `dv_foot` (the largest change of a foot's velocity, ankle or boot tip, from one tick to the next, m/s per tick: a pop whatever the speed); `STRIDE n= step_s_mean= step_s_cv= step_m_mean= step_m_cv=` gives the attacker's running steps (plant to plant of alternate feet above 2 m/s, no skill move or shot: time, ground distance and their spread),
and `POSE_LIMITS` carries the running-only `run_min_pelvis_ratio`, `run_lean_max_deg` and `run_bob_max_m`; the dribble probe behind these targets is `dotnet run -c Release --project src/FootballSim.Batch -- --probe dribble`.

`FOOT_SKATE ctx=<skill|shot|touch|other> n= max_mps= p95_mps= over_0.5= worst_tick= worst_foot=` (printed with the FOOT_JUMP lines) measures foot skate: on every tick an attacker foot's sole is within 0.03 m of the ground (its boot tip, or its heel taken as the ankle less the ankle's rest height) and the foot is not pinned to the ball by a skill clip, its speed over the ground (of the tip and heel, the slower of those on the ground, so a foot rolling over its toes or heel is not counted), m/s; per context (a skill move; a shot; a touch, the exit after a move included; anything else) the ticks counted, the largest, the 95th percentile, the ticks over 0.5 m/s and the worst tick and foot. A swinging foot that skims the ground counts too, so the maximum is a swing's speed; compare `p95_mps` and `over_0.5` against plain running (a jog or sprint replay: about 0.15-0.2 m/s and 4% of the grounded ticks). With `ATTACKER_ANIM_LOG=1`, `MOCAP_SKATE` prints each counted tick over 0.5 m/s and `MOCAP_SOLE` both feet's tip and heel heights and speeds, the tick's context, the shown pelvis height (`pel_y`) and whether the foot is held planted, on every skill-move tick and while a skill clip plays or hands back.

With the skinned attacker a `MOCAP_POSE` line follows `BODY_POSE`: the fastest turn of any bone's local rotation on one tick outside shots (`bone_max_deg_s`, the bone and tick, and `bone_over_1500` ticks above 1,500 deg/s, a pop), leaving out the foot and toes of a foot while it plays the ball (a touch's pose, a skill move, a kick), which a `PLAYING_FOOT_TURN max_deg_s= at= over_1500=` line gives apart, and in `--attacker-anim mocap` the lowest boot tip (`min_toe_y_m`) and the ticks measured and those drawn wholly from the cycles. With `--attacker-anim procedural` the attacker's measurements come from the figure's joints; with `mocap` the ankles, knees, boot tips, pelvis (between the hip joints) and lean (hips to neck) are read from the skinned body as shown, and a stance is a foot held where it landed (`FOOT_SLIDE`, `STRIDE`; `STRIDE` counts only steps drawn wholly by one body, none across a procedural/cycle hand-over); `BODY_POSE`'s elbow range is still the procedural arm's; its `lean_close_deg` is the mean steady lean in close control. `ATTACKER_ANIM_LOG=1` in the environment prints the mocap attacker's per-tick diagnostics (`MOCAP_FOOT` weight, phase, stance, hold and ankles per foot; `MOCAP_CYCLE` each cycle's weight, stance share and frame; `MOCAP_KNEE`; `MOCAP_FAST` bones over 1,500 deg/s; `MOCAP_JUMP` foot moves over 0.09 m or velocity changes over 6 m/s per tick, with the touch state, for either body; `MOCAP_SLIDE`; `MOCAP_TOE`; `MOCAP_STEP`;
`SKILL_LAYER` each change of the skill-clip layer's state, clip and clip time; `SKILL_SOLE` the sole-to-ball distance on each tick of a sole move's span).
In `--attacker-anim mocap` the body-language layers (see the character models above) are measured on the shown body: a `BODY_LANG_TURN` line per turn (the smoothed sideways acceleration over 1.5 m/s^2 for at least 0.15 s while running freely above 2 m/s: no skill move or shot, the cycles' full weight) gives its ticks, the largest bank toward the inside (`bank_in_max_deg`: the trunk line from between the hip joints to the neck, rolled about the facing) and the largest bank less the cycle's own roll at that phase and speed (`bank_in_net_deg`: the stride's sway taken out), the layer's own bank, and on that net peak's tick the speed, the shoulders' turn ahead of the hips toward the inside (`shoulder_lead_deg`, the line between the upper arms against the line between the hip joints; `shoulder_lead_net_deg` less the cycle's own counter-rotation at that phase) and the head's ahead of the shoulders (`head_lead_deg`, the head bone's forward), with the largest of each over the turn. The `BODY_LANG` line gives the turn with the largest net bank, the largest bank 0.5-1 s after a turn ended and while running straight (no turn for 1 s, sideways acceleration under 0.5 m/s^2), each raw and net; the steady trunk lean less the cycles' own steady lean at the same speed (`steady_lean_vs_cycles_deg`, the cycles sampled over a stride at the blend the speed asks for; steady: both accelerations under 0.5 m/s^2), with the ball within 1.2 m and without; the largest lean forward of that steady lean while speeding up (`lean_acc_peak_deg`, the speed and acceleration there) and back of it while braking (`lean_brake_peak_deg`, with the pelvis drop below the cycles' there, and `lean_brake_at_6_min_deg` the least on ticks braking at 6 m/s^2 or more); the posture with the ball against without (`posture_lean_deg`, `posture_pelvis_drop_m`, NaN without steady ticks of both, and `pelvis_below_cycles_ball_m`); the head pitched down with the ball within 1 m (`head_down_deg_mean`, `min`); each dribble touch's follow-through (`follow_m_min`, `mean`, `n`: the touching ankle's move along the ball's launch direction relative to the pelvis over the 0.08 s after the contact, for touches made with the cycles' full weight; `standing_n` counts the others); and the fastest bone turn outside the whole leg of a foot playing the ball (`off_leg_bone_max_deg_s`, `at`, `off_leg_over_1500`; shots left out, as `MOCAP_POSE`'s, which leaves out only that foot and its toes). `BODY_LANG=off` in the environment leaves the layers out (the measurements still run); `BODY_LANG_LOG=1` prints a `BODY_LANG_TICK` line per tick.

A skill move prints `SKILL_FOOT kind= contact|arrived t= err_m=` (the move's surface to the ball surface at the contact tick, and 0.04 s after it) and, in
clips mode, `SKILL_CLIP start` (the move, foot, the two clips and the running clip's weight, the entry speed, the clip time it starts at and the
core's contact and end times); on quit `SKILL_CONTACT n= mean_m= max_m=` (the contact ticks) with `sole_max_err_m` (a sole move's sole to the ball
from its landing until its hand-back would begin), `on_ball_max_err_m` (the same over the whole span to the end tick, leaving out the ticks on which
the same foot already winds up for the core's exit touch, which `on_ball_exit_touch_ticks` and `on_ball_exit_touch_max_err_m` give apart) and
`step_over_clear_min_m` (the nearest any surface of a step-over's circling boot came to the ball surface, ticks on which the exit touch has that foot at a touch weight of 0.9 or more left out; negative is inside it). With the skinned
attacker drawn from his bones (`--attacker-anim mocap`) every one of these surface points is the skinned boot's own, as shown.
