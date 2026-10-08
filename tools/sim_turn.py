"""Simulate one AI turn against the running game without calling the API.

It does what the agent does on its first turns: read the state, look for sites for the opening
buildings, mark trees for each lumberjack flag, set priorities and crews. Building is a dry run
unless --live is given, so the default changes nothing except priorities and crews.

    py tools\\sim_turn.py            # report only (builds are dry runs)
    py tools\\sim_turn.py --live     # actually place the buildings and connect them

The whole report is also written to sim-out.txt so it can be pasted back in one piece.
"""
import json
import sys
import urllib.request

BASE = "http://127.0.0.1:8787"
LIVE = "--live" in sys.argv
OUT = []


def say(text=""):
    print(text)
    OUT.append(text)


def get(path):
    with urllib.request.urlopen(BASE + path, timeout=60) as response:
        return json.loads(response.read().decode("utf-8"))


def post(body):
    data = json.dumps(body).encode("utf-8")
    request = urllib.request.Request(BASE + "/command", data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.loads(response.read().decode("utf-8"))


def short(value, limit=700):
    text = value if isinstance(value, str) else json.dumps(value)
    return text if len(text) <= limit else text[:limit] + "...(cut)"


def step(title, action):
    say("")
    say("=== " + title)
    try:
        result = action()
        say(short(result) if result is not None else "(done)")
        return result
    except Exception as exc:  # report and keep going: each step is independent
        say("FAILED: " + type(exc).__name__ + ": " + str(exc))
        return None


state = get("/state")
faction = state.get("faction") or "Folktails"
buildings = state.get("placed_buildings") or []

say("SIMULATED TURN (" + ("LIVE: buildings are placed" if LIVE else "dry run: nothing is built") + ")")
say("faction=%s cycle=%s day=%s progress=%.2f speed=%s" % (
    faction, state.get("cycle"), state.get("cycle_day"), state.get("cycle_progress") or 0, state.get("speed")))
say("beavers=" + short(state.get("beavers"), 300))
say("stock=" + short(state.get("stock"), 400))
say("hazard: active=" + str(state.get("hazard_active")) + " approaching=" + str(state.get("hazard_approaching")))

say("")
say("=== placed buildings")
for b in buildings:
    say("  %s at (%s,%s,%s) finished=%s access=%s problems=%s" % (
        b.get("name"), b.get("x"), b.get("y"), b.get("z"), b.get("finished"),
        b.get("access_cell") and (b["access_cell"].get("x"), b["access_cell"].get("y")),
        b.get("problems") or "-"))

center = next((b for b in buildings if str(b.get("name", "")).startswith("DistrictCenter")), None)
road = (center or {}).get("access_cell") or {}
near = {"near_x": (center or {}).get("x", 100), "near_y": (center or {}).get("y", 130)}

# ---- opening buildings, in the build order the agent is told to follow
for base in ["SmallWarehouse", "SmallTank", "Inventor", "Lodge"]:
    prefab = base + "." + faction
    if any(str(b.get("name", "")) == prefab for b in buildings):
        say("")
        say("=== " + prefab + ": already placed, skipped")
        continue

    def try_site(prefab=prefab):
        found = post(dict({"action": "find_sites", "prefab": prefab}, **near))
        sites = found.get("sites") or []
        if not sites:
            return "no site: " + short(found, 300)
        top = sites[0]
        body = {"action": "build", "prefab": prefab, "x": top["x"], "y": top["y"], "z": top["z"],
                "orientation": top["orientation"], "dry_run": not LIVE}
        placed = post(body)
        text = "best site (%s,%s,z%s) %s walk=%s new_tiles=%s -> %s" % (
            top["x"], top["y"], top["z"], top["orientation"], top.get("walk_steps_to_settlement"),
            top.get("new_path_tiles"), short(placed, 250))
        if LIVE and placed.get("ok") and top.get("new_path_tiles") and road:
            door = top["doorstep"]
            text += "\n  connect: " + short(post({"action": "connect", "x1": door["x"], "y1": door["y"],
                                                    "x2": road.get("x"), "y2": road.get("y")}), 300)
        return text

    step("site + build " + prefab, try_site)

# ---- trees for every lumberjack flag
for b in buildings:
    if str(b.get("name", "")).startswith("LumberjackFlag") and b.get("access_cell"):
        cell = b["access_cell"]
        step("mark_trees for flag at (%s,%s) door (%s,%s)" % (b["x"], b["y"], cell["x"], cell["y"]),
             lambda cell=cell: post({"action": "mark_trees", "from_x": cell["x"], "from_y": cell["y"]}))

# ---- priorities and crews
step("apply_priorities", lambda: post({"action": "apply_priorities"}))
step("manage_workers", lambda: post({"action": "manage_workers"}))

# ---- storage on any finished warehouse
for b in buildings:
    if "Warehouse" in str(b.get("name", "")) and b.get("finished"):
        step("components of warehouse at (%s,%s)" % (b["x"], b["y"]),
             lambda b=b: get("/components?x=%s&y=%s" % (b["x"], b["y"])))
        if LIVE:
            step("set_storage Berries at (%s,%s)" % (b["x"], b["y"]),
                 lambda b=b: post({"action": "set_storage", "x": b["x"], "y": b["y"], "good": "Berries"}))

step("recipes", lambda: "%s recipes available" % get("/recipes?faction=" + faction).get("count"))

with open("sim-out.txt", "w", encoding="utf-8") as handle:
    handle.write("\n".join(OUT))
say("")
say("Report saved to sim-out.txt")
