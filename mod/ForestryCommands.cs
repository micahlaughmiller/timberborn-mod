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

        public static string Mark(string body, bool add)
        {
            int x1 = Json.Int(body, "x1", int.MinValue), y1 = Json.Int(body, "y1", int.MinValue);
            int x2 = Json.Int(body, "x2", int.MinValue), y2 = Json.Int(body, "y2", int.MinValue);
            if (x1 == int.MinValue || y1 == int.MinValue || x2 == int.MinValue || y2 == int.MinValue)
                return Fail((add ? "mark_trees" : "unmark_trees") + " requires x1, y1, x2 and y2");

            int left = Math.Min(x1, x2), right = Math.Max(x1, x2);
            int top = Math.Min(y1, y2), bottom = Math.Max(y1, y2);
            if (right - left + 1 > MaxSide || bottom - top + 1 > MaxSide)
                return Fail("area is too large; each side may be at most " + MaxSide + " cells. Mark it in pieces.");

            var forestry = AIForestryServices.Instance;
            var world = AIWorldServices.Instance;
            if (forestry == null || world == null)
                return Fail("no save loaded, or forestry/world services not bound (check no-forestry.flag / no-world.flag)");

            var size = world.Terrain.Size;
            var cells = new List<Vector3Int>();
            int treeCells = 0;

            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    if (x < 0 || y < 0 || x >= size.x || y >= size.y) continue;

                    int z = SurfaceZ(world, x, y);
                    if (z < 0) continue;

                    var cell = new Vector3Int(x, y, z);
                    cells.Add(cell);
                    if (forestry.Area.HasYielder(cell)) treeCells++;
                }
            }

            if (cells.Count == 0) return Fail("that rectangle has no terrain inside the map");

            try
            {
                if (add) forestry.Area.AddCoordinates(cells);
                else forestry.Area.RemoveCoordinates(cells);
            }
            catch (Exception e)
            {
                var root = e;
                while (root.InnerException != null) root = root.InnerException;
                return Fail("the game refused: " + root.GetType().Name + ": " + root.Message);
            }

            return "{\"ok\":true,\"detail\":" + Json.Str((add ? "marked " : "unmarked ") + cells.Count + " cells for tree cutting between ("
                       + left + "," + top + ") and (" + right + "," + bottom + "); " + treeCells + " of them have trees on them")
                 + ",\"cells\":" + cells.Count + ",\"cells_with_trees\":" + treeCells + "}";
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
