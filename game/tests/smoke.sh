#!/bin/zsh
# Plays game/tests/smoke_replay.json through the real match scene (no window) and through the batch tool,
# then checks that the two round reports are identical. Run from the repo root.
set -e
replay=${1:-game/tests/smoke_replay.json}
game=$(godot-mono --headless --path game -- --replay "../$replay" --quit-after-round | sed -n 's/^REPORT //p')
batch=$(dotnet run -c Release --project src/FootballSim.Batch -- --replay "$replay" | sed -n '/^{/,$p')
python3 - "$game" "$batch" <<'PY'
import json, sys
a, b = json.loads(sys.argv[1]), json.loads(sys.argv[2])
print("game :", sys.argv[1][:100], "...")
print("batch:", json.dumps(b, separators=(",", ":"))[:100], "...")
if a == b:
    print("IDENTICAL")
else:
    print("DIFFERENT"); sys.exit(1)
PY
