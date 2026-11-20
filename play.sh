#!/bin/zsh
# Builds the game's C# and then plays it, so a play-test always runs the checked-out code.
# `godot-mono --path game` alone runs whatever was last compiled into game/.godot/mono/temp/bin, which a git pull does not update.
#
# Usage: ./play.sh [game options]   e.g. ./play.sh --figure procedural
# Game options are passed to the game after its "--" (see the README's game options).
set -eu
root=${0:A:h}
dotnet build "$root/game/FootballSim.Game.csproj" -nologo -v quiet -clp:ErrorsOnly ||
  { echo "play.sh: build failed, not starting the game" >&2; exit 1; }
echo "play.sh: built $(git -C "$root" rev-parse --short HEAD)$(git -C "$root" diff --quiet HEAD -- src game 2>/dev/null || echo ' + uncommitted changes')"
if (( $# )); then
  exec ${GODOT_BIN:-godot-mono} --path "$root/game" -- "$@"
else
  exec ${GODOT_BIN:-godot-mono} --path "$root/game"
fi
