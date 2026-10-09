"""Offline 100-day simulation of the colony plan, written to a Word document.

No game and no API needed. It plays the agreed policy forward day by day: the build ladder, the storage ramp
(3, 6, 10, 20 days), the 40% storage spending cap with a log reserve, housing before storage, farms, the drought
every cycle, and the 15-step tree-flag rule. Every game number that has not been read from the game is an
ASSUMPTION listed in the document, so it can be corrected and the run repeated.

    py tools\\simulate_100_days.py                      # writes timberborn-100-days.docx
    py tools\\simulate_100_days.py --params mine.json   # override any assumption, e.g. {"lodge_beds": 3}
    py tools\\simulate_100_days.py --days 150 --out plan.docx

Needs: py -m pip install python-docx
"""
import argparse
import json
import math

# ---- assumptions: (value, status, note). Status UNVERIFIED means not read from the game.
ASSUME = {
    "start_adults": (9, "game", "from /state at start"),
    "start_children": (4, "game", "from /state at start"),
    "start_berries": (130, "game", "from /state at start"),
    "start_logs": (0, "game", "stock shows no logs at start"),
    "cycle_days": (16, "game", "from /state"),
    "hazard_start_day": (14, "game", "drought starts cycle day 14 for 3 days (debug state)"),
    "hazard_days": (3, "game", "drought duration"),
    "drink_from_river": (True, "UNVERIFIED", "beavers drink from open water until the first pump or tank exists"),
    "water_per_beaver": (2.5, "UNVERIFIED", "repo note says about 2.1; check /specs?type=NeedSpec"),
    "food_per_beaver": (2.5, "UNVERIFIED", "check /specs?type=NeedSpec"),
    "child_factor": (0.5, "UNVERIFIED", "children eat and drink less"),
    "center_beds": (12, "UNVERIFIED", "does the district center house beavers? check /spec?name=DistrictCenter.Folktails"),
    "lodge_beds": (4, "UNVERIFIED", "check /spec?name=Lodge.Folktails"),
    "lodge_cost": (12, "game", "building list"),
    "pump_cost": (12, "game", "building list"),
    "small_tank_cost": (15, "game", "building list"),
    "small_wh_cost": (3, "game", "building list"),
    "medium_wh_cost": (15, "game", "building list (unlocked at start)"),
    "farm_cost": (25, "game", "building list (EfficientFarmHouse)"),
    "mill_cost": (15, "game", "building list"),
    "wheel_cost": (20, "game", "building list (PowerWheel)"),
    "inventor_cost": (12, "game", "building list"),
    "grill_cost": (25, "game", "building list"),
    "small_tank_cap": (300, "UNVERIFIED", "check /spec?name=SmallTank.Folktails"),
    "small_wh_cap": (180, "UNVERIFIED", "check /spec?name=SmallWarehouse.Folktails"),
    "medium_wh_cap": (900, "UNVERIFIED", "check /spec?name=MediumWarehouse.Folktails"),
    "center_store": (200, "UNVERIFIED", "goods the district center itself can hold"),
    "logs_per_lumberjack": (6, "UNVERIFIED", "measure from /state stock changes"),
    "berries_per_gatherer": (10, "UNVERIFIED", "per gatherer per day"),
    "bush_supply": (3000, "UNVERIFIED", "berries available in range; the map showed about 580 bushes"),
    "water_per_pump": (48, "UNVERIFIED", "recipe 'Water' is 1 per 0.33 h; 16 working hours"),
    "farm_food_per_day": (24, "UNVERIFIED", "carrots per farm house at maturity"),
    "farm_lag": (6, "UNVERIFIED", "days from placing a farm to its first harvest"),
    "mill_planks_per_day": (12.8, "game", "Plank recipe: 1 per 1.25 h, 16 h"),
    "hazard_output_factor": (0.5, "UNVERIFIED", "pumps and farms during drought"),
    "mature_days": (13, "UNVERIFIED", "days for a child to become an adult"),
    "birth_rate": (0.05, "UNVERIFIED", "births per adult per day when beds and needs allow"),
    "build_days": (2, "UNVERIFIED", "days from placing to finished"),
    "forest_logs": (700, "UNVERIFIED", "logs in the trees within 15 steps of the first flags"),
    "forest_refill": (500, "UNVERIFIED", "logs reachable from each new flag"),
    "stage_days": ([3, 6, 10, 20], "plan", "storage ramp"),
    "water_target_per_beaver_day": (3, "user", "3 units per beaver per day"),
    "food_target_per_type_per_beaver_day": (2, "user", "2 of each food type per beaver per day (read as per day)"),
    "band": (0.15, "user", "15% fluctuation"),
    "storage_spend_share": (0.4, "plan", "storage spends at most this share of log income"),
}


