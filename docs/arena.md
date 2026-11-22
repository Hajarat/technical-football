# Arena, coordinates and scenarios

World coordinates are metres: X toward the attacked goal (goal line 52.5), Y up, Z across (the camera stands on the +Z side).
The pitch (`src/FootballSim.Core/Pitch.cs`) is a walled arena whose lengths come from the scenario's `pitch` block: `length`,
`width`, the goal's `goal_width` and `goal_height`, and the penalty area's `penalty_area_depth` (16.5 m from the goal line) and
`penalty_area_width` (40.32 m; in the scenarios' 20 m wide arena the area runs from wall to wall, so its sides are the boards).
`Pitch.InPenaltyArea` says whether a ball is in the area (any part of it on or over the line). The game draws the outline, the
5.5 m goal area, the penalty area's line and the penalty spot.

Scenario files are in `data/scenarios/`; player and tuning files are in `data/players/` and `data/tuning/`. Player, tuning and scenario files are re-read every time a round is set up, so editing a JSON file in `data/` shows up next round.
A file that fails to load keeps its last good version and the HUD warning line names the file and field.

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
and within 6 m takes close control.
