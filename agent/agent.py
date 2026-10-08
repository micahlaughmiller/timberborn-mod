"""
Timberborn AI Director: external agent loop.

Each turn it reads the live world snapshot from the in-game mod, asks Claude what
to do next, applies the commands, and pushes the reasoning on screen so a viewer
can follow the decision, not just the outcome.

    python agent/agent.py --goal agent/goal.md

Requires ANTHROPIC_API_KEY in the environment and the game running with the mod
loaded and a save open.
"""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

import anthropic

MOD_URL = "http://127.0.0.1:8787"
DEFAULT_MODEL = os.environ.get("TIMBERBORN_MODEL", "claude-sonnet-5-5")

MAX_TOOL_ROUNDS = 8   # tool calls the model may chain inside one turn
KEEP_TURNS = 6        # whole past turns kept in context

TOOLS = [
    {
        "name": "get_map",
        "description": (
            "Read a window of the map. Returns height rows (one base-36 character per "
            "cell: the surface level, '-' means no terrain) and water rows ('~' = water, "
            "'.' = none). Rows go y ascending, columns x ascending, starting at "
            "origin. Max 48x48. With no arguments it centres on the district center."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "x": {"type": "integer", "description": "left edge"},
                "y": {"type": "integer", "description": "top edge"},
                "w": {"type": "integer"},
                "h": {"type": "integer"},
            },
        },
    },
    {
        "name": "get_buildings",
        "description": "List building names you can build with their material and science costs.",
        "input_schema": {
            "type": "object",
            "properties": {"include_locked": {"type": "boolean"}},
        },
    },
    {
        "name": "build",
        "description": (
            "Place a building at a map coordinate. It becomes a construction site and "
            "beavers build it once the materials have been delivered. Use dry_run=true "
            "to check a spot without placing anything. Trees, other buildings and uneven "
            "ground block placement; the reply says why a spot was refused."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "prefab": {"type": "string", "description": "Exact name from get_buildings, e.g. 'SmallWarehouse.Folktails'"},
                "x": {"type": "integer"},
                "y": {"type": "integer"},
                "z": {"type": "integer", "description": "Use the z that find_sites returned for this spot. Some buildings (a water pump on the river bank) sit one level below the surface, and the default surface height is wrong for them."},
                "orientation": {"type": "string", "enum": ["Cw0", "Cw90", "Cw180", "Cw270"]},
                "dry_run": {"type": "boolean"},
            },
            "required": ["prefab", "x", "y"],
        },
    },
    {
        "name": "find_sites",
        "description": (
            "Find spots where a building can actually be placed. Scans a window, tries all four "
            "orientations and applies the game's full placement rules (a water pump must have its "
            "intake on water, flags must be reachable). Returns a list of {x, y, z, orientation} sorted "
            "by distance to near_x/near_y (default: the district center). Each spot already uses the facing whose "
            "door gives the shortest real walk to the district center (walk_steps_to_settlement), going around trees, "
            "water and cliffs, and includes the building's access cell (doorstep). ALWAYS use this instead of "
            "guessing coordinates, then build at one of the returned spots with the same orientation."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "prefab": {"type": "string"},
                "x": {"type": "integer", "description": "window left edge (default: around the district center)"},
                "y": {"type": "integer", "description": "window top edge"},
                "w": {"type": "integer", "description": "window width, max 40, default 24"},
                "h": {"type": "integer", "description": "window height, max 40, default 24"},
                "near_x": {"type": "integer", "description": "prefer spots close to this point, e.g. the trees or the water"},
                "near_y": {"type": "integer"},
                "max": {"type": "integer", "description": "how many spots to return, default 10"},
            },
            "required": ["prefab"],
        },
    },
    {
        "name": "set_gatherer",
        "description": (
            "Choose what a gatherer flag collects. A flag does nothing until a good is selected: its "
            "`gathering` field in placed_buildings is 'Nothing' and the game shows 'No good selected'. "
            "Call it right after placing a gatherer flag with good='Berries'. The options are Nothing, Berries, "
            "Dandelions and Chestnuts (a partial, case-insensitive name works). With x and y omitted it applies to every gatherer flag. "
            "Leave out `good` to list the options."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "x": {"type": "integer", "description": "the flag's x from placed_buildings (optional)"},
                "y": {"type": "integer"},
                "good": {"type": "string", "description": "what to gather, e.g. 'berry'"},
            },
        },
    },
    {
        "name": "demolish",
        "description": (
            "Remove one of your own buildings or path tiles to undo a mistake (a flag placed on the wrong "
            "level, a site you no longer want). Give the x and y exactly as listed in placed_buildings, "
            "or the cell of a path tile. It cannot remove trees, beavers or terrain, and it refuses the "
            "district center. Use it sparingly: materials already delivered to a site may be lost."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "x": {"type": "integer"},
                "y": {"type": "integer"},
                "prefab": {"type": "string", "description": "optional: the building name, as a safety check"},
            },
            "required": ["x", "y"],
        },
    },
    {
        "name": "connect",
        "description": (
            "Lay a path between two cells, routing around trees, buildings, water and steep ground and "
            "reusing path tiles that already exist. Give it the ACCESS cells of the two buildings you "
            "want to join: each building in placed_buildings has access_cell {x, y}, the "
            "one free cell outside its door that a path must end on. Joining a new building's access cell to the "
            "district center's access cell is the normal way to make it reachable. If it says no route "
            "exists, something is blocking: clear trees with mark_trees, or place the building elsewhere."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "x1": {"type": "integer", "description": "access_cell x of the first building"},
                "y1": {"type": "integer"},
                "x2": {"type": "integer", "description": "access_cell x of the second building"},
                "y2": {"type": "integer"},
            },
            "required": ["x1", "y1", "x2", "y2"],
        },
    },
    {
        "name": "build_path",
        "description": (
            "Lay Path tiles along an L-shaped route from (x1,y1) to (x2,y2): horizontally along y1 "
            "to x2, then vertically along x2 to y2. Beavers can only walk and haul along paths, so "
            "every building needs one connecting it to the district center. Tiles blocked by trees, "
            "buildings or water are listed in blocked_or_existing (an already-built path tile also "
            "shows there); route around real obstacles with a second call. Max 150 tiles."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "x1": {"type": "integer"},
                "y1": {"type": "integer"},
                "x2": {"type": "integer"},
                "y2": {"type": "integer"},
                "dry_run": {"type": "boolean"},
            },
            "required": ["x1", "y1", "x2", "y2"],
        },
    },
    {
        "name": "mark_trees",
        "description": (
            "Mark a rectangle (corners x1,y1 and x2,y2, each side at most 40 cells) for tree cutting. "
            "Lumberjack flags only cut trees inside marked areas. Find trees with get_map's objects "
            "grid. Pass from_x/from_y = the lumberjack flag's access_cell and only cells a beaver can actually walk to "
            "from the flag are marked (trees up a cliff or across water cannot be cut). The reply reports how many "
            "marked cells have trees and how many were skipped as unreachable."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "x1": {"type": "integer"},
                "y1": {"type": "integer"},
                "x2": {"type": "integer"},
                "y2": {"type": "integer"},
                "from_x": {"type": "integer", "description": "access_cell x of the lumberjack flag these trees are for"},
                "from_y": {"type": "integer", "description": "access_cell y of the lumberjack flag"},
                "max_steps": {"type": "integer", "description": "how far a beaver may walk from the flag, default 30"},
            },
            "required": ["x1", "y1", "x2", "y2"],
        },
    },
    {
        "name": "unmark_trees",
        "description": "Remove a rectangle from the tree-cutting area, for example to protect trees you want to keep.",
        "input_schema": {
            "type": "object",
            "properties": {
                "x1": {"type": "integer"},
                "y1": {"type": "integer"},
                "x2": {"type": "integer"},
                "y2": {"type": "integer"},
            },
            "required": ["x1", "y1", "x2", "y2"],
        },
    },
    {
        "name": "set_speed",
        "description": "Set game speed. 0 pauses, 1 is normal, 3 is fast, up to 7.",
        "input_schema": {
            "type": "object",
            "properties": {"speed": {"type": "integer"}},
            "required": ["speed"],
        },
    },
    {
        "name": "note",
        "description": (
            "Update the on-screen caption viewers see. Call this every turn. Keep it "
            "short and plain-spoken: the real reason for the move, not filler."
        ),
        "input_schema": {
            "type": "object",
            "properties": {
                "goal": {"type": "string", "description": "Current objective, a few words"},
                "thought": {"type": "string", "description": "Why, in one sentence"},
                "action_summary": {"type": "string", "description": "What you are doing now"},
            },
            "required": ["goal", "thought"],
        },
    },
]

