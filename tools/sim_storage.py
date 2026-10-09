"""Tell every finished warehouse and tank what it holds.

Sets each store straight away, finished or not, so one that accepts the setting while it is still being built
is ready to take goods the moment it is done. It then waits (the game must be running, not paused) for them
to finish and sets them again, in case the game resets the setting on completion:

    warehouses -> Berries (food)     tanks -> Water

    py tools\\sim_storage.py                       # wait up to 5 minutes, then set what is finished
    py tools\\sim_storage.py --wait 0              # do not wait, set only what is finished now
    py tools\\sim_storage.py --warehouse Log --tank Badwater   # different goods

Safe to run again: it sets the same good on the same buildings.
"""
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:8787"


def arg(name, default):
    for i, a in enumerate(sys.argv):
        if a == name and i + 1 < len(sys.argv):
            return sys.argv[i + 1]
    return default


WAIT = int(arg("--wait", "300"))
WAREHOUSE_GOOD = arg("--warehouse", "Berries")
TANK_GOOD = arg("--tank", "Water")
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


def good_for(name):
    if "Tank" in name:
        return TANK_GOOD
    if "Warehouse" in name or "Pile" in name:
        return WAREHOUSE_GOOD
    return None


def stores():
    return [b for b in get("/state").get("placed_buildings", []) if good_for(str(b.get("name", "")))]


def apply(found, label):
    results = {}
    for b in found:
        name = str(b["name"])
        good = good_for(name)
        reply = post({"action": "set_storage", "x": b["x"], "y": b["y"], "good": good})
        state_word = "finished" if b.get("finished") else "under construction"
        say("[%s] %s at (%s,%s) %s -> %s: %s" % (label, name, b["x"], b["y"], state_word, good, json.dumps(reply)[:450]))
        results[(b["x"], b["y"])] = reply.get("ok")
    return results


found = stores()
if not found:
    say("no warehouses or tanks placed yet")

# 1. Right now, finished or not: a store that accepts the setting while under construction is ready to take
#    goods the moment it is built.
done = apply(found, "now")

# 2. Wait for construction to finish and set again, in case the game resets the setting when a building completes.
deadline = time.time() + WAIT
while WAIT > 0 and time.time() < deadline:
    found = stores()
    waiting = [b for b in found if not b.get("finished")]
    if not waiting:
        done = apply(found, "after finishing")
        break
    say("waiting for %d storage building(s) to finish (unpause the game if it is paused)..." % len(waiting))
    time.sleep(5)

say("")
say("%d of %d storage buildings set" % (sum(1 for ok in done.values() if ok), len(found)))
with open("storage-out.txt", "w", encoding="utf-8") as handle:
    handle.write("\n".join(OUT))
say("Report saved to storage-out.txt")
