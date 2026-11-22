#!/bin/zsh
# Plays the seven angled-shot replays (game/tests/shot_angles, written by the batch tool's --write-shot-replays) through the
# real match scene without a window and checks what the shot layer drew: the clip of the kind and angle bin meant, on the foot
# meant, no stand-in clip, the striking foot never changed after the release, no hand-back before the contact, the timeline's
# jump onto the contact at most two clip frames, the boot on the ball, and for the power kicks the hips within 25 degrees of
# the aim at the strike. Run from the repo root (after dotnet build game/FootballSim.Game.csproj).
set -u
dir=game/tests/shot_angles
typeset -A want
want=(
  shot-power-0 "clip=shot_run_right"
  shot-power-across90 "clip=shot_power_run_across90_right"
  shot-power-across180 "clip=shot_power_run_across180_right"
  shot-finesse-across45 "clip=shot_finesse_run_across45_right"
  shot-chip-0 "clip=shot_chip_run_0_right"
  shot-outside-open45 "clip=shot_outside_run_open45_right"
  shot-weak-foot-open90 "clip=shot_power_run_across90_left"
)
bad=0
for n in ${(ko)want}; do
  log=$(godot-mono --headless --path game -- --replay ../$dir/$n.json --quit-after-round 2>&1)
  shot=$(print -r -- $log | grep '^SHOT_CLIP shot ' | tail -1)
  contact=$(print -r -- $log | grep '^SHOT_CLIP contact ')
  summary=$(print -r -- $log | grep '^SHOT_CLIP summary ')
  why=""
  [[ $shot == *${want[$n]}\ * ]] || why+=" wrong clip (${shot##*clip=})"
  [[ $(print -r -- $contact | grep -c .) == 1 ]] || why+=" no single contact"
  [[ $summary == *"shots=1 fallback=0 foot_changed=0 hand_backs=0 snaps_over_2=0"* ]] || why+=" summary: $summary"
  skin=${${contact##*skin_err_m=}%% *}
  (( ${skin#-} <= 0.01 )) || why+=" boot ${skin} m off the ball"
  if [[ $n == shot-power-* || $n == shot-weak-* ]]; then
    off=${${contact##*hips_off_aim_deg=}%% *}
    (( ${off#-} <= 25 )) || why+=" hips ${off} degrees off the aim"
  fi
  if [[ -n $why ]]; then print "FAIL $n:$why"; bad=1
  else print "ok   $n: ${want[$n]#clip=}, hips ${${contact##*hips_off_aim_deg=}%% *} deg off the aim, ${summary##*max_snap_frames=} frame snap"; fi
done
exit $bad
