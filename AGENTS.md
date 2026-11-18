# Agent notes

## Running the game from tools

- Windowed runs (anything that renders, such as `--shots`) go only through `game/tests/run-bg.sh`, so the window never comes to the front or takes keyboard focus. Usage: `game/tests/run-bg.sh -- --replay tests/smoke_replay.json --shots out/x --shot-on strike --quit-after-round`.
- Prefer `--headless` when no screenshots are needed; that needs no wrapper.
- Never launch `godot-mono --path game` directly from a tool without `--headless`.

## The devbox (headless Linux)

- The repo is also cloned at `~/Projects/football-sim` on the devbox (same GitHub remote), with .NET SDK 11.0.100 in `/opt/dotnet`, Godot 4.7.2 .NET as `godot-mono`, zsh and Xvfb.
- `game/tests/run-bg.sh` works there too: it renders into a virtual X display (`xvfb-run`, software Vulkan via Mesa llvmpipe, about 25 fps), so screenshots are taken and reviewed on the devbox. Use the same command line as on the Mac.
- Play-testing needs a window and a gamepad, so it stays on the Mac.
- Godot's import writes `game/*.uid` files; delete them before committing.