SYSTEM = """You are playing a full game of Timberborn as the Folktails, solo, in front of a live audience.

Each turn you get a world snapshot (/state). Act through tools. Rules:

- Call `note` every turn. The audience sees only that caption, so say the real reason for your move.
- Never guess coordinates for a building. Call `find_sites` with the building name and a target point
  (`near_x`, `near_y`: where the trees, water or berries are, or the district center) and build at one
  of the returned spots, passing the same `x`, `y`, `z` and `orientation` it gives. Leaving out `z` lets the game guess
  the surface level, which is wrong for buildings that sit lower, such as a water pump on the river bank. Those spots already satisfy the game's rules,
  including water for pumps and being reachable for flags. Use `get_buildings` for costs and `get_map`
  to understand the terrain. Heights in the map are surface levels.
- `placed_buildings` in the snapshot lists what you have placed, with its facing, whether it is
  finished, and its `access_cell`: the one free cell outside its door that a path must end on for beavers to get in. A path that
  stops beside a building does nothing.
- get_map also returns an `objects` grid: a letter per cell naming what stands there (trees, berry
  bushes, ruins, buildings), with `object_legend` saying which letter is which. Use it. A lumberjack
  flag only helps if there are trees close to it, and a gatherer flag only if there are berry bushes
  close to it. Check the grid; do not guess where the trees are.
- Paths only connect tiles on the SAME level. A cliff of even one level is a barrier: a building on higher
  or lower ground than the path network cannot be reached, and trees up a cliff cannot be cut. Compare
  the height numbers in the map and keep flags, trees and buildings on the same level as the district.
- A gatherer flag is idle until you choose what it collects. After placing one, call `set_gatherer` with
  the food you want (berries). Check `gathering` in placed_buildings: `Nothing` means the flag is doing nothing.
- Beavers walk and haul along paths. A building with no path to the district center will never be
  built or worked, and the game shows "Unconnected building" for it. After placing anything, call
  `connect` from that building's `access_cell` (in placed_buildings) to the district center's
  `access_cell`. Do not hand-draw routes with build_path unless connect fails.
  Read the reply: it says how many tiles were new, and whether any could not be placed.
- A lumberjack flag only sends beavers to trees inside an area marked for cutting. Placing the flag
  is not enough: call mark_trees on a rectangle of trees close to the flag, passing the flag's access_cell as from_x/from_y (the reply says how many
  of the marked cells have trees; if that is 0 you picked bare ground). Keep the marked area near
  the flag and connected by path, or the lumberjacks walk too far to be useful.
- A placed building is only a construction site. Beavers build it after the materials are delivered
  from storage, so make sure the district actually has those goods (see `stock`), and that
  storage and a path connect to it.
- Coordinates are x, y on the ground; z is height. The district center is your hub: put things
  within walking distance of it and connect them with paths.
- Like a human player, you do not know when a drought or badtide will come or how long it will
  last. The game warns about 3 days ahead (`hazard_approaching`, with `hazard_type`), but never says
  how many days remain; once it starts you see `hazard_active` and `hazard_days_left`. Timberborn
  has recurring hazards, droughts cut off water, so keep a healthy water and food reserve at all
  times and grow it before expanding. When the warning appears, use what time is left to top up
  storage and finish what protects the colony. Never state the date of the next hazard in your
  notes, because you do not know it.
- SURVIVAL COMES FIRST. Beavers drink water constantly and the starting water runs out within a day or
  two, after which they die. In the first game days your only goals are: (1) a lumberjack flag that is
  truly working, so logs arrive; (2) a water pump built with those logs, on the river bank. Do not spend
  logs on anything else until the pump is finished. Verify instead of assuming: after placing a flag,
  check on later turns that `Log` is rising or that sites are getting finished. If after about a game
  day nothing has improved, something is wrong (unreachable flag, trees not reachable, no workers):
  find the cause and fix it, or demolish it and place it somewhere reachable, rather than waiting.
- Pace your building to your income. Every construction site waiting on materials competes for the
  same logs, so queuing several costly buildings at once means none of them finish. Early on, logs
  are the bottleneck: queue ONE costly building, wait until it is built, then queue the next. A
  `Log` stock of 0 does not prove logs are not being cut; cut logs go straight to waiting sites.
  Check `placed_buildings` for `finished: false` before adding more.
- When there is nothing useful to do but wait, run the game fast (speed 5 to 7). Each of your turns
  is only a few seconds of real time, so at speed 3 almost no game time passes between them.
- The game starts paused. Set speed when you want time to pass, and slow down or pause if you need
  to think through something complicated.
- Commands can fail. Read the reply and adapt; never repeat a command that just failed for the
  same reason.
- If `in_game` is false, no save is loaded: do not issue commands.
- If `errors` is non-empty, those sections are missing rather than zero. Say so in your note
  instead of reasoning over them.
- Prefer one clear decision per turn over scattering buildings.
"""


