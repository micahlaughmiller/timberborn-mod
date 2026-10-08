using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.Coordinates;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Finds the cheapest walkable route between two cells and lays the missing Path tiles. The cells
    /// are normally two buildings' doorsteps (placed_buildings[].entrance.DoorstepCoordinates), the one
    /// cell outside a door that a path has to end on. Existing path tiles are reused for free; new
    /// tiles cost one each, so the route prefers what already exists. Cells blocked by trees, buildings,
    /// water or ground that rises more than one level between neighbours are routed around.
    /// </summary>
    internal static class Connector
    {
        private const int MaxSide = 90;
        private const int Margin = 12;

        private struct Cell
        {
            public bool Passable;
            public bool Exists;
            public int Z;
        }

        public static string Connect(string body)
        {
            int x1 = Json.Int(body, "x1", int.MinValue), y1 = Json.Int(body, "y1", int.MinValue);
            int x2 = Json.Int(body, "x2", int.MinValue), y2 = Json.Int(body, "y2", int.MinValue);
            if (x1 == int.MinValue || y1 == int.MinValue || x2 == int.MinValue || y2 == int.MinValue)
                return Placer.Fail("connect requires x1, y1, x2, y2: the doorstep cells to join (see placed_buildings[].entrance.DoorstepCoordinates)");

            var build = AIBuildServices.Instance;
            var world = AIWorldServices.Instance;
            if (build == null || world == null) return Placer.Fail("no save loaded, or build/world services not bound");

            if (!Placer.TryGetBlueprint(build, "Path", out var blueprint, out var blueprintError)) return Placer.Fail(blueprintError);
            var pathSpec = blueprint.GetSpec(typeof(BlockObjectSpec)) as BlockObjectSpec;
            if (pathSpec == null) return Placer.Fail("the Path template has no block layout");

            var size = world.Terrain.Size;
            int minX = Math.Max(0, Math.Min(x1, x2) - Margin), maxX = Math.Min(size.x - 1, Math.Max(x1, x2) + Margin);
            int minY = Math.Max(0, Math.Min(y1, y2) - Margin), maxY = Math.Min(size.y - 1, Math.Max(y1, y2) + Margin);
            if (maxX - minX + 1 > MaxSide || maxY - minY + 1 > MaxSide)
                return Placer.Fail("those points are too far apart for one route (limit about " + (MaxSide - 2 * Margin) + " cells). Connect them in stages.");

            var existing = ExistingPaths();
            var cache = new Dictionary<long, Cell>();

            Cell Look(int x, int y)
            {
                long key = Key(x, y);
                if (cache.TryGetValue(key, out var known)) return known;

                var cell = new Cell { Z = Placer.SurfaceZ(world, x, y) };
                if (cell.Z >= 0)
                {
                    if (existing.Contains(key)) { cell.Passable = true; cell.Exists = true; }
                    else
                    {
                        try
                        {
                            cell.Passable = build.Validator.BlocksValid(pathSpec,
                                new Placement(new Vector3Int(x, y, cell.Z), Orientation.Cw0, FlipMode.Unflipped));
                        }
                        catch { cell.Passable = false; }
                    }
                }
                cache[key] = cell;
                return cell;
            }

            long start = Key(x1, y1), goal = Key(x2, y2);
            var startCell = Look(x1, y1);
            var goalCell = Look(x2, y2);
            if (!startCell.Passable) return Placer.Fail("the start cell (" + x1 + "," + y1 + ") cannot hold a path (occupied or no terrain). Use the doorstep cell of the building.");
            if (!goalCell.Passable) return Placer.Fail("the end cell (" + x2 + "," + y2 + ") cannot hold a path (occupied or no terrain). Use the doorstep cell of the building.");

            // 0-1 breadth-first search: existing tiles cost 0, new tiles cost 1.
            var dist = new Dictionary<long, int>();
            var previous = new Dictionary<long, long>();
            var queue = new LinkedList<long>();
            dist[start] = startCell.Exists ? 0 : 1;
            queue.AddFirst(start);

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                long current = queue.First.Value;
                queue.RemoveFirst();
                if (current == goal) break;

                int cx = (int)(current % 100000L), cy = (int)(current / 100000L);
                int currentZ = Look(cx, cy).Z;

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i], ny = cy + dy[i];
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;

                    var next = Look(nx, ny);
                    if (!next.Passable || Math.Abs(next.Z - currentZ) > 1) continue;

                    int stepCost = next.Exists ? 0 : 1;
                    int newDist = dist[current] + stepCost;
                    long nextKey = Key(nx, ny);

                    if (!dist.TryGetValue(nextKey, out var old) || newDist < old)
                    {
                        dist[nextKey] = newDist;
                        previous[nextKey] = current;
                        if (stepCost == 0) queue.AddFirst(nextKey); else queue.AddLast(nextKey);
                    }
                }
            }

            if (!dist.ContainsKey(goal))
                return Placer.Fail("no walkable route from (" + x1 + "," + y1 + ") to (" + x2 + "," + y2 + ") within "
                                   + (maxX - minX + 1) + "x" + (maxY - minY + 1) + " cells. Trees, water, other buildings or steep ground are in the way. "
                                   + "Clear trees with mark_trees or move one of the buildings.");

            var route = new List<long>();
            for (long at = goal; ; at = previous[at])
            {
                route.Add(at);
                if (at == start) break;
            }
            route.Reverse();

            int reused = 0, placed = 0;
            var failed = new List<string>();
            foreach (var key in route)
            {
                int x = (int)(key % 100000L), y = (int)(key / 100000L);
                if (cache[key].Exists) { reused++; continue; }

                var reply = Placer.Place("{\"prefab\":\"Path\",\"x\":" + x + ",\"y\":" + y + "}");
                if (Json.Field(reply, "ok") == "true") placed++;
                else failed.Add("[" + x + "," + y + "]");
            }

            bool complete = failed.Count == 0;
            return "{\"ok\":" + (complete ? "true" : "false")
                 + ",\"detail\":" + Json.Str((complete ? "connected" : "partly connected") + " (" + x1 + "," + y1 + ") to (" + x2 + "," + y2 + "): "
                        + route.Count + " tiles on the route, " + placed + " new, " + reused + " already built"
                        + (complete ? "" : ", " + failed.Count + " could not be placed"))
                 + ",\"route_length\":" + route.Count + ",\"new_tiles\":" + placed + ",\"reused_tiles\":" + reused
                 + ",\"failed_tiles\":[" + string.Join(",", failed.Take(20)) + "]}";
        }

        internal static long Key(int x, int y) => (long)y * 100000L + x;

        /// <summary>
        /// Walking distance in steps from the target cell to every cell it can reach inside the window,
        /// by the same rules the connector uses (a cell must be an existing path or able to hold one,
        /// and neighbours may differ by at most one level). Returns null if the target cell itself
        /// is not walkable. One search serves any number of candidate doors.
        /// </summary>
        internal static Dictionary<long, int> WalkingDistances(AIBuildServices build, AIWorldServices world,
                                                               int tx, int ty, int minX, int maxX, int minY, int maxY)
        {
            if (!Placer.TryGetBlueprint(build, "Path", out var blueprint, out _)) return null;
            var pathSpec = blueprint.GetSpec(typeof(BlockObjectSpec)) as BlockObjectSpec;
            if (pathSpec == null) return null;

            var existing = ExistingPaths();
            var cache = new Dictionary<long, Cell>();

            Cell Look(int x, int y)
            {
                long key = Key(x, y);
                if (cache.TryGetValue(key, out var known)) return known;

                var cell = new Cell { Z = Placer.SurfaceZ(world, x, y) };
                if (cell.Z >= 0)
                {
                    if (existing.Contains(key)) { cell.Passable = true; cell.Exists = true; }
                    else
                    {
                        try
                        {
                            cell.Passable = build.Validator.BlocksValid(pathSpec,
                                new Placement(new Vector3Int(x, y, cell.Z), Orientation.Cw0, FlipMode.Unflipped));
                        }
                        catch { cell.Passable = false; }
                    }
                }
                cache[key] = cell;
                return cell;
            }

            if (!Look(tx, ty).Passable) return null;

            var dist = new Dictionary<long, int> { [Key(tx, ty)] = 0 };
            var queue = new Queue<long>();
            queue.Enqueue(Key(tx, ty));

            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                long current = queue.Dequeue();
                int cx = (int)(current % 100000L), cy = (int)(current / 100000L);
                int currentZ = Look(cx, cy).Z;

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i], ny = cy + dy[i];
                    if (nx < minX || nx > maxX || ny < minY || ny > maxY) continue;

                    long nextKey = Key(nx, ny);
                    if (dist.ContainsKey(nextKey)) continue;

                    var next = Look(nx, ny);
                    if (!next.Passable || Math.Abs(next.Z - currentZ) > 1) continue;

                    dist[nextKey] = dist[current] + 1;
                    queue.Enqueue(nextKey);
                }
            }

            return dist;
        }

        /// <summary>Cells that already hold a Path, so the route can reuse them instead of paying for new tiles.</summary>
        private static HashSet<long> ExistingPaths()
        {
            var set = new HashSet<long>();
            var core = AIGameServices.Instance;
            if (core == null) return set;

            foreach (var entity in GameAccess.Enumerate(core.Entities.Entities))
            {
                if (StateReader.EntityName(entity) != "Path") continue;
                if (WorldReader.EntityCell(entity, out var cell)) set.Add(Key(cell.x, cell.y));
            }
            return set;
        }
    }
}
