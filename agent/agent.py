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
                "z": {"type": "integer", "description": "Optional. Defaults to the terrain surface."},
                "orientation": {"type": "string", "enum": ["Cw0", "Cw90", "Cw180", "Cw270"]},
                "dry_run": {"type": "boolean"},
            },
            "required": ["prefab", "x", "y"],
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
            "grid. The reply reports how many marked cells actually have trees on them."
        ),
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
- Look before you build. Use `get_map` to find open, flat ground and `get_buildings` for costs.
  Check a spot with build(dry_run=true) before committing. Heights in the map are surface levels:
  a building needs every cell it covers to be the same level, free of trees and other buildings.
- get_map also returns an `objects` grid: a letter per cell naming what stands there (trees, berry
  bushes, ruins, buildings), with `object_legend` saying which letter is which. Use it. A lumberjack
  flag only helps if there are trees close to it, and a gatherer flag only if there are berry bushes
  close to it. Check the grid; do not guess where the trees are.
- Beavers walk and haul along paths. A building with no path to the district center will never be
  built or worked. After placing anything, connect it with build_path, and check the reply: if it lists
  blocked tiles, route around them with another build_path call.
- A lumberjack flag only sends beavers to trees inside an area marked for cutting. Placing the flag
  is not enough: call mark_trees on a rectangle of trees close to the flag (the reply says how many
  of the marked cells have trees; if that is 0 you picked bare ground). Keep the marked area near
  the flag and connected by path, or the lumberjacks walk too far to be useful.
- A placed building is only a construction site. Beavers build it after the materials are delivered
  from storage, so make sure the district actually has those goods (see `stock`), and that
  storage and a path connect to it.
- Coordinates are x, y on the ground; z is height. The district center is your hub: put things
  within walking distance of it and connect them with paths.
- Droughts are the real clock (`days_until_hazard`, `hazard_type`). Water and food storage ahead
  of a drought beat any expansion.
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


def play_turn(client, model, system, goal_message, turns, turn_no, verbose):
    state = call_mod("/state")

    if not state.get("in_game", False):
        print(f"[turn {turn_no}] waiting: {state.get('error') or state.get('note') or 'no save loaded'}")
        return None

    print(f"[turn {turn_no}] cycle {state.get('cycle')} day {state.get('cycle_day')} "
          f"drought in {state.get('days_until_hazard')} days, beavers {state.get('beavers')}, "
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
                turns.append(finished)
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("\nstopped.")


if __name__ == "__main__":
    main()
