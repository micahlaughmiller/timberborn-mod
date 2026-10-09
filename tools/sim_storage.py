"""Tell every finished warehouse and tank what it holds.

A storage building's setting only exists once it is finished, so this waits (the game must be running,
not paused) until the buildings are done, then sets each one:

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


deadline = time.time() + WAIT
while True:
    found = stores()
    waiting = [b for b in found if not b.get("finished")]
    if not waiting or time.time() >= deadline:
        break
    say("waiting for %d storage building(s) to finish (unpause the game if it is paused)..." % len(waiting))
    time.sleep(5)

if not found:
    say("no warehouses or tanks placed yet")

done = {}
for b in found:
    name = str(b["name"])
    if not b.get("finished"):
        say("%s at (%s,%s): not finished yet, skipped" % (name, b["x"], b["y"]))
        continue
    good = good_for(name)
    reply = post({"action": "set_storage", "x": b["x"], "y": b["y"], "good": good})
    say("%s at (%s,%s) -> %s: %s" % (name, b["x"], b["y"], good, json.dumps(reply)[:500]))
    done[(b["x"], b["y"])] = reply.get("ok")

say("")
say("%d of %d storage buildings set" % (sum(1 for ok in done.values() if ok), len(found)))
with open("storage-out.txt", "w", encoding="utf-8") as handle:
    handle.write("\n".join(OUT))
say("Report saved to storage-out.txt")
