# Technical Football (working title)

**A football game for players who have outgrown the usual ones: deep technique, organic play that never repeats, brutal
when you're sloppy and euphoric when an attack flows.**

## Why it exists

Twenty years of playing football games leaves you knowing exactly what they get wrong: the canned animations, the
scripted outcomes, the match that plays like the last one. This is an attempt to build what the ultimate football game
should feel like.

## What it stands for

- **Played against people.** It is made for humans playing each other, side by side on one couch first.
- **Organic, never scripted.** The ball, the touches, the body and the keeper are simulated tick by tick, and nothing
  decides an outcome ahead of time, so no two attacks play alike.
- **Hard, then rewarding.** Sloppy touches and rushed shots are punished. Clean technique strung together is what
  scores.
- **Measured, not guessed.** Every part of the feel has a probe and a number behind it, so a tuning change is something
  you can check rather than argue about.

## Where it's going

It starts with the duel, one attacker against the keeper, and that duel is built until it is right. From there it grows
into 5v5, a small-sided mode built around skill with room for a few geeky quirks, and then the traditional 11-a-side.
It is a personal project, built for the love of the game.

## Getting started

Today the game is the 1v1 duel, one attacker against the keeper: a simulation core in C#, game data in JSON and a Godot 4.7 (.NET) shell. Run everything from the repo root.

- **Play** (needs a gamepad): `./play.sh`. It builds the C# first, since `godot-mono --path game` alone runs the last compiled build, which a pull does not update. Game options go after `--`, e.g. `./play.sh --figure procedural`. The controls are in [docs/controls.md](docs/controls.md); the game also shows them on `H` or when paused.
- **First checkout:** run `godot-mono --headless --path game --import` once.
- **Build the game project:** `dotnet build game/FootballSim.Game.csproj`
- **Tests:** `dotnet test FootballSim.slnx`
- **Batch run** (10 000 scripted rounds, no game): `dotnet run -c Release --project src/FootballSim.Batch -- --scenario penalty --n 10000 --seed 1`
- **Headless run** (a replay through the game scene, no window): `godot-mono --headless --path game -- --replay tests/smoke_replay.json --quit-after-round`; `game/tests/smoke.sh` checks that its report matches the batch tool's.
- **Screenshots:** `game/tests/run-bg.sh -- --replay tests/smoke_replay.json --shots out/replay --shot-on setup,strike,outcome --quit-after-round`. They go to `game/out/` (ignored by git). Tools and agents must use `run-bg.sh` for any windowed run and never launch `godot-mono` without `--headless` directly; see [AGENTS.md](AGENTS.md). The other commands, game options, probes and session logs are in [docs/tools.md](docs/tools.md).

## How the project is laid out

- `src/` is the simulation core in C#: `FootballSim.Core` (the pitch, ball, touch controller, shots, keeper, defender and rounds), `FootballSim.Anim` (the animation runtime shared by the game), `FootballSim.Batch` (the batch runner and probes) and the test projects.
- `data/` is JSON: `tuning/` (every number of the feel), `players/` (rated player sheets) and `scenarios/` (start spots).
- `game/` is the Godot 4.7 .NET shell: the match scene, the figures and their animation, the HUD, input and the assets in `game/assets/`.
- `tools/` is the animation pipeline (`tools/anim/`, Python, run with `uv`) and the character build (`tools/characters/`).

## Documentation

- [docs/arena.md](docs/arena.md): coordinates, the walled arena and penalty area, scenarios, loose-ball start spots, scripted rounds.
- [docs/dribbling.md](docs/dribbling.md): speeds, turning, the touch controller, the boards, lock-on (going for a loose ball), and the skill moves with their timings.
- [docs/shooting.md](docs/shooting.md): the five shot types, the striking foot, accuracy, aim, charge and the strike.
- [docs/keeper.md](docs/keeper.md): how the keeper positions himself, closes down, saves and recovers, and the keeper probes.
- [docs/defending.md](docs/defending.md): the defender: his bot, the auto tackle, lunge, slide, barge and block, how a round ends with him in it, and the defender probe.
- [docs/controls.md](docs/controls.md): the controls panel, the gamepad and keyboard controls, skill-move gestures and the macOS controller note.
- [docs/animation.md](docs/animation.md): the rigged character models, how the attacker's running, skill moves and shots and the keeper are drawn, and the measurements the game prints.
- [docs/tools.md](docs/tools.md): the commands table, game options, probes, replays and session logs.
- [AGENTS.md](AGENTS.md): how tools and agents run the game.
- Asset sources and licences: [game/assets/characters/SOURCES.md](game/assets/characters/SOURCES.md) and [game/assets/anim/SOURCES.md](game/assets/anim/SOURCES.md).