def call_mod(path, payload=None, timeout=35):
    """GET or POST against the in-game HTTP API. Returns a dict and never raises."""
    url = MOD_URL + path
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method="POST" if data is not None else "GET")
    req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode())
    except urllib.error.HTTPError as exc:
        # The mod returns JSON error bodies with 4xx/5xx; keep them, they explain the failure.
        try:
            return json.loads(exc.read().decode())
        except Exception:
            return {"ok": False, "error": f"HTTP {exc.code} from mod at {url}"}
    except urllib.error.URLError as exc:
        return {"ok": False, "error": f"cannot reach mod at {url}: {exc}"}
    except (json.JSONDecodeError, TimeoutError, OSError) as exc:
        return {"ok": False, "error": f"bad response from mod: {exc}"}


def run_tool(name, args):
    """Route one tool call to the right mod endpoint."""
    if name == "get_map":
        query = urllib.parse.urlencode({k: v for k, v in args.items() if v is not None})
        return call_mod("/map" + ("?" + query if query else ""))
    if name == "get_buildings":
        return call_mod("/buildings" + ("?all=1" if args.get("include_locked") else ""))
    payload = dict(args)
    payload["action"] = name
    return call_mod("/command", payload)


def bootstrap_context():
    """Static-ish facts handed over once at the start so the first turn isn't spent fetching them."""
    buildings = call_mod("/buildings")
    area = call_mod("/map")
    return (
        "Buildings you can build right now (names, unlock state, costs):\n"
        + json.dumps(buildings)
        + "\n\nMap around the district center:\n"
        + json.dumps(area)
    )