def run(P, days):
    S = dict(adults=P["start_adults"], kids=[2, 5, 8, 11][:P["start_children"]], logs=P["start_logs"], planks=0.0,
             water=60.0, food=float(P["start_berries"]), bushes=P["bush_supply"], forest=P["forest_logs"])
    built = dict(flag_lj=0, gatherer=0, pump=0, lodge=0, tank=0, small_wh=0, medium_wh=0, inventor=0, mill=0,
                 wheel=0, farm=0, grill=0)
    farm_ready = []     # day each farm starts producing
    pending = []        # (done_day, kind)
    rows, events, problems = [], [], {}
    stage, held, budget = 0, 0, 0.0
    income_hist, low_logs, homeless_days, relocations = [], 0, 0, 0
    first = {}

    def beavers():
        return S["adults"] + len(S["kids"])

    def count(kind):
        return built[kind] + sum(1 for _, k in pending if k == kind)

    def beds():
        return P["center_beds"] + built["lodge"] * P["lodge_beds"]

    def pending_beds():
        return beds() + sum(P["lodge_beds"] for _, k in pending if k == "lodge")

    def cap_water():
        return max(50.0, built["tank"] * P["small_tank_cap"])

    def cap_goods():
        return P["center_store"] + built["small_wh"] * P["small_wh_cap"] + built["medium_wh"] * P["medium_wh_cap"]

    def weighted():
        return S["adults"] + P["child_factor"] * len(S["kids"])

    for day in range(1, days + 1):
        notes = []
        cyc = (day - 1) % P["cycle_days"] + 1
        hazard = P["hazard_start_day"] <= cyc < P["hazard_start_day"] + P["hazard_days"]
        factor = P["hazard_output_factor"] if hazard else 1.0

        for item in [p for p in pending if p[0] <= day]:
            pending.remove(item)
            built[item[1]] += 1
            first.setdefault(item[1], day)
            if item[1] == "farm":
                farm_ready.append(day + P["farm_lag"])
            notes.append("finished " + item[1])

        # ---- staffing, in the worker priority the user set
        free = S["adults"]
        slots = [("center_min", 2), ("gatherer", built["gatherer"]), ("farm", 2 * sum(1 for d in farm_ready if d <= day)),
                 ("pump", built["pump"]), ("lumberjack", built["flag_lj"]), ("mill", built["mill"]),
                 ("wheel", built["wheel"]), ("inventor", built["inventor"]), ("grill", built["grill"]), ("center_extra", 2)]
        staff = {}
        for name, want in slots:
            take = min(free, want)
            staff[name] = take
            free -= take
        wanted = sum(w for n, w in slots if n != "center_extra")
        unstaffed = max(0, wanted - S["adults"])
        if unstaffed:
            problems.setdefault("unstaffed", []).append(day)

        # ---- production
        lj = staff["lumberjack"]
        cut = min(lj * P["logs_per_lumberjack"], S["forest"])
        if S["forest"] <= 0.4 * P["forest_logs"] and relocations < (day // 12) + 1 and S["forest"] < 200:
            relocations += 1
            S["forest"] += P["forest_refill"]
            cut = 0
            notes.append("TREES MORE THAN 15 STEPS AWAY: new lumberjack flag placed, one day of cutting lost")
        S["forest"] -= cut
        space = cap_goods() - S["logs"] - S["planks"]
        if cut > space:
            notes.append("logs store full, %.0f logs wasted" % (cut - max(space, 0)))
            problems.setdefault("overflow", []).append(day)
            cut = max(space, 0)
        S["logs"] += cut

        goods_per_day = (sum(income_hist[-3:]) / max(1, len(income_hist[-3:]))) + (P["mill_planks_per_day"] if built["mill"] else 0)
        d_target = P["stage_days"][stage]
        plank_target = d_target * P["mill_planks_per_day"]
        reserve = max(30.0, 3 * (sum(income_hist[-3:]) / max(1, len(income_hist[-3:]))))
        if staff["mill"] and staff["wheel"] and S["planks"] < plank_target and S["logs"] > reserve:
            make = min(P["mill_planks_per_day"], S["logs"] - reserve)
            S["logs"] -= make
            S["planks"] += make
        income_hist.append(cut)

        S["water"] = min(cap_water(), S["water"] + staff["pump"] * P["water_per_pump"] * factor)
        berries = min(S["bushes"], staff["gatherer"] * P["berries_per_gatherer"])
        S["bushes"] -= berries
        if S["bushes"] <= 0:
            first.setdefault("bushes_out", day)
        produced_farm = 0.0
        active = [d for d in farm_ready if d <= day]
        if active:
            produced_farm = min(staff["farm"] / 2.0, len(active)) * P["farm_food_per_day"] * factor
        S["food"] = min(cap_goods(), S["food"] + berries + produced_farm)

        # ---- consumption, shortages, population
        need_w = weighted() * P["water_per_beaver"]
        need_f = weighted() * P["food_per_beaver"]
        river = P["drink_from_river"] and built["pump"] == 0 and built["tank"] == 0
        if S["water"] < need_w and not river:
            lost = max(1, math.ceil(0.05 * beavers()))
            notes.append("WATER SHORTAGE: %d beaver(s) lost" % lost)
            problems.setdefault("water_short", []).append(day)
            for _ in range(lost):
                (S["kids"].pop() if S["kids"] else S.__setitem__("adults", S["adults"] - 1))
        S["water"] = max(0.0, S["water"] - need_w)
        if S["food"] < need_f:
            lost = max(1, math.ceil(0.05 * beavers()))
            notes.append("FOOD SHORTAGE: %d beaver(s) lost" % lost)
            problems.setdefault("food_short", []).append(day)
            for _ in range(lost):
                (S["kids"].pop() if S["kids"] else S.__setitem__("adults", S["adults"] - 1))
        S["food"] = max(0.0, S["food"] - need_f)

        S["kids"] = [a + 1 for a in S["kids"]]
        grown = [a for a in S["kids"] if a >= P["mature_days"]]
        S["kids"] = [a for a in S["kids"] if a < P["mature_days"]]
        S["adults"] += len(grown)

        if beavers() == 0:
            first.setdefault("extinct", day)
        w_days = S["water"] / max(1.0, beavers() * P["water_per_beaver"])
        f_days = S["food"] / max(1.0, beavers() * P["food_per_beaver"])
        free_beds = beds() - beavers()
        if free_beds > 0 and w_days >= 1 and f_days >= 1:
            S.setdefault("acc", 0.0)
            S["acc"] += P["birth_rate"] * S["adults"]
            while S["acc"] >= 1 and beds() - beavers() > 0:
                S["kids"].append(0)
                S["acc"] -= 1
                notes.append("birth")
        if beavers() > beds():
            homeless_days += 1
            problems.setdefault("homeless", []).append(day)

        # ---- decisions
        B = beavers()
        income = sum(income_hist[-3:]) / max(1, len(income_hist[-3:]))
        reserve = max(30.0, 3 * income)
        budget = min(60.0, budget + P["storage_spend_share"] * income)
        pop = B + 4
        w_target = B * P["water_target_per_beaver_day"] * d_target
        types = 1 + (1 if built["farm"] else 0)
        f_target = types * B * P["food_target_per_type_per_beaver_day"] * d_target
        g_target = d_target * (income + (P["mill_planks_per_day"] if built["mill"] else 0))
        low = P["band"]
        if S["water"] >= (1 - low) * w_target and S["food"] >= (1 - low) * min(f_target, cap_goods()):
            held += 1
        else:
            held = 0
        if held >= 2 and stage < len(P["stage_days"]) - 1 and beds() >= B and B > 0:
            stage += 1
            held = 0
            notes.append("STORAGE STAGE UP: now targeting %d days" % P["stage_days"][stage])
            first.setdefault("stage%d" % P["stage_days"][stage], day)

        made = []

        def build(kind, cost, free_item=False):
            if not free_item:
                if S["logs"] < cost:
                    return False
                S["logs"] -= cost
            pending.append((day + (0 if free_item else P["build_days"]), kind))
            made.append(kind)
            return True

        per_day = 2 if built["flag_lj"] else 1
        ladder = [
            ("flag_lj", lambda: count("flag_lj") < 2, 0),
            ("gatherer", lambda: count("gatherer") < 1, 0),
            ("pump", lambda: count("pump") < 1, P["pump_cost"]),
            ("flag_lj", lambda: count("flag_lj") < max(2, math.ceil(S["adults"] / 5)), 0),
            ("lodge", lambda: pending_beds() < pop, P["lodge_cost"]),
            ("small_wh", lambda: count("small_wh") < 1, P["small_wh_cost"]),
            ("tank", lambda: count("tank") < 1, P["small_tank_cost"]),
            ("gatherer", lambda: count("gatherer") < min(5, math.ceil(weighted() * P["food_per_beaver"] / (0.8 * P["berries_per_gatherer"]))) and S["bushes"] > 200, 0),
            ("pump", lambda: count("pump") < 2 and w_days < 5, P["pump_cost"]),
        ]
        gate_ok = w_days >= 5 and f_days >= 5 and beds() >= B
        if gate_ok:
            ladder += [("inventor", lambda: count("inventor") < 1, P["inventor_cost"]),
                       ("mill", lambda: count("mill") < 1, P["mill_cost"]),
                       ("wheel", lambda: count("wheel") < count("mill"), P["wheel_cost"])]
        # Free items (flags) never wait for logs; costly ones go in order and stop at the first unaffordable one,
        # so logs are saved for it instead of being spent on something lower in the list.
        for kind, need, cost in ladder:
            if cost == 0:
                while need():
                    build(kind, 0, True)
        costly_done = 0
        for kind, need, cost in ladder:
            if cost == 0:
                continue
            if costly_done >= per_day:
                break
            if need():
                if not build(kind, cost):
                    break
                costly_done += 1

        # storage ladder: one store at a time, funded from the budget and never below the reserve
        if len(made) < per_day + 1 and gate_ok and not any(k in ("tank", "small_wh", "medium_wh") for _, k in pending):
            if built["tank"] * P["small_tank_cap"] < w_target and budget >= P["small_tank_cost"] and S["logs"] - P["small_tank_cost"] >= reserve:
                if build("tank", P["small_tank_cost"]):
                    budget -= P["small_tank_cost"]
            elif cap_goods() < f_target + g_target and budget >= P["medium_wh_cost"] and S["logs"] - P["medium_wh_cost"] >= reserve:
                if build("medium_wh", P["medium_wh_cost"]):
                    budget -= P["medium_wh_cost"]

        if len(made) < per_day and w_days >= 10 and f_days >= 8 and beds() >= pop and count("farm") < math.ceil(B / 4):
            if S["logs"] - P["farm_cost"] >= reserve and build("farm", P["farm_cost"]):
                pass
        if len(made) < per_day and farm_ready and farm_ready[0] <= day and count("grill") < 1:
            if S["logs"] - P["grill_cost"] >= reserve:
                build("grill", P["grill_cost"])

        if S["logs"] < reserve and day > 6:
            low_logs += 1
            if low_logs >= 2:
                problems.setdefault("log_deadlock", []).append(day)
        else:
            low_logs = 0

        if made:
            notes.append("placed " + ", ".join(made))
        rows.append(dict(day=day, adults=S["adults"], kids=len(S["kids"]), beds=beds(), logs=int(S["logs"]),
                         planks=int(S["planks"]), water=int(S["water"]), w_days=round(w_days, 1),
                         food=int(S["food"]), f_days=round(f_days, 1), stage=P["stage_days"][stage],
                         hazard="drought" if hazard else "", notes="; ".join(notes)))
    return rows, problems, first, built, S


def runs(ranges):
    """Collapse a sorted list of days into 'a-b, c' text."""
    out, start, prev = [], None, None
    for d in ranges + [None]:
        if start is None:
            start = prev = d
        elif d is not None and d == prev + 1:
            prev = d
        else:
            out.append(str(start) if start == prev else "%d-%d" % (start, prev))
            start = prev = d
    return ", ".join(out)


# ---- everything known to be open from the build, test and design work, independent of the simulation
KNOWN = [
    ("MOD GAPS (the AI cannot see or do these yet)", [
        "/state has no housing section: no beds, free beds or homeless count, so 'housing for every beaver' cannot be measured.",
        "/state reports no store capacity, so days of stock and 'target met' cannot be computed from the game.",
        "No forest status: nothing reports how far the nearest marked tree is, so the 15-step rule has no data to run on.",
        "No farming at all: the mod cannot plant crops or mark farmland. Needs a planting service; the real type names are not looked up yet.",
        "No irrigation or moisture read, so fields cannot be sited by water.",
        "Stairs are not supported; crossing levels works only over a natural Slope.",
        "Science: nothing reads or spends science points beyond unlock state, so the inventor plan is not measured.",
    ]),
    ("UNTESTED IN THE GAME", [
        "Gemini provider: never run; the first call may be rejected over a tool schema.",
        "Anthropic provider after the recent changes (map_now refresh, set_storage on placement): not run since credits ran out.",
        "set_storage on a construction site: the run showed the call is made; whether it holds after building was not confirmed.",
        "Workers staffing the new upper-level lumberjack flag: unconfirmed that beavers walk the slope and cut.",
        "Pumps during a drought, farms in a drought: behaviour assumed, not observed.",
        "Anything after day 1: the colony has not been run for a cycle with the full opening.",
        "The tree-name pattern (Pine, Birch, Oak, ...) for walking through trees: unverified for other tree types.",
    ]),
    ("DESIGN CONFLICTS TO DECIDE", [
        "Food rule is ambiguous: 2 per beaver per day per type (520 per type at 13 beavers) or 2 in total (26).",
        "Housing first vs storage first: the plan puts housing first; births need reserves, so the first drought can bite.",
        "The 20-day target is not reachable before the day-14 drought; the plan ramps 3, 6, 10, 20 days. Confirm the ramp.",
        "Medium warehouse (15 logs) is unlocked at start and far better per log than small ones; the plan uses them after the starter.",
        "With about 9 adults only about 8 job slots can be staffed; more buildings than that sit idle.",
    ]),
    ("FIXED DURING TESTING (listed so they are not rediscovered)", [
        "Doors on a different level than the building; walk measured through the building's own footprint; search margins too small for long routes; tree marking blocked by trees; flags far from trees. All fixed and pushed; the last four need a rebuild to take effect.",
    ]),
]


def write_doc(path, P, rows, problems, first, built, final, days):
    from docx import Document
    from docx.enum.section import WD_ORIENT
    from docx.shared import Pt

    doc = Document()
    section = doc.sections[0]
    section.orientation = WD_ORIENT.LANDSCAPE
    section.page_width, section.page_height = section.page_height, section.page_width
    doc.styles["Normal"].font.size = Pt(10)

    doc.add_heading("Timberborn colony plan: %d-day simulation" % days, 0)
    doc.add_paragraph("Offline simulation of the agreed policy. Game numbers marked UNVERIFIED are assumptions, "
                      "not read from the game. This document lists every problem found first; the day-by-day "
                      "timeline is at the back.")

    doc.add_heading("1. Problems found by the simulation", 1)
    found = []
    if "water_short" in problems:
        found.append("Water shortage on days %s." % runs(problems["water_short"]))
    if "food_short" in problems:
        found.append("Food shortage on days %s." % runs(problems["food_short"]))
    if "homeless" in problems:
        found.append("Beavers without a bed on %d days (%s)." % (len(problems["homeless"]), runs(problems["homeless"])))
    if "log_deadlock" in problems:
        found.append("Log stock below the reserve for two or more days in a row on days %s: storage and production "
                     "spending is frozen then." % runs(problems["log_deadlock"]))
    if "unstaffed" in problems:
        found.append("More job slots than adult beavers on %d days (first %d, last %d); the extras sit idle."
                     % (len(problems["unstaffed"]), problems["unstaffed"][0], problems["unstaffed"][-1]))
    if "overflow" in problems:
        found.append("Log store full, production wasted, on days %s." % runs(problems["overflow"]))
    notes = [r for r in rows if "TREES MORE THAN 15" in r["notes"]]
    if notes:
        found.append("Nearest trees exceeded 15 steps %d times (days %s): a new flag was needed each time."
                     % (len(notes), ", ".join(str(r["day"]) for r in notes)))
    if "bushes_out" in first:
        found.append("The wild berry supply ran out on day %d. Food production then depends on farms, which the plan "
                     "gates behind 8 days of stored food." % first["bushes_out"])
    if "farm" not in first and "food_short" in problems:
        found.append("PLAN FLAW: the farm gate (10 days of water, 8 days of food, housing for everyone) is circular. Food "
                     "stock only ever matches what the beavers eat, so 8 days of food never accumulates and no farm is built "
                     "before the berries run out. The gate must be tied to berry supply remaining or to food production "
                     "versus consumption, not to stock.")
    if "extinct" in first:
        found.append("THE COLONY DIED OUT on day %d." % first["extinct"])
    if "stage20" not in first:
        found.append("The 20-day storage stage was NOT reached in %d days." % days)
    else:
        found.append("The 20-day storage stage was reached on day %d." % first["stage20"])
    if "farm" not in first:
        found.append("No farm was ever built: its gate (10 days of water, 8 of food, housing for everyone) never held.")
    for line in found or ["None."]:
        doc.add_paragraph(line, style="List Bullet")

    doc.add_heading("2. Known gaps, untested pieces and open decisions", 1)
    for title, items in KNOWN:
        doc.add_heading(title, 2)
        for item in items:
            doc.add_paragraph(item, style="List Bullet")

    doc.add_heading("3. Milestones", 1)
    table = doc.add_table(rows=1, cols=2)
    table.style = "Light Grid Accent 1"
    table.rows[0].cells[0].text, table.rows[0].cells[1].text = "Milestone", "Day"
    labels = [("pump", "First water pump finished"), ("gatherer", "First gatherer flag"), ("small_wh", "Starter warehouse"),
              ("tank", "Starter tank"), ("lodge", "First lodge"), ("inventor", "Inventor"), ("mill", "Lumber mill"),
              ("wheel", "Power wheel"), ("farm", "First farm"), ("grill", "Grill"), ("stage6", "Storage stage 6 days"),
              ("stage10", "Storage stage 10 days"), ("stage20", "Storage stage 20 days")]
    for key, label in labels:
        cells = table.add_row().cells
        cells[0].text = label
        cells[1].text = str(first.get(key, "never"))

    doc.add_heading("Population and stock at checkpoints", 2)
    table = doc.add_table(rows=1, cols=8)
    table.style = "Light Grid Accent 1"
    for i, h in enumerate(["Day", "Adults", "Children", "Beds", "Logs", "Planks", "Water days", "Food days"]):
        table.rows[0].cells[i].text = h
    for r in rows:
        if r["day"] in (7, 14, 21, 30, 40, 50, 60, 75, 90, days):
            cells = table.add_row().cells
            for i, v in enumerate([r["day"], r["adults"], r["kids"], r["beds"], r["logs"], r["planks"], r["w_days"], r["f_days"]]):
                cells[i].text = str(v)

    doc.add_heading("4. Assumptions (correct these and rerun)", 1)
    table = doc.add_table(rows=1, cols=4)
    table.style = "Light Grid Accent 1"
    for i, h in enumerate(["Name", "Value", "Status", "Note / how to verify"]):
        table.rows[0].cells[i].text = h
    for name, (value, status, note) in ASSUME.items():
        cells = table.add_row().cells
        cells[0].text, cells[1].text, cells[2].text, cells[3].text = name, str(P[name]), status, note

    doc.add_heading("5. Policy the simulation applied", 1)
    for line in [
        "Order: survival (water, food, housing) -> lumberjack flags -> housing for everyone plus 4 spare beds -> starter warehouse and tank -> "
        "gate (5 days of water and food, housing covers everyone) -> inventor, lumber mill, power wheel -> storage ladder -> farms and grill.",
        "Storage targets: water = beavers x 3 x days; food = types x beavers x 2 x days; goods = days x daily log income plus planks. Days ramp 3, 6, 10, 20; a stage "
        "advances after 2 days at 85% of target with everyone housed.",
        "Storage spends at most 40% of log income, one store at a time, never below a reserve of max(30, 3 days of income).",
        "Staffing priority: district center minimum, food, water, logs, planks, power, science, then the rest. Only as many jobs as adult beavers.",
        "Hazard: a 3-day drought from cycle day 14 every cycle, output halved.",
        "Not modelled: decorations, well-being, bots, badwater, gears, science points, foresters, farm irrigation, births by couples.",
    ]:
        doc.add_paragraph(line, style="List Bullet")

    doc.add_heading("6. Day-by-day timeline", 1)
    table = doc.add_table(rows=1, cols=12)
    table.style = "Light Grid Accent 1"
    for i, h in enumerate(["Day", "Adults", "Kids", "Beds", "Logs", "Planks", "Water", "W days", "Food", "F days", "Stage", "Events"]):
        table.rows[0].cells[i].text = h
    for r in rows:
        cells = table.add_row().cells
        for i, v in enumerate([r["day"], r["adults"], r["kids"], r["beds"], r["logs"], r["planks"], r["water"], r["w_days"],
                               r["food"], r["f_days"], r["stage"], (r["hazard"] + " " if r["hazard"] else "") + r["notes"]]):
            cells[i].text = str(v)
            for p in cells[i].paragraphs:
                for run in p.runs:
                    run.font.size = Pt(8)

    doc.save(path)


def main():
    parser = argparse.ArgumentParser(description="100-day colony simulation, written as a Word document")
    parser.add_argument("--days", type=int, default=100)
    parser.add_argument("--out", default="timberborn-100-days.docx")
    parser.add_argument("--params", default=None, help="JSON file overriding assumption values")
    args = parser.parse_args()

    P = {k: v[0] for k, v in ASSUME.items()}
    if args.params:
        with open(args.params, encoding="utf-8") as fh:
            P.update(json.load(fh))

    rows, problems, first, built, final = run(P, args.days)
    write_doc(args.out, P, rows, problems, first, built, final, args.days)
    print("wrote", args.out)
    for key, days in problems.items():
        print("  %s: %d day(s), first day %d" % (key, len(days), days[0]))
    print("  milestones:", {k: v for k, v in sorted(first.items(), key=lambda kv: kv[1])})
    last = rows[-1]
    print("  day %d: %d adults, %d kids, %d beds, %d logs, water %.1f d, food %.1f d, stage %d"
          % (last["day"], last["adults"], last["kids"], last["beds"], last["logs"], last["w_days"], last["f_days"], last["stage"]))


if __name__ == "__main__":
    main()
