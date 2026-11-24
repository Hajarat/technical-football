# Arena, coordinates and scenarios

World coordinates are metres: X toward the attacked goal (goal line 52.5), Y up, Z across (the camera stands on the +Z side).
The pitch (`src/FootballSim.Core/Pitch.cs`) is a walled arena whose lengths come from the scenario's `pitch` block: `length`,
`width`, the goal's `goal_width` and `goal_height`, and the penalty area's `penalty_area_depth` (16.5 m from the goal line) and
`penalty_area_width` (40.32 m; in the scenarios' 20 m wide arena the area runs from wall to wall, so its sides are the boards).
`Pitch.InPenaltyArea` says whether a ball is in the area (any part of it on or over the line). The game draws the outline, the
5.5 m goal area, the penalty area's line and the penalty spot.

Scenario files are in `data/scenarios/`; player and tuning files are in `data/players/` and `data/tuning/`. Player, tuning and scenario files are re-read every time a round is set up, so editing a JSON file in `data/` shows up next round.
A file that fails to load keeps its last good version and the HUD warning line names the file and field.

The defender duel (`data/scenarios/defender_duel.json`) has `"defenders": 1`: one outfield defender, placed by the scenario's
`defender` spot and played by the defender bot, with the sheet named in `default_defender`. See [defending.md](defending.md).

Two v one (`data/scenarios/two_v_one.json`) adds a second attacker: `team_size` 2, a `mate` start spot (the team-mate, with
the sheet named in `default_mate`), one defender and `"offside": true`. The carrier starts at (4, −3) with the ball, the
team-mate at (5, 5), the defender at (16, 0). See [passing.md](passing.md) and [teammate.md](teammate.md).

## Loose-ball start spots

Loose-ball start spots (`data/scenarios/loose_*.json`, D-pad left / right in the game) begin with the ball already lost
between the attacker and the keeper: a scenario's `ball_loose` makes the ball count as lost from the first tick, and
`ball_speed` / `ball_heading_deg` (degrees, 0 toward the goal, positive toward +Z) start it rolling. Heavy touch (rolling on
ahead of him, uncontested), 50-50 (a still ball halfway to the keeper, 5 m off his line), the keeper's ball (nearer the
keeper), parry rebound (coming back off the keeper at 9 m/s across the attacker) and rolling across (crossing in front of
him at 45 degrees).

## Scripted rounds

Scripted rounds: the batch tool's scripted attacker runs at the goal and, in a scenario with `shoot_distance`, presses shoot
inside it once the ball is strikable or its next touch comes early enough in the wind-up; with `strike_distance` (breakaway
11.3, halfway run 12.4, angled run 11.1 m, the distances the scoring bands were first measured at) it presses only once the
ball, carried on at his speed to the contact, will be that close at the strike. Within 4 m of the keeper it stops sprinting
and within 6 m takes close control. Against a defender it plays one of the styles in [defending.md](defending.md#the-probe); with a team-mate one of solo, square and through ([teammate.md](teammate.md#scripted-styles-and-bands)).

## The first attack

The arena keeps the ball in play, so a round goes on after a missed shot until a goal, the keeper holding the ball, the
defender controlling it, a foul, an offside or the clock. The round report's `first_attack` block (`World.Measure.cs`) says
how the attacking side's first go ended, which every human-probe band reads: `goal`, `ended_tick` (from Begin) and
`ended_by`, one of `goal`, `miss` (a struck shot met the boards with no keeper, frame or defender touch since its strike),
`held`, `dispossessed`, `foul`, `offside` and `time` (the clock, or a round ended for a value that became not-a-number). A
parry, the frame or a block keeps the first attack on, so a rebound goal is a first-attack goal; after a clean miss a later
goal is the round's but not the first attack's. It changes nothing in the round. A report saved before it existed has none.

## How the arena is drawn

All of it is view only: `game/Pitch.cs` draws the shapes the simulation's `Pitch` already has and changes none of them.

- **Grass** (`game/grass.gdshader`): a CC0 colour / normal / roughness set tiled every 2 m in world space (a second sample at another scale and angle hides the repeat), with mowing stripes 5 m wide along X that alternate by 6% in brightness. The white markings are separate unshaded meshes just above it, so they stay crisp.
- **Boards and surround**: CC0 concrete (surround) and painted-metal (boards: only its normal and roughness, under a flat muted dark blue-grey with a white cap on top) sets, projected in world metres. The board's inner face is still the simulation's wall.
- **Cage** (`game/cage_net.gdshader`): on every wall from the board top (`arena.board_height`) up to `Pitch.Ceiling`, a diamond-mesh net with about 10 cm cells, a thin post every 4 m and a rail at ceiling height; over the goal mouth the net starts above the crossbar; there is no roof net. Strands are kept at least a pixel wide and melt into an even veil in the distance so they do not shimmer. Wherever a wall faces the camera (the near, +Z side) its net and posts fade to a few percent opacity so they never hide play. The goal net uses the same material.
- **Sky and light** (`Match.MakeEnvironment`): a 1K CC0 overcast HDRI as the sky, background and ambient light, ACES tonemapping, subtle SSAO and a little glow, plus the sun and its shadows.
- **Ball** (`game/ball_pattern.gdshader`, `BallView`): twelve dark pentagons on white, found by angle to the icosahedron's vertices; the mesh turns with `Ball.Spin`, integrated every rendered frame.
- **Shot trail** (`game/BallTrail.cs`, `game/ball_trail.gdshader`): a strike starts a camera-facing smoke ribbon through the ball's recent positions when the ball leaves faster than 12 m/s; its width, length and opacity grow with speed up to 30 m/s. A save, frame hit, board hit, goal or bounce, a ball under 8 m/s, or 1 s after the strike stops it, and the rest fades over 0.3 s. Touches never start one. The constants are at the top of `BallTrail.cs`.

Assets and their sources are listed in `game/assets/textures/SOURCES.md`.
