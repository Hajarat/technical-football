# Agent notes

## Running the game from tools

- Windowed runs (anything that renders, such as `--shots`) go only through `game/tests/run-bg.sh`, so the window never comes to the front or takes keyboard focus. Usage: `game/tests/run-bg.sh -- --replay tests/smoke_replay.json --shots out/x --shot-on strike --quit-after-round`.
- Prefer `--headless` when no screenshots are needed; that needs no wrapper.
- Never launch `godot-mono --path game` directly from a tool without `--headless`.

## What to run when

Run the cheapest check that answers the question, and climb only when it calls for more:

| Step | When | What |
|---|---|---|
| 0 | Every edit | Build the projects you changed |
| 1 | While iterating | Tests for that area (`dotnet test src/FootballSim.Core.Tests --filter <name>`; the default run already skips the tests marked `Category=Slow`, and a `--filter` adds to that exclusion, so name a slow test with `--settings src/FootballSim.Core.Tests/all.runsettings --filter <name>`) and the one probe the theory is about, at a small `--n`; try tuning ideas with `--set` before editing code |
| 2 | Before reporting | That probe at its full `--n`, and the replays of the behaviour you touched, `--headless` |
| 3 | Only for a visible change | Screenshots through `run-bg.sh`: one strip per move, not sheets plus videos |
| 4 | Only when the user asks | The full suites: `dotnet test src/FootballSim.Core.Tests --settings src/FootballSim.Core.Tests/all.runsettings` (slow tests included, about 8 minutes; plain `dotnet test FootballSim.slnx` is the fast set), `game/tests/smoke.sh`, the skill-replay table, the shot check, the skill probe. At merge, push and suggest them; do not start them unasked |
| 5 | The Mac | Play-testing |

- An agent runs only the checks for what it touched; regressions elsewhere are caught at step 4.
- Godot runs whatever game assembly was last built: run `dotnet build game/FootballSim.Game.csproj` before replaying in the game, and reject any replay log whose `BUILD` line says `stale=true` or names another commit.
- Do not use `dotnet run --no-build` on a build you did not just make.
- Keep the box's limit of 4 threads for anything parallel.

## Test cards: agree a run before it counts

Seeds alone are noise, not a test. Before any run whose numbers will be reported as evidence that a change works, the agent writes a test card in chat and agrees it with the user, revising it until the user says go. Quick runs while building need no card. Every card has:

1. **The complaint**, in the user's words.
2. **The situation that produces it**: the scenario, probe or human plan that actually puts the player there. If none does, building it comes first; never run the closest one in its place.
3. **What varies besides seeds**: the change on and off, a rating sweep, attacker styles, human plans.
4. **The number that measures the complaint itself**, plus guard numbers (the bands that must not move).
5. **A prediction**: what should move, and roughly how much.
6. **Whether a script can hide the change**: a scripted actor that adapts to the change (for example one that times its press from the wind-up) shows nothing; say how the run avoids that.

The report then answers the card: the measured numbers against the prediction, and what to test next.

## Clutter

`tools/clean.sh` removes Claude Code scratch folders for this repo and its worktrees, and entries in `out/` and `game/out/`, that nothing has written to for 3 days (`--days N`, `--dry-run`), and lists worktrees already merged into main. It never touches `sessions/`.

## The devbox (headless Linux)

- The repo is also cloned at `~/Projects/football-sim` on the devbox (same GitHub remote), with .NET SDK 11.0.100 in `/opt/dotnet`, Godot 4.7.2 .NET as `godot-mono`, zsh and Xvfb.
- `game/tests/run-bg.sh` works there too: it renders into a virtual X display (`xvfb-run`, software Vulkan via Mesa llvmpipe, about 25 fps), so screenshots are taken and reviewed on the devbox. Use the same command line as on the Mac.
- Play-testing needs a window and a gamepad, so it stays on the Mac.
- Godot's import writes `game/*.uid` files; delete them before committing.
