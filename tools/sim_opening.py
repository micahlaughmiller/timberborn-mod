"""The opening build after the lumberjack flags, in the order the AI is told to follow.

    gatherer flag (beside the nearest berry bushes, set to Berries)
    water pump (at the water nearest the district center)
    small warehouse, small tank, one inventor, then housing

Each building: find a site with find_sites (the spot beside its target for flags and pumps, the shortest
walk for the rest), build it, connect its door to the district center's door, read what the game says.
A building already in the colony is skipped, so it is safe to run twice.

    py tools\\sim_opening.py            # report only (builds are dry runs)
    py tools\\sim_opening.py --live     # place, connect, set the gatherer, apply priorities and crews
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


def short(value, limit=350):
    text = value if isinstance(value, str) else json.dumps(value)
    return text if len(text) <= limit else text[:limit] + "...(cut)"


state = get("/state")
faction = state.get("faction") or "Folktails"
placed = state.get("placed_buildings") or []
center = next((b for b in placed if str(b.get("name", "")).startswith("DistrictCenter")), None)
if not center:
    sys.exit("no district center found in /state")
road = center["access_cell"]
say("OPENING (" + ("LIVE" if LIVE else "dry run: nothing is built") + ") faction=%s center=(%d,%d) level=%d" % (
    faction, center["x"], center["y"], center["z"]))

# ---- where the bushes and the water are, from the map around the center
SIDE = 48
area = get("/map?x=%d&y=%d&w=%d&h=%d" % (center["x"] - SIDE // 2, center["y"] - SIDE // 2, SIDE, SIDE))
ox, oy = area["origin"]["x"], area["origin"]["y"]
bush_letters = {k for k, v in area.get("object_legend", {}).items() if "Bush" in v}
bushes = [(ox + c, oy + r) for r, row in enumerate(area["objects"]) for c, ch in enumerate(row) if ch in bush_letters]
water = [(ox + c, oy + r) for r, row in enumerate(area["water"]) for c, ch in enumerate(row) if ch == "~"]


def nearest(points):
    if not points:
        return None
    return min(points, key=lambda p: abs(p[0] - road["x"]) + abs(p[1] - road["y"]))


say("bushes in view: %d, water cells in view: %d" % (len(bushes), len(water)))


def place(base, target=None, after=None, limit=5):
    prefab = base + "." + faction
    say("")
    say("=== " + prefab)
    if any(str(b.get("name", "")) == prefab for b in placed):
        say("already placed, skipped")
        return None
    try:
        query = {"action": "find_sites", "prefab": prefab, "max": 10}
        if target:
            query.update({"near_x": target[0], "near_y": target[1], "rank": "near"})
            say("looking beside (%d,%d)" % target)
        found = post(query)
        sites = [s for s in found.get("sites", []) if not target or s["distance"] <= limit] or found.get("sites", [])
        if not sites:
            say("no site: " + short(found, 300))
            return None
        site = sites[0]
        door = site.get("doorstep") or {"x": site["x"], "y": site["y"]}
        say("site (%d,%d,z%d) %s door (%d,%d) %s cells from the target, walk=%s new_tiles=%s" % (
            site["x"], site["y"], site["z"], site["orientation"], door["x"], door["y"], site["distance"],
            site.get("walk_steps_to_settlement"), site.get("new_path_tiles")))

        built = post({"action": "build", "prefab": prefab, "x": site["x"], "y": site["y"], "z": site["z"],
                      "orientation": site["orientation"], "dry_run": not LIVE})
        say("build: " + short(built, 250))
        if LIVE and built.get("ok"):
            if site.get("new_path_tiles"):
                say("connect: " + short(post({"action": "connect", "x1": door["x"], "y1": door["y"],
                                              "x2": road["x"], "y2": road["y"]}), 300))
            if after:
                after(site)
        return site
    except Exception as exc:  # report and carry on with the next building
        say("FAILED: " + type(exc).__name__ + ": " + str(exc))
        return None


def to_berries(site):
    say("set_gatherer: " + short(post({"action": "set_gatherer", "x": site["x"], "y": site["y"], "good": "Berries"}), 300))


# ---- the build order
place("GathererFlag", nearest(bushes), after=to_berries)
place("WaterPump", nearest(water), limit=6)
place("SmallWarehouse")
place("SmallTank")
place("Inventor")
place("Lodge")

if LIVE:
    say("")
    say("=== apply_priorities / manage_workers")
    say(short(post({"action": "apply_priorities"}), 350))
    say(short(post({"action": "manage_workers"}), 500))

with open("opening-out.txt", "w", encoding="utf-8") as handle:
    handle.write("\n".join(OUT))
say("")
say("Report saved to opening-out.txt")