def flatten(turns):
    return [message for turn in turns for message in turn]


THINKING_TYPES = ("thinking", "redacted_thinking")


def block_type(block):
    return block.get("type") if isinstance(block, dict) else getattr(block, "type", None)


def strip_thinking(turn):
    """Drop reasoning blocks from a finished turn before it goes into history.

    Thinking blocks carry a signature bound to the exact conversation that preceded them. Once old
    turns are trimmed away, a kept block no longer matches its prefix and the API rejects the whole
    request with a 400. They are only needed while the turn that produced them is still running,
    so history keeps the text and tool calls and nothing else.
    """
    cleaned = []
    for message in turn:
        content = message["content"]
        if message["role"] == "assistant" and isinstance(content, list):
            kept = [b for b in content if block_type(b) not in THINKING_TYPES]
            if not kept:
                continue
            cleaned.append({"role": "assistant", "content": kept})
        else:
            cleaned.append(message)
    return cleaned


def play_turn(client, model, system, goal_message, turns, turn_no, verbose):
    state = call_mod("/state")

    if not state.get("in_game", False):
        print(f"[turn {turn_no}] waiting: {state.get('error') or state.get('note') or 'no save loaded'}")
        return None

    print(f"[turn {turn_no}] cycle {state.get('cycle')} day {state.get('cycle_day')} "
          f"hazard {'ACTIVE' if state.get('hazard_active') else ('WARNING' if state.get('hazard_approaching') else 'none')}, beavers {state.get('beavers')}, "
          f"stock {json.dumps({k: v.get('available') for k, v in (state.get('stock') or {}).items() if isinstance(v, dict)})}")

    this_turn = [{
        "role": "user",
        "content": "World snapshot:\n" + json.dumps(state) + "\n\nWhat do you do next?",
    }]

    for _ in range(MAX_TOOL_ROUNDS):
        messages = [goal_message] + flatten(turns[-(KEEP_TURNS - 1):]) + this_turn
        response = client.messages.create(
            model=model,
            max_tokens=2500,
            system=system,
            tools=TOOLS,
            messages=messages,
        )
        this_turn.append({"role": "assistant", "content": response.content})

        results = []
        for block in response.content:
            if block.type == "text" and block.text.strip() and verbose:
                print("    " + block.text.strip())
            elif block.type == "tool_use":
                outcome = run_tool(block.name, block.input)
                summary = json.dumps(outcome)
                print(f"    -> {block.name}({json.dumps(block.input)}): "
                      f"{summary if len(summary) < 300 else summary[:300] + '...'}")
                results.append({
                    "type": "tool_result",
                    "tool_use_id": block.id,
                    "content": summary,
                })

        if not results:
            break
        this_turn.append({"role": "user", "content": results})

    return this_turn


