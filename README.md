# Timberborn AI Director

Give a model a goal, then watch it play Timberborn live, with its reasoning
captioned on screen for the recording.

Two halves:

- **`mod/`** is a C# mod for Timberborn's own mod loader (Update 6+, no BepInEx).
  It exposes a local HTTP API on `127.0.0.1:8787` for reading the world and issuing
  commands, and draws a caption in the top-left of the game showing the AI's goal
  and reasoning.
- **`agent/`** is a Python loop outside the game. Each turn it reads the state, asks
  Claude what to do, applies the commands, and updates the caption.

The agent never touches the mouse or reads pixels. It reads structured state and
issues structured commands, which is what makes the run legible on video.

## How the mod reads and changes the game

The game's services are plain objects built by its dependency-injection container
(Bindito), not Unity components, so they cannot be looked up from outside. The mod
registers a `[Context("Game")]` configurator (`mod/GameServices.cs`) that asks the
container for the real services:

| Holder | Services | Off-switch flag |
|---|---|---|
| `AIGameServices` | cycle, weather, beaver population, entity registry, speed | `no-di.flag` |
| `AIWorldServices` | districts, goods, terrain, water, map size | `no-world.flag` |
| `AIBuildServices` | building list and unlocks, science, blueprints, validators, factories | `no-build.flag` |
| `AIForestryServices` | the tree-cutting area | `no-forestry.flag` |

**If a save will not load after a change**, a constructor argument is not injectable.
Create an empty file with one of those names next to `TimberbornAI.dll` in the Mods
folder, relaunch, and the save loads without that group. Then tell whoever is
maintaining this which group it was.

Placement runs the game's own checks: block rules, then the full preview rules
(a pump needs water, a building must be reachable). Buildings become ordinary
construction sites, so costs and build time behave as for a human click.

## HTTP API

Read endpoints (GET):

| Endpoint | Returns |
|---|---|
| `/health` | Frame counters and queued jobs. Answered without the game thread, so it works even if the game is stuck. |
| `/ping` | `{"ok":true}` through the game thread. |
| `/state` | Cycle and day, beavers, entity counts, districts, stock, placed buildings (with `access_cell`), science, speed, and hazard status. `?debug=1` adds the hidden weather schedule. |
| `/map?x&y&w&h` | Heights, water, and an objects grid (trees, bushes, buildings) for a window up to 48x48. |
| `/buildings?all=1` | Building template names, unlock state and costs. |
| `/dump` | Writes every game type name to `timberborn-ai-types.txt` (a discovery aid). |

Commands (POST `/command`, JSON with an `action`):

| Action | Purpose |
|---|---|
| `find_sites` | Spots where a building can really be placed, with the facing whose door gives the shortest walk. |
| `build` | Place a building. Supports `dry_run`, `orientation`, `z`. |
| `connect` | Route a path between two access cells around obstacles, reusing existing path. |
| `build_path` | Lay an L-shaped path (a blunt fallback). |
| `mark_trees` / `unmark_trees` | Mark or clear a rectangle for tree cutting. |
| `set_speed` | 0 pauses, 1 to 7 plays. |
| `note` | Update the on-screen caption. |

### What the agent knows about weather

The agent is given what a human player sees, no more. It does not get the drought
schedule. It sees `hazard_approaching` (with the hazard type) during the game's
3-day warning, `hazard_active` and `hazard_days_left` while it lasts, and never a
countdown. `/state?debug=1` still exposes the schedule for your own testing.

## Build and deploy

Needs the .NET SDK and a Timberborn install.

**Keep the git clone outside the game's Mods folder** and deploy only the two files
the game needs. The game scans mod subfolders, and a clone drags `.git`, `bin` and
`obj` into that scan (slow, especially in a OneDrive-synced Documents folder). Your
real Documents folder may be OneDrive-redirected, so use the path the game actually
reads. Confirmed on the game PC:

```
C:\Users\micah\OneDrive\Micah's Stuff\Documents\Timberborn\Mods\TimberbornAI\
```

```bash
cd /d "C:\Users\micah\Documents\business\mods\timberborn-mod"
git pull
dotnet build mod/TimberbornAI.csproj -c Release -p:GameManaged="C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn_Data\Managed" -p:ModInstallDir="C:\Users\micah\OneDrive\Micah's Stuff\Documents\Timberborn\Mods\TimberbornAI"
```

`DeployMod` copies `manifest.json` and `TimberbornAI.dll` into `ModInstallDir`.
**Quit the game before building**: it holds the DLL open, and mod code only loads at
startup. The project references every `Timberborn.*`, `Bindito.*` and `UnityEngine*`
assembly in the game's `Managed` folder, so a new game type never needs a new
reference.

The entry point is `IModStarter` (`Timberborn.ModManagerScene`), which the game
calls once at startup. The mod sets `Application.runInBackground` so the game keeps
running while the agent or a recorder has focus, and drains HTTP work from a Unity
PlayerLoop hook so it does not depend on any GameObject staying alive.

If the Mods menu does not show the mod, check
`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log` for what folder it
scanned. Log lines from the mod start with `[TimberbornAI]`.

## You pick the map, the faction and the goal

Everything else is the AI's job. Start a game in Timberborn with the map and faction you want and leave it on the
first screen (it starts paused). The mod reports `faction` in `/state`, read from the district center's name. The
AI is handed the building list and the faction's own need specs up front, and works out what its beavers need and
which buildings provide it. The built-in playbook is worked out for Folktails; for another faction the AI keeps the
same priorities and uses that faction's equivalents, confirming each with `inspect_building` and `problems`.
Pass the goal as a file (`--goal`) or inline (`--goal-text "..."`). Starting or loading the game itself is still
done by hand.

## Run the agent

Needs Python 3.10+ and an Anthropic API key. On Windows the launcher is `py`.

```bash
py -m pip install -r agent\requirements.txt
set ANTHROPIC_API_KEY=your-key
py agent\agent.py --goal agent\goal.md --max-turns 5
```

If your key is not scoped to a workspace, also `set ANTHROPIC_WORKSPACE_ID=...`.
The model defaults to `claude-sonnet-5-5`; change it with `--model` or
`TIMBERBORN_MODEL`. Edit [goal.md](agent/goal.md) to change what it tries to achieve.
That file plus the system prompt in `agent/agent.py` is the whole brief.

Useful flags: `--interval` (seconds between turns), `--max-turns` (0 runs until you
press Ctrl+C), `--quiet` (hide the model's free text).

## Recording

Capture the game window as usual. The caption panel is drawn top-left through IMGUI,
so it lands in the recording without compositing. Keep an eye on API cost on long
runs; `--interval` controls how often it acts.

## Tools

`tools/find-types.ps1` looks up game types and, with `-Members -Full`, prints their
properties and method signatures. This is how the real service names and entrance
structure were found, and it is the first thing to reach for when a game update
renames something:

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools\find-types.ps1 -Members -Full -Pattern "^(ClassName)$"
```

## Known limits

- An entrance has two cells: `Coordinates`, the free cell outside the door where a
  path ends (reported as `access_cell`), and `DoorstepCoordinates`, which lies inside
  the building's own footprint. Paths must end on `access_cell`.
- `connect` assumes ground may rise by at most one level between neighbouring path
  tiles. If the game disagrees on some terrain, routes will come back blocked.
  A road over a natural Slope is accepted by the game (confirmed in play), so `connect`
  can join two levels where one exists.
- The AI cannot yet place plantations or fields, set worker priorities, or assign
  beavers; it works through buildings, paths, tree cutting and game speed.
- Everything here is built against one game version. Types and members shift between
  updates, so rerun the lookups in `tools/` after a game patch if something stops
  working.
