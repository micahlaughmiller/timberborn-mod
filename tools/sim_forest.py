"""Place lumberjack flags at the densest tree clusters, join them to the road, and mark the trees around them.

It looks at the map around the district center, finds where trees are thickest on the center's own level
and on the level above it, and for each spot: finds a flag site, builds it, connects it to the center's
door (over a natural slope for the upper level), then marks every tree within 30 walking steps of the flag.

    py tools\\sim_forest.py            # report only: where it would place flags (nothing is built or marked)
    py tools\\sim_forest.py --live     # place, connect and mark
    py tools\\sim_forest.py --live --per-level 3 --up 2   # three flags per level, and also the level 2 above

Options: --per-level N (default 2), --up N (how many levels above the center to include, default 1),
--radius N (mark radius, default 30).
"""
import json
import re
import sys
import urllib.request

BASE = "http://127.0.0.1:8787"
LIVE = "--live" in sys.argv
OUT = []
TREE = re.compile(r"Pine|Birch|Oak|Maple|Chestnut|Tree", re.I)


def arg(name, default):
    for i, a in enumerate(sys.argv):
        if a == name and i + 1 < len(sys.argv):
            return int(sys.argv[i + 1])
    return default


PER_LEVEL = arg("--per-level", 2)
UP = arg("--up", 1)
RADIUS = arg("--radius", 30)


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


def short(value, limit=400):
    text = value if isinstance(value, str) else json.dumps(value)
    return text if len(text) <= limit else text[:limit] + "...(cut)"


state = get("/state")
faction = state.get("faction") or "Folktails"
center = next((b for b in state.get("placed_buildings", []) if str(b.get("name", "")).startswith("DistrictCenter")), None)
if not center:
    sys.exit("no district center found in /state")

road = center["access_cell"]
cx, cy, cz = center["x"], center["y"], center["z"]
say("LUMBERJACK FLAGS (" + ("LIVE" if LIVE else "dry run: nothing is built or marked") + ") faction=" + faction
    + " center=(%d,%d) level=%d" % (cx, cy, cz))

# ---- read the map around the center: trees and ground level per cell
SIDE = 48
x0, y0 = cx - SIDE // 2, cy - SIDE // 2
area = get("/map?x=%d&y=%d&w=%d&h=%d" % (x0, y0, SIDE, SIDE))
ox, oy = area["origin"]["x"], area["origin"]["y"]
legend = area.get("object_legend", {})
tree_letters = {letter for letter, text in legend.items() if TREE.search(text)}
objects, heights = area["objects"], area["heights"]


def level(x, y):
    try:
        ch = heights[y - oy][x - ox]
        return None if ch == "-" else int(ch, 36)
    except (IndexError, ValueError):
        return None


trees = {}
for r, row in enumerate(objects):
    for c, ch in enumerate(row):
        if ch in tree_letters:
            trees[(ox + c, oy + r)] = level(ox + c, oy + r)

say("trees in the %dx%d window: %d" % (SIDE, SIDE, len(trees)))
by_level = {}
for z in trees.values():
    by_level[z] = by_level.get(z, 0) + 1
say("trees by ground level: " + ", ".join("level %s: %d" % (z, n) for z, n in sorted(by_level.items(), key=lambda kv: str(kv[0]))))


def density(x, y, z, r=4):
    return sum(1 for (tx, ty), tz in trees.items() if tz == z and abs(tx - x) <= r and abs(ty - y) <= r)


def best_spots(z, count, spacing=10):
    """The densest cells on this ground level, each at least `spacing` from one already chosen."""
    cells = [(density(x, y, z), x, y) for (x, y), tz in trees.items() if tz == z]
    cells.sort(reverse=True)
    chosen = []
    for score, x, y in cells:
        if all(abs(x - px) + abs(y - py) >= spacing for _, px, py in chosen):
            chosen.append((score, x, y))
        if len(chosen) == count:
            break
    return chosen


plans = []
for z in range(cz, cz + 1 + UP):
    spots = best_spots(z, PER_LEVEL)
    label = "the center's level" if z == cz else "%d level(s) up" % (z - cz)
    if not spots:
        say("level %d (%s): no trees found" % (z, label))
    for score, x, y in spots:
        plans.append((z, label, score, x, y))

prefab = "LumberjackFlag." + faction
for z, label, score, x, y in plans:
    say("")
    say("=== flag on level %d (%s) near the cluster at (%d,%d), %d trees within 4 cells" % (z, label, x, y, score))
    try:
        # rank "near": the spot closest to the tree wins, not the one closest to the settlement
        found = post({"action": "find_sites", "prefab": prefab, "near_x": x, "near_y": y, "rank": "near", "max": 12})
        sites = [s for s in found.get("sites", []) if s["z"] == z]
        if not sites:
            say("no flag site on level %d near there: %s" % (z, short(found, 300)))
            continue
        site = sites[0]
        door = site.get("doorstep") or {"x": site["x"], "y": site["y"]}
        say("best site (%d,%d,z%d) door (%d,%d) %d cells from the tree, walk to settlement=%s new_tiles=%s" % (
            site["x"], site["y"], site["z"], door["x"], door["y"], site["distance"],
            site.get("walk_steps_to_settlement"), site.get("new_path_tiles")))
        if site["distance"] > 3:
            say("WARNING: no valid flag site within 3 cells of that tree; this one is %d away" % site["distance"])

        built = post({"action": "build", "prefab": prefab, "x": site["x"], "y": site["y"], "z": site["z"],
                      "orientation": site["orientation"], "dry_run": not LIVE})
        say("build: " + short(built, 250))
        if not built.get("ok"):
            continue

        if LIVE:
            joined = post({"action": "connect", "x1": door["x"], "y1": door["y"], "x2": road["x"], "y2": road["y"]})
            say("connect: " + short(joined, 300))
            marked = post({"action": "mark_trees", "from_x": door["x"], "from_y": door["y"], "radius": RADIUS})
            say("mark_trees: " + short(marked, 400))
        else:
            say("(dry run: would connect the door to the center and mark every tree within %d steps)" % RADIUS)
    except Exception as exc:  # report and carry on with the next spot
        say("FAILED: " + type(exc).__name__ + ": " + str(exc))

if LIVE:
    say("")
    say("=== apply_priorities / manage_workers")
    say(short(post({"action": "apply_priorities"}), 300))
    say(short(post({"action": "manage_workers"}), 500))

with open("forest-out.txt", "w", encoding="utf-8") as handle:
    handle.write("\n".join(OUT))
say("")
say("Report saved to forest-out.txt")
