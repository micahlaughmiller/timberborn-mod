using System;
using System.Collections.Generic;
using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// Marks or unmarks a rectangle for tree cutting through the game's own TreeCuttingArea,
    /// the same list the in-game cutting tool edits. Lumberjack flags only send beavers to
    /// trees inside this area.
    /// </summary>
    internal static class ForestryCommands
    {
        private const int MaxSide = 40;
        private const int DefaultAroundRadius = 30; // mark any tree within this many walking steps of the flag
        private const int MaxAroundRadius = 40;

        public static string Mark(string body, bool add)
        {
            int x1 = Json.Int(body, "x1", int.MinValue), y1 = Json.Int(body, "y1", int.MinValue);
            int x2 = Json.Int(body, "x2", int.MinValue), y2 = Json.Int(body, "y2", int.MinValue);

            // "Around the flag" mode: no rectangle; every cell within radius of the flag's access cell that a
            // beaver can walk to, nearest first. This is how a person marks a forest: all of it near the flag.
            int radius = Json.Int(body, "radius", int.MinValue);
            int centerX = Json.Int(body, "from_x", int.MinValue), centerY = Json.Int(body, "from_y", int.MinValue);
            // A flag and no rectangle means "every tree within 30 steps".
            if (add && radius == int.MinValue && x1 == int.MinValue && centerX != int.MinValue && centerY != int.MinValue)
                radius = DefaultAroundRadius;

            bool around = add && radius != int.MinValue && centerX != int.MinValue && centerY != int.MinValue;
            if (around)
            {
                radius = Math.Max(1, Math.Min(MaxAroundRadius, radius));
                x1 = centerX - radius; x2 = centerX + radius;
                y1 = centerY - radius; y2 = centerY + radius;
            }

            if (x1 == int.MinValue || y1 == int.MinValue || x2 == int.MinValue || y2 == int.MinValue)
                return Fail((add ? "mark_trees" : "unmark_trees") + " requires x1, y1, x2 and y2, or from_x, from_y and radius");

            int left = Math.Min(x1, x2), right = Math.Max(x1, x2);
            int top = Math.Min(y1, y2), bottom = Math.Max(y1, y2);
            if (!around && (right - left + 1 > MaxSide || bottom - top + 1 > MaxSide))
                return Fail("area is too large; each side may be at most " + MaxSide + " cells. Mark it in pieces.");

            var forestry = AIForestryServices.Instance;
            var world = AIWorldServices.Instance;
            if (forestry == null || world == null)
                return Fail("no save loaded, or forestry/world services not bound (check no-forestry.flag / no-world.flag)");

            var size = world.Terrain.Size;
            var cells = new List<Vector3Int>();

            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    if (x < 0 || y < 0 || x >= size.x || y >= size.y) continue;

                    int z = SurfaceZ(world, x, y);
                    if (z < 0) continue;

                    cells.Add(new Vector3Int(x, y, z));
                }
            }

            if (cells.Count == 0) return Fail("that rectangle has no terrain inside the map");

            // Optional reachability filter: only mark cells a beaver can walk to from the flag.
            // Trees up a cliff, across water or behind other trees cannot be cut, and marking them
            // only gives lumberjacks work they can never finish.
            int unreachable = 0, otherLevel = 0;
            int fromX = Json.Int(body, "from_x", int.MinValue), fromY = Json.Int(body, "from_y", int.MinValue);
            if (add && fromX != int.MinValue && fromY != int.MinValue)
            {
                int maxSteps = around ? radius : Json.Int(body, "max_steps", 30);
                // Same level only by default: trees up a cliff are not marked until a way up has been proven.
                bool anyLevel = (Json.Field(body, "levels") ?? "same").ToLowerInvariant() == "any";
                var build = AIBuildServices.Instance;
                if (build == null) return Fail("build services not bound, cannot check reachability");

                int fx0 = Math.Max(0, Math.Min(left, fromX) - 6), fx1 = Math.Min(size.x - 1, Math.Max(right, fromX) + 6);
                int fy0 = Math.Max(0, Math.Min(top, fromY) - 6), fy1 = Math.Min(size.y - 1, Math.Max(bottom, fromY) + 6);
                var field = Connector.WalkingDistances(build, world, fromX, fromY, fx0, fx1, fy0, fy1, false, anyLevel);
                var anyField = anyLevel ? field : Connector.WalkingDistances(build, world, fromX, fromY, fx0, fx1, fy0, fy1, false, true);
                if (field == null)
                    return Fail("the start cell (" + fromX + "," + fromY + ") is not walkable; use the lumberjack flag's access_cell");

                bool Near(int x, int y)
                {
                    int[] dx = { 0, 1, -1, 0, 0 }, dy = { 0, 0, 0, 1, -1 };
                    for (int i = 0; i < 5; i++)
                        if (field.TryGetValue(Connector.Key(x + dx[i], y + dy[i]), out var steps) && steps <= maxSteps) return true;
                    return false;
                }

                var reachable = new List<Vector3Int>();
                foreach (var cell in cells) if (Near(cell.x, cell.y)) reachable.Add(cell);
                unreachable = cells.Count - reachable.Count;

                // How many of the skipped cells could be reached by climbing a natural slope. Reported, not marked.
                if (!anyLevel && anyField != null)
                {
                    bool NearAny(int x, int y)
                    {
                        int[] ddx = { 0, 1, -1, 0, 0 }, ddy = { 0, 0, 0, 1, -1 };
                        for (int i = 0; i < 5; i++)
                            if (anyField.TryGetValue(Connector.Key(x + ddx[i], y + ddy[i]), out var st) && st <= maxSteps) return true;
                        return false;
                    }

                    foreach (var cell in cells) if (!Near(cell.x, cell.y) && NearAny(cell.x, cell.y)) otherLevel++;
                }

                // Nearest first, so a capped or partial result still covers the closest trees.
                reachable.Sort((a, b) => (Math.Abs(a.x - fromX) + Math.Abs(a.y - fromY)).CompareTo(Math.Abs(b.x - fromX) + Math.Abs(b.y - fromY)));
                cells = reachable;

                if (cells.Count == 0)
                    return Fail("no cell in that rectangle can be reached on foot from (" + fromX + "," + fromY + ") within " + maxSteps
                                + " steps. Cliffs, water or other obstacles are in the way; pick trees on the same level as the flag.");
            }

            // HasYielder only knows about trees in cells that are already inside the cutting
            // area, so the count has to be taken after adding (and before removing).
            int treeCells = 0;
            try
            {
                if (add)
                {
                    forestry.Area.AddCoordinates(cells);
                    foreach (var cell in cells) if (forestry.Area.HasYielder(cell)) treeCells++;
                }
                else
                {
                    foreach (var cell in cells) if (forestry.Area.HasYielder(cell)) treeCells++;
                    forestry.Area.RemoveCoordinates(cells);
                }
            }
            catch (Exception e)
            {
                var root = e;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("the game refused: " + root.GetType().Name + ": " + root.Message);
            }

            return "{\"ok\":true,\"detail\":" + Json.Str((add ? "marked " : "unmarked ") + cells.Count + " cells for tree cutting between ("
                       + left + "," + top + ") and (" + right + "," + bottom + "); " + treeCells + " of them have trees on them")
                 + ",\"cells\":" + cells.Count + ",\"cells_with_trees\":" + treeCells + ",\"cells_skipped_unreachable\":" + unreachable + ",\"cells_only_reachable_up_a_slope\":" + otherLevel + "}";
        }

        private static int SurfaceZ(AIWorldServices world, int x, int y)
        {
            int top = -1;
            foreach (var cell in GameAccess.Enumerate(world.Terrain.GetAllHeightsInCell(new Vector2Int(x, y))))
            {
                if (cell is Vector3Int v) top = Math.Max(top, v.z);
            }
            return top;
        }

        private static string Fail(string error) => "{\"ok\":false,\"error\":" + Json.Str(error) + "}";
    }
}
