# Controls

The game shows the controls for the device in use: keyboard and mouse or gamepad, switching live to whichever produced the last
meaningful input (a key, a mouse button or more than a few pixels of mouse movement; a pad button or a stick or trigger past 0.3).
A small hint in the top-right corner names the toggle: `H: controls` on the keyboard, `Menu: controls` on the pad (`Options` on PlayStation, `+` on Nintendo). The
full list opens on `H` and, on either device, while the game is paused (`P` / `Esc` / Menu), and stays out of the play area otherwise. It lists
movement, shooting, skill moves (the right-stick gesture in words for the pad, the keys for the keyboard) and round / menu actions. Pad buttons use the pad's own names (Xbox, PlayStation or Nintendo, picked
from the name the OS reports; Xbox names when unknown), and the face-button swap keeps the shoot button the right-hand one, so the
labels match what is pressed. Every label is read from `game/Bindings.cs`, where `InputReader` also takes its keys and buttons, so
changing a binding there changes the panel. `-- --controls-panel keyboard|xbox|playstation|nintendo` opens the panel for that
device without a pad, for screenshots.

## Gamepad

The table uses Xbox names.

| Control | Action |
| --- | --- |
| Left stick | Run (stick right = toward the goal) and aim |
| Right trigger | Sprint |
| Left trigger (past half) | Shield (close control) |
| B / Circle (hold, release) | Power shot: power builds while held (full in 0.3 s) |
| Right bumper + B | Curved (finesse) shot |
| Left bumper + B | Chip |
| B (hold for power, release), then B again before the strike | Low (driven) shot; right bumper + B, B is a low curved shot; left bumper + B, B is played as a chip |
| B, then A / Cross before the strike | Fake shot: left stick at rest a fake stop, pushed a fake cut that way; the shot winds up as a real one and the fake's contact comes when the strike would have (at least 0.10 s after A; A in the last 0.05 s before the strike is too late) |
| Right stick: forward, then straight on to a side | Step-over to that side |
| Right stick: a side flicked from rest and released | Body feint to that side |
| Right stick: a side held 0.1 s | Ball roll to that side |
| Right stick: forward flick while sprinting | Knock-on: a heavy touch ahead to sprint onto |
| Right bumper + left stick away from a loose ball | Let it run: no eager regain (see [dribbling.md](dribbling.md)) |
| Left stick back with left and right bumpers held | Drag-back |
| Right stick: back, then straight on to a side | Elastico toward that side |
| Right stick: forward flick, rest, back | Heel-to-heel: the ball flicked on straight ahead |
| Right stick: forward flick, rest, a side | Spin (a roulette) toward that side |
| Right stick: back flick, rest, a side | McGeady spin: the ball dragged behind, a turn away, out square to that side |
| Right stick back alone | Nothing (the start of an elastico or a McGeady spin) |
| A, X, Y, right bumper + A | Nothing during play: there are no passes |
| A / Cross | Start the round; at the report, next round (new seed) |
| Y / Triangle | At the report, retry with the same seed |
| D-pad left / right | Previous / next start spot |
| D-pad up / down | Next attacker / next keeper |
| Right stick click | Toggle the debug overlay (shot wedge, aimed and landed markers, keeper reach) |
| View / Share / Back | Capture the round: opens the capture panel, in any state (see [Round capture](#round-capture)) |
| Menu / Options / Start | Pause |
| Y / Triangle while paused | Open the player editor (see [Player editor](#player-editor)) |
| Keyboard `B` | Swap face buttons (A with B, X with Y) for the active pad. On by default for Nintendo / Switch / Pro Controller pads; the choice is saved per pad name |

Skill-move directions are taken against the running direction (the facing when standing), and every side move works to
either side (the attacker draws them from authored clips, see [animation.md](animation.md)). The right stick counts from half deflection; the timings are in the `skill` section of
`data/tuning/default.json`:

- Step-over: the side within 0.25 s of forward, within 30 degrees of square, without passing the stick through rest
  (forward, rest, side is a spin).
- Body feint: from rest, a side held at least a tick and back at rest within 0.1 s. Ball roll: a side held 0.1 s.
- Knock-on: forward past 0.85 deflection while sprinting; it fires 0.15 s after the flick unless the stick goes to a side or
  back first, and a flick released to rest waits until a spin or heel-to-heel can no longer follow (0.125 s after the release).
- Heel-to-heel and spin: from rest a forward flick of at most 0.125 s, rest, then back (heel-to-heel) or a side (spin), within
  0.125 s of the release (`combo_step_window`). Elastico: back, then a side as for the step-over. McGeady spin: from rest a back
  flick of at most 0.125 s, rest, then the side he comes out to, within 0.125 s of the release (back straight on to the side,
  without rest, is the elastico; the back flick that ended a heel-to-heel does not start one).
- Drag-back: the left stick `drag_back_stick_deg` (120) or more from forward with both bumpers held, on the tick the three come
  together; it replaces the touch controller's own turn at the feet if that began within `drag_back_over_feet_turn` (0.1 s).
- Low shot: hold B for the power wanted, release, and press B again any time after the release and before the strike (the
  strike comes no sooner than 0.45 s after the first press and 0.12 s after the release; see [shooting.md](shooting.md)). The charge is the first press's,
  so a low shot can have full power; bumpers held on either press count. There is no minimum gap between the release and
  the second press.

## Keyboard fallback

Works alongside a gamepad (the larger stick value wins) and when none is detected. Ignored in replay mode.

| Key | Action |
| --- | --- |
| WASD | Run and aim (W = toward the far touchline, D = toward the goal) |
| Ctrl | Sprint (either Ctrl) |
| Q | Shield (close control) |
| Left mouse (hold, release) | Shoot; pressed again after the release, before the strike, a low shot |
| E | Curved (finesse) shot, held while shooting |
| Left Shift | Chip, held while shooting |
| Right mouse | Cancel: pressed after shoot and before the strike, a fake shot |
| Back against the run + E + Left Shift | Drag-back, as the pad's left stick back with both bumpers |
| Space, G | Shoot and cancel again, for keyboard-only play |
| X | Knock-on |
| 1 / 2 / 3 / 4 | Step-over / body feint / ball roll / drag-back, side from A/D, right by default |
| Enter | Start the round; at the report, next round |
| Backspace | At the report, retry with the same seed |
| Left / Right arrow | Previous / next start spot (the pad's D-pad) |
| Up / Down arrow | Next attacker / next keeper (the pad's D-pad) |
| O | Toggle the debug overlay |
| M | Capture the round (see [Round capture](#round-capture)) |
| P or Esc | Pause |
| B | Swap the pad's face buttons (see above) |
| H | Show or hide the controls panel |
| T | Open the player editor while play is stopped: paused, in Setup or at the report |

There is no driven-shot key: as on the pad, the low shot is shoot pressed twice.

## Round capture

When something in play feels wrong, View / Share / Back on the pad (`-` on Nintendo) or M on the keyboard opens the capture
panel at any moment: in Setup, during a round, at its outcome or at the report, paused or not (never while the player editor is
open, and the editor does not open while the panel is). The game stays frozen while the panel is open and carries on as it was
when it closes.

- During a round, or at its outcome before the report, the capture holds the round so far: every tick up to the press, marked
  partial; the round then carries on. In Setup or at the report it holds the last finished round, whole. With no round played
  yet the toast reads "Nothing to capture".
- Pick one of eight tags with the D-pad up / down (Up / Down arrow): wrong foot, bad aim, shot power / accuracy, touch / dribble,
  keeper, skill move, animation, other. The first is selected on opening.
- Type a note on the keyboard if you want one (up to 200 characters; Backspace deletes). The pad never types.
- A / Cross (Enter) saves with the tag and the note; B / Circle (Esc) saves with the tag and no note. There is no cancel: a press
  is always a capture. The button that saved does nothing in play until it is let go.
- The toast shows the id and tag for 3 seconds, such as "Captured C14 · wrong foot". The id counts across every session, so "C14"
  names one capture. The files are described in [tools.md](tools.md#session-logs).

Quitting while the panel is open saves the capture with the note typed so far. A round that began and got no report (the game
quit or its window closed, or the round set up again over it, as when closing the player editor with a change) is saved as
unfinished too.

## Player editor

A menu over the stopped game for setting every field of the attacker's sheet: the 15 outfield ratings (1 to 99; the rows are the
code's `PlayerSheet.RatingNames` minus the keeper ratings, so a new rating gets a row by itself), preferred foot, each trait on or
off, height (1.60 to 2.05 m, steps of 0.01) and weight (55 to 100 kg). Values are clamped as a player file's are. It opens with
Y / Triangle while paused, or T on the keyboard while paused, in Setup or at the report; play input is ignored while it is open.
It does not open while the capture panel is open.

The edited player is **Custom**. Opening the editor with Custom selected edits it; with a shipped attacker selected, Custom is
first replaced by a copy of that attacker (when Custom holds edits that match no preset and no shipped attacker, that takes a
second press: "Custom will be replaced: press Y again"). The Base row steps through the shipped attackers and loads one into
Custom (a second press when edits would be lost). The line under the rows lists what differs from the Base attacker.

| Pad (Xbox names) | Keyboard | Action |
| --- | --- | --- |
| D-pad up / down | Up / Down arrow | Pick a row (stats column) or a slot (presets column); held, it repeats |
| D-pad left / right | Left / Right arrow | Change the value by 1; held, it repeats after 0.35 s and speeds up to 20 steps a second |
| LB / RB | Shift + Left / Right arrow | Change the value by 10 |
| Y | Tab | Switch between the stats and the presets column |
| X | S | Save Custom into the marked slot, from either column (a filled slot: press again within 2 s to overwrite) |
| A | Enter | Load the marked slot into Custom, from either column (when Custom holds edits that match no preset and no shipped attacker and the slot differs: press again within 2 s) |
| View | Delete | Clear the marked slot, from either column (press again within 2 s) |
| B or Menu | Esc or T | Close |

Save, load and clear act on the marked slot whichever column has focus, so a player can be saved straight from the stats column
into slot 1 (the slot the editor opens on). The marked slot shows `▸` while the presets column has focus and a hollow `▹` while
the stats column has it. Every press says what happened on the line under the rows (saved, overwritten, loaded, cleared, empty,
or press again to confirm: "Load slot 3: unsaved edits will be lost: press load again"), and writes a `player_editor` line to the session log.

Closing applies: if the sheet changed, Custom becomes the attacker and the round is set up again with the same start spot and
seed (a live round is abandoned, and the pause is lifted). Closing without a change sets nothing up again. Custom then stays the
attacker across start spots; D-pad up (Up arrow) cycles the attackers with Custom last.

There are ten preset slots, named `from <base> · HH:MM` when saved. The last Custom player, whether it was the attacker at quit
and the presets are kept in the game's user folder (`user://player_editor.json`; on macOS
`~/Library/Application Support/Godot/app_userdata/Football Sim/`), never in the repo, and come back at the next launch. The
data/players files are never written. A round played with Custom saves its sheet in its replay, so it replays exactly.

## Controller not detected on macOS

If the HUD says "No gamepad detected", macOS is not listing the controller as a game controller. A pad in XInput / "Xbox 360 for Windows" mode is not
recognised by macOS. Switch the controller to its Bluetooth, Switch, PlayStation or Apple mode. The `pads` lines in `session.jsonl` record what Godot saw.
