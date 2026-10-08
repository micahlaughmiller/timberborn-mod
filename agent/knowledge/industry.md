# Industry, power, metal, bots

- Chain of unlocks: science (Inventor) -> planks (Lumber Mill) -> gears (Gear Workshop) -> paper, metal. Sluices, impermeable
  floors, mines and smelting all sit behind metal, which comes from scrap or an underground mine, refined in a Smelter.
- Power: a Power Wheel is a beaver turning a crank, good for early plank production. A Water Wheel gives a lot of power
  while the river flows and stalls completely in a drought. A Geothermal Engine on a vent gives steady power with no fuel
  or water. Windmills fluctuate, so players pair them with Gravity Batteries.
- Rough figures to verify before use: Lumber Mill about 50 HP, Gristmill about 60 HP, Aquifer Drill about 400 HP.
- Ratios players keep: one Gristmill feeds about two Bakeries; keep one more Lumber Mill than the number of plank
  consumers (Gear Workshops, Paper Mills, Wood Workshops) so construction never runs out of planks. A Lumber Mill makes
  about one plank per 1.3 hours; a Gear Workshop turns one plank into one gear in about 3 hours.
- Hazards: mines, smelters, explosive factories, dirt excavators and wood workshops injure workers, so they need
  Medical Beds. Badwater contact contaminates beavers; an Herbalist brews the antidote from berries.
- Bots (Bot Part Factory, Bot Assembler) do not get hurt, need no food or water and work around the clock, so they take
  over dangerous heavy industry and hauling late in the game.
- Power can run under paths with vertical and horizontal shafts, covered by platforms so traffic is not blocked.

## Bots: when and why

- Mines, smelters, explosive factories, dirt excavators and wood workshops injure workers often; without bots up to a
  quarter of the beaver workforce can end up in medical beds. Plan Bot Part Factories and Bot Assemblers before you
  move into heavy metal and explosives.
- Bots cannot be injured, do not sleep and do not need ordinary food or water; they need a way to refuel or charge,
  which depends on the faction.
- Rollout order: (1) put bots in the hazardous workplaces first, (2) then use them as builders and long-distance haulers
  that work around the clock, (3) then let them take 50 percent or more of general tasks so the beavers have leisure and
  high well-being.
- The exact recipes, power draws and assembly times for bots are not known from these notes. Read them with
  inspect_building (BotPartFactory, BotAssembler) before planning.

## Bot recipes and costs

Verified against this game's building list: the Bot Part Factory costs 500 science, 50 planks, 25 gears and 15 metal
blocks; the Bot Assembler costs 750 science, 100 planks, 50 gears and 50 metal blocks (150 planks, 75 gears and 65 metal
blocks in total, plus 1250 science). The Refinery (biofuel) and Smelter are also in the list and cost science and metal.

Unverified, from a wiki summary (confirm with inspect_building on BotPartFactory and BotAssembler before planning):
- Bot Part Factory, about 150 HP: one chassis per 18 hours (5 planks, 1 metal block), one head per 18 hours (1 plank,
  3 gears, 1 metal block), or four limbs per 18 hours (1 plank and 3 gears each).
- Bot Assembler, about 250 HP, 2 workers: one bot per 36 hours from a chassis, a head and limbs. A power shortfall stalls it.
- A whole bot is roughly 10 planks, 15 gears and 2 metal blocks of parts.
- Bots reportedly last about 70 days. Folktails keep them running with fuel from a biofuel refinery (fed carrots or
  potatoes); Iron Teeth use charging stations.
- Because the grid needs about 400 HP for both buildings plus a steady plank and gear supply, bots belong well after the
  basics, power, metal and the science to unlock them.
