#!/bin/zsh
# Runs the game in a window WITHOUT it coming to the front or taking keyboard focus (macOS), for tools and agents.
# Needed for any run that renders: --shots / --shot-* / --shot-on. Headless runs (--headless, no screenshots) don't need it.
#
# Usage: game/tests/run-bg.sh -- --replay tests/smoke_replay.json --shots out/x --shot-on strike --quit-after-round
# Everything after the leading "--" is passed to the game after its own "--", with --background added.
# Relative --replay / --shots paths resolve against the game project folder (game/).
#
# How: `open -g -n -W` launches a separate instance without activating it and waits for it to exit; the game then
# hides itself (--background). Godot's stdout/stderr are captured to a log and printed afterwards, so REPORT / SHOT /
# FOOT_* lines are visible to the caller. Exit status: `open -W` does not return the app's exit code, so this script
# exits 0 when the run finished, 2 for a launch failure, and 1 if the log contains an "ERROR:" or "SCRIPT ERROR:" line.
# Godot grabs the front for a moment while it starts, before any game code can run, so a small watcher hands the
# front back to the app that had it, only while Godot itself is frontmost and only until the run ends.
#
# On Linux (the headless devbox) there is no window to keep in the background: the game renders into a virtual X
# display (xvfb-run, software Vulkan via Mesa llvmpipe) with the dummy audio driver, from the game folder, and the same
# log and exit-status rules apply. Override the binary with GODOT_BIN (default godot-mono on PATH).
set -u
app=${GODOT_APP:-/Applications/Godot_mono.app}
here=${0:A:h}
game=${here:h}
[[ ${1:-} == -- ]] && shift
log=$(mktemp -t run-bg.XXXXXX) || exit 2
if [[ $(uname) != Darwin ]]; then
  (cd $game && xvfb-run -a -s "-screen 0 1920x1080x24" ${GODOT_BIN:-godot-mono} --audio-driver Dummy --path $game -- "$@") >$log 2>&1
  rc=$?
  cat $log
  if (( rc != 0 )); then echo "run-bg: godot exited $rc" >&2; rm -f $log; exit 2; fi
  bad=0
  grep -qE '^(SCRIPT )?ERROR:' $log && bad=1
  rm -f $log
  exit $bad
fi
prev=$(lsappinfo front)
previd=$(lsappinfo info -only bundleid $prev 2>/dev/null | sed -n 's/.*="\(.*\)"/\1/p' | head -1)
(
  while true; do
    cur=$(lsappinfo front)
    if [[ $cur != $prev ]]; then
      name=$(lsappinfo info -only name $cur 2>/dev/null | head -1)
      [[ $name == *'"Godot"'* ]] && [[ -n $previd ]] && open -b $previd
    fi
    sleep 0.03
  done
) &
watcher=$!
open -g -n -W -a $app --stdout $log --stderr $log --args --path $game -- --background "$@"
rc=$?
kill $watcher 2>/dev/null
cat $log
if (( rc != 0 )); then echo "run-bg: open failed ($rc)" >&2; rm -f $log; exit 2; fi
bad=0
grep -qE '^(SCRIPT )?ERROR:' $log && bad=1
rm -f $log
exit $bad
