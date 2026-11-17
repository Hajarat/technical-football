# Agent notes

## Running the game from tools

- Windowed runs (anything that renders, such as `--shots`) go only through `game/tests/run-bg.sh`, so the window never comes to the front or takes keyboard focus. Usage: `game/tests/run-bg.sh -- --replay tests/smoke_replay.json --shots out/x --shot-on strike --quit-after-round`.
- Prefer `--headless` when no screenshots are needed; that needs no wrapper.
- Never launch `godot-mono --path game` directly from a tool without `--headless`.