def main():
    parser = argparse.ArgumentParser(description="Drive a Timberborn playthrough with Claude.")
    parser.add_argument("--goal", default="agent/goal.md", help="File describing the playthrough objective")
    parser.add_argument("--model", default=DEFAULT_MODEL, help="Claude model id")
    parser.add_argument("--interval", type=float, default=15.0, help="Seconds to wait between turns")
    parser.add_argument("--max-turns", type=int, default=0, help="0 runs until interrupted")
    parser.add_argument("--quiet", action="store_true", help="Hide the model's free-text commentary")
    args = parser.parse_args()

    if not os.environ.get("ANTHROPIC_API_KEY"):
        sys.exit("ANTHROPIC_API_KEY is not set.")

    with open(args.goal, encoding="utf-8") as fh:
        goal_text = fh.read()

    probe = call_mod("/health", timeout=5)
    if probe.get("error"):
        sys.exit(f"Mod not reachable. Is the game running with the mod loaded?\n{probe['error']}")

    # A key that is not scoped to a workspace must say which workspace to use.
    headers = {}
    workspace = os.environ.get("ANTHROPIC_WORKSPACE_ID")
    if workspace:
        headers["anthropic-workspace-id"] = workspace
    client = anthropic.Anthropic(default_headers=headers or None)
    goal_message = {
        "role": "user",
        "content": "Your objective for this playthrough:\n\n" + goal_text + "\n\n" + bootstrap_context(),
    }

    turns = []
    turn_no = 0
    try:
        while args.max_turns == 0 or turn_no < args.max_turns:
            turn_no += 1
            try:
                finished = play_turn(client, args.model, SYSTEM, goal_message, turns, turn_no, not args.quiet)
            except anthropic.APIStatusError as exc:
                # 400/401/403/404 mean the request itself is wrong (bad key, workspace,
                # model id). Retrying cannot fix that, so stop and say why.
                if exc.status_code in (400, 401, 403, 404):
                    sys.exit(f"API rejected the request ({exc.status_code}): {exc.message}")
                print(f"[turn {turn_no}] API error {exc.status_code}: {exc.message}; retrying after a pause", file=sys.stderr)
                time.sleep(20)
                continue
            except anthropic.APIConnectionError as exc:
                print(f"[turn {turn_no}] network error: {exc}; retrying after a pause", file=sys.stderr)
                time.sleep(20)
                continue

            if finished:
                turns.append(strip_thinking(finished))
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("\nstopped.")


if __name__ == "__main__":
    main()
