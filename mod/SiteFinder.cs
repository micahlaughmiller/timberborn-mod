using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Finds spots where a building can really be placed, so the agent chooses from a list instead of
    /// guessing coordinates. Scans a window, tries every orientation, keeps cells that pass the block
    /// rules, then runs the game's full placement rules (a pump needs water, a flag needs a reachable
    /// spot) on the nearest candidates. Results are sorted by distance to a target point.
    /// </summary>
    internal static class SiteFinder
    {
        private const int MaxSide = 40;
        private const int DefaultResults = 10;
        private const int MaxResults = 25;
        private const int MaxFullChecks = 120; // each full check builds and discards a preview

        public static string Find(string body)
        {
            var prefab = Json.Field(body, "prefab");
            if (string.IsNullOrEmpty(prefab)) return Placer.Fail("find_sites requires \"prefab\" (see /buildings)");

            var build = AIBuildServices.Instance;
            var world = AIWorldServices.Instance;
            if (build == null || world == null)
                return Placer.Fail("no save loaded, or build/world services not bound");

            // Same template, unlock and blueprint checks as a real placement.
            object spec;
            try
            {
                if (!GameAccess.Invoke(build.Buildings, "GetBuildingTemplate", out spec, prefab) || spec == null)
                    return Placer.Fail("unknown building \"" + prefab + "\" (see /buildings)");
            }
            catch (Exception e) { return Placer.Fail("unknown building \"" + prefab + "\": " + Placer.Root(e).Message); }

            GameAccess.Invoke(build.Unlocking, "Unlocked", out var unlocked, spec);
            if (!(unlocked is bool isUnlocked && isUnlocked)) return Placer.Fail("\"" + prefab + "\" is not unlocked yet");

            if (!Placer.TryGetBlueprint(build, prefab, out var blueprint, out var blueprintError)) return Placer.Fail(blueprintError);
            var blockSpec = blueprint.GetSpec(typeof(BlockObjectSpec)) as BlockObjectSpec;
            if (blockSpec == null) return Placer.Fail("\"" + prefab + "\" cannot be placed on the map");

            // Window, defaulting to 24x24 around the district center.
            var size = world.Terrain.Size;
            var center = DistrictCenter(world, size);
            int w = Math.Max(1, Math.Min(MaxSide, Json.Int(body, "w", 24)));
            int h = Math.Max(1, Math.Min(MaxSide, Json.Int(body, "h", 24)));
            // The window follows near_x/near_y when given, so a spot next to far-away trees is actually searched.
            int focusX = Json.Int(body, "near_x", center.x), focusY = Json.Int(body, "near_y", center.y);
            int x0 = Json.Int(body, "x", focusX - w / 2);
            int y0 = Json.Int(body, "y", focusY - h / 2);
            x0 = Math.Max(0, Math.Min(size.x - 1, x0));
            y0 = Math.Max(0, Math.Min(size.y - 1, y0));
            w = Math.Min(w, size.x - x0);
            h = Math.Min(h, size.y - y0);

            // Sort target: near_x/near_y if given (for example trees), else the district center.
            int nearX = Json.Int(body, "near_x", center.x);
            int nearY = Json.Int(body, "near_y", center.y);
            int wanted = Math.Max(1, Math.Min(MaxResults, Json.Int(body, "max", DefaultResults)));

            // rank "near" puts the spot closest to near_x/near_y first (a lumberjack flag beside its trees);
            // the default favours a short walk to the settlement (storage, housing, workshops).
            bool nearFirst = string.Equals(Json.Field(body, "rank"), "near", StringComparison.OrdinalIgnoreCase);
            int Score(Candidate c) => nearFirst
                ? c.Distance * 4 + Math.Max(0, c.Walk)
                : c.Distance + c.NewTiles + 2 * Math.Max(0, c.Walk);

            // 1. Cheap pass: block rules only, every orientation, at the surface and the two levels below
            // (buildings that stand in water or against a bank sit lower than the shore).
            var candidates = new List<Candidate>();
            int blockChecks = 0;
            int blockedDoors = 0;
            var entranceCells = WorldReader.EntranceCells();
            foreach (Orientation orientation in Enum.GetValues(typeof(Orientation)))
            {
                for (int y = y0; y < y0 + h; y++)
                {
                    for (int x = x0; x < x0 + w; x++)
                    {
                        int surface = Placer.SurfaceZ(world, x, y);
                        if (surface < 0) continue;

                        for (int dz = 0; dz >= -2; dz--)
                        {
                            int z = surface + dz;
                            if (z < 0) break;

                            blockChecks++;
                            var placement = new Placement(new Vector3Int(x, y, z), orientation, FlipMode.Unflipped);
                            bool valid;
                            try { valid = build.Validator.BlocksValid(blockSpec, placement); }
                            catch { valid = false; }

                            if (valid && CoversAny(blockSpec, placement, entranceCells))
                            {
                                blockedDoors++;
                                break; // would sit in front of an existing door
                            }

                            if (valid)
                            {
                                candidates.Add(new Candidate { X = x, Y = y, Z = z, Orientation = orientation, Distance = Math.Abs(x - nearX) + Math.Abs(y - nearY) });
                                break; // the highest valid level for this cell and orientation is enough
                            }
                        }
                    }
                }
            }

            // 2. Full rules on the nearest candidates only.
            // The four facings of one cell are checked together, then only the facing whose door opens
            // closest to the target is kept: a human turns the building so its door faces the road.
            // Real walking distances to the settlement, from one search outward from its doorstep
            // (or to_x/to_y if given). Choosing the facing by actual walk, not by straight-line
            // direction, accounts for trees, water and cliffs that make a short line a long walk.
            Dictionary<long, int> walk = null;
            Dictionary<long, int> costs = null; // new path tiles needed to connect each cell
            int toX, toY;
            bool haveTarget = WorldReader.DistrictDoorstep(out toX, out toY);
            toX = Json.Int(body, "to_x", toX);
            toY = Json.Int(body, "to_y", toY);
            if (haveTarget || (Json.Int(body, "to_x", int.MinValue) != int.MinValue && Json.Int(body, "to_y", int.MinValue) != int.MinValue))
            {
                int fx0 = Math.Max(0, Math.Min(x0, toX) - 8), fx1 = Math.Min(size.x - 1, Math.Max(x0 + w, toX) + 8);
                int fy0 = Math.Max(0, Math.Min(y0, toY) - 8), fy1 = Math.Min(size.y - 1, Math.Max(y0 + h, toY) + 8);
                if (fx1 - fx0 + 1 <= 110 && fy1 - fy0 + 1 <= 110)
                {
                    walk = Connector.WalkingDistances(build, world, toX, toY, fx0, fx1, fy0, fy1);
                    costs = Connector.WalkingDistances(build, world, toX, toY, fx0, fx1, fy0, fy1, true);
                }
            }

            var passing = new List<Candidate>();
            var cellsSeen = new HashSet<long>();
            int fullChecks = 0;
            foreach (var c in candidates.OrderBy(c => c.Distance).ThenBy(c => c.X).ThenBy(c => c.Y))
            {
                long cellKey = ((long)c.Y * 100000L + c.X) * 100L + c.Z;
                if (!cellsSeen.Contains(cellKey) && cellsSeen.Count >= wanted) continue; // enough cells; finish only the ones already started
                if (fullChecks >= MaxFullChecks) break;
                fullChecks++;

                var placement = new Placement(new Vector3Int(c.X, c.Y, c.Z), c.Orientation, FlipMode.Unflipped);
                if (!Placer.FullyValid(build, blockSpec, placement, out _)) continue;

                // The door position comes from the blueprint, not from the preview, which does not report it reliably.
                bool hasDoor = Placer.TryDoorstep(blockSpec, placement, out var doorstep);

                // A door must open onto ground at the building's own level. A building standing on a bump one
                // level above its door cell looks reachable by (x, y) alone but nothing can step up to it.
                if (hasDoor && DoorOnOtherLevel(world, c.X, c.Y, c.Z, doorstep.x, doorstep.y)) continue;

                c.HasDoor = hasDoor;
                c.DoorX = doorstep.x;
                c.DoorY = doorstep.y;
                c.DoorDistance = hasDoor ? Math.Abs(doorstep.x - nearX) + Math.Abs(doorstep.y - nearY) : 0;

                if (hasDoor && walk != null)
                {
                    // A door that opens onto ground nothing can cross cannot be connected at all.
                    long doorKey = Connector.Key(doorstep.x, doorstep.y);
                    if (!walk.TryGetValue(doorKey, out var steps)) continue;
                    c.Walk = steps;
                    c.NewTiles = costs != null && costs.TryGetValue(doorKey, out var needed) ? needed : steps;
                    // Fewest new path tiles first (so buildings line up along existing roads), then shortest walk.
                    c.DoorDistance = steps * 10 + c.NewTiles;
                }
                else if (!hasDoor && walk != null)
                {
                    // Posts such as lumberjack and gatherer flags have no door, but beavers still have to
                    // reach them. They need the flag's own cell or a neighbour to be walkable from the
                    // settlement on one level; a flag on higher ground or across water never is.
                    int best = ReachSteps(walk, c.X, c.Y);
                    if (best < 0) continue;
                    c.Walk = best;
                    int bestTiles = costs != null ? ReachSteps(costs, c.X, c.Y) : best;
                    c.NewTiles = bestTiles < 0 ? best : bestTiles;
                    c.DoorDistance = best * 10 + c.NewTiles;
                }
                passing.Add(c);
                cellsSeen.Add(cellKey);
            }

            // The walks above were measured with each candidate's own footprint still free ground, so a route
            // could pass straight through the building it is about to place, and a door that faces a dead
            // pocket looked as good as one that faces the road. Re-measure the leading candidates, each
            // facing separately, with the footprint blocked, and drop the ones whose door is then cut off.
            if (walk != null && haveTarget)
            {
                int gx0 = Math.Max(0, Math.Min(x0, toX) - 8), gx1 = Math.Min(size.x - 1, Math.Max(x0 + w, toX) + 8);
                int gy0 = Math.Max(0, Math.Min(y0, toY) - 8), gy1 = Math.Min(size.y - 1, Math.Max(y0 + h, toY) + 8);
                var sharedWalk = new Dictionary<long, Connector.Cell>();
                var rechecked = new List<Candidate>();

                foreach (var c in passing.OrderBy(c => Score(c)).ThenBy(c => c.DoorDistance).Take(Math.Max(24, wanted * 6)))
                {
                    if (!c.HasDoor) { rechecked.Add(c); continue; }

                    var footprint = Footprint(blockSpec, new Placement(new Vector3Int(c.X, c.Y, c.Z), c.Orientation, FlipMode.Unflipped));
                    var walkBlocked = Connector.WalkingDistances(build, world, toX, toY, gx0, gx1, gy0, gy1, false, true, false, footprint, sharedWalk);
                    long doorKey = Connector.Key(c.DoorX, c.DoorY);
                    if (walkBlocked == null || !walkBlocked.TryGetValue(doorKey, out var steps)) continue;

                    var costBlocked = Connector.WalkingDistances(build, world, toX, toY, gx0, gx1, gy0, gy1, true, true, false, footprint, sharedWalk);
                    c.Walk = steps;
                    c.NewTiles = costBlocked != null && costBlocked.TryGetValue(doorKey, out var needed) ? needed : steps;
                    c.DoorDistance = steps * 10 + c.NewTiles;
                    rechecked.Add(c);
                }

                passing = rechecked;
            }

            var good = passing
                .GroupBy(c => ((long)c.Y * 100000L + c.X) * 100L + c.Z)
                .Select(g => g.OrderBy(c => c.DoorDistance).First())
                .OrderBy(c => Score(c)).ThenBy(c => c.DoorDistance)
                .Take(wanted)
                .ToList();

            var items = good.Select(c => "{\"x\":" + c.X + ",\"y\":" + c.Y + ",\"z\":" + c.Z
                                         + ",\"orientation\":" + Json.Str(c.Orientation.ToString())
                                         + ",\"distance\":" + c.Distance
                                         + (c.HasDoor && c.Walk >= 0 ? ",\"doorstep\":{\"x\":" + c.DoorX + ",\"y\":" + c.DoorY + "}" : "")
                                         + (c.Walk >= 0 ? ",\"walk_steps_to_settlement\":" + c.Walk + ",\"new_path_tiles\":" + c.NewTiles : "")
                                         + "}");

            return "{\"ok\":true,\"prefab\":" + Json.Str(prefab)
                 + ",\"window\":{\"x\":" + x0 + ",\"y\":" + y0 + ",\"w\":" + w + ",\"h\":" + h + "}"
                 + ",\"sorted_by_distance_to\":{\"x\":" + nearX + ",\"y\":" + nearY + "}"
                 + ",\"skipped_in_front_of_doors\":" + blockedDoors + ",\"block_checks\":" + blockChecks + ",\"passed_block_rules\":" + candidates.Count + ",\"full_checks\":" + fullChecks
                 + ",\"sites\":[" + string.Join(",", items) + "]"
                 + (good.Count == 0 ? ",\"note\":" + Json.Str(candidates.Count == 0
                        ? "no spot in this window passes the block rules; try a bigger or different window"
                        : "spots pass the block rules but none passed the full placement rules in the nearest " + fullChecks + "; widen the window or move near_x/near_y")
                        : "")
                 + "}";
        }

        /// <summary>Fewest steps from the settlement to the cell or one of its four neighbours, or -1 if none is walkable.</summary>
        private static int ReachSteps(Dictionary<long, int> walk, int x, int y)
        {
            int best = -1;
            int[] dx = { 0, 1, -1, 0, 0 }, dy = { 0, 0, 0, 1, -1 };
            for (int i = 0; i < 5; i++)
            {
                if (walk.TryGetValue(Connector.Key(x + dx[i], y + dy[i]), out var steps) && (best < 0 || steps < best)) best = steps;
            }
            return best;
        }

        /// <summary>
        /// Whether beavers could walk from the settlement to this building on one level: through its door if it
        /// has one, otherwise to its own cell or a neighbour. Used by build so an unreachable spot is refused
        /// even when the agent skipped find_sites. Returns true when it cannot judge (no district doorstep).
        /// </summary>
        internal static bool ReachableFromSettlement(AIBuildServices build, AIWorldServices world, BlockObjectSpec spec,
                                                     Placement placement, out string why)
        {
            why = null;
            if (!WorldReader.DistrictDoorstep(out var tx, out var ty)) return true;

            var at = placement.Coordinates;
            var size = world.Terrain.Size;
            int minX = Math.Max(0, Math.Min(tx, at.x) - 30), maxX = Math.Min(size.x - 1, Math.Max(tx, at.x) + 30);
            int minY = Math.Max(0, Math.Min(ty, at.y) - 30), maxY = Math.Min(size.y - 1, Math.Max(ty, at.y) + 30);
            if (maxX - minX + 1 > 110 || maxY - minY + 1 > 110)
            {
                why = "it is too far from the settlement to check that beavers can reach it; build closer to the district center";
                return false;
            }

            var field = Connector.WalkingDistances(build, world, tx, ty, minX, maxX, minY, maxY);
            if (field == null) return true;

            if (Placer.TryDoorstep(spec, placement, out var door))
            {
                if (DoorOnOtherLevel(world, at.x, at.y, at.z, door.x, door.y))
                {
                    why = "its door would open onto ground at a different height than the building stands on; beavers cannot "
                          + "step up or down there without a natural slope or stairs. Pick a spot from find_sites.";
                    return false;
                }

                if (field.ContainsKey(Connector.Key(door.x, door.y))) return true;
                why = "its door would open onto ground that is not connected, on one level, to the district center "
                      + "(a cliff, water, trees or another building are in the way)";
                return false;
            }

            if (ReachSteps(field, at.x, at.y) >= 0) return true;
            why = "beavers could not walk to it from the district center on one level (it is on higher or lower ground, "
                  + "across water, or behind trees)";
            return false;
        }

        /// <summary>
        /// True when the building stands on the surface (its own cell's ground height is its level) but the ground
        /// outside its door is at a different height. Buildings set lower than the shore, such as pumps, are not
        /// judged because their level is deliberately not the surface.
        /// </summary>
        private static bool DoorOnOtherLevel(AIWorldServices world, int x, int y, int z, int doorX, int doorY)
        {
            int own = Placer.SurfaceZ(world, x, y);
            if (own != z) return false;

            int outside = Placer.SurfaceZ(world, doorX, doorY);
            return outside >= 0 && outside != z;
        }

        /// <summary>The (x, y) cells a building covers at this placement, as Connector keys.</summary>
        private static HashSet<long> Footprint(BlockObjectSpec spec, Placement placement)
        {
            var cells = new HashSet<long>();
            try
            {
                foreach (object block in spec.GetBlocks(placement))
                {
                    if (GameAccess.MemberAny(block, "Coordinates", "Coordinate", "Position") is Vector3Int v)
                        cells.Add(Connector.Key(v.x, v.y));
                }
            }
            catch { }
            return cells;
        }

        /// <summary>True if any cell of the building's footprint at this placement is one of the given (x, y) cells.</summary>
        private static bool CoversAny(BlockObjectSpec spec, Placement placement, HashSet<long> cells)
        {
            if (cells.Count == 0) return false;

            try
            {
                foreach (object block in spec.GetBlocks(placement))
                {
                    if (GameAccess.MemberAny(block, "Coordinates", "Coordinate", "Position") is Vector3Int v
                        && cells.Contains((long)v.y * 100000L + v.x))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static Vector3Int DistrictCenter(AIWorldServices world, Vector3Int size)
        {
            foreach (var center in GameAccess.Enumerate(world.Districts.FinishedDistrictCenters))
            {
                if (GameAccess.Member(center, "CenterCoordinates") is Vector3Int c) return c;
            }
            return new Vector3Int(size.x / 2, size.y / 2, 0);
        }

        private sealed class Candidate
        {
            public int X, Y, Z, Distance;
            public int DoorX, DoorY, DoorDistance;
            public int NewTiles; // new path tiles needed to connect it to the settlement
            public int Walk = -1; // steps from the doorstep to the settlement, -1 if not computed
            public bool HasDoor;
            public Orientation Orientation;
        }
    }
}
