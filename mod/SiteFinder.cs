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
            int x0 = Json.Int(body, "x", center.x - w / 2);
            int y0 = Json.Int(body, "y", center.y - h / 2);
            x0 = Math.Max(0, Math.Min(size.x - 1, x0));
            y0 = Math.Max(0, Math.Min(size.y - 1, y0));
            w = Math.Min(w, size.x - x0);
            h = Math.Min(h, size.y - y0);

            // Sort target: near_x/near_y if given (for example trees), else the district center.
            int nearX = Json.Int(body, "near_x", center.x);
            int nearY = Json.Int(body, "near_y", center.y);
            int wanted = Math.Max(1, Math.Min(MaxResults, Json.Int(body, "max", DefaultResults)));

            // 1. Cheap pass: block rules only, every orientation, at the surface and the two levels below
            // (buildings that stand in water or against a bank sit lower than the shore).
            var candidates = new List<Candidate>();
            int blockChecks = 0;
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
                if (!Placer.FullyValidWithDoor(build, blockSpec, placement, out _, out var doorstep, out var hasDoor)) continue;

                c.HasDoor = hasDoor;
                c.DoorX = doorstep.x;
                c.DoorY = doorstep.y;
                c.DoorDistance = hasDoor ? Math.Abs(doorstep.x - nearX) + Math.Abs(doorstep.y - nearY) : 0;
                passing.Add(c);
                cellsSeen.Add(cellKey);
            }

            var good = passing
                .GroupBy(c => ((long)c.Y * 100000L + c.X) * 100L + c.Z)
                .Select(g => g.OrderBy(c => c.DoorDistance).First())
                .OrderBy(c => c.Distance).ThenBy(c => c.DoorDistance)
                .Take(wanted)
                .ToList();

            var items = good.Select(c => "{\"x\":" + c.X + ",\"y\":" + c.Y + ",\"z\":" + c.Z
                                         + ",\"orientation\":" + Json.Str(c.Orientation.ToString())
                                         + ",\"distance\":" + c.Distance
                                         + (c.HasDoor ? ",\"doorstep\":{\"x\":" + c.DoorX + ",\"y\":" + c.DoorY + "}" : "")
                                         + "}");

            return "{\"ok\":true,\"prefab\":" + Json.Str(prefab)
                 + ",\"window\":{\"x\":" + x0 + ",\"y\":" + y0 + ",\"w\":" + w + ",\"h\":" + h + "}"
                 + ",\"sorted_by_distance_to\":{\"x\":" + nearX + ",\"y\":" + nearY + "}"
                 + ",\"block_checks\":" + blockChecks + ",\"passed_block_rules\":" + candidates.Count + ",\"full_checks\":" + fullChecks
                 + ",\"sites\":[" + string.Join(",", items) + "]"
                 + (good.Count == 0 ? ",\"note\":" + Json.Str(candidates.Count == 0
                        ? "no spot in this window passes the block rules; try a bigger or different window"
                        : "spots pass the block rules but none passed the full placement rules in the nearest " + fullChecks + "; widen the window or move near_x/near_y")
                        : "")
                 + "}";
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
            public bool HasDoor;
            public Orientation Orientation;
        }
    }
}
