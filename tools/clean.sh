#!/usr/bin/env bash
# Removes the clutter that tool runs leave behind. Nothing it touches is tracked by git.
#
# Usage: tools/clean.sh [--days N] [--dry-run]
#   --days N   only remove things nothing has written to for N days (default 3), so a live session's files survive
#   --dry-run  print what would go, remove nothing
#
# What it removes:
#   - Claude Code scratch folders for this repo and its worktrees (/tmp/claude-<uid>/*football-sim*/<session>/),
#     where screenshots, strips and videos pile up
#   - entries in out/ and game/out/
# What it only lists: worktrees whose branch is already merged into main, to remove with `orca worktree rm`.
# sessions/ (play-test recordings) is data and is never touched.
set -euo pipefail
days=3 dry=0
while (( $# )); do
  case $1 in
    --days) days=$2; shift 2 ;;
    --dry-run) dry=1; shift ;;
    *) echo "usage: $0 [--days N] [--dry-run]" >&2; exit 2 ;;
  esac
done
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
mins=$(( days * 1440 ))
freed=0

# Removes $1 when no file under it changed within the last $days days.
sweep() {
  local d=$1
  [[ -e $d ]] || return 0
  if [[ -n $(find "$d" -mmin -"$mins" -print -quit 2>/dev/null) ]]; then return 0; fi
  local kb; kb=$(du -sk "$d" 2>/dev/null | cut -f1)
  freed=$(( freed + kb ))
  printf '%s %6s MB  %s\n' "$( (( dry )) && echo would-remove || echo removed)" $(( kb / 1024 )) "$d"
  (( dry )) || rm -rf -- "$d"
}

shopt -s nullglob
for proj in /tmp/claude-"$(id -u)"/*football-sim*/; do
  for d in "$proj"*/; do sweep "${d%/}"; done
  (( dry )) || rmdir "$proj" 2>/dev/null || true
done
for d in "$repo"/out/* "$repo"/game/out/*; do sweep "$d"; done

echo "total: $(( freed / 1024 )) MB $( (( dry )) && echo 'would be freed' || echo freed)"

merged=$(git -C "$repo" branch --format='%(refname:short)' --merged main | grep -vx main || true)
git -C "$repo" worktree list --porcelain | awk '/^worktree /{w=$2} /^branch /{sub("refs/heads/","",$2); print w, $2}' |
  while read -r wt br; do
    grep -qx -- "$br" <<<"$merged" || continue
    if [[ $(git -C "$repo" rev-parse "$br") == $(git -C "$repo" rev-parse main) ]]; then
      echo "worktree with no commits beyond main (just started, or finished; check before removing): $wt [$br]"
    else
      echo "merged worktree (remove with: orca worktree rm): $wt [$br]"
    fi
  done
